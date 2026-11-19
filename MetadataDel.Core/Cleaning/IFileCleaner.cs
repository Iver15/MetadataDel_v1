namespace MetadataDel.Core.Cleaning;

/// <summary>
/// Контракт очистителя метаданных для конкретного типа файла.
/// </summary>
public interface IFileCleaner
{
	/// <summary>
	/// Очищает метаданные файла по указанному пути.
	/// </summary>
	Task<CleanResult> CleanAsync(string path, CleanOptions options, CancellationToken ct = default);
}

/// <summary>
/// Опции очистки метаданных.
/// </summary>
public sealed record CleanOptions(bool Backup = false, bool AggressivePdf = false, bool WipeFsTimestamps = false);

/// <summary>
/// Результат очистки конкретного файла.
/// </summary>
public sealed record CleanResult(string Path, bool Success, string? Message = null)
{
    /// <summary>
    /// Файл записан, но часть метаданных удалить не удалось (зашифрованный или повреждённый фрагмент,
    /// неподдерживаемая структура, остатки после проверки). Такой результат нельзя показывать как полную очистку.
    /// </summary>
    public bool Incomplete { get; init; }
}
