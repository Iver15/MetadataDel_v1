using OpenMcdf;
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
            var warnings = new List<string?>();
            var rebuilt = transaction.WorkingPath + ".rebuilt";
            try
            {
                // Copy live entries into a fresh container; free sectors must not retain deleted properties.
                using (var source = RootStorage.OpenRead(transaction.WorkingPath))
                using (var output = File.Create(rebuilt))
                    Rebuild(source, output, warnings, ct);
                File.Move(rebuilt, transaction.WorkingPath, overwrite: true);
                transaction.Commit(ct);
            }
            finally { if (File.Exists(rebuilt)) File.Delete(rebuilt); }
            warnings.Add(FileCleaningTransaction.WipeTimestamps(path, options.WipeFsTimestamps));
            var message = string.Join(" ", warnings.OfType<string>().Distinct());
            return Task.FromResult(new CleanResult(path, true, message.Length == 0 ? null : message));
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
    internal static byte[] ScrubCompoundFile(byte[] compoundFile, List<string> warnings, CancellationToken ct = default)
    {
        var collected = new List<string?>();
        using var input = new MemoryStream(compoundFile, writable: false);
        using var output = new MemoryStream();
        using (var source = RootStorage.Open(input, StorageModeFlags.LeaveOpen))
            Rebuild(source, output, collected, ct);
        warnings.AddRange(collected.OfType<string>().Distinct());
        return output.ToArray();
    }

    /// <summary>Перечисляет оставшиеся свойства во всех хранилищах контейнера, ничего не меняя.</summary>
    internal static List<(string Code, string Description)> Inspect(Storage root)
    {
        var findings = new List<(string Code, string Description)>();
        InspectStorage(root, string.Empty, findings);
        return findings;
    }

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
        ScrubApplicationProperties(storage, scrub: false, described, new List<string?>());
        findings.AddRange(described.Select(d => ("ole.internal", location.Length == 0 ? d : $"{d} ({location.TrimEnd('/')})")));
    }

    private static void Rebuild(RootStorage source, Stream output, List<string?> warnings, CancellationToken ct)
    {
        source.BaseStream.Position = 26;
        var version = (OpenMcdf.Version)source.BaseStream.ReadByte();
        using var destination = RootStorage.Create(output, version, StorageModeFlags.LeaveOpen);
        CopyStorage(source, destination, warnings, ct);
    }

    // Word, Excel and OLE Package keep personal data inside their own streams; returns scrubbed copies of those streams.
    private static Dictionary<string, byte[]> ScrubApplicationProperties(Storage storage, bool scrub, List<string> findings, List<string?> warnings)
    {
        var replacements = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var unremovable = new List<string>();
        if (storage.ContainsEntry("WordDocument"))
        {
            var wordDocument = ReadStream(storage, "WordDocument");
            var tableName = WordBinaryMetadata.GetTableStreamName(wordDocument, out var problem);
            if (tableName != null && !storage.ContainsEntry(tableName)) problem = "не найден поток таблиц Word";
            else if (tableName != null)
            {
                var table = ReadStream(storage, tableName);
                try
                {
                    var found = WordBinaryMetadata.Process(wordDocument, ref table, scrub, unremovable);
                    findings.AddRange(found);
                    if (scrub && found.Count > 0)
                    {
                        replacements["WordDocument"] = wordDocument;
                        replacements[tableName] = table;
                    }
                    if (storage.ContainsEntry("Data"))
                    {
                        var data = ReadStream(storage, "Data");
                        if (WordDocumentText.ProcessHyperlinkData(wordDocument, table, data, scrub) > 0)
                        {
                            findings.Add("локальные пути в гиперссылках Word");
                            if (scrub) replacements["Data"] = data;
                        }
                    }
                }
                catch (InvalidDataException ex) { problem = ex.Message; }
            }
            if (problem != null) warnings.Add($"Служебные свойства Word не удалены: {problem}.");
        }

        if (storage.ContainsEntry("Workbook"))
        {
            var workbook = ReadStream(storage, "Workbook");
            var found = ExcelBinaryMetadata.Process(workbook, scrub, unremovable, out var problem);
            findings.AddRange(found);
            if (scrub && found.Count > 0 && problem == null)
                replacements["Workbook"] = workbook;
            if (problem != null) warnings.Add($"Имена пользователей в книге Excel не удалены: {problem}.");
        }

        if (storage.ContainsEntry(OlePackageMetadata.StreamName))
        {
            var found = OlePackageMetadata.Process(ReadStream(storage, OlePackageMetadata.StreamName), scrub, out var scrubbed, out var problem);
            findings.AddRange(found);
            if (scrubbed != null) replacements[OlePackageMetadata.StreamName] = scrubbed;
            if (problem != null) warnings.Add($"Путь к вложенному файлу не удалён: {problem}.");
        }

        // Hidden text and sheets are content, not metadata: they are reported, never silently kept or deleted.
        findings.AddRange(unremovable);
        warnings.AddRange(unremovable.Select(item => $"Найден {item}: MetadataDel его не удаляет, проверьте файл перед отправкой."));
        return replacements;
    }

    internal static byte[] ReadStream(Storage storage, string name)
    {
        using var input = storage.OpenStream(name);
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        return buffer.ToArray();
    }

    // Every storage drops its property streams and gets its own Word/Excel scrub: embedded objects are documents too.
    private static void CopyStorage(Storage source, Storage destination, List<string?> warnings, CancellationToken ct)
    {
        var replacements = ScrubApplicationProperties(source, scrub: true, new List<string>(), warnings);
        destination.CLSID = source.CLSID;
        destination.StateBits = source.StateBits;
        foreach (var entry in source.EnumerateEntries())
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Name.StartsWith("\x05", StringComparison.Ordinal)) continue;
            if (entry.Type == EntryType.Storage)
                CopyStorage(source.OpenStorage(entry.Name), destination.CreateStorage(entry.Name), warnings, ct);
            else if (entry.Type == EntryType.Stream)
            {
                using var output = destination.CreateStream(entry.Name);
                if (replacements.TryGetValue(entry.Name, out var scrubbed))
                {
                    output.Write(scrubbed);
                    continue;
                }
                using var input = source.OpenStream(entry.Name);
                input.CopyTo(output);
            }
        }
    }
}
