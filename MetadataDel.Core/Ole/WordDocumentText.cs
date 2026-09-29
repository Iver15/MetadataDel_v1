using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace MetadataDel.Core.Ole;

/// <summary>
/// Текст документа Word 97–2003 через таблицу фрагментов (Clx) и прямое форматирование символов (CHPX FKP).
/// Текст нельзя укорачивать: позиции символов (CP) используются во всех таблицах документа,
/// поэтому правки выполняются на месте с сохранением длины.
/// </summary>
internal static partial class WordDocumentText
{
    private const int ClxIndex = 33;
    private const int PlcfBteChpxIndex = 12;
    private const int FkpSize = 512;
    private const char FieldBegin = '\u0013';
    private const char FieldSeparator = '\u0014';
    private const char FieldEnd = '\u0015';
    private const ushort SprmCFVanish = 0x083C;
    private const ushort SprmCFData = 0x0806;
    private const ushort SprmCPicLocation = 0x6A03;

    // A quoted local path in a field instruction: drive letter, UNC share or file: URL. The closing quote may be
    // missing when a nested field starts inside the quoted text.
    [GeneratedRegex("\"(?<path>(?:[A-Za-z]:[\\\\/]|\\\\\\\\|file:)[^\"]*)(?<close>\"|$)", RegexOptions.IgnoreCase)]
    private static partial Regex QuotedLocalPath();

    // A local path inside binary hyperlink data: drive letter or UNC share (\\server) followed by path characters.
    // A doubled separator after "." is our own relative padding (see RelativePath), not a share.
    [GeneratedRegex("(?:[A-Za-z]:[\\\\/]|(?<![.\\\\/])\\\\\\\\[A-Za-z0-9])[^\\x00-\\x1F\"<>|*?]{2,}")]
    private static partial Regex LocalPathInBinary();

    private readonly record struct Piece(int Offset, int Length, bool Compressed);

    /// <summary>
    /// Сокращает локальные пути в кодах полей (HYPERLINK, INCLUDEPICTURE, INCLUDETEXT, LINK и др.)
    /// до имени файла: <c>"C:\Users\имя\a.png"</c> → <c>"a.png"</c> и пробелы до прежней длины.
    /// Возвращает число найденных путей.
    /// </summary>
    public static int ProcessFieldPaths(byte[] wordDocument, byte[] table, bool scrub)
    {
        var found = 0;
        foreach (var piece in ReadPieces(wordDocument, table))
        {
            var encoding = piece.Compressed ? Encoding.Latin1 : Encoding.Unicode;
            var charSize = piece.Compressed ? 1 : 2;
            var bytes = wordDocument.AsSpan(piece.Offset, piece.Length);
            // Both decoders map one code unit to one char, so char indexes translate back to byte offsets.
            var text = encoding.GetString(bytes).ToCharArray();
            foreach (var (start, length) in FieldInstructions(text))
            {
                var instruction = new string(text, start, length);
                foreach (Match match in QuotedLocalPath().Matches(instruction))
                {
                    var path = match.Groups["path"].Value;
                    var name = path[(path.LastIndexOfAny(['\\', '/', ':']) + 1)..];
                    found++;
                    if (!scrub) continue;
                    // Only the replaced range is re-encoded; the rest of the piece stays byte-identical.
                    var replacement = ("\"" + name + match.Groups["close"].Value).PadRight(match.Length);
                    encoding.GetBytes(replacement).CopyTo(bytes[((start + match.Index) * charSize)..]);
                }
            }
        }
        return found;
    }

    /// <summary>
    /// Есть ли в документе скрытый текст (sprmCFVanish). Скрытые знаки абзаца и ячеек, которыми Word
    /// склеивает абзацы, текстом не считаются.
    /// </summary>
    public static bool HasHiddenText(byte[] wordDocument, byte[] table)
    {
        var pieces = ReadPieces(wordDocument, table);
        foreach (var run in ReadCharacterSprms(wordDocument, table))
        {
            // 0x01 hides the run; 0x81 inverts the style value, which is visible by default.
            if (!run.Sprms.Any(sprm => sprm.Code == SprmCFVanish && wordDocument[sprm.Offset] is 0x01 or 0x81)) continue;
            foreach (var piece in pieces)
            {
                var start = Math.Max(run.Start, piece.Offset);
                var end = Math.Min(run.End, piece.Offset + piece.Length);
                if (start >= end) continue;
                var text = (piece.Compressed ? Encoding.Latin1 : Encoding.Unicode).GetString(wordDocument, start, end - start);
                if (text.Any(c => !char.IsWhiteSpace(c) && c is not ('\u0007' or '\u000C'))) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Затирает локальные пути в данных гиперссылок (поток Data, на них указывает sprmCPicLocation
    /// у символа с sprmCFData): путь заменяется именем файла и нулями до прежней длины. Возвращает число путей.
    /// </summary>
    public static int ProcessHyperlinkData(byte[] wordDocument, byte[] table, byte[] data, bool scrub)
    {
        var found = 0;
        var blocks = new HashSet<int>();
        foreach (var run in ReadCharacterSprms(wordDocument, table))
        {
            var hasData = run.Sprms.Any(sprm => sprm.Code == SprmCFData && wordDocument[sprm.Offset] == 1);
            var location = run.Sprms.FirstOrDefault(sprm => sprm.Code == SprmCPicLocation);
            if (hasData && location.Code != 0)
                blocks.Add(BinaryPrimitives.ReadInt32LittleEndian(wordDocument.AsSpan(location.Offset)));
        }
        foreach (var offset in blocks)
        {
            // NilPICFAndBinData: lcb (whole block), cbHeader, header, then binData (HFD for hyperlinks).
            if (offset < 0 || offset + 6 > data.Length) continue;
            var lcb = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
            var header = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4));
            if (lcb <= header || header < 6 || (long)offset + lcb > data.Length) continue;
            found += ScrubPaths(data.AsSpan(offset + header, lcb - header), scrub);
        }
        return found;
    }

    // ".\.\.\name" of exactly the old length: a valid relative path to the same file name. A doubled separator
    // absorbs an odd remainder; Windows and URL parsers collapse both.
    private static string RelativePath(string name, string original, int length)
    {
        var separator = original.Contains('\\') ? '\\' : '/';
        var padding = length - name.Length;
        var prefix = string.Concat(Enumerable.Repeat("." + separator, padding / 2));
        return padding % 2 == 1 ? prefix + separator + name : prefix + name;
    }

    // Path strings inside hyperlink monikers are length-prefixed, so they keep their length (see RelativePath).
    private static int ScrubPaths(Span<byte> block, bool scrub)
    {
        var found = 0;
        foreach (var (encoding, parity) in new[] { (Encoding.Latin1, 0), (Encoding.Unicode, 0), (Encoding.Unicode, 1) })
        {
            var charSize = encoding == Encoding.Latin1 ? 1 : 2;
            var region = block[parity..];
            region = region[..(region.Length / charSize * charSize)];
            var text = encoding.GetString(region);
            foreach (Match match in LocalPathInBinary().Matches(text))
            {
                var name = match.Value[(match.Value.LastIndexOfAny(['\\', '/', ':']) + 1)..];
                if (name.Length == match.Length) continue;
                found++;
                if (!scrub) continue;
                // "/C:/dir/a" (moniker form) takes its leading slash along; "file:///C:/" keeps its URL prefix.
                var start = match.Index;
                if (start > 0 && text[start - 1] is '/' or '\\' && (start < 2 || text[start - 2] is not ('/' or '\\'))) start--;
                var length = match.Index + match.Length - start;
                encoding.GetBytes(RelativePath(name, match.Value, length)).CopyTo(region[(start * charSize)..]);
            }
        }
        return found;
    }

    // Character run with direct formatting: byte range of its text in WordDocument and its sprms (operand offsets).
    private readonly record struct CharacterRun(int Start, int End, List<(ushort Code, int Offset)> Sprms);

    private static List<CharacterRun> ReadCharacterSprms(byte[] wordDocument, byte[] table)
    {
        var runs = new List<CharacterRun>();
        if (!WordBinaryMetadata.TryGetBlock(wordDocument, table, PlcfBteChpxIndex, out var plc, out _)) return runs;
        if ((plc.Length - 4) % 8 != 0) throw new InvalidDataException("Повреждена таблица форматирования Word.");
        var count = (plc.Length - 4) / 8;
        for (var i = 0; i < count; i++)
        {
            var pn = BinaryPrimitives.ReadUInt32LittleEndian(plc[((count + 1) * 4 + i * 4)..]) & 0x3FFFFF;
            var page = (long)pn * FkpSize;
            if (page + FkpSize > wordDocument.Length) throw new InvalidDataException("Повреждена таблица форматирования Word.");
            ReadFkp(wordDocument, (int)page, runs);
        }
        return runs;
    }

    // ChpxFkp: rgfc[crun + 1], rgb[crun] (word offsets of Chpx), crun in the last byte; Chpx = cb + grpprl.
    private static void ReadFkp(byte[] wordDocument, int page, List<CharacterRun> runs)
    {
        var fkp = wordDocument.AsSpan(page, FkpSize);
        var count = fkp[FkpSize - 1];
        if ((count + 1) * 4 + count > FkpSize - 1) return;
        for (var i = 0; i < count; i++)
        {
            var offset = fkp[(count + 1) * 4 + i] * 2;
            if (offset == 0 || offset >= FkpSize - 1) continue;
            var size = fkp[offset];
            if (offset + 1 + size > FkpSize) continue;
            var start = BinaryPrimitives.ReadInt32LittleEndian(fkp[(i * 4)..]);
            var end = BinaryPrimitives.ReadInt32LittleEndian(fkp[((i + 1) * 4)..]);
            runs.Add(new CharacterRun(start, end, ReadSprms(wordDocument, page + offset + 1, size)));
        }
    }

    // Operand size comes from spra (bits 13–15); variable operands start with their byte count.
    private static List<(ushort Code, int Offset)> ReadSprms(byte[] data, int start, int length)
    {
        var sprms = new List<(ushort Code, int Offset)>();
        var position = start;
        var end = start + length;
        while (position + 2 <= end)
        {
            var code = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position));
            position += 2;
            var operand = (code >> 13) switch
            {
                0 or 1 => 1,
                2 or 4 or 5 => 2,
                3 => 4,
                7 => 3,
                _ => position < end ? 1 + data[position] : -1
            };
            if (operand < 0 || position + operand > end) break;
            sprms.Add((code, position));
            position += operand;
        }
        return sprms;
    }

    // Instruction text runs from a field begin mark to its separator or end mark; nested fields are handled separately.
    private static IEnumerable<(int Start, int Length)> FieldInstructions(char[] text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != FieldBegin) continue;
            var end = i + 1;
            while (end < text.Length && text[end] is not (FieldBegin or FieldSeparator or FieldEnd)) end++;
            yield return (i + 1, end - i - 1);
        }
    }

    // Clx: Prc* (0x01, cbGrpprl, grpprl) then Pcdt (0x02, lcb, PlcPcd: CPs[n + 1], Pcd[n] of 8 bytes).
    private static List<Piece> ReadPieces(byte[] wordDocument, byte[] table)
    {
        var pieces = new List<Piece>();
        if (!WordBinaryMetadata.TryGetBlock(wordDocument, table, ClxIndex, out var clx, out _)) return pieces;
        var position = 0;
        while (position < clx.Length && clx[position] == 0x01)
        {
            if (position + 3 > clx.Length) throw new InvalidDataException("Повреждена таблица фрагментов Word.");
            position += 3 + BinaryPrimitives.ReadInt16LittleEndian(clx[(position + 1)..]);
        }
        if (position + 5 > clx.Length || clx[position] != 0x02) throw new InvalidDataException("Повреждена таблица фрагментов Word.");
        var plc = clx.Slice(position + 5);
        var lcb = BinaryPrimitives.ReadInt32LittleEndian(clx[(position + 1)..]);
        if (lcb < 4 || lcb > plc.Length || (lcb - 4) % 12 != 0) throw new InvalidDataException("Повреждена таблица фрагментов Word.");
        var count = (lcb - 4) / 12;
        for (var i = 0; i < count; i++)
        {
            var cpStart = BinaryPrimitives.ReadInt32LittleEndian(plc[(i * 4)..]);
            var cpEnd = BinaryPrimitives.ReadInt32LittleEndian(plc[((i + 1) * 4)..]);
            var fc = BinaryPrimitives.ReadUInt32LittleEndian(plc[((count + 1) * 4 + i * 8 + 2)..]);
            var compressed = (fc & 0x40000000) != 0;
            var offset = compressed ? (long)(fc & ~0x40000000u) / 2 : fc;
            var length = (long)(cpEnd - cpStart) * (compressed ? 1 : 2);
            if (length < 0 || offset + length > wordDocument.Length) throw new InvalidDataException("Повреждена таблица фрагментов Word.");
            pieces.Add(new Piece((int)offset, (int)length, compressed));
        }
        return pieces;
    }
}
