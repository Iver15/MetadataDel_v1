using System.Collections.Concurrent;
using System.Text;
using MetadataDel.Core.Audit;
using MetadataDel.Core.Cleaning;
using MetadataDel.Core.Excel;
using MetadataDel.Core.Ole;
using MetadataDel.Core.Pdf;
using MetadataDel.Core.Word;

namespace MetadataDel.MacCli;

public static class Program
{
    private static bool _logEnabled;

    public static async Task<int> Main(string[] args)
    {
        if (args.Any(a => a.Equals("--help", StringComparison.OrdinalIgnoreCase) || a.Equals("-h", StringComparison.OrdinalIgnoreCase)))
        {
            PrintUsage();
            return 0;
        }

        if (HasArg(args, "--install") ||
            HasArg(args, "--uninstall") ||
            HasArg(args, "--install-shell") ||
            HasArg(args, "--uninstall-shell") ||
            HasArg(args, "--diagnostics"))
        {
            Console.Error.WriteLine("Режимы установки и контекстного меню доступны только в Windows-сборке MetadataDel.");
            return 2;
        }

        if (HasArg(args, "--audit"))
        {
            return RunAuditMode(args);
        }

        var (options, paths) = ParseArgs(args);
        if (paths.Count == 0)
        {
            PrintUsage();
            return 2;
        }

        var services = BuildServiceProvider();
        var results = new ConcurrentBag<bool>();
        using var semaphore = new SemaphoreSlim(Math.Max(1, Environment.ProcessorCount));

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
        else
        {
            Console.WriteLine(summary);
        }

        return results.All(x => x) ? 0 : (results.Any(x => x) ? 1 : 2);
    }

    private static bool HasArg(string[] args, string option) =>
        args.Any(a => a.Equals(option, StringComparison.OrdinalIgnoreCase));

    private static (CleanOptions options, List<string> paths) ParseArgs(string[] args)
    {
        var backup = false;
        var aggressive = false;
        var wipeFs = false;
        var files = new List<string>();

        foreach (var raw in args)
        {
            var argument = raw.Trim();
            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                if (argument.StartsWith("--backup", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = argument.Split('=', 2);
                    backup = parts.Length == 1 ||
                        parts[1].Equals("on", StringComparison.OrdinalIgnoreCase) ||
                        parts[1].Equals("true", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (argument.Equals("--log", StringComparison.OrdinalIgnoreCase))
                {
                    _logEnabled = true;
                    continue;
                }

                if (argument.Equals("--aggressive-pdf", StringComparison.OrdinalIgnoreCase))
                {
                    aggressive = true;
                    continue;
                }

                if (argument.Equals("--wipe-fs", StringComparison.OrdinalIgnoreCase) ||
                    argument.Equals("--wipe-fs-timestamps", StringComparison.OrdinalIgnoreCase))
                {
                    wipeFs = true;
                    continue;
                }
            }

            var path = argument.Trim('"');
            if (Directory.Exists(path))
            {
                var extensions = new[] { "*.pdf", "*.docx", "*.doc", "*.xlsx", "*.xls" };
                foreach (var extension in extensions)
                {
                    files.AddRange(Directory.GetFiles(path, extension, SearchOption.AllDirectories));
                }
            }
            else
            {
                files.Add(path);
            }
        }

        return (new CleanOptions(backup, aggressive, wipeFs), files);
    }

    private static IServiceProvider BuildServiceProvider()
    {
        var services = new Dictionary<Type, object>
        {
            { typeof(PdfCleaner), new PdfCleaner() },
            { typeof(DocxCleaner), new DocxCleaner() },
            { typeof(ExcelCleaner), new ExcelCleaner() },
            { typeof(OleDocumentCleaner), new OleDocumentCleaner() },
        };

        return new SimpleServiceProvider(services);
    }

    private static void PrintUsage()
    {
        const string usage = "Использование: MetadataDel [--backup[=on|off]] [--log] [--aggressive-pdf] [--wipe-fs|--wipe-fs-timestamps] <file1> <file2> ...\n" +
                             "Дополнительно: --help | -h — показать справку\n" +
                             "Аудит: MetadataDel --audit <file1> <file2> ...\n" +
                             "Примеры:\n" +
                             "  MetadataDel --log --backup=on file.pdf file.docx\n" +
                             "  MetadataDel --audit file.pdf";
        Console.Error.WriteLine(usage);
    }

    private static void ReportFailure(string path, string reason, Exception? exception = null)
    {
        var reportPath = UserErrorReportWriter.TryWrite(path, reason, exception);
        var suffix = reportPath == null ? string.Empty : $" Файл с пояснением: {reportPath}";
        WriteFailure($"[FAIL] {path} :: {reason}.{suffix}");
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
            File.AppendAllText(file, DateTime.UtcNow.ToString("o") + " " + message + Environment.NewLine);
        }
        catch
        {
        }
    }

    private static int RunAuditMode(string[] args)
    {
        var paths = args
            .Where(a => !a.Equals("--audit", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Trim().Trim('"'))
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .ToList();

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

internal static class UserErrorReportWriter
{
    public static string? TryWrite(string path, string reason, Exception? exception = null)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return null;
            }

            var fileName = Path.GetFileName(path) + ".MetadataDel-ошибка.txt";
            var reportPath = Path.Combine(directory, fileName);
            File.WriteAllText(reportPath, BuildReport(path, reason, exception), Encoding.UTF8);
            return reportPath;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildReport(string path, string reason, Exception? exception)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Не удалось обработать файл");
        builder.AppendLine();
        builder.AppendLine("Файл:");
        builder.AppendLine(path);
        builder.AppendLine();
        builder.AppendLine("Что случилось:");
        builder.AppendLine(reason);

        if (exception != null)
        {
            builder.AppendLine();
            builder.AppendLine("Подробности:");
            builder.AppendLine(exception.Message);
        }

        builder.AppendLine();
        builder.AppendLine("Когда это произошло:");
        builder.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        return builder.ToString();
    }
}
