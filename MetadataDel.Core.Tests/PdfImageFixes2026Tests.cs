using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Filespec;
using iText.Kernel.Pdf.Xobject;
using iText.Layout.Element;
using MetadataDel.Core.Audit;
using MetadataDel.Core.Cleaning;
using MetadataDel.Core.Media;
using MetadataDel.Core.Pdf;
using Xunit;
using Path = System.IO.Path;

namespace MetadataDel.Core.Tests;

/// <summary>Регрессии 2026: цепочки фильтров, связанные файлы, теги, аннотации, слои, неполная очистка и форматы изображений.</summary>
public sealed class PdfImageFixes2026Tests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mdel-pdfimg2026", Guid.NewGuid().ToString("N"));

    public PdfImageFixes2026Tests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch { }
    }

    // ---------------------------------------------------------------- PDF

    [Theory]
    [InlineData("FlateDecode")]
    [InlineData("ASCIIHexDecode")]
    public async Task Pdf_JpegBehindFilterChain_LosesExifAndAuditSeesIt(string firstFilter)
    {
        var path = Path.Combine(_directory, "chain.pdf");
        using (var document = new PdfDocument(new PdfWriter(path)))
        {
            var page = document.AddNewPage();
            var jpeg = ResidualMetadataTests.Jpeg("CHAIN-EXIF-SECRET", 1);
            var encoded = firstFilter == "FlateDecode" ? Deflate(jpeg) : Encoding.ASCII.GetBytes(Convert.ToHexString(jpeg) + ">");
            var image = new PdfStream(encoded);
            image.SetCompressionLevel(CompressionConstants.NO_COMPRESSION);
            image.Put(PdfName.Type, PdfName.XObject);
            image.Put(PdfName.Subtype, PdfName.Image);
            image.Put(PdfName.Width, new PdfNumber(1));
            image.Put(PdfName.Height, new PdfNumber(1));
            image.Put(PdfName.ColorSpace, PdfName.DeviceGray);
            image.Put(PdfName.BitsPerComponent, new PdfNumber(8));
            image.Put(PdfName.Filter, new PdfArray(new List<PdfObject> { new PdfName(firstFilter), PdfName.DCTDecode }));
            image.Put(PdfName.DecodeParms, new PdfArray(new List<PdfObject> { PdfNull.PDF_NULL, PdfNull.PDF_NULL }));
            image.MakeIndirect(document);
            new PdfCanvas(page).AddXObjectWithTransformationMatrix(new PdfImageXObject(image), 10, 0, 0, 10, 20, 600);
        }
        Assert.DoesNotContain("CHAIN-EXIF-SECRET", Encoding.Latin1.GetString(File.ReadAllBytes(path)));
        Assert.Contains("CHAIN-EXIF-SECRET", DecodedPdfText(path));
        Assert.Contains(MetadataAuditService.Audit(path).Findings, f => f.Code == "pdf.images");

        var result = await new PdfCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain("CHAIN-EXIF-SECRET", DecodedPdfText(path));
        Assert.DoesNotContain(MetadataAuditService.Audit(path).Findings, f => f.Code == "pdf.images");
        using var cleaned = new PdfDocument(new PdfReader(path));
        var stream = AllObjects(cleaned).OfType<PdfStream>().Single(s => PdfName.Image.Equals(s.GetAsName(PdfName.Subtype)));
        Assert.Equal(PdfName.DCTDecode, stream.GetAsName(PdfName.Filter));
        var bytes = stream.GetBytes(false);
        Assert.True(ImageMetadata.IsJpeg(bytes));
        Assert.False(ImageMetadata.HasMetadata(bytes));
    }

    [Fact]
    public async Task Pdf_AssociatedFilesOpiAndAttachmentAnnotationsAreNotWritten()
    {
        var path = Path.Combine(_directory, "af.pdf");
        using (var document = new PdfDocument(new PdfWriter(path)))
        {
            var page = document.AddNewPage();
            var form = new PdfFormXObject(new Rectangle(10, 10));
            new PdfCanvas(form, document).Rectangle(0, 0, 5, 5).Fill();
            var spec = PdfFileSpec.CreateEmbeddedFileSpec(document, Encoding.ASCII.GetBytes("AF-PAYLOAD-SECRET"), "d", "af-secret.txt", null, null, null);
            form.GetPdfObject().Put(new PdfName("AF"), new PdfArray(spec.GetPdfObject()));
            var opi = new PdfDictionary();
            var opi13 = new PdfDictionary();
            opi13.Put(PdfName.F, new PdfString("C:/Users/OPI-PATH-SECRET/hires.tif"));
            opi.Put(new PdfName("1.3"), opi13);
            form.GetPdfObject().Put(new PdfName("OPI"), opi);
            var canvas = new PdfCanvas(page);
            canvas.AddXObjectAt(form, 50, 50);

            var attachment = PdfFileSpec.CreateEmbeddedFileSpec(document, Encoding.ASCII.GetBytes("ANNOT-PAYLOAD-SECRET"), "d", "annot-secret.txt", null, null, null);
            page.AddAnnotation(new PdfFileAttachmentAnnotation(new Rectangle(100, 100, 20, 20), attachment));
        }
        Assert.Contains("AF-PAYLOAD-SECRET", DecodedPdfText(path));

        var result = await new PdfCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        var text = DecodedPdfText(path);
        foreach (var secret in new[] { "AF-PAYLOAD-SECRET", "af-secret.txt", "OPI-PATH-SECRET", "ANNOT-PAYLOAD-SECRET", "annot-secret.txt", "/EmbeddedFile" })
            Assert.DoesNotContain(secret, text);
        Assert.False(MetadataAuditService.Audit(path).HasSensitiveMetadata, MetadataAuditService.Audit(path).Summarize(20));
    }

    [Fact]
    public async Task Pdf_StructureTreeWithAltActualTextAndTitleIsNotWritten()
    {
        var path = Path.Combine(_directory, "tagged.pdf");
        using (var document = new PdfDocument(new PdfWriter(path)))
        {
            document.SetTagged();
            using var layout = new iText.Layout.Document(document, PageSize.A4, false);
            var paragraph = new Paragraph("Tagged visible text");
            paragraph.GetAccessibilityProperties().SetAlternateDescription("ALT-STRUCT-SECRET").SetActualText("ACTUAL-STRUCT-SECRET");
            layout.Add(paragraph);
            foreach (var kid in document.GetStructTreeRoot().GetKids().OfType<iText.Kernel.Pdf.Tagging.PdfStructElem>())
                kid.GetPdfObject().Put(PdfName.T, new PdfString("TITLE-STRUCT-SECRET"));
            document.GetCatalog().GetPdfObject().Put(PdfName.Outlines, OutlineWithTitle(document, "OUTLINE-SECRET"));
        }
        Assert.Contains("ALT-STRUCT-SECRET", DecodedPdfText(path));

        var result = await new PdfCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        var text = DecodedPdfText(path);
        foreach (var secret in new[] { "ALT-STRUCT-SECRET", "ACTUAL-STRUCT-SECRET", "TITLE-STRUCT-SECRET", "/StructElem", "/StructTreeRoot", "OUTLINE-SECRET" })
            Assert.DoesNotContain(secret, text);
        using var cleaned = new PdfDocument(new PdfReader(path));
        Assert.Contains("Tagged visible text", PdfTextExtractor.GetTextFromPage(cleaned.GetFirstPage()));
    }

    [Fact]
    public async Task Pdf_VisibleMarkupAnnotationsAreFlattenedAndRedactionsDropped()
    {
        var path = Path.Combine(_directory, "annots.pdf");
        using (var document = new PdfDocument(new PdfWriter(path)))
        {
            var page = document.AddNewPage();
            new PdfCanvas(page).BeginText().SetFontAndSize(iText.Kernel.Font.PdfFontFactory.CreateFont(), 12)
                .MoveText(20, 780).ShowText("Body text").EndText();

            // Повёрнутое на 90° оформление: BBox 100x20, /Matrix [0 1 -1 0 0 0], Rect 20x100 в точке (300, 300).
            var stamp = new PdfStampAnnotation(new Rectangle(300, 300, 20, 100));
            var rotated = TextAppearance(document, "STAMP-VISIBLE");
            rotated.GetPdfObject().Put(PdfName.Matrix, new PdfArray(new float[] { 0, 1, -1, 0, 0, 0 }));
            stamp.SetNormalAppearance(rotated.GetPdfObject());
            stamp.SetContents("Stamp author comment");
            page.AddAnnotation(stamp);

            var hidden = new PdfStampAnnotation(new Rectangle(100, 100, 100, 20));
            hidden.SetNormalAppearance(TextAppearance(document, "HIDDEN-STAMP").GetPdfObject());
            hidden.SetFlags(PdfAnnotation.HIDDEN);
            page.AddAnnotation(hidden);

            var square = new PdfSquareAnnotation(new Rectangle(100, 400, 100, 20));
            var states = new PdfDictionary();
            states.Put(new PdfName("On"), TextAppearance(document, "STATE-ON").GetPdfObject());
            states.Put(new PdfName("Off"), TextAppearance(document, "STATE-OFF").GetPdfObject());
            square.SetNormalAppearance(states);
            square.SetAppearanceState(new PdfName("On"));
            page.AddAnnotation(square);

            var redact = new PdfRedactAnnotation(new Rectangle(20, 770, 100, 20));
            redact.SetNormalAppearance(TextAppearance(document, "REDACT-OVERLAY").GetPdfObject());
            page.AddAnnotation(redact);
        }

        var result = await new PdfCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.Contains("редактирован", result.Message ?? "", StringComparison.OrdinalIgnoreCase);
        using var cleaned = new PdfDocument(new PdfReader(path));
        var page1 = cleaned.GetFirstPage();
        Assert.Empty(page1.GetAnnotations());
        var listener = new TextPositions();
        new PdfCanvasProcessor(listener).ProcessPageContent(page1);
        var text = string.Concat(listener.Chunks.Select(c => c.Text));
        Assert.Contains("Body text", text);
        Assert.Contains("STAMP-VISIBLE", text);
        Assert.Contains("STATE-ON", text);
        Assert.DoesNotContain("STATE-OFF", text);
        Assert.DoesNotContain("HIDDEN-STAMP", text);
        Assert.DoesNotContain("REDACT-OVERLAY", text);
        Assert.DoesNotContain("Stamp author comment", DecodedPdfText(path));
        Assert.DoesNotContain("AP-XMP", DecodedPdfText(path));
        // Точка (2, 5) оформления: /Matrix даёт (-5, 2), отображение BBox на Rect сдвигает на (320, 300).
        var start = listener.Chunks.First(c => c.Text.StartsWith("STAMP", StringComparison.Ordinal)).Start;
        Assert.InRange(start.Get(Vector.I1), 314.5f, 315.5f);
        Assert.InRange(start.Get(Vector.I2), 301.5f, 302.5f);
    }

    [Fact]
    public async Task Pdf_AuditFailureIsReportedAsIncompleteButSuccessful()
    {
        var path = SimplePdf("audit-fail.pdf");
        PdfCleaner.AuditOverride.Value = _ => throw new InvalidOperationException("audit exploded");
        CleanResult result;
        try { result = await new PdfCleaner().CleanAsync(path, new CleanOptions()); }
        finally { PdfCleaner.AuditOverride.Value = null; }

        Assert.True(result.Success, result.Message);
        Assert.True(result.Incomplete);
        Assert.Contains("не удалось проверить результат", result.Message ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pdf_ResidualFindingsAfterCleaningMarkResultIncomplete()
    {
        var path = SimplePdf("residual.pdf");
        PdfCleaner.AuditOverride.Value = p => new MetadataAuditResult(p, "pdf", new[] { new MetadataAuditFinding("pdf.test", "тестовый остаток") });
        CleanResult result;
        try { result = await new PdfCleaner().CleanAsync(path, new CleanOptions()); }
        finally { PdfCleaner.AuditOverride.Value = null; }

        Assert.True(result.Success, result.Message);
        Assert.True(result.Incomplete);
        Assert.Contains("тестовый остаток", result.Message ?? "");
    }

    [Fact]
    public void Pdf_EncryptedWithOwnerPasswordCanStillBeAudited()
    {
        // Без itext.bouncy-castle-adapter iText 9 не открывает даже RC4/AES-файлы с пустым паролем пользователя.
        var path = Path.Combine(_directory, "encrypted.pdf");
        var properties = new WriterProperties().SetStandardEncryption(null, Encoding.ASCII.GetBytes("owner"),
            EncryptionConstants.ALLOW_PRINTING, EncryptionConstants.ENCRYPTION_AES_128);
        using (var document = new PdfDocument(new PdfWriter(path, properties)))
        {
            document.GetDocumentInfo().SetAuthor("ENCRYPTED-AUTHOR");
            new PdfCanvas(document.AddNewPage()).Rectangle(10, 10, 10, 10).Fill();
        }

        Assert.Contains(MetadataAuditService.Audit(path).Findings, f => f.Code == "pdf.info.author");
    }

    [Fact]
    public async Task Pdf_CleanFileIsNotIncompleteAndItext9OutputHasNoProducerTraces()
    {
        var path = SimplePdf("clean.pdf");
        var result = await new PdfCleaner().CleanAsync(path, new CleanOptions());
        Assert.True(result.Success, result.Message);
        Assert.False(result.Incomplete, result.Message);

        // Байтовые правки Info и комментария iText рассчитаны на классическую таблицу xref без потоков объектов.
        var raw = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        Assert.Contains("\nxref", raw);
        foreach (var trace in new[] { "/ObjStm", "/XRef", "%iText", "iText", "Producer", "CreationDate", "ModDate" })
            Assert.DoesNotContain(trace, raw);
        using var document = new PdfDocument(new PdfReader(path));
        Assert.Empty(document.GetTrailer().GetAsDictionary(PdfName.Info)?.KeySet() ?? new List<PdfName>());
        Assert.False(MetadataAuditService.Audit(path).HasSensitiveMetadata);
    }

    [Fact]
    public async Task Pdf_LayersReachableOnlyFromResourcesAreRenamed()
    {
        var path = Path.Combine(_directory, "ocg.pdf");
        using (var document = new PdfDocument(new PdfWriter(path)))
        {
            var page = document.AddNewPage();
            var orphan = Ocg(document, "ORPHAN-LAYER-SECRET");
            var member = Ocg(document, "OCMD-LAYER-SECRET");
            var ocmd = new PdfDictionary();
            ocmd.Put(PdfName.Type, PdfName.OCMD);
            ocmd.Put(PdfName.OCGs, new PdfArray(member));
            ocmd.MakeIndirect(document);
            var formLayer = Ocg(document, "FORM-LAYER-SECRET");

            var form = new PdfFormXObject(new Rectangle(10, 10));
            new PdfCanvas(form, document).BeginMarkedContent(PdfName.OC, formLayer).Rectangle(0, 0, 5, 5).Fill().EndMarkedContent();
            new PdfCanvas(page)
                .BeginMarkedContent(PdfName.OC, orphan).Rectangle(10, 10, 10, 10).Fill().EndMarkedContent()
                .BeginMarkedContent(PdfName.OC, ocmd).Rectangle(30, 10, 10, 10).Fill().EndMarkedContent()
                .AddXObjectAt(form, 60, 60);
        }
        Assert.Contains("ORPHAN-LAYER-SECRET", DecodedPdfText(path));
        Assert.Contains(MetadataAuditService.Audit(path).Findings, f => f.Code == "pdf.layers");

        var result = await new PdfCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        var text = DecodedPdfText(path);
        foreach (var secret in new[] { "ORPHAN-LAYER-SECRET", "OCMD-LAYER-SECRET", "FORM-LAYER-SECRET" })
            Assert.DoesNotContain(secret, text);
        Assert.DoesNotContain(MetadataAuditService.Audit(path).Findings, f => f.Code == "pdf.layers");
    }

    [Fact]
    public async Task Pdf_DamagedJpegIsSalvagedAndReportedIncomplete()
    {
        var path = Path.Combine(_directory, "damaged.pdf");
        using (var document = new PdfDocument(new PdfWriter(path)))
        {
            var page = document.AddNewPage();
            var image = new PdfStream(TruncatedJpeg("DAMAGED-EXIF-SECRET"));
            image.Put(PdfName.Type, PdfName.XObject);
            image.Put(PdfName.Subtype, PdfName.Image);
            image.Put(PdfName.Width, new PdfNumber(1));
            image.Put(PdfName.Height, new PdfNumber(1));
            image.Put(PdfName.ColorSpace, PdfName.DeviceGray);
            image.Put(PdfName.BitsPerComponent, new PdfNumber(8));
            image.Put(PdfName.Filter, PdfName.DCTDecode);
            image.MakeIndirect(document);
            new PdfCanvas(page).AddXObjectWithTransformationMatrix(new PdfImageXObject(image), 10, 0, 0, 10, 20, 600);
        }

        var result = await new PdfCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.True(result.Incomplete);
        Assert.Contains("повреждено", result.Message ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DAMAGED-EXIF-SECRET", DecodedPdfText(path));
    }

    // ---------------------------------------------------------------- images

    [Fact]
    public void Png_UnknownChunksAreRemovedByAllowlist()
    {
        var png = Png(("IHDR", new byte[13]), ("pHYs", new byte[9]), ("caBX", Encoding.ASCII.GetBytes("C2PA-SECRET")),
            ("iDOT", new byte[28]), ("mkBT", Encoding.ASCII.GetBytes("FIREWORKS-SECRET")), ("IDAT", new byte[] { 1, 2, 3 }), ("IEND", Array.Empty<byte>()));
        Assert.True(ImageMetadata.HasMetadata(png));

        var stripped = ImageMetadata.Strip(png)!;

        var text = Encoding.Latin1.GetString(stripped);
        Assert.DoesNotContain("C2PA-SECRET", text);
        Assert.DoesNotContain("FIREWORKS-SECRET", text);
        Assert.DoesNotContain("iDOT", text);
        Assert.Contains("pHYs", text);
        Assert.Contains("IDAT", text);
        Assert.False(ImageMetadata.HasMetadata(stripped));
    }

    [Fact]
    public void Jpeg_ForeignApp0IsRemovedButJfifIccAndAdobeAreKept()
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 0xFF, 0xD8 });
        JpegSegment(output, 0xE0, Concat("JFIF\0"u8.ToArray(), new byte[] { 1, 1, 0, 0, 1, 0, 1, 0, 0 }));
        JpegSegment(output, 0xE0, Concat("AVI1\0"u8.ToArray(), Encoding.ASCII.GetBytes("APP0-SECRET")));
        JpegSegment(output, 0xE2, Concat("ICC_PROFILE\0"u8.ToArray(), new byte[] { 1, 1, 9, 9 }));
        JpegSegment(output, 0xEE, Concat("Adobe"u8.ToArray(), new byte[] { 0, 100, 0, 0, 0, 0, 1 }));
        JpegSegment(output, 0xDB, new byte[65]);
        JpegSegment(output, 0xC0, new byte[] { 8, 0, 1, 0, 1, 1, 1, 0x11, 0 });
        JpegSegment(output, 0xDA, new byte[] { 1, 1, 0, 0, 63, 0 });
        output.Write(new byte[] { 0x12, 0x34, 0xFF, 0xD9 });

        var stripped = ImageMetadata.Strip(output.ToArray())!;

        var text = Encoding.Latin1.GetString(stripped);
        Assert.DoesNotContain("APP0-SECRET", text);
        Assert.Contains("JFIF", text);
        Assert.Contains("ICC_PROFILE", text);
        Assert.Contains("Adobe", text);
    }

    [Fact]
    public void Jpeg2000_MainHeaderCommentsAreRemovedFromRawCodestreamAndJp2()
    {
        var codestream = Codestream("J2K-COMMENT-SECRET");
        Assert.True(ImageMetadata.HasMetadata(codestream));
        var stripped = ImageMetadata.Strip(codestream)!;
        Assert.DoesNotContain("J2K-COMMENT-SECRET", Encoding.Latin1.GetString(stripped));
        Assert.Equal(codestream.Length - (4 + 2 + "J2K-COMMENT-SECRET".Length), stripped.Length);
        Assert.True(stripped.AsSpan().EndsWith(codestream.AsSpan(codestream.Length - 20)));

        using var jp2 = new MemoryStream();
        Box(jp2, "jP  ", new byte[] { 0x0D, 0x0A, 0x87, 0x0A });
        Box(jp2, "ftyp", Concat("jp2 "u8.ToArray(), new byte[4], "jp2 "u8.ToArray()));
        Box(jp2, "jp2h", new byte[8]);
        Box(jp2, "jp2c", codestream);
        var file = jp2.ToArray();
        var cleaned = ImageMetadata.Strip(file)!;
        Assert.DoesNotContain("J2K-COMMENT-SECRET", Encoding.Latin1.GetString(cleaned));
        var last = cleaned.AsSpan().IndexOf("jp2c"u8) - 4;
        Assert.Equal(cleaned.Length - last, (int)BinaryPrimitives.ReadUInt32BigEndian(cleaned.AsSpan(last)));
        Assert.False(ImageMetadata.HasMetadata(cleaned));
    }

    [Fact]
    public void Jpeg_GarbageBetweenSegmentsIsSkippedAndReportedMalformed()
    {
        var jpeg = ResidualMetadataTests.Jpeg("GARBAGE-EXIF-SECRET", 1);
        var dqt = jpeg.AsSpan().IndexOf(new byte[] { 0xFF, 0xDB });
        var damaged = Concat(jpeg[..dqt], new byte[] { 0x00, 0x11, 0x22 }, jpeg[dqt..]);

        var result = ImageMetadata.StripDetailed(damaged);

        Assert.Equal(ImageStripStatus.Malformed, result.Status);
        Assert.NotNull(result.Data);
        Assert.DoesNotContain("GARBAGE-EXIF-SECRET", Encoding.Latin1.GetString(result.Data!));
        Assert.True(result.Data!.AsSpan().IndexOf(new byte[] { 0xFF, 0xDA }) > 0);
    }

    [Fact]
    public void Truncated_PngGifAndWebpKeepPixelDataButLoseMetadata()
    {
        var png = Png(("IHDR", new byte[13]), ("tEXt", Encoding.ASCII.GetBytes("Author\0PNG-TRUNC-SECRET")), ("IDAT", new byte[40]));
        png = png[..^20];
        var pngResult = ImageMetadata.StripDetailed(png);
        Assert.Equal(ImageStripStatus.Malformed, pngResult.Status);
        Assert.DoesNotContain("PNG-TRUNC-SECRET", Encoding.Latin1.GetString(pngResult.Data!));
        Assert.Contains("IDAT", Encoding.Latin1.GetString(pngResult.Data!));

        using var gif = new MemoryStream();
        gif.Write("GIF89a"u8);
        gif.Write(new byte[] { 1, 0, 1, 0, 0, 0, 0 });
        gif.Write(new byte[] { 0x21, 0xFE, 15 });
        gif.Write("GIF-TRUNC-SECRET"u8[..15]);
        gif.WriteByte(0);
        gif.Write(new byte[] { 0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 10, 1, 2, 3 });
        var gifResult = ImageMetadata.StripDetailed(gif.ToArray());
        Assert.Equal(ImageStripStatus.Malformed, gifResult.Status);
        Assert.DoesNotContain("GIF-TRUNC-SECRE", Encoding.Latin1.GetString(gifResult.Data!));
        Assert.Equal((byte)0x2C, gifResult.Data![13]);

        using var webp = new MemoryStream();
        webp.Write("RIFF\0\0\0\0WEBP"u8);
        WebpChunk(webp, "VP8X", new byte[10] { 0x0C, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        WebpChunk(webp, "EXIF", Encoding.ASCII.GetBytes("WEBP-TRUNC-SECRET!"));
        WebpChunk(webp, "VP8 ", new byte[30]);
        var webpData = webp.ToArray()[..^10];
        var webpResult = ImageMetadata.StripDetailed(webpData);
        Assert.Equal(ImageStripStatus.Malformed, webpResult.Status);
        Assert.DoesNotContain("WEBP-TRUNC-SECRET", Encoding.Latin1.GetString(webpResult.Data!));
        Assert.Contains("VP8 ", Encoding.Latin1.GetString(webpResult.Data!));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Tiff_IdentifyingTagsAreRemovedFromEveryIfd(bool littleEndian)
    {
        var tiff = Tiff(littleEndian);
        Assert.True(ImageMetadata.HasMetadata(tiff));

        var stripped = ImageMetadata.Strip(tiff)!;

        var text = Encoding.Latin1.GetString(stripped);
        foreach (var secret in new[] { "TIFF-MAKE-SECRET", "TIFF-ARTIST-SECRET", "TIFF-EXIF-SECRET", "TIFF-SOFT-SECRET" })
            Assert.DoesNotContain(secret, text);
        Assert.Equal(tiff.Length, stripped.Length);
        Assert.False(ImageMetadata.HasMetadata(stripped));

        var ifds = ReadIfds(stripped, littleEndian);
        Assert.Equal(2, ifds.Count);
        Assert.Equal(new ushort[] { 256, 257, 258, 259, 262, 273, 278, 279 }, ifds[0]);
        Assert.Equal(new ushort[] { 256, 257, 258, 259, 262, 273, 278, 279 }, ifds[1]);
        Assert.True(stripped.AsSpan().IndexOf(TiffPixels) > 0);
    }

    [Fact]
    public void Heif_ExifOrXmpItemIsReportedAsUnsupportedMetadata()
    {
        var withExif = Heif("avif", ("Exif", null));
        var result = ImageMetadata.StripDetailed(withExif);
        Assert.Equal(ImageStripStatus.UnsupportedWithMetadata, result.Status);
        Assert.Null(result.Data);
        Assert.True(ImageMetadata.HasMetadata(withExif));
        Assert.Null(ImageMetadata.Strip(withExif));

        Assert.True(ImageMetadata.HasMetadata(Heif("heic", ("mime", "application/rdf+xml"))));
        Assert.False(ImageMetadata.HasMetadata(Heif("heic", ("hvc1", null))));
    }

    // ---------------------------------------------------------------- fixtures

    private string SimplePdf(string name)
    {
        var path = Path.Combine(_directory, name);
        using var document = new PdfDocument(new PdfWriter(path));
        new PdfCanvas(document.AddNewPage()).Rectangle(10, 10, 10, 10).Fill();
        return path;
    }

    private static PdfDictionary OutlineWithTitle(PdfDocument document, string title)
    {
        var outlines = new PdfDictionary();
        var item = new PdfDictionary();
        item.Put(PdfName.Title, new PdfString(title));
        item.Put(PdfName.Parent, outlines.MakeIndirect(document));
        item.MakeIndirect(document);
        outlines.Put(PdfName.First, item);
        outlines.Put(PdfName.Last, item);
        outlines.Put(PdfName.Count, new PdfNumber(1));
        return outlines;
    }

    private static PdfFormXObject TextAppearance(PdfDocument document, string text)
    {
        var form = new PdfFormXObject(new Rectangle(0, 0, 100, 20));
        new PdfCanvas(form, document).BeginText().SetFontAndSize(iText.Kernel.Font.PdfFontFactory.CreateFont(), 8)
            .MoveText(2, 5).ShowText(text).EndText();
        form.GetPdfObject().Put(PdfName.Metadata, new PdfStream(Encoding.ASCII.GetBytes("<x:xmpmeta>AP-XMP</x:xmpmeta>")).MakeIndirect(document));
        return form;
    }

    private static PdfDictionary Ocg(PdfDocument document, string name)
    {
        var ocg = new PdfDictionary();
        ocg.Put(PdfName.Type, PdfName.OCG);
        ocg.Put(PdfName.Name, new PdfString(name));
        ocg.MakeIndirect(document);
        return ocg;
    }

    private sealed class TextPositions : IEventListener
    {
        public List<(string Text, Vector Start)> Chunks { get; } = new();

        public void EventOccurred(IEventData data, EventType type)
        {
            if (data is TextRenderInfo info) Chunks.Add((info.GetText(), info.GetBaseline().GetStartPoint()));
        }

        public ICollection<EventType> GetSupportedEvents() => new[] { EventType.RENDER_TEXT };
    }

    private static IEnumerable<PdfObject> AllObjects(PdfDocument document) =>
        Enumerable.Range(1, document.GetNumberOfPdfObjects() - 1).Select(document.GetPdfObject).Where(o => o != null);

    /// <summary>Сырые байты файла плюс раскодированные потоки и словари всех объектов.</summary>
    private static string DecodedPdfText(string path)
    {
        var text = new StringBuilder(Encoding.Latin1.GetString(File.ReadAllBytes(path)));
        using var document = new PdfDocument(new PdfReader(path));
        foreach (var obj in AllObjects(document))
        {
            if (obj is PdfStream stream)
            {
                byte[] bytes;
                try { bytes = stream.GetBytes(true); } catch { bytes = stream.GetBytes(false); }
                text.Append('\n').Append(Encoding.Latin1.GetString(bytes));
            }
            text.Append('\n').Append(obj);
        }
        return text.ToString();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, true)) zlib.Write(data);
        return output.ToArray();
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static void JpegSegment(Stream output, byte marker, byte[] payload)
    {
        output.Write(new byte[] { 0xFF, marker, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2) });
        output.Write(payload);
    }

    /// <summary>JPEG, у которого длина APP1 с EXIF выходит за конец файла.</summary>
    private static byte[] TruncatedJpeg(string secret)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 0xFF, 0xD8 });
        JpegSegment(output, 0xE0, Concat("JFIF\0"u8.ToArray(), new byte[] { 1, 1, 0, 0, 1, 0, 1, 0, 0 }));
        output.Write(new byte[] { 0xFF, 0xE1, 0x7F, 0xFF });
        output.Write("Exif\0\0"u8);
        output.Write(Encoding.ASCII.GetBytes(secret));
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

    private static void WebpChunk(Stream output, string type, byte[] data)
    {
        output.Write(Encoding.ASCII.GetBytes(type));
        output.Write(BitConverter.GetBytes((uint)data.Length));
        output.Write(data);
        if (data.Length % 2 == 1) output.WriteByte(0);
    }

    private static void Box(Stream output, string type, byte[] payload)
    {
        var header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(payload.Length + 8));
        Encoding.ASCII.GetBytes(type).CopyTo(header, 4);
        output.Write(header);
        output.Write(payload);
    }

    private static byte[] Codestream(string comment)
    {
        using var output = new MemoryStream();
        void Segment(byte marker, byte[] payload)
        {
            output.Write(new byte[] { 0xFF, marker, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2) });
            output.Write(payload);
        }
        output.Write(new byte[] { 0xFF, 0x4F });
        Segment(0x51, new byte[39]);
        Segment(0x52, new byte[10]);
        Segment(0x5C, new byte[2]);
        Segment(0x64, Concat(new byte[] { 0, 1 }, Encoding.ASCII.GetBytes(comment)));
        // SOT: Isot, Psot (длина части тайла от SOT до конца данных), TPsot, TNsot.
        output.Write(new byte[] { 0xFF, 0x90, 0, 10, 0, 0, 0, 0, 0, 26, 0, 1 });
        output.Write(new byte[] { 0xFF, 0x93 });
        output.Write(Enumerable.Range(1, 12).Select(i => (byte)i).ToArray());
        output.Write(new byte[] { 0xFF, 0xD9 });
        return output.ToArray();
    }

    private static readonly byte[] TiffPixels = { 0xAB, 0xCD, 0xEF, 0x12 };

    /// <summary>Двухстраничный TIFF: IFD0 с Make, Artist и ExifIFD, IFD1 с Software.</summary>
    private static byte[] Tiff(bool le)
    {
        var data = new byte[1024];
        void U16(int at, int v) { if (le) BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at), (ushort)v); else BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(at), (ushort)v); }
        void U32(int at, long v) { if (le) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), (uint)v); else BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(at), (uint)v); }
        data[0] = data[1] = (byte)(le ? 'I' : 'M');
        U16(2, 42);
        U32(4, 8);
        var free = 600;
        int Ascii(string s) { var at = free; Encoding.ASCII.GetBytes(s + "\0").CopyTo(data, at); free += s.Length + 2 & ~1; return at; }
        var pixels = 900;
        TiffPixels.CopyTo(data, pixels);

        void Ifd(int at, (int Tag, int Type, int Count, long Value)[] entries, long next)
        {
            U16(at, entries.Length);
            for (var i = 0; i < entries.Length; i++)
            {
                var e = at + 2 + i * 12;
                U16(e, entries[i].Tag);
                U16(e + 2, entries[i].Type);
                U32(e + 4, entries[i].Count);
                if (entries[i].Type == 3 && entries[i].Count == 1) U16(e + 8, (int)entries[i].Value);
                else U32(e + 8, entries[i].Value);
            }
            U32(at + 2 + entries.Length * 12, next);
        }

        (int, int, int, long)[] Basic() => new (int, int, int, long)[]
        {
            (256, 3, 1, 2), (257, 3, 1, 2), (258, 3, 1, 8), (259, 3, 1, 1), (262, 3, 1, 1), (273, 4, 1, pixels), (278, 3, 1, 2), (279, 4, 1, 4)
        };
        var make = Ascii("TIFF-MAKE-SECRET");
        var artist = Ascii("TIFF-ARTIST-SECRET");
        var software = Ascii("TIFF-SOFT-SECRET");
        var comment = Ascii("TIFF-EXIF-SECRET");
        var exif = 400;
        Ifd(exif, new (int, int, int, long)[] { (37510, 7, 17, comment) }, 0);
        var ifd0 = Basic().ToList();
        ifd0.Insert(5, (271, 2, 17, make));
        ifd0.Add((315, 2, 19, artist));
        ifd0.Add((34665, 4, 1, exif));
        Ifd(8, ifd0.ToArray(), 200);
        var ifd1 = Basic().ToList();
        ifd1.Add((305, 2, 17, software));
        Ifd(200, ifd1.ToArray(), 0);
        return data;
    }

    private static List<ushort[]> ReadIfds(byte[] data, bool le)
    {
        ushort U16(int at) => le ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)) : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));
        uint U32(int at) => le ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
        var result = new List<ushort[]>();
        for (var ifd = (int)U32(4); ifd != 0 && result.Count < 10; )
        {
            var count = U16(ifd);
            result.Add(Enumerable.Range(0, count).Select(i => U16(ifd + 2 + i * 12)).ToArray());
            ifd = (int)U32(ifd + 2 + count * 12);
        }
        return result;
    }

    private static byte[] Heif(string brand, params (string Type, string? Mime)[] items)
    {
        byte[] FullBox(string type, byte version, byte[] payload)
        {
            using var box = new MemoryStream();
            Box(box, type, Concat(new byte[] { version, 0, 0, 0 }, payload));
            return box.ToArray();
        }
        var entries = items.Select((item, index) => FullBox("infe", 2, Concat(
            new byte[] { 0, (byte)(index + 1), 0, 0 }, Encoding.ASCII.GetBytes(item.Type),
            item.Mime == null ? new byte[] { 0 } : Concat(new byte[] { 0 }, Encoding.ASCII.GetBytes(item.Mime), new byte[] { 0 })))).ToArray();
        var iinf = FullBox("iinf", 0, Concat(new byte[] { 0, (byte)items.Length }, Concat(entries)));
        var meta = FullBox("meta", 0, Concat(FullBox("hdlr", 0, Concat(new byte[4], "pict"u8.ToArray(), new byte[13])), iinf));
        using var file = new MemoryStream();
        Box(file, "ftyp", Concat(Encoding.ASCII.GetBytes(brand), new byte[4], "mif1"u8.ToArray(), Encoding.ASCII.GetBytes(brand)));
        file.Write(meta);
        Box(file, "mdat", new byte[16]);
        return file.ToArray();
    }
}
