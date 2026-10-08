using MetadataDel.Core.CommandLine;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using MetadataDel.Core.Audit;
using MetadataDel.Core.Cleaning;
using MetadataDel.Core.Excel;
using MetadataDel.Core.Pdf;
using MetadataDel.Core.Word;
using Microsoft.Win32;

namespace MetadataDel.Cli;

public static class Program
{
    private static bool _logEnabled;
    private static readonly object LogLock = new();

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        if (OperatingSystem.IsWindows() && DesktopEntryPoint.ShouldOpenMainWindow(args))
        {
            return SetupLauncher.Run();
        }

        if (HasArg(args, "--help") || HasArg(args, "-h"))
        {
            PrintUsage();
            return 0;
        }

        var maintenanceModes = new[] { "--install", "--uninstall", "--install-shell", "--uninstall-shell", "--diagnostics" };
        if (maintenanceModes.Any(mode => HasArg(args, mode)) && args.Length != 1)
        {
            Console.Error.WriteLine("Служебный режим нужно запускать отдельно, без других параметров и файлов.");
            return 2;
        }
        var maintenanceExitCode = HandleMaintenanceMode(args);
        if (maintenanceExitCode.HasValue)
        {
            return maintenanceExitCode.Value;
        }

        CliArguments parsed;
        try { parsed = CliArguments.Parse(args); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        _logEnabled = parsed.Log;
        if (parsed.Audit) return RunAuditMode(parsed.Paths);
        var options = parsed.Options;
        var paths = parsed.Paths;
        if (paths.Count == 0)
        {
            PrintUsage();
            return 2;
        }

        var services = BuildServiceProvider();
        var results = new ConcurrentBag<bool>();
        var parallelism = Math.Max(1, Environment.ProcessorCount);
        using var semaphore = new SemaphoreSlim(parallelism);

        var tasks = paths.Select(async path =>
        {
            await semaphore.WaitAsync();

            try
            {
                if (!File.Exists(path))
                {
                    ReportFailure(path, "файл не найден");
                    results.Add(false);
                    return;
                }

                var cleaner = CleanerFactory.Resolve(path, services);
                if (cleaner == null)
                {
                    ReportFailure(path, "расширение не поддерживается");
                    results.Add(false);
                    return;
                }

                var result = await cleaner.CleanAsync(path, options);
                results.Add(result.Success);

                if (!result.Success)
                {
                    ReportFailure(path, result.Message ?? "неизвестная ошибка при очистке");
                    return;
                }

                var logPath = string.Equals(result.Path, path, StringComparison.OrdinalIgnoreCase)
                    ? path
                    : $"{path} -> {result.Path}";

                if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    WriteWarning($"[WARN] {logPath} :: {result.Message}");
                    return;
                }

                if (_logEnabled)
                {
                    LogLine($"[OK]   {logPath}");
                }
            }
            catch (Exception ex)
            {
                results.Add(false);
                ReportFailure(path, ex.Message, ex);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        var total = results.Count;
        var success = results.Count(x => x);
        var summary = $"Итог: {success}/{total} файлов обработано успешно.";
        if (_logEnabled)
        {
            LogLine(summary);
        }

        return results.All(x => x) ? 0 : (results.Any(x => x) ? 1 : 2);
    }

    private static int? HandleMaintenanceMode(string[] args)
    {
        if (!args.Any(a => a.StartsWith("--", StringComparison.Ordinal)))
        {
            return null;
        }

        if (HasArg(args, "--install"))
        {
            return ExecuteWindowsMaintenanceMode(
                "--install",
                () =>
                {
                    var summary = SelfInstaller.Install();
                    PrintInstallSummary(summary, fullInstall: true);
                });
        }

        if (HasArg(args, "--uninstall"))
        {
            return ExecuteWindowsMaintenanceMode(
                "--uninstall",
                () =>
                {
                    var summary = SelfInstaller.Uninstall(removeFiles: true);
                    PrintUninstallSummary(summary, removeFiles: true);
                });
        }

        if (HasArg(args, "--install-shell"))
        {
            return ExecuteWindowsMaintenanceMode(
                "--install-shell",
                () =>
                {
                    var summary = SelfInstaller.InstallShellIntegration();
                    PrintInstallSummary(summary, fullInstall: false);
                });
        }

        if (HasArg(args, "--uninstall-shell"))
        {
            return ExecuteWindowsMaintenanceMode(
                "--uninstall-shell",
                () =>
                {
                    var summary = SelfInstaller.Uninstall(removeFiles: false);
                    PrintUninstallSummary(summary, removeFiles: false);
                });
        }

        if (HasArg(args, "--diagnostics"))
        {
            return ExecuteWindowsMaintenanceMode(
                "--diagnostics",
                () => PrintDiagnostics(SelfInstaller.GetDiagnostics()));
        }

        return null;
    }

    private static bool HasArg(string[] args, string option) =>
        args.TakeWhile(a => a != "--").Any(a => a.Equals(option, StringComparison.OrdinalIgnoreCase));

    private static int ExecuteWindowsMaintenanceMode(string mode, Action action)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine($"Режим {mode} доступен только в Windows.");
            return 2;
        }

        try
        {
            action();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Ошибка режима {mode}: {ex.Message}");
            return 2;
        }
    }

    private static void PrintInstallSummary(SelfInstaller.InstallSummary summary, bool fullInstall)
    {
        if (fullInstall)
        {
            Console.WriteLine($"MetadataDel установлен в: {summary.InstallDirectory}");
            Console.WriteLine("Программа добавлена в список установленных приложений Windows.");
        }

        Console.WriteLine("Контекстное меню добавлено для: " + string.Join(", ", summary.RegisteredExtensions));

        foreach (var warning in summary.Warnings)
        {
            Console.WriteLine("Предупреждение: " + warning);
        }
    }

    private static void PrintUninstallSummary(SelfInstaller.UninstallSummary summary, bool removeFiles)
    {
        Console.WriteLine("Контекстное меню удалено.");

        if (!removeFiles)
        {
            return;
        }

        if (summary.FilesRemoved)
        {
            Console.WriteLine($"Файлы приложения удалены из: {summary.InstallDirectory}");
        }
        else if (summary.RemovalScheduled)
        {
            Console.WriteLine($"Удаление файлов запланировано после завершения процесса: {summary.InstallDirectory}");
        }
        else
        {
            Console.WriteLine("Файлы приложения не найдены, удалять было нечего.");
        }
    }

    private static void PrintDiagnostics(SelfInstaller.InstallationDiagnostics diagnostics)
    {
        Console.WriteLine("Контекстное меню может быть добавлено для: " + string.Join(", ", diagnostics.RegisterableExtensions));

        foreach (var warning in diagnostics.Warnings)
        {
            Console.WriteLine("Предупреждение: " + warning);
        }
    }

    private static void PrintUsage()
    {
        const string usage = "Использование: MetadataDel.exe [--backup[=on|off]] [--log] [--aggressive-pdf] [--wipe-fs|--wipe-fs-timestamps] <file1> <file2> ...\n" +
                             "Дополнительно: --help | -h — показать справку\n" +
                             "Аудит: MetadataDel.exe --audit <file1> <file2> ...\n" +
                             "Служебные режимы Windows: --install | --uninstall | --install-shell | --uninstall-shell | --diagnostics\n" +
                             "Примеры:\n" +
                             "  MetadataDel.exe --log --backup=on file.pdf file.docx\n" +
                             "  MetadataDel.exe --wipe-fs \"D:\\docs\"";
        Console.Error.WriteLine(usage);
    }

    private static IServiceProvider BuildServiceProvider()
    {
        var services = new Dictionary<Type, object>
        {
            { typeof(PdfCleaner), new PdfCleaner() },
            { typeof(DocxCleaner), new DocxCleaner() },
            { typeof(ExcelCleaner), new ExcelCleaner() },
            { typeof(MetadataDel.Core.Ole.OleDocumentCleaner), new MetadataDel.Core.Ole.OleDocumentCleaner() },
        };

        return new SimpleServiceProvider(services);
    }

    private static void WriteFailure(string message)
    {
        if (_logEnabled)
        {
            LogLine(message);
        }
        else
        {
            Console.Error.WriteLine(message);
        }
    }

    private static void ReportFailure(string path, string reason, Exception? exception = null)
    {
        var reportPath = UserErrorReportWriter.TryWrite(path, reason, exception);
        var suffix = reportPath == null ? string.Empty : $" Файл с пояснением: {reportPath}";
        WriteFailure($"[FAIL] {path} :: {reason}.{suffix}");
    }

    private static void WriteWarning(string message)
    {
        if (_logEnabled)
        {
            LogLine(message);
        }
        else
        {
            Console.WriteLine(message);
        }
    }

    private static void LogLine(string message)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MetadataDel", "logs");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, DateTime.UtcNow.ToString("yyyy-MM-dd") + ".log");
            lock (LogLock) File.AppendAllText(file, DateTime.UtcNow.ToString("o") + " " + message + Environment.NewLine);
        }
        catch { }
    }

    private static int RunAuditMode(List<string> paths)
    {
        if (paths.Count == 0)
        {
            Console.Error.WriteLine("Для режима --audit укажите хотя бы один файл.");
            return 2;
        }

        var foundSensitiveMetadata = false;
        var hadErrors = false;

        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"[MISS] {path} :: файл не найден");
                hadErrors = true;
                continue;
            }

            try
            {
                var audit = MetadataAuditService.Audit(path);
                if (!audit.HasSensitiveMetadata)
                {
                    Console.WriteLine($"[CLEAN] {path} :: следов метаданных не найдено");
                    continue;
                }

                foundSensitiveMetadata = true;
                Console.WriteLine($"[META]  {path} :: {audit.Summarize()}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FAIL]  {path} :: {ex.Message}");
                hadErrors = true;
            }
        }

        return hadErrors ? 2 : (foundSensitiveMetadata ? 1 : 0);
    }
}

internal sealed class SimpleServiceProvider : IServiceProvider
{
    private readonly IReadOnlyDictionary<Type, object> _services;

    public SimpleServiceProvider(IReadOnlyDictionary<Type, object> services) => _services = services;

    public object? GetService(Type serviceType) => _services.TryGetValue(serviceType, out var service) ? service : null;
}

internal static class SelfInstaller
{
    private const string AppName = "MetadataDel";
    private const string ExeName = "MetadataDel.exe";
    private const string MenuName = "Удалить метаданные";
    private const string Publisher = "MetadataDel";
    private const string UninstallEntryName = "MetadataDel";
    private const string UninstallLauncherFileName = "Удалить MetadataDel.cmd";

    private static readonly string[] ShellExtensions = { ".pdf", ".docx", ".xlsx", ".xls", ".doc" };

    public static InstallSummary Install()
    {
        var installDirectory = GetInstallDirectory();
        Directory.CreateDirectory(installDirectory);

        var currentExePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(currentExePath))
        {
            var currentExeDirectory = Path.GetDirectoryName(currentExePath)!;
            var targetExePath = Path.Combine(installDirectory, ExeName);

            // Для single-file: копируем сам exe напрямую (AppContext.BaseDirectory
            // указывает на временную папку распаковки, а не на реальное расположение exe)
            if (!PathsEqual(currentExeDirectory, installDirectory))
            {
                File.Copy(currentExePath, targetExePath, overwrite: true);
                var iconSource = Path.Combine(currentExeDirectory, "app.ico");
                if (File.Exists(iconSource)) File.Copy(iconSource, Path.Combine(installDirectory, "app.ico"), overwrite: true);

                // Прежние версии ставили exiftool в tools; очистка PDF больше его не использует.
                var staleTools = Path.Combine(installDirectory, "tools");
                if (Directory.Exists(staleTools))
                {
                    try { Directory.Delete(staleTools, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }

        var targetExe = Path.Combine(installDirectory, ExeName);
        var summary = InstallShellIntegration(targetExe, installDirectory);
        CreateSendToShortcut(targetExe);
        RegisterUninstallEntry(installDirectory, targetExe);
        CreateUninstallLauncher(installDirectory);
        return summary;
    }

    public static InstallSummary InstallShellIntegration()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            throw new InvalidOperationException("Не удалось определить путь к текущему exe.");
        }
        var installDirectory = Path.GetDirectoryName(exePath) ?? GetInstallDirectory();
        return InstallShellIntegration(exePath, installDirectory);
    }

    public static UninstallSummary Uninstall(bool removeFiles)
    {
        foreach (var extension in ShellExtensions)
        {
            UnregisterVerb(extension);
        }
        UnregisterDirectoryVerb();
        DeleteSendToShortcut();

        var installDirectory = GetInstallDirectory();
        var filesRemoved = false;
        var removalScheduled = false;

        if (removeFiles) DeleteUninstallEntry();
        if (removeFiles) DeleteLogs();
        if (removeFiles && Directory.Exists(installDirectory))
        {
            DeleteUninstallLauncher(installDirectory);

            var currentExeDirectory = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(currentExeDirectory) && PathsEqual(currentExeDirectory, installDirectory))
            {
                removalScheduled = ScheduleDirectoryRemoval(installDirectory);
            }
            else
            {
                Directory.Delete(installDirectory, recursive: true);
                filesRemoved = true;
            }
        }

        return new UninstallSummary(installDirectory, filesRemoved, removalScheduled);
    }

    // Журналы содержат пути очищенных файлов, поэтому удаляются вместе с программой.
    private static void DeleteLogs()
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
        try
        {
            var logs = Path.Combine(appData, "logs");
            if (Directory.Exists(logs)) Directory.Delete(logs, recursive: true);
            var settings = Path.Combine(appData, "settings.json");
            if (File.Exists(settings)) File.Delete(settings);
            if (Directory.Exists(appData) && !Directory.EnumerateFileSystemEntries(appData).Any()) Directory.Delete(appData);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Managed: MSI для всех пользователей, правый клик меняет только администратор.
    public static ShellIntegrationState GetShellIntegrationState()
    {
        if (IsManagedInstall()) return ShellIntegrationState.Managed;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($"Software\\Classes\\SystemFileAssociations\\.pdf\\shell\\{MenuName}\\command");
            if (key?.GetValue(null) is not string command) return ShellIntegrationState.Off;
            var exe = Environment.ProcessPath;
            return exe != null && command.StartsWith($"\"{exe}\"", StringComparison.OrdinalIgnoreCase) ? ShellIntegrationState.On : ShellIntegrationState.Stale;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return ShellIntegrationState.Off; }
    }

    public static bool IsManagedInstall()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return Environment.ProcessPath?.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) == true;
    }

    public static string LogsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName, "logs");

    public static InstallationDiagnostics GetDiagnostics() =>
        new(ShellExtensions, Array.Empty<string>());

    private static InstallSummary InstallShellIntegration(string exePath, string installDirectory)
    {
        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException("Не найден исполняемый файл для регистрации контекстного меню.", exePath);
        }

        var registeredExtensions = new List<string>();
        var warnings = new List<string>();

        foreach (var extension in ShellExtensions)
        {
            RegisterVerb(extension, exePath);
            registeredExtensions.Add(extension);
        }

        // Контекстное меню для папок
        RegisterDirectoryVerb(exePath);

        return new InstallSummary(installDirectory, registeredExtensions, warnings);
    }

    private static string GetInstallDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    private static string GetUninstallRegistryPath() =>
        $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{UninstallEntryName}";

    private static void RegisterVerb(string extension, string exePath)
    {
        using var baseKey = Registry.CurrentUser.CreateSubKey($"Software\\Classes\\SystemFileAssociations\\{extension}\\shell\\{MenuName}", true);
        baseKey?.SetValue("Icon", exePath, RegistryValueKind.String);
        // Player = один процесс получает все выделенные файлы, а не по процессу на файл
        baseKey?.SetValue("MultiSelectModel", "Player", RegistryValueKind.String);

        using var commandKey = Registry.CurrentUser.CreateSubKey($"Software\\Classes\\SystemFileAssociations\\{extension}\\shell\\{MenuName}\\command", true);
        commandKey?.SetValue(null, $"\"{exePath}\" --log \"%1\"", RegistryValueKind.String);
    }

    private static void RegisterDirectoryVerb(string exePath)
    {
        using var baseKey = Registry.CurrentUser.CreateSubKey($"Software\\Classes\\Directory\\shell\\{MenuName}", true);
        baseKey?.SetValue("Icon", exePath, RegistryValueKind.String);

        using var commandKey = Registry.CurrentUser.CreateSubKey($"Software\\Classes\\Directory\\shell\\{MenuName}\\command", true);
        commandKey?.SetValue(null, $"\"{exePath}\" --log \"%1\"", RegistryValueKind.String);
    }

    private static void UnregisterDirectoryVerb()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($"Software\\Classes\\Directory\\shell\\{MenuName}", throwOnMissingSubKey: false);
        }
        catch { }
    }

    private static void UnregisterVerb(string extension)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($"Software\\Classes\\SystemFileAssociations\\{extension}\\shell\\{MenuName}", throwOnMissingSubKey: false);
        }
        catch
        {
            // ignore
        }
    }

    private static void RegisterUninstallEntry(string installDirectory, string exePath)
    {
        using var uninstallKey = Registry.CurrentUser.CreateSubKey(GetUninstallRegistryPath(), writable: true);
        uninstallKey?.SetValue("DisplayName", AppName, RegistryValueKind.String);
        uninstallKey?.SetValue("DisplayVersion", GetDisplayVersion(), RegistryValueKind.String);
        uninstallKey?.SetValue("Publisher", Publisher, RegistryValueKind.String);
        uninstallKey?.SetValue("InstallLocation", installDirectory, RegistryValueKind.String);
        uninstallKey?.SetValue("DisplayIcon", exePath, RegistryValueKind.String);
        uninstallKey?.SetValue("UninstallString", $"\"{exePath}\" --uninstall", RegistryValueKind.String);
        uninstallKey?.SetValue("QuietUninstallString", $"\"{exePath}\" --uninstall", RegistryValueKind.String);
        uninstallKey?.SetValue("NoModify", 1, RegistryValueKind.DWord);
        uninstallKey?.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        uninstallKey?.SetValue("EstimatedSize", GetDirectorySizeInKb(installDirectory), RegistryValueKind.DWord);
    }

    private static void CreateSendToShortcut(string exePath)
    {
        try
        {
            var sendToDir = Environment.GetFolderPath(Environment.SpecialFolder.SendTo);
            var shortcutPath = Path.Combine(sendToDir, "Удалить метаданные.cmd");
            // cmd-обёртка: передаёт все выделенные файлы как аргументы одному процессу
            var script = "@echo off\r\n" +
                         $"\"{exePath}\" --log %*\r\n";
            File.WriteAllText(shortcutPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch { }
    }

    private static void DeleteSendToShortcut()
    {
        try
        {
            var sendToDir = Environment.GetFolderPath(Environment.SpecialFolder.SendTo);
            var shortcutPath = Path.Combine(sendToDir, "Удалить метаданные.cmd");
            if (File.Exists(shortcutPath)) File.Delete(shortcutPath);
        }
        catch { }
    }

    private static void DeleteUninstallEntry()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(GetUninstallRegistryPath(), throwOnMissingSubKey: false);
        }
        catch
        {
            // ignore
        }
    }

    private static void CreateUninstallLauncher(string installDirectory)
    {
        var launcherPath = Path.Combine(installDirectory, UninstallLauncherFileName);
        var script = "@echo off\r\n" +
                     "\"%~dp0MetadataDel.exe\" --uninstall\r\n";

        File.WriteAllText(launcherPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void DeleteUninstallLauncher(string installDirectory)
    {
        var launcherPath = Path.Combine(installDirectory, UninstallLauncherFileName);
        if (File.Exists(launcherPath))
        {
            File.Delete(launcherPath);
        }
    }

    private static bool ScheduleDirectoryRemoval(string directoryPath)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"mdel-uninstall-{Guid.NewGuid():N}.cmd");
        var script = "@echo off\r\n" +
                     "ping 127.0.0.1 -n 3 > nul\r\n" +
                     $"rmdir /s /q \"{directoryPath}\"\r\n" +
                     "del /f /q \"%~f0\"\r\n";

        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{scriptPath}\"\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath()
        });

        return process != null;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string GetDisplayVersion() =>
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private static int GetDirectorySizeInKb(string directoryPath)
    {
        try
        {
            var sizeInBytes = Directory
                .EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)
                .Sum(filePath => new FileInfo(filePath).Length);

            var sizeInKb = (sizeInBytes + 1023) / 1024;
            return sizeInKb > int.MaxValue ? int.MaxValue : (int)sizeInKb;
        }
        catch
        {
            return 0;
        }
    }

    internal sealed record InstallSummary(string InstallDirectory, IReadOnlyList<string> RegisteredExtensions, IReadOnlyList<string> Warnings);
    internal sealed record UninstallSummary(string InstallDirectory, bool FilesRemoved, bool RemovalScheduled);
    internal enum ShellIntegrationState { Off, On, Stale, Managed }
    internal sealed record InstallationDiagnostics(IReadOnlyList<string> RegisterableExtensions, IReadOnlyList<string> Warnings);
}
