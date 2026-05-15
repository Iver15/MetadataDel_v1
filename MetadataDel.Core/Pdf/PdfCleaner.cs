using System.Diagnostics;
using MetadataDel.Core.Audit;
using MetadataDel.Core.Cleaning;
using iText.Kernel.Pdf;
using iText.Kernel.Utils;
using iText.Forms;
using iText.Forms.Fields;

namespace MetadataDel.Core.Pdf;

/// <summary>
/// Очиститель метаданных PDF с пересборкой документа и опциональной агрессивной очисткой через exiftool.
/// </summary>
public sealed class PdfCleaner : IFileCleaner
{
    private const int MaxRetries = 12;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    /// <inheritdoc />
    public async Task<CleanResult> CleanAsync(string path, CleanOptions options, CancellationToken ct = default)
    {
        try
        {
            var warnings = new List<string>();

            // Резервная копия до изменения
            if (options.Backup)
            {
                try
                {
                    File.Copy(path, path + ".bak", overwrite: true);
                }
                catch
                {
                    // Не критично: продолжаем очистку даже если резерв не создан
                }
            }

            // Читаем оригинал в память, обрабатываем, записываем результат обратно
            byte[] originalBytes = await ReadAllBytesWithRetryAsync(path, ct);

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
                    catalog.Remove(new PdfName("PieceInfo"));

                    for (int i = 1; i <= dst.GetNumberOfPages(); i++)
                    {
                        var page = dst.GetPage(i);
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
                }
                cleanedBytes = outputStream.ToArray();
            }

            // iText AGPL принудительно вписывает Producer при Close().
            // Патчим байты в памяти до записи на диск — заменяем содержимое
            // строки Producer пробелами той же длины, чтобы не сломать xref.
            BlankPdfStringValue(cleanedBytes, "/Producer");

            var finalPath = path;

            try
            {
                AddWarning(warnings, await PersistCleanedPdfAsync(path, cleanedBytes, options, ct));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var fallbackPath = GetFallbackCopyPath(path);
                try
                {
                    AddWarning(warnings, await PersistCleanedPdfAsync(fallbackPath, cleanedBytes, options, ct));
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

    private static async Task<string?> PersistCleanedPdfAsync(string path, byte[] cleanedBytes, CleanOptions options, CancellationToken ct)
    {
        var warnings = new List<string>();

        await WriteAllBytesWithRetryAsync(path, cleanedBytes, ct);
        await WaitForExclusiveAccessAsync(path, ct);

        // exiftool всегда пытается дочистить (ICC, Trailer и пр.)
        // Producer уже удалён патчингом байтов, exiftool опционален
        var exiftool = await RunExiftoolCleanupAsync(path, aggressive: true, ct);
        if (!exiftool.Success && options.AggressivePdf)
        {
            // Предупреждаем только если пользователь явно просил aggressive
            warnings.Add($"exiftool-очистка не удалась: {exiftool.Message}");
        }

        // Дополнительно очищаем метаданные файловой системы
        if (options.WipeFsTimestamps)
        {
            try
            {
                var ts = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                File.SetCreationTimeUtc(path, ts);
                File.SetLastWriteTimeUtc(path, ts);
                File.SetLastAccessTimeUtc(path, ts);
            }
            catch { /* ignore */ }
        }

        var audit = await TryAuditWithRetryAsync(path, ct);
        if (audit?.HasSensitiveMetadata == true)
        {
            warnings.Add("После очистки остались признаки метаданных: " + audit.Summarize());
        }

        return JoinWarnings(warnings);
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
    /// Запись файла с retry — для сетевых дисков с транзиентными блокировками.
    /// </summary>
    private static async Task WriteAllBytesWithRetryAsync(string path, byte[] data, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                await fs.WriteAsync(data, ct);
                return;
            }
            catch (IOException) when (attempt < MaxRetries)
            {
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    /// <summary>
    /// Дожидается, пока файл можно будет снова открыть эксклюзивно.
    /// Это снижает число ложных ошибок на сетевых дисках после записи.
    /// </summary>
    private static async Task WaitForExclusiveAccessAsync(string path, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return;
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

    private static async Task<CleanResult> RunExiftoolCleanupAsync(string path, bool aggressive, CancellationToken ct)
    {
        try
        {
            var exiftoolPath = ResolveExiftoolPath();
            if (exiftoolPath == null)
            {
                return new CleanResult(path, false, "exiftool не найден. Установите exiftool или добавьте tools/win/exiftool.exe.");
            }

            var arguments = aggressive
                ? "-overwrite_original -all= -Trailer:all= -ICC_Profile:all= "
                : "-overwrite_original -all= ";

            for (int attempt = 0; ; attempt++)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exiftoolPath,
                    Arguments = arguments + "\"" + path + "\"",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    return new CleanResult(path, false, "Не удалось запустить exiftool для очистки PDF.");
                }

                await process.WaitForExitAsync(ct);
                if (process.ExitCode == 0)
                {
                    return new CleanResult(path, true);
                }

                var err = (await process.StandardError.ReadToEndAsync()).Trim();
                if (attempt < MaxRetries && IsFileLockError(err))
                {
                    await Task.Delay(RetryDelay, ct);
                    continue;
                }

                return new CleanResult(path, false, $"exiftool завершился с кодом {process.ExitCode}: {err}");
            }
        }
        catch (OperationCanceledException)
        {
            return new CleanResult(path, false, "Очистка PDF через exiftool отменена.");
        }
        catch (Exception ex)
        {
            return new CleanResult(path, false, $"Не удалось выполнить exiftool-очистку PDF: {ex.Message}");
        }
    }

    private static string? ResolveExiftoolPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var processPath = Environment.ProcessPath;
            var processDirectory = string.IsNullOrWhiteSpace(processPath)
                ? null
                : Path.GetDirectoryName(processPath);
            if (!string.IsNullOrWhiteSpace(processDirectory))
            {
                var bundled = Path.Combine(processDirectory, "tools", "win", "exiftool.exe");
                if (File.Exists(bundled))
                {
                    return bundled;
                }
            }
        }

        var env = Environment.GetEnvironmentVariable("EXIFTOOL_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        return "exiftool";
    }

    private static bool IsFileLockError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var text = message.ToLowerInvariant();
        return text.Contains("used by another process")
            || text.Contains("being used by another process")
            || text.Contains("process cannot access the file")
            || text.Contains("file is locked")
            || text.Contains("permission denied");
    }

    /// <summary>
    /// Находит PDF-ключ (например /Producer) и затирает значение-строку пробелами.
    /// Длина не меняется → xref-смещения остаются валидными.
    /// </summary>
    private static void BlankPdfStringValue(byte[] pdf, string key)
    {
        var keyBytes = System.Text.Encoding.ASCII.GetBytes(key);

        for (int pos = 0; pos <= pdf.Length - keyBytes.Length; pos++)
        {
            if (!MatchesAt(pdf, pos, keyBytes))
                continue;

            // Нашли ключ — ищем открывающую скобку '(' после него
            int i = pos + keyBytes.Length;
            while (i < pdf.Length && pdf[i] != (byte)'(' && pdf[i] != (byte)'/')
                i++;

            if (i >= pdf.Length || pdf[i] != (byte)'(')
                continue;

            // Затираем содержимое между ( и ) с учётом PDF-экранирования
            i++; // пропускаем '('
            int depth = 1;
            while (i < pdf.Length && depth > 0)
            {
                if (pdf[i] == (byte)'\\')
                {
                    pdf[i] = (byte)' ';
                    i++;
                    if (i < pdf.Length)
                        pdf[i] = (byte)' ';
                }
                else if (pdf[i] == (byte)'(')
                {
                    depth++;
                    pdf[i] = (byte)' ';
                }
                else if (pdf[i] == (byte)')')
                {
                    depth--;
                    if (depth > 0)
                        pdf[i] = (byte)' ';
                }
                else
                {
                    pdf[i] = (byte)' ';
                }

                if (depth > 0) i++;
            }
        }
    }

    private static bool MatchesAt(byte[] data, int offset, byte[] pattern)
    {
        if (offset + pattern.Length > data.Length)
            return false;
        for (int i = 0; i < pattern.Length; i++)
        {
            if (data[offset + i] != pattern[i])
                return false;
        }
        return true;
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
