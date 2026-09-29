using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Layer;
using iText.Kernel.Pdf.Xobject;
using MetadataDel.Core.Audit;
using MetadataDel.Core.Cleaning;
using MetadataDel.Core.Excel;
using MetadataDel.Core.Media;
using MetadataDel.Core.Pdf;
using MetadataDel.Core.Word;
using Xunit;
using Path = System.IO.Path;

namespace MetadataDel.Core.Tests;

/// <summary>Метаданные вне свойств документа: изображения, встроенные объекты, служебные части, объекты PDF.</summary>
public sealed class ResidualMetadataTests : IDisposable
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mdel-residual", Guid.NewGuid().ToString("N"));

    public ResidualMetadataTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch { }
    }

    [Fact]
    public void Jpeg_RemovesExifXmpIptcCommentsAndTrailerButKeepsOrientationAndScan()
    {
        var jpeg = Jpeg("JPEG-SECRET", orientation: 6);

        var stripped = ImageMetadata.Strip(jpeg);

        Assert.NotNull(stripped);
        Assert.DoesNotContain("JPEG-SECRET", Encoding.Latin1.GetString(stripped!));
        Assert.NotEmpty(ScanDataInside(stripped!));
        Assert.Equal(new byte[] { 0xFF, 0xD9 }, stripped[^2..]);
        var exif = Encoding.Latin1.GetString(stripped);
        Assert.Contains("Exif", exif);
        Assert.False(ImageMetadata.HasMetadata(stripped));
        Assert.Null(ImageMetadata.Strip(stripped));
    }

    [Fact]
    public void Jpeg_WithoutRotationDropsExifCompletely()
    {
        var stripped = ImageMetadata.Strip(Jpeg("JPEG-SECRET", orientation: 1))!;
        Assert.DoesNotContain("Exif", Encoding.Latin1.GetString(stripped));
    }

    [Fact]
    public void Png_RemovesTextExifAndTimeChunks()
    {
        var png = Png(("IHDR", new byte[13]), ("tEXt", Encoding.Latin1.GetBytes("Author\0PNG-SECRET")),
            ("eXIf", Encoding.Latin1.GetBytes("MM\0*PNG-SECRET")), ("tIME", new byte[7]), ("IDAT", new byte[] { 1, 2, 3 }), ("IEND", Array.Empty<byte>()));

        var stripped = ImageMetadata.Strip(png)!;

        var text = Encoding.Latin1.GetString(stripped);
        Assert.DoesNotContain("PNG-SECRET", text);
        Assert.DoesNotContain("tIME", text);
        Assert.Contains("IDAT", text);
        Assert.Contains("IEND", text);
    }

    [Fact]
    public async Task Docx_RemovesMetadataFromImagesEmbeddingsTemplateAndServiceParts()
    {
        var path = Path.Combine(_directory, "rich.docx");
        CreateRichDocx(path);
        var secrets = new[]
        {
            "IMG-SECRET", "THUMB-SECRET", "TEMPLATE-SECRET", "DESCR-SECRET", "NAME-SECRET", "CEX-SECRET", "PRINTER-SECRET",
            "EMBED-SECRET", "EMBED-IMG-SECRET", "OLE-SECRET", "ORPHAN-SECRET", "5EAD5EAD", "00C0FFEE", "PERM-SECRET"
        };
        Assert.All(secrets, s => Assert.Contains(s, DeepText(path)));
        Assert.True(MetadataAuditService.Audit(path).HasSensitiveMetadata);

        var result = await new DocxCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        var text = DeepText(path);
        Assert.All(secrets, s => Assert.DoesNotContain(s, text));
        var audit = MetadataAuditService.Audit(path);
        Assert.False(audit.HasSensitiveMetadata, audit.Summarize(20));
        using var document = WordprocessingDocument.Open(path, false);
        Assert.Contains("Visible text", document.MainDocumentPart!.Document.InnerText);
        Assert.Single(document.MainDocumentPart.ImageParts);
        Assert.Single(document.MainDocumentPart.EmbeddedPackageParts);
        Assert.Single(document.MainDocumentPart.EmbeddedObjectParts);
        Assert.Empty(document.MainDocumentPart.DocumentSettingsPart!.ExternalRelationships);
        Assert.Contains("Picture 1", ReadEntry(path, "word/document.xml"));
        Assert.DoesNotContain("printerSettings", ReadEntry(path, "word/document.xml"));
    }

    [Fact]
    public async Task Xlsx_RemovesPivotRevisionPrinterAndWorkbookIdentifiers()
    {
        var path = Path.Combine(_directory, "rich.xlsx");
        CreateRichXlsx(path);
        var secrets = new[] { "PIVOT-SECRET", "RECORD-SECRET", "REV-SECRET", "USER-SECRET", "PRINTER-SECRET", "DOCID-SECRET", "ABSPATH-SECRET", "IMG-SECRET" };
        Assert.All(secrets, s => Assert.Contains(s, DeepText(path)));
        Assert.True(MetadataAuditService.Audit(path).HasSensitiveMetadata);

        var result = await new ExcelCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        var text = DeepText(path);
        Assert.All(secrets, s => Assert.DoesNotContain(s, text));
        var audit = MetadataAuditService.Audit(path);
        Assert.False(audit.HasSensitiveMetadata, audit.Summarize(20));
        var sheet = ReadEntry(path, "xl/worksheets/");
        Assert.Contains("<pageSetup", sheet);
        Assert.DoesNotContain("r:id", Regex.Match(sheet, "<pageSetup[^>]*>").Value);
        Assert.Contains("saveData=\"0\"", ReadEntry(path, "pivotCacheDefinition"));
        Assert.DoesNotContain("AlternateContent", ReadEntry(path, "xl/workbook.xml"));
        using var document = SpreadsheetDocument.Open(path, false);
        Assert.Single(document.WorkbookPart!.PivotTableCacheDefinitionParts);
    }

    [Fact]
    public async Task Pdf_RemovesObjectMetadataImageExifLayerNamesAndProducerTraces()
    {
        var path = Path.Combine(_directory, "objects.pdf");
        CreateRichPdf(path);
        var audit = MetadataAuditService.Audit(path);
        Assert.Contains(audit.Findings, f => f.Code == "pdf.images");
        Assert.Contains(audit.Findings, f => f.Code == "pdf.objects.metadata");
        Assert.Contains(audit.Findings, f => f.Code == "pdf.objects.pieceInfo");
        Assert.Contains(audit.Findings, f => f.Code == "pdf.layers");

        var result = await new PdfCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        var text = PdfText(path);
        foreach (var secret in new[] { "EXIF-SECRET", "IMGXMP-SECRET", "PIECE-SECRET", "LAYER-SECRET", "AUTHOR-SECRET", "%iText", "Producer", "CreationDate" })
            Assert.DoesNotContain(secret, text);
        audit = MetadataAuditService.Audit(path);
        Assert.False(audit.HasSensitiveMetadata, audit.Summarize(20));
        using var document = new PdfDocument(new PdfReader(path));
        Assert.Contains("Visible PDF text", PdfTextExtractor.GetTextFromPage(document.GetFirstPage()));
        Assert.Empty(document.GetTrailer().GetAsDictionary(PdfName.Info)?.KeySet() ?? new List<PdfName>());
        var image = Enumerable.Range(1, document.GetNumberOfPdfObjects() - 1).Select(document.GetPdfObject).OfType<PdfStream>()
            .Single(s => PdfName.Image.Equals(s.GetAsName(PdfName.Subtype)));
        Assert.Equal(PdfName.DCTDecode, image.GetAsName(PdfName.Filter));
        Assert.NotEmpty(ScanDataInside(image.GetBytes(false)));
    }

    [Fact]
    public async Task Pdf_AuditFindsPreviousRevisionsAndCleaningDropsThem()
    {
        var original = Path.Combine(_directory, "original.pdf");
        var updated = Path.Combine(_directory, "updated.pdf");
        using (var document = new PdfDocument(new PdfWriter(original)))
        {
            document.GetDocumentInfo().SetAuthor("OLD-AUTHOR-SECRET");
            new PdfCanvas(document.AddNewPage()).Rectangle(10, 10, 10, 10).Fill();
        }
        using (var document = new PdfDocument(new PdfReader(original), new PdfWriter(updated), new StampingProperties().UseAppendMode()))
            document.GetDocumentInfo().SetAuthor("");

        Assert.Contains(MetadataAuditService.Audit(updated).Findings, f => f.Code == "pdf.revisions");
        Assert.True((await new PdfCleaner().CleanAsync(updated, new CleanOptions())).Success);
        Assert.DoesNotContain(MetadataAuditService.Audit(updated).Findings, f => f.Code == "pdf.revisions");
        Assert.DoesNotContain("OLD-AUTHOR-SECRET", PdfText(updated));
    }

    [Fact]
    public async Task Pdf_DoesNotAppendIncrementalUpdateThroughExiftool()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_directory, "exiftool.pdf");
        using (var document = new PdfDocument(new PdfWriter(path)))
            new PdfCanvas(document.AddNewPage()).Rectangle(10, 10, 10, 10).Fill();
        var tool = Path.Combine(_directory, "fake-exiftool");
        File.WriteAllText(tool, "#!/bin/sh\nfor last; do :; done\nprintf 'EXIFTOOL-RAN' >> \"$last\"\n");
        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var previous = Environment.GetEnvironmentVariable("EXIFTOOL_PATH");
        try
        {
            Environment.SetEnvironmentVariable("EXIFTOOL_PATH", tool);
            var result = await new PdfCleaner().CleanAsync(path, new CleanOptions(AggressivePdf: true));
            Assert.True(result.Success, result.Message);
            Assert.Null(result.Message);
        }
        finally { Environment.SetEnvironmentVariable("EXIFTOOL_PATH", previous); }
        Assert.DoesNotContain("EXIFTOOL-RAN", Encoding.Latin1.GetString(File.ReadAllBytes(path)));
    }

    [Fact]
    public void Audit_FindsUnreferencedPackagePartsAndUserPaths()
    {
        var path = Path.Combine(_directory, "plain.docx");
        using (var document = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            Feed(document.AddMainDocumentPart(), $"<w:document xmlns:w=\"{W}\"><w:body><w:p><w:r><w:t>text</w:t></w:r></w:p></w:body></w:document>");
        Assert.False(MetadataAuditService.Audit(path).HasSensitiveMetadata, MetadataAuditService.Audit(path).Summarize());

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("docProps/thumbnail.jpeg").Open()))
                writer.Write("orphan");
            using (var writer = new StreamWriter(archive.CreateEntry("word/footer9.xml").Open()))
                writer.Write($"<w:ftr xmlns:w=\"{W}\"><w:p><w:r><w:instrText> INCLUDETEXT \"C:\\\\Users\\\\ivanov\\\\a.docx\" </w:instrText></w:r></w:p></w:ftr>");
        }

        var audit = MetadataAuditService.Audit(path);
        Assert.Contains(audit.Findings, f => f.Code == "package.thumbnail");
        Assert.Contains(audit.Findings, f => f.Code == "package.userPath");
    }

    [Fact]
    public void Gif_RemovesCommentsAndXmpButKeepsAnimationLoop()
    {
        var gif = new List<byte>();
        gif.AddRange("GIF89a"u8.ToArray());
        gif.AddRange(new byte[] { 1, 0, 1, 0, 0x80, 0, 0 });
        gif.AddRange(new byte[6]);
        gif.AddRange(new byte[] { 0x21, 0xFF, 11 }); gif.AddRange("NETSCAPE2.0"u8.ToArray()); gif.AddRange(new byte[] { 3, 1, 0, 0, 0 });
        gif.AddRange(new byte[] { 0x21, 0xFE, 10 }); gif.AddRange("GIF-SECRET"u8.ToArray()); gif.Add(0);
        gif.AddRange(new byte[] { 0x21, 0xFF, 11 }); gif.AddRange("XMP DataXMP"u8.ToArray()); gif.Add(10); gif.AddRange("XMP-SECRET"u8.ToArray()); gif.Add(0);
        gif.AddRange(new byte[] { 0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, 0x44, 0x01, 0 });
        gif.Add(0x3B);
        gif.AddRange("TRAILER-SECRET"u8.ToArray());

        var text = Encoding.Latin1.GetString(ImageMetadata.Strip(gif.ToArray())!);

        Assert.DoesNotContain("SECRET", text);
        Assert.Contains("NETSCAPE2.0", text);
        Assert.EndsWith(";", text);
    }

    [Fact]
    public void Webp_RemovesExifAndXmpChunksAndFlags()
    {
        byte[] Chunk(string type, byte[] data) => Encoding.ASCII.GetBytes(type).Concat(BitConverter.GetBytes(data.Length)).Concat(data)
            .Concat(data.Length % 2 == 1 ? new byte[1] : Array.Empty<byte>()).ToArray();
        var body = "WEBP"u8.ToArray().Concat(Chunk("VP8X", new byte[] { 0x0C, 0, 0, 0, 0, 0, 0, 0, 0, 0 }))
            .Concat(Chunk("VP8L", new byte[] { 1, 2, 3 })).Concat(Chunk("EXIF", "EXIF-SECRET"u8.ToArray()))
            .Concat(Chunk("XMP ", "XMP-SECRET"u8.ToArray())).ToArray();
        var webp = "RIFF"u8.ToArray().Concat(BitConverter.GetBytes(body.Length)).Concat(body).ToArray();

        var stripped = ImageMetadata.Strip(webp)!;

        Assert.DoesNotContain("SECRET", Encoding.Latin1.GetString(stripped));
        Assert.Equal(stripped.Length - 8, BitConverter.ToInt32(stripped, 4));
        Assert.Equal(0, stripped[20] & 0x0C);
    }

    [Fact]
    public void Svg_RemovesEditorMetadataAndExportPaths()
    {
        var svg = Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\"?><!-- Created by SVG-SECRET --><svg xmlns=\"http://www.w3.org/2000/svg\" " +
            "xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\" xmlns:sodipodi=\"http://sodipodi.sourceforge.net/DTD/sodipodi-0.dtd\" " +
            "sodipodi:docname=\"DOCNAME-SECRET.svg\" inkscape:export-filename=\"C:\\Users\\EXPORT-SECRET\\a.png\">" +
            "<metadata><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">RDF-SECRET</rdf:RDF></metadata>" +
            "<sodipodi:namedview id=\"base\"/><title>Visible title</title><rect width=\"1\" height=\"1\"/></svg>");

        var text = Encoding.UTF8.GetString(ImageMetadata.Strip(svg)!);

        Assert.DoesNotContain("SECRET", text);
        Assert.DoesNotContain("inkscape", text);
        Assert.Contains("<rect", text);
        Assert.Contains("Visible title", text);
    }

    [Fact]
    public async Task Docx_RemovesHiddenTextAndShortensLocalPathsInFieldsAndLinks()
    {
        var path = Path.Combine(_directory, "hidden.docx");
        using (var document = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            Feed(main.AddNewPart<StyleDefinitionsPart>(),
                $"<w:styles xmlns:w=\"{W}\"><w:style w:type=\"character\" w:styleId=\"Secret\"><w:rPr><w:vanish/></w:rPr></w:style>" +
                "<w:style w:type=\"character\" w:styleId=\"SecretChild\"><w:basedOn w:val=\"Secret\"/></w:style></w:styles>");
            main.AddHyperlinkRelationship(new Uri("file:///C:/Users/LINK-SECRET/report.docx#part"), true, "rIdLink");
            Feed(main,
                $"<w:document xmlns:w=\"{W}\" xmlns:r=\"{R}\"><w:body><w:p>" +
                "<w:r><w:t xml:space=\"preserve\">Visible </w:t></w:r>" +
                "<w:r><w:rPr><w:vanish/></w:rPr><w:t>HIDDEN-DIRECT</w:t></w:r>" +
                "<w:r><w:rPr><w:rStyle w:val=\"SecretChild\"/></w:rPr><w:t>HIDDEN-STYLE</w:t></w:r>" +
                "<w:r><w:rPr><w:vanish w:val=\"0\"/></w:rPr><w:t>shown</w:t></w:r>" +
                "<w:r><w:rPr><w:vanish/></w:rPr><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:rPr><w:vanish/></w:rPr><w:instrText> XE \"Index entry\" </w:instrText></w:r><w:r><w:rPr><w:vanish/></w:rPr><w:fldChar w:fldCharType=\"end\"/></w:r>" +
                "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> INCLUDEPICTURE \"C:\\\\Users\\\\FIELD-</w:instrText></w:r>" +
                "<w:r><w:instrText>SECRET\\\\pic.jpg\" \\d </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>result</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r>" +
                "<w:fldSimple w:instr=\" INCLUDETEXT \\\\\\\\server\\\\SIMPLE-SECRET\\\\a.docx \"><w:r><w:t>text</w:t></w:r></w:fldSimple>" +
                "<w:hyperlink r:id=\"rIdLink\"><w:r><w:t>link</w:t></w:r></w:hyperlink>" +
                "</w:p></w:body></w:document>");
        }
        Assert.Contains(MetadataAuditService.Audit(path).Findings, f => f.Description.Contains("скрытый текст"));

        var result = await new DocxCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.Contains("скрытый текст", result.Message);
        var text = DeepText(path);
        foreach (var secret in new[] { "HIDDEN-DIRECT", "HIDDEN-STYLE", "FIELD-SECRET", "SIMPLE-SECRET", "LINK-SECRET" })
            Assert.DoesNotContain(secret, text);
        var xml = ReadEntry(path, "word/document.xml");
        Assert.Contains("Visible", xml);
        Assert.Contains("shown", xml);
        Assert.Contains("XE \"Index entry\"", xml);
        Assert.Contains("INCLUDEPICTURE \"pic.jpg\"", xml);
        Assert.Contains("INCLUDETEXT a.docx", xml);
        using var cleaned = WordprocessingDocument.Open(path, false);
        Assert.Equal("report.docx#part", cleaned.MainDocumentPart!.HyperlinkRelationships.Single().Uri.OriginalString);
        var audit = MetadataAuditService.Audit(path);
        Assert.False(audit.HasSensitiveMetadata, audit.Summarize(20));
    }

    [Fact]
    public async Task Xlsx_RemovesUnreferencedHiddenSheetsAndKeepsReferencedOnes()
    {
        var path = Path.Combine(_directory, "hidden.xlsx");
        using (var document = SpreadsheetDocument.Create(path, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            Feed(workbookPart,
                $"<workbook xmlns=\"{S}\" xmlns:r=\"{R}\"><bookViews><workbookView activeTab=\"2\"/></bookViews><sheets>" +
                "<sheet name=\"Visible\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Secret\" sheetId=\"2\" state=\"veryHidden\" r:id=\"rId2\"/>" +
                "<sheet name=\"Lookup data\" sheetId=\"3\" state=\"hidden\" r:id=\"rId3\"/></sheets><definedNames>" +
                "<definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"1\" hidden=\"1\">Secret!$A$1:$A$2</definedName>" +
                "<definedName name=\"_xlnm.Print_Area\" localSheetId=\"2\">'Lookup data'!$A$1</definedName></definedNames></workbook>");
            var visible = workbookPart.AddNewPart<WorksheetPart>("rId1");
            Feed(visible, $"<worksheet xmlns=\"{S}\"><sheetData><row r=\"1\"><c r=\"A1\"><f>'Lookup data'!A1*2</f><v>2</v></c></row></sheetData></worksheet>");
            // Бинарная часть с «xml» в типе содержимого не должна считаться ссылкой на скрытый лист.
            Feed(visible.AddNewPart<SpreadsheetPrinterSettingsPart>(), new byte[] { 0, 1, 2, 3 });
            Feed(workbookPart.AddNewPart<WorksheetPart>("rId2"),
                $"<worksheet xmlns=\"{S}\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>SHEET-SECRET</t></is></c></row></sheetData></worksheet>");
            Feed(workbookPart.AddNewPart<WorksheetPart>("rId3"),
                $"<worksheet xmlns=\"{S}\"><sheetData><row r=\"1\"><c r=\"A1\"><v>1</v></c></row></sheetData></worksheet>");
        }

        var result = await new ExcelCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.Contains("«Secret»", result.Message);
        Assert.Contains("«Lookup data»", result.Message);
        Assert.DoesNotContain("SHEET-SECRET", DeepText(path));
        var workbook = ReadEntry(path, "xl/workbook.xml");
        Assert.DoesNotContain("Secret", workbook);
        Assert.Contains("localSheetId=\"1\">'Lookup data'", workbook);
        Assert.Contains("activeTab=\"1\"", workbook);
        using var cleaned = SpreadsheetDocument.Open(path, false);
        Assert.Equal(2, cleaned.WorkbookPart!.WorksheetParts.Count());
        Assert.Contains(MetadataAuditService.Audit(path).Findings, f => f.Description.Contains("скрытые листы"));
    }

    [Fact]
    public async Task Xlsx_PivotOnExternalConnectionIsRejectedWithoutChanges()
    {
        var path = Path.Combine(_directory, "olap.xlsx");
        using (var document = SpreadsheetDocument.Create(path, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            Feed(workbookPart, $"<workbook xmlns=\"{S}\" xmlns:r=\"{R}\"><sheets><sheet name=\"A\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Feed(workbookPart.AddNewPart<WorksheetPart>("rId1"), $"<worksheet xmlns=\"{S}\"><sheetData/></worksheet>");
            Feed(workbookPart.AddNewPart<ConnectionsPart>(), $"<connections xmlns=\"{S}\"><connection id=\"1\" name=\"cube\" type=\"5\"/></connections>");
            Feed(workbookPart.AddNewPart<PivotTableCacheDefinitionPart>(),
                $"<pivotCacheDefinition xmlns=\"{S}\"><cacheSource type=\"external\" connectionId=\"1\"/><cacheFields count=\"0\"/></pivotCacheDefinition>");
        }
        var original = File.ReadAllBytes(path);

        var result = await new ExcelCleaner().CleanAsync(path, new CleanOptions());

        Assert.False(result.Success);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    // ---------------------------------------------------------------- fixtures

    private static readonly byte[] ScanData = { 0x12, 0xFF, 0x00, 0x34, 0xFF, 0xD0, 0x56 };

    private static IEnumerable<byte[]> ScanDataInside(byte[] data)
    {
        var index = data.AsSpan().IndexOf(ScanData);
        return index < 0 ? Array.Empty<byte[]>() : new[] { data.AsSpan(index, ScanData.Length).ToArray() };
    }

    internal static byte[] Jpeg(string secret, ushort orientation)
    {
        using var output = new MemoryStream();
        void Segment(byte marker, byte[] payload)
        {
            output.Write(new byte[] { 0xFF, marker, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2) });
            output.Write(payload);
        }
        byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
        var ascii = Encoding.Latin1.GetBytes(secret);

        output.Write(new byte[] { 0xFF, 0xD8 });
        Segment(0xE0, Concat("JFIF\0"u8.ToArray(), new byte[] { 1, 1, 0, 0, 1, 0, 1, 0, 0 }));
        Segment(0xE1, Concat("Exif\0\0MM\0*"u8.ToArray(), new byte[] { 0, 0, 0, 8, 0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, (byte)(orientation >> 8), (byte)orientation, 0, 0, 0, 0, 0, 0 }, ascii));
        Segment(0xE1, Concat("http://ns.adobe.com/xap/1.0/\0"u8.ToArray(), ascii));
        Segment(0xED, Concat("Photoshop 3.0\0"u8.ToArray(), ascii));
        Segment(0xFE, ascii);
        Segment(0xDB, new byte[65]);
        Segment(0xC0, new byte[] { 8, 0, 1, 0, 1, 1, 1, 0x11, 0 });
        Segment(0xDA, new byte[] { 1, 1, 0, 0, 63, 0 });
        output.Write(ScanData);
        output.Write(new byte[] { 0xFF, 0xD9 });
        output.Write(ascii);
        return output.ToArray();
    }

    private static byte[] Png(params (string Type, byte[] Data)[] chunks)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        foreach (var (type, data) in chunks)
        {
            output.Write(new byte[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length });
            output.Write(Encoding.ASCII.GetBytes(type));
            output.Write(data);
            output.Write(new byte[4]);
        }
        return output.ToArray();
    }

    private static void Feed(OpenXmlPart part, string xml) => Feed(part, Encoding.UTF8.GetBytes(xml));

    private static void Feed(OpenXmlPart part, byte[] data)
    {
        using var stream = new MemoryStream(data);
        part.FeedData(stream);
    }

    private static byte[] CompoundFile(string secret)
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var root = OpenMcdf.RootStorage.Create(path))
            {
                using (var properties = root.CreateStream("\u0005SummaryInformation"))
                    properties.Write(Encoding.Latin1.GetBytes(secret));
                using (var contents = root.CreateStream("CONTENTS"))
                    contents.Write("embedded content"u8);
            }
            return File.ReadAllBytes(path);
        }
        finally { File.Delete(path); }
    }

    private void CreateRichDocx(string path)
    {
        var embedded = Path.Combine(_directory, "embedded.xlsx");
        using (var workbook = SpreadsheetDocument.Create(embedded, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = workbook.AddWorkbookPart();
            Feed(workbookPart, $"<workbook xmlns=\"{S}\" xmlns:r=\"{R}\"><sheets/></workbook>");
            workbook.PackageProperties.Creator = "EMBED-SECRET";
            var sheet = workbookPart.AddNewPart<WorksheetPart>();
            Feed(sheet, $"<worksheet xmlns=\"{S}\"><sheetData/></worksheet>");
            var drawing = sheet.AddNewPart<DrawingsPart>();
            Feed(drawing, "<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\"/>");
            Feed(drawing.AddNewPart<ImagePart>("image/jpeg", "rIdImg"), Jpeg("EMBED-IMG-SECRET", 1));
        }

        using (var document = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            Feed(main,
                $"<w:document xmlns:w=\"{W}\" xmlns:r=\"{R}\" xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\">" +
                "<w:body><w:p><w:permStart w:id=\"9\" w:ed=\"CORP\\PERM-SECRET\"/><w:r><w:t>Visible text</w:t></w:r><w:permEnd w:id=\"9\"/></w:p>" +
                "<w:p><w:r><w:drawing><wp:inline><wp:docPr id=\"1\" name=\"C:\\Users\\NAME-SECRET\\a.jpg\" descr=\"C:\\Users\\DESCR-SECRET\\a.jpg\"/></wp:inline></w:drawing></w:r></w:p>" +
                "<w:sectPr><w:printerSettings r:id=\"rIdP\"/></w:sectPr></w:body></w:document>");
            Feed(main.AddNewPart<ImagePart>("image/jpeg", "rIdImg"), Jpeg("IMG-SECRET", 1));
            Feed(main.AddNewPart<WordprocessingPrinterSettingsPart>("rIdP"), Encoding.Unicode.GetBytes("PRINTER-SECRET"));
            Feed(main.AddNewPart<WordCommentsExtensiblePart>(),
                "<w16cex:commentsExtensible xmlns:w16cex=\"http://schemas.microsoft.com/office/word/2018/wordml/cex\"><w16cex:commentExtensible w16cex:durableId=\"CEX-SECRET\"/></w16cex:commentsExtensible>");
            Feed(main.AddNewPart<StyleDefinitionsPart>(), $"<w:styles xmlns:w=\"{W}\"><w:style w:type=\"paragraph\" w:styleId=\"Normal\" w:rsid=\"00C0FFEE\"/></w:styles>");
            var settings = main.AddNewPart<DocumentSettingsPart>();
            Feed(settings, $"<w:settings xmlns:w=\"{W}\" xmlns:r=\"{R}\" xmlns:w15=\"http://schemas.microsoft.com/office/word/2012/wordml\">" +
                           "<w:attachedTemplate r:id=\"rIdT\"/><w15:docId w15:val=\"{5EAD5EAD-0000-4000-8000-000000000000}\"/></w:settings>");
            settings.AddExternalRelationship(R + "/attachedTemplate", new Uri("file:///C:/Users/TEMPLATE-SECRET/Normal.dotm"), "rIdT");
            var package = main.AddEmbeddedPackagePart(EmbeddedPackagePartType.Xlsx);
            Feed(package, File.ReadAllBytes(embedded));
            Feed(main.AddEmbeddedObjectPart("application/vnd.openxmlformats-officedocument.oleObject"), CompoundFile("OLE-SECRET"));
            Feed(document.AddThumbnailPart("image/jpeg"), Jpeg("THUMB-SECRET", 1));
        }

        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        using var orphan = archive.CreateEntry("word/media/orphan.jpeg").Open();
        orphan.Write(Jpeg("ORPHAN-SECRET", 1));
    }

    private static void CreateRichXlsx(string path)
    {
        using var document = SpreadsheetDocument.Create(path, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        Feed(workbookPart,
            $"<workbook xmlns=\"{S}\" xmlns:r=\"{R}\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\">" +
            "<fileVersion appName=\"xl\" lastEdited=\"7\"/><mc:AlternateContent><mc:Choice Requires=\"x15\">" +
            "<x15ac:absPath xmlns:x15ac=\"http://schemas.microsoft.com/office/spreadsheetml/2010/11/ac\" url=\"C:\\Users\\ABSPATH-SECRET\\\"/></mc:Choice></mc:AlternateContent>" +
            "<sheets><sheet name=\"Data\" sheetId=\"1\" r:id=\"rIdS\"/></sheets><extLst><ext uri=\"{B58B0392-4F1F-4190-BB64-5DF3571DCE5F}\" " +
            "xmlns:xr=\"http://schemas.microsoft.com/office/spreadsheetml/2014/revision\"><xr:revisionPtr documentId=\"DOCID-SECRET\"/></ext></extLst></workbook>");
        var sheet = workbookPart.AddNewPart<WorksheetPart>("rIdS");
        Feed(sheet, $"<worksheet xmlns=\"{S}\" xmlns:r=\"{R}\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Visible</t></is></c></row></sheetData>" +
                    "<pageSetup orientation=\"portrait\" r:id=\"rIdPr\"/></worksheet>");
        Feed(sheet.AddNewPart<SpreadsheetPrinterSettingsPart>("rIdPr"), Encoding.Unicode.GetBytes("PRINTER-SECRET"));
        var drawing = sheet.AddNewPart<DrawingsPart>("rIdD");
        Feed(drawing, "<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\"/>");
        Feed(drawing.AddNewPart<ImagePart>("image/jpeg", "rIdImg"), Jpeg("IMG-SECRET", 1));
        var cache = workbookPart.AddNewPart<PivotTableCacheDefinitionPart>("rIdPc");
        Feed(cache, $"<pivotCacheDefinition xmlns=\"{S}\" xmlns:r=\"{R}\" r:id=\"rIdRec\" refreshedBy=\"PIVOT-SECRET\" refreshedDate=\"45000.5\" recordCount=\"1\">" +
                    "<cacheSource type=\"worksheet\"><worksheetSource ref=\"A1:A1\" sheet=\"Data\"/></cacheSource>" +
                    "<cacheFields count=\"1\"><cacheField name=\"c\" numFmtId=\"0\"><sharedItems/></cacheField></cacheFields></pivotCacheDefinition>");
        Feed(cache.AddNewPart<PivotTableCacheRecordsPart>("rIdRec"), $"<pivotCacheRecords xmlns=\"{S}\" count=\"1\"><r><s v=\"RECORD-SECRET\"/></r></pivotCacheRecords>");
        Feed(workbookPart.AddNewPart<WorkbookRevisionHeaderPart>(), $"<headers xmlns=\"{S}\" guid=\"{{00000000-0000-0000-0000-000000000001}}\">" +
                                                                    "<header guid=\"{00000000-0000-0000-0000-000000000002}\" dateTime=\"2020-01-01T00:00:00\" maxSheetId=\"2\" userName=\"REV-SECRET\"/></headers>");
        Feed(workbookPart.AddNewPart<WorkbookUserDataPart>(), $"<users xmlns=\"{S}\" count=\"1\"><userInfo guid=\"{{00000000-0000-0000-0000-000000000003}}\" name=\"USER-SECRET\" id=\"1\" dateTime=\"2020-01-01T00:00:00\"/></users>");
    }

    private static void CreateRichPdf(string path)
    {
        using var document = new PdfDocument(new PdfWriter(path));
        document.GetDocumentInfo().SetAuthor("AUTHOR-SECRET");
        var page = document.AddNewPage();

        var xmp = new PdfStream(Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">IMGXMP-SECRET</x:xmpmeta>"));
        xmp.Put(PdfName.Type, PdfName.Metadata);
        xmp.Put(PdfName.Subtype, PdfName.XML);
        var image = new PdfStream(Jpeg("EXIF-SECRET", 1));
        image.Put(PdfName.Type, PdfName.XObject);
        image.Put(PdfName.Subtype, PdfName.Image);
        image.Put(PdfName.Width, new PdfNumber(1));
        image.Put(PdfName.Height, new PdfNumber(1));
        image.Put(PdfName.ColorSpace, PdfName.DeviceGray);
        image.Put(PdfName.BitsPerComponent, new PdfNumber(8));
        image.Put(PdfName.Filter, PdfName.DCTDecode);
        image.Put(PdfName.Metadata, xmp.MakeIndirect(document));
        image.MakeIndirect(document);

        var form = new PdfFormXObject(new Rectangle(10, 10));
        var pieceInfo = new PdfDictionary();
        var application = new PdfDictionary();
        application.Put(new PdfName("Private"), new PdfString("PIECE-SECRET"));
        pieceInfo.Put(new PdfName("Illustrator"), application);
        form.GetPdfObject().Put(new PdfName("PieceInfo"), pieceInfo);
        new PdfCanvas(form, document).Rectangle(0, 0, 5, 5).Fill();

        var canvas = new PdfCanvas(page);
        canvas.BeginText().SetFontAndSize(iText.Kernel.Font.PdfFontFactory.CreateFont(), 12).MoveText(20, 700).ShowText("Visible PDF text").EndText();
        canvas.AddXObjectWithTransformationMatrix(new PdfImageXObject(image), 10, 0, 0, 10, 20, 600);
        canvas.AddXObjectAt(form, 50, 50);
        var layer = new PdfLayer("LAYER-SECRET", document);
        canvas.BeginLayer(layer).Rectangle(100, 100, 10, 10).Fill().EndLayer();
    }

    // ---------------------------------------------------------------- inspection helpers

    private static string ReadEntry(string path, string entryName)
    {
        using var archive = ZipFile.OpenRead(path);
        // Имя части задаёт SDK, поэтому допускаем поиск по фрагменту пути.
        var entry = archive.GetEntry(entryName) ?? archive.Entries.Single(e => e.FullName.Contains(entryName, StringComparison.Ordinal) &&
                                                                              !e.FullName.Contains("_rels", StringComparison.Ordinal));
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    /// <summary>Текст всех частей пакета, включая вложенные архивы и потоки OLE (Latin-1 и UTF-16).</summary>
    private static string DeepText(string path) => DeepText(File.ReadAllBytes(path));

    private static string DeepText(byte[] data)
    {
        var text = new StringBuilder(Encoding.Latin1.GetString(data)).Append('\n').Append(Encoding.Unicode.GetString(data));
        if (data.Length > 4 && data[0] == (byte)'P' && data[1] == (byte)'K')
        {
            using var archive = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                text.Append('\n').Append(DeepText(buffer.ToArray()));
            }
        }
        else if (data.Length > 8 && data[0] == 0xD0 && data[1] == 0xCF)
        {
            using var root = OpenMcdf.RootStorage.Open(new MemoryStream(data));
            AppendStorage(root, text);
        }
        return text.ToString();
    }

    private static void AppendStorage(OpenMcdf.Storage storage, StringBuilder text)
    {
        foreach (var entry in storage.EnumerateEntries())
        {
            if (entry.Type == OpenMcdf.EntryType.Storage) AppendStorage(storage.OpenStorage(entry.Name), text);
            else if (entry.Type == OpenMcdf.EntryType.Stream)
            {
                using var stream = storage.OpenStream(entry.Name);
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                text.Append('\n').Append(DeepText(buffer.ToArray()));
            }
        }
    }

    /// <summary>Байты PDF вместе с распакованными Flate-потоками.</summary>
    private static string PdfText(string path)
    {
        var data = File.ReadAllBytes(path);
        var text = new StringBuilder(Encoding.Latin1.GetString(data));
        foreach (Match match in Regex.Matches(text.ToString(), "stream\r?\n(.*?)\r?\nendstream", RegexOptions.Singleline))
        {
            try
            {
                using var input = new ZLibStream(new MemoryStream(Encoding.Latin1.GetBytes(match.Groups[1].Value)), CompressionMode.Decompress);
                using var output = new MemoryStream();
                input.CopyTo(output);
                text.Append('\n').Append(Encoding.Latin1.GetString(output.ToArray()));
            }
            catch (InvalidDataException) { }
        }
        return text.ToString();
    }
}
