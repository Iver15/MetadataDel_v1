using System.Text.RegularExpressions;

namespace MetadataDel.Core.CommandLine;

/// <summary>
/// Private per-user folders of MetadataDel: backups of originals, logs and error reports.
/// Nothing that contains original metadata or document paths is written next to the user's documents.
/// </summary>
public static partial class AppDataPaths
{
    private const string AppName = "MetadataDel";
    private static readonly AsyncLocal<string?> FlowRoot = new();
    private static string? _processRoot;
    private static string? Override => FlowRoot.Value ?? Volatile.Read(ref _processRoot);

    /// <summary>Settings and backups: macOS ~/Library/Application Support/MetadataDel, Windows %LocalAppData%\MetadataDel.</summary>
    public static string DataDirectory => Override ?? DefaultDataDirectory();

    /// <summary>Originals saved before cleaning; never deleted silently by uninstall.</summary>
    public static string BackupsDirectory => Path.Combine(DataDirectory, "Backups");

    /// <summary>macOS ~/Library/Logs/MetadataDel (shared with the Finder action), Windows %LocalAppData%\MetadataDel\logs.</summary>
    public static string LogsDirectory => Override is { } root ? Path.Combine(root, "logs") : DefaultLogsDirectory();

    /// <summary>Redirects all folders for the current async flow (tests only).</summary>
    internal static IDisposable OverrideRootForTests(string root)
    {
        var previous = FlowRoot.Value;
        FlowRoot.Value = root;
        return new Restore(() => FlowRoot.Value = previous);
    }

    /// <summary>Redirects all folders of the process (test assembly initializer), so tests never touch the real profile.</summary>
    internal static void SetProcessRootForTests(string root) => Volatile.Write(ref _processRoot, root);

    /// <summary>Creates a folder readable only by the current user on Unix.</summary>
    internal static string EnsurePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>Daily log file of the command line tool.</summary>
    public static string DailyLogPath(DateTime utcNow) => Path.Combine(LogsDirectory, utcNow.ToString("yyyy-MM-dd") + ".log");

    /// <summary>
    /// Deletes this application's own log and error-report files older than <paramref name="maxAge"/>.
    /// Only names produced by MetadataDel are touched; foreign files in the folder are left alone.
    /// </summary>
    public static int DeleteExpiredLogs(TimeSpan maxAge, DateTime? utcNow = null, string? directory = null)
    {
        directory ??= LogsDirectory;
        var threshold = (utcNow ?? DateTime.UtcNow) - maxAge;
        var deleted = 0;
        try
        {
            if (!Directory.Exists(directory)) return 0;
            var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in Directory.EnumerateFiles(directory, "*", options))
            {
                if (!OwnLogName().IsMatch(Path.GetFileName(file))) continue;
                try
                {
                    if (File.GetLastWriteTimeUtc(file) >= threshold) continue;
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return deleted;
    }

    /// <summary>Folders used by earlier versions on macOS; expired files there are removed too.</summary>
    public static IEnumerable<string> LegacyLogDirectories()
    {
        if (!OperatingSystem.IsMacOS()) yield break;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, "Library", "Application Support", AppName, "logs");
        yield return Path.Combine(home, ".local", "share", AppName, "logs");
    }

    // yyyy-MM-dd.log, finder-action[-yyyy-MM-dd].log, error reports "<stamp>-<id>-<name>.MetadataDel-ошибка.txt".
    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2}\.log|finder-action(-\d{4}-\d{2}-\d{2})?\.log|\d{8}-\d{6}-[0-9a-f]{8}-.+\.MetadataDel-ошибка\.txt)$", RegexOptions.CultureInvariant)]
    private static partial Regex OwnLogName();

    private static string DefaultDataDirectory()
    {
        if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", AppName);
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return Path.Combine(string.IsNullOrWhiteSpace(xdg) || !Path.IsPathRooted(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
            : xdg, AppName);
    }

    private static string DefaultLogsDirectory() => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", AppName)
        : Path.Combine(DefaultDataDirectory(), "logs");

    private sealed class Restore(Action action) : IDisposable
    {
        private Action? _action = action;
        public void Dispose() { _action?.Invoke(); _action = null; }
    }
}
