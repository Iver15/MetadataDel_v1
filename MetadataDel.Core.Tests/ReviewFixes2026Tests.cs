using iText.Kernel.Pdf;
using MetadataDel.Core.Cleaning;
using MetadataDel.Core.CommandLine;
using MetadataDel.Core.Pdf;
using Xunit;

namespace MetadataDel.Core.Tests;

/// <summary>Исправления по итогам ревью: неполный результат при копии PDF рядом, служебные файлы в папках, копии в работе.</summary>
public sealed class ReviewFixes2026Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mdel-review2026-" + Guid.NewGuid().ToString("N"));

    public ReviewFixes2026Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        if (!OperatingSystem.IsWindows())
            foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Pdf_CleanCopyNextToReadOnlyOriginal_IsIncomplete()
    {
        var path = Path.Combine(_dir, "readonly.pdf");
        using (var document = new PdfDocument(new PdfWriter(path)))
        {
            document.AddNewPage();
            document.GetDocumentInfo().SetAuthor("Private Author");
        }
        File.SetAttributes(path, FileAttributes.ReadOnly);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead);

        var result = await new PdfCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.True(result.Incomplete, result.Message);
        Assert.NotEqual(path, result.Path);
        Assert.Contains("Оригинал не изменён", result.Message);
    }

    [Fact]
    public void FolderExpansion_SkipsOfficeLocksAppleDoubleAndWorkingCopies()
    {
        var folder = Path.Combine(_dir, "folder");
        Directory.CreateDirectory(folder);
        foreach (var name in new[] { "report.docx", "~$report.docx", "._report.docx",
                     $".report.docx.{Guid.NewGuid():N}.tmp.docx", $".mdel-{Guid.NewGuid():N}.pdf" })
            File.WriteAllText(Path.Combine(folder, name), "x");

        var parsed = CliArguments.Parse(new[] { folder });

        Assert.Equal(new[] { Path.Combine(folder, "report.docx") }, parsed.Paths.Select(Path.GetFullPath));
    }

    [Fact]
    public void Deduplicate_KeepsOneEntryForDifferentSpellingsOfOneFile()
    {
        var path = Path.Combine(_dir, "Отчёт.pdf");
        File.WriteAllText(path, "x");
        var nfd = Path.Combine(_dir, "Отчёт.pdf".Normalize(System.Text.NormalizationForm.FormD));

        var result = CliArguments.Deduplicate(new[] { path, path.ToUpperInvariant(), nfd }, caseInsensitive: true);

        Assert.Single(result);
    }

    [Fact]
    public void WorkingCopy_IsNotTreatedAsStaleByParallelRun()
    {
        var path = Path.Combine(_dir, "old.docx");
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));

        using var transaction = new FileCleaningTransaction(path, new CleanOptions(), CancellationToken.None);
        FileCleaningTransaction.RemoveStaleTemporaryFiles(path);

        Assert.True(File.Exists(transaction.WorkingPath));
    }
}

public sealed class FileSystemAudit2026Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mdel-fsaudit2026-" + Guid.NewGuid().ToString("N"));

    public FileSystemAudit2026Tests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Audit_ReportsWhereFromsUntilCleaned()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var path = Path.Combine(_dir, "downloaded.pdf");
        using (var document = new PdfDocument(new PdfWriter(path))) document.AddNewPage();
        var xattr = System.Diagnostics.Process.Start("xattr", new[] { "-w", "com.apple.metadata:kMDItemWhereFroms", "https://example.com/x", path })!;
        await xattr.WaitForExitAsync();

        Assert.Contains(Audit.MetadataAuditService.AuditWithFileSystem(path).Findings, f => f.Code == "fs.xattr");
        Assert.DoesNotContain(Audit.MetadataAuditService.Audit(path).Findings, f => f.Code == "fs.xattr");

        var result = await new PdfCleaner().CleanAsync(path, new CleanOptions());
        Assert.True(result.Success, result.Message);
        Assert.False(result.Incomplete, result.Message);
        Assert.DoesNotContain(Audit.MetadataAuditService.AuditWithFileSystem(path).Findings, f => f.Code == "fs.xattr");
    }
}
