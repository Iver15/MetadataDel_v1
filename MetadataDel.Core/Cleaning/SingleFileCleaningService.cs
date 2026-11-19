namespace MetadataDel.Core.Cleaning;

/// <summary>Result of one file plus the private backup copy made before cleaning, if any.</summary>
public sealed record SingleFileCleaningOutcome(CleanResult Result, string? BackupPath);

/// <summary>One-file adapter shared by the windows and the command line; document transformations remain in the cleaners.</summary>
public sealed class SingleFileCleaningService
{
    /// <summary>Supported document extensions (lower case, with dot).</summary>
    public static readonly IReadOnlyList<string> SupportedExtensions = [".pdf", ".docx", ".xlsx", ".doc", ".xls"];

    private readonly Func<string, IFileCleaner?> _resolve;

    /// <summary>Uses the built-in cleaners.</summary>
    public SingleFileCleaningService() : this(DefaultCleaner) { }

    internal SingleFileCleaningService(Func<string, IFileCleaner?> resolve) => _resolve = resolve;

    private static IFileCleaner? DefaultCleaner(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => new Pdf.PdfCleaner(),
        ".docx" => new Word.DocxCleaner(),
        ".xlsx" => new Excel.ExcelCleaner(),
        ".doc" or ".xls" => new Ole.OleDocumentCleaner(),
        _ => null
    };

    /// <summary>Processes one regular supported file, preserving the cleaner's output path, warnings and Incomplete flag.</summary>
    public async Task<CleanResult> CleanAsync(string path, CleanOptions options, CancellationToken ct = default) =>
        (await CleanWithBackupAsync(path, options, ct).ConfigureAwait(false)).Result;

    /// <summary>
    /// Same as <see cref="CleanAsync"/>, but reports where the original was saved. The backup goes to the private
    /// per-user backups folder, never next to the document.
    /// </summary>
    public async Task<SingleFileCleaningOutcome> CleanWithBackupAsync(string path, CleanOptions options, CancellationToken ct = default)
    {
        string? backup = null;
        try
        {
            path = Path.GetFullPath(path);
            if (Directory.Exists(path)) return new(new(path, false, "Для папок используйте очистку правым кликом."), null);
            if (!File.Exists(path)) return new(new(path, false, "Файл не найден."), null);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return new(new(path, false, "Символические ссылки не обрабатываются."), null);
            var cleaner = _resolve(path);
            if (cleaner == null) return new(new(path, false, "Поддерживаются PDF, DOCX, XLSX, DOC и XLS."), null);
            ct.ThrowIfCancellationRequested();
            if (options.Backup)
            {
                backup = FileCleaningTransaction.CreateBackup(path);
                options = options with { Backup = false };
            }
            var result = await cleaner.CleanAsync(path, options, ct).ConfigureAwait(false);
            // "with" keeps Incomplete: a partially cleaned file must never be reported as fully cleaned.
            return new(result with { Message = TranslateError(result.Message, ct) }, backup);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(new(path, false, TranslateError(ex.Message, ct)), backup);
        }
    }

    private static string? TranslateError(string? message, CancellationToken ct) => message switch
    {
        "File contains corrupted data." => "Файл повреждён или имеет неверный формат.",
        "The operation was canceled." or "A task was canceled." when ct.IsCancellationRequested => "Очистка прервана. Исходный файл не изменён.",
        _ => message
    };
}
