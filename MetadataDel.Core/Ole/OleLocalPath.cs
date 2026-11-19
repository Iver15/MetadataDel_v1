using System.Text.RegularExpressions;

namespace MetadataDel.Core.Ole;

/// <summary>
/// Сокращение локальных путей до имени файла без изменения длины строки. Двоичные форматы Office хранят пути
/// в записях фиксированной длины и со смещениями, поэтому путь заменяется относительным <c>.\.\.\имя</c>
/// той же длины: каталоги (имя пользователя, папки, сервер) исчезают, имя файла остаётся.
/// </summary>
internal static partial class OleLocalPath
{
    // A URL scheme of two or more characters: "C:" is a drive, not a scheme.
    [GeneratedRegex("^(?<scheme>[A-Za-z][A-Za-z0-9+.\\-]+):")]
    private static partial Regex Scheme();

    /// <summary>
    /// Возвращает строку той же длины, где каталоги локального пути заменены на <c>.\</c>, или <c>null</c>,
    /// если это не локальный путь (http, mailto и другие URL — содержимое документа) или в нём нет каталогов
    /// с именами. Поддерживаются диск, UNC, относительные пути и URL <c>file:</c>; хвост после нуля сохраняется.
    /// </summary>
    public static string? Shorten(string value)
    {
        var terminator = value.IndexOf('\0');
        var path = terminator >= 0 ? value[..terminator] : value;
        var prefixLength = 0;
        var scheme = Scheme().Match(path);
        if (scheme.Success)
        {
            if (!scheme.Groups["scheme"].Value.Equals("file", StringComparison.OrdinalIgnoreCase)) return null;
            prefixLength = scheme.Length;
            while (prefixLength < path.Length && path[prefixLength] is '/' or '\\') prefixLength++;
        }
        var rest = path[prefixLength..];
        var nameStart = rest.LastIndexOfAny(['\\', '/', ':']) + 1;
        if (!HasNamedDirectory(rest[..nameStart])) return null;
        return path[..prefixLength] + RelativePath(rest[nameStart..], rest, rest.Length) + value[path.Length..];
    }

    // "." and ".." say nothing about the user; a drive, server or folder name does.
    private static bool HasNamedDirectory(string directory) =>
        directory.Contains(':') || directory.Split('\\', '/').Any(part => part.Length > 0 && part is not ("." or ".."));

    /// <summary>
    /// <c>.\.\.\имя</c> ровно заданной длины: корректный относительный путь к тому же имени файла.
    /// Нечётный остаток поглощается удвоенным разделителем; Windows и разборщики URL его схлопывают.
    /// </summary>
    public static string RelativePath(string name, string original, int length)
    {
        var separator = original.Contains('\\') ? '\\' : '/';
        var padding = length - name.Length;
        var prefix = string.Concat(Enumerable.Repeat("." + separator, padding / 2));
        return padding % 2 == 1 ? prefix + separator + name : prefix + name;
    }
}
