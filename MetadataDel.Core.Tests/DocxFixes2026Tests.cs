using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MetadataDel.Core.Audit;
using MetadataDel.Core.Cleaning;
using MetadataDel.Core.Excel;
using MetadataDel.Core.Word;
using Xunit;

namespace MetadataDel.Core.Tests;

/// <summary>Регрессии DOCX и общего слоя OpenXML-пакетов (исправления 2026 года).</summary>
public sealed class DocxFixes2026Tests : IDisposable
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private const string Namespaces =
        "xmlns:w=\"" + W + "\" xmlns:r=\"" + R + "\" " +
        "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\" " +
        "xmlns:v=\"urn:schemas-microsoft-com:vml\" xmlns:o=\"urn:schemas-microsoft-com:office:office\" " +
        "xmlns:w16du=\"http://schemas.microsoft.com/office/word/2023/wordml/word16du\" " +
        "xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" mc:Ignorable=\"w16du\"";

    private const string Date = "2025-01-01T00:00:00Z";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mdel-docx2026", Guid.NewGuid().ToString("N"));

    public DocxFixes2026Tests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch { }
    }

    // ------------------------------------------------------------------ 1. customXml / smartTag

    [Fact]
    public async Task CustomXmlAndSmartTag_AreUnwrappedWithoutPropertiesAndStayValid()
    {
        var path = Path.Combine(_directory, "customxml.docx");
        CreateDocx(path,
            "<w:customXml w:element=\"block\"><w:customXmlPr><w:placeholder w:val=\"PH-SECRET\"/><w:attr w:name=\"owner\" w:val=\"ATTR-SECRET\"/></w:customXmlPr>" +
            "<w:p><w:customXml w:element=\"inline\"><w:customXmlPr><w:attr w:name=\"a\" w:val=\"RUNATTR-SECRET\"/></w:customXmlPr><w:r><w:t>Inline</w:t></w:r></w:customXml>" +
            "</w:p></w:customXml>" +
            "<w:p><w:smartTag w:element=\"PersonName\"><w:smartTagPr><w:attr w:name=\"ProductID\" w:val=\"SMART-SECRET\"/></w:smartTagPr><w:r><w:t>Ivan</w:t></w:r></w:smartTag></w:p>" +
            "<w:tbl><w:tblPr><w:tblW w:w=\"0\" w:type=\"auto\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"2000\"/></w:tblGrid>" +
            "<w:customXml w:element=\"row\"><w:customXmlPr><w:attr w:name=\"r\" w:val=\"ROW-SECRET\"/></w:customXmlPr>" +
            "<w:tr><w:customXml w:element=\"cell\"><w:customXmlPr><w:attr w:name=\"c\" w:val=\"CELL-SECRET\"/></w:customXmlPr>" +
            "<w:tc><w:p><w:r><w:t>Cell</w:t></w:r></w:p></w:tc></w:customXml></w:tr></w:customXml></w:tbl>" +
            "<w:p/><w:sectPr/>");
        // Валидатор SDK не описывает устаревший smartTag, остальная разметка исходного файла корректна.
        Assert.DoesNotContain(Validate(path), e => !e.Contains("smartTag", StringComparison.Ordinal));

        var result = await new DocxCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        AssertValid(path, FileFormatVersions.Office2007);
        AssertValid(path);
        var xml = ReadEntry(path, "word/document.xml");
        foreach (var secret in new[] { "PH-SECRET", "ATTR-SECRET", "RUNATTR-SECRET", "SMART-SECRET", "ROW-SECRET", "CELL-SECRET", "customXml", "smartTag" })
            Assert.DoesNotContain(secret, xml);
        foreach (var text in new[] { "Inline", "Ivan", "Cell" })
            Assert.Contains(text, xml);
    }

    // ------------------------------------------------------------------ 2. табличные и нумерационные исправления

    [Fact]
    public async Task TableAndNumberingRevisionMarkers_AreAcceptedAndStayValid()
    {
        var path = Path.Combine(_directory, "tablerevisions.docx");
        var track = $"w:author=\"REV-AUTHOR\" w:date=\"{Date}\"";
        CreateDocx(path,
            "<w:tbl><w:tblPr><w:tblW w:w=\"0\" w:type=\"auto\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"2000\"/><w:gridCol w:w=\"2000\"/></w:tblGrid>" +
            $"<w:tr><w:tc><w:tcPr><w:tcW w:w=\"2000\" w:type=\"dxa\"/><w:cellIns w:id=\"1\" {track}/></w:tcPr><w:p><w:r><w:t>A1</w:t></w:r></w:p></w:tc>" +
            $"<w:tc><w:tcPr><w:tcW w:w=\"2000\" w:type=\"dxa\"/><w:cellDel w:id=\"2\" {track}/></w:tcPr><w:p><w:r><w:t>DELETED-CELL-SECRET</w:t></w:r></w:p></w:tc></w:tr>" +
            $"<w:tr><w:tblPrEx><w:jc w:val=\"center\"/><w:tblPrExChange w:id=\"3\" {track}><w:tblPrEx/></w:tblPrExChange></w:tblPrEx>" +
            $"<w:tc><w:tcPr><w:tcW w:w=\"2000\" w:type=\"dxa\"/><w:cellMerge w:id=\"4\" {track} w:vMerge=\"cont\" w:vMergeOrig=\"rest\"/></w:tcPr><w:p><w:r><w:t>A2</w:t></w:r></w:p></w:tc>" +
            "<w:tc><w:p><w:r><w:t>B2</w:t></w:r></w:p></w:tc></w:tr>" +
            $"<w:tr><w:trPr><w:del w:id=\"5\" {track}/></w:trPr><w:tc><w:p><w:r><w:t>DELETED-ROW-SECRET</w:t></w:r></w:p></w:tc><w:tc><w:p/></w:tc></w:tr>" +
            "</w:tbl>" +
            $"<w:p><w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/><w:numberingChange w:id=\"6\" {track} w:original=\"%1.\"/></w:numPr></w:pPr><w:r><w:t>Item</w:t></w:r></w:p>" +
            "<w:sectPr/>",
            main => Feed(main.AddNewPart<NumberingDefinitionsPart>(), NumberingXml()));
        AssertValid(path);

        var result = await new DocxCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        AssertValid(path);
        var xml = ReadEntry(path, "word/document.xml");
        foreach (var marker in new[] { "cellIns", "cellDel", "cellMerge", "numberingChange", "tblPrExChange", "REV-AUTHOR", "DELETED-CELL-SECRET", "DELETED-ROW-SECRET" })
            Assert.DoesNotContain(marker, xml);
        foreach (var text in new[] { "A1", "A2", "B2", "Item" })
            Assert.Contains(text, xml);
        using var document = WordprocessingDocument.Open(path, false);
        var rows = document.MainDocumentPart!.Document!.Body!.Descendants<DocumentFormat.OpenXml.Wordprocessing.TableRow>().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Single(rows[0].Elements<DocumentFormat.OpenXml.Wordprocessing.TableCell>());
        var merge = rows[1].Descendants<DocumentFormat.OpenXml.Wordprocessing.VerticalMerge>().Single();
        Assert.True(merge.Val == null || merge.Val.Value == DocumentFormat.OpenXml.Wordprocessing.MergedCellValues.Continue);
        Assert.False(MetadataAuditService.Audit(path).HasSensitiveMetadata, MetadataAuditService.Audit(path).Summarize(20));
    }

    // ------------------------------------------------------------------ 3. glossary

    [Fact]
    public async Task GlossaryDocument_IsSanitizedLikeMainDocument()
    {
        var path = Path.Combine(_directory, "glossary.docx");
        CreateDocx(path, "<w:p><w:r><w:t>Body</w:t></w:r></w:p><w:sectPr/>", main =>
        {
            var settings = main.AddNewPart<DocumentSettingsPart>();
            Feed(settings, $"<w:settings xmlns:w=\"{W}\"><w:writeProtection w:recommended=\"1\"/><w:documentProtection w:edit=\"readOnly\" w:enforcement=\"1\"/></w:settings>");

            var glossary = main.AddNewPart<GlossaryDocumentPart>();
            Feed(glossary, $"<w:glossaryDocument xmlns:w=\"{W}\"><w:docParts><w:docPart><w:docPartPr><w:name w:val=\"Block\"/><w:guid w:val=\"{{A0000000-0000-0000-0000-000000000001}}\"/></w:docPartPr>" +
                           "<w:docPartBody><w:p w:rsidR=\"00AA11BB\"><w:r><w:t>Block text</w:t></w:r></w:p></w:docPartBody></w:docPart></w:docParts></w:glossaryDocument>");
            var glossarySettings = glossary.AddNewPart<DocumentSettingsPart>();
            Feed(glossarySettings, $"<w:settings xmlns:w=\"{W}\" xmlns:r=\"{R}\" xmlns:w15=\"http://schemas.microsoft.com/office/word/2012/wordml\">" +
                                   "<w:attachedTemplate r:id=\"rIdGT\"/><w:docVars><w:docVar w:name=\"Owner\" w:val=\"GLOSSVAR-SECRET\"/></w:docVars>" +
                                   "<w:rsids><w:rsidRoot w:val=\"00FACE01\"/><w:rsid w:val=\"00FACE02\"/></w:rsids>" +
                                   "<w15:docId w15:val=\"{6105AAAA-0000-4000-8000-000000000000}\"/></w:settings>");
            glossarySettings.AddExternalRelationship(R + "/attachedTemplate", new Uri("file:///C:/Users/GLOSSTPL-SECRET/Normal.dotm"), "rIdGT");
            Feed(glossary.AddNewPart<FootnotesPart>(), $"<w:footnotes xmlns:w=\"{W}\"><w:footnote w:id=\"1\"><w:p w:rsidR=\"00FEED01\"><w:ins w:id=\"1\" w:author=\"GLOSSAUTHOR-SECRET\" w:date=\"{Date}\"><w:r><w:t>Note</w:t></w:r></w:ins></w:p></w:footnote></w:footnotes>");
            Feed(glossary.AddNewPart<HeaderPart>(), $"<w:hdr xmlns:w=\"{W}\"><w:p w:rsidRPr=\"00FEED03\"><w:r><w:t>Head</w:t></w:r></w:p></w:hdr>");
            Feed(glossary.AddNewPart<WebSettingsPart>(), $"<w:webSettings xmlns:w=\"{W}\"><w:divs><w:div w:id=\"1\" w:rsid=\"00FEED02\"/></w:divs></w:webSettings>");
        });
        Assert.True(MetadataAuditService.Audit(path).HasSensitiveMetadata);

        var result = await new DocxCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        var text = DeepText(path);
        foreach (var secret in new[] { "GLOSSVAR-SECRET", "00FACE01", "00FACE02", "6105AAAA", "GLOSSTPL-SECRET", "GLOSSAUTHOR-SECRET", "00FEED01", "00FEED03", "00AA11BB" })
            Assert.DoesNotContain(secret, text);
        Assert.Contains("Block text", text);
        Assert.Contains("Note", text);
        var audit = MetadataAuditService.Audit(path);
        Assert.False(audit.HasSensitiveMetadata, audit.Summarize(20));
        Assert.False(result.Incomplete);
        // Защита документа — функциональная настройка, её нельзя снимать.
        var mainSettings = ReadEntry(path, "word/settings.xml");
        Assert.Contains("documentProtection", mainSettings);
        Assert.Contains("writeProtection", mainSettings);
    }

    // ------------------------------------------------------------------ 4. цифровая подпись

    [Fact]
    public async Task DigitalSignatureParts_AreAuditedAndRemovedFromDocx()
    {
        var path = Path.Combine(_directory, "signed.docx");
        CreateDocx(path, "<w:p><w:r><w:t>Signed</w:t></w:r></w:p><w:sectPr/>", main => AddSignature(main.OpenXmlPackage));
        Assert.Contains(MetadataAuditService.Audit(path).Findings, f => f.Code.Contains("signature", StringComparison.OrdinalIgnoreCase));

        var result = await new DocxCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.Contains("цифровая подпись удалена", result.Message ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SIGNER-SECRET", DeepText(path));
        Assert.DoesNotContain("relationships/digital-signature/", DeepText(path));
        using (var archive = ZipFile.OpenRead(path))
            Assert.DoesNotContain(archive.Entries, e => e.FullName.Contains("xmlsignatures", StringComparison.OrdinalIgnoreCase));
        var audit = MetadataAuditService.Audit(path);
        Assert.False(audit.HasSensitiveMetadata, audit.Summarize(20));
    }

    [Fact]
    public async Task DigitalSignatureParts_AreRemovedFromXlsx()
    {
        var path = Path.Combine(_directory, "signed.xlsx");
        using (var workbook = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = workbook.AddWorkbookPart();
            var sheet = workbookPart.AddNewPart<WorksheetPart>("rIdS1");
            Feed(sheet, $"<worksheet xmlns=\"{S}\"><sheetData/></worksheet>");
            Feed(workbookPart, $"<workbook xmlns=\"{S}\" xmlns:r=\"{R}\"><sheets><sheet name=\"A\" sheetId=\"1\" r:id=\"rIdS1\"/></sheets></workbook>");
            AddSignature(workbook);
        }
        Assert.Contains(MetadataAuditService.Audit(path).Findings, f => f.Code.Contains("signature", StringComparison.OrdinalIgnoreCase));

        var result = await new ExcelCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.Contains("цифровая подпись удалена", result.Message ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SIGNER-SECRET", DeepText(path));
        Assert.DoesNotContain(MetadataAuditService.Audit(path).Findings, f => f.Code.Contains("signature", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ 5. имена и описания картинок, href/alt

    [Fact]
    public async Task PictureFileNames_AreRemovedAndAuditMatchesCleaner()
    {
        var path = Path.Combine(_directory, "pictures.docx");
        CreateDocx(path,
            Picture(1, "IMG_2031.JPG", "Screenshot 2024-01-02 at 10.11.12.png", "Снимок экрана 2024-05-01 в 12.00.00.png", "photo_2023-03-04_12-00-00.jpg") +
            Picture(2, "Рисунок 5", "Схема процесса", "Picture 7", null) +
            "<w:p><w:r><w:pict><v:shape id=\"s1\" style=\"width:10pt;height:10pt\" alt=\"IMG_9999.HEIC\"><v:fill o:href=\"C:\\Users\\HREF-SECRET\\bg.png\"/></v:shape>" +
            "<v:rect id=\"s2\" style=\"width:10pt;height:10pt\" alt=\"C:\\Users\\ALT-SECRET\\x.png\"/></w:pict></w:r></w:p>" +
            "<w:sectPr/>",
            main => Feed(main.AddNewPart<ImagePart>("image/png", "rIdImg"), new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
        var before = MetadataAuditService.Audit(path);
        Assert.Contains(before.Findings, f => f.Code == "package.imagePath");

        var result = await new DocxCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        var xml = ReadEntry(path, "word/document.xml");
        foreach (var secret in new[] { "IMG_2031", "Screenshot", "Снимок", "photo_2023", "IMG_9999", "HREF-SECRET", "ALT-SECRET", "C0FFEEC0FFE" })
            Assert.DoesNotContain(secret, xml);
        // Обычные имена и осмысленные описания Word — не метаданные.
        Assert.Contains("Рисунок 5", xml);
        Assert.Contains("Схема процесса", xml);
        Assert.Contains("Picture 7", xml);
        Assert.Contains("name=\"Picture 1\"", xml);
        var audit = MetadataAuditService.Audit(path);
        Assert.False(audit.HasSensitiveMetadata, audit.Summarize(20));
        Assert.DoesNotContain(Validate(path), e => !e.Contains("/v:", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ 6. относительные внешние ссылки

    [Fact]
    public async Task RelativeExternalTargets_AreReducedToFileName()
    {
        var path = Path.Combine(_directory, "relative.docx");
        CreateDocx(path,
            "<w:p><w:hyperlink r:id=\"rIdH1\"><w:r><w:t>one</w:t></w:r></w:hyperlink><w:hyperlink r:id=\"rIdH2\"><w:r><w:t>two</w:t></w:r></w:hyperlink>" +
            "<w:hyperlink r:id=\"rIdH3\"><w:r><w:t>three</w:t></w:r></w:hyperlink><w:hyperlink r:id=\"rIdH4\"><w:r><w:t>web</w:t></w:r></w:hyperlink>" +
            "<w:hyperlink r:id=\"rIdH5\"><w:r><w:t>mail</w:t></w:r></w:hyperlink></w:p>" +
            "<w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> HYPERLINK \"..\\\\..\\\\FIELDDIR\\\\Secret\\\\y.docx\" </w:instrText></w:r>" +
            "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>field</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p>" +
            "<w:p><w:fldSimple w:instr=\" DATE \\@ &quot;d/M/yyyy&quot; \"><w:r><w:t>1/1/2025</w:t></w:r></w:fldSimple></w:p>" +
            "<w:sectPr/>",
            main =>
            {
                main.AddHyperlinkRelationship(new Uri(@"..\..\HR\Salaries\x.xlsx", UriKind.Relative), true, "rIdH1");
                main.AddHyperlinkRelationship(new Uri("../RELDIR/b.docx#part", UriKind.Relative), true, "rIdH2");
                main.AddHyperlinkRelationship(new Uri("//server/SHAREDIR/x.docx", UriKind.Relative), true, "rIdH3");
                main.AddHyperlinkRelationship(new Uri("https://example.com/keep/path"), true, "rIdH4");
                main.AddHyperlinkRelationship(new Uri("mailto:someone@example.com"), true, "rIdH5");
                main.AddHyperlinkRelationship(new Uri("www.example.com/keep/page", UriKind.Relative), true, "rIdH6");
            });

        var result = await new DocxCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        var text = DeepText(path);
        foreach (var secret in new[] { "Salaries", "RELDIR", "SHAREDIR", "FIELDDIR" })
            Assert.DoesNotContain(secret, text);
        using var document = WordprocessingDocument.Open(path, false);
        var targets = document.MainDocumentPart!.HyperlinkRelationships.ToDictionary(r => r.Id, r => Uri.UnescapeDataString(r.Uri.OriginalString));
        Assert.Equal("x.xlsx", targets["rIdH1"]);
        Assert.Equal("b.docx#part", targets["rIdH2"]);
        Assert.Equal("x.docx", targets["rIdH3"]);
        Assert.Equal("https://example.com/keep/path", targets["rIdH4"]);
        Assert.Equal("mailto:someone@example.com", targets["rIdH5"]);
        Assert.Equal("www.example.com/keep/page", targets["rIdH6"]);
        var xml = ReadEntry(path, "word/document.xml");
        Assert.Contains("y.docx", xml);
        Assert.Contains("d/M/yyyy", xml);
    }

    // ------------------------------------------------------------------ 7. идентификаторы

    [Fact]
    public async Task EditorIdentifiers_AreRemovedAndListIdsAreDeterministic()
    {
        var first = Path.Combine(_directory, "ids1.docx");
        var second = Path.Combine(_directory, "ids2.docx");
        foreach (var path in new[] { first, second })
        {
            CreateDocx(path,
                "<w:p w16du:dateUtc=\"2024-02-03T04:05:06Z\"><w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr></w:pPr><w:r><w:t>Item</w:t></w:r></w:p><w:sectPr/>",
                main =>
                {
                    Feed(main.AddNewPart<StyleDefinitionsPart>(), $"<w:styles xmlns:w=\"{W}\"><w:style w:type=\"paragraph\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/><w:rsid w:val=\"00AB12CD\"/></w:style></w:styles>");
                    Feed(main.AddNewPart<NumberingDefinitionsPart>(), NumberingXml());
                });
        }

        Assert.True((await new DocxCleaner().CleanAsync(first, new CleanOptions())).Success);
        Assert.True((await new DocxCleaner().CleanAsync(second, new CleanOptions())).Success);

        var text = DeepText(first);
        foreach (var secret in new[] { "00AB12CD", "1A2B3C4D", "5E6F7A8B", "2B3C4D5E", "6F7A8B9C", "dateUtc", "durableId", "123456789" })
            Assert.DoesNotContain(secret, text);
        var numbering = ReadEntry(first, "word/numbering.xml");
        var nsids = Regex.Matches(numbering, "<w:nsid w:val=\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(2, nsids.Count);
        Assert.All(nsids, n => Assert.Matches("^[0-9A-F]{8}$", n));
        Assert.Equal(2, nsids.Distinct().Count());
        Assert.Equal(2, Regex.Matches(numbering, "<w:tmpl w:val=\"[0-9A-F]{8}\"").Count);
        Assert.Equal(numbering, ReadEntry(second, "word/numbering.xml"));
        var audit = MetadataAuditService.Audit(first);
        Assert.False(audit.HasSensitiveMetadata, audit.Summarize(20));
    }

    // ------------------------------------------------------------------ 8. библиография

    [Fact]
    public async Task BibliographyCustomXml_RemovalIsReported()
    {
        var path = Path.Combine(_directory, "bibliography.docx");
        CreateDocx(path, "<w:p><w:r><w:t>Cited</w:t></w:r></w:p><w:sectPr/>", main =>
            Feed(main.AddCustomXmlPart(CustomXmlPartType.Bibliography),
                "<b:Sources SelectedStyle=\"\\APA.XSL\" xmlns:b=\"http://schemas.openxmlformats.org/officeDocument/2006/bibliography\" xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/bibliography\">" +
                "<b:Source><b:Tag>Iva20</b:Tag><b:Author><b:Author><b:NameList><b:Person><b:Last>BIB-SECRET</b:Last></b:Person></b:NameList></b:Author></b:Author></b:Source></b:Sources>"));

        var result = await new DocxCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain("BIB-SECRET", DeepText(path));
        Assert.Contains("список источников", result.Message ?? "", StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ 9. ZIP-слой

    [Fact]
    public async Task ZipLayer_IsRewrittenInStableOrderWithoutCommentsAndExtraFields()
    {
        var source = Path.Combine(_directory, "source.docx");
        CreateDocx(source, "<w:p><w:r><w:t>Zip</w:t></w:r></w:p><w:p><w:r><w:drawing><wp:inline><wp:extent cx=\"1\" cy=\"1\"/><wp:docPr id=\"1\" name=\"Picture 1\"/>" +
                           "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic><pic:nvPicPr><pic:cNvPr id=\"0\" name=\"Picture 1\"/><pic:cNvPicPr/></pic:nvPicPr>" +
                           "<pic:blipFill><a:blip r:embed=\"rIdImg\"/></pic:blipFill><pic:spPr/></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p><w:sectPr/>",
            main => Feed(main.AddNewPart<ImagePart>("image/png", "rIdImg"), new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 }));

        // Пересобираем архив вручную: [Content_Types].xml в конце, имя с %xx, комментарии, NTFS/UT-поля и лишняя запись.
        var entries = new List<(string Name, byte[] Data)>();
        using (var archive = ZipFile.OpenRead(source))
        {
            foreach (var entry in archive.Entries)
            {
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                var data = buffer.ToArray();
                var name = entry.FullName;
                if (name.EndsWith(".png", StringComparison.Ordinal)) name = "word/media/pic%201.png";
                if (name == "word/_rels/document.xml.rels")
                    data = Encoding.UTF8.GetBytes(Regex.Replace(Encoding.UTF8.GetString(data), "Target=\"[^\"]*media/[^\"]+\"", "Target=\"media/pic%201.png\""));
                entries.Add((name, data));
            }
        }
        entries.Add(("word/media/orphan.png", new byte[] { 1, 2, 3 }));
        var contentTypes = entries.Single(e => e.Name == "[Content_Types].xml");
        entries.Remove(contentTypes);
        entries.Add(contentTypes);
        var path = Path.Combine(_directory, "zip.docx");
        WriteRawZip(path, entries, "ARCHIVE-COMMENT-SECRET", "ENTRY-COMMENT-SECRET");
        Assert.True(HasExtraFields(path));
        Assert.Contains(MetadataAuditService.Audit(path).Findings, f => f.Code == "package.zipComment");

        var result = await new DocxCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        var raw = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        Assert.DoesNotContain("COMMENT-SECRET", raw);
        Assert.False(HasExtraFields(path));
        using (var archive = ZipFile.OpenRead(path))
        {
            var names = archive.Entries.Select(e => e.FullName).ToList();
            Assert.Equal("[Content_Types].xml", names[0]);
            Assert.Equal("_rels/.rels", names[1]);
            Assert.Equal(names.Skip(2).OrderBy(n => n, StringComparer.Ordinal), names.Skip(2));
            Assert.Contains("word/media/pic%201.png", names);
            Assert.DoesNotContain("word/media/orphan.png", names);
            Assert.All(archive.Entries, e => Assert.Equal(1980, e.LastWriteTime.Year));
            Assert.All(archive.Entries, e => Assert.Equal("", e.Comment));
            Assert.Equal("", archive.Comment);
        }
        using (var document = WordprocessingDocument.Open(path, false))
            Assert.Single(document.MainDocumentPart!.ImageParts);
        var audit = MetadataAuditService.Audit(path);
        Assert.False(audit.HasSensitiveMetadata, audit.Summarize(20));
    }

    // ------------------------------------------------------------------ 11. неполная очистка

    [Fact]
    public async Task UncleanableEmbeddedDocument_MarksResultIncomplete()
    {
        var path = Path.Combine(_directory, "incomplete.docx");
        CreateDocx(path, "<w:p><w:r><w:t>Body</w:t></w:r></w:p><w:sectPr/>", main =>
            Feed(main.AddEmbeddedPackagePart(EmbeddedPackagePartType.Xlsx), Encoding.ASCII.GetBytes("this is not a zip package")));

        var result = await new DocxCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.True(result.Incomplete, result.Message);
    }

    [Fact]
    public async Task CleanDocx_IsNotMarkedIncomplete()
    {
        var path = Path.Combine(_directory, "complete.docx");
        CreateDocx(path, "<w:p><w:r><w:t>Body</w:t></w:r></w:p><w:sectPr/>");

        var result = await new DocxCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.False(result.Incomplete, result.Message);
    }

    // ------------------------------------------------------------------ helpers

    private static void CreateDocx(string path, string body, Action<MainDocumentPart>? configure = null)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        configure?.Invoke(main);
        Feed(main, $"<w:document {Namespaces}><w:body>{body}</w:body></w:document>");
    }

    private static string Picture(int id, string docPrName, string? descr, string cNvPrName, string? title)
    {
        var description = descr == null ? "" : $" descr=\"{descr}\"";
        var picTitle = title == null ? "" : $" title=\"{title}\"";
        return "<w:p><w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"100\" cy=\"100\"/>" +
               $"<wp:docPr id=\"{id}\" name=\"{docPrName}\"{description}><a:extLst><a:ext uri=\"{{FF2B5EF4-FFF2-40B4-BE49-F238E27FC236}}\">" +
               $"<a16:creationId xmlns:a16=\"http://schemas.microsoft.com/office/drawing/2014/main\" id=\"{{11111111-2222-3333-4444-C0FFEEC0FFE{id}}}\"/></a:ext></a:extLst></wp:docPr>" +
               "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic>" +
               $"<pic:nvPicPr><pic:cNvPr id=\"0\" name=\"{cNvPrName}\"{picTitle}/><pic:cNvPicPr/></pic:nvPicPr>" +
               "<pic:blipFill><a:blip r:embed=\"rIdImg\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
               "<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"100\" cy=\"100\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>" +
               "</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>";
    }

    private static string NumberingXml()
    {
        static string Abstract(int id, string nsid, string tmpl) =>
            $"<w:abstractNum w:abstractNumId=\"{id}\"><w:nsid w:val=\"{nsid}\"/><w:multiLevelType w:val=\"hybridMultilevel\"/><w:tmpl w:val=\"{tmpl}\"/>" +
            "<w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"decimal\"/><w:lvlText w:val=\"%1.\"/><w:lvlJc w:val=\"left\"/></w:lvl></w:abstractNum>";
        return $"<w:numbering xmlns:w=\"{W}\" xmlns:w16cid=\"http://schemas.microsoft.com/office/word/2016/wordml/cid\" " +
               "xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" mc:Ignorable=\"w16cid\">" +
               Abstract(0, "1A2B3C4D", "5E6F7A8B") + Abstract(1, "2B3C4D5E", "6F7A8B9C") +
               "<w:num w:numId=\"1\" w16cid:durableId=\"123456789\"><w:abstractNumId w:val=\"0\"/></w:num></w:numbering>";
    }

    private static void AddSignature(OpenXmlPackage package)
    {
        var origin = package switch
        {
            WordprocessingDocument word => word.AddDigitalSignatureOriginPart(),
            SpreadsheetDocument workbook => workbook.AddDigitalSignatureOriginPart(),
            _ => throw new NotSupportedException()
        };
        var signature = origin.AddNewPart<XmlSignaturePart>();
        Feed(signature, "<Signature xmlns=\"http://www.w3.org/2000/09/xmldsig#\"><KeyInfo><X509Data><X509SubjectName>CN=SIGNER-SECRET, E=signer@example.com</X509SubjectName>" +
                        "</X509Data></KeyInfo><Object><SignatureProperties><SignatureProperty><SignatureTime><Value>2024-01-01T10:00:00Z</Value></SignatureTime>" +
                        "</SignatureProperty></SignatureProperties></Object></Signature>");
    }

    private static void AssertValid(string path, FileFormatVersions version = FileFormatVersions.Microsoft365)
    {
        var errors = Validate(path, version);
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    private static List<string> Validate(string path, FileFormatVersions version = FileFormatVersions.Microsoft365)
    {
        using var document = WordprocessingDocument.Open(path, false);
        var validator = new OpenXmlValidator(version);
        return validator.Validate(document).Select(e => $"{e.Part?.Uri} {e.Path?.XPath}: {e.Description}").ToList();
    }

    private static void Feed(OpenXmlPart part, string xml) => Feed(part, Encoding.UTF8.GetBytes(xml));

    private static void Feed(OpenXmlPart part, byte[] data)
    {
        using var stream = new MemoryStream(data);
        part.FeedData(stream);
    }

    private static string ReadEntry(string path, string entryName)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry(entryName) ?? throw new InvalidOperationException(entryName + " not found");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    /// <summary>Текст всех записей архива, включая вложенные архивы.</summary>
    private static string DeepText(string path) => DeepText(File.ReadAllBytes(path));

    private static string DeepText(byte[] data)
    {
        var text = new StringBuilder(Encoding.Latin1.GetString(data)).Append('\n').Append(Encoding.UTF8.GetString(data));
        if (data.Length > 4 && data[0] == (byte)'P' && data[1] == (byte)'K')
        {
            using var archive = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                text.Append('\n').Append(entry.FullName).Append('\n').Append(DeepText(buffer.ToArray()));
            }
        }
        return text.ToString();
    }

    // ---------------------------------------------------------------- raw ZIP

    /// <summary>Записывает ZIP без сжатия с NTFS- и UT-полями времени и комментариями (как это делают сторонние архиваторы).</summary>
    private static void WriteRawZip(string path, IReadOnlyList<(string Name, byte[] Data)> entries, string archiveComment, string entryComment)
    {
        var extra = new byte[4 + 5 + 4 + 32];
        BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(0), 0x5455);
        BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(2), 5);
        extra[4] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(extra.AsSpan(5), 1_700_000_000);
        BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(9), 0x000A);
        BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(11), 32);
        BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(17), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(19), 24);
        for (var i = 21; i < extra.Length; i++) extra[i] = 0x42;

        var comment = Encoding.ASCII.GetBytes(entryComment);
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        var offsets = new List<long>();
        foreach (var (name, data) in entries)
        {
            offsets.Add(output.Position);
            var nameBytes = Encoding.UTF8.GetBytes(name);
            writer.Write(0x04034b50u); writer.Write((ushort)20); writer.Write((ushort)0x0800); writer.Write((ushort)0);
            writer.Write((ushort)0x6000); writer.Write((ushort)0x5821); writer.Write(Crc32(data));
            writer.Write((uint)data.Length); writer.Write((uint)data.Length);
            writer.Write((ushort)nameBytes.Length); writer.Write((ushort)extra.Length);
            writer.Write(nameBytes); writer.Write(extra); writer.Write(data);
        }
        var directoryStart = output.Position;
        for (var i = 0; i < entries.Count; i++)
        {
            var (name, data) = entries[i];
            var nameBytes = Encoding.UTF8.GetBytes(name);
            writer.Write(0x02014b50u); writer.Write((ushort)0x031E); writer.Write((ushort)20); writer.Write((ushort)0x0800); writer.Write((ushort)0);
            writer.Write((ushort)0x6000); writer.Write((ushort)0x5821); writer.Write(Crc32(data));
            writer.Write((uint)data.Length); writer.Write((uint)data.Length);
            writer.Write((ushort)nameBytes.Length); writer.Write((ushort)extra.Length); writer.Write((ushort)comment.Length);
            writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(0x81A40000u); writer.Write((uint)offsets[i]);
            writer.Write(nameBytes); writer.Write(extra); writer.Write(comment);
        }
        var directorySize = output.Position - directoryStart;
        var archiveCommentBytes = Encoding.ASCII.GetBytes(archiveComment);
        writer.Write(0x06054b50u); writer.Write((ushort)0); writer.Write((ushort)0);
        writer.Write((ushort)entries.Count); writer.Write((ushort)entries.Count);
        writer.Write((uint)directorySize); writer.Write((uint)directoryStart);
        writer.Write((ushort)archiveCommentBytes.Length); writer.Write(archiveCommentBytes);
        writer.Flush();
        File.WriteAllBytes(path, output.ToArray());
    }

    /// <summary>Есть ли в центральном каталоге или локальных заголовках дополнительные поля.</summary>
    private static bool HasExtraFields(string path)
    {
        var data = File.ReadAllBytes(path);
        var end = data.Length - 22;
        while (end >= 0 && BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(end)) != 0x06054b50) end--;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(end + 10));
        var position = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(end + 16));
        for (var i = 0; i < count; i++)
        {
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position + 28));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position + 30));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position + 32));
            var local = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 42));
            if (extraLength != 0 || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(local + 28)) != 0) return true;
            position += 46 + nameLength + extraLength + commentLength;
        }
        return false;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}
