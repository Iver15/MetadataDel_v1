using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace MetadataDel.Core.Ole;

/// <summary>
/// Проект VBA ([MS-OVBA]): хранилище <c>_VBA_PROJECT_CUR</c> книги Excel, <c>Macros</c> документа Word
/// или корень <c>vbaProject.bin</c>. Макросы не удаляются. В текстовом потоке PROJECT пути к файлу справки
/// и к скомпилированному проекту (HelpFile, ExeName32) заменяются пустыми строками; синтаксис потока
/// сохраняется. Сжатый поток <c>VBA/dir</c> только проверяется: пути в ссылках на библиотеки и в
/// PROJECTHELPFILEPATH остаются, о них сообщается.
/// </summary>
internal static partial class VbaProjectMetadata
{
    public const string ProjectStream = "PROJECT";
    public const string VbaStorage = "VBA";
    private static readonly string[] PathKeys = ["HelpFile", "ExeName32"];

    // Absolute local path in the decompressed dir stream; system and Office folders are not personal.
    [GeneratedRegex(@"(?:[A-Za-z]:\\|\\\\[A-Za-z0-9])[^\x00-\x1F""<>|*?#]{2,}")]
    private static partial Regex LocalPath();

    [GeneratedRegex(@"^[A-Za-z]:\\(?:Windows|WINNT|Program Files(?: \(x86\))?)\\", RegexOptions.IgnoreCase)]
    private static partial Regex SystemPath();

    /// <summary>Является ли хранилище проектом VBA (поток PROJECT и хранилище VBA рядом).</summary>
    public static bool IsProject(OpenMcdf.Storage storage) =>
        storage.ContainsEntry(ProjectStream) && storage.ContainsEntry(VbaStorage) &&
        storage.EnumerateEntries().Any(e => e.Name == VbaStorage && e.Type == OpenMcdf.EntryType.Storage);

    /// <summary>
    /// Заменяет значения HelpFile и ExeName32 в основном разделе потока PROJECT на <c>""</c>.
    /// Возвращает новый поток или <c>null</c>, если путей нет.
    /// </summary>
    public static byte[]? ScrubProjectStream(byte[] project)
    {
        var output = new List<byte>(project.Length);
        var changed = false;
        var inMainSection = true;
        var position = 0;
        while (position < project.Length)
        {
            var end = project.AsSpan(position).IndexOf((byte)'\n');
            var lineEnd = end < 0 ? project.Length : position + end + 1;
            var line = project.AsSpan(position, lineEnd - position);
            var content = line.TrimEnd("\r\n"u8);
            if (content.Length > 0 && content[0] == (byte)'[') inMainSection = false;
            string? key = null;
            foreach (var candidate in PathKeys)
                if (inMainSection && StartsWithKey(content, candidate)) key = candidate;
            if (key != null && !content[(key.Length + 1)..].SequenceEqual("\"\""u8))
            {
                output.AddRange(Encoding.ASCII.GetBytes(key + "=\"\""));
                output.AddRange(line[content.Length..].ToArray());
                changed = true;
            }
            else output.AddRange(line.ToArray());
            position = lineEnd;
        }
        return changed ? output.ToArray() : null;
    }

    private static bool StartsWithKey(ReadOnlySpan<byte> line, string key) =>
        line.Length > key.Length && line[key.Length] == (byte)'=' &&
        Encoding.ASCII.GetString(line[..key.Length]).Equals(key, StringComparison.OrdinalIgnoreCase);

    /// <summary>Есть ли в сжатом потоке <c>dir</c> пути вне системных папок (ссылки, файл справки).</summary>
    /// <exception cref="InvalidDataException">Поток не является корректным сжатым контейнером.</exception>
    public static bool DirHasLocalPaths(byte[] compressedDir)
    {
        var dir = Decompress(compressedDir);
        foreach (var (encoding, parity) in new[] { (Encoding.Latin1, 0), (Encoding.Unicode, 0), (Encoding.Unicode, 1) })
        {
            var charSize = encoding == Encoding.Latin1 ? 1 : 2;
            var region = dir.AsSpan(Math.Min(parity, dir.Length));
            var text = encoding.GetString(region[..(region.Length / charSize * charSize)]);
            if (LocalPath().Matches(text).Any(m => !SystemPath().IsMatch(m.Value))) return true;
        }
        return false;
    }

    /// <summary>
    /// Распаковка CompressedContainer ([MS-OVBA] 2.4.1): байт 0x01, затем блоки по 4096 байт —
    /// несжатые или из последовательностей токенов (литерал или ссылка смещение/длина).
    /// </summary>
    /// <exception cref="InvalidDataException">Нарушена структура контейнера.</exception>
    internal static byte[] Decompress(ReadOnlySpan<byte> container)
    {
        if (container.Length == 0 || container[0] != 0x01) throw new InvalidDataException("Неверная сигнатура сжатого потока VBA.");
        var output = new List<byte>(container.Length * 2);
        var position = 1;
        while (position < container.Length)
        {
            if (position + 2 > container.Length) throw new InvalidDataException("Обрезан сжатый поток VBA.");
            var header = BinaryPrimitives.ReadUInt16LittleEndian(container[position..]);
            if (((header >> 12) & 0x7) != 0x3) throw new InvalidDataException("Неверный заголовок блока VBA.");
            var chunkEnd = Math.Min(container.Length, position + (header & 0x0FFF) + 3);
            var compressed = (header & 0x8000) != 0;
            position += 2;
            var chunkStart = output.Count;
            if (!compressed)
            {
                if (position + 4096 > container.Length) throw new InvalidDataException("Обрезан блок VBA.");
                output.AddRange(container.Slice(position, 4096).ToArray());
                position += 4096;
                continue;
            }
            while (position < chunkEnd)
            {
                var flags = container[position++];
                for (var bit = 0; bit < 8 && position < chunkEnd; bit++)
                {
                    if ((flags & (1 << bit)) == 0)
                    {
                        output.Add(container[position++]);
                        continue;
                    }
                    if (position + 2 > chunkEnd) throw new InvalidDataException("Обрезан блок VBA.");
                    var token = BinaryPrimitives.ReadUInt16LittleEndian(container[position..]);
                    position += 2;
                    var difference = output.Count - chunkStart;
                    var bitCount = 4;
                    while ((1 << bitCount) < difference) bitCount++;
                    var lengthMask = 0xFFFF >> bitCount;
                    var length = (token & lengthMask) + 3;
                    var offset = (token >> (16 - bitCount)) + 1;
                    if (offset > difference) throw new InvalidDataException("Неверная ссылка в блоке VBA.");
                    var source = output.Count - offset;
                    for (var i = 0; i < length; i++) output.Add(output[source + i]);
                }
            }
        }
        return output.ToArray();
    }
}
