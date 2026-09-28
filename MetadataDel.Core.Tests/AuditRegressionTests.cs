using System.Text;
using DocumentFormat.OpenXml.Packaging;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using MetadataDel.Core.Audit;
using MetadataDel.Core.Cleaning;
using MetadataDel.Core.Excel;
using MetadataDel.Core.Pdf;
using MetadataDel.Core.Word;
using Xunit;
using W = DocumentFormat.OpenXml.Wordprocessing;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace MetadataDel.Core.Tests;

public sealed partial class MetadataCleaningTests
{
    [Fact]
    public void Audit_RejectsUnsupportedFormat()
    {
        var path = Path.Combine(_tempDirectory, "unknown.txt");
        File.WriteAllText(path, "Author: Private");
        Assert.Throws<NotSupportedException>(() => MetadataAuditService.Audit(path));
    }

    [Fact]
    public async Task Pdf_BackupFailurePreservesOriginal()
    {
        var path = Path.Combine(_tempDirectory, "backup.pdf");
        CreatePdf(path);
        var original = File.ReadAllBytes(path);
        Directory.CreateDirectory(path + ".bak");
        var result = await new PdfCleaner().CleanAsync(path, new CleanOptions(Backup: true));
        Assert.False(result.Success);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Pdf_PreservesFilledFormText()
    {
        var path = Path.Combine(_tempDirectory, "form.pdf");
        CreateComplexPdf(path);
        var result = await new PdfCleaner().CleanAsync(path, new CleanOptions());
        Assert.True(result.Success, result.Message);
        using var document = new PdfDocument(new PdfReader(path));
        Assert.Contains("secret value", PdfTextExtractor.GetTextFromPage(document.GetFirstPage()));
    }

    [Fact]
    public async Task Docx_FailureDoesNotPartiallyCleanOriginal()
    {
        var path = Path.Combine(_tempDirectory, "broken.docx");
        CreateStructuredDocx(path);
        using (var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry("word/header1.xml")!;
            using var stream = entry.Open();
            stream.SetLength(0);
            stream.Write(Encoding.UTF8.GetBytes("<broken"));
        }
        var original = File.ReadAllBytes(path);
        var result = await new DocxCleaner().CleanAsync(path, new CleanOptions());
        Assert.False(result.Success);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("xlsx")]
    public async Task OpenXml_RemovesIdentifierAndVersion(string extension)
    {
        var path = Path.Combine(_tempDirectory, "identity." + extension);
        if (extension == "docx") CreateDocx(path); else CreateWorkbookArtifactXlsx(path);
        using (OpenXmlPackage document = extension == "docx"
                   ? WordprocessingDocument.Open(path, true) : SpreadsheetDocument.Open(path, true))
        {
            document.PackageProperties.Identifier = "internal-customer-123";
            document.PackageProperties.Version = "private-version";
        }
        IFileCleaner cleaner = extension == "docx" ? new DocxCleaner() : new ExcelCleaner();
        Assert.True((await cleaner.CleanAsync(path, new CleanOptions())).Success);
        using OpenXmlPackage cleaned = extension == "docx"
            ? WordprocessingDocument.Open(path, false) : SpreadsheetDocument.Open(path, false);
        Assert.True(string.IsNullOrEmpty(cleaned.PackageProperties.Identifier));
        Assert.True(string.IsNullOrEmpty(cleaned.PackageProperties.Version));
    }

    [Fact]
    public async Task Docx_RemovesPreviousFormatting()
    {
        var path = Path.Combine(_tempDirectory, "revision.docx");
        CreateDocx(path);
        using (var document = WordprocessingDocument.Open(path, true))
        {
            document.MainDocumentPart!.Document.Body!.Descendants<W.Run>().First().PrependChild(
                new W.RunProperties(new W.RunPropertiesChange(new W.PreviousRunProperties(new W.Bold()))
                { Id = "7", Author = "Private" }));
        }
        Assert.True((await new DocxCleaner().CleanAsync(path, new CleanOptions())).Success);
        Assert.DoesNotContain("rPrChange", ReadZipEntry(path, "word/document.xml"));
    }

    [Fact]
    public async Task Xlsx_PreservesLocalStructuredDefinedName()
    {
        var path = Path.Combine(_tempDirectory, "formula.xlsx");
        CreateWorkbookArtifactXlsx(path);
        using (var document = SpreadsheetDocument.Open(path, true))
        {
            document.WorkbookPart!.Workbook.GetFirstChild<S.DefinedNames>()!.AppendChild(
                new S.DefinedName { Name = "LocalTotal", Text = "SUM(Table1[Amount])" });
        }
        Assert.True((await new ExcelCleaner().CleanAsync(path, new CleanOptions())).Success);
        Assert.Contains("SUM(Table1[Amount])", ReadZipEntry(path, "xl/workbook.xml"));
    }

    [Fact]
    public async Task CancelledCleanupPreservesOriginal()
    {
        var path = Path.Combine(_tempDirectory, "cancel.docx");
        CreateDocx(path);
        var original = File.ReadAllBytes(path);
        var result = await new DocxCleaner().CleanAsync(path, new CleanOptions(), new CancellationToken(true));
        Assert.False(result.Success);
        Assert.Equal(original, File.ReadAllBytes(path));
    }
}

public sealed partial class MetadataCleaningTests
{
    [Theory]
    [InlineData("doc")]
    [InlineData("xls")]
    public async Task Ole_RemovesPropertyBytesAndPreservesContent(string extension)
    {
        var path = Path.Combine(_tempDirectory, "legacy." + extension);
        var secret = "PRIVATE-OWNER-unique-marker";
        using (var root = OpenMcdf.RootStorage.Create(path))
        {
            using (var properties = root.CreateStream("\x05SummaryInformation"))
                properties.Write(Encoding.UTF8.GetBytes(secret));
            using (var content = root.CreateStream("Workbook"))
                content.Write(Encoding.UTF8.GetBytes("Visible workbook content"));
        }
        using (var before = OpenMcdf.RootStorage.OpenRead(path))
            Assert.True(before.ContainsEntry("Workbook"), string.Join(",", before.EnumerateEntries().Select(e => e.Name)));
        Assert.True(MetadataAuditService.Audit(path).HasSensitiveMetadata);
        var result = await new MetadataDel.Core.Ole.OleDocumentCleaner().CleanAsync(path, new CleanOptions(Backup: true));
        Assert.True(result.Success, result.Message);
        Assert.False(MetadataAuditService.Audit(path).HasSensitiveMetadata);
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(File.ReadAllBytes(path)));
        using var cleaned = OpenMcdf.RootStorage.OpenRead(path);
        using var reader = new StreamReader(cleaned.OpenStream("Workbook"));
        Assert.Equal("Visible workbook content", reader.ReadToEnd());
        Assert.Contains(secret, Encoding.UTF8.GetString(File.ReadAllBytes(path + ".bak")));
    }

    [Fact]
    public async Task Doc_KeepsEveryNonPropertyStreamOfWordLayout()
    {
        // Culture-sensitive StartsWith("\x05") is true for any name under ICU and emptied real .doc files.
        var path = Path.Combine(_tempDirectory, "word97.doc");
        var wordClsid = new Guid("00020906-0000-0000-C000-000000000046");
        var streams = new Dictionary<string, byte[]>
        {
            ["WordDocument"] = Enumerable.Range(0, 20_000).Select(i => (byte)(i * 7)).ToArray(),
            ["1Table"] = Enumerable.Range(0, 9_000).Select(i => (byte)(i * 13)).ToArray(),
            ["Data"] = Encoding.UTF8.GetBytes("picture data"),
            ["\u0001CompObj"] = Encoding.UTF8.GetBytes("Microsoft Word 97-2003"),
        };
        var nested = Encoding.UTF8.GetBytes("<customXml/>");
        using (var root = OpenMcdf.RootStorage.Create(path))
        {
            root.CLSID = wordClsid;
            foreach (var (name, bytes) in streams)
                using (var stream = root.CreateStream(name)) stream.Write(bytes);
            foreach (var name in new[] { "\u0005SummaryInformation", "\u0005DocumentSummaryInformation" })
                using (var stream = root.CreateStream(name)) stream.Write(Encoding.UTF8.GetBytes("PRIVATE-AUTHOR"));
            using (var item = root.CreateStorage("MsoDataStore").CreateStorage("item0").CreateStream("Item"))
                item.Write(nested);
        }

        var result = await new MetadataDel.Core.Ole.OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        using var cleaned = OpenMcdf.RootStorage.OpenRead(path);
        Assert.Equal(wordClsid, cleaned.CLSID);
        Assert.Equal(
            streams.Keys.Append("MsoDataStore").Order(StringComparer.Ordinal),
            cleaned.EnumerateEntries().Select(e => e.Name).Order(StringComparer.Ordinal));
        foreach (var (name, bytes) in streams)
            Assert.Equal(bytes, ReadAll(cleaned.OpenStream(name)));
        Assert.Equal(nested, ReadAll(cleaned.OpenStorage("MsoDataStore").OpenStorage("item0").OpenStream("Item")));
    }

    private static byte[] ReadAll(Stream stream)
    {
        using (stream)
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
    }

    [Fact]
    public async Task Backup_DoesNotOverwritePreviousOriginal()
    {
        var path = Path.Combine(_tempDirectory, "repeat.docx");
        CreateDocx(path);
        var original = File.ReadAllBytes(path);
        var cleaner = new DocxCleaner();
        Assert.True((await cleaner.CleanAsync(path, new CleanOptions(Backup: true))).Success);
        Assert.True((await cleaner.CleanAsync(path, new CleanOptions(Backup: true))).Success);
        Assert.Equal(original, File.ReadAllBytes(path + ".bak"));
        Assert.True(File.Exists(path + ".2.bak"));
    }

    [Fact]
    public async Task Xlsx_ExternalFormulaFailurePreservesOriginal()
    {
        var path = Path.Combine(_tempDirectory, "external.xlsx");
        CreateWorkbookArtifactXlsx(path);
        using (var document = SpreadsheetDocument.Open(path, true))
        {
            var cell = document.WorkbookPart!.WorksheetParts.First().Worksheet.Descendants<S.Cell>().First();
            cell.CellFormula = new S.CellFormula("[1]Sheet1!A1");
        }
        var original = File.ReadAllBytes(path);
        var result = await new ExcelCleaner().CleanAsync(path, new CleanOptions());
        Assert.False(result.Success);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Xlsx_PreservesVmlControlsWhileRemovingNotes()
    {
        var path = Path.Combine(_tempDirectory, "controls.xlsx");
        CreateWorkbookArtifactXlsx(path);
        string drawingPath;
        using (var document = SpreadsheetDocument.Open(path, true))
        {
            var worksheet = document.WorkbookPart!.WorksheetParts.First();
            var vml = worksheet.AddNewPart<VmlDrawingPart>();
            drawingPath = vml.Uri.ToString().TrimStart('/');
            using var stream = vml.GetStream(FileMode.Create);
            stream.Write(Encoding.UTF8.GetBytes("""
                <xml xmlns:v="urn:schemas-microsoft-com:vml" xmlns:x="urn:schemas-microsoft-com:office:excel">
                  <v:shape id="note"><x:ClientData ObjectType="Note" /></v:shape>
                  <v:shape id="button"><x:ClientData ObjectType="Button" /></v:shape>
                </xml>
                """));
            worksheet.Worksheet.AppendChild(new S.LegacyDrawing { Id = worksheet.GetIdOfPart(vml) });
        }
        var result = await new ExcelCleaner().CleanAsync(path, new CleanOptions());
        Assert.True(result.Success, result.Message);
        Assert.Contains("Button", ReadZipEntry(path, drawingPath));
        Assert.DoesNotContain("Note", ReadZipEntry(path, drawingPath));
        using var cleaned = SpreadsheetDocument.Open(path, false);
        Assert.NotNull(cleaned.WorkbookPart!.WorksheetParts.First().Worksheet.GetFirstChild<S.LegacyDrawing>());
    }

    [Fact]
    public void Arguments_ExpandUppercaseAndDeduplicatePaths()
    {
        var path = Path.Combine(_tempDirectory, "UPPER.PDF");
        CreatePdf(path);
        var parsed = MetadataDel.Core.CommandLine.CliArguments.Parse(new[] { "--audit", "--log", _tempDirectory, path });
        Assert.True(parsed.Audit);
        Assert.True(parsed.Log);
        Assert.Equal(new[] { path }, parsed.Paths);
    }

    [Theory]
    [InlineData("--backup=typo")]
    [InlineData("--backupp")]
    [InlineData("--unknown")]
    public void Arguments_RejectUnknownOptions(string option)
    {
        Assert.Throws<ArgumentException>(() => MetadataDel.Core.CommandLine.CliArguments.Parse(new[] { option }));
    }

    [Fact]
    public void Arguments_PreserveWhitespaceAndSupportEndOfOptions()
    {
        var parsed = MetadataDel.Core.CommandLine.CliArguments.Parse(new[] { "--", "  spaced.pdf", "--audit" });
        Assert.False(parsed.Audit);
        Assert.Equal(Path.GetFullPath("  spaced.pdf"), parsed.Paths[0]);
        Assert.Equal(Path.GetFullPath("--audit"), parsed.Paths[1]);
    }

    [Fact]
    public void Arguments_DoNotFollowDirectorySymlinkLoops()
    {
        if (OperatingSystem.IsWindows()) return; // Symlink privilege is not guaranteed in Windows CI.
        var path = Path.Combine(_tempDirectory, "input.pdf");
        CreatePdf(path);
        Directory.CreateSymbolicLink(Path.Combine(_tempDirectory, "loop"), _tempDirectory);
        var parsed = MetadataDel.Core.CommandLine.CliArguments.Parse(new[] { _tempDirectory });
        Assert.Equal(new[] { path }, parsed.Paths);
    }

    [Fact]
    public void Pdf_AuditDetectsCustomInfoAndDates()
    {
        var path = Path.Combine(_tempDirectory, "dated.pdf");
        using (var document = new PdfDocument(new PdfWriter(path)))
        {
            document.AddNewPage();
            document.GetDocumentInfo().SetMoreInfo("PrivateOwner", "Customer 123");
        }
        var audit = MetadataAuditService.Audit(path);
        Assert.Contains(audit.Findings, f => f.Description.Contains("PrivateOwner"));
        Assert.Contains(audit.Findings, f => f.Description.Contains("CreationDate"));
    }
}

public sealed partial class MetadataCleaningTests
{
    [Fact]
    public async Task Pdf_ExiftoolDrainsBothOutputStreams()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_tempDirectory, "process.pdf");
        CreatePdf(path);
        var tool = Path.Combine(_tempDirectory, "fake-exiftool");
        File.WriteAllText(tool, "#!/bin/sh\n/usr/bin/yes output | /usr/bin/head -c 262144\n/usr/bin/yes error | /usr/bin/head -c 262144 >&2\nexit 0\n");
        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var previous = Environment.GetEnvironmentVariable("EXIFTOOL_PATH");
        try
        {
            Environment.SetEnvironmentVariable("EXIFTOOL_PATH", tool);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = await new PdfCleaner().CleanAsync(path, new CleanOptions(AggressivePdf: true), timeout.Token);
            Assert.True(result.Success, result.Message);
            Assert.Null(result.Message);
        }
        finally { Environment.SetEnvironmentVariable("EXIFTOOL_PATH", previous); }
    }
}
