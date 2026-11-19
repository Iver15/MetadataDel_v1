using MetadataDel.Core.CommandLine;
using System.Diagnostics;
using System.Text;
using MetadataDel.Core.Audit;
using Microsoft.Win32;

namespace MetadataDel.Cli;

public static class Program
{
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
        CliCleaningRunner.ExpireOldLogs();
        if (parsed.Audit) return RunAuditMode(parsed.Paths);
        if (parsed.Paths.Count == 0)
        {
            PrintUsage();
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        using var interrupt = CliCleaningRunner.CancelOnInterrupt(cancellation);
        var runner = new CliCleaningRunner(parsed.Log, Console.Out, Console.Error);
        var summary = await runner.RunAsync(parsed.Paths, parsed.Options, cancellation.Token);
        // Explorer and "Send to" run this WinExe with --log and no console: say when something needs attention.
        if (OperatingSystem.IsWindows() && parsed.Log && (summary.Partial > 0 || summary.Failed > 0))
            ShellNotification.Show(summary);
        return summary.ExitCode;
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

        if (Directory.Exists(AppDataPaths.BackupsDirectory))
        {
            Console.WriteLine($"Резервные копии документов сохранены и не удалялись: {AppDataPaths.BackupsDirectory}");
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
        var usage = "Использование: MetadataDel.exe [--backup[=on|off]] [--log] [--aggressive-pdf] [--wipe-fs|--wipe-fs-timestamps] <file1> <file2> ...\n" +
                             "Дополнительно: --help | -h — показать справку\n" +
                             "Аудит: MetadataDel.exe --audit <file1> <file2> ...\n" +
                             "Служебные режимы Windows: --install | --uninstall | --install-shell | --uninstall-shell | --diagnostics\n" +
                             "Примеры:\n" +
                             "  MetadataDel.exe --log --backup=on file.pdf file.docx\n" +
                             "  MetadataDel.exe --wipe-fs \"D:\\docs\"\n" +
                             "Коды завершения: 0 — все файлы очищены полностью; 1 — часть файлов не очищена полностью или завершилась ошибкой;\n" +
                             "  2 — ни один файл не очищен; 130 — очистка прервана (Ctrl+C).\n" +
                             $"Резервные копии: {AppDataPaths.BackupsDirectory}\n" +
                             $"Журнал и пояснения к ошибкам: {AppDataPaths.LogsDirectory}";
        Console.Error.WriteLine(usage);
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
                var audit = MetadataAuditService.AuditWithFileSystem(path);
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
    // Папка Backups с оригиналами документов не удаляется: это данные пользователя.
    private static void DeleteLogs()
    {
        var appData = AppDataPaths.DataDirectory;
        try
        {
            var logs = AppDataPaths.LogsDirectory;
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

    public static string LogsDirectory => AppDataPaths.LogsDirectory;

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
