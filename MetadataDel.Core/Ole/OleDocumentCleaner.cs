using OpenMcdf;
using MetadataDel.Core.Audit;
using MetadataDel.Core.Cleaning;

namespace MetadataDel.Core.Ole;

/// <summary>
/// Очищает OLE property streams во всех хранилищах и служебные свойства Word/Excel 97–2003 без установленного Office.
/// </summary>
public sealed class OleDocumentCleaner : IFileCleaner
{
    /// <inheritdoc />
    public Task<CleanResult> CleanAsync(string path, CleanOptions options, CancellationToken ct = default)
    {
        try
        {
            using var transaction = new FileCleaningTransaction(path, options, ct);
            var state = new ScrubState();
            var rebuilt = transaction.WorkingPath + ".rebuilt";
            try
            {
                // Copy live entries into a fresh container; free sectors must not retain deleted properties.
                using (var source = RootStorage.OpenRead(transaction.WorkingPath))
                using (var output = File.Create(rebuilt))
                    Rebuild(source, output, state, ct);
                File.Move(rebuilt, transaction.WorkingPath, overwrite: true);
                // Итоговая проверка пересобранного файла, как в DocxCleaner/ExcelCleaner: всё, что аудит ещё видит
                // (скрытый текст и листы, изображения с EXIF, неразобранные структуры), делает результат неполным.
                // Макросы VBA сохраняются намеренно, о них уже есть отдельное предупреждение.
                List<MetadataAuditFinding> residual;
                try
                {
                    residual = MetadataAuditService.Audit(transaction.WorkingPath).Findings.Where(f => f.Code != MacrosFindingCode).ToList();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    residual = new List<MetadataAuditFinding>();
                    state.WarnIncomplete($"Не удалось проверить результат очистки ({ex.Message}): часть метаданных могла остаться.");
                }
                if (residual.Count > 0)
                {
                    var audit = new MetadataAuditResult(path, Path.GetExtension(path).TrimStart('.'), residual);
                    state.WarnIncomplete("После очистки остались признаки метаданных: " + audit.Summarize());
                }
                transaction.Commit(ct);
            }
            finally { if (File.Exists(rebuilt)) File.Delete(rebuilt); }
            var warnings = new List<string?>(state.Warnings) { FileCleaningTransaction.WipeTimestamps(path, options.WipeFsTimestamps) };
            var message = string.Join(" ", warnings.OfType<string>().Distinct());
            // The file is written either way; Incomplete tells the caller that some metadata is still inside.
            return Task.FromResult(new CleanResult(path, true, message.Length == 0 ? null : message) { Incomplete = state.Incomplete });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new CleanResult(path, false, ex.Message));
        }
    }

    /// <summary>
    /// Очищает OLE-контейнер в памяти, например встроенный объект <c>oleObject*.bin</c> из DOCX/XLSX.
    /// Предупреждения (зашифрованный документ, неразборчивая структура) дописываются в <paramref name="warnings"/>.
    /// </summary>
    /// <exception cref="Exception">Данные не являются корректным OLE-контейнером (исключения OpenMcdf).</exception>
    internal static byte[] ScrubCompoundFile(byte[] compoundFile, List<string> warnings, CancellationToken ct = default) =>
        ScrubCompoundFile(compoundFile, warnings, out _, ct);

    /// <summary>
    /// То же, что <see cref="ScrubCompoundFile(byte[], List{string}, CancellationToken)"/>; <paramref name="incomplete"/>
    /// равен <c>true</c>, если часть метаданных осталась (зашифрованный или повреждённый документ, изображения с EXIF и т. п.).
    /// </summary>
    internal static byte[] ScrubCompoundFile(byte[] compoundFile, List<string> warnings, out bool incomplete, CancellationToken ct = default)
    {
        var state = new ScrubState();
        using var input = new MemoryStream(compoundFile, writable: false);
        using var output = new MemoryStream();
        using (var source = RootStorage.Open(input, StorageModeFlags.LeaveOpen))
            Rebuild(source, output, state, ct);
        warnings.AddRange(state.Warnings.Where(w => !warnings.Contains(w)));
        incomplete = state.Incomplete;
        return output.ToArray();
    }

    /// <summary>Код находки «макросы VBA»: макросы сохраняются намеренно, о них предупреждают отдельно.</summary>
    internal const string MacrosFindingCode = "ole.macros";

    private const string MacrosFinding = "макросы VBA";

    /// <summary>Перечисляет оставшиеся свойства во всех хранилищах контейнера, ничего не меняя.</summary>
    internal static List<(string Code, string Description)> Inspect(Storage root)
    {
        var findings = new List<(string Code, string Description)>();
        InspectStorage(root, string.Empty, findings);
        return findings;
    }

    // Warnings of one run and whether metadata was left in the file (encrypted or damaged parts, unsupported formats).
    private sealed class ScrubState
    {
        public List<string> Warnings { get; } = new();

        public bool Incomplete { get; private set; }

        public void Warn(string warning)
        {
            if (!Warnings.Contains(warning)) Warnings.Add(warning);
        }

        public void WarnIncomplete(string warning)
        {
            Warn(warning);
            Incomplete = true;
        }
    }

    // Scrubbed copies of streams and streams that are left out of the rebuilt storage.
    private sealed record ScrubPlan(Dictionary<string, byte[]> Replacements, HashSet<string> Dropped);

    private static void InspectStorage(Storage storage, string location, List<(string Code, string Description)> findings)
    {
        foreach (var entry in storage.EnumerateEntries())
        {
            if (entry.Name.StartsWith("\x05", StringComparison.Ordinal))
                findings.Add(("ole.properties", "OLE-свойства: " + location + entry.Name.TrimStart('\x05')));
            else if (entry.Type == EntryType.Storage)
                InspectStorage(storage.OpenStorage(entry.Name), location + entry.Name + "/", findings);
        }
        var described = new List<string>();
        ScrubApplicationProperties(storage, scrub: false, described, new ScrubState());
        findings.AddRange(described.Distinct().Select(d => (d == MacrosFinding ? MacrosFindingCode : "ole.internal",
            location.Length == 0 ? d : $"{d} ({location.TrimEnd('/')})")));
    }

    private static void Rebuild(RootStorage source, Stream output, ScrubState state, CancellationToken ct)
    {
        source.BaseStream.Position = 26;
        var version = (OpenMcdf.Version)source.BaseStream.ReadByte();
        using var destination = RootStorage.Create(output, version, StorageModeFlags.LeaveOpen);
        CopyStorage(source, destination, state, ct);
    }

    // Parsers report damaged structures as InvalidDataException; out-of-range reads of unchecked fields are the same thing.
    private static bool IsParseError(Exception ex) =>
        ex is InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException;

    private static string Describe(Exception ex) =>
        ex is InvalidDataException && ex.Message.Length > 0 ? ex.Message.TrimEnd('.') : "структура повреждена";

    // Word, Excel, OLE Package and VBA keep personal data inside their own streams; returns scrubbed copies of those streams.
    private static ScrubPlan ScrubApplicationProperties(Storage storage, bool scrub, List<string> findings, ScrubState state)
    {
        var plan = new ScrubPlan(new Dictionary<string, byte[]>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
        var replacements = plan.Replacements;
        var unremovable = new List<string>();
        var residual = new List<string>();
        if (storage.ContainsEntry("WordDocument"))
        {
            var wordDocument = ReadStream(storage, "WordDocument");
            var tableName = WordBinaryMetadata.GetTableStreamName(wordDocument, out var problem);
            if (tableName != null && !storage.ContainsEntry(tableName)) problem = "не найден поток таблиц Word";
            else if (tableName != null)
            {
                // Word reads only the table stream named by fWhichTblStm; the other one is a stale copy
                // (old authors, save history) and is left out of the rebuilt file.
                var inactive = tableName == "1Table" ? "0Table" : "1Table";
                if (storage.ContainsEntry(inactive))
                {
                    findings.Add($"устаревшая копия таблиц Word ({inactive})");
                    plan.Dropped.Add(inactive);
                }
                if (WordBinaryMetadata.IsFastSaved(wordDocument))
                {
                    findings.Add("удалённый текст в документе Word с быстрым сохранением");
                    state.WarnIncomplete("Документ Word сохранён в режиме быстрого сохранения: удалённый текст и старые правки " +
                                         "могут оставаться в файле. Пересохраните документ в Word с отключённым быстрым сохранением и очистите снова.");
                }
                var table = ReadStream(storage, tableName);
                var data = storage.ContainsEntry("Data") ? ReadStream(storage, "Data") : null;
                if (OleImages.HasMetadata(wordDocument) || OleImages.HasMetadata(table) || data != null && OleImages.HasMetadata(data))
                    residual.Add("метаданные изображений в документе Word (EXIF, GPS)");
                try
                {
                    var found = WordBinaryMetadata.Process(wordDocument, ref table, scrub, unremovable, residual);
                    findings.AddRange(found);
                    if (scrub && found.Count > 0)
                    {
                        replacements["WordDocument"] = wordDocument;
                        replacements[tableName] = table;
                    }
                    if (data != null) ProcessWordHyperlinkData(wordDocument, table, data, scrub, findings, residual, replacements);
                }
                catch (Exception ex) when (IsParseError(ex)) { problem = Describe(ex); }
            }
            if (problem != null) state.WarnIncomplete($"Служебные свойства Word не удалены: {problem}.");
        }

        if (storage.ContainsEntry("Workbook"))
        {
            var workbook = ReadStream(storage, "Workbook");
            var original = workbook.ToArray();
            string? problem;
            try
            {
                findings.AddRange(ExcelBinaryMetadata.Process(workbook, scrub, unremovable, residual, out problem));
            }
            catch (Exception ex) when (IsParseError(ex))
            {
                problem = Describe(ex);
                workbook = original;
            }
            // Edits are made in place and keep record lengths, so they stay valid even when a later record is damaged.
            if (scrub && !workbook.AsSpan().SequenceEqual(original))
                replacements["Workbook"] = workbook;
            if (problem != null) state.WarnIncomplete($"Имена пользователей и пути в книге Excel удалены не полностью: {problem}.");
            if (storage.ContainsEntry("User Names") || storage.ContainsEntry("Revision Log"))
                residual.Add("имена пользователей и журнал изменений общей книги Excel");
        }
        else if (storage.ContainsEntry("Book"))
        {
            findings.Add("книга Excel 5.0/95 (поток Book) не проверена");
            state.WarnIncomplete("Книга в формате Excel 5.0/95 (поток Book) не поддерживается: имена пользователей внутри книги не удалены. " +
                                 "Пересохраните её в формате Excel 97–2003 или XLSX и очистите снова.");
        }

        if (storage.ContainsEntry(OlePackageMetadata.StreamName))
        {
            var found = OlePackageMetadata.Process(ReadStream(storage, OlePackageMetadata.StreamName), scrub, out var scrubbed, out var problem);
            findings.AddRange(found);
            if (scrubbed != null) replacements[OlePackageMetadata.StreamName] = scrubbed;
            if (problem != null) state.WarnIncomplete($"Путь к вложенному файлу не удалён: {problem}.");
        }

        if (VbaProjectMetadata.IsProject(storage))
            ScrubVbaProject(storage, scrub, findings, residual, replacements, state);

        // Hidden text and sheets are content, not metadata: they are reported, never silently kept or deleted.
        findings.AddRange(unremovable);
        foreach (var item in unremovable) state.Warn($"Найден {item}: MetadataDel его не удаляет, проверьте файл перед отправкой.");
        // Metadata that stays in the file because it cannot be removed safely: the result is incomplete.
        findings.AddRange(residual);
        foreach (var item in residual) state.WarnIncomplete($"Не удалено: {item}. Проверьте файл перед отправкой.");
        return plan;
    }

    // Пути в данных гиперссылок (поток Data) находятся через форматирование символов: если его не разобрать,
    // остальная очистка Word всё равно применяется, а результат помечается неполным.
    private static void ProcessWordHyperlinkData(byte[] wordDocument, byte[] table, byte[] data, bool scrub, List<string> findings,
        List<string> residual, Dictionary<string, byte[]> replacements)
    {
        try
        {
            if (WordDocumentText.ProcessHyperlinkData(wordDocument, table, data, scrub) == 0) return;
            findings.Add("локальные пути в гиперссылках Word");
            if (scrub) replacements["Data"] = data;
        }
        catch (Exception ex) when (IsParseError(ex))
        {
            residual.Add("возможные локальные пути в гиперссылках Word (форматирование символов не разобрано)");
        }
    }

    // Macros are kept. PROJECT loses help-file paths; the compressed dir stream is only checked.
    private static void ScrubVbaProject(Storage storage, bool scrub, List<string> findings, List<string> residual,
        Dictionary<string, byte[]> replacements, ScrubState state)
    {
        findings.Add(MacrosFinding);
        state.Warn("Найдены макросы VBA: MetadataDel их не удаляет, а код и комментарии макросов могут содержать имена и пути, " +
                   "проверьте их перед отправкой.");
        var project = VbaProjectMetadata.ScrubProjectStream(ReadStream(storage, VbaProjectMetadata.ProjectStream));
        if (project != null)
        {
            findings.Add("путь к файлу справки проекта VBA");
            if (scrub) replacements[VbaProjectMetadata.ProjectStream] = project;
        }
        var vba = storage.OpenStorage(VbaProjectMetadata.VbaStorage);
        if (!vba.ContainsEntry("dir")) return;
        try
        {
            if (VbaProjectMetadata.DirHasLocalPaths(ReadStream(vba, "dir")))
                residual.Add("пути к файлам в ссылках и справке проекта VBA");
        }
        catch (Exception ex) when (IsParseError(ex))
        {
            residual.Add("ссылки проекта VBA (поток dir не разобран)");
        }
    }

    internal static byte[] ReadStream(Storage storage, string name)
    {
        using var input = storage.OpenStream(name);
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        return buffer.ToArray();
    }

    // Every storage drops its property streams and gets its own Word/Excel scrub: embedded objects are documents too.
    private static void CopyStorage(Storage source, Storage destination, ScrubState state, CancellationToken ct)
    {
        var plan = ScrubApplicationProperties(source, scrub: true, new List<string>(), state);
        destination.CLSID = source.CLSID;
        destination.StateBits = source.StateBits;
        foreach (var entry in source.EnumerateEntries())
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Name.StartsWith("\x05", StringComparison.Ordinal)) continue;
            if (entry.Type == EntryType.Storage)
                CopyStorage(source.OpenStorage(entry.Name), destination.CreateStorage(entry.Name), state, ct);
            else if (entry.Type == EntryType.Stream)
            {
                if (plan.Dropped.Contains(entry.Name)) continue;
                using var output = destination.CreateStream(entry.Name);
                if (plan.Replacements.TryGetValue(entry.Name, out var scrubbed))
                {
                    output.Write(scrubbed);
                    continue;
                }
                using var input = source.OpenStream(entry.Name);
                input.CopyTo(output);
            }
        }
        // OpenMcdf stamps new storages with the current time; the rebuilt file must not record when it was cleaned.
        // Set last: adding children updates the parent's modification time.
        destination.CreationTime = DateTime.FromFileTimeUtc(0);
        destination.ModifiedTime = DateTime.FromFileTimeUtc(0);
    }
}
