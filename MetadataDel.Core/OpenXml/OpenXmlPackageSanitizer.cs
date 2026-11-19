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

    public OpenXmlSanitizeContext(CancellationToken cancellationToken = default) : this(new List<string>(), new IncompleteFlag(), 0, cancellationToken) { }

    private OpenXmlSanitizeContext(List<string> warnings, IncompleteFlag incomplete, int depth, CancellationToken cancellationToken)
    {
        Warnings = warnings;
        _incomplete = incomplete;
        Depth = depth;
        CancellationToken = cancellationToken;
    }

    private sealed class IncompleteFlag { public bool Value; }

    private readonly IncompleteFlag _incomplete;

    /// <summary>Часть метаданных осталась в файле (общий признак для всех вложенных документов).</summary>
    public bool Incomplete => _incomplete.Value;

    public List<string> Warnings { get; }

    /// <summary>0 — очищаемый файл, больше 0 — встроенный объект.</summary>
    public int Depth { get; }

    public CancellationToken CancellationToken { get; }

    public OpenXmlSanitizeContext Nested() => new(Warnings, _incomplete, Depth + 1, CancellationToken);

    public void Warn(string warning)
    {
        if (!Warnings.Contains(warning)) Warnings.Add(warning);
    }

    /// <summary>Предупреждение о метаданных, которые остались в файле: результат будет помечен как неполный.</summary>
    public void WarnIncomplete(string warning)
    {
        Warn(warning);
        _incomplete.Value = true;
    }

    /// <summary>Помечает результат неполным, когда предупреждение уже записано (например, очистителем OLE).</summary>
    public void MarkIncomplete() => _incomplete.Value = true;
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

    // Относительный путь с папками (..\, ./) или сетевой путь вида //server/share.
    private static readonly Regex RelativePathPattern = new(@"^\s*(?:\.{1,2}[\\/]|//)", RegexOptions.CultureInvariant);

    // Схема URI: http:, mailto:, ftp: и т. п. Одна буква с двоеточием — это диск Windows, а не схема.
    private static readonly Regex UriSchemePattern = new(@"^\s*[A-Za-z][A-Za-z0-9+.\-]+:", RegexOptions.CultureInvariant);

    // Веб-адрес без схемы (www.example.com/page): это содержимое ссылки, а не путь к файлу.
    private static readonly Regex WebHostPattern = new(
        @"^\s*(?:www\.|[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}(?::\d+)?/)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Имя файла изображения или видео целиком (IMG_2031.JPG, «Снимок экрана … .png»): Word и Excel подставляют
    // его в имя и описание картинки, и оно выдаёт устройство, дату съёмки или имя пользователя.
    private static readonly Regex MediaFileNamePattern = new(
        @"^[^\r\n<>""|?*]{0,255}?\.(?:jpe?g|jpe|jfif|png|gif|bmp|dib|tiff?|heic|heif|webp|avif|emf|wmf|emz|wmz|svg|ico|psd|raw|dng|cr2|cr3|nef|arw|orf|rw2|" +
        @"mp4|m4v|mov|avi|wmv|mkv|webm|3gp|mp3|wav|m4a|wma|aac|flac|ogg)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private const string DigitalSignatureOrigin = "/digital-signature/origin";

    public static bool LooksLikeLocalPath(string? value) => value != null && LocalPathPattern.IsMatch(value);

    /// <summary>Абсолютный локальный или сетевой путь либо относительный путь с папками.</summary>
    public static bool LooksLikeLocalOrRelativePath(string? value) =>
        value != null && (LocalPathPattern.IsMatch(value) || RelativePathPattern.IsMatch(value));

    /// <summary>Значение целиком — имя медиафайла (с путём или без).</summary>
    public static bool LooksLikeMediaFileName(string? value) => value != null && MediaFileNamePattern.IsMatch(value);

    /// <summary>
    /// Имя или описание объекта, которое на самом деле является путём или именем исходного файла.
    /// Одно правило для очистки и для проверки, чтобы проверка не находила то, что очистка оставляет.
    /// </summary>
    public static bool IsIdentifyingFileReference(string? value) => LooksLikeLocalPath(value) || LooksLikeMediaFileName(value);

    /// <summary>
    /// Внешняя ссылка на файл, а не на ресурс по URI-схеме (http, mailto и т. п.): абсолютный или относительный путь.
    /// </summary>
    public static bool IsExternalFilePath(string target) =>
        LooksLikeLocalPath(target) ||
        target.Length > 0 && !target.StartsWith('#') && !UriSchemePattern.IsMatch(target) && !WebHostPattern.IsMatch(target);

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
        RemoveDigitalSignatures(package, context);

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
                    WordPrivacySanitizer.WarnIfBibliography(part, context);
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
                        StripImage(part, context);
                    else if (IsScannableXml(part))
                        SanitizeDrawingAttributes(part, context);
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
    /// Относительные пути (..\..\Отдел\Файл.xlsx) раскрывают структуру папок так же. Ссылки со схемой URI
    /// (http, https, mailto, ftp и т. п.) — содержимое документа, их не трогаем; якорь (#...) сохраняется.
    /// </summary>
    private static void RewriteLocalLinks(OpenXmlPartContainer container, OpenXmlSanitizeContext context)
    {
        foreach (var hyperlink in container.HyperlinkRelationships.Where(r => r.IsExternal).ToList())
        {
            if (ShortenedTarget(hyperlink.Uri) is not { } shortened) continue;
            container.DeleteReferenceRelationship(hyperlink);
            container.AddHyperlinkRelationship(shortened, true, hyperlink.Id);
            context.Warn("Ссылки на локальные файлы сокращены до имени файла.");
        }

        foreach (var external in container.ExternalRelationships.ToList())
        {
            if (ShortenedTarget(external.Uri) is not { } shortened) continue;
            container.DeleteExternalRelationship(external);
            container.AddExternalRelationship(external.RelationshipType, shortened, external.Id);
            context.Warn("Ссылки на локальные файлы сокращены до имени файла.");
        }
    }

    private static Uri? ShortenedTarget(Uri uri)
    {
        var target = uri.OriginalString;
        if (!IsExternalFilePath(target)) return null;
        var name = FileNameOf(Uri.UnescapeDataString(target));
        if (name == target || name == Uri.UnescapeDataString(target)) return null;
        return new Uri(name, UriKind.Relative);
    }

    /// <summary>
    /// Цифровая подпись после очистки всё равно недействительна, а её части хранят сертификат подписавшего
    /// (имя, адрес почты, издатель) и время подписания.
    /// </summary>
    private static void RemoveDigitalSignatures(OpenXmlPackage package, OpenXmlSanitizeContext context)
    {
        var signatures = package.Parts.Select(p => p.OpenXmlPart)
            .Where(p => p is DigitalSignatureOriginPart || p.RelationshipType.EndsWith(DigitalSignatureOrigin, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var part in signatures)
            package.DeletePart(part);
        if (signatures.Count > 0) context.Warn(DigitalSignatureWarning);
    }

    private const string DigitalSignatureWarning =
        "Цифровая подпись удалена: после очистки она стала бы недействительной, а сертификат раскрывает имя и адрес подписавшего и время подписания. " +
        "При необходимости подпишите очищенный документ заново.";

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

    private static void StripImage(OpenXmlPart part, OpenXmlSanitizeContext context)
    {
        byte[] data;
        using (var stream = part.GetStream(FileMode.Open, FileAccess.Read))
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            data = buffer.ToArray();
        }
        var stripped = StripImageData(data, context);
        if (stripped == null) return;
        using var output = new MemoryStream(stripped);
        part.FeedData(output);
    }

    /// <summary>
    /// Снимает метаданные изображения. Повреждённые картинки и форматы, которые можно только распознать
    /// (HEIC/AVIF с EXIF или XMP), помечают результат неполным: часть сведений могла остаться.
    /// </summary>
    private static byte[]? StripImageData(byte[] data, OpenXmlSanitizeContext context)
    {
        var result = ImageMetadata.StripDetailed(data);
        if (result.Status == ImageStripStatus.Malformed)
            context.WarnIncomplete("Часть изображений повреждена: метаданные удалены из читаемой части, остаток проверить не удалось.");
        else if (result.Status == ImageStripStatus.UnsupportedWithMetadata || result.ResidualMetadata)
            context.WarnIncomplete("В документе есть изображения (например, HEIC или AVIF) с метаданными, которые не удаляются: EXIF или XMP в них сохранены.");
        return result.Data;
    }

    /// <summary>
    /// Убирает пути и имена исходных файлов картинок: Word и Excel записывают их в описание (descr), заголовок,
    /// имя фигуры или VML-атрибуты o:title/o:href/alt, а также GUID создания объектов (a16:creationId).
    /// </summary>
    private static void SanitizeDrawingAttributes(OpenXmlPart part, OpenXmlSanitizeContext context)
    {
        try
        {
            OpenXmlPartXml.Update(part, SanitizeDrawingAttributes);
        }
        catch (XmlException)
        {
            // Устаревший VML бывает невалидным XML; такие части не меняем и честно сообщаем, что не проверили их.
            context.WarnIncomplete($"Часть {part.Uri} не разбирается как XML: имена и пути к исходным файлам в ней не проверены.");
        }
    }

    private const string Office2014DrawingNamespace = "http://schemas.microsoft.com/office/drawing/2014/main";

    internal static bool SanitizeDrawingAttributes(XDocument document)
    {
        var changed = false;
        foreach (var element in document.Descendants())
        {
            var local = element.Name.LocalName;
            foreach (var attribute in element.Attributes()
                         .Where(a => !a.IsNamespaceDeclaration &&
                                     (a.Name.LocalName is "descr" or "title" or "href" or "alt" && IsIdentifyingFileReference(a.Value) ||
                                      local == "imagedata" && a.Name.LocalName == "title"))
                         .ToList())
            {
                attribute.Remove();
                changed = true;
            }

            if (local is "docPr" or "cNvPr")
            {
                // Обычные имена Word («Рисунок 3», «Picture 3») не трогаем: заменяются только имена и пути файлов.
                var name = element.Attribute("name");
                if (name != null && IsIdentifyingFileReference(name.Value))
                {
                    name.Value = "Picture " + ((string?)element.Attribute("id") ?? "1");
                    changed = true;
                }
            }
        }

        // a16:creationId и a16:predDERef — GUID, созданные при вставке объекта; по ним связываются копии документа.
        foreach (var identifier in document.Descendants()
                     .Where(e => e.Name.Namespace == Office2014DrawingNamespace && e.Name.LocalName is "creationId" or "predDERef" &&
                                 e.Parent?.Name.LocalName == "ext")
                     .ToList())
        {
            var extension = identifier.Parent!;
            identifier.Remove();
            changed = true;
            if (extension.HasElements) continue;
            var list = extension.Parent;
            extension.Remove();
            if (list?.Name.LocalName == "extLst" && !list.HasElements) list.Remove();
        }
        return changed;
    }

    private static void SanitizeEmbeddedOle(EmbeddedObjectPart part, OpenXmlSanitizeContext context)
    {
        var data = ReadAll(part);
        if (!IsCompoundFile(data)) return;
        try
        {
            var scrubbed = OleDocumentCleaner.ScrubCompoundFile(data, context.Warnings, out var incomplete, context.CancellationToken);
            using var output = new MemoryStream(scrubbed);
            part.FeedData(output);
            // Предупреждение уже добавлено очистителем OLE; здесь только переносим признак неполной очистки.
            if (incomplete) context.MarkIncomplete();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.WarnIncomplete($"Не удалось очистить встроенный OLE-объект {part.Uri}: {ex.Message}");
        }
    }

    private static void SanitizeEmbeddedPackage(EmbeddedPackagePart part, OpenXmlSanitizeContext context)
    {
        if (context.Depth + 1 > OpenXmlSanitizeContext.MaxDepth)
        {
            context.WarnIncomplete($"Слишком глубокая вложенность объектов, {part.Uri} не очищен.");
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
            context.WarnIncomplete($"Не удалось очистить встроенный документ {part.Uri}: {ex.Message}");
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
            SanitizeGenericPackage(stream, context);
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
    private static void SanitizeGenericPackage(Stream stream, OpenXmlSanitizeContext context)
    {
        using var package = Package.Open(stream, FileMode.Open, FileAccess.ReadWrite);
        var signatureRelationships = package.GetRelationships()
            .Where(r => r.RelationshipType.EndsWith(DigitalSignatureOrigin, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var relationship in signatureRelationships)
            package.DeleteRelationship(relationship.Id);
        var signatureParts = package.GetParts()
            .Where(p => p.ContentType.StartsWith("application/vnd.openxmlformats-package.digital-signature", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var part in signatureParts)
            package.DeletePart(part.Uri);
        if (signatureRelationships.Count + signatureParts.Count > 0) context.Warn(DigitalSignatureWarning);

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
                var stripped = StripImageData(data, context);
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
