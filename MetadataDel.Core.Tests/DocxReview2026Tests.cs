using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MetadataDel.Core.Cleaning;
using MetadataDel.Core.Word;
using Xunit;
using Wp = DocumentFormat.OpenXml.Wordprocessing;

namespace MetadataDel.Core.Tests;

/// <summary>Регрессии DOCX по итогам ревью: удалённые знаки абзаца, видимость текста по стилям, осиротевшие медиа, заполнители.</summary>
public sealed class DocxReview2026Tests : IDisposable
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private const string Namespaces =
        "xmlns:w=\"" + W + "\" xmlns:r=\"" + R + "\" " +
        "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\" " +
        "xmlns:v=\"urn:schemas-microsoft-com:vml\" xmlns:o=\"urn:schemas-microsoft-com:office:office\"";

    private const string Track = "w:author=\"REV-AUTHOR\" w:date=\"2025-01-01T00:00:00Z\"";

    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mdel-docxreview2026", Guid.NewGuid().ToString("N"));

    public DocxReview2026Tests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch { }
    }

    // ------------------------------------------------------------------ 1. удалённый знак абзаца

    [Fact]
    public async Task DeletedListItem_WithDeletedParagraphMark_LeavesNoEmptyItem()
    {
        var path = Path.Combine(_directory, "list.docx");
        const string numPr = "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr>";
        CreateDocx(path,
            $"<w:p><w:pPr>{numPr}</w:pPr><w:r><w:t>A</w:t></w:r></w:p>" +
            $"<w:p><w:pPr>{numPr}<w:rPr><w:del w:id=\"1\" {Track}/></w:rPr></w:pPr><w:del w:id=\"2\" {Track}><w:r><w:delText>B</w:delText></w:r></w:del></w:p>" +
            $"<w:p><w:pPr>{numPr}</w:pPr><w:r><w:t>C</w:t></w:r></w:p>" +
            "<w:sectPr/>",
            main => Feed(main.AddNewPart<NumberingDefinitionsPart>(), NumberingXml()));
        AssertValid(path);

        var result = await new DocxCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        AssertValid(path);
        Assert.Equal(new[] { "A", "C" }, ParagraphTexts(path));
        using var document = WordprocessingDocument.Open(path, false);
        Assert.All(document.MainDocumentPart!.Document!.Body!.Elements<Wp.Paragraph>(),
            p => Assert.NotNull(p.ParagraphProperties?.NumberingProperties));
        Assert.DoesNotContain("REV-AUTHOR", ReadEntry(path, "word/document.xml"));
    }

    [Fact]
    public async Task ChainOfDeletedParagraphMarks_IsMergedIntoFollowingParagraph()
    {
        var path = Path.Combine(_directory, "chain.docx");
        CreateDocx(path,
            $"<w:p><w:pPr><w:jc w:val=\"left\"/><w:rPr><w:del w:id=\"1\" {Track}/></w:rPr></w:pPr><w:r><w:t xml:space=\"preserve\">Hello </w:t></w:r>" +
            $"<w:del w:id=\"2\" {Track}><w:r><w:delText>gone</w:delText></w:r></w:del></w:p>" +
            $"<w:p><w:pPr><w:rPr><w:del w:id=\"3\" {Track}/></w:rPr></w:pPr><w:del w:id=\"4\" {Track}><w:r><w:delText>Second</w:delText></w:r></w:del></w:p>" +
            "<w:p><w:pPr><w:jc w:val=\"center\"/></w:pPr><w:r><w:t>Tail</w:t></w:r></w:p>" +
            // Перед таблицей: пустой абзац удаляется, абзац с оставшимся текстом сохраняется без метки.
            $"<w:p><w:pPr><w:rPr><w:del w:id=\"5\" {Track}/></w:rPr></w:pPr><w:del w:id=\"6\" {Track}><w:r><w:delText>Before table</w:delText></w:r></w:del></w:p>" +
            Table("Cell") +
            $"<w:p><w:pPr><w:rPr><w:del w:id=\"7\" {Track}/></w:rPr></w:pPr><w:r><w:t>Kept</w:t></w:r></w:p>" +
            Table("Cell2") +
            // Абзац с разрывом раздела не удаляется никогда.
            $"<w:p><w:pPr><w:rPr><w:del w:id=\"8\" {Track}/></w:rPr><w:sectPr/></w:pPr></w:p>" +
            "<w:p><w:r><w:t>End</w:t></w:r></w:p>" +
            "<w:sectPr/>");
        AssertValid(path);

        var result = await new DocxCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        AssertValid(path);
        Assert.Equal(new[] { "Hello Tail", "Kept", "", "End" }, ParagraphTexts(path));
        using var document = WordprocessingDocument.Open(path, false);
        var body = document.MainDocumentPart!.Document!.Body!;
        var merged = body.Elements<Wp.Paragraph>().First();
        Assert.Equal(Wp.JustificationValues.Center, merged.ParagraphProperties!.Justification!.Val!.Value);
        Assert.Single(body.Elements<Wp.Paragraph>(), p => p.ParagraphProperties?.SectionProperties != null);
        Assert.IsType<Wp.Table>(body.Elements<Wp.Paragraph>().First().NextSibling());
    }

    [Fact]
    public async Task MovedParagraph_SourceParagraphDisappears()
    {
        var path = Path.Combine(_directory, "moved.docx");
        CreateDocx(path,
            "<w:p><w:r><w:t>First</w:t></w:r></w:p>" +
            $"<w:p><w:pPr><w:rPr><w:moveFrom w:id=\"1\" {Track}/></w:rPr></w:pPr><w:moveFromRangeStart w:id=\"2\" {Track} w:name=\"move1\"/>" +
            $"<w:moveFrom w:id=\"3\" {Track}><w:r><w:t>Moved</w:t></w:r></w:moveFrom><w:moveFromRangeEnd w:id=\"2\"/></w:p>" +
            "<w:p><w:r><w:t>Last</w:t></w:r></w:p>" +
            $"<w:p><w:pPr><w:rPr><w:moveTo w:id=\"4\" {Track}/></w:rPr></w:pPr><w:moveToRangeStart w:id=\"5\" {Track} w:name=\"move1\"/>" +
            $"<w:moveTo w:id=\"6\" {Track}><w:r><w:t>Moved</w:t></w:r></w:moveTo><w:moveToRangeEnd w:id=\"5\"/></w:p>" +
            "<w:sectPr/>");
        AssertValid(path);

        var result = await new DocxCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        AssertValid(path);
        Assert.Equal(new[] { "First", "Last", "Moved" }, ParagraphTexts(path));
    }

    [Fact]
    public async Task DeletedParagraphMark_InHeaderAndGlossary_IsAccepted()
    {
        var path = Path.Combine(_directory, "header.docx");
        CreateDocx(path,
            "<w:p><w:r><w:t>Body</w:t></w:r></w:p><w:sectPr><w:headerReference w:type=\"default\" r:id=\"rIdHdr\"/></w:sectPr>",
            main =>
            {
                Feed(main.AddNewPart<HeaderPart>("rIdHdr"),
                    $"<w:hdr xmlns:w=\"{W}\"><w:p><w:pPr><w:rPr><w:del w:id=\"1\" {Track}/></w:rPr></w:pPr><w:del w:id=\"2\" {Track}><w:r><w:delText>Old</w:delText></w:r></w:del></w:p>" +
                    "<w:p><w:r><w:t>Head</w:t></w:r></w:p></w:hdr>");
                Feed(main.AddNewPart<GlossaryDocumentPart>(),
                    $"<w:glossaryDocument xmlns:w=\"{W}\"><w:docParts><w:docPart><w:docPartPr><w:name w:val=\"Block\"/><w:guid w:val=\"{{A0000000-0000-0000-0000-000000000001}}\"/></w:docPartPr>" +
                    $"<w:docPartBody><w:p><w:pPr><w:rPr><w:del w:id=\"3\" {Track}/></w:rPr></w:pPr><w:del w:id=\"4\" {Track}><w:r><w:delText>Old</w:delText></w:r></w:del></w:p>" +
                    "<w:p><w:r><w:t>Block</w:t></w:r></w:p></w:docPartBody></w:docPart></w:docParts></w:glossaryDocument>");
            });
        AssertValid(path);

        var result = await new DocxCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        AssertValid(path);
        using var document = WordprocessingDocument.Open(path, false);
        var header = document.MainDocumentPart!.HeaderParts.Single().Header!;
        Assert.Equal(new[] { "Head" }, header.Elements<Wp.Paragraph>().Select(p => p.InnerText));
        var glossary = document.MainDocumentPart.GlossaryDocumentPart!.GlossaryDocument!;
        Assert.Equal(new[] { "Block" }, glossary.Descendants<Wp.Paragraph>().Select(p => p.InnerText));
    }

    // ------------------------------------------------------------------ 2. приоритет свойств скрытого текста

    [Fact]
    public async Task RunUnhiddenByOwnOrCharacterStyle_InHiddenParagraphStyle_IsKept()
    {
        var path = Path.Combine(_directory, "unhide.docx");
        CreateDocx(path,
            "<w:p><w:pPr><w:pStyle w:val=\"HiddenPara\"/></w:pPr>" +
            "<w:r><w:t>SECRET-HIDDEN</w:t></w:r>" +
            "<w:r><w:rPr><w:vanish w:val=\"0\"/></w:rPr><w:t>VISIBLE-DIRECT0</w:t></w:r>" +
            "<w:r><w:rPr><w:vanish w:val=\"false\"/></w:rPr><w:t>VISIBLE-DIRECTFALSE</w:t></w:r>" +
            "<w:r><w:rPr><w:rStyle w:val=\"Shown\"/></w:rPr><w:t>VISIBLE-STYLE</w:t></w:r>" +
            "<w:r><w:rPr><w:rStyle w:val=\"ShownChild\"/></w:rPr><w:t>VISIBLE-CHILD</w:t></w:r>" +
            "<w:r><w:rPr><w:rStyle w:val=\"Plain\"/></w:rPr><w:t>SECRET-PLAIN</w:t></w:r>" +
            "<w:r><w:rPr><w:rStyle w:val=\"Shown\"/><w:vanish/></w:rPr><w:t>SECRET-DIRECT</w:t></w:r>" +
            "</w:p>" +
            "<w:p><w:pPr><w:pStyle w:val=\"HiddenChild\"/></w:pPr><w:r><w:t>SECRET-CHILDPARA</w:t></w:r></w:p>" +
            "<w:p><w:pPr><w:pStyle w:val=\"ShownPara\"/></w:pPr><w:r><w:t>VISIBLE-PARA</w:t></w:r></w:p>" +
            "<w:sectPr/>",
            main => Feed(main.AddNewPart<StyleDefinitionsPart>(),
                $"<w:styles xmlns:w=\"{W}\">" +
                "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style>" +
                "<w:style w:type=\"paragraph\" w:styleId=\"HiddenPara\"><w:name w:val=\"Hidden Para\"/><w:rPr><w:vanish/></w:rPr></w:style>" +
                "<w:style w:type=\"paragraph\" w:styleId=\"HiddenChild\"><w:name w:val=\"Hidden Child\"/><w:basedOn w:val=\"HiddenPara\"/></w:style>" +
                "<w:style w:type=\"paragraph\" w:styleId=\"ShownPara\"><w:name w:val=\"Shown Para\"/><w:basedOn w:val=\"HiddenPara\"/><w:rPr><w:vanish w:val=\"off\"/></w:rPr></w:style>" +
                "<w:style w:type=\"character\" w:styleId=\"Shown\"><w:name w:val=\"Shown\"/><w:rPr><w:vanish w:val=\"0\"/></w:rPr></w:style>" +
                "<w:style w:type=\"character\" w:styleId=\"ShownChild\"><w:name w:val=\"Shown Child\"/><w:basedOn w:val=\"Shown\"/></w:style>" +
                "<w:style w:type=\"character\" w:styleId=\"Plain\"><w:name w:val=\"Plain\"/><w:rPr><w:b/></w:rPr></w:style>" +
                "</w:styles>"));
        AssertValid(path);

        var result = await new DocxCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        AssertValid(path);
        var xml = ReadEntry(path, "word/document.xml");
        foreach (var visible in new[] { "VISIBLE-DIRECT0", "VISIBLE-DIRECTFALSE", "VISIBLE-STYLE", "VISIBLE-CHILD", "VISIBLE-PARA" })
            Assert.Contains(visible, xml);
        foreach (var secret in new[] { "SECRET-HIDDEN", "SECRET-PLAIN", "SECRET-DIRECT", "SECRET-CHILDPARA" })
            Assert.DoesNotContain(secret, xml);
    }

    // ------------------------------------------------------------------ 3. медиа удалённого скрытого текста

    [Fact]
    public async Task MediaOfRemovedHiddenRuns_IsDeletedFromPackage()
    {
        var path = Path.Combine(_directory, "hiddenmedia.docx");
        CreateDocx(path,
            $"<w:p><w:r><w:rPr><w:vanish/></w:rPr>{Drawing(1, "rIdHidden")}</w:r><w:r><w:t>Text</w:t></w:r></w:p>" +
            $"<w:p><w:r>{Drawing(2, "rIdVisible")}</w:r></w:p>" +
            $"<w:p><w:r><w:rPr><w:vanish/></w:rPr>{Drawing(3, "rIdShared")}</w:r></w:p>" +
            "<w:p><w:r><w:rPr><w:vanish/></w:rPr><w:pict><v:shape id=\"s1\" style=\"width:10pt;height:10pt\"><v:imagedata r:id=\"rIdVml\" o:title=\"\"/></v:shape></w:pict></w:r></w:p>" +
            "<w:sectPr><w:headerReference w:type=\"default\" r:id=\"rIdHdr\"/></w:sectPr>",
            main =>
            {
                Feed(main.AddNewPart<ImagePart>("image/png", "rIdHidden"), Png.Append((byte)1).ToArray());
                Feed(main.AddNewPart<ImagePart>("image/png", "rIdVisible"), Png.Append((byte)2).ToArray());
                Feed(main.AddNewPart<ImagePart>("image/png", "rIdVml"), Png.Append((byte)3).ToArray());
                var shared = main.AddNewPart<ImagePart>("image/png", "rIdShared");
                Feed(shared, Png.Append((byte)4).ToArray());
                var header = main.AddNewPart<HeaderPart>("rIdHdr");
                header.AddPart(shared, "rIdHdrImg");
                Feed(header, $"<w:hdr {Namespaces}><w:p><w:r>{Drawing(4, "rIdHdrImg")}</w:r></w:p></w:hdr>");
            });
        Assert.True(MediaEntries(path) == 4, string.Join(", ", EntryNames(path)));

        var result = await new DocxCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(Validate(path), e => !e.Contains("/v:", StringComparison.Ordinal));
        using (var document = WordprocessingDocument.Open(path, false))
        {
            var main = document.MainDocumentPart!;
            var ids = main.Parts.Select(p => p.RelationshipId).ToHashSet();
            Assert.Contains("rIdVisible", ids);
            Assert.Contains("rIdHdr", ids);
            Assert.DoesNotContain("rIdHidden", ids);
            Assert.DoesNotContain("rIdVml", ids);
            Assert.DoesNotContain("rIdShared", ids);
            Assert.Single(main.HeaderParts.Single().ImageParts);
        }
        // В пакете остались только видимая картинка и картинка колонтитула.
        Assert.Equal(2, MediaEntries(path));
        Assert.Contains("Text", ReadEntry(path, "word/document.xml"));
    }

    // ------------------------------------------------------------------ 4. заполнители элементов управления содержимым

    [Fact]
    public async Task ContentControlPlaceholderState_IsPreserved()
    {
        var path = Path.Combine(_directory, "placeholder.docx");
        CreateDocx(path,
            "<w:sdt><w:sdtPr><w:alias w:val=\"ALIAS-SECRET\"/><w:tag w:val=\"TAG-SECRET\"/><w:id w:val=\"1\"/>" +
            "<w:placeholder><w:docPart w:val=\"DefaultPlaceholder_1\"/></w:placeholder><w:showingPlcHdr/><w:text/></w:sdtPr>" +
            "<w:sdtContent><w:p><w:r><w:t>Click here to enter text.</w:t></w:r></w:p></w:sdtContent></w:sdt>" +
            "<w:sectPr/>");
        AssertValid(path);

        var result = await new DocxCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        AssertValid(path);
        var xml = ReadEntry(path, "word/document.xml");
        Assert.DoesNotContain("ALIAS-SECRET", xml);
        Assert.DoesNotContain("TAG-SECRET", xml);
        using var document = WordprocessingDocument.Open(path, false);
        var properties = document.MainDocumentPart!.Document!.Body!.Descendants<Wp.SdtProperties>().Single();
        Assert.NotNull(properties.GetFirstChild<Wp.ShowingPlaceholder>());
        Assert.Equal("DefaultPlaceholder_1", properties.GetFirstChild<Wp.SdtPlaceholder>()?.DocPartReference?.Val?.Value);
    }

    // ------------------------------------------------------------------ helpers

    private static string Table(string text) =>
        "<w:tbl><w:tblPr><w:tblW w:w=\"0\" w:type=\"auto\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"2000\"/></w:tblGrid>" +
        $"<w:tr><w:tc><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:tc></w:tr></w:tbl>";

    private static string Drawing(int id, string relationshipId) =>
        "<w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"100\" cy=\"100\"/>" +
        $"<wp:docPr id=\"{id}\" name=\"Picture {id}\"/>" +
        "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic>" +
        $"<pic:nvPicPr><pic:cNvPr id=\"0\" name=\"Picture {id}\"/><pic:cNvPicPr/></pic:nvPicPr>" +
        $"<pic:blipFill><a:blip r:embed=\"{relationshipId}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
        "<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"100\" cy=\"100\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>" +
        "</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing>";

    private static string NumberingXml() =>
        $"<w:numbering xmlns:w=\"{W}\"><w:abstractNum w:abstractNumId=\"0\"><w:multiLevelType w:val=\"hybridMultilevel\"/>" +
        "<w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"decimal\"/><w:lvlText w:val=\"%1.\"/><w:lvlJc w:val=\"left\"/></w:lvl></w:abstractNum>" +
        "<w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num></w:numbering>";

    private static List<string> ParagraphTexts(string path)
    {
        using var document = WordprocessingDocument.Open(path, false);
        return document.MainDocumentPart!.Document!.Body!.Elements<Wp.Paragraph>().Select(p => p.InnerText).ToList();
    }

    private static int MediaEntries(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.Count(e => e.FullName.Contains("media/", StringComparison.Ordinal) && !e.FullName.EndsWith(".rels", StringComparison.Ordinal));
    }

    private static List<string> EntryNames(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.Select(e => e.FullName).ToList();
    }

    private static void CreateDocx(string path, string body, Action<MainDocumentPart>? configure = null)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        configure?.Invoke(main);
        Feed(main, $"<w:document {Namespaces}><w:body>{body}</w:body></w:document>");
    }

    private static void AssertValid(string path)
    {
        var errors = Validate(path);
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    private static List<string> Validate(string path)
    {
        using var document = WordprocessingDocument.Open(path, false);
        var validator = new OpenXmlValidator(FileFormatVersions.Microsoft365);
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
}
