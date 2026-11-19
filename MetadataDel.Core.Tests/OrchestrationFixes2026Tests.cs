using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using iText.Kernel.Pdf;
using MetadataDel.Core.Cleaning;
using MetadataDel.Core.CommandLine;
using Xunit;

namespace MetadataDel.Core.Tests;

internal static class TestProfile
{
    // No test may write backups, logs or error reports into the real user profile.
    [ModuleInitializer]
    internal static void Initialize() =>
        AppDataPaths.SetProcessRootForTests(Path.Combine(Path.GetTempPath(), "mdel-test-profile-" + Environment.ProcessId));
}

public sealed class OrchestrationFixes2026Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mdel-orchestration-" + Guid.NewGuid().ToString("N"));
    private string Profile => Path.Combine(_dir, "profile");

    public OrchestrationFixes2026Tests() => Directory.CreateDirectory(Path.Combine(_dir, "docs"));
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Docx(string name = "Отчёт.docx")
    {
        var path = Path.Combine(_dir, "docs", name);
        using var doc = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        doc.AddMainDocumentPart().Document = new Document(new Body(new Paragraph(new Run(new Text("Содержание")))));
        doc.PackageProperties.Creator = "Private author";
        return path;
    }

    private string Pdf(string name = "scan.pdf")
    {
        var path = Path.Combine(_dir, "docs", name);
        using (var pdf = new PdfDocument(new PdfWriter(path)))
        {
            pdf.GetDocumentInfo().SetAuthor("Private author");
            pdf.AddNewPage();
        }
        return path;
    }

    private static string Run(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{file} {string.Join(' ', args)}: {process.StandardError.ReadToEnd()}");
        return output;
    }

    private static HashSet<string> XattrNames(string path) =>
        Run("/usr/bin/xattr", path).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

    private const string Quarantine = "0083;6740a1b2;Safari;A1B2C3D4-0000-0000-0000-000000000000";

    // ---- 1. File-system metadata ---------------------------------------------------------------

    [Fact]
    public async Task MacOS_DocxCleaning_DropsDownloadSourceAndTags_KeepsQuarantine()
    {
        if (!OperatingSystem.IsMacOS()) return; // Extended attributes of interest exist only on macOS.
        var path = Docx();
        Run("/usr/bin/xattr", "-w", "com.apple.metadata:kMDItemWhereFroms", "https://mail.example/secret?from=boss", path);
        Run("/usr/bin/xattr", "-w", "com.apple.metadata:_kMDItemUserTags", "Клиент X", path);
        Run("/usr/bin/xattr", "-w", "com.apple.metadata:kMDItemFinderComment", "private note", path);
        Run("/usr/bin/xattr", "-w", "com.apple.quarantine", Quarantine, path);

        var result = await new MetadataDel.Core.Word.DocxCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        var names = XattrNames(path);
        names.Remove("com.apple.provenance"); // System attribute without user data; may be undeletable.
        Assert.Equal(["com.apple.quarantine"], names);
        Assert.Equal(Quarantine, Run("/usr/bin/xattr", "-p", "com.apple.quarantine", path).Trim());
    }

    [Fact]
    public async Task MacOS_PdfCleaning_CopiesOnlyQuarantineToFreshFile()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var path = Pdf();
        Run("/usr/bin/xattr", "-w", "com.apple.metadata:kMDItemWhereFroms", "https://example/secret", path);
        Run("/usr/bin/xattr", "-w", "com.apple.quarantine", Quarantine, path);

        var result = await new MetadataDel.Core.Pdf.PdfCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        var names = XattrNames(path);
        names.Remove("com.apple.provenance");
        Assert.Equal(["com.apple.quarantine"], names);
    }

    [Fact]
    public void Unix_StatReadsOwnerAndGroupOfFile()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Docx();
        var owner = UnixOwnership.Stat(path);
        Assert.NotNull(owner);
        var format = OperatingSystem.IsMacOS() ? new[] { "-f", "%u %g" } : new[] { "-c", "%u %g" };
        var expected = Run("/usr/bin/stat", [.. format, path]).Trim();
        Assert.Equal(expected, $"{owner!.Value.Uid} {owner.Value.Gid}");
    }

    // ---- 2. Incomplete results ------------------------------------------------------------------

    private sealed class FakeCleaner(Func<string, CleanResult> result) : IFileCleaner
    {
        public Task<CleanResult> CleanAsync(string path, CleanOptions options, CancellationToken ct = default) => Task.FromResult(result(path));
    }

    private static SingleFileCleaningService Partial() =>
        new(_ => new FakeCleaner(p => new CleanResult(p, true, "File contains corrupted data.") { Incomplete = true }));

    [Fact]
    public async Task SingleFileService_PreservesIncompleteWhileTranslatingMessage()
    {
        var result = await Partial().CleanAsync(Docx(), new CleanOptions());
        Assert.True(result.Success);
        Assert.True(result.Incomplete);
        Assert.Equal("Файл повреждён или имеет неверный формат.", result.Message);
    }

    [Fact]
    public async Task GuiClean_ReportsIncompleteWithExitCodeOne()
    {
        var path = Docx();
        using var output = new StringWriter();
        Assert.Equal(1, await GuiCleanCommand.RunAsync(["--gui-clean", "--backup=off", "--", path], output, TextWriter.Null, Partial(), default));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        Assert.True(json.RootElement.GetProperty("incomplete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("backupPath").ValueKind);
    }

    [Fact]
    public async Task CliRunner_PrintsPartialAndExitsWithOne()
    {
        var path = Docx();
        using var stdout = new StringWriter();
        var summary = await new CliCleaningRunner(false, stdout, TextWriter.Null, Partial()).RunAsync([path], new CleanOptions(), default);
        Assert.Equal(new CliBatchSummary(1, 0, 1, 0, 0), summary);
        Assert.Equal(1, summary.ExitCode);
        Assert.Contains("[PARTIAL] " + path, stdout.ToString());
        Assert.Contains("очищено не полностью: 1", stdout.ToString());
    }

    [Theory]
    [InlineData(3, 0, 0, 0, 0)]
    [InlineData(2, 1, 0, 0, 1)]
    [InlineData(0, 1, 0, 0, 1)]
    [InlineData(0, 1, 1, 0, 1)]
    [InlineData(1, 0, 1, 0, 1)]
    [InlineData(0, 0, 2, 0, 2)]
    [InlineData(1, 0, 0, 1, 130)]
    public void ExitCodes_OnlyFullCleaningIsZero(int cleaned, int partial, int failed, int cancelled, int expected) =>
        Assert.Equal(expected, new CliBatchSummary(cleaned + partial + failed + cancelled, cleaned, partial, failed, cancelled).ExitCode);

    // ---- 3. Backups -------------------------------------------------------------------------------

    [Fact]
    public async Task Backups_GoToPrivateFolder_NeverOverwrite_AndAreNotNextToDocument()
    {
        using var profile = AppDataPaths.OverrideRootForTests(Profile);
        var path = Docx("Договор.docx");
        var original = File.ReadAllBytes(path);
        var service = new SingleFileCleaningService();

        var first = await service.CleanWithBackupAsync(path, new CleanOptions(Backup: true));
        var second = await service.CleanWithBackupAsync(path, new CleanOptions(Backup: true));

        Assert.True(first.Result.Success && second.Result.Success);
        Assert.Equal(Path.Combine(Profile, "Backups", DateTime.Now.ToString("yyyy-MM-dd"), "Договор.docx"), first.BackupPath);
        Assert.Equal("Договор (2).docx", Path.GetFileName(second.BackupPath));
        Assert.Equal(original, File.ReadAllBytes(first.BackupPath!));
        Assert.Equal(["Договор.docx"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.Combine(Profile, "Backups")));
    }

    [Fact]
    public async Task CliRunner_LogsBackupLocation()
    {
        using var profile = AppDataPaths.OverrideRootForTests(Profile);
        var path = Docx();
        using var stdout = new StringWriter();
        var summary = await new CliCleaningRunner(false, stdout, TextWriter.Null).RunAsync([path], new CleanOptions(Backup: true), default);
        Assert.Equal(0, summary.ExitCode);
        Assert.Contains($"[BACKUP] {path} -> {Path.Combine(Profile, "Backups")}", stdout.ToString());
    }

    // ---- 4. Error reports -------------------------------------------------------------------------

    [Fact]
    public void ErrorReports_GoToPrivateLogs_WithUniqueNames()
    {
        using var profile = AppDataPaths.OverrideRootForTests(Profile);
        var path = Path.Combine(_dir, "docs", "broken.docx");
        File.WriteAllText(path, "broken");
        var existing = path + ".MetadataDel-ошибка.txt";
        File.WriteAllText(existing, "user file");

        var first = UserErrorReportWriter.TryWrite(path, "Файл повреждён");
        var second = UserErrorReportWriter.TryWrite(path, "Файл повреждён");

        Assert.NotNull(first); Assert.NotNull(second); Assert.NotEqual(first, second);
        Assert.Equal(AppDataPaths.LogsDirectory, Path.GetDirectoryName(first));
        Assert.Contains(path, File.ReadAllText(first!));
        Assert.Equal("user file", File.ReadAllText(existing));
        Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(path)!).Length);
    }

    // ---- 5. Leftover temporary files and cancellation ---------------------------------------------

    [Fact]
    public async Task StaleWorkingCopiesOfSameDocumentAreRemoved_OthersKept()
    {
        var path = Docx("a.docx");
        var folder = Path.GetDirectoryName(path)!;
        var hex = new string('b', 32);
        string Create(string name, bool old)
        {
            var file = Path.Combine(folder, name);
            File.WriteAllText(file, "leftover");
            if (old) File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-11));
            return file;
        }
        var stale = Create($".a.docx.{hex}.tmp.docx", old: true);
        var staleRebuilt = Create($".a.docx.{hex}.tmp.docx.rebuilt", old: true);
        var fresh = Create($".a.docx.{new string('c', 32)}.tmp.docx", old: false);
        var shortGuid = Create($".a.docx.{new string('b', 31)}.tmp.docx", old: true);
        var otherDocument = Create($".b.docx.{hex}.tmp.docx", old: true);
        var foreign = Create($".a.docx.{hex}.tmp.docx.bak", old: true);
        var stalePdf = Create($".mdel-{hex}.pdf", old: true);
        var foreignPdf = Create(".mdel-notaguid.pdf", old: true);

        Assert.True((await new MetadataDel.Core.Word.DocxCleaner().CleanAsync(path, new CleanOptions())).Success);
        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(staleRebuilt));
        Assert.All(new[] { fresh, shortGuid, otherDocument, foreign, stalePdf, foreignPdf }, file => Assert.True(File.Exists(file), file));

        var pdf = Pdf();
        Assert.True((await new MetadataDel.Core.Pdf.PdfCleaner().CleanAsync(pdf, new CleanOptions())).Success);
        Assert.False(File.Exists(stalePdf));
        Assert.True(File.Exists(foreignPdf));
        Assert.Equal([foreignPdf], Directory.GetFiles(folder, ".mdel-*.pdf"));
    }

    [Fact]
    public async Task CancelledRunLeavesOriginalAndNoWorkingCopy()
    {
        var path = Docx();
        var original = File.ReadAllBytes(path);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var stderr = new StringWriter();
        var summary = await new CliCleaningRunner(false, TextWriter.Null, stderr).RunAsync([path], new CleanOptions(), cancelled.Token);
        Assert.Equal(CliBatchSummary.InterruptedExitCode, summary.ExitCode);
        Assert.Contains("[STOP]", stderr.ToString());
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    // ---- 6. Logs ------------------------------------------------------------------------------------

    [Fact]
    public void ExpiredLogs_OnlyOwnOldFilesAreDeleted()
    {
        var logs = Path.Combine(_dir, "logs");
        Directory.CreateDirectory(logs);
        var now = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
        string Create(string name, int ageDays)
        {
            var file = Path.Combine(logs, name);
            File.WriteAllText(file, "x");
            File.SetLastWriteTimeUtc(file, now.AddDays(-ageDays));
            return file;
        }
        var oldDaily = Create("2026-09-01.log", 40);
        var oldFinder = Create("finder-action-2026-09-01.log", 40);
        var oldLegacyFinder = Create("finder-action.log", 40);
        var oldReport = Create("20260901-101010-0123abcd-Отчёт.docx.MetadataDel-ошибка.txt", 40);
        var recent = Create("2026-11-18.log", 1);
        var foreign = Create("notes.log", 40);

        Assert.Equal(4, AppDataPaths.DeleteExpiredLogs(TimeSpan.FromDays(30), now, logs));
        Assert.All(new[] { oldDaily, oldFinder, oldLegacyFinder, oldReport }, f => Assert.False(File.Exists(f), f));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public void MacOS_LogsShareFinderActionFolder()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var profile = AppDataPaths.OverrideRootForTests(Profile);
        Assert.Equal(Path.Combine(Profile, "logs"), AppDataPaths.LogsDirectory);
    }

    // ---- 7–8. Arguments -------------------------------------------------------------------------------

    [Fact]
    public void Deduplicate_TreatsCaseAndUnicodeFormsAsOneFileOnMacAndWindows()
    {
        var nfc = "/docs/Отчёт.pdf".Normalize(NormalizationForm.FormC);
        var nfd = "/docs/Отчёт.pdf".Normalize(NormalizationForm.FormD);
        Assert.NotEqual(nfc, nfd);
        Assert.Equal([nfc], CliArguments.Deduplicate([nfc, nfd, "/DOCS/ОТЧЁТ.PDF"], caseInsensitive: true));
        Assert.Equal(3, CliArguments.Deduplicate([nfc, nfd, "/DOCS/ОТЧЁТ.PDF"], caseInsensitive: false).Count);
    }

    [Theory]
    [InlineData("D:\\\"", "D:\\")]
    [InlineData("D:", "D:\\")]
    [InlineData("C:\\docs\\a.pdf", "C:\\docs\\a.pdf")]
    [InlineData("C:\\docs\\folder\"", "C:\\docs\\folder")]
    [InlineData("relative.pdf", "relative.pdf")]
    public void WindowsShellArguments_DriveRootsAndEscapedQuotes(string argument, string expected) =>
        Assert.Equal(expected, CliArguments.NormalizeWindowsShellArgument(argument));
}
