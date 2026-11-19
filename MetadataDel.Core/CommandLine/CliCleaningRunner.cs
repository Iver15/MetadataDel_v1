using MetadataDel.Core.Cleaning;

namespace MetadataDel.Core.CommandLine;

/// <summary>Counts of one command line run and the resulting process exit code.</summary>
public sealed record CliBatchSummary(int Total, int Cleaned, int Partial, int Failed, int Cancelled)
{
    /// <summary>Exit code when the run was interrupted by Ctrl+C or SIGTERM.</summary>
    public const int InterruptedExitCode = 130;

    /// <summary>
    /// 0 — every file fully cleaned; 1 — some files were not fully cleaned or failed; 2 — nothing was cleaned;
    /// 130 — interrupted.
    /// </summary>
    public int ExitCode => Cancelled > 0 ? InterruptedExitCode
        : Failed == 0 && Partial == 0 ? 0
        : Cleaned + Partial > 0 ? 1 : 2;

    /// <summary>One-line Russian summary.</summary>
    public string Describe()
    {
        var parts = new List<string> { $"Итог: полностью очищено {Cleaned} из {Total}" };
        if (Partial > 0) parts.Add($"очищено не полностью: {Partial}");
        if (Failed > 0) parts.Add($"с ошибкой: {Failed}");
        if (Cancelled > 0) parts.Add($"прервано: {Cancelled}");
        return string.Join(", ", parts) + ".";
    }
}

/// <summary>
/// The batch loop shared by the Windows and macOS command line tools. Output lines: [OK], [WARN], [PARTIAL]
/// (written, but metadata remains), [FAIL], [STOP] (interrupted), [BACKUP] (where the original was saved).
/// </summary>
public sealed class CliCleaningRunner
{
    private readonly bool _logEnabled;
    private readonly TextWriter _stdout, _stderr;
    private readonly object _lock = new();
    private readonly SingleFileCleaningService _service;
    private int _cleaned, _partial, _failed, _cancelled;

    /// <param name="logEnabled">--log: every line goes to the private daily log instead of the console.</param>
    /// <param name="stdout">Results and the summary.</param>
    /// <param name="stderr">Failures.</param>
    public CliCleaningRunner(bool logEnabled, TextWriter stdout, TextWriter stderr) : this(logEnabled, stdout, stderr, new()) { }

    internal CliCleaningRunner(bool logEnabled, TextWriter stdout, TextWriter stderr, SingleFileCleaningService service)
    {
        _logEnabled = logEnabled; _stdout = stdout; _stderr = stderr; _service = service;
    }

    /// <summary>Cleans every path; a cancelled token stops new files and interrupts the current ones safely.</summary>
    public async Task<CliBatchSummary> RunAsync(IReadOnlyList<string> paths, CleanOptions options, CancellationToken ct)
    {
        var service = _service;
        using var semaphore = new SemaphoreSlim(Math.Max(1, Environment.ProcessorCount));
        var tasks = paths.Select(async path =>
        {
            try { await semaphore.WaitAsync(ct); }
            catch (OperationCanceledException) { Stop(path); return; }
            try { await CleanOneAsync(service, path, options, ct); }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) Stop(path);
                else ReportFailure(path, ex.Message, ex);
            }
            finally { semaphore.Release(); }
        }).ToList();
        await Task.WhenAll(tasks);
        var summary = new CliBatchSummary(paths.Count, _cleaned, _partial, _failed, _cancelled);
        Warning(summary.Describe());
        return summary;
    }

    private async Task CleanOneAsync(SingleFileCleaningService service, string path, CleanOptions options, CancellationToken ct)
    {
        if (!File.Exists(path)) { ReportFailure(path, "файл не найден"); return; }
        if (!SingleFileCleaningService.SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
        {
            ReportFailure(path, "расширение не поддерживается");
            return;
        }
        var outcome = await service.CleanWithBackupAsync(path, options, ct);
        if (outcome.BackupPath != null) Warning($"[BACKUP] {path} -> {outcome.BackupPath}");
        var result = outcome.Result;
        if (!result.Success)
        {
            if (ct.IsCancellationRequested) Stop(path);
            else ReportFailure(path, result.Message ?? "неизвестная ошибка при очистке");
            return;
        }
        var logPath = string.Equals(result.Path, path, StringComparison.OrdinalIgnoreCase) ? path : $"{path} -> {result.Path}";
        if (result.Incomplete)
        {
            Interlocked.Increment(ref _partial);
            var detail = string.IsNullOrWhiteSpace(result.Message) ? "" : ": " + result.Message;
            Warning($"[PARTIAL] {logPath} :: метаданные удалены не полностью{detail}");
            return;
        }
        Interlocked.Increment(ref _cleaned);
        if (!string.IsNullOrWhiteSpace(result.Message)) Warning($"[WARN] {logPath} :: {result.Message}");
        else if (_logEnabled) LogLine($"[OK]   {logPath}");
    }

    private void Stop(string path)
    {
        Interlocked.Increment(ref _cancelled);
        Failure($"[STOP] {path} :: очистка прервана, исходный файл не изменён.");
    }

    private void ReportFailure(string path, string reason, Exception? exception = null)
    {
        Interlocked.Increment(ref _failed);
        var reportPath = UserErrorReportWriter.TryWrite(path, reason, exception);
        var suffix = reportPath == null ? string.Empty : $" Пояснение: {reportPath}";
        Failure($"[FAIL] {path} :: {reason.TrimEnd('.')}.{suffix}");
    }

    private void Failure(string message)
    {
        if (_logEnabled) LogLine(message);
        else lock (_lock) _stderr.WriteLine(message);
    }

    // Results and the summary: stdout, or the log with --log.
    private void Warning(string message)
    {
        if (_logEnabled) LogLine(message);
        else lock (_lock) _stdout.WriteLine(message);
    }

    /// <summary>Appends a line to today's private log; logging never breaks cleaning.</summary>
    public void LogLine(string message)
    {
        try
        {
            AppDataPaths.EnsurePrivateDirectory(AppDataPaths.LogsDirectory);
            var now = DateTime.UtcNow;
            lock (_lock) File.AppendAllText(AppDataPaths.DailyLogPath(now), now.ToString("o") + " " + message + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Deletes this application's logs and error reports older than 30 days (current and legacy folders).</summary>
    public static void ExpireOldLogs()
    {
        var age = TimeSpan.FromDays(30);
        AppDataPaths.DeleteExpiredLogs(age);
        foreach (var legacy in AppDataPaths.LegacyLogDirectories()) AppDataPaths.DeleteExpiredLogs(age, directory: legacy);
    }

    /// <summary>
    /// Ctrl+C and SIGTERM request a graceful stop: working copies are removed and originals stay intact.
    /// A second signal terminates immediately.
    /// </summary>
    public static IDisposable CancelOnInterrupt(CancellationTokenSource source)
    {
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            if (source.IsCancellationRequested) return;
            e.Cancel = true;
            source.Cancel();
        };
        Console.CancelKeyPress += handler;
        System.Runtime.InteropServices.PosixSignalRegistration? sigterm = null;
        if (!OperatingSystem.IsWindows())
        {
            sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, context =>
            {
                if (source.IsCancellationRequested) return;
                context.Cancel = true;
                source.Cancel();
            });
        }
        return new Registration(() => { Console.CancelKeyPress -= handler; sigterm?.Dispose(); });
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
