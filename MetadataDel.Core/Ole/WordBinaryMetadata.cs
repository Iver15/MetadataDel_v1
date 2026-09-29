using System.Buffers.Binary;

namespace MetadataDel.Core.Ole;

/// <summary>
/// Свойства, которые Word 97–2003 хранит вне OLE property streams: таблица SttbfAssoc
/// (автор, последний редактор, заголовок, шаблон, источники слияния), даты и счётчики Dop,
/// история сохранений SttbSavedBy, авторы исправлений и комментариев, инициалы и даты комментариев.
/// Формат описан в [MS-DOC].
/// </summary>
internal static class WordBinaryMetadata
{
    private const ushort WordIdent = 0xA5EC;
    private const ushort MinWord97Fib = 0x00C1;
    private const ushort EncryptedFlag = 0x0100;
    private const ushort WhichTableFlag = 0x0200;
    private const int PlcfandRefIndex = 4;
    private const int DopIndex = 31;
    private const int SttbfAssocIndex = 32;
    private const int GrpXstAtnOwnersIndex = 36;
    private const int SttbfRMarkIndex = 51;
    private const int SttbSavedByIndex = 71;
    private const int AtrdExtraIndex = 97;
    private const int AtrdSize = 30;       // ATRDPre10; xstUsrInitl (cch + 9 chars) comes first
    private const int AtrdExtraSize = 18;  // ATRDPost10; dttm comes first
    private const string AnonymousName = "Unknown";
    private const string AnonymousInitials = "U";
    // DopBase: dttmCreated, dttmRevised, dttmLastPrint, nRevision, tmEdited occupy bytes 0x14..0x25.
    private const int DopStatsStart = 0x14;
    private const int DopStatsEnd = 0x26;

    /// <summary>Возвращает имя потока таблиц или <c>null</c> с причиной, если документ нельзя разобрать.</summary>
    public static string? GetTableStreamName(ReadOnlySpan<byte> wordDocument, out string? problem)
    {
        problem = null;
        if (wordDocument.Length < 34 || U16(wordDocument, 0) != WordIdent)
            problem = "поток WordDocument не похож на документ Word";
        else if (U16(wordDocument, 2) < MinWord97Fib)
            problem = "документы Word 6/95 не поддерживаются";
        else if ((U16(wordDocument, 10) & EncryptedFlag) != 0)
            problem = "документ Word зашифрован";
        if (problem != null) return null;
        return (U16(wordDocument, 10) & WhichTableFlag) != 0 ? "1Table" : "0Table";
    }

    /// <summary>
    /// Находит служебные свойства и при <paramref name="scrub"/> стирает их в переданных буферах.
    /// Изменённые таблицы пишутся на прежнее место (остаток заполняется нулями) или, если не помещаются,
    /// в конец потока таблиц; новые fc/lcb записываются в FIB.
    /// То, что найдено, но не может быть удалено без переразметки текста, добавляется в <paramref name="unremovable"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">Структуры FIB или таблиц повреждены.</exception>
    public static List<string> Process(byte[] wordDocument, ref byte[] table, bool scrub, List<string> unremovable)
    {
        var findings = new List<string>();

        if (WordDocumentText.ProcessFieldPaths(wordDocument, table, scrub) > 0)
            findings.Add("локальные пути в полях Word");
        if (WordDocumentText.HasHiddenText(wordDocument, table))
            unremovable.Add("скрытый текст Word");

        if (TryGetBlock(wordDocument, table, DopIndex, out var dop, out _) && dop.Length >= DopStatsEnd)
        {
            var stats = dop.Slice(DopStatsStart, DopStatsEnd - DopStatsStart);
            if (stats.IndexOfAnyExcept((byte)0) >= 0)
            {
                findings.Add("даты создания и правки, число редакций Word");
                if (scrub) stats.Clear();
            }
        }

        if (TryGetBlock(wordDocument, table, SttbfAssocIndex, out var assoc, out var assocLcbOffset) && !assoc.IsEmpty)
        {
            var empty = BuildEmptySttb(assoc);
            if (empty.Length != assoc.Length || !assoc[..empty.Length].SequenceEqual(empty))
            {
                findings.Add("автор, заголовок и шаблон во внутренних таблицах Word");
                if (scrub)
                {
                    assoc.Clear();
                    empty.CopyTo(assoc);
                    BinaryPrimitives.WriteUInt32LittleEndian(wordDocument.AsSpan(assocLcbOffset), (uint)empty.Length);
                }
            }
        }

        if (TryGetBlock(wordDocument, table, SttbSavedByIndex, out var savedBy, out var savedByLcbOffset) && !savedBy.IsEmpty)
        {
            findings.Add("история сохранений Word");
            if (scrub)
            {
                savedBy.Clear();
                BinaryPrimitives.WriteUInt32LittleEndian(wordDocument.AsSpan(savedByLcbOffset), 0);
            }
        }

        if (TryGetBlock(wordDocument, table, SttbfRMarkIndex, out var revisionAuthors, out _))
        {
            var anonymous = BuildSttb(revisionAuthors, _ => AnonymousName);
            if (!revisionAuthors.SequenceEqual(anonymous))
            {
                findings.Add("авторы исправлений Word");
                if (scrub) table = ReplaceBlock(wordDocument, table, SttbfRMarkIndex, anonymous);
            }
        }

        if (TryGetBlock(wordDocument, table, GrpXstAtnOwnersIndex, out var commentAuthors, out _))
        {
            var anonymous = AnonymizeXstGroup(commentAuthors);
            if (!commentAuthors.SequenceEqual(anonymous))
            {
                findings.Add("авторы комментариев Word");
                if (scrub) table = ReplaceBlock(wordDocument, table, GrpXstAtnOwnersIndex, anonymous);
            }
        }

        if (TryGetBlock(wordDocument, table, PlcfandRefIndex, out var commentRefs, out _))
        {
            if ((commentRefs.Length - 4) % (4 + AtrdSize) != 0)
                throw new InvalidDataException("Повреждена таблица комментариев Word.");
            var count = (commentRefs.Length - 4) / (4 + AtrdSize);
            var initials = new byte[2 + 2 * AnonymousInitials.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(initials, (ushort)AnonymousInitials.Length);
            System.Text.Encoding.Unicode.GetBytes(AnonymousInitials).CopyTo(initials, 2);
            for (var i = 0; i < count; i++)
            {
                var xstUsrInitl = commentRefs.Slice((count + 1) * 4 + i * AtrdSize, 20);
                if (xstUsrInitl[..initials.Length].SequenceEqual(initials) && xstUsrInitl[initials.Length..].IndexOfAnyExcept((byte)0) < 0)
                    continue;
                if (!findings.Contains("инициалы авторов комментариев Word")) findings.Add("инициалы авторов комментариев Word");
                if (scrub)
                {
                    xstUsrInitl.Clear();
                    initials.CopyTo(xstUsrInitl);
                }
            }
        }

        if (TryGetBlock(wordDocument, table, AtrdExtraIndex, out var commentExtras, out _))
        {
            if (commentExtras.Length % AtrdExtraSize != 0)
                throw new InvalidDataException("Повреждены даты комментариев Word.");
            for (var offset = 0; offset < commentExtras.Length; offset += AtrdExtraSize)
            {
                var dttm = commentExtras.Slice(offset, 4);
                if (dttm.IndexOfAnyExcept((byte)0) < 0) continue;
                if (!findings.Contains("даты комментариев Word")) findings.Add("даты комментариев Word");
                if (scrub) dttm.Clear();
            }
        }

        return findings;
    }

    // Writes a rebuilt block in place when it fits, otherwise appends it to the table stream; updates fc/lcb.
    private static byte[] ReplaceBlock(byte[] wordDocument, byte[] table, int index, byte[] replacement)
    {
        TryGetBlock(wordDocument, table, index, out var block, out var lcbOffset);
        var fc = BinaryPrimitives.ReadUInt32LittleEndian(wordDocument.AsSpan(lcbOffset - 4));
        block.Clear();
        if (replacement.Length > block.Length)
        {
            fc = (uint)table.Length;
            Array.Resize(ref table, table.Length + replacement.Length);
        }
        replacement.CopyTo(table, (int)fc);
        BinaryPrimitives.WriteUInt32LittleEndian(wordDocument.AsSpan(lcbOffset - 4), fc);
        BinaryPrimitives.WriteUInt32LittleEndian(wordDocument.AsSpan(lcbOffset), (uint)replacement.Length);
        return table;
    }

    // GrpXstAtnOwners: back-to-back Xst (cch + UTF-16 chars) filling the whole block.
    private static byte[] AnonymizeXstGroup(ReadOnlySpan<byte> group)
    {
        using var result = new MemoryStream();
        var offset = 0;
        while (offset < group.Length)
        {
            if (offset + 2 > group.Length) throw new InvalidDataException("Повреждён список авторов комментариев Word.");
            offset += 2 + 2 * U16(group, offset);
            if (offset > group.Length) throw new InvalidDataException("Повреждён список авторов комментариев Word.");
            WriteXst(result, AnonymousName);
        }
        return result.ToArray();
    }

    private static void WriteXst(Stream output, string value)
    {
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(length, (ushort)value.Length);
        output.Write(length);
        output.Write(System.Text.Encoding.Unicode.GetBytes(value));
    }

    // Same header and count; each string produced by <paramref name="replace"/>, extra data zeroed.
    private static byte[] BuildSttb(ReadOnlySpan<byte> sttb, Func<int, string> replace)
    {
        var extended = sttb.Length >= 2 && U16(sttb, 0) == 0xFFFF;
        if (!extended) throw new InvalidDataException("Неподдерживаемая таблица строк Word.");
        if (sttb.Length < 6) throw new InvalidDataException("Повреждена таблица строк Word.");
        var count = U16(sttb, 2);
        var extraSize = U16(sttb, 4);
        using var result = new MemoryStream();
        result.Write(sttb[..6]);
        for (var i = 0; i < count; i++)
        {
            WriteXst(result, replace(i));
            result.Write(new byte[extraSize]);
        }
        return result.ToArray();
    }

    // Same header, every string replaced by an empty one (cch = 0, extra data zeroed).
    private static byte[] BuildEmptySttb(ReadOnlySpan<byte> sttb)
    {
        var extended = sttb.Length >= 2 && U16(sttb, 0) == 0xFFFF;
        var header = extended ? 2 : 0;
        if (sttb.Length < header + 4) throw new InvalidDataException("Повреждена таблица строк Word.");
        var count = U16(sttb, header);
        var extraSize = U16(sttb, header + 2);
        var lengthSize = extended ? 2 : 1;
        var result = new byte[header + 4 + count * (lengthSize + extraSize)];
        if (result.Length > sttb.Length) throw new InvalidDataException("Повреждена таблица строк Word.");
        sttb[..(header + 4)].CopyTo(result);
        return result;
    }

    // FIB layout: FibBase (32 bytes), csw + fibRgW, cslw + fibRgLw, cbRgFcLcb + (fc, lcb) pairs.
    internal static bool TryGetBlock(byte[] wordDocument, byte[] table, int index, out Span<byte> block, out int lcbOffset)
    {
        block = default;
        lcbOffset = 0;
        var position = 32;
        position += 2 + 2 * ReadCount(wordDocument, position);
        position += 2 + 4 * ReadCount(wordDocument, position);
        var pairs = ReadCount(wordDocument, position);
        position += 2 + 8 * index;
        if (index >= pairs || position + 8 > wordDocument.Length) return false;

        var fc = BinaryPrimitives.ReadUInt32LittleEndian(wordDocument.AsSpan(position));
        var lcb = BinaryPrimitives.ReadUInt32LittleEndian(wordDocument.AsSpan(position + 4));
        if (lcb == 0) return false;
        if (fc > (uint)table.Length || lcb > (uint)table.Length - fc)
            throw new InvalidDataException("Ссылка FIB выходит за пределы потока таблиц Word.");
        block = table.AsSpan((int)fc, (int)lcb);
        lcbOffset = position + 4;
        return true;
    }

    private static int ReadCount(byte[] wordDocument, int position) =>
        position + 2 <= wordDocument.Length
            ? U16(wordDocument, position)
            : throw new InvalidDataException("Повреждён заголовок FIB документа Word.");

    private static ushort U16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
}
