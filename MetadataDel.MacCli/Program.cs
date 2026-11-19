using MetadataDel.Core.Audit;
using MetadataDel.Core.CommandLine;

namespace MetadataDel.MacCli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        using var interrupt = CliCleaningRunner.CancelOnInterrupt(cancellation);
        CliCleaningRunner.ExpireOldLogs();

        if (args.FirstOrDefault() == "--gui-clean")
            return await GuiCleanCommand.RunAsync(args, Console.Out, Console.Error, cancellation.Token);

        if (HasArg(args, "--help") || HasArg(args, "-h"))
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

        CliArguments parsed;
        try { parsed = CliArguments.Parse(args); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        if (parsed.Audit) return RunAuditMode(parsed.Paths);
        if (parsed.Paths.Count == 0)
        {
            PrintUsage();
            return 2;
        }

        var runner = new CliCleaningRunner(parsed.Log, Console.Out, Console.Error);
        var summary = await runner.RunAsync(parsed.Paths, parsed.Options, cancellation.Token);
        return summary.ExitCode;
    }

    private static bool HasArg(string[] args, string option) =>
        args.TakeWhile(a => a != "--").Any(a => a.Equals(option, StringComparison.OrdinalIgnoreCase));

    private static void PrintUsage()
    {
        var usage = "Использование: MetadataDel [--backup[=on|off]] [--log] [--aggressive-pdf] [--wipe-fs|--wipe-fs-timestamps] <file1> <file2> ...\n" +
                             "Дополнительно: --help | -h — показать справку\n" +
                             "Аудит: MetadataDel --audit <file1> <file2> ...\n" +
                             "Примеры:\n" +
                             "  MetadataDel --log --backup=on file.pdf file.docx\n" +
                             "  MetadataDel --audit file.pdf\n" +
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
