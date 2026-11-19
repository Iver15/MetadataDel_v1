using System.Buffers.Binary;
using System.Text;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Filespec;
using iText.Kernel.Pdf.Xobject;
using MetadataDel.Core.Audit;
using MetadataDel.Core.Cleaning;
using MetadataDel.Core.Ole;
using MetadataDel.Core.Pdf;
using Xunit;
using Path = System.IO.Path;

namespace MetadataDel.Core.Tests;

// Регрессии ревью 2026: итоговый аудит DOC/XLS, принтер и автосохранение в FIB, сбой анализа скрытого текста,
// веб-адреса без схемы в гиперссылках XLS.
public sealed partial class MetadataCleaningTests
{
    [Fact]
    public async Task Doc_HiddenTextLeftAfterCleaningMarksResultIncomplete()
    {
        var path = Path.Combine(_tempDirectory, "review-hidden.doc");
        CreateWord97Doc(path, hiddenText: true);

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.True(result.Incomplete, result.Message);
        Assert.Contains("После очистки остались признаки метаданных: ", result.Message);
        Assert.Contains("скрытый текст Word", result.Message);
    }

    [Fact]
    public async Task Xls_HiddenSheetLeftAfterCleaningMarksResultIncomplete()
    {
        var path = Path.Combine(_tempDirectory, "review-hidden.xls");
        using (var root = OpenMcdf.RootStorage.Create(path))
        using (var stream = root.CreateStream("Workbook"))
            stream.Write(BuildWorkbook(encrypted: false, hiddenSheet: true));

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.True(result.Incomplete, result.Message);
        Assert.Contains("После очистки остались признаки метаданных: ", result.Message);
    }

    [Fact]
    public async Task Doc_FullyCleanedDocumentStaysComplete()
    {
        var path = Path.Combine(_tempDirectory, "review-clean.doc");
        CreateWord97Doc(path);

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.False(result.Incomplete, result.Message);
        Assert.DoesNotContain("После очистки остались", result.Message ?? "");
    }

    [Fact]
    public async Task Doc_ClearsPrinterAndAutosaveBlocksInFib()
    {
        var path = Path.Combine(_tempDirectory, "review-printer.doc");
        CreateWord97Doc(path);
        var printerDriver = Encoding.Latin1.GetBytes("\0\0\\\\secret-print-server\\HP LaserJet\0winspool\0Ne01:\0");
        var portrait = Encoding.Unicode.GetBytes("Secret Printer Portrait\0");
        var landscape = Encoding.Unicode.GetBytes("Secret Printer Landscape\0");
        var autosave = Encoding.Unicode.GetBytes("\u0029C:\\Users\\secret-user\\AppData\\autosave.asd\0");
        PatchStream(path, "1Table", _ => { }, table =>
        {
            var blocks = new List<byte>(table);
            int Append(byte[] block) { var fc = blocks.Count; blocks.AddRange(block); return fc; }
            var positions = new[] { Append(printerDriver), Append(portrait), Append(landscape), Append(autosave) };
            PatchStream(path, "WordDocument", wordDocument =>
            {
                SetFcLcb(wordDocument, 27, positions[0], printerDriver.Length);
                SetFcLcb(wordDocument, 28, positions[1], portrait.Length);
                SetFcLcb(wordDocument, 29, positions[2], landscape.Length);
                SetFcLcb(wordDocument, 35, positions[3], autosave.Length);
            });
            return blocks.ToArray();
        });
        var audit = MetadataAuditService.Audit(path).Findings.Select(f => f.Description).ToList();
        Assert.Contains("сведения о принтере Word (имя принтера, драйвер, порт)", audit);
        Assert.Contains("путь к исходному файлу автосохранения Word", audit);

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.False(result.Incomplete, result.Message);
        var bytes = File.ReadAllBytes(path);
        foreach (var secret in new[] { "secret-print-server", "Secret Printer", "secret-user" })
            AssertNoText(bytes, secret);
        using var root = OpenMcdf.RootStorage.OpenRead(path);
        var wordDocument = ReadAll(root.OpenStream("WordDocument"));
        foreach (var index in new[] { 27, 28, 29, 35 }) Assert.Equal(0u, Lcb(wordDocument, index));
        Assert.False(MetadataAuditService.Audit(path).HasSensitiveMetadata, MetadataAuditService.Audit(path).Summarize());
    }

    [Fact]
    public async Task Doc_DamagedCharacterFormattingDoesNotBlockOtherScrubs()
    {
        var path = Path.Combine(_tempDirectory, "review-chpx.doc");
        CreateWord97Doc(path);
        // PlcfBteChpx длиной 13 байт: (lcb - 4) не делится на 8, разбор форматирования бросает InvalidDataException.
        PatchStream(path, "WordDocument", wordDocument => SetFcLcb(wordDocument, 12, ChpxPlcOffset, 13));

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.True(result.Incomplete, result.Message);
        Assert.Contains("скрытый текст", result.Message);
        var bytes = File.ReadAllBytes(path);
        foreach (var secret in new[] { "Secret Author", "Secret Editor", "Secret Reviewer", "secret-user", "Olga" })
            AssertNoText(bytes, secret);
    }

    [Fact]
    public async Task Xls_KeepsSchemeLessWebLinksButShortensRelativeFilePaths()
    {
        const uint stringMoniker = 0x0001 | 0x0010 | 0x0100;
        var intranet = HyperlinkRecord(0x13, HyperlinkString("intranet"), UrlMoniker("intranet/page"));
        var www = HyperlinkRecord(stringMoniker, HyperlinkString("www"), HyperlinkString("www.example.com/x"));
        var relative = HyperlinkRecord(0x13, HyperlinkString("up"), UrlMoniker(@"..\secret-dir\budget.xls"));
        var slashRelative = HyperlinkRecord(0x13, HyperlinkString("dot"), UrlMoniker("../secret-other/notes.xls"));
        var drive = HyperlinkRecord(stringMoniker, HyperlinkString("drive"), HyperlinkString(@"C:\Users\secret-user\a.xls"));
        var path = CreateXls("review-links.xls", BuildXls(sheet: [intranet, www, relative, slashRelative, drive]));

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.False(result.Incomplete, result.Message);
        var after = ReadWorkbook(path);
        Assert.True(after.AsSpan().IndexOf(Encoding.Unicode.GetBytes("intranet/page\0")) >= 0);
        Assert.True(after.AsSpan().IndexOf(Encoding.Unicode.GetBytes("www.example.com/x\0")) >= 0);
        foreach (var secret in new[] { "secret-dir", "secret-other", "secret-user" }) AssertNoText(after, secret);
        Assert.True(after.AsSpan().IndexOf(Encoding.Unicode.GetBytes("budget.xls")) >= 0);
        Assert.False(MetadataAuditService.Audit(path).HasSensitiveMetadata, MetadataAuditService.Audit(path).Summarize());
    }
}

/// <summary>Регрессии ревью 2026 для PDF: неприменённое редактирование, уведомление об удалённых элементах, пометки без /AP.</summary>
public sealed class PdfReview2026Tests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mdel-pdfreview2026", Guid.NewGuid().ToString("N"));

    public PdfReview2026Tests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch { }
    }

    [Fact]
    public async Task Pdf_UnappliedRedactionMarksResultIncomplete()
    {
        var path = Path.Combine(_directory, "redact.pdf");
        using (var document = new PdfDocument(new PdfWriter(path)))
        {
            var page = document.AddNewPage();
            new PdfCanvas(page).Rectangle(10, 10, 10, 10).Fill();
            page.AddAnnotation(new PdfRedactAnnotation(new Rectangle(20, 770, 100, 20)));
        }

        var result = await new PdfCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.True(result.Incomplete, result.Message);
        Assert.Contains("Redact", result.Message);
    }

    [Fact]
    public async Task Pdf_RemovedNavigationAndNotesAreListedInOneInformationalWarning()
    {
        var path = Path.Combine(_directory, "navigation.pdf");
        using (var document = new PdfDocument(new PdfWriter(path)))
        {
            var page = document.AddNewPage();
            new PdfCanvas(page).Rectangle(10, 10, 10, 10).Fill();
            page.AddAnnotation(new PdfLinkAnnotation(new Rectangle(10, 10, 50, 10))
                .SetAction(iText.Kernel.Pdf.Action.PdfAction.CreateURI("https://example.com/")));
            page.AddAnnotation(new PdfTextAnnotation(new Rectangle(100, 100, 20, 20)).SetContents(new PdfString("note")));
            var attachment = PdfFileSpec.CreateEmbeddedFileSpec(document, Encoding.ASCII.GetBytes("x"), "d", "a.txt", null, null, null);
            page.AddAnnotation(new PdfFileAttachmentAnnotation(new Rectangle(200, 200, 20, 20), attachment));
            var outlines = new PdfDictionary();
            var item = new PdfDictionary();
            item.Put(PdfName.Title, new PdfString("Chapter"));
            item.Put(PdfName.Parent, outlines.MakeIndirect(document));
            item.MakeIndirect(document);
            outlines.Put(PdfName.First, item);
            outlines.Put(PdfName.Last, item);
            outlines.Put(PdfName.Count, new PdfNumber(1));
            document.GetCatalog().GetPdfObject().Put(PdfName.Outlines, outlines);
            var labels = new PdfDictionary();
            var label = new PdfDictionary();
            label.Put(PdfName.P, new PdfString("Draft-"));
            labels.Put(PdfName.Nums, new PdfArray(new PdfObject[] { new PdfNumber(0), label }));
            document.GetCatalog().GetPdfObject().Put(PdfName.PageLabels, labels);
        }

        var result = await new PdfCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.False(result.Incomplete, result.Message);
        var message = result.Message ?? "";
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(message, "Удалены "));
        foreach (var kind in new[] { "ссылки", "закладки", "заметки", "вложения в комментариях", "метки страниц" })
            Assert.Contains(kind, message);
        Assert.DoesNotContain("структура тегов", message);
    }

    [Fact]
    public async Task Pdf_PlainPageHasNoRemovalWarning()
    {
        var path = Path.Combine(_directory, "plain.pdf");
        using (var document = new PdfDocument(new PdfWriter(path)))
            new PdfCanvas(document.AddNewPage()).Rectangle(10, 10, 10, 10).Fill();

        var result = await new PdfCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain("Удалены", result.Message ?? "");
    }

    [Fact]
    public async Task Pdf_MarkupWithoutAppearanceIsDrawnFromQuadPoints()
    {
        var path = Path.Combine(_directory, "markup.pdf");
        using (var document = new PdfDocument(new PdfWriter(path)))
        {
            var page = document.AddNewPage();
            new PdfCanvas(page).Rectangle(10, 10, 10, 10).Fill();
            PdfDictionary Markup(string subtype, float[] color, float y)
            {
                var annotation = new PdfDictionary();
                annotation.Put(PdfName.Type, PdfName.Annot);
                annotation.Put(PdfName.Subtype, new PdfName(subtype));
                annotation.Put(PdfName.Rect, new PdfArray(new float[] { 100, y, 300, y + 20 }));
                annotation.Put(PdfName.QuadPoints, new PdfArray(new float[] { 100, y + 20, 300, y + 20, 100, y, 300, y }));
                annotation.Put(PdfName.C, new PdfArray(color));
                annotation.Put(PdfName.T, new PdfString("MARKUP-AUTHOR-SECRET"));
                return annotation;
            }
            page.GetPdfObject().Put(PdfName.Annots, new PdfArray(new List<PdfObject>
            {
                Markup("Highlight", new[] { 0.25f, 0.5f, 0.75f }, 700),
                Markup("StrikeOut", new[] { 0.75f, 0.25f, 0.5f }, 600),
                Markup("Underline", new[] { 0.5f, 0.75f, 0.25f }, 500),
                Markup("Squiggly", new[] { 0.125f, 0.25f, 0.375f }, 400)
            }));
        }

        var result = await new PdfCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        using var cleaned = new PdfDocument(new PdfReader(path));
        var page1 = cleaned.GetFirstPage();
        Assert.Empty(page1.GetAnnotations());
        var content = Encoding.Latin1.GetString(page1.GetContentBytes());
        Assert.Contains("0.25 0.5 0.75 rg", content);
        Assert.Contains("0.75 0.25 0.5 RG", content);
        Assert.Contains("0.5 0.75 0.25 RG", content);
        Assert.Contains("0.125 0.25 0.375 RG", content);
        var states = page1.GetResources().GetResource(PdfName.ExtGState);
        Assert.NotNull(states);
        Assert.Contains(states.KeySet(), key => PdfName.Multiply.Equals(states.GetAsDictionary(key)?.GetAsName(PdfName.BM)));
        Assert.DoesNotContain("MARKUP-AUTHOR-SECRET", Encoding.Latin1.GetString(File.ReadAllBytes(path)));
    }
}
