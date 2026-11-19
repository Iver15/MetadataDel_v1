using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using MetadataDel.Core.Audit;
using MetadataDel.Core.Cleaning;
using MetadataDel.Core.Excel;
using MetadataDel.Core.Word;
using Xunit;

namespace MetadataDel.Core.Tests;

/// <summary>Регрессии очистки XLSX: внешние ссылки, таблицы запросов, встроенные книги и остаточные данные.</summary>
public sealed class XlsxFixes2026Tests : IDisposable
{
    private const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string C = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    private const string X14 = "http://schemas.microsoft.com/office/spreadsheetml/2009/9/main";
    private const string XM = "http://schemas.microsoft.com/office/excel/2006/main";
    private const string XR = "http://schemas.microsoft.com/office/spreadsheetml/2014/revision";
    private const string XR2 = "http://schemas.microsoft.com/office/spreadsheetml/2015/revision2";
    private const string XR3 = "http://schemas.microsoft.com/office/spreadsheetml/2016/revision3";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mdel-xlsx2026", Guid.NewGuid().ToString("N"));

    public XlsxFixes2026Tests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch { }
    }

    // ---------------------------------------------------------------- 1. внешние ссылки в формулах

    [Fact]
    public async Task Xlsx_UrlInFormulaAndDefinedNameIsNotExternal()
    {
        var path = Path.Combine(_directory, "url.xlsx");
        BuildWorkbook(path,
            Sheet("<sheetData><row r=\"1\"><c r=\"A1\" t=\"str\"><f>HYPERLINK(\"https://example.com/[1]x!y\",\"site\")</f><v>site</v></c></row></sheetData>"),
            definedNames: "<definedName name=\"SiteLink\">HYPERLINK(\"https://example.com\",\"site\")</definedName>");

        var result = await new ExcelCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.Contains("HYPERLINK(\"https://example.com/[1]x!y\",\"site\")", ReadEntry(path, "xl/worksheets/sheet1.xml"));
        var workbook = ReadEntry(path, "xl/workbook.xml");
        Assert.Contains("SiteLink", workbook);
        Assert.DoesNotContain("externalReferences", workbook);
    }

    [Fact]
    public async Task Xlsx_StructuredReferencesNextToSheetReferencesStayLocal()
    {
        var path = Path.Combine(_directory, "structured.xlsx");
        BuildWorkbook(path,
            Sheet("<sheetData><row r=\"1\"><c r=\"A1\"><f>SUM(Table1[Amount])*Sheet2!B1</f><v>1</v></c>" +
                  "<c r=\"B1\"><f>Table1[[#This Row],[x]]*Sheet2!A1</f><v>1</v></c></row></sheetData>"),
            definedNames: "<definedName name=\"RowCalc\">Table1[[#This Row],[x]]*Sheet2!A1</definedName>" +
                          "<definedName name=\"Total\">SUM(Table1[Amount])*Sheet2!B1</definedName>" +
                          "<definedName name=\"SelfName\">[0]!RowCalc</definedName>");

        var result = await new ExcelCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        var workbook = ReadEntry(path, "xl/workbook.xml");
        Assert.Contains("RowCalc", workbook);
        Assert.Contains("name=\"Total\"", workbook);
        Assert.Contains("SelfName", workbook);
        Assert.Contains("SUM(Table1[Amount])*Sheet2!B1", ReadEntry(path, "xl/worksheets/sheet1.xml"));
    }

    [Fact]
    public async Task Xlsx_QuotedExternalReferenceIsRejected()
    {
        var path = Path.Combine(_directory, "quoted.xlsx");
        BuildWorkbook(path, Sheet("<sheetData><row r=\"1\"><c r=\"A1\"><f>SUM('[1]My Sheet'!A1:A3)</f><v>1</v></c></row></sheetData>"));
        var original = File.ReadAllBytes(path);

        var result = await new ExcelCleaner().CleanAsync(path, new CleanOptions());

        Assert.False(result.Success);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Xlsx_ExternalDefinedNameUsedOnlyByAnotherNameIsRejected()
    {
        var path = Path.Combine(_directory, "chain.xlsx");
        BuildWorkbook(path,
            Sheet("<sheetData><row r=\"1\"><c r=\"A1\"><f>Doubled</f><v>2</v></c></row></sheetData>"),
            definedNames: "<definedName name=\"Remote\">[1]!Rate</definedName><definedName name=\"Doubled\">Remote*2</definedName>");
        var original = File.ReadAllBytes(path);

        var result = await new ExcelCleaner().CleanAsync(path, new CleanOptions());

        Assert.False(result.Success);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    // ---------------------------------------------------------------- 2. внешние ссылки вне ячеек

    [Theory]
    [InlineData("conditionalFormatting")]
    [InlineData("dataValidation")]
    [InlineData("x14DataValidation")]
    [InlineData("chart")]
    public async Task Xlsx_ExternalReferencesOutsideCellsAreRejected(string location)
    {
        var path = Path.Combine(_directory, location + ".xlsx");
        var body = location switch
        {
            "conditionalFormatting" => "<sheetData/><conditionalFormatting sqref=\"A1\"><cfRule type=\"expression\" priority=\"1\">" +
                                       "<formula>[1]Sheet1!$A$1&gt;0</formula></cfRule></conditionalFormatting>",
            "dataValidation" => "<sheetData/><dataValidations count=\"1\"><dataValidation type=\"list\" sqref=\"A1\">" +
                                "<formula1>[1]Sheet1!$A$1:$A$3</formula1></dataValidation></dataValidations>",
            "x14DataValidation" => $"<sheetData/><extLst><ext uri=\"{{CCE6A557-97BC-4b89-ADB6-D9C93CAAB3DF}}\" xmlns:x14=\"{X14}\">" +
                                   $"<x14:dataValidations count=\"1\" xmlns:xm=\"{XM}\"><x14:dataValidation type=\"list\"><x14:formula1>" +
                                   "<xm:f>'[1]Lists'!$A$1:$A$3</xm:f></x14:formula1><xm:sqref>A1</xm:sqref></x14:dataValidation></x14:dataValidations></ext></extLst>",
            _ => "<sheetData/>"
        };
        BuildWorkbook(path, Sheet(body), extra: (_, sheet) =>
        {
            if (location != "chart") return;
            var drawing = sheet.AddNewPart<DrawingsPart>();
            Feed(drawing, "<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\"/>");
            Feed(drawing.AddNewPart<ChartPart>(), $"<c:chartSpace xmlns:c=\"{C}\"><c:chart><c:plotArea><c:barChart><c:ser><c:val><c:numRef>" +
                                                  "<c:f>[1]Sheet1!$A$1:$A$3</c:f></c:numRef></c:val></c:ser></c:barChart></c:plotArea></c:chart></c:chartSpace>");
        });
        var original = File.ReadAllBytes(path);

        var result = await new ExcelCleaner().CleanAsync(path, new CleanOptions());

        Assert.False(result.Success);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    // ---------------------------------------------------------------- 3. таблицы запросов

    [Fact]
    public async Task Xlsx_QueryTableAttachedToTableIsRejected()
    {
        var path = Path.Combine(_directory, "query.xlsx");
        BuildWorkbook(path, Sheet("<sheetData/><tableParts count=\"1\"><tablePart r:id=\"rIdT\"/></tableParts>"), external: false, extra: (workbook, sheet) =>
        {
            Feed(workbook.AddNewPart<ConnectionsPart>(), $"<connections xmlns=\"{S}\"><connection id=\"1\" name=\"Query\" type=\"5\"/></connections>");
            var table = sheet.AddNewPart<TableDefinitionPart>("rIdT");
            Feed(table, $"<table xmlns=\"{S}\" id=\"1\" name=\"Table1\" displayName=\"Table1\" ref=\"A1:A2\" tableType=\"queryTable\">" +
                        "<tableColumns count=\"1\"><tableColumn id=\"1\" name=\"x\" queryTableFieldId=\"1\"/></tableColumns></table>");
            Feed(table.AddNewPart<QueryTablePart>(), $"<queryTable xmlns=\"{S}\" name=\"Query\" connectionId=\"1\"/>");
        });
        var original = File.ReadAllBytes(path);

        var result = await new ExcelCleaner().CleanAsync(path, new CleanOptions());

        Assert.False(result.Success);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    // ---------------------------------------------------------------- 4. скрытые листы во встроенной книге

    [Fact]
    public async Task Docx_EmbeddedChartWorkbookKeepsHiddenSheets()
    {
        var embedded = Path.Combine(_directory, "chart-data.xlsx");
        using (var document = SpreadsheetDocument.Create(embedded, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            Feed(workbookPart, $"<workbook xmlns=\"{S}\" xmlns:r=\"{R}\"><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/>" +
                               "<sheet name=\"Data\" sheetId=\"2\" state=\"hidden\" r:id=\"rId2\"/></sheets></workbook>");
            Feed(workbookPart.AddNewPart<WorksheetPart>("rId1"), Sheet("<sheetData/>"));
            Feed(workbookPart.AddNewPart<WorksheetPart>("rId2"), Sheet("<sheetData><row r=\"1\"><c r=\"A1\"><v>42</v></c></row></sheetData>"));
        }

        var path = Path.Combine(_directory, "chart.docx");
        using (var document = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            Feed(main, $"<w:document xmlns:w=\"{W}\"><w:body><w:p><w:r><w:t>Chart</w:t></w:r></w:p></w:body></w:document>");
            var chart = main.AddNewPart<ChartPart>("rIdChart");
            Feed(chart, $"<c:chartSpace xmlns:c=\"{C}\" xmlns:r=\"{R}\"><c:chart><c:plotArea><c:barChart><c:ser><c:val><c:numRef>" +
                        "<c:f>'Data'!$A$1</c:f></c:numRef></c:val></c:ser></c:barChart></c:plotArea></c:chart>" +
                        "<c:externalData r:id=\"rIdData\"/></c:chartSpace>");
            Feed(chart.AddNewPart<EmbeddedPackagePart>("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "rIdData"),
                File.ReadAllBytes(embedded));
        }

        var result = await new DocxCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.Contains("скрыт", result.Message);
        using var cleaned = WordprocessingDocument.Open(path, false);
        var package = cleaned.MainDocumentPart!.ChartParts.Single().EmbeddedPackagePart!;
        using var stream = new MemoryStream();
        using (var input = package.GetStream(FileMode.Open, FileAccess.Read)) input.CopyTo(stream);
        stream.Position = 0;
        using var workbook = SpreadsheetDocument.Open(stream, false);
        Assert.Equal(2, workbook.WorkbookPart!.WorksheetParts.Count());
        Assert.Contains("name=\"Data\"", ReadPart(workbook.WorkbookPart));
    }

    // ---------------------------------------------------------------- 5. остаточные данные

    [Fact]
    public async Task Xlsx_RemovesRevisionUidsCustomViewsAndCustomProperties()
    {
        var path = Path.Combine(_directory, "views.xlsx");
        var viewGuid = "{11111111-2222-3333-4444-555555555555}";
        BuildWorkbook(path,
            $"<worksheet xmlns=\"{S}\" xmlns:r=\"{R}\" xmlns:xr=\"{XR}\" xmlns:xr2=\"{XR2}\" xmlns:xr3=\"{XR3}\" " +
            "xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" mc:Ignorable=\"xr xr2 xr3\" " +
            "xr:uid=\"{AAAAAAAA-0000-0000-0000-00000000UID1}\"><sheetViews><sheetView workbookViewId=\"0\"/></sheetViews><sheetData/>" +
            $"<customSheetViews><customSheetView guid=\"{viewGuid}\"><pageSetup orientation=\"portrait\"/></customSheetView></customSheetViews>" +
            "<customProperties><customPr name=\"Owner\" r:id=\"rIdCp\"/></customProperties>" +
            "<tableParts count=\"1\"><tablePart r:id=\"rIdT\"/></tableParts></worksheet>",
            external: false,
            workbookTail: $"<customWorkbookViews><customWorkbookView name=\"VIEW-SECRET - Personal View\" guid=\"{viewGuid}\" " +
                          "windowWidth=\"100\" windowHeight=\"100\" activeSheetId=\"1\"/></customWorkbookViews>",
            definedNames: "<definedName name=\"Z_11111111_2222_3333_4444_555555555555_.wvu.PrintArea\" hidden=\"1\" localSheetId=\"0\">Sheet1!$A$1:$B$2</definedName>" +
                          "<definedName name=\"Kept\">Sheet1!$A$1</definedName>",
            extra: (_, sheet) =>
            {
                Feed(sheet.AddNewPart<CustomPropertyPart>("application/vnd.openxmlformats-officedocument.spreadsheetml.customProperty", "rIdCp"),
                    Encoding.Unicode.GetBytes("CUSTPROP-SECRET"));
                Feed(sheet.AddNewPart<TableDefinitionPart>("rIdT"),
                    $"<table xmlns=\"{S}\" xmlns:xr=\"{XR}\" xmlns:xr3=\"{XR3}\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" " +
                    "mc:Ignorable=\"xr xr3\" id=\"1\" xr:uid=\"{BBBBBBBB-0000-0000-0000-00000000UID2}\" name=\"Table1\" displayName=\"Table1\" ref=\"A1:A2\">" +
                    "<autoFilter ref=\"A1:A2\" xr:uid=\"{CCCCCCCC-0000-0000-0000-00000000UID3}\"/><tableColumns count=\"1\">" +
                    "<tableColumn id=\"1\" xr3:uid=\"{DDDDDDDD-0000-0000-0000-00000000UID4}\" name=\"x\"/></tableColumns></table>");
            });

        var before = MetadataAuditService.Audit(path);
        Assert.Contains(before.Findings, f => f.Description.Contains("идентификаторы правок"));
        Assert.Contains(before.Findings, f => f.Description.Contains("представления"));
        Assert.Contains(before.Findings, f => f.Description.Contains("свойства листа"));

        var result = await new ExcelCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        var text = DeepText(path);
        Assert.DoesNotContain("VIEW-SECRET", text);
        Assert.DoesNotContain("CUSTPROP-SECRET", text);
        Assert.DoesNotContain("UID1", text);
        Assert.DoesNotContain("UID2", text);
        Assert.DoesNotContain("UID3", text);
        Assert.DoesNotContain("UID4", text);
        Assert.DoesNotContain(".wvu.", text);
        var workbook = ReadEntry(path, "xl/workbook.xml");
        Assert.Contains("Kept", workbook);
        var sheet = ReadEntry(path, "xl/worksheets/sheet1.xml");
        Assert.DoesNotContain("customSheetViews", sheet);
        Assert.DoesNotContain("customProperties", sheet);
        Assert.Contains("tableParts", sheet);
        using (var cleaned = SpreadsheetDocument.Open(path, false))
            Assert.Empty(cleaned.WorkbookPart!.WorksheetParts.SelectMany(p => p.CustomPropertyParts));
        var after = MetadataAuditService.Audit(path);
        Assert.False(after.HasSensitiveMetadata, after.Summarize(20));
        Assert.False(result.Incomplete);
    }

    [Fact]
    public async Task Xlsx_PrunesUnusedSharedStringsAndRemapsIndexes()
    {
        var path = Path.Combine(_directory, "strings.xlsx");
        using (var document = SpreadsheetDocument.Create(path, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            Feed(workbookPart, $"<workbook xmlns=\"{S}\" xmlns:r=\"{R}\"><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/>" +
                               "<sheet name=\"Secret\" sheetId=\"2\" state=\"hidden\" r:id=\"rId2\"/></sheets></workbook>");
            Feed(workbookPart.AddNewPart<WorksheetPart>("rId1"),
                Sheet("<sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>3</v></c><c r=\"C1\" t=\"s\"><v>3</v></c></row></sheetData>"));
            Feed(workbookPart.AddNewPart<WorksheetPart>("rId2"),
                Sheet("<sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>2</v></c></row></sheetData>"));
            Feed(workbookPart.AddNewPart<SharedStringTablePart>(),
                $"<sst xmlns=\"{S}\" count=\"5\" uniqueCount=\"4\"><si><t>Keep A</t></si><si><t>DELETED-SECRET</t></si>" +
                "<si><t>HIDDEN-SECRET</t></si><si><r><t>Keep</t></r><r><t xml:space=\"preserve\"> B</t></r></si></sst>");
        }

        Assert.Contains(MetadataAuditService.Audit(path).Findings, f => f.Description.Contains("неиспользуемые строки"));

        var result = await new ExcelCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        var text = DeepText(path);
        Assert.DoesNotContain("DELETED-SECRET", text);
        Assert.DoesNotContain("HIDDEN-SECRET", text);
        var strings = ReadEntry(path, "xl/sharedStrings.xml");
        Assert.Contains("count=\"3\"", strings);
        Assert.Contains("uniqueCount=\"2\"", strings);
        Assert.Contains("Keep A", strings);
        Assert.Contains(" B", strings);
        var sheet = ReadEntry(path, "xl/worksheets/sheet1.xml");
        Assert.Contains("<c r=\"A1\" t=\"s\"><v>0</v></c>", sheet);
        Assert.Contains("<c r=\"B1\" t=\"s\"><v>1</v></c>", sheet);
        Assert.Contains("<c r=\"C1\" t=\"s\"><v>1</v></c>", sheet);
        Assert.False(MetadataAuditService.Audit(path).HasSensitiveMetadata, MetadataAuditService.Audit(path).Summarize(20));
    }

    [Fact]
    public async Task Xlsx_SharedStringsWithUnexpectedIndexAreLeftIntact()
    {
        var path = Path.Combine(_directory, "strings-bad.xlsx");
        using (var document = SpreadsheetDocument.Create(path, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            Feed(workbookPart, $"<workbook xmlns=\"{S}\" xmlns:r=\"{R}\"><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Feed(workbookPart.AddNewPart<WorksheetPart>("rId1"),
                Sheet("<sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>1</v></c><c r=\"B1\" t=\"s\"><v>oops</v></c></row></sheetData>"));
            Feed(workbookPart.AddNewPart<SharedStringTablePart>(),
                $"<sst xmlns=\"{S}\" count=\"1\" uniqueCount=\"2\"><si><t>UNUSED-TEXT</t></si><si><t>Used</t></si></sst>");
        }

        var result = await new ExcelCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.Contains("UNUSED-TEXT", ReadEntry(path, "xl/sharedStrings.xml"));
        Assert.Contains("<v>1</v>", ReadEntry(path, "xl/worksheets/sheet1.xml"));
    }

    [Fact]
    public async Task Xlsx_PowerQueryRemovalIsReported()
    {
        var path = Path.Combine(_directory, "mashup.xlsx");
        BuildWorkbook(path, Sheet("<sheetData/>"), external: false, extra: (workbook, _) =>
        {
            var custom = workbook.AddCustomXmlPart(CustomXmlPartType.CustomXml);
            Feed(custom, Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(
                "<?xml version=\"1.0\" encoding=\"utf-16\"?><DataMashup xmlns=\"http://schemas.microsoft.com/DataMashup\">AAAAAA==</DataMashup>")).ToArray());
        });

        var result = await new ExcelCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.Contains("Power Query", result.Message);
        Assert.DoesNotContain("DataMashup", DeepText(path));
    }

    // ---------------------------------------------------------------- 6. неполная очистка

    [Fact]
    public async Task Xlsx_ResultIsIncompleteWhenAuditStillFindsMetadata()
    {
        var path = Path.Combine(_directory, "kept-hidden.xlsx");
        using (var document = SpreadsheetDocument.Create(path, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            Feed(workbookPart, $"<workbook xmlns=\"{S}\" xmlns:r=\"{R}\"><sheets><sheet name=\"Visible\" sheetId=\"1\" r:id=\"rId1\"/>" +
                               "<sheet name=\"Lookup\" sheetId=\"2\" state=\"hidden\" r:id=\"rId2\"/></sheets></workbook>");
            Feed(workbookPart.AddNewPart<WorksheetPart>("rId1"), Sheet("<sheetData><row r=\"1\"><c r=\"A1\"><f>Lookup!A1</f><v>1</v></c></row></sheetData>"));
            Feed(workbookPart.AddNewPart<WorksheetPart>("rId2"), Sheet("<sheetData><row r=\"1\"><c r=\"A1\"><v>1</v></c></row></sheetData>"));
        }

        var result = await new ExcelCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.True(result.Incomplete);
    }

    [Fact]
    public async Task Xlsx_CleanWorkbookIsComplete()
    {
        var path = Path.Combine(_directory, "plain.xlsx");
        BuildWorkbook(path, Sheet("<sheetData><row r=\"1\"><c r=\"A1\"><v>1</v></c></row></sheetData>"), external: false);

        var result = await new ExcelCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.False(result.Incomplete, result.Message);
    }

    // ---------------------------------------------------------------- fixtures

    private static string Sheet(string body) => $"<worksheet xmlns=\"{S}\" xmlns:r=\"{R}\">{body}</worksheet>";

    /// <summary>
    /// Книга из двух листов (Sheet1, Sheet2); при <paramref name="external"/> — со связью с внешней книгой,
    /// которую очистка удаляет.
    /// </summary>
    private static void BuildWorkbook(string path, string sheet1, string? definedNames = null, bool external = true,
        string workbookTail = "", Action<WorkbookPart, WorksheetPart>? extra = null)
    {
        using var document = SpreadsheetDocument.Create(path, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        Feed(workbookPart,
            $"<workbook xmlns=\"{S}\" xmlns:r=\"{R}\"><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/>" +
            "<sheet name=\"Sheet2\" sheetId=\"2\" r:id=\"rId2\"/></sheets>" +
            (external ? "<externalReferences><externalReference r:id=\"rIdExt\"/></externalReferences>" : "") +
            (definedNames == null ? "" : "<definedNames>" + definedNames + "</definedNames>") +
            workbookTail + "</workbook>");
        var sheet = workbookPart.AddNewPart<WorksheetPart>("rId1");
        Feed(sheet, sheet1);
        Feed(workbookPart.AddNewPart<WorksheetPart>("rId2"), Sheet("<sheetData/>"));
        if (external)
        {
            var link = workbookPart.AddNewPart<ExternalWorkbookPart>("rIdExt");
            Feed(link, $"<externalLink xmlns=\"{S}\" xmlns:r=\"{R}\"><externalBook r:id=\"rIdPath\"><sheetNames><sheetName val=\"Sheet1\"/>" +
                       "</sheetNames></externalBook></externalLink>");
            link.AddExternalRelationship(R + "/externalLinkPath", new Uri("file:///C:/Users/EXT-SECRET/Other.xlsx"), "rIdPath");
        }
        extra?.Invoke(workbookPart, sheet);
    }

    private static void Feed(OpenXmlPart part, string xml) => Feed(part, Encoding.UTF8.GetBytes(xml));

    private static void Feed(OpenXmlPart part, byte[] data)
    {
        using var stream = new MemoryStream(data);
        part.FeedData(stream);
    }

    private static string ReadPart(OpenXmlPart part)
    {
        using var reader = new StreamReader(part.GetStream(FileMode.Open, FileAccess.Read));
        return reader.ReadToEnd();
    }

    private static string ReadEntry(string path, string entryName)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry(entryName) ?? throw new FileNotFoundException(entryName);
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    /// <summary>Текст всех частей пакета, включая вложенные архивы (Latin-1 и UTF-16).</summary>
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
        return text.ToString();
    }
}
