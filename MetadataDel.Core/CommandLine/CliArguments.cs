using System.Text;
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
                var value = OperatingSystem.IsWindows() ? NormalizeWindowsShellArgument(argument) : argument;
                if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Пустой путь к файлу.");
                inputs.Add(Path.GetFullPath(value));
            }
        }
        var paths = new List<string>();
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
                if (Path.GetExtension(file).ToLowerInvariant() is ".pdf" or ".docx" or ".doc" or ".xlsx" or ".xls" &&
                    !IsServiceFile(Path.GetFileName(file)))
                    paths.Add(Path.GetFullPath(file));
            }
        }
        return new CliArguments(new CleanOptions(backup, aggressive, wipe), log, audit,
            Deduplicate(paths, caseInsensitive: OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()));
    }

    /// <summary>
    /// Keeps the first spelling of every file. On macOS and Windows the file system ignores case and Unicode
    /// normalization (NFC/NFD), so "Отчёт.pdf" typed and dropped from Finder is one file, not two parallel jobs.
    /// </summary>
    internal static List<string> Deduplicate(IEnumerable<string> paths, bool caseInsensitive)
    {
        var seen = new Dictionary<string, List<string>>(caseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var path in paths)
        {
            var key = caseInsensitive ? path.Normalize(NormalizationForm.FormC) : path;
            if (!seen.TryGetValue(key, out var kept)) { seen[key] = new List<string> { path }; result.Add(path); continue; }
            if (kept.Contains(path, StringComparer.Ordinal)) continue;
            // На регистрозависимом томе (case-sensitive APFS) «a.pdf» и «A.pdf» — два разных файла.
            if (kept.All(other => AreDistinctEntries(other, path))) { kept.Add(path); result.Add(path); }
        }
        return result;
    }

    /// <summary>Both spellings exist as separate entries of the same directory listing.</summary>
    private static bool AreDistinctEntries(string first, string second)
    {
        var directory = Path.GetDirectoryName(first);
        if (directory == null || !string.Equals(directory, Path.GetDirectoryName(second), StringComparison.Ordinal)) return false;
        try
        {
            var names = Directory.EnumerateFileSystemEntries(directory)
                .Select(entry => Path.GetFileName(entry).Normalize(NormalizationForm.FormC))
                .ToHashSet(StringComparer.Ordinal);
            return names.Contains(Path.GetFileName(first).Normalize(NormalizationForm.FormC)) &&
                   names.Contains(Path.GetFileName(second).Normalize(NormalizationForm.FormC)) &&
                   !string.Equals(Path.GetFileName(first).Normalize(NormalizationForm.FormC),
                       Path.GetFileName(second).Normalize(NormalizationForm.FormC), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Служебные файлы, которые не являются документами пользователя: блокировки Office (~$), AppleDouble (._)
    /// и незавершённые рабочие копии MetadataDel.
    /// </summary>
    internal static bool IsServiceFile(string name) =>
        name.StartsWith("~$", StringComparison.Ordinal) || name.StartsWith("._", StringComparison.Ordinal) ||
        System.Text.RegularExpressions.Regex.IsMatch(name, @"^\..+\.[0-9a-f]{32}\.tmp\.[A-Za-z]+(\.rebuilt)?$|^\.mdel-[0-9a-f]{32}\.pdf$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Explorer passes "%1" for a drive root as <c>D:\"</c> (the backslash escapes the closing quote).
    /// A quote can never be part of a Windows file name, so a trailing one is dropped; a bare "D:" means the drive root
    /// rather than the drive's current directory.
    /// </summary>
    internal static string NormalizeWindowsShellArgument(string argument)
    {
        var value = argument.TrimEnd('"');
        if (value.Length == 2 && char.IsAsciiLetter(value[0]) && value[1] == ':') value += "\\";
        return value;
    }
}
