using MetadataDel.Core.Cleaning;

namespace MetadataDel.Core.CommandLine;

/// <summary>Единый разбор параметров и входных файлов обеих CLI.</summary>
public sealed record CliArguments(CleanOptions Options, bool Log, bool Audit, List<string> Paths)
{
    /// <summary>Проверяет все параметры до изменения файлов; раскрывает папки без перехода по ссылкам.</summary>
    public static CliArguments Parse(string[] args)
    {
        bool backup = false, aggressive = false, wipe = false, log = false, audit = false, literal = false;
        var inputs = new List<string>();
        foreach (var argument in args)
        {
            if (!literal && argument == "--") { literal = true; continue; }
            if (!literal && argument.StartsWith('-'))
            {
                switch (argument.ToLowerInvariant())
                {
                    case "--backup": case "--backup=on": case "--backup=true": backup = true; break;
                    case "--backup=off": case "--backup=false": backup = false; break;
                    case "--aggressive-pdf": aggressive = true; break;
                    case "--wipe-fs": case "--wipe-fs-timestamps": wipe = true; break;
                    case "--log": log = true; break;
                    case "--audit": audit = true; break;
                    default: throw new ArgumentException("Неизвестный параметр: " + argument);
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(argument)) throw new ArgumentException("Пустой путь к файлу.");
                inputs.Add(Path.GetFullPath(argument));
            }
        }
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            if (!Directory.Exists(input)) { paths.Add(input); continue; }
            if ((File.GetAttributes(input) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Обход символической ссылки на папку не поддерживается: " + input);
            var enumeration = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (var file in Directory.EnumerateFiles(input, "*", enumeration))
            {
                if (Path.GetExtension(file).ToLowerInvariant() is ".pdf" or ".docx" or ".doc" or ".xlsx" or ".xls")
                    paths.Add(Path.GetFullPath(file));
            }
        }
        return new CliArguments(new CleanOptions(backup, aggressive, wipe), log, audit, paths.ToList());
    }
}
