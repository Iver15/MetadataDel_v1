using System.Buffers.Binary;
using System.Text;

namespace MetadataDel.Core.Ole;

/// <summary>
/// Пути в потоке <c>\x01Ole10Native</c> объекта OLE Package (вложенный файл): исходный путь и временный путь
/// в ANSI и в необязательном Unicode-хвосте. Обычно они содержат имя пользователя (<c>C:\Users\имя\...</c>).
/// Пути сокращаются до имени файла; подпись и содержимое вложенного файла не меняются.
/// </summary>
internal static class OlePackageMetadata
{
    public const string StreamName = "\u0001Ole10Native";

    /// <summary>
    /// Находит пути с каталогами и при <paramref name="scrub"/> возвращает пересобранный поток в <paramref name="scrubbed"/>.
    /// </summary>
    public static List<string> Process(byte[] stream, bool scrub, out byte[]? scrubbed, out string? problem)
    {
        scrubbed = null;
        problem = null;
        var findings = new List<string>();
        Package package;
        try { package = Parse(stream); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException)
        {
            problem = "не удалось разобрать вложенный файл OLE Package";
            return findings;
        }

        var paths = new[] { package.SourcePath, package.TempPath, package.TempPathW, package.SourcePathW };
        if (!paths.Any(HasDirectory)) return findings;
        findings.Add("путь к вложенному файлу OLE Package");
        if (scrub) scrubbed = Build(package);
        return findings;
    }

    private sealed record Package(
        ushort Type, byte[] Label, byte[] SourcePath, uint Reserved, byte[] TempPath, byte[] Data,
        byte[]? TempPathW, byte[]? LabelW, byte[]? SourcePathW, byte[] Trailer, byte[] Outside);

    // DWORD size, WORD type, label\0, source path\0, DWORD reserved, DWORD cb + temp path\0, DWORD cb + data,
    // then optionally three (DWORD chars + UTF-16) strings: temp path, label, source path.
    private static Package Parse(byte[] stream)
    {
        var position = 0;
        var size = ReadUInt32(stream, ref position);
        var end = (int)Math.Min((long)stream.Length, 4L + size);
        var type = ReadUInt16(stream, ref position);
        var label = ReadZeroTerminated(stream, ref position, end);
        var sourcePath = ReadZeroTerminated(stream, ref position, end);
        var reserved = ReadUInt32(stream, ref position);
        var tempPath = ReadCounted(stream, ref position, end, 1);
        var data = ReadCounted(stream, ref position, end, 1);

        byte[]? tempPathW = null, labelW = null, sourcePathW = null;
        var tailStart = position;
        try
        {
            tempPathW = ReadCounted(stream, ref position, end, 2);
            labelW = ReadCounted(stream, ref position, end, 2);
            sourcePathW = ReadCounted(stream, ref position, end, 2);
        }
        catch (InvalidDataException)
        {
            // The Unicode tail is optional; keep whatever follows the data untouched.
            tempPathW = labelW = sourcePathW = null;
            position = tailStart;
        }
        // Bytes inside the declared size are kept in the trailer; bytes past it (padding) are kept as they are.
        return new Package(type, label, sourcePath, reserved, tempPath, data, tempPathW, labelW, sourcePathW,
            stream[position..end], stream[end..]);
    }

    private static byte[] Build(Package package)
    {
        using var body = new MemoryStream();
        using var writer = new BinaryWriter(body);
        writer.Write(package.Type);
        WriteZeroTerminated(writer, package.Label);
        WriteZeroTerminated(writer, FileName(package.SourcePath));
        writer.Write(package.Reserved);
        WriteCounted(writer, FileName(package.TempPath), 1);
        WriteCounted(writer, package.Data, 1);
        if (package.TempPathW != null)
        {
            WriteCounted(writer, FileNameW(package.TempPathW), 2);
            WriteCounted(writer, package.LabelW!, 2);
            WriteCounted(writer, FileNameW(package.SourcePathW!), 2);
        }
        writer.Write(package.Trailer);
        writer.Flush();

        var result = new byte[4 + body.Length + package.Outside.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)body.Length);
        body.ToArray().CopyTo(result, 4);
        package.Outside.CopyTo(result, 4 + (int)body.Length);
        return result;
    }

    private static bool HasDirectory(byte[]? path) =>
        path != null && (path.AsSpan().IndexOfAny((byte)'\\', (byte)'/', (byte)':') >= 0);

    // ANSI path; a zero terminator, if present, stays at the end.
    private static byte[] FileName(byte[] path)
    {
        var text = path.AsSpan();
        var terminated = text.Length > 0 && text[^1] == 0;
        if (terminated) text = text[..^1];
        var name = text[(text.LastIndexOfAny((byte)'\\', (byte)'/', (byte)':') + 1)..];
        return terminated ? [.. name, 0] : name.ToArray();
    }

    private static byte[] FileNameW(byte[] path)
    {
        var text = Encoding.Unicode.GetString(path);
        var terminated = text.EndsWith('\0');
        text = text.TrimEnd('\0');
        text = text[(text.LastIndexOfAny(['\\', '/', ':']) + 1)..];
        return Encoding.Unicode.GetBytes(terminated ? text + "\0" : text);
    }

    private static uint ReadUInt32(byte[] data, ref int position)
    {
        if (position + 4 > data.Length) throw new InvalidDataException();
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position));
        position += 4;
        return value;
    }

    private static ushort ReadUInt16(byte[] data, ref int position)
    {
        if (position + 2 > data.Length) throw new InvalidDataException();
        var value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position));
        position += 2;
        return value;
    }

    // Returns the string without its terminator.
    private static byte[] ReadZeroTerminated(byte[] data, ref int position, int end)
    {
        var length = data.AsSpan(position, end - position).IndexOf((byte)0);
        if (length < 0) throw new InvalidDataException();
        var value = data[position..(position + length)];
        position += length + 1;
        return value;
    }

    // DWORD count (in units of unitSize bytes) followed by that many units, returned verbatim.
    private static byte[] ReadCounted(byte[] data, ref int position, int end, int unitSize)
    {
        if (position + 4 > end) throw new InvalidDataException();
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position));
        var size = (long)count * unitSize;
        if (size > end - position - 4) throw new InvalidDataException();
        position += 4;
        var value = data[position..(position + (int)size)];
        position += (int)size;
        return value;
    }

    private static void WriteZeroTerminated(BinaryWriter writer, byte[] value)
    {
        writer.Write(value);
        writer.Write((byte)0);
    }

    private static void WriteCounted(BinaryWriter writer, byte[] value, int unitSize)
    {
        writer.Write((uint)(value.Length / unitSize));
        writer.Write(value);
    }
}
