namespace MetadataDel.Core.Cleaning;

/// <summary>One-file desktop adapter; document transformations remain in the existing cleaners.</summary>
public sealed class SingleFileCleaningService
{
    /// <summary>Processes one regular supported file, preserving the cleaner's output path and warnings.</summary>
    public async Task<CleanResult> CleanAsync(string path, CleanOptions options, CancellationToken ct = default)
    {
        try
        {
            path = Path.GetFullPath(path);
            if (Directory.Exists(path)) return new(path, false, "Для папок используйте очистку правым кликом.");
            if (!File.Exists(path)) return new(path, false, "Файл не найден.");
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return new(path, false, "Символические ссылки не обрабатываются.");
            IFileCleaner? cleaner = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".pdf" => new Pdf.PdfCleaner(),
                ".docx" => new Word.DocxCleaner(),
                ".xlsx" => new Excel.ExcelCleaner(),
                ".doc" or ".xls" => new Ole.OleDocumentCleaner(),
                _ => null
            };
            if (cleaner == null) return new(path, false, "Поддерживаются PDF, DOCX, XLSX, DOC и XLS.");
            var result = await cleaner.CleanAsync(path, options, ct).ConfigureAwait(false);
            return result with { Message = TranslateError(result.Message) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(path, false, TranslateError(ex.Message));
        }
    }

    private static string? TranslateError(string? message) => message == "File contains corrupted data."
        ? "Файл повреждён или имеет неверный формат." : message;
}
