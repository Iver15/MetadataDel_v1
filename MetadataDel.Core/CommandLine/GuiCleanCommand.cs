using System.Text.Json;
using MetadataDel.Core.Cleaning;

namespace MetadataDel.Core.CommandLine;

/// <summary>Opt-in one-file JSON protocol for the native macOS window. Never used by Finder.</summary>
public static class GuiCleanCommand
{
    /// <summary>Protocol version; 2 added "incomplete" and "backupPath".</summary>
    public const int SchemaVersion = 2;

    /// <summary>
    /// Validates the complete request before opening a document, then writes one JSON response.
    /// Exit code: 0 — fully cleaned, 1 — written but some metadata remains (incomplete), 2 — not cleaned.
    /// </summary>
    public static Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct = default) =>
        RunAsync(args, output, error, new SingleFileCleaningService(), ct);

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, SingleFileCleaningService service, CancellationToken ct)
    {
        string? input = null;
        try
        {
            var separator = Array.IndexOf(args, "--");
            if (args.Length < 3 || args[0] != "--gui-clean" || separator < 1 || separator != args.Length - 2)
                throw new ArgumentException("Ожидается один файл после --.");
            bool backup = true;
            foreach (var option in args.Skip(1).Take(separator - 1))
                backup = option switch
                {
                    "--backup=on" => true,
                    "--backup=off" => false,
                    _ => throw new ArgumentException("Неизвестный параметр: " + option)
                };
            if (!Path.IsPathFullyQualified(args[^1])) throw new ArgumentException("Нужен абсолютный путь к файлу.");
            input = Path.GetFullPath(args[^1]);
            var outcome = await service.CleanWithBackupAsync(input, new CleanOptions(Backup: backup), ct);
            await WriteResult(output, input, outcome.Result, outcome.BackupPath);
            return !outcome.Result.Success ? 2 : outcome.Result.Incomplete ? 1 : 0;
        }
        catch (Exception ex)
        {
            var message = ex is OperationCanceledException ? "Очистка прервана. Исходный файл не изменён." : ex.Message;
            await WriteResult(output, input, new CleanResult(input ?? "", false, message), null);
            await error.WriteLineAsync(message);
            return 2;
        }
    }

    internal static Task WriteResult(TextWriter output, string? input, CleanResult result, string? backupPath) =>
        output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            schemaVersion = SchemaVersion, inputPath = input, outputPath = result.Success ? result.Path : null,
            success = result.Success, incomplete = result.Success && result.Incomplete, message = result.Message,
            backupPath
        }));
}
