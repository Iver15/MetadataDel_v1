using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.CustomProperties;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.VariantTypes;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Action;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Filespec;
using iText.Kernel.XMP;
using iText.Forms;
using iText.Forms.Fields;
using MetadataDel.Core.Audit;
using MetadataDel.Core.Cleaning;
using MetadataDel.Core.Excel;
using MetadataDel.Core.Pdf;
using MetadataDel.Core.Word;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Xunit;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace MetadataDel.Core.Tests;

public sealed class MetadataCleaningTests : IDisposable
{
    private readonly string _tempDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mdel-tests", Guid.NewGuid().ToString("N"));

    public MetadataCleaningTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task DocxCleaner_RemovesResidualMetadata()
    {
        var path = System.IO.Path.Combine(_tempDirectory, "sample.docx");
        CreateDocx(path);

        Assert.True(MetadataAuditService.Audit(path).HasSensitiveMetadata);

        var cleaner = new DocxCleaner();
        var result = await cleaner.CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.False(MetadataAuditService.Audit(path).HasSensitiveMetadata, MetadataAuditService.Audit(path).Summarize());
    }

    [Fact]
    public async Task ExcelCleaner_RemovesResidualMetadata()
    {
        var path = System.IO.Path.Combine(_tempDirectory, "sample.xlsx");
        CreateXlsx(path);

        Assert.True(MetadataAuditService.Audit(path).HasSensitiveMetadata);

        var cleaner = new ExcelCleaner();
        var result = await cleaner.CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.False(MetadataAuditService.Audit(path).HasSensitiveMetadata, MetadataAuditService.Audit(path).Summarize());
    }

    [Fact]
    public async Task PdfCleaner_RemovesResidualMetadata()
    {
        var path = System.IO.Path.Combine(_tempDirectory, "sample.pdf");
        CreatePdf(path);

        Assert.True(MetadataAuditService.Audit(path).HasSensitiveMetadata);

        var cleaner = new PdfCleaner();
        var result = await cleaner.CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.False(MetadataAuditService.Audit(path).HasSensitiveMetadata, MetadataAuditService.Audit(path).Summarize());
    }

    [Fact]
    public async Task DocxCleaner_RemovesHeaderFooterAndStructuredMetadata()
    {
        var path = System.IO.Path.Combine(_tempDirectory, "structured.docx");
        CreateStructuredDocx(path);

        Assert.True(MetadataAuditService.Audit(path).HasSensitiveMetadata);

        var cleaner = new DocxCleaner();
        var result = await cleaner.CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.False(MetadataAuditService.Audit(path).HasSensitiveMetadata, MetadataAuditService.Audit(path).Summarize());

        var documentXml = ReadZipEntry(path, "word/document.xml");
        var headerXml = ReadZipEntry(path, "word/header1.xml");
        Assert.DoesNotContain("alias", documentXml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tag", documentXml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("author=", headerXml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExcelCleaner_RemovesWorkbookLevelArtifacts()
    {
        var path = System.IO.Path.Combine(_tempDirectory, "workbook-artifacts.xlsx");
        CreateWorkbookArtifactXlsx(path);

        Assert.True(MetadataAuditService.Audit(path).HasSensitiveMetadata);

        var cleaner = new ExcelCleaner();
        var result = await cleaner.CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.False(MetadataAuditService.Audit(path).HasSensitiveMetadata, MetadataAuditService.Audit(path).Summarize());

        var workbookXml = ReadZipEntry(path, "xl/workbook.xml");
        Assert.DoesNotContain("externalReferences", workbookXml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[OtherWorkbook.xlsx]", workbookXml, StringComparison.OrdinalIgnoreCase);
        Assert.False(ZipEntryExists(path, "customXml/item1.xml"));
    }

    [Fact]
    public async Task ExcelCleaner_RemovesWorkbookAbsolutePath()
    {
        var path = System.IO.Path.Combine(_tempDirectory, "workbook-absolute-path.xlsx");
        CreateWorkbookWithAbsolutePathXlsx(path);

        Assert.True(MetadataAuditService.Audit(path).HasSensitiveMetadata);

        var cleaner = new ExcelCleaner();
        var result = await cleaner.CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.False(MetadataAuditService.Audit(path).HasSensitiveMetadata, MetadataAuditService.Audit(path).Summarize());

        var workbookXml = ReadZipEntry(path, "xl/workbook.xml");
        Assert.DoesNotContain("absPath", workbookXml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\MetadataDelTest", workbookXml, StringComparison.OrdinalIgnoreCase);
        Assert.All(ReadZipEntryExternalAttributes(path), attributes =>
            Assert.NotEqual(0, attributes & unchecked((int)0xFFFF0000)));
        Assert.All(ReadZipEntryLastWriteTimes(path), lastWriteTime =>
            Assert.Equal(1980, lastWriteTime.Year));
    }

    [Fact]
    public async Task PdfCleaner_AggressiveMode_RemovesResidualMetadata()
    {
        var path = System.IO.Path.Combine(_tempDirectory, "aggressive.pdf");
        CreatePdf(path);

        Assert.True(MetadataAuditService.Audit(path).HasSensitiveMetadata);

        var cleaner = new PdfCleaner();
        var result = await cleaner.CleanAsync(path, new CleanOptions(AggressivePdf: true));

        Assert.True(result.Success, result.Message);
        Assert.False(MetadataAuditService.Audit(path).HasSensitiveMetadata, MetadataAuditService.Audit(path).Summarize());
    }

    [Fact]
    public async Task PdfCleaner_RemovesComplexPdfArtifacts()
    {
        var path = System.IO.Path.Combine(_tempDirectory, "complex.pdf");
        CreateComplexPdf(path);

        Assert.True(MetadataAuditService.Audit(path).HasSensitiveMetadata);

        var cleaner = new PdfCleaner();
        var result = await cleaner.CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.False(MetadataAuditService.Audit(path).HasSensitiveMetadata, MetadataAuditService.Audit(path).Summarize());

        using var document = new PdfDocument(new PdfReader(path));
        var catalog = document.GetCatalog().GetPdfObject();

        Assert.Null(document.GetXmpMetadata());
        Assert.False(catalog.ContainsKey(PdfName.Metadata));
        Assert.False(catalog.ContainsKey(PdfName.OpenAction));
        Assert.False(catalog.ContainsKey(PdfName.AA));
        Assert.False(catalog.ContainsKey(PdfName.StructTreeRoot));
        Assert.False(catalog.ContainsKey(PdfName.MarkInfo));
        Assert.False(catalog.ContainsKey(new PdfName("AF")));
        Assert.Null(catalog.GetAsDictionary(PdfName.Names)?.GetAsDictionary(PdfName.EmbeddedFiles));
        Assert.Null(PdfAcroForm.GetAcroForm(document, false));
        Assert.Empty(document.GetFirstPage().GetAnnotations());
    }

    [Fact]
    public async Task PdfCleaner_CreatesCleanedCopy_WhenOriginalPdfIsLocked()
    {
        var path = System.IO.Path.Combine(_tempDirectory, "locked.pdf");
        CreatePdf(path);

        using var lockStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var cleaner = new PdfCleaner();
        var result = await cleaner.CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.NotEqual(path, result.Path);
        Assert.Contains(".MetadataDel.cleaned.pdf", result.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("очищенная копия", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(result.Path));
        Assert.True(MetadataAuditService.Audit(path).HasSensitiveMetadata);
        Assert.False(MetadataAuditService.Audit(result.Path).HasSensitiveMetadata, MetadataAuditService.Audit(result.Path).Summarize());
    }

    private static void CreateDocx(string path)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        mainPart.Document = new W.Document(
            new W.Body(
                new W.Paragraph(
                    new W.Run(new W.Text("Visible text")),
                    new W.InsertedRun(
                        new W.Run(new W.Text("Inserted secret")))
                    {
                        Author = "Tester",
                        Date = DateTime.Parse("2025-01-01T00:00:00Z")
                    },
                    new W.CommentRangeStart { Id = "0" },
                    new W.Run(new W.Text("Commented")),
                    new W.CommentRangeEnd { Id = "0" },
                    new W.Run(new W.CommentReference { Id = "0" }))));

        var commentsPart = mainPart.AddNewPart<WordprocessingCommentsPart>();
        commentsPart.Comments = new W.Comments(
            new W.Comment
            {
                Id = "0",
                Author = "Tester",
                Initials = "TT",
                Date = DateTime.Parse("2025-01-01T00:00:00Z")
            });

        var settingsPart = mainPart.AddNewPart<DocumentSettingsPart>();
        settingsPart.Settings = new W.Settings(
            new W.TrackRevisions(),
            new W.Rsids(new W.RsidRoot { Val = "00112233" }),
            new W.DocumentVariables(new W.DocumentVariable { Name = "Owner", Val = "Tester" }));

        var customXmlPart = mainPart.AddCustomXmlPart(CustomXmlPartType.CustomXml);
        using (var writer = new StreamWriter(customXmlPart.GetStream(FileMode.Create, FileAccess.Write)))
        {
            writer.Write("<root><owner>Tester</owner></root>");
        }

        document.PackageProperties.Creator = "Tester";
        document.PackageProperties.LastModifiedBy = "Editor";
        document.PackageProperties.Description = "Sensitive";

        var customPropsPart = document.AddCustomFilePropertiesPart();
        var docxProperty = new CustomDocumentProperty
        {
            Name = "Owner",
            FormatId = "{D5CDD505-2E9C-101B-9397-08002B2CF9AE}",
            PropertyId = 2
        };
        docxProperty.AppendChild(new VTLPWSTR("Tester"));
        customPropsPart.Properties = new Properties(docxProperty);

        mainPart.Document.Save();
    }

    private static void CreateStructuredDocx(string path)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        var headerPart = mainPart.AddNewPart<HeaderPart>();
        var footerPart = mainPart.AddNewPart<FooterPart>();

        headerPart.Header = new W.Header(
            new W.Paragraph(
                new W.InsertedRun(new W.Run(new W.Text("Header revision")))
                {
                    Author = "HeaderUser",
                    Date = DateTime.Parse("2025-02-01T00:00:00Z")
                }));

        footerPart.Footer = new W.Footer(
            new W.Paragraph(new W.Run(new W.Text("Footer text"))));

        mainPart.Document = new W.Document(
            new W.Body(
                new W.SdtBlock(
                    new W.SdtProperties(
                        new W.SdtAlias { Val = "Owner Alias" },
                        new W.Tag { Val = "SensitiveTag" }),
                    new W.SdtContentBlock(
                        new W.Paragraph(new W.Run(new W.Text("Structured content"))))),
                new W.Paragraph(new W.Run(new W.Text("Main body"))),
                new W.SectionProperties(
                    new W.HeaderReference
                    {
                        Type = W.HeaderFooterValues.Default,
                        Id = mainPart.GetIdOfPart(headerPart)
                    },
                    new W.FooterReference
                    {
                        Type = W.HeaderFooterValues.Default,
                        Id = mainPart.GetIdOfPart(footerPart)
                    })));

        var settingsPart = mainPart.AddNewPart<DocumentSettingsPart>();
        settingsPart.Settings = new W.Settings(
            new W.TrackRevisions(),
            new W.Rsids(new W.RsidRoot { Val = "ABCDEF01" }),
            new W.DocumentVariables(new W.DocumentVariable { Name = "Owner", Val = "HeaderUser" }));

        var customXmlPart = mainPart.AddCustomXmlPart(CustomXmlPartType.CustomXml);
        using (var writer = new StreamWriter(customXmlPart.GetStream(FileMode.Create, FileAccess.Write)))
        {
            writer.Write("<root><tag>HeaderUser</tag></root>");
        }

        document.PackageProperties.Creator = "HeaderUser";
        document.PackageProperties.LastModifiedBy = "Editor";

        var customPropsPart = document.AddCustomFilePropertiesPart();
        var property = new CustomDocumentProperty
        {
            Name = "Department",
            FormatId = "{D5CDD505-2E9C-101B-9397-08002B2CF9AE}",
            PropertyId = 2
        };
        property.AppendChild(new VTLPWSTR("Finance"));
        customPropsPart.Properties = new Properties(property);

        mainPart.Document.Save();
        headerPart.Header.Save();
        footerPart.Footer.Save();
    }

    private static void CreateXlsx(string path)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();

        var sheetData = new S.SheetData(
            new S.Row(
                new S.Cell
                {
                    CellReference = "A1",
                    DataType = S.CellValues.String,
                    CellValue = new S.CellValue("Cell")
                }));
        worksheetPart.Worksheet = new S.Worksheet(sheetData);

        var commentsPart = worksheetPart.AddNewPart<WorksheetCommentsPart>();
        commentsPart.Comments = new S.Comments(
            new S.Authors(new S.Author("Tester")),
            new S.CommentList(
                new S.Comment
                {
                    Reference = "A1",
                    AuthorId = 0U,
                    CommentText = new S.CommentText(new S.Run(new S.Text("Sensitive comment")))
                }));

        var connectionsPart = workbookPart.AddNewPart<ConnectionsPart>();
        connectionsPart.Connections = new S.Connections(
            new S.Connection
            {
                Id = 1U,
                Name = "ExternalConnection",
                Description = "Sensitive"
            });

        workbookPart.Workbook = new S.Workbook(
            new S.Sheets(
                new S.Sheet
                {
                    Name = "Sheet1",
                    SheetId = 1U,
                    Id = workbookPart.GetIdOfPart(worksheetPart)
                }));

        document.PackageProperties.Creator = "Tester";
        document.PackageProperties.LastModifiedBy = "Editor";
        document.PackageProperties.Description = "Sensitive";

        var customPropsPart = document.AddCustomFilePropertiesPart();
        var xlsxProperty = new CustomDocumentProperty
        {
            Name = "Owner",
            FormatId = "{D5CDD505-2E9C-101B-9397-08002B2CF9AE}",
            PropertyId = 2
        };
        xlsxProperty.AppendChild(new VTLPWSTR("Tester"));
        customPropsPart.Properties = new Properties(xlsxProperty);

        workbookPart.Workbook.Save();
    }

    private static void CreateWorkbookArtifactXlsx(string path)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();

        worksheetPart.Worksheet = new S.Worksheet(
            new S.SheetData(
                new S.Row(
                    new S.Cell
                    {
                        CellReference = "A1",
                        DataType = S.CellValues.String,
                        CellValue = new S.CellValue("Report")
                    })));

        var commentsPart = worksheetPart.AddNewPart<WorksheetCommentsPart>();
        commentsPart.Comments = new S.Comments(
            new S.Authors(new S.Author("WorkbookUser")),
            new S.CommentList(
                new S.Comment
                {
                    Reference = "A1",
                    AuthorId = 0U,
                    CommentText = new S.CommentText(new S.Run(new S.Text("Private note")))
                }));

        var connectionsPart = workbookPart.AddNewPart<ConnectionsPart>();
        connectionsPart.Connections = new S.Connections(
            new S.Connection
            {
                Id = 1U,
                Name = "CRMExport",
                Description = "External source"
            });

        var customXmlPart = workbookPart.AddCustomXmlPart(CustomXmlPartType.CustomXml);
        using (var writer = new StreamWriter(customXmlPart.GetStream(FileMode.Create, FileAccess.Write)))
        {
            writer.Write("<root><owner>WorkbookUser</owner></root>");
        }

        workbookPart.Workbook = new S.Workbook(
            new S.DefinedNames(
                new S.DefinedName
                {
                    Name = "ExternalRef",
                    Text = "[OtherWorkbook.xlsx]Sheet1!$A$1"
                }),
            new S.ExternalReferences(new S.ExternalReference { Id = "rIdExternal1" }),
            new S.Sheets(
                new S.Sheet
                {
                    Name = "Sheet1",
                    SheetId = 1U,
                    Id = workbookPart.GetIdOfPart(worksheetPart)
                }));

        document.PackageProperties.Creator = "WorkbookUser";
        document.PackageProperties.LastModifiedBy = "Editor";
        document.PackageProperties.Description = "Private workbook";

        var customPropsPart = document.AddCustomFilePropertiesPart();
        var property = new CustomDocumentProperty
        {
            Name = "Project",
            FormatId = "{D5CDD505-2E9C-101B-9397-08002B2CF9AE}",
            PropertyId = 2
        };
        property.AppendChild(new VTLPWSTR("Secret"));
        customPropsPart.Properties = new Properties(property);

        workbookPart.Workbook.Save();
    }

    private static void CreateWorkbookWithAbsolutePathXlsx(string path)
    {
        using (var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();

            worksheetPart.Worksheet = new S.Worksheet(
                new S.SheetData(
                    new S.Row(
                        new S.Cell
                        {
                            CellReference = "A1",
                            DataType = S.CellValues.String,
                            CellValue = new S.CellValue("Visible")
                        })));

            workbookPart.Workbook = new S.Workbook(
                new S.Sheets(
                    new S.Sheet
                    {
                        Name = "Sheet1",
                        SheetId = 1U,
                        Id = workbookPart.GetIdOfPart(worksheetPart)
                    }));

            workbookPart.Workbook.Save();
        }

        AddWorkbookAbsolutePath(path);
        ClearZipEntryExternalAttributes(path);
    }

    private static void AddWorkbookAbsolutePath(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = archive.GetEntry("xl/workbook.xml") ?? throw new InvalidOperationException("xl/workbook.xml not found");

        XDocument document;
        using (var stream = entry.Open())
        {
            document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }

        entry.Delete();
        var root = document.Root ?? throw new InvalidOperationException("workbook.xml root not found");

        XNamespace mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
        XNamespace x15ac = "http://schemas.microsoft.com/office/spreadsheetml/2010/11/ac";

        root.AddFirst(
            new XElement(mc + "AlternateContent",
                new XElement(mc + "Choice",
                    new XAttribute("Requires", "x15"),
                    new XElement(x15ac + "absPath",
                        new XAttribute("url", @"C:\MetadataDelTest\Documents\secret\")))));

        var updatedEntry = archive.CreateEntry("xl/workbook.xml");
        using var output = updatedEntry.Open();
        document.Save(output, SaveOptions.DisableFormatting);
    }

    private static void ClearZipEntryExternalAttributes(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        foreach (var entry in archive.Entries)
        {
            entry.ExternalAttributes = 0;
        }
    }

    private static void CreatePdf(string path)
    {
        using var writer = new PdfWriter(path);
        using var document = new PdfDocument(writer);

        document.AddNewPage();
        document.GetDocumentInfo()
            .SetAuthor("Tester")
            .SetCreator("MetadataDel.Tests")
            .SetTitle("Sensitive PDF")
            .SetSubject("Sensitive")
            .SetKeywords("secret");

        var page = document.GetFirstPage();
        page.AddAnnotation(new PdfTextAnnotation(new iText.Kernel.Geom.Rectangle(20, 20, 20, 20)).SetContents("Note"));
    }

    private static void CreateComplexPdf(string path)
    {
        using var writer = new PdfWriter(path);
        using var document = new PdfDocument(writer);

        document.SetTagged();
        var page = document.AddNewPage();
        document.GetDocumentInfo()
            .SetAuthor("Tester")
            .SetCreator("MetadataDel.Tests")
            .SetTitle("Sensitive PDF")
            .SetSubject("Sensitive")
            .SetKeywords("secret")
            .SetProducer("Custom Producer");

        var xmp = XMPMetaFactory.ParseFromString("""
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
              <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                <rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/">
                  <dc:title>
                    <rdf:Alt>
                      <rdf:li xml:lang="x-default">Sensitive PDF</rdf:li>
                    </rdf:Alt>
                  </dc:title>
                </rdf:Description>
              </rdf:RDF>
            </x:xmpmeta>
            """);
        document.SetXmpMetadata(xmp);

        document.GetCatalog().SetOpenAction(PdfAction.CreateURI("https://example.com/open"));
        document.GetCatalog().GetPdfObject().Put(PdfName.AA, new PdfDictionary());

        var fileSpec = PdfFileSpec.CreateEmbeddedFileSpec(
            document,
            Encoding.UTF8.GetBytes("secret attachment"),
            "attachment",
            "secret.txt",
            null,
            null,
            null);
        document.AddFileAttachment("secret.txt", fileSpec);
        document.GetCatalog().Put(new PdfName("AF"), new PdfArray(fileSpec.GetPdfObject()));

        page.AddAnnotation(new PdfTextAnnotation(new iText.Kernel.Geom.Rectangle(20, 20, 20, 20)).SetContents("Note"));

        var form = PdfAcroForm.GetAcroForm(document, true);
        var field = PdfFormField.CreateText(
            document,
            new iText.Kernel.Geom.Rectangle(36, 700, 120, 20),
            "secretField",
            "secret value");
        form.AddField(field, page);
    }

    private static string ReadZipEntry(string path, string entryName)
    {
        using var archive = ZipFile.OpenRead(path);
        using var stream = archive.GetEntry(entryName)?.Open() ?? throw new InvalidOperationException($"Entry not found: {entryName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static bool ZipEntryExists(string path, string entryName)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.GetEntry(entryName) != null;
    }

    private static IReadOnlyList<int> ReadZipEntryExternalAttributes(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.Select(e => e.ExternalAttributes).ToList();
    }

    private static IReadOnlyList<DateTimeOffset> ReadZipEntryLastWriteTimes(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.Select(e => e.LastWriteTime).ToList();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
