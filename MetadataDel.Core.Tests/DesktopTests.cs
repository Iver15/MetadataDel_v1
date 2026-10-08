using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MetadataDel.Core.Cleaning;
using MetadataDel.Core.CommandLine;
using Xunit;

namespace MetadataDel.Core.Tests;

public sealed class DesktopTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "mdel-desktop-" + Guid.NewGuid());
    public DesktopTests() => Directory.CreateDirectory(dir);
    public void Dispose() => Directory.Delete(dir, true);
    private string Document(string name = "-Договор 'пример'.docx")
    {
        var path = Path.Combine(dir, name);
        using var doc = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        doc.AddMainDocumentPart().Document = new Document(new Body(new Paragraph(new Run(new Text("Содержание")))));
        doc.PackageProperties.Creator = "Private author";
        return path;
    }
    [Fact]
    public void OnlyEmptyArgumentsOpenWindow()
    {
        Assert.True(DesktopEntryPoint.ShouldOpenMainWindow([]));
        foreach (var args in new[] { new[] { "--log", "file.pdf" }, new[] { "--install" }, new[] { "--audit", "file.pdf" }, new[] { "--", "--document.pdf" } })
            Assert.False(DesktopEntryPoint.ShouldOpenMainWindow(args));
    }
    [Fact]
    public async Task GuiCleansWithUniqueBackupsAndStructuredResult()
    {
        var path = Document();
        var original = File.ReadAllBytes(path);
        using var output = new StringWriter();
        Assert.Equal(0, await GuiCleanCommand.RunAsync(["--gui-clean", "--backup=on", "--", path], output, TextWriter.Null));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(path, json.RootElement.GetProperty("inputPath").GetString());
        Assert.Equal(path, json.RootElement.GetProperty("outputPath").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(original, File.ReadAllBytes(path + ".bak"));
        Assert.True((await new SingleFileCleaningService().CleanAsync(path, new CleanOptions(Backup: true))).Success);
        Assert.True(File.Exists(path + ".2.bak"));
        Assert.Equal(original, File.ReadAllBytes(path + ".bak"));
    }
    [Fact]
    public async Task InvalidGuiArgumentsNeverChangeDocument()
    {
        var path = Document();
        var original = File.ReadAllBytes(path);
        foreach (var args in new[] { new[] { "--gui-clean", "--backup=typo", "--", path }, new[] { "--gui-clean", "--", path, path }, new[] { "--gui-clean", "--audit", "--", path } })
            Assert.Equal(2, await GuiCleanCommand.RunAsync(args, TextWriter.Null, TextWriter.Null));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".bak"));
    }
    [Theory]
    [InlineData("docx")]
    [InlineData("xlsx")]
    public async Task BrokenFileReturnsFailureWithoutOutputPath(string extension)
    {
        var path = Path.Combine(dir, "broken." + extension);
        File.WriteAllText(path, "broken");
        using var output = new StringWriter();
        Assert.Equal(2, await GuiCleanCommand.RunAsync(["--gui-clean", "--", path], output, TextWriter.Null));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("поврежд", json.RootElement.GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("outputPath").ValueKind);
        Assert.Equal("broken", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(dir, ".*.tmp.*"));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    [Theory]
    [InlineData("docx")]
    [InlineData("xlsx")]
    public void BrokenOpenXmlAuditReleasesFile(string extension)
    {
        var path = Path.Combine(dir, "broken." + extension);
        File.WriteAllText(path, "broken");
        Assert.ThrowsAny<Exception>(() => MetadataDel.Core.Audit.MetadataAuditService.Audit(path));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(6, exclusive.Length);
    }
    [Fact]
    public async Task GuiRejectsDirectoriesUnsupportedFilesAndLinks()
    {
        var cleaner = new SingleFileCleaningService();
        Assert.False((await cleaner.CleanAsync(dir, new())).Success);
        var text = Path.Combine(dir, "note.txt"); File.WriteAllText(text, "private");
        Assert.False((await cleaner.CleanAsync(text, new())).Success);
        if (!OperatingSystem.IsWindows())
        {
            var path = Document(); var link = Path.Combine(dir, "link.docx");
            File.CreateSymbolicLink(link, path);
            Assert.False((await cleaner.CleanAsync(link, new())).Success);
        }
    }
}
