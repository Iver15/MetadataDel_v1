using System.IO.Packaging;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using MetadataDel.Core.Media;
using MetadataDel.Core.Ole;

namespace MetadataDel.Core.OpenXml;

#pragma warning disable OOXML0001

/// <summary>Состояние очистки пакета, общее для вложенных документов.</summary>
internal sealed class OpenXmlSanitizeContext
{
    public const int MaxDepth = 8;

    public OpenXmlSanitizeContext(CancellationToken cancellationToken = default) : this(new List<string>(), 0, cancellationToken) { }

    private OpenXmlSanitizeContext(List<string> warnings, int depth, CancellationToken cancellationToken)
    {
        Warnings = warnings;
        Depth = depth;
        CancellationToken = cancellationToken;
    }

    public List<string> Warnings { get; }

    /// <summary>0 — очищаемый файл, больше 0 — встроенный объект.</summary>
    public int Depth { get; }

    public CancellationToken CancellationToken { get; }

    public OpenXmlSanitizeContext Nested() => new(Warnings, Depth + 1, CancellationToken);

    public void Warn(string warning)
    {
        if (!Warnings.Contains(warning)) Warnings.Add(warning);
    }
}

/// <summary>
/// Очистка, общая для всех OpenXML-пакетов: свойства, миниатюра, custom XML, метаданные изображений,
/// встроенные документы и OLE-объекты, пути к исходным файлам картинок.
/// </summary>
internal static class OpenXmlPackageSanitizer
{
    private const string ExtendedPropertiesContentType = "application/vnd.openxmlformats-officedocument.extended-properties+xml";
    private const string CustomPropertiesContentType = "application/vnd.openxmlformats-officedocument.custom-properties+xml";

    // Большие табличные части не содержат описаний картинок; их разбор только замедлил бы очистку.
    private static readonly string[] SkippedXmlContentTypes =
    {
        "spreadsheetml.worksheet+xml", "spreadsheetml.sharedStrings+xml", "spreadsheetml.pivotCacheRecords+xml",
        "spreadsheetml.calcChain+xml"
    };

    private static readonly Regex LocalPathPattern = new(
        @"^\s*(?:[A-Za-z]:[\\/]|\\\\|file:|/(?:Users|home|Volumes|private|var|tmp|mnt|media)/)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool LooksLikeLocalPath(string? value) => value != null && LocalPathPattern.IsMatch(value);

    /// <summary>Имя файла из локального пути или file:-ссылки; якорь (#...) сохраняется.</summary>
    public static string FileNameOf(string path)
    {
        var fragment = "";
        var hash = path.IndexOf('#');
        if (hash > 0)
        {
            fragment = path[hash..];
            path = path[..hash];
        }
        var name = path.TrimEnd('\\', '/');
        var separator = name.LastIndexOfAny(new[] { '\\', '/' });
        if (separator >= 0) name = name[(separator + 1)..];
        if (name.Length > 1 && name[1] == ':') name = name[2..];
        return (name.Length == 0 ? "file" : name) + fragment;
    }

    public static void Sanitize(OpenXmlPackage package, OpenXmlSanitizeContext context)
    {
        CleanPackageProperties(package.PackageProperties);
        foreach (var extended in package.GetPartsOfType<ExtendedFilePropertiesPart>())
            CleanExtendedProperties(extended.Properties);
        foreach (var part in package.Parts.Select(p => p.OpenXmlPart)
                     .Where(p => p is CustomFilePropertiesPart or ThumbnailPart).ToList())
            package.DeletePart(part);

        var visited = new HashSet<OpenXmlPart>();
        var deletions = new List<(OpenXmlPartContainer Parent, OpenXmlPart Part)>();
        RewriteLocalLinks(package, context);
        foreach (var (parent, part) in Walk(package, visited))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            RewriteLocalLinks(part, context);
            switch (part)
            {
                case CustomXmlPart:
                    deletions.Add((parent, part));
                    break;
                case EmbeddedPackagePart embedded:
                    SanitizeEmbeddedPackage(embedded, context);
                    break;
                case EmbeddedObjectPart ole:
                    SanitizeEmbeddedOle(ole, context);
                    break;
                default:
                    if (part.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                        StripImage(part);
                    else if (IsScannableXml(part))
                        SanitizeDrawingAttributes(part);
                    break;
            }
        }

        foreach (var (parent, part) in deletions)
            parent.DeletePart(part);
    }

    /// <summary>Обходит все части пакета, возвращая каждую один раз вместе с родителем.</summary>
    public static IEnumerable<(OpenXmlPartContainer Parent, OpenXmlPart Part)> Walk(OpenXmlPartContainer container, HashSet<OpenXmlPart> visited)
    {
        foreach (var pair in container.Parts.ToList())
        {
            var part = pair.OpenXmlPart;
            if (!visited.Add(part)) continue;
            yield return (container, part);
            foreach (var nested in Walk(part, visited))
                yield return nested;
        }
    }

    /// <summary>
    /// Ссылки на локальные и сетевые файлы (гиперссылки, связанные картинки и объекты) сокращаются до имени файла:
    /// у получателя абсолютный путь всё равно не откроется, а имя пользователя и структура папок раскрываются.
    /// </summary>
    private static void RewriteLocalLinks(OpenXmlPartContainer container, OpenXmlSanitizeContext context)
    {
        foreach (var hyperlink in container.HyperlinkRelationships.Where(r => r.IsExternal).ToList())
        {
            var target = hyperlink.Uri.OriginalString;
            if (!LooksLikeLocalPath(target)) continue;
            container.DeleteReferenceRelationship(hyperlink);
            container.AddHyperlinkRelationship(new Uri(FileNameOf(Uri.UnescapeDataString(target)), UriKind.Relative), true, hyperlink.Id);
            context.Warn("Ссылки на локальные файлы сокращены до имени файла.");
        }

        foreach (var external in container.ExternalRelationships.ToList())
        {
            var target = external.Uri.OriginalString;
            if (!LooksLikeLocalPath(target)) continue;
            container.DeleteExternalRelationship(external);
            container.AddExternalRelationship(external.RelationshipType, new Uri(FileNameOf(Uri.UnescapeDataString(target)), UriKind.Relative), external.Id);
            context.Warn("Ссылки на локальные файлы сокращены до имени файла.");
        }
    }

    public static void CleanPackageProperties(IPackageProperties properties)
    {
        properties.Creator = null;
        properties.LastModifiedBy = null;
        properties.Title = null;
        properties.Subject = null;
        properties.Keywords = null;
        properties.Description = null;
        properties.Category = null;
        properties.ContentStatus = null;
        properties.ContentType = null;
        properties.Identifier = null;
        properties.Version = null;
        properties.Language = null;

        try { properties.Created = null; } catch { }
        try { properties.Modified = null; } catch { }
        try { properties.LastPrinted = null; } catch { }
        try { properties.Revision = null; } catch { }
    }

    public static void CleanExtendedProperties(DocumentFormat.OpenXml.ExtendedProperties.Properties? properties)
    {
        if (properties == null) return;

        // Оставлять нечего: все поля app.xml — сведения о приложении, авторе и статистике документа.
        properties.RemoveAllChildren();
    }

    /// <summary>XML-часть по типу содержимого (подстрока «xml» есть и в бинарных типах openxmlformats).</summary>
    public static bool IsXmlContentType(string contentType) =>
        contentType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase) ||
        contentType.EndsWith("/xml", StringComparison.OrdinalIgnoreCase) ||
        contentType.EndsWith("vmlDrawing", StringComparison.OrdinalIgnoreCase);

    private static bool IsScannableXml(OpenXmlPart part) =>
        IsXmlContentType(part.ContentType) &&
        !SkippedXmlContentTypes.Any(t => part.ContentType.EndsWith(t, StringComparison.OrdinalIgnoreCase));

    private static void StripImage(OpenXmlPart part)
    {
        byte[] data;
        using (var stream = part.GetStream(FileMode.Open, FileAccess.Read))
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            data = buffer.ToArray();
        }
        var stripped = ImageMetadata.Strip(data);
        if (stripped == null) return;
        using var output = new MemoryStream(stripped);
        part.FeedData(output);
    }

    /// <summary>
    /// Убирает пути к исходным файлам картинок: Word и Excel записывают их в описание (descr), имя фигуры
    /// или VML-атрибуты o:title/o:href.
    /// </summary>
    private static void SanitizeDrawingAttributes(OpenXmlPart part)
    {
        try
        {
            OpenXmlPartXml.Update(part, SanitizeDrawingAttributes);
        }
        catch (XmlException)
        {
            // Устаревший VML бывает невалидным XML; такие части не меняем, аудит сообщит о найденных путях.
        }
    }

    internal static bool SanitizeDrawingAttributes(XDocument document)
    {
        var changed = false;
        foreach (var element in document.Descendants())
        {
            switch (element.Name.LocalName)
            {
                case "docPr":
                case "cNvPr":
                    foreach (var attribute in element.Attributes()
                                 .Where(a => a.Name.LocalName is "descr" or "title" && LooksLikeLocalPath(a.Value)).ToList())
                    {
                        attribute.Remove();
                        changed = true;
                    }
                    var name = element.Attribute("name");
                    if (name != null && LooksLikeLocalPath(name.Value))
                    {
                        name.Value = "Picture " + ((string?)element.Attribute("id") ?? "1");
                        changed = true;
                    }
                    break;
                case "imagedata":
                    foreach (var attribute in element.Attributes()
                                 .Where(a => a.Name.LocalName == "title" || a.Name.LocalName == "href" && LooksLikeLocalPath(a.Value)).ToList())
                    {
                        attribute.Remove();
                        changed = true;
                    }
                    break;
                case "shape":
                    var alt = element.Attribute("alt");
                    if (alt != null && LooksLikeLocalPath(alt.Value))
                    {
                        alt.Remove();
                        changed = true;
                    }
                    break;
            }
        }
        return changed;
    }

    private static void SanitizeEmbeddedOle(EmbeddedObjectPart part, OpenXmlSanitizeContext context)
    {
        var data = ReadAll(part);
        if (!IsCompoundFile(data)) return;
        try
        {
            var scrubbed = OleDocumentCleaner.ScrubCompoundFile(data, context.Warnings, context.CancellationToken);
            using var output = new MemoryStream(scrubbed);
            part.FeedData(output);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Warn($"Не удалось очистить встроенный OLE-объект {part.Uri}: {ex.Message}");
        }
    }

    private static void SanitizeEmbeddedPackage(EmbeddedPackagePart part, OpenXmlSanitizeContext context)
    {
        if (context.Depth + 1 > OpenXmlSanitizeContext.MaxDepth)
        {
            context.Warn($"Слишком глубокая вложенность объектов, {part.Uri} не очищен.");
            return;
        }

        var buffer = new MemoryStream();
        using (var input = part.GetStream(FileMode.Open, FileAccess.Read))
            input.CopyTo(buffer);
        buffer.Position = 0;
        var nested = context.Nested();
        try
        {
            SanitizePackageStream(buffer, part.ContentType, nested);
            OpenXmlZipAttributes.Normalize(buffer);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Warn($"Не удалось очистить встроенный документ {part.Uri}: {ex.Message}");
            return;
        }
        buffer.Position = 0;
        part.FeedData(buffer);
    }

    /// <summary>Очищает OpenXML-пакет в потоке, выбирая обработчик по типу содержимого.</summary>
    internal static void SanitizePackageStream(Stream stream, string contentType, OpenXmlSanitizeContext context)
    {
        if (contentType.Contains("wordprocessingml", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("ms-word", StringComparison.OrdinalIgnoreCase))
        {
            using var document = WordprocessingDocument.Open(stream, true);
            WordPrivacySanitizer.Sanitize(document, context);
        }
        else if (contentType.Contains("spreadsheetml", StringComparison.OrdinalIgnoreCase) ||
                 contentType.Contains("ms-excel", StringComparison.OrdinalIgnoreCase))
        {
            using var document = SpreadsheetDocument.Open(stream, true);
            SpreadsheetPrivacySanitizer.Sanitize(document, context);
        }
        else if (contentType.Contains("presentationml", StringComparison.OrdinalIgnoreCase) ||
                 contentType.Contains("ms-powerpoint", StringComparison.OrdinalIgnoreCase))
        {
            using var document = PresentationDocument.Open(stream, true);
            SanitizePresentation(document, context);
        }
        else
        {
            SanitizeGenericPackage(stream);
        }
    }

    private static void SanitizePresentation(PresentationDocument document, OpenXmlSanitizeContext context)
    {
        var presentation = document.PresentationPart;
        if (presentation != null)
        {
            if (presentation.CommentAuthorsPart != null) presentation.DeletePart(presentation.CommentAuthorsPart);
            if (presentation.authorsPart != null) presentation.DeletePart(presentation.authorsPart);
            foreach (var slide in presentation.SlideParts)
            {
                if (slide.SlideCommentsPart != null) slide.DeletePart(slide.SlideCommentsPart);
                foreach (var comments in slide.GetPartsOfType<PowerPointCommentPart>().ToList()) slide.DeletePart(comments);
            }
        }
        Sanitize(document, context);
    }

    /// <summary>Пакеты неизвестного типа (например, Visio): свойства, пользовательские свойства и изображения.</summary>
    private static void SanitizeGenericPackage(Stream stream)
    {
        using var package = Package.Open(stream, FileMode.Open, FileAccess.ReadWrite);
        var core = package.PackageProperties;
        core.Creator = null; core.LastModifiedBy = null; core.Title = null; core.Subject = null;
        core.Keywords = null; core.Description = null; core.Category = null; core.ContentStatus = null;
        core.Identifier = null; core.Version = null; core.Language = null; core.Revision = null;
        core.Created = null; core.Modified = null; core.LastPrinted = null;

        foreach (var part in package.GetParts().ToList())
        {
            if (part.ContentType == ExtendedPropertiesContentType || part.ContentType == CustomPropertiesContentType)
            {
                var root = part.ContentType == ExtendedPropertiesContentType
                    ? "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties"
                    : "http://schemas.openxmlformats.org/officeDocument/2006/custom-properties";
                using var output = part.GetStream(FileMode.Create, FileAccess.Write);
                new XDocument(new XElement(XName.Get("Properties", root))).Save(output);
            }
            else if (part.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                byte[] data;
                using (var input = part.GetStream(FileMode.Open, FileAccess.Read))
                using (var buffer = new MemoryStream())
                {
                    input.CopyTo(buffer);
                    data = buffer.ToArray();
                }
                var stripped = ImageMetadata.Strip(data);
                if (stripped == null) continue;
                using var output = part.GetStream(FileMode.Create, FileAccess.Write);
                output.Write(stripped);
            }
        }
    }

    internal static bool IsCompoundFile(ReadOnlySpan<byte> data) =>
        data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 });

    private static byte[] ReadAll(OpenXmlPart part)
    {
        using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}

#pragma warning restore OOXML0001
