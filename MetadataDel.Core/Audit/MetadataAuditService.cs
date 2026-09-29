using DocumentFormat.OpenXml.Packaging;
using iText.Forms;
using iText.Kernel.Pdf;
using MetadataDel.Core.OpenXml;

namespace MetadataDel.Core.Audit;

/// <summary>
/// Найденный признак остаточных метаданных.
/// </summary>
public sealed record MetadataAuditFinding(string Code, string Description);

/// <summary>
/// Результат аудита метаданных для одного файла.
/// </summary>
public sealed record MetadataAuditResult(string Path, string Format, IReadOnlyList<MetadataAuditFinding> Findings)
{
    /// <summary>
    /// Возвращает <c>true</c>, если найдены признаки метаданных или служебных следов.
    /// </summary>
    public bool HasSensitiveMetadata => Findings.Count > 0;

    /// <summary>
    /// Возвращает краткую человекочитаемую сводку найденных признаков.
    /// </summary>
    public string Summarize(int maxItems = 5)
    {
        if (Findings.Count == 0)
        {
            return "следов метаданных не найдено";
        }

        var items = Findings
            .Select(f => f.Description)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxItems)
            .ToList();

        var suffix = Findings.Count > maxItems ? $" и ещё {Findings.Count - maxItems}" : string.Empty;
        return string.Join(", ", items) + suffix;
    }
}

/// <summary>
/// Выполняет аудит остаточных метаданных для поддерживаемых форматов.
/// </summary>
public static class MetadataAuditService
{
    /// <summary>
    /// Анализирует файл и возвращает список найденных признаков метаданных.
    /// </summary>
    public static MetadataAuditResult Audit(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".pdf" => AuditPdf(path),
            ".docx" => AuditDocx(path),
            ".xlsx" => AuditXlsx(path),
            ".doc" or ".xls" => AuditOle(path, extension),
            _ => throw new NotSupportedException($"Аудит формата {extension} не поддерживается.")
        };
    }

    private static MetadataAuditResult AuditOle(string path, string extension)
    {
        // Encrypted or malformed Word/Excel streams cannot be inspected; the cleaner reports them as warnings.
        using var root = OpenMcdf.RootStorage.OpenRead(path);
        var findings = Ole.OleDocumentCleaner.Inspect(root)
            .Select(f => new MetadataAuditFinding(f.Code, f.Description))
            .ToList();
        return new MetadataAuditResult(path, extension.TrimStart('.'), findings);
    }

    private static MetadataAuditResult AuditPdf(string path)
    {
        var findings = new List<MetadataAuditFinding>();

        using var document = new PdfDocument(new PdfReader(path));
        var info = document.GetDocumentInfo();
        AddIf(findings, HasText(info.GetAuthor()), "pdf.info.author", "автор PDF");
        AddIf(findings, HasText(info.GetCreator()), "pdf.info.creator", "creator PDF");
        AddIf(findings, HasText(info.GetTitle()), "pdf.info.title", "заголовок PDF");
        AddIf(findings, HasText(info.GetSubject()), "pdf.info.subject", "subject PDF");
        AddIf(findings, HasText(info.GetKeywords()), "pdf.info.keywords", "keywords PDF");
        AddIf(findings, HasText(info.GetProducer()), "pdf.info.producer", "producer PDF");

        var infoDictionary = document.GetTrailer().GetAsDictionary(PdfName.Info);
        foreach (var key in infoDictionary?.KeySet() ?? (ICollection<PdfName>)Array.Empty<PdfName>())
        {
            if (key == PdfName.Author || key == PdfName.Creator || key == PdfName.Title ||
                key == PdfName.Subject || key == PdfName.Keywords || key == PdfName.Producer) continue;
            AddIf(findings, HasText(infoDictionary!.Get(key)?.ToString()), "pdf.info.other", "свойство PDF " + key);
        }
        for (var page = 1; page <= document.GetNumberOfPages(); page++)
        {
            var dictionary = document.GetPage(page).GetPdfObject();
            AddIf(findings, dictionary.ContainsKey(PdfName.Metadata) || dictionary.ContainsKey(new PdfName("PieceInfo")) ||
                dictionary.ContainsKey(PdfName.AA) || dictionary.ContainsKey(new PdfName("AF")), "pdf.page.metadata", "метаданные страницы PDF");
        }

        var xmp = document.GetXmpMetadata();
        AddIf(findings, xmp is { Length: > 0 }, "pdf.xmp", "XMP-метаданные PDF");

        var catalog = document.GetCatalog();
        AddIf(findings, catalog.GetPdfObject().ContainsKey(PdfName.Metadata), "pdf.catalog.metadata", "каталожная ссылка на metadata");
        AddIf(findings, catalog.GetPdfObject().ContainsKey(PdfName.OpenAction), "pdf.catalog.openAction", "OpenAction PDF");
        AddIf(findings, catalog.GetPdfObject().ContainsKey(PdfName.AA), "pdf.catalog.additionalActions", "additional actions PDF");
        AddIf(findings, catalog.GetPdfObject().ContainsKey(PdfName.StructTreeRoot), "pdf.catalog.structure", "структурное дерево PDF");
        AddIf(findings, catalog.GetPdfObject().ContainsKey(new PdfName("AF")), "pdf.catalog.associatedFiles", "associated files PDF");
        AddIf(findings, HasEmbeddedFiles(document), "pdf.embedded", "вложения PDF");
        AddIf(findings, HasAnnotations(document), "pdf.annotations", "аннотации PDF");
        AddIf(findings, PdfAcroForm.GetAcroForm(document, false) != null, "pdf.form", "формы PDF");
        AddIf(findings, catalog.GetPdfObject().ContainsKey(Pdf.PdfCleaner.Threads) || catalog.GetPdfObject().ContainsKey(Pdf.PdfCleaner.SpiderInfo),
            "pdf.catalog.threads", "статьи или данные веб-захвата PDF");

        AuditPdfObjects(document, findings);
        AuditPdfLayers(catalog.GetPdfObject(), findings);

        var raw = File.ReadAllBytes(path);
        AddIf(findings, raw.AsSpan().IndexOf("%iText"u8) >= 0, "pdf.producerComment", "служебный комментарий с версией iText");
        AddIf(findings, HasPreviousRevisions(document, raw), "pdf.revisions",
            "предыдущие версии PDF (инкрементальные обновления)");

        return new MetadataAuditResult(path, "pdf", findings);
    }

    private static MetadataAuditResult AuditDocx(string path)
    {
        var findings = new List<string>();
        using (var document = WordprocessingDocument.Open(path, false))
            WordPrivacySanitizer.HasResidualMetadata(document, findings);
        return new MetadataAuditResult(path, "docx", WithPackageScan(path, findings.Select((f, i) => new MetadataAuditFinding($"docx.{i}", f))));
    }

    private static MetadataAuditResult AuditXlsx(string path)
    {
        var findings = new List<string>();
        using (var document = SpreadsheetDocument.Open(path, false))
            SpreadsheetPrivacySanitizer.HasResidualMetadata(document, findings);
        return new MetadataAuditResult(path, "xlsx", WithPackageScan(path, findings.Select((f, i) => new MetadataAuditFinding($"xlsx.{i}", f))));
    }

    // Независимая проверка ZIP-архива дополняет проверки типизированных частей.
    private static List<MetadataAuditFinding> WithPackageScan(string path, IEnumerable<MetadataAuditFinding> typed)
    {
        var findings = typed.ToList();
        using var stream = File.OpenRead(path);
        PackageResidueScanner.Scan(stream, findings);
        return findings;
    }

    private static void AuditPdfObjects(PdfDocument document, ICollection<MetadataAuditFinding> findings)
    {
        bool metadata = false, pieceInfo = false, modified = false, thumbnails = false, images = false;
        for (var number = 1; number < document.GetNumberOfPdfObjects(); number++)
        {
            if (document.GetPdfObject(number) is not PdfDictionary dictionary) continue;
            metadata |= dictionary.ContainsKey(PdfName.Metadata);
            pieceInfo |= dictionary.ContainsKey(Pdf.PdfCleaner.PieceInfo);
            modified |= dictionary.ContainsKey(Pdf.PdfCleaner.LastModified);
            thumbnails |= dictionary.ContainsKey(Pdf.PdfCleaner.Thumb);
            if (!images && dictionary is PdfStream stream && PdfName.Image.Equals(stream.GetAsName(PdfName.Subtype)) &&
                Pdf.PdfCleaner.GetSingleFilter(stream) is { } filter && (filter.Equals(PdfName.DCTDecode) || filter.Equals(PdfName.JPXDecode)))
                images = Media.ImageMetadata.HasMetadata(stream.GetBytes(false));
        }
        AddIf(findings, metadata, "pdf.objects.metadata", "XMP-метаданные объектов PDF (изображений, форм, страниц)");
        AddIf(findings, pieceInfo, "pdf.objects.pieceInfo", "данные приложений PDF (PieceInfo)");
        AddIf(findings, modified, "pdf.objects.lastModified", "даты изменения объектов PDF");
        AddIf(findings, thumbnails, "pdf.objects.thumbnails", "миниатюры страниц PDF");
        AddIf(findings, images, "pdf.images", "метаданные изображений в PDF (EXIF, GPS, XMP)");
    }

    private static void AuditPdfLayers(PdfDictionary catalog, ICollection<MetadataAuditFinding> findings)
    {
        var properties = catalog.GetAsDictionary(PdfName.OCProperties);
        if (properties == null) return;
        var groups = properties.GetAsArray(PdfName.OCGs);
        var named = false;
        for (var i = 0; i < (groups?.Size() ?? 0); i++)
        {
            var group = groups!.GetAsDictionary(i);
            named |= group != null && (group.ContainsKey(Pdf.PdfCleaner.Usage) ||
                                       group.GetAsString(PdfName.Name)?.ToUnicodeString() != $"Layer {i + 1}");
        }
        var configurations = new List<PdfDictionary?> { properties.GetAsDictionary(PdfName.D) };
        var extra = properties.GetAsArray(PdfName.Configs);
        for (var i = 0; i < (extra?.Size() ?? 0); i++) configurations.Add(extra!.GetAsDictionary(i));
        named |= configurations.OfType<PdfDictionary>().Any(c => c.ContainsKey(PdfName.Name) || c.ContainsKey(PdfName.Creator));
        AddIf(findings, named, "pdf.layers", "имена слоёв PDF и сведения о создавшем их приложении");
    }

    // Старые версии остаются в файле после сохранения поверх (в том числе после exiftool и Acrobat).
    private static bool HasPreviousRevisions(PdfDocument document, byte[] raw)
    {
        if (!document.GetTrailer().ContainsKey(PdfName.Prev)) return false;
        var linearized = raw.AsSpan(0, Math.Min(raw.Length, 2048)).IndexOf("/Linearized"u8) >= 0;
        var endMarkers = 0;
        for (var index = 0; ; index++)
        {
            var found = raw.AsSpan(index).IndexOf("%%EOF"u8);
            if (found < 0) break;
            endMarkers++;
            index += found;
        }
        // Линеаризованный файл без правок содержит две секции xref и два маркера %%EOF.
        return !linearized || endMarkers > 2;
    }

    private static bool HasEmbeddedFiles(PdfDocument document)
    {
        var names = document.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.Names);
        return names?.GetAsDictionary(PdfName.EmbeddedFiles) != null;
    }

    private static bool HasAnnotations(PdfDocument document)
    {
        for (var pageNumber = 1; pageNumber <= document.GetNumberOfPages(); pageNumber++)
        {
            if (document.GetPage(pageNumber).GetAnnotations().Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void AddIf(ICollection<MetadataAuditFinding> findings, bool condition, string code, string description)
    {
        if (condition)
        {
            findings.Add(new MetadataAuditFinding(code, description));
        }
    }

    private static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);
}
