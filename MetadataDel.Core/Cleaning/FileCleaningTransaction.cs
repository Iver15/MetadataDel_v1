namespace MetadataDel.Core.Cleaning;

// Keep every destructive step on a sibling copy; publish only a complete result.
internal sealed class FileCleaningTransaction : IDisposable
{
    private readonly string _path;
    public string WorkingPath { get; }

    public FileCleaningTransaction(string path, CleanOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _path = Path.GetFullPath(path);
        if ((File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Очистка символических ссылок не поддерживается. Укажите исходный файл.");
        if (options.Backup) CreateBackup(_path);
        WorkingPath = Path.Combine(Path.GetDirectoryName(_path)!,
            $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp{Path.GetExtension(_path)}");
        try { File.Copy(_path, WorkingPath); }
        catch { Dispose(); throw; }
    }

    internal static void CreateBackup(string path)
    {
        // Never replace the only surviving copy of an earlier original.
        var backup = path + ".bak";
        for (var index = 2; File.Exists(backup); index++)
            backup = path + $".{index}.bak";
        File.Copy(path, backup, overwrite: false);
    }

    public void Commit(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Respect existing readers/locks before replacing the directory entry.
        using (new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        File.Move(WorkingPath, _path, overwrite: true);
    }

    public void Dispose()
    {
        if (WorkingPath != null && File.Exists(WorkingPath)) File.Delete(WorkingPath);
    }

    internal static string? WipeTimestamps(string path, bool enabled)
    {
        if (!enabled) return null;
        try
        {
            var timestamp = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetCreationTimeUtc(path, timestamp);
            File.SetLastWriteTimeUtc(path, timestamp);
            File.SetLastAccessTimeUtc(path, timestamp);
            return null;
        }
        catch (Exception ex) { return "Не удалось сбросить даты файла: " + ex.Message; }
    }
}
