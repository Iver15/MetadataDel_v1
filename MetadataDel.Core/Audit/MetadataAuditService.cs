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
            _ => new MetadataAuditResult(path, extension, Array.Empty<MetadataAuditFinding>())
        };
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

        return new MetadataAuditResult(path, "pdf", findings);
    }

    private static MetadataAuditResult AuditDocx(string path)
    {
        var findings = new List<string>();
        using var document = WordprocessingDocument.Open(path, false);
        WordPrivacySanitizer.HasResidualMetadata(document, findings);
        return new MetadataAuditResult(path, "docx", findings.Select((f, i) => new MetadataAuditFinding($"docx.{i}", f)).ToList());
    }

    private static MetadataAuditResult AuditXlsx(string path)
    {
        var findings = new List<string>();
        using var document = SpreadsheetDocument.Open(path, false);
        SpreadsheetPrivacySanitizer.HasResidualMetadata(document, findings);
        return new MetadataAuditResult(path, "xlsx", findings.Select((f, i) => new MetadataAuditFinding($"xlsx.{i}", f)).ToList());
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
