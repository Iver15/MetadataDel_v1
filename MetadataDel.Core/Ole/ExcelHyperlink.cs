using System.Buffers.Binary;
using System.Text;

namespace MetadataDel.Core.Ole;

/// <summary>
/// Гиперссылка книги Excel (запись HLink, [MS-XLS] 2.4.140): ref8, hlinkClsid и объект Hyperlink
/// ([MS-OSHARED] 2.3.7.1). Локальные пути в моникерах (файловый моникер: ANSI- и Unicode-путь; URL-моникер
/// с <c>file:</c> или путём; строковый моникер) сокращаются до имени файла с сохранением длины, дата создания
/// ссылки обнуляется. Ссылки http, https, mailto и прочие URL — содержимое документа, они не меняются;
/// отображаемый текст и адрес внутри книги тоже не меняются.
/// </summary>
internal static class ExcelHyperlink
{
    private const uint HasMoniker = 0x0001;
    private const uint HasLocationString = 0x0008;
    private const uint HasDisplayName = 0x0010;
    private const uint HasGuid = 0x0020;
    private const uint HasCreationTime = 0x0040;
    private const uint HasFrameName = 0x0080;
    private const uint MonikerSavedAsString = 0x0100;

    private static readonly Guid UrlMoniker = new("79EAC9E0-BAF9-11CE-8C82-00AA004BA90B");
    private static readonly Guid FileMoniker = new("00000303-0000-0000-C000-000000000046");
    private static readonly Guid ItemMoniker = new("00000304-0000-0000-C000-000000000046");
    private static readonly Guid AntiMoniker = new("00000305-0000-0000-C000-000000000046");
    private static readonly Guid CompositeMoniker = new("00000309-0000-0000-C000-000000000046");

    public readonly record struct Result(int Paths, int Dates);

    /// <summary>Находит пути и дату создания; при <paramref name="scrub"/> правит запись на месте.</summary>
    public static Result Process(Span<byte> record, bool scrub)
    {
        const int header = 24; // ref8 + hlinkClsid
        if (record.Length < header + 8) return default;
        try
        {
            var position = header;
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(record[(position + 4)..]);
            position += 8;
            var paths = 0;
            var dates = 0;
            if ((flags & HasDisplayName) != 0) SkipString(record, ref position);
            if ((flags & HasFrameName) != 0) SkipString(record, ref position);
            if ((flags & HasMoniker) != 0)
            {
                if ((flags & MonikerSavedAsString) != 0) paths += ProcessString(record, ref position, scrub);
                else paths += ProcessMoniker(record, ref position, scrub, depth: 0);
            }
            if ((flags & HasLocationString) != 0) SkipString(record, ref position);
            if ((flags & HasGuid) != 0) position += 16;
            if ((flags & HasCreationTime) != 0)
            {
                var time = record.Slice(position, 8);
                if (time.IndexOfAnyExcept((byte)0) >= 0)
                {
                    dates++;
                    if (scrub) time.Clear();
                }
            }
            return new Result(paths, dates);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidDataException)
        {
            // Unknown or damaged moniker: fall back to a search for absolute local paths of the same length.
            return new Result(WordDocumentText.ScrubPaths(record[header..], scrub), 0);
        }
    }

    // HyperlinkString: length in characters including the terminating zero, then UTF-16.
    private static void SkipString(Span<byte> record, ref int position)
    {
        var count = BinaryPrimitives.ReadUInt32LittleEndian(record[position..]);
        if (count > (uint)record.Length) throw new InvalidDataException();
        position += 4 + (int)count * 2;
        if (position > record.Length) throw new InvalidDataException();
    }

    private static int ProcessString(Span<byte> record, ref int position, bool scrub)
    {
        var start = position + 4;
        SkipString(record, ref position);
        return ReplaceUnicodeIfFilePath(record[start..position], scrub);
    }

    private static int ProcessMoniker(Span<byte> record, ref int position, bool scrub, int depth)
    {
        if (depth > 8) throw new InvalidDataException();
        var clsid = new Guid(record.Slice(position, 16));
        position += 16;
        if (clsid == UrlMoniker)
        {
            // length: bytes of the null-terminated URL plus optional serialGUID, serialVersion and uriFlags.
            var length = BinaryPrimitives.ReadUInt32LittleEndian(record[position..]);
            if (length > (uint)(record.Length - position - 4)) throw new InvalidDataException();
            var data = record.Slice(position + 4, (int)length);
            position += 4 + (int)length;
            var end = 0;
            while (end + 1 < data.Length && (data[end] | data[end + 1]) != 0) end += 2;
            return ReplaceUnicodeIfFilePath(data[..end], scrub);
        }
        if (clsid == FileMoniker)
        {
            // cAnti, ansiLength + ansiPath, endServer, versionNumber, reserved (20 bytes), cbUnicodePathSize,
            // then optionally cbUnicodePathBytes, usKeyValue and unicodePath (no terminator).
            position += 2;
            var ansiLength = BinaryPrimitives.ReadUInt32LittleEndian(record[position..]);
            if (ansiLength > (uint)(record.Length - position - 4)) throw new InvalidDataException();
            var ansi = record.Slice(position + 4, (int)ansiLength);
            position += 4 + (int)ansiLength + 24;
            var found = ReplaceAnsi(ansi, scrub);
            var unicodeSize = BinaryPrimitives.ReadUInt32LittleEndian(record[position..]);
            position += 4;
            if (unicodeSize > 0)
            {
                var bytes = BinaryPrimitives.ReadUInt32LittleEndian(record[position..]);
                if (bytes > (uint)(record.Length - position - 6) || bytes % 2 != 0) throw new InvalidDataException();
                found += ReplaceUnicode(record.Slice(position + 6, (int)bytes), scrub);
                position += 6 + (int)bytes;
            }
            return found > 0 ? 1 : 0;
        }
        if (clsid == CompositeMoniker)
        {
            var count = BinaryPrimitives.ReadUInt32LittleEndian(record[position..]);
            position += 4;
            if (count > 64) throw new InvalidDataException();
            var found = 0;
            for (var i = 0; i < count; i++) found += ProcessMoniker(record, ref position, scrub, depth + 1);
            return found;
        }
        if (clsid == ItemMoniker)
        {
            // delimiterLength + delimiter, itemLength + item: a location inside the target, not a path.
            for (var part = 0; part < 2; part++)
            {
                var length = BinaryPrimitives.ReadUInt32LittleEndian(record[position..]);
                if (length > (uint)(record.Length - position - 4)) throw new InvalidDataException();
                position += 4 + (int)length;
            }
            return 0;
        }
        if (clsid == AntiMoniker)
        {
            position += 4;
            return 0;
        }
        throw new InvalidDataException();
    }

    // Строки URL- и строкового моникера без схемы бывают и веб-адресами (intranet/page, www.example.com/x): путём к файлу
    // считается только строка с диском, UNC, file:, ведущими ./ ../ .\ ..\ или /, либо с обратной косой чертой.
    private static int ReplaceUnicodeIfFilePath(Span<byte> text, bool scrub)
    {
        var value = Encoding.Unicode.GetString(text);
        var terminator = value.IndexOf('\0');
        return LooksLikeFilePath(terminator >= 0 ? value[..terminator] : value) ? ReplaceUnicode(text, scrub) : 0;
    }

    internal static bool LooksLikeFilePath(string value)
    {
        var text = value.TrimStart();
        if (text.Length >= 2 && char.IsAsciiLetter(text[0]) && text[1] == ':') return true;
        if (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.StartsWith('/') || text.StartsWith("./", StringComparison.Ordinal) || text.StartsWith("../", StringComparison.Ordinal))
            return true;
        return text.Contains('\\');
    }

    private static int ReplaceUnicode(Span<byte> text, bool scrub)
    {
        var value = Encoding.Unicode.GetString(text);
        var replacement = OleLocalPath.Shorten(value);
        if (replacement == null) return 0;
        if (scrub) Encoding.Unicode.GetBytes(replacement).CopyTo(text);
        return 1;
    }

    // ANSI path in the system code page: separators are ASCII, other bytes go through Latin-1 unchanged.
    private static int ReplaceAnsi(Span<byte> text, bool scrub)
    {
        var value = Encoding.Latin1.GetString(text);
        var replacement = OleLocalPath.Shorten(value);
        if (replacement == null) return 0;
        if (scrub) Encoding.Latin1.GetBytes(replacement).CopyTo(text);
        return 1;
    }
}
