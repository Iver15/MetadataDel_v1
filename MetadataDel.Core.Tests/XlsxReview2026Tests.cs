using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MetadataDel.Core.Audit;
using MetadataDel.Core.Cleaning;
using MetadataDel.Core.Excel;
using Xunit;

namespace MetadataDel.Core.Tests;

/// <summary>Регрессии очистки XLSX по ревью: таблицы на скрытых листах, повреждённый VML, параметры общего доступа.</summary>
public sealed class XlsxReview2026Tests : IDisposable
{
    private const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string C = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    private const string X14 = "http://schemas.microsoft.com/office/spreadsheetml/2009/9/main";
    private const string XM = "http://schemas.microsoft.com/office/excel/2006/main";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mdel-xlsx-review2026", Guid.NewGuid().ToString("N"));

    public XlsxReview2026Tests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch { }
    }

    // ---------------------------------------------------------------- 1. таблица (ListObject) на скрытом листе

    [Theory]
    [InlineData("vlookup")]
    [InlineData("structured")]
    [InlineData("byTableName")]
    [InlineData("pivot")]
    [InlineData("conditionalFormatting")]
    [InlineData("dataValidationIndirect")]
    [InlineData("x14DataValidation")]
    [InlineData("definedName")]
    [InlineData("chart")]
    public async Task Xlsx_HiddenSheetWithTableUsedElsewhereIsKept(string location)
    {
        var path = Path.Combine(_directory, location + ".xlsx");
        var body = location switch
        {
            "vlookup" => "<sheetData><row r=\"2\"><c r=\"B2\"><f>VLOOKUP(A2,tblRates,2,0)</f><v>1</v></c></row></sheetData>",
            "structured" => "<sheetData><row r=\"1\"><c r=\"A1\"><f>SUM(TBLRATES[Amt])</f><v>1</v></c></row></sheetData>",
            "byTableName" => "<sheetData><row r=\"1\"><c r=\"A1\"><f>SUM(Table7[Amt])</f><v>1</v></c></row></sheetData>",
            "conditionalFormatting" => "<sheetData/><conditionalFormatting sqref=\"A1\"><cfRule type=\"expression\" priority=\"1\">" +
                                       "<formula>COUNTIF(tblRates[Code],A1)&gt;0</formula></cfRule></conditionalFormatting>",
            "dataValidationIndirect" => "<sheetData/><dataValidations count=\"1\"><dataValidation type=\"list\" sqref=\"A1\">" +
                                        "<formula1>INDIRECT(\"tblRates[Code]\")</formula1></dataValidation></dataValidations>",
            "x14DataValidation" => $"<sheetData/><extLst><ext uri=\"{{CCE6A557-97BC-4b89-ADB6-D9C93CAAB3DF}}\" xmlns:x14=\"{X14}\">" +
                                   $"<x14:dataValidations count=\"1\" xmlns:xm=\"{XM}\"><x14:dataValidation type=\"list\"><x14:formula1>" +
                                   "<xm:f>tblRates[Code]</xm:f></x14:formula1><xm:sqref>A1</xm:sqref></x14:dataValidation></x14:dataValidations></ext></extLst>",
            _ => "<sheetData/>"
        };
        var definedNames = location == "definedName" ? "<definedNames><definedName name=\"RateCodes\">tblRates[Code]</definedName></definedNames>" : "";
        BuildHiddenTableWorkbook(path, Sheet(body), definedNames, extra: (workbookPart, main) =>
        {
            if (location == "pivot")
            {
                Feed(workbookPart.AddNewPart<PivotTableCacheDefinitionPart>("rIdPc"),
                    $"<pivotCacheDefinition xmlns=\"{S}\" xmlns:r=\"{R}\"><cacheSource type=\"worksheet\"><worksheetSource name=\"tblRates\"/></cacheSource>" +
                    "<cacheFields count=\"1\"><cacheField name=\"Code\" numFmtId=\"0\"><sharedItems/></cacheField></cacheFields></pivotCacheDefinition>");
            }
            if (location == "chart")
            {
                var drawing = main.AddNewPart<DrawingsPart>();
                Feed(drawing, "<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\"/>");
                Feed(drawing.AddNewPart<ChartPart>(), $"<c:chartSpace xmlns:c=\"{C}\"><c:chart><c:plotArea><c:barChart><c:ser><c:val><c:numRef>" +
                                                      "<c:f>tblRates[Amt]</c:f></c:numRef></c:val></c:ser></c:barChart></c:plotArea></c:chart></c:chartSpace>");
            }
        });

        var result = await new ExcelCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.Contains("name=\"Rates\"", ReadEntry(path, "xl/workbook.xml"));
        using var document = SpreadsheetDocument.Open(path, false);
        Assert.Equal(2, document.WorkbookPart!.WorksheetParts.Count());
        Assert.Contains("«Rates»", result.Message);
        Assert.Contains("оставлен", result.Message);
    }

    [Fact]
    public async Task Xlsx_HiddenSheetWithTableMentionedOnlyAsTextIsRemoved()
    {
        var path = Path.Combine(_directory, "text-only.xlsx");
        // Имя таблицы в строковом литерале, как часть другого имени и в тексте ячейки — это не ссылка на таблицу.
        BuildHiddenTableWorkbook(path,
            Sheet("<sheetData><row r=\"1\"><c r=\"A1\"><f>LEN(\"tblRates\")+tblRatesOld</f><v>1</v></c>" +
                  "<c r=\"B1\" t=\"inlineStr\"><is><t>tblRates</t></is></c></row></sheetData>"),
            "<definedNames><definedName name=\"tblRatesOld\">Main!$C$1</definedName></definedNames>");

        var result = await new ExcelCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain("name=\"Rates\"", ReadEntry(path, "xl/workbook.xml"));
        using var document = SpreadsheetDocument.Open(path, false);
        Assert.Single(document.WorkbookPart!.WorksheetParts);
    }

    [Fact]
    public async Task Xlsx_PivotOverTableIsNotReportedAsDetached()
    {
        var path = Path.Combine(_directory, "pivot-table.xlsx");
        using (var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            Feed(workbookPart, $"<workbook xmlns=\"{S}\" xmlns:r=\"{R}\"><sheets><sheet name=\"Main\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            var main = workbookPart.AddNewPart<WorksheetPart>("rId1");
            Feed(main, Sheet(RatesData + "<tableParts count=\"1\"><tablePart r:id=\"rIdT\"/></tableParts>"));
            Feed(main.AddNewPart<TableDefinitionPart>("rIdT"), RatesTable);
            Feed(workbookPart.AddNewPart<PivotTableCacheDefinitionPart>("rIdPc"),
                $"<pivotCacheDefinition xmlns=\"{S}\" xmlns:r=\"{R}\"><cacheSource type=\"worksheet\"><worksheetSource name=\"tblRates\"/></cacheSource>" +
                "<cacheFields count=\"1\"><cacheField name=\"Code\" numFmtId=\"0\"><sharedItems/></cacheField></cacheFields></pivotCacheDefinition>");
        }

        var result = await new ExcelCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.False(result.Incomplete, result.Message);
        Assert.DoesNotContain("копию исходных данных", result.Message ?? "");
    }

    // ---------------------------------------------------------------- 2. повреждённый VML с примечаниями

    [Fact]
    public async Task Xlsx_MalformedCommentVmlDoesNotFailCleaning()
    {
        var path = Path.Combine(_directory, "bad-vml.xlsx");
        using (var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            Feed(workbookPart, $"<workbook xmlns=\"{S}\" xmlns:r=\"{R}\"><sheets><sheet name=\"Main\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            var main = workbookPart.AddNewPart<WorksheetPart>("rId1");
            Feed(main, Sheet("<sheetData><row r=\"1\"><c r=\"A1\"><v>1</v></c></row></sheetData><legacyDrawing r:id=\"rIdVml\"/>"));
            Feed(main.AddNewPart<WorksheetCommentsPart>("rIdCm"),
                $"<comments xmlns=\"{S}\"><authors><author>Ivan Petrov</author></authors><commentList>" +
                "<comment ref=\"A1\" authorId=\"0\"><text><t>note</t></text></comment></commentList></comments>");
            // Сторонний генератор: незакрытый <br> и необъявленный префикс — XML не разбирается.
            Feed(main.AddNewPart<VmlDrawingPart>("rIdVml"),
                "<xml xmlns:v=\"urn:schemas-microsoft-com:vml\"><v:shape id=\"_x0000_s1025\"><x:ClientData ObjectType=\"Note\">" +
                "<x:Row>0</x:Row><br></x:ClientData></v:shape></xml>");
        }

        var result = await new ExcelCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.True(result.Incomplete);
        Assert.Contains("VML", result.Message);
        using var cleaned = SpreadsheetDocument.Open(path, false);
        var sheet = cleaned.WorkbookPart!.WorksheetParts.Single();
        Assert.Null(sheet.WorksheetCommentsPart);
    }

    // ---------------------------------------------------------------- 3. fileSharing

    [Fact]
    public async Task Xlsx_FileSharingKeepsReadOnlyRecommendationAndDropsUserName()
    {
        var path = Path.Combine(_directory, "sharing.xlsx");
        BuildSharingWorkbook(path, "readOnlyRecommended=\"1\" userName=\"Ivan Petrov\" algorithmName=\"SHA-512\" " +
                                   "hashValue=\"aGFzaA==\" saltValue=\"c2FsdA==\" spinCount=\"100000\"");

        var result = await new ExcelCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.False(result.Incomplete, result.Message);
        var workbook = ReadEntry(path, "xl/workbook.xml");
        Assert.Contains("<fileSharing", workbook);
        Assert.Contains("readOnlyRecommended=\"1\"", workbook);
        Assert.Contains("hashValue=\"aGFzaA==\"", workbook);
        Assert.Contains("spinCount=\"100000\"", workbook);
        Assert.DoesNotContain("userName", workbook);
        Assert.DoesNotContain("Ivan Petrov", DeepText(path));
        using var document = SpreadsheetDocument.Open(path, false);
        var errors = new OpenXmlValidator(FileFormatVersions.Microsoft365).Validate(document)
            .Select(e => $"{e.Part?.Uri} {e.Path?.XPath}: {e.Description}").ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void Audit_FileSharingFlagsOnlyUserName()
    {
        var withoutUser = Path.Combine(_directory, "sharing-plain.xlsx");
        BuildSharingWorkbook(withoutUser, "readOnlyRecommended=\"1\" reservationPassword=\"CC1A\"");
        var withUser = Path.Combine(_directory, "sharing-user.xlsx");
        BuildSharingWorkbook(withUser, "readOnlyRecommended=\"1\" userName=\"Ivan Petrov\"");

        var plain = MetadataAuditService.Audit(withoutUser);
        var named = MetadataAuditService.Audit(withUser);

        Assert.DoesNotContain(plain.Findings, f => f.Description.Contains("служебные метаданные книги"));
        Assert.DoesNotContain(plain.Findings, f => f.Description.Contains("имена пользователей"));
        Assert.Contains(named.Findings, f => f.Description.Contains("имена пользователей") || f.Description.Contains("служебные метаданные книги"));
    }

    // ---------------------------------------------------------------- fixtures

    private const string RatesData = "<sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Code</t></is></c><c r=\"B1\" t=\"inlineStr\"><is><t>Amt</t></is></c></row>" +
                                     "<row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>A</t></is></c><c r=\"B2\"><v>5</v></c></row></sheetData>";

    private const string RatesTable = $"<table xmlns=\"{S}\" id=\"1\" name=\"Table7\" displayName=\"tblRates\" ref=\"A1:B2\"><autoFilter ref=\"A1:B2\"/>" +
                                      "<tableColumns count=\"2\"><tableColumn id=\"1\" name=\"Code\"/><tableColumn id=\"2\" name=\"Amt\"/></tableColumns></table>";

    private static string Sheet(string body) => $"<worksheet xmlns=\"{S}\" xmlns:r=\"{R}\">{body}</worksheet>";

    /// <summary>Видимый лист Main и скрытый лист Rates с таблицей Table7 (отображаемое имя tblRates).</summary>
    private static void BuildHiddenTableWorkbook(string path, string main, string definedNames, Action<WorkbookPart, WorksheetPart>? extra = null)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        Feed(workbookPart, $"<workbook xmlns=\"{S}\" xmlns:r=\"{R}\"><sheets><sheet name=\"Main\" sheetId=\"1\" r:id=\"rId1\"/>" +
                           "<sheet name=\"Rates\" sheetId=\"2\" state=\"hidden\" r:id=\"rId2\"/></sheets>" + definedNames + "</workbook>");
        var mainPart = workbookPart.AddNewPart<WorksheetPart>("rId1");
        Feed(mainPart, main);
        var rates = workbookPart.AddNewPart<WorksheetPart>("rId2");
        Feed(rates, Sheet(RatesData + "<tableParts count=\"1\"><tablePart r:id=\"rIdT\"/></tableParts>"));
        Feed(rates.AddNewPart<TableDefinitionPart>("rIdT"), RatesTable);
        extra?.Invoke(workbookPart, mainPart);
    }

    private static void BuildSharingWorkbook(string path, string sharingAttributes)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        Feed(workbookPart, $"<workbook xmlns=\"{S}\" xmlns:r=\"{R}\"><fileSharing {sharingAttributes}/>" +
                           "<sheets><sheet name=\"Main\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Feed(workbookPart.AddNewPart<WorksheetPart>("rId1"), Sheet("<sheetData/>"));
    }

    private static void Feed(OpenXmlPart part, string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        part.FeedData(stream);
    }

    private static string ReadEntry(string path, string entryName)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry(entryName) ?? throw new FileNotFoundException(entryName);
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    /// <summary>Текст всех частей пакета (Latin-1 и UTF-16).</summary>
    private static string DeepText(string path)
    {
        var text = new StringBuilder();
        using var archive = ZipFile.OpenRead(path);
        foreach (var entry in archive.Entries)
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            text.Append('\n').Append(Encoding.Latin1.GetString(buffer.ToArray())).Append('\n').Append(Encoding.Unicode.GetString(buffer.ToArray()));
        }
        return text.ToString();
    }
}
