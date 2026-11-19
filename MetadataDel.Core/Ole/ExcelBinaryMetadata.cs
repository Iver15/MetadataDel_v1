using System.Buffers.Binary;
using System.Text;

namespace MetadataDel.Core.Ole;

/// <summary>
/// Имена пользователей и пути в книге Excel 97–2003 (BIFF8, [MS-XLS]): WriteAccess (последний сохранивший),
/// Note (авторы примечаний), FileSharing (кто задал пароль на запись), HLink (локальные пути в гиперссылках
/// и дата их создания), SupBook и ExternName (пути к связанным книгам), UserBView (имена личных представлений
/// общей книги).
/// </summary>
/// <remarks>
/// Все правки выполняются на месте с сохранением длины записей: смещения листов в BoundSheet8.lbPlyPos,
/// Index и DBCell остаются верными, переразметка потока не нужна. Пути заменяются относительными
/// <c>.\.\имя</c> той же длины (<see cref="OleLocalPath"/>). Запись, продолженная записями Continue,
/// не меняется: её поля могут переходить через границу фрагментов, поэтому о ней сообщается как о проблеме.
/// GUID пользовательских представлений (UserBView/UserSViewBegin) не меняется: это случайный идентификатор
/// представления, на который ссылаются имена Z_&lt;guid&gt;.wvu.* и записи листов; его замена ломает связи.
/// </remarks>
internal static class ExcelBinaryMetadata
{
    private const ushort EofRecord = 0x000A;
    private const ushort NoteRecord = 0x001C;
    private const ushort ExternNameRecord = 0x0023;
    private const ushort FilePassRecord = 0x002F;
    private const ushort ContinueRecord = 0x003C;
    private const ushort FileSharingRecord = 0x005B;
    private const ushort WriteAccessRecord = 0x005C;
    private const ushort BoundSheetRecord = 0x0085;
    private const ushort MsoDrawingGroupRecord = 0x00EB;
    private const ushort UserBViewRecord = 0x01A9;
    private const ushort SupBookRecord = 0x01AE;
    private const ushort HLinkRecord = 0x01B8;
    private const ushort BofRecord = 0x0809;
    private const ushort Biff5Version = 0x0500;

    /// <summary>
    /// Находит имена и пути и при <paramref name="scrub"/> затирает их в переданном буфере.
    /// Скрытые листы добавляются в <paramref name="unremovable"/>: удалить их без переразметки книги нельзя;
    /// метаданные изображений — в <paramref name="residual"/>. <paramref name="problem"/> описывает то,
    /// что помешало разобрать или очистить книгу целиком; правки до места проблемы остаются корректными.
    /// </summary>
    public static List<string> Process(byte[] workbook, bool scrub, List<string> unremovable, List<string> residual, out string? problem)
    {
        var problems = new List<string>();
        var findings = new List<string>();
        void Find(string finding) { if (!findings.Contains(finding)) findings.Add(finding); }

        var sawGlobalsEnd = false;
        var sawBof = false;
        var depth = 0;
        var personalViews = 0;
        using var drawingGroup = new MemoryStream();
        var previous = (ushort)0;
        var offset = 0;
        while (offset + 4 <= workbook.Length)
        {
            var type = BinaryPrimitives.ReadUInt16LittleEndian(workbook.AsSpan(offset));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(workbook.AsSpan(offset + 2));
            var body = offset + 4;
            if (body + length > workbook.Length)
            {
                problems.Add("поток Workbook обрезан (последняя запись неполная)");
                break;
            }
            var record = workbook.AsSpan(body, length);
            var next = body + length;
            var continued = next + 4 <= workbook.Length && BinaryPrimitives.ReadUInt16LittleEndian(workbook.AsSpan(next)) == ContinueRecord;
            // A continued record is only inspected: its fields may cross into the Continue fragment.
            var edit = scrub && !continued;

            try
            {
                switch (type)
                {
                    case BofRecord:
                        if (!sawBof && length >= 2 && BinaryPrimitives.ReadUInt16LittleEndian(record) == Biff5Version)
                        {
                            problem = "книга Excel 5.0/95 (BIFF5) не поддерживается";
                            return findings;
                        }
                        sawBof = true;
                        depth++;
                        break;
                    case EofRecord:
                        sawGlobalsEnd = true;
                        if (depth > 0) depth--;
                        break;
                    case FilePassRecord:
                        problems.Add("книга Excel зашифрована");
                        problem = string.Join("; ", problems);
                        return findings;
                    case WriteAccessRecord:
                        if (ReadUserName(record).Trim().Length > 0)
                        {
                            Find("имя пользователя, сохранившего книгу Excel");
                            if (edit)
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
                        var author = record[8..];
                        if (ReadUserName(author).Trim().Length > 0)
                        {
                            Find("авторы примечаний Excel");
                            if (edit) BlankCharacters(author);
                        }
                        break;
                    case FileSharingRecord:
                        // fReadOnlyRec, wResPass, then stUNUsername only when a write-reservation password is set.
                        // The password verifier and the read-only recommendation are functional and stay.
                        if (length < 7 || BinaryPrimitives.ReadUInt16LittleEndian(record[2..]) == 0) break;
                        var reservedBy = record[4..];
                        if (ReadUserName(reservedBy).Trim().Length > 0)
                        {
                            Find("имя пользователя, задавшего пароль на запись книги Excel");
                            if (edit) BlankCharacters(reservedBy);
                        }
                        break;
                    case BoundSheetRecord:
                        // lbPlyPos, hsState (0 visible, 1 hidden, 2 very hidden), dt, stName (ShortXLUnicodeString).
                        if (length >= 8 && (workbook[body + 4] & 0x03) != 0)
                            unremovable.Add($"скрытый лист Excel «{ReadSheetName(record[6..])}»");
                        break;
                    case HLinkRecord:
                        var link = ExcelHyperlink.Process(record, edit);
                        if (link.Paths > 0) Find("локальные пути в гиперссылках Excel");
                        if (link.Dates > 0) Find("даты создания гиперссылок Excel");
                        if (continued && link.Paths + link.Dates > 0) problems.Add("гиперссылка Excel продолжается в записи Continue");
                        break;
                    case SupBookRecord:
                        if (ProcessSupBook(record, edit))
                        {
                            Find("пути к связанным книгам Excel");
                            if (continued) problems.Add("ссылка на внешнюю книгу Excel продолжается в записи Continue");
                        }
                        break;
                    case ExternNameRecord:
                        if (ProcessExternName(record, edit))
                        {
                            Find("пути к связанным книгам Excel");
                            if (continued) problems.Add("внешнее имя Excel продолжается в записи Continue");
                        }
                        break;
                    case UserBViewRecord:
                        if (ProcessUserBView(record, edit, ref personalViews))
                        {
                            Find("имена личных представлений общей книги Excel");
                            if (continued) problems.Add("представление Excel продолжается в записи Continue");
                        }
                        break;
                    case MsoDrawingGroupRecord:
                        drawingGroup.Write(record);
                        break;
                    case ContinueRecord when previous == MsoDrawingGroupRecord:
                        drawingGroup.Write(record);
                        break;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidDataException)
            {
                problems.Add($"не удалось разобрать запись 0x{type:X4}");
            }

            if (type != ContinueRecord) previous = type;
            offset = next;
        }

        if (offset < workbook.Length && workbook.AsSpan(offset).IndexOfAnyExcept((byte)0) >= 0 && problems.Count == 0)
            problems.Add("поток Workbook обрезан");
        if (!sawGlobalsEnd) problems.Add("не удалось разобрать поток Workbook");
        else if (depth > 0 && problems.Count == 0) problems.Add("поток Workbook обрезан (нет конца листа)");

        if (OleImages.HasMetadata(drawingGroup.ToArray()))
            residual.Add("метаданные изображений в книге Excel (EXIF, GPS)");

        problem = problems.Count == 0 ? null : string.Join("; ", problems.Distinct());
        return findings;
    }

    // SupBook: ctab, cch, virtPath (XLUnicodeStringNoCch), rgst. cch 0x0401 is the workbook itself, 0x3A01 add-ins.
    private static bool ProcessSupBook(Span<byte> record, bool scrub)
    {
        if (record.Length < 4) return false;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(record[2..]);
        if (count is 0x0401 or 0x3A01 || count == 0) return false;
        if (record.Length < 5) throw new InvalidDataException();
        var wide = (record[4] & 1) != 0;
        var chars = record.Slice(5, count * (wide ? 2 : 1));
        var path = Decode(chars, wide);
        var replacement = ShortenVirtualPath(path);
        if (replacement == null) return false;
        if (scrub) Encode(replacement, wide).CopyTo(chars);
        return true;
    }

    // ExternName: flags (2 bytes), 4 bytes of ixals/reserved, then a ShortXLUnicodeString name in every variant.
    private static bool ProcessExternName(Span<byte> record, bool scrub)
    {
        if (record.Length < 8) return false;
        var count = record[6];
        var wide = (record[7] & 1) != 0;
        var chars = record.Slice(8, Math.Min(record.Length - 8, count * (wide ? 2 : 1)));
        var replacement = OleLocalPath.Shorten(Decode(chars, wide));
        if (replacement == null) return false;
        if (scrub) Encode(replacement, wide).CopyTo(chars);
        return true;
    }

    // UserBView: st (XLUnicodeString) at offset 50; fPersonalView is bit 0 of the word at offset 46.
    // A personal view of a shared workbook is named after its user ("Имя - Личное представление").
    private static bool ProcessUserBView(Span<byte> record, bool scrub, ref int index)
    {
        if (record.Length < 53 || (record[46] & 1) == 0) return false;
        var name = record[50..];
        var count = BinaryPrimitives.ReadUInt16LittleEndian(name);
        var wide = (name[2] & 1) != 0;
        var chars = name.Slice(3, count * (wide ? 2 : 1));
        if (Decode(chars, wide).Trim().Length == 0) return false;
        index++;
        if (scrub)
        {
            var anonymous = count >= 6 ? $"View {index}" : index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Encode(anonymous.PadRight(count)[..count], wide).CopyTo(chars);
        }
        return true;
    }

    /// <summary>
    /// Закодированный путь внешней книги ([MS-XLS] VirtualPath): 0x01 в начале — признак кодирования,
    /// далее 0x01 + буква диска (или «@» для UNC), 0x02 — корень диска, 0x03 — разделитель каталогов,
    /// 0x04 — «..», 0x05 + длина — URL как есть, 0x06–0x08 — папки автозагрузки и библиотеки Office.
    /// Каталоги заменяются на <c>.\</c> (0x03 в закодированном виде) с сохранением числа символов.
    /// Пути DDE/OLE («приложение 0x03 тема») сокращаются в теме.
    /// </summary>
    internal static string? ShortenVirtualPath(string path)
    {
        if (path.Length == 0) return null;
        if (path[0] != '\u0001')
        {
            var topic = path.IndexOf('\u0003') + 1;
            var shortened = OleLocalPath.Shorten(path[topic..]);
            return shortened == null ? null : path[..topic] + shortened;
        }
        var body = path[1..];
        if (body.Length > 2 && body[0] == '\u0005')
        {
            var url = OleLocalPath.Shorten(body[2..]);
            return url == null ? null : path[..3] + url;
        }
        var nameStart = body.LastIndexOfAny(['\u0003', '\\', '/']) + 1;
        var directory = body[..nameStart];
        // Separators, ".." and the Office special folders say nothing about the user.
        if (!directory.Any(c => c is '\u0001' or '\u0002' || c >= ' ' && c != '.')) return null;
        var name = body[nameStart..];
        var padding = body.Length - name.Length;
        var prefix = string.Concat(Enumerable.Repeat(".\u0003", padding / 2));
        return "\u0001" + (padding % 2 == 1 ? prefix + "\u0003" : prefix) + name;
    }

    private static string Decode(ReadOnlySpan<byte> chars, bool wide) =>
        wide ? Encoding.Unicode.GetString(chars) : Encoding.Latin1.GetString(chars);

    // Same character count as the decoded original; compressed strings only receive Latin-1 characters back.
    private static byte[] Encode(string text, bool wide) =>
        wide ? Encoding.Unicode.GetBytes(text) : Encoding.Latin1.GetBytes(text);

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
