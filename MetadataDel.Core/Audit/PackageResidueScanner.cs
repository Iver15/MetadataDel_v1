using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using MetadataDel.Core.Media;
using MetadataDel.Core.Ole;
using MetadataDel.Core.OpenXml;

namespace MetadataDel.Core.Audit;

/// <summary>
/// Проверяет OpenXML-пакет на уровне ZIP-архива, не опираясь на логику очистки: любые оставшиеся части
/// и атрибуты с метаданными находятся, даже если на них нет типизированных ссылок.
/// </summary>
internal static class PackageResidueScanner
{
    private const int MaxDepth = 8;

    private static readonly Regex UserPathPattern = new(
        @"(?:[A-Za-z]:|%5C|\\\\[^\\/""<>]+)(?:[\\/]|%5C|%2F)+(?:Users|Documents and Settings)(?:[\\/]|%5C|%2F)+[^\\/""<>%]+" +
        @"|(?<![\w.])/(?:Users|home)/[^/""<>\s]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CommentPartPattern = new(
        @"(?:^|/)(?:comments\d*|commentsExtended|commentsExtensible|commentsIds|people|commentAuthors|authors|threadedComment\d*|modernComment[^/]*|person)\.xml$" +
        @"|(?:^|/)(?:comments|threadedComments|persons)/",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> IdentityAttributes = new(StringComparer.Ordinal)
    {
        "author", "initials", "userName", "refreshedBy", "ed", "userId", "providerId"
    };

    private static readonly HashSet<string> ServiceElements = new(StringComparer.Ordinal)
    {
        "rsids", "docVars", "attachedTemplate", "docId", "revisionPtr", "absPath", "fileVersion", "mailMerge"
    };

    private static readonly HashSet<string> RevisionElements = new(StringComparer.Ordinal)
    {
        "ins", "del", "moveFrom", "moveTo", "rPrChange", "pPrChange", "sectPrChange", "tblPrChange",
        "trPrChange", "tcPrChange", "tblGridChange", "numberingChange"
    };

    private static readonly HashSet<string> PopulatedAppProperties = new(StringComparer.Ordinal)
    {
        "Company", "Manager", "Template", "HyperlinkBase", "Application", "AppVersion", "TotalTime"
    };

    public static void Scan(Stream package, ICollection<MetadataAuditFinding> findings) => Scan(package, findings, "", 0);

    private static void Scan(Stream package, ICollection<MetadataAuditFinding> findings, string prefix, int depth)
    {
        using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (name.EndsWith("/", StringComparison.Ordinal)) continue;
            var lower = name.ToLowerInvariant();

            if (lower == "docprops/custom.xml") Add(findings, prefix, "custom", "пользовательские свойства документа");
            if (lower.StartsWith("docprops/thumbnail", StringComparison.Ordinal)) Add(findings, prefix, "thumbnail", "миниатюра документа");
            if (lower.StartsWith("customxml/", StringComparison.Ordinal) && !lower.Contains("/_rels/")) Add(findings, prefix, "customXml", "custom XML");
            if (CommentPartPattern.IsMatch(lower)) Add(findings, prefix, "comments", "комментарии или их авторы");
            if (lower.Contains("/printersettings/")) Add(findings, prefix, "printer", "настройки принтера");
            if (lower.Contains("/revisions/")) Add(findings, prefix, "revisions", "журнал изменений общей книги");
            if (lower.Contains("pivotcacherecords")) Add(findings, prefix, "pivotRecords", "кэш исходных данных сводной таблицы");

            var data = Read(entry);
            if (ImageMetadata.HasMetadata(data))
                Add(findings, prefix, "image", "метаданные изображений (EXIF, GPS, XMP)");

            if (OpenXmlPackageSanitizer.IsCompoundFile(data))
            {
                ScanOle(data, findings, prefix + name + ": ");
                continue;
            }
            if (data.Length > 4 && data[0] == (byte)'P' && data[1] == (byte)'K' && lower.Contains("embeddings/"))
            {
                if (depth + 1 > MaxDepth) Add(findings, prefix, "depth", "слишком глубоко вложенный объект");
                else
                {
                    try { Scan(new MemoryStream(data), findings, prefix + "встроенный " + Path.GetFileName(name) + ": ", depth + 1); }
                    catch (InvalidDataException) { Add(findings, prefix, "embedded", "непрочитанный встроенный объект " + name); }
                }
                continue;
            }

            if (lower.EndsWith(".xml", StringComparison.Ordinal) || lower.EndsWith(".rels", StringComparison.Ordinal) ||
                lower.EndsWith(".vml", StringComparison.Ordinal))
                ScanXml(lower, data, findings, prefix);
        }
    }

    private static void ScanXml(string name, byte[] data, ICollection<MetadataAuditFinding> findings, string prefix)
    {
        var text = System.Text.Encoding.UTF8.GetString(data);
        // Пути ищутся в связях, атрибутах и кодах полей; путь, набранный в тексте документа, — это содержимое.
        if (name.EndsWith(".rels", StringComparison.Ordinal) && UserPathPattern.IsMatch(text))
            Add(findings, prefix, "userPath", "локальные пути с именем пользователя");

        XDocument document;
        try { document = XDocument.Parse(text); }
        catch (XmlException) { return; }
        var root = document.Root;
        if (root == null) return;

        if (name == "docprops/core.xml" && root.Elements().Any(e => !string.IsNullOrWhiteSpace(e.Value) || e.HasAttributes && e.Name.LocalName is "created" or "modified"))
            Add(findings, prefix, "core", "свойства документа (автор, даты, заголовок)");
        if (name == "docprops/app.xml" && root.Elements().Any(e => PopulatedAppProperties.Contains(e.Name.LocalName) && !string.IsNullOrWhiteSpace(e.Value)))
            Add(findings, prefix, "app", "сведения о приложении, организации и шаблоне");

        if (name.EndsWith(".rels", StringComparison.Ordinal) && root.Elements().Any(e =>
                (string?)e.Attribute("TargetMode") == "External" &&
                ((string?)e.Attribute("Type") ?? "").EndsWith("/attachedTemplate", StringComparison.Ordinal)))
            Add(findings, prefix, "template", "ссылка на шаблон документа");

        foreach (var element in root.DescendantsAndSelf())
        {
            var local = element.Name.LocalName;
            if (ServiceElements.Contains(local)) Add(findings, prefix, "service", "служебные идентификаторы и настройки");
            if (RevisionElements.Contains(local) && name.StartsWith("word/", StringComparison.Ordinal))
                Add(findings, prefix, "revisions", "исправления (tracked changes)");
            if (local == "instrText" && UserPathPattern.IsMatch(element.Value))
                Add(findings, prefix, "userPath", "локальные пути с именем пользователя");
            foreach (var attribute in element.Attributes())
            {
                var attributeName = attribute.Name.LocalName;
                if (attribute.IsNamespaceDeclaration) continue;
                if (UserPathPattern.IsMatch(attribute.Value))
                    Add(findings, prefix, "userPath", "локальные пути с именем пользователя");
                if (IdentityAttributes.Contains(attributeName) && !string.IsNullOrWhiteSpace(attribute.Value))
                    Add(findings, prefix, "identity", "имена пользователей в разметке");
                else if (attributeName.StartsWith("rsid", StringComparison.Ordinal))
                    Add(findings, prefix, "rsid", "идентификаторы сеансов редактирования (rsid)");
                else if (attributeName is "descr" or "title" or "href" or "alt" && OpenXmlPackageSanitizer.LooksLikeLocalPath(attribute.Value))
                    Add(findings, prefix, "imagePath", "пути к исходным файлам изображений");
                else if (attributeName == "title" && local == "imagedata")
                    Add(findings, prefix, "imagePath", "имена исходных файлов изображений");
            }
        }
    }

    internal static void ScanOle(byte[] data, ICollection<MetadataAuditFinding> findings, string prefix)
    {
        try
        {
            using var root = OpenMcdf.RootStorage.Open(new MemoryStream(data));
            foreach (var (code, description) in OleDocumentCleaner.Inspect(root))
                Add(findings, prefix, code, description);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Add(findings, prefix, "ole", "непрочитанный встроенный OLE-объект");
        }
    }

    private static byte[] Read(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void Add(ICollection<MetadataAuditFinding> findings, string prefix, string code, string description)
    {
        var finding = new MetadataAuditFinding("package." + code, prefix + description);
        if (!findings.Contains(finding)) findings.Add(finding);
    }
}
