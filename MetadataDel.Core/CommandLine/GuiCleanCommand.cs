using System.Text.Json;
using MetadataDel.Core.Cleaning;

namespace MetadataDel.Core.CommandLine;

/// <summary>Opt-in one-file JSON protocol for the native macOS window. Never used by Finder.</summary>
public static class GuiCleanCommand
{
    /// <summary>Validates the complete request before opening a document, then writes one JSON response.</summary>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
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
            var result = await new SingleFileCleaningService().CleanAsync(input, new CleanOptions(Backup: backup));
            await WriteResult(output, input, result);
            return result.Success ? 0 : 2;
        }
        catch (Exception ex)
        {
            await WriteResult(output, input, new CleanResult(input ?? "", false, ex.Message));
            await error.WriteLineAsync(ex.Message);
            return 2;
        }
    }

    internal static Task WriteResult(TextWriter output, string? input, CleanResult result) =>
        output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            schemaVersion = 1, inputPath = input, outputPath = result.Success ? result.Path : null,
            success = result.Success, message = result.Message
        }));
}
