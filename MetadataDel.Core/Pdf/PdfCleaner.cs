using System.Text;
using MetadataDel.Core.Audit;
using MetadataDel.Core.Cleaning;
using MetadataDel.Core.Media;
using iText.Kernel.Pdf;
using iText.Kernel.Utils;
using iText.Forms;
using iText.Forms.Fields;

namespace MetadataDel.Core.Pdf;

/// <summary>
/// Очиститель метаданных PDF: пересобирает документ из страниц, поэтому старые версии и неиспользуемые объекты не переносятся.
/// </summary>
public sealed class PdfCleaner : IFileCleaner
{
    private const int MaxRetries = 12;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    internal static readonly PdfName PieceInfo = new("PieceInfo");
    internal static readonly PdfName LastModified = new("LastModified");
    internal static readonly PdfName Thumb = new("Thumb");
    internal static readonly PdfName Beads = new("B");
    internal static readonly PdfName Threads = new("Threads");
    internal static readonly PdfName SpiderInfo = new("SpiderInfo");
    internal static readonly PdfName Usage = new("Usage");

    /// <inheritdoc />
    public async Task<CleanResult> CleanAsync(string path, CleanOptions options, CancellationToken ct = default)
    {
        try
        {
            var warnings = new List<string>();

            ct.ThrowIfCancellationRequested();
            path = Path.GetFullPath(path);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Очистка символических ссылок не поддерживается. Укажите исходный файл.");
            if (options.Backup) FileCleaningTransaction.CreateBackup(path);

            // Читаем оригинал в память, обрабатываем, записываем результат обратно
            byte[] originalBytes = await ReadAllBytesWithRetryAsync(path, ct);

            using (var flattened = new MemoryStream())
            {
                using (var source = new PdfDocument(new PdfReader(new MemoryStream(originalBytes)), new PdfWriter(flattened)))
                {
                    var form = PdfAcroForm.GetAcroForm(source, false);
                    if (form != null)
                    {
                        form.FlattenFields();
                        if (PdfAcroForm.GetAcroForm(source, false)?.GetFormFields().Count > 0)
                            throw new InvalidDataException("Не удалось сохранить видимое содержимое PDF-формы.");
                    }
                }
                originalBytes = flattened.ToArray();
            }

            byte[] cleanedBytes;
            using (var inputStream = new MemoryStream(originalBytes))
            using (var outputStream = new MemoryStream())
            {
                using (var reader = new PdfReader(inputStream))
                using (var writer = new PdfWriter(outputStream, new WriterProperties()
                    .SetPdfVersion(PdfVersion.PDF_1_7)
                    .SetCompressionLevel(9)))
                using (var src = new PdfDocument(reader))
                using (var dst = new PdfDocument(writer))
                {
                    // XMP, PieceInfo и EXIF картинок убираются до копирования: удалённые из словарей объекты
                    // тогда не попадают в новый файл даже как неиспользуемые.
                    ScrubObjects(src);
                    var merger = new PdfMerger(dst);
                    merger.Merge(src, 1, src.GetNumberOfPages());

                    var info = dst.GetDocumentInfo();
                    info.SetTitle("");
                    info.SetAuthor("");
                    info.SetSubject("");
                    info.SetKeywords("");
                    info.SetCreator("");
                    info.SetProducer("");

                    RemoveEmbeddedFiles(dst);

                    var catalog = dst.GetCatalog();
                    catalog.Remove(PdfName.Metadata);
                    catalog.Remove(PdfName.OpenAction);
                    catalog.Remove(PdfName.AA);
                    catalog.Remove(PdfName.Outlines);
                    catalog.Remove(PdfName.StructTreeRoot);
                    catalog.Remove(PdfName.MarkInfo);
                    catalog.Remove(PdfName.EmbeddedFiles);
                    catalog.Remove(new PdfName("AF"));
                    catalog.Remove(PieceInfo);
                    catalog.Remove(Threads);
                    catalog.Remove(SpiderInfo);
                    catalog.Remove(PdfName.Collection);
                    catalog.Remove(PdfName.Perms);
                    catalog.Remove(new PdfName("Legal"));

                    for (int i = 1; i <= dst.GetNumberOfPages(); i++)
                    {
                        var page = dst.GetPage(i);
                        page.GetPdfObject().Remove(PdfName.Metadata);
                        page.GetPdfObject().Remove(PieceInfo);
                        page.GetPdfObject().Remove(LastModified);
                        page.GetPdfObject().Remove(Thumb);
                        page.GetPdfObject().Remove(Beads);
                        page.GetPdfObject().Remove(PdfName.AA);
                        page.GetPdfObject().Remove(new PdfName("AF"));
                        var anns = page.GetAnnotations();
                        for (int j = anns.Count - 1; j >= 0; j--)
                            page.RemoveAnnotation(anns[j]);
                    }

                    var acro = PdfAcroForm.GetAcroForm(dst, false);
                    if (acro != null)
                    {
                        try { acro.FlattenFields(); } catch { }
                        try { acro.SetNeedAppearances(false); } catch { }
                    }

                    AnonymizeLayers(dst);
                }
                cleanedBytes = outputStream.ToArray();
            }

            // iText (AGPL) при закрытии всегда пишет Producer, даты и служебный комментарий с версией.
            // Правки байтов не меняют длину, поэтому таблица xref остаётся верной.
            BlankInfoDictionary(cleanedBytes);
            BlankProducerComment(cleanedBytes);

            var finalPath = path;

            try
            {
                AddWarning(warnings, await PersistCleanedPdfAsync(path, path, cleanedBytes, options, ct));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var fallbackPath = GetFallbackCopyPath(path);
                try
                {
                    AddWarning(warnings, await PersistCleanedPdfAsync(fallbackPath, path, cleanedBytes, options, ct));
                    finalPath = fallbackPath;
                    warnings.Add($"Исходный PDF был занят другой программой, поэтому очищенная копия сохранена рядом: {fallbackPath}");
                }
                catch (Exception fallbackEx)
                {
                    throw new IOException(
                        $"Не удалось обновить исходный PDF и не удалось сохранить очищенную копию рядом. Исходная ошибка: {ex.Message}",
                        fallbackEx);
                }
            }

            return new CleanResult(finalPath, true, JoinWarnings(warnings));
        }
        catch (Exception ex)
        {
            return new CleanResult(path, false, ex.Message);
        }
	}

    private static async Task<string?> PersistCleanedPdfAsync(string path, string permissionSource, byte[] cleanedBytes, CleanOptions options, CancellationToken ct)
    {
        var warnings = new List<string>();

        var outputPath = path;
        var temporaryPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $".mdel-{Guid.NewGuid():N}.pdf");
        try
        {
        path = temporaryPath;
        await File.WriteAllBytesAsync(path, cleanedBytes, ct);

        // exiftool здесь не используется: для PDF он дописывает инкрементальное обновление,
        // и в файле остаётся предыдущая версия, а удалять после пересборки ему уже нечего.
        var audit = await TryAuditWithRetryAsync(path, ct);
        if (audit?.HasSensitiveMetadata == true)
        {
            warnings.Add("После очистки остались признаки метаданных: " + audit.Summarize());
        }

        ct.ThrowIfCancellationRequested();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(outputPath))
                    using (new FileStream(outputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                FileCleaningTransaction.Publish(temporaryPath, outputPath, permissionSource);
                break;
            }
            catch (IOException) when (attempt < MaxRetries) { await Task.Delay(RetryDelay, ct); }
        }
        AddWarning(warnings, FileCleaningTransaction.WipeTimestamps(outputPath, options.WipeFsTimestamps));
        return JoinWarnings(warnings);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    /// <summary>
    /// Чтение файла с retry и FileShare.ReadWrite — для сетевых дисков,
    /// где Explorer/антивирус/индексатор могут кратковременно блокировать файл.
    /// </summary>
    private static async Task<byte[]> ReadAllBytesWithRetryAsync(string path, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var bytes = new byte[fs.Length];
                await fs.ReadExactlyAsync(bytes, ct);
                return bytes;
            }
            catch (IOException) when (attempt < MaxRetries)
            {
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    /// <summary>
    /// Аудит — проверочный шаг. Если файл кратковременно блокируется внешним процессом,
    /// несколько раз пробуем снова и затем тихо пропускаем аудит вместо провала очистки.
    /// </summary>
    private static async Task<MetadataAuditResult?> TryAuditWithRetryAsync(string path, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return MetadataAuditService.Audit(path);
            }
            catch (IOException) when (attempt < MaxRetries)
            {
                await Task.Delay(RetryDelay, ct);
            }
            catch
            {
                return null;
            }
        }
    }

    private static string GetFallbackCopyPath(string originalPath)
    {
        var directory = Path.GetDirectoryName(originalPath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(originalPath);
        var extension = Path.GetExtension(originalPath);

        var firstCandidate = Path.Combine(directory, fileName + ".MetadataDel.cleaned" + extension);
        if (!File.Exists(firstCandidate))
        {
            return firstCandidate;
        }

        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(directory, $"{fileName}.MetadataDel.cleaned ({index}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Убирает метаданные из объектов исходного документа до копирования страниц.</summary>
    internal static void ScrubObjects(PdfDocument document)
    {
        for (var number = 1; number < document.GetNumberOfPdfObjects(); number++)
        {
            if (document.GetPdfObject(number) is not PdfDictionary dictionary) continue;
            dictionary.Remove(PdfName.Metadata);
            dictionary.Remove(PieceInfo);
            dictionary.Remove(LastModified);
            if (PdfName.Page.Equals(dictionary.GetAsName(PdfName.Type)))
            {
                dictionary.Remove(Thumb);
                dictionary.Remove(Beads);
            }
            if (dictionary is PdfStream stream && PdfName.Image.Equals(stream.GetAsName(PdfName.Subtype)))
                StripImageMetadata(stream);
        }
    }

    /// <summary>JPEG и JPEG 2000 хранятся в PDF целиком, вместе с EXIF (GPS, камера) и XMP.</summary>
    private static void StripImageMetadata(PdfStream stream)
    {
        var filter = GetSingleFilter(stream);
        if (filter == null || !filter.Equals(PdfName.DCTDecode) && !filter.Equals(PdfName.JPXDecode)) return;
        var stripped = ImageMetadata.Strip(stream.GetBytes(false));
        if (stripped == null) return;
        var decodeParms = stream.Get(PdfName.DecodeParms);
        // SetData считает данные раскодированными и убирает фильтр — возвращаем его, чтобы байты не пережимались.
        stream.SetData(stripped);
        stream.Put(PdfName.Filter, filter);
        if (decodeParms != null) stream.Put(PdfName.DecodeParms, decodeParms);
    }

    internal static PdfName? GetSingleFilter(PdfStream stream) => stream.Get(PdfName.Filter) switch
    {
        PdfName name => name,
        PdfArray array when array.Size() == 1 => array.GetAsName(0),
        _ => null
    };

    /// <summary>Имена слоёв и сведения о создавшем их приложении заменяются нейтральными.</summary>
    private static void AnonymizeLayers(PdfDocument document)
    {
        var properties = document.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.OCProperties);
        if (properties == null) return;
        var groups = properties.GetAsArray(PdfName.OCGs);
        for (var i = 0; i < (groups?.Size() ?? 0); i++)
        {
            var group = groups!.GetAsDictionary(i);
            if (group == null) continue;
            group.Put(PdfName.Name, new PdfString($"Layer {i + 1}"));
            group.Remove(Usage);
        }
        var configurations = new List<PdfDictionary?> { properties.GetAsDictionary(PdfName.D) };
        var extra = properties.GetAsArray(PdfName.Configs);
        for (var i = 0; i < (extra?.Size() ?? 0); i++) configurations.Add(extra!.GetAsDictionary(i));
        foreach (var configuration in configurations.OfType<PdfDictionary>())
        {
            configuration.Remove(PdfName.Name);
            configuration.Remove(PdfName.Creator);
        }
    }

    /// <summary>Заменяет содержимое Info-словаря пробелами: остаётся пустой словарь &lt;&lt; &gt;&gt;.</summary>
    private static void BlankInfoDictionary(byte[] pdf)
    {
        // Правим только объект, на который указывает xref: поиск по всему файлу задел бы содержимое страниц.
        using var inspection = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        var reference = inspection.GetTrailer().GetAsDictionary(PdfName.Info)?.GetIndirectReference();
        if (reference == null) return;
        var offset = checked((int)reference.GetOffset());
        var text = Encoding.Latin1.GetString(pdf, offset, Math.Min(pdf.Length - offset, 1 << 20));
        var end = text.IndexOf("endobj", StringComparison.Ordinal);
        if (end < 0) throw new InvalidDataException("Не найден конец Info-объекта PDF.");
        var open = text.IndexOf("<<", StringComparison.Ordinal);
        var close = text.LastIndexOf(">>", end, StringComparison.Ordinal);
        if (open < 0 || close <= open) throw new InvalidDataException("Не найден словарь Info-объекта PDF.");
        pdf.AsSpan(offset + open + 2, close - open - 2).Fill((byte)' ');
    }

    private static void BlankProducerComment(byte[] pdf)
    {
        var index = pdf.AsSpan().LastIndexOf("%iText"u8);
        if (index < 0) return;
        for (var i = index + 1; i < pdf.Length && pdf[i] is not ((byte)'\r' or (byte)'\n'); i++)
            pdf[i] = (byte)' ';
    }

    private static void AddWarning(ICollection<string> warnings, string? warning)
    {
        if (!string.IsNullOrWhiteSpace(warning))
        {
            warnings.Add(warning);
        }
    }

    private static string? JoinWarnings(IReadOnlyCollection<string> warnings)
    {
        if (warnings.Count == 0)
        {
            return null;
        }

        return string.Join(" ", warnings.Where(w => !string.IsNullOrWhiteSpace(w)));
    }

    private static void RemoveEmbeddedFiles(PdfDocument doc)
    {
        var catalog = doc.GetCatalog();
        var namesDict = catalog.GetPdfObject().GetAsDictionary(PdfName.Names);
        if (namesDict == null)
        {
            return;
        }

        var embedded = namesDict.GetAsDictionary(PdfName.EmbeddedFiles);
        if (embedded != null)
        {
            RemoveEmbeddedFilesTree(embedded);
            namesDict.Remove(PdfName.EmbeddedFiles);
        }

        if (namesDict.IsEmpty())
        {
            catalog.GetPdfObject().Remove(PdfName.Names);
        }
    }

    private static void RemoveEmbeddedFilesTree(PdfDictionary tree)
    {
        if (tree == null)
        {
            return;
        }

        var names = tree.GetAsArray(PdfName.Names);
        if (names != null)
        {
            for (int i = 1; i < names.Size(); i += 2)
            {
                var fileSpec = names.GetAsDictionary(i);
                RemoveFileSpec(fileSpec);
            }
            names.Clear();
        }

        var kids = tree.GetAsArray(PdfName.Kids);
        if (kids != null)
        {
            for (int i = 0; i < kids.Size(); i++)
            {
                var kid = kids.GetAsDictionary(i);
                if (kid != null)
                {
                    RemoveEmbeddedFilesTree(kid);
                }
            }
            kids.Clear();
        }

        tree.Clear();
    }

    private static void RemoveFileSpec(PdfDictionary? fileSpec)
    {
        if (fileSpec == null)
        {
            return;
        }

        var ef = fileSpec.GetAsDictionary(PdfName.EF);
        if (ef != null)
        {
            ef.Clear();
        }

        fileSpec.Remove(PdfName.EF);
        fileSpec.Remove(PdfName.F);
        fileSpec.Remove(PdfName.UF);
        fileSpec.Remove(new PdfName("DOS"));
        fileSpec.Remove(new PdfName("Mac"));
        fileSpec.Remove(new PdfName("Unix"));
        fileSpec.Clear();
    }
}
