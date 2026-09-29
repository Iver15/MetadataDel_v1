using System.Buffers.Binary;
using System.Text;

namespace MetadataDel.Core.Ole;

/// <summary>
/// Имена пользователей в книге Excel 97–2003 (BIFF8): запись WriteAccess с именем последнего
/// сохранившего ([MS-XLS] 2.4.349) и авторы примечаний в записях Note листов ([MS-XLS] 2.4.179).
/// </summary>
internal static class ExcelBinaryMetadata
{
    private const ushort EofRecord = 0x000A;
    private const ushort NoteRecord = 0x001C;
    private const ushort FilePassRecord = 0x002F;
    private const ushort WriteAccessRecord = 0x005C;
    private const ushort BoundSheetRecord = 0x0085;

    /// <summary>
    /// Находит имена и при <paramref name="scrub"/> затирает их пробелами в переданном буфере.
    /// Длины записей не меняются, поэтому смещения листов и индексов остаются верными.
    /// Скрытые листы добавляются в <paramref name="unremovable"/>: удалить их без переразметки книги нельзя.
    /// </summary>
    public static List<string> Process(byte[] workbook, bool scrub, List<string> unremovable, out string? problem)
    {
        problem = null;
        var findings = new List<string>();
        var sawGlobalsEnd = false;
        var offset = 0;
        while (offset + 4 <= workbook.Length)
        {
            var type = BinaryPrimitives.ReadUInt16LittleEndian(workbook.AsSpan(offset));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(workbook.AsSpan(offset + 2));
            var body = offset + 4;
            if (body + length > workbook.Length) break;

            switch (type)
            {
                case FilePassRecord:
                    problem = "книга Excel зашифрована";
                    return findings;
                case WriteAccessRecord:
                    var record = workbook.AsSpan(body, length);
                    if (ReadUserName(record).Trim().Length > 0)
                    {
                        findings.Add("имя пользователя, сохранившего книгу Excel");
                        if (scrub)
                        {
                            // XLUnicodeString requires at least one character: cch = 1, fHighByte = 0, ' '.
                            record.Fill((byte)' ');
                            BinaryPrimitives.WriteUInt16LittleEndian(record, 1);
                            record[2] = 0;
                        }
                    }
                    break;
                case NoteRecord:
                    // rw, col, grbit, idObj, then stAuthor (XLUnicodeString); only cell notes carry an author.
                    if (length < 11) break;
                    var author = workbook.AsSpan(body + 8, length - 8);
                    if (ReadUserName(author).Trim().Length > 0)
                    {
                        if (!findings.Contains("авторы примечаний Excel")) findings.Add("авторы примечаний Excel");
                        if (scrub) BlankCharacters(author);
                    }
                    break;
                case BoundSheetRecord:
                    // lbPlyPos, hsState (0 visible, 1 hidden, 2 very hidden), dt, stName (ShortXLUnicodeString).
                    if (length >= 8 && (workbook[body + 4] & 0x03) != 0)
                        unremovable.Add($"скрытый лист Excel «{ReadSheetName(workbook.AsSpan(body + 6, length - 6))}»");
                    break;
                case EofRecord:
                    sawGlobalsEnd = true;
                    break;
            }
            offset = body + length;
        }

        // Trailing padding after the last substream is common; only a truncated globals part is an error.
        if (!sawGlobalsEnd) problem = "не удалось разобрать поток Workbook";
        return findings;
    }

    // Keeps cch and encoding so the record length is unchanged; every character becomes a space.
    private static void BlankCharacters(Span<byte> text)
    {
        var count = BinaryPrimitives.ReadUInt16LittleEndian(text);
        var wide = (text[2] & 1) != 0;
        var characters = text[3..];
        var size = Math.Min(characters.Length, count * (wide ? 2 : 1));
        for (var i = 0; i < size; i++)
            characters[i] = wide && i % 2 == 1 ? (byte)0 : (byte)' ';
    }

    private static string ReadSheetName(ReadOnlySpan<byte> name)
    {
        var count = name[0];
        var wide = (name[1] & 1) != 0;
        var size = Math.Min(name.Length - 2, count * (wide ? 2 : 1));
        return wide ? Encoding.Unicode.GetString(name.Slice(2, size)) : Encoding.Latin1.GetString(name.Slice(2, size));
    }

    private static string ReadUserName(ReadOnlySpan<byte> record)
    {
        if (record.Length < 3) return string.Empty;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(record);
        var wide = (record[2] & 1) != 0;
        var bytes = record[3..];
        var size = Math.Min(bytes.Length, count * (wide ? 2 : 1));
        return wide
            ? Encoding.Unicode.GetString(bytes[..size])
            : Encoding.Latin1.GetString(bytes[..size]);
    }
}
