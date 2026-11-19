using System.Text;
using MetadataDel.Core.Audit;
using MetadataDel.Core.Cleaning;
using MetadataDel.Core.Media;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Xobject;
using iText.Kernel.Utils;
using iText.Forms;

namespace MetadataDel.Core.Pdf;

/// <summary>
/// Очиститель метаданных PDF: пересобирает документ из страниц, поэтому старые версии и неиспользуемые объекты не переносятся.
/// </summary>
public sealed class PdfCleaner : IFileCleaner
{
    private const int MaxRetries = 12;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    internal static readonly PdfName PieceInfo = new("PieceInfo");
    internal static readonly PdfName LastModified = new("LastModified");
    internal static readonly PdfName Thumb = new("Thumb");
    internal static readonly PdfName Beads = new("B");
    internal static readonly PdfName Threads = new("Threads");
    internal static readonly PdfName SpiderInfo = new("SpiderInfo");
    internal static readonly PdfName Usage = new("Usage");
    internal static readonly PdfName AssociatedFiles = new("AF");
    internal static readonly PdfName Opi = new("OPI");

    /// <summary>Подмена проверки результата в тестах; значение видно только в текущем асинхронном потоке.</summary>
    internal static readonly AsyncLocal<Func<string, MetadataAuditResult>?> AuditOverride = new();

    // Видимые пометки, оформление которых переносится в содержимое страницы. Ссылки, всплывающие окна, виджеты форм,
    // вложения, мультимедиа и заметки-иконки (Text) не переносятся: они либо не видны при печати, либо сами несут данные.
    private static readonly HashSet<string> FlattenedAnnotationTypes = new(StringComparer.Ordinal)
    {
        "Highlight", "Underline", "StrikeOut", "Squiggly", "Stamp", "FreeText", "Ink", "Square", "Circle", "Line",
        "Polygon", "PolyLine", "Caret", "Watermark"
    };

    private const int AnnotationHidden = 2;
    private const int AnnotationNoView = 32;

    /// <summary>Предупреждения очистки; часть из них означает, что очистка неполная.</summary>
    private sealed class CleaningReport
    {
        private readonly List<string> _warnings = new();

        public bool Incomplete { get; private set; }

        public void Warn(string? warning, bool incomplete)
        {
            Incomplete |= incomplete;
            if (!string.IsNullOrWhiteSpace(warning) && !_warnings.Contains(warning)) _warnings.Add(warning);
        }

        public string? Message => _warnings.Count == 0 ? null : string.Join(" ", _warnings);
    }

    /// <inheritdoc />
    public async Task<CleanResult> CleanAsync(string path, CleanOptions options, CancellationToken ct = default)
    {
        try
        {
            var report = new CleaningReport();

            ct.ThrowIfCancellationRequested();
            path = Path.GetFullPath(path);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Очистка символических ссылок не поддерживается. Укажите исходный файл.");
            if (options.Backup) FileCleaningTransaction.CreateBackup(path);

            // Читаем оригинал в память, обрабатываем, записываем результат обратно
            byte[] originalBytes = await ReadAllBytesWithRetryAsync(path, ct);
            string? removedElements;

            using (var flattened = new MemoryStream())
            {
                using (var source = new PdfDocument(new PdfReader(new MemoryStream(originalBytes)), new PdfWriter(flattened)))
                {
                    var form = PdfAcroForm.GetAcroForm(source, false);
                    if (form != null)
                    {
                        form.FlattenFields();
                        if (PdfAcroForm.GetAcroForm(source, false)?.GetAllFormFields().Count > 0)
                            throw new InvalidDataException("Не удалось сохранить видимое содержимое PDF-формы.");
                    }
                    removedElements = DescribeRemovedElements(source);
                    FlattenAnnotations(source, report);
                }
                originalBytes = flattened.ToArray();
            }
            // Справочное предупреждение: эти элементы не переносятся при пересборке, но очистку неполной не делают.
            report.Warn(removedElements, false);

            byte[] cleanedBytes;
            using (var inputStream = new MemoryStream(originalBytes))
            using (var outputStream = new MemoryStream())
            {
                using (var reader = new PdfReader(inputStream))
                using (var writer = new PdfWriter(outputStream, new WriterProperties()
                    .SetPdfVersion(PdfVersion.PDF_1_7)
                    .SetCompressionLevel(9)))
                using (var src = new PdfDocument(reader))
                using (var dst = new PdfDocument(writer))
                {
                    // XMP, PieceInfo и EXIF картинок убираются до копирования: удалённые из словарей объекты
                    // тогда не попадают в новый файл даже как неиспользуемые.
                    var imageIssues = new List<string>();
                    ScrubObjects(src, imageIssues);
                    foreach (var issue in imageIssues) report.Warn(issue, true);
                    // Аннотации (видимые уже перенесены в содержимое) не копируются вовсе: скопированный и затем удалённый
                    // объект всё равно записывается в файл вместе с вложениями, на которые ссылается.
                    for (var i = 1; i <= src.GetNumberOfPages(); i++) src.GetPage(i).GetPdfObject().Remove(PdfName.Annots);

                    // Структурное дерево (Alt, ActualText, заголовки элементов) и закладки не копируются: удаление из каталога
                    // после слияния не помогает, скопированные объекты всё равно записываются.
                    var merger = new PdfMerger(dst, new PdfMergerProperties().SetMergeTags(false).SetMergeOutlines(false));
                    merger.Merge(src, 1, src.GetNumberOfPages());

                    var info = dst.GetDocumentInfo();
                    info.SetTitle("");
                    info.SetAuthor("");
                    info.SetSubject("");
                    info.SetKeywords("");
                    info.SetCreator("");
                    info.SetProducer("");

                    RemoveEmbeddedFiles(dst);

                    var catalog = dst.GetCatalog();
                    catalog.Remove(PdfName.Metadata);
                    catalog.Remove(PdfName.OpenAction);
                    catalog.Remove(PdfName.AA);
                    catalog.Remove(PdfName.Outlines);
                    catalog.Remove(PdfName.StructTreeRoot);
                    catalog.Remove(PdfName.MarkInfo);
                    catalog.Remove(PdfName.EmbeddedFiles);
                    catalog.Remove(AssociatedFiles);
                    catalog.Remove(PieceInfo);
                    catalog.Remove(Threads);
                    catalog.Remove(SpiderInfo);
                    catalog.Remove(PdfName.Collection);
                    catalog.Remove(PdfName.Perms);
                    catalog.Remove(new PdfName("Legal"));

                    for (int i = 1; i <= dst.GetNumberOfPages(); i++)
                    {
                        var page = dst.GetPage(i);
                        page.GetPdfObject().Remove(PdfName.Metadata);
                        page.GetPdfObject().Remove(PieceInfo);
                        page.GetPdfObject().Remove(LastModified);
                        page.GetPdfObject().Remove(Thumb);
                        page.GetPdfObject().Remove(Beads);
                        page.GetPdfObject().Remove(PdfName.AA);
                        page.GetPdfObject().Remove(AssociatedFiles);
                        var anns = page.GetAnnotations();
                        for (int j = anns.Count - 1; j >= 0; j--)
                            page.RemoveAnnotation(anns[j]);
                    }

                    var acro = PdfAcroForm.GetAcroForm(dst, false);
                    if (acro != null)
                    {
                        try { acro.FlattenFields(); }
                        catch (Exception ex)
                        {
                            report.Warn($"Не удалось преобразовать поля PDF-формы в обычное содержимое ({ex.Message}): значения полей могли остаться.", true);
                        }
                        try { acro.SetNeedAppearances(false); }
                        catch (Exception ex)
                        {
                            report.Warn($"Не удалось обновить настройки PDF-формы ({ex.Message}).", true);
                        }
                    }

                    AnonymizeLayers(dst);
                }
                cleanedBytes = outputStream.ToArray();
            }

            // iText (AGPL) при закрытии всегда пишет Producer, даты и служебный комментарий с версией.
            // Правки байтов не меняют длину, поэтому таблица xref остаётся верной.
            BlankInfoDictionary(cleanedBytes);
            BlankProducerComment(cleanedBytes);

            var finalPath = path;

            try
            {
                await PersistCleanedPdfAsync(path, path, cleanedBytes, options, report, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var fallbackPath = GetFallbackCopyPath(path);
                try
                {
                    await PersistCleanedPdfAsync(fallbackPath, path, cleanedBytes, options, report, ct);
                    finalPath = fallbackPath;
                    report.Warn($"Исходный PDF не удалось перезаписать (занят другой программой или защищён от записи), поэтому очищенная копия сохранена рядом: {fallbackPath}. Оригинал не изменён и по-прежнему содержит метаданные.", true);
                }
                catch (Exception fallbackEx)
                {
                    throw new IOException(
                        $"Не удалось обновить исходный PDF и не удалось сохранить очищенную копию рядом. Исходная ошибка: {ex.Message}",
                        fallbackEx);
                }
            }

            return new CleanResult(finalPath, true, report.Message) { Incomplete = report.Incomplete };
        }
        catch (Exception ex)
        {
            return new CleanResult(path, false, ex.Message);
        }
    }

    private static async Task PersistCleanedPdfAsync(string path, string permissionSource, byte[] cleanedBytes, CleanOptions options,
        CleaningReport report, CancellationToken ct)
    {
        var outputPath = path;
        var temporaryPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $".mdel-{Guid.NewGuid():N}.pdf");
        try
        {
        path = temporaryPath;
        await File.WriteAllBytesAsync(path, cleanedBytes, ct);

        // exiftool здесь не используется: для PDF он дописывает инкрементальное обновление,
        // и в файле остаётся предыдущая версия, а удалять после пересборки ему уже нечего.
        var (audit, auditError) = await TryAuditWithRetryAsync(path, ct);
        if (audit == null)
        {
            report.Warn($"Не удалось проверить результат очистки PDF ({auditError?.Message ?? "неизвестная ошибка"}): " +
                        "часть метаданных могла остаться.", true);
        }
        else if (audit.HasSensitiveMetadata)
        {
            report.Warn("После очистки остались признаки метаданных: " + audit.Summarize(), true);
        }

        ct.ThrowIfCancellationRequested();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(outputPath))
                    using (new FileStream(outputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                FileCleaningTransaction.Publish(temporaryPath, outputPath, permissionSource);
                break;
            }
            catch (IOException) when (attempt < MaxRetries) { await Task.Delay(RetryDelay, ct); }
        }
        report.Warn(FileCleaningTransaction.WipeTimestamps(outputPath, options.WipeFsTimestamps), false);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    /// <summary>
    /// Чтение файла с retry и FileShare.ReadWrite — для сетевых дисков,
    /// где Explorer/антивирус/индексатор могут кратковременно блокировать файл.
    /// </summary>
    private static async Task<byte[]> ReadAllBytesWithRetryAsync(string path, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var bytes = new byte[fs.Length];
                await fs.ReadExactlyAsync(bytes, ct);
                return bytes;
            }
            catch (IOException) when (attempt < MaxRetries)
            {
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    /// <summary>
    /// Аудит — проверочный шаг. Если файл кратковременно блокируется внешним процессом, несколько раз пробуем снова.
    /// Ошибка проверки не прерывает очистку, но возвращается вызывающему коду: результат тогда считается неполным.
    /// </summary>
    private static async Task<(MetadataAuditResult? Result, Exception? Error)> TryAuditWithRetryAsync(string path, CancellationToken ct)
    {
        var audit = AuditOverride.Value ?? MetadataAuditService.Audit;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return (audit(path), null);
            }
            catch (IOException) when (attempt < MaxRetries)
            {
                await Task.Delay(RetryDelay, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (null, ex);
            }
        }
    }

    private static string GetFallbackCopyPath(string originalPath)
    {
        var directory = Path.GetDirectoryName(originalPath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(originalPath);
        var extension = Path.GetExtension(originalPath);

        var firstCandidate = Path.Combine(directory, fileName + ".MetadataDel.cleaned" + extension);
        if (!File.Exists(firstCandidate))
        {
            return firstCandidate;
        }

        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(directory, $"{fileName}.MetadataDel.cleaned ({index}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Переносит нормальное оформление (/AP /N, с учётом /AS) видимых пометок в содержимое страницы по ISO 32000-1, 12.5.5:
    /// BBox оформления, преобразованный его /Matrix, вписывается в /Rect аннотации. Сами аннотации затем удаляются.
    /// Неприменённые пометки редактирования (Redact) не переносятся: их оформление скрыло бы текст, но не удалило его.
    /// </summary>
    private static void FlattenAnnotations(PdfDocument document, CleaningReport report)
    {
        var redactions = false;
        var dropped = 0;
        for (var pageNumber = 1; pageNumber <= document.GetNumberOfPages(); pageNumber++)
        {
            var page = document.GetPage(pageNumber);
            var annotations = page.GetPdfObject().GetAsArray(PdfName.Annots);
            if (annotations == null) continue;

            var drawings = new List<(Action<PdfCanvas> Draw, PdfDictionary? OptionalContent)>();
            for (var i = 0; i < annotations.Size(); i++)
            {
                var annotation = annotations.GetAsDictionary(i);
                var subtype = annotation?.GetAsName(PdfName.Subtype)?.GetValue();
                if (annotation == null || subtype == null) continue;
                if (subtype == "Redact")
                {
                    redactions = true;
                    continue;
                }
                if (!FlattenedAnnotationTypes.Contains(subtype)) continue;
                var flags = annotation.GetAsNumber(PdfName.F)?.IntValue() ?? 0;
                if ((flags & (AnnotationHidden | AnnotationNoView)) != 0) continue;
                var optionalContent = annotation.GetAsDictionary(PdfName.OC);
                var appearance = GetNormalAppearance(annotation);
                if (appearance == null)
                {
                    // Выделение и подчёркивание без /AP рисуются по /QuadPoints и /C так, как их показывают просмотрщики.
                    var markup = TextMarkupDrawing(annotation, subtype);
                    if (markup != null) drawings.Add((markup, optionalContent));
                    else dropped++;
                    continue;
                }
                var rect = annotation.GetAsArray(PdfName.Rect);
                var transform = rect == null || rect.Size() != 4 ? null : AppearanceTransform(appearance, rect);
                if (transform == null)
                {
                    dropped++;
                    continue;
                }
                var m = transform;
                drawings.Add((canvas => canvas.AddXObjectWithTransformationMatrix(new PdfFormXObject(appearance), m[0], m[1], m[2], m[3], m[4], m[5]),
                    optionalContent));
            }
            if (drawings.Count == 0) continue;

            // Исходное содержимое оборачивается в q/Q, чтобы его незакрытое состояние графики не сдвигало оформление.
            page.NewContentStreamBefore().GetOutputStream().WriteBytes(Encoding.ASCII.GetBytes("q\n"));
            var after = page.NewContentStreamAfter();
            after.GetOutputStream().WriteBytes(Encoding.ASCII.GetBytes("Q\n"));
            var pageCanvas = new PdfCanvas(after, page.GetResources(), document);
            foreach (var (draw, optionalContent) in drawings)
            {
                pageCanvas.SaveState();
                if (optionalContent != null) pageCanvas.BeginMarkedContent(PdfName.OC, optionalContent);
                draw(pageCanvas);
                if (optionalContent != null) pageCanvas.EndMarkedContent();
                pageCanvas.RestoreState();
            }
        }
        // Пометка Redact только закрывает текст при показе: сам текст остаётся в файле, поэтому очистка неполная.
        if (redactions)
            report.Warn("В PDF были неприменённые пометки редактирования (Redact): они удалены, но текст и изображения под ними " +
                        "остались в документе. Примените редактирование в PDF-редакторе и очистите файл ещё раз.", true);
        if (dropped > 0)
            report.Warn($"Видимые пометки PDF без готового оформления ({dropped} шт.) не перенесены на страницу и удалены: " +
                        "проверьте документ перед отправкой.", false);
    }

    private static readonly HashSet<string> TextMarkupTypes = new(StringComparer.Ordinal) { "Highlight", "Underline", "StrikeOut", "Squiggly" };

    /// <summary>
    /// Оформление пометки текста (ISO 32000-1, 12.5.6.10) по /QuadPoints и /C: выделение — заливка четырёхугольников
    /// в режиме наложения Multiply, подчёркивание, зачёркивание и волнистая линия — линии вдоль нижнего края и середины.
    /// <c>null</c> — пометка другого типа, без цвета или без корректных /QuadPoints.
    /// </summary>
    private static Action<PdfCanvas>? TextMarkupDrawing(PdfDictionary annotation, string subtype)
    {
        if (!TextMarkupTypes.Contains(subtype)) return null;
        var quadPoints = annotation.GetAsArray(PdfName.QuadPoints);
        if (quadPoints == null || quadPoints.Size() == 0 || quadPoints.Size() % 8 != 0) return null;
        var values = Enumerable.Range(0, quadPoints.Size()).Select(i => quadPoints.GetAsNumber(i)?.FloatValue()).ToList();
        if (values.Any(v => v == null || !float.IsFinite(v.Value))) return null;
        var color = MarkupColor(annotation.GetAsArray(PdfName.C), subtype);
        if (color == null) return null;
        var opacity = Math.Clamp(annotation.GetAsNumber(PdfName.CA)?.FloatValue() ?? 1f, 0f, 1f);

        var quads = new List<Quad>();
        for (var q = 0; q < values.Count; q += 8)
        {
            var points = Enumerable.Range(0, 4).Select(p => (X: values[q + 2 * p]!.Value, Y: values[q + 2 * p + 1]!.Value)).ToList();
            // Порядок точек у разных программ разный (Acrobat: верх-лево, верх-право, низ-лево, низ-право; ISO — против часовой),
            // поэтому нижний и верхний края определяются по координате Y, левый и правый концы — по X.
            var ordered = points.OrderBy(p => p.Y).ToList();
            var bottom = ordered.Take(2).OrderBy(p => p.X).ToList();
            var top = ordered.Skip(2).OrderBy(p => p.X).ToList();
            quads.Add(new Quad(bottom[0], bottom[1], top[0], top[1]));
        }

        return canvas =>
        {
            var state = new iText.Kernel.Pdf.Extgstate.PdfExtGState();
            if (subtype == "Highlight") state.SetBlendMode(iText.Kernel.Pdf.Extgstate.PdfExtGState.BM_MULTIPLY);
            state.SetFillOpacity(opacity).SetStrokeOpacity(opacity);
            canvas.SetExtGState(state);
            if (subtype == "Highlight")
            {
                canvas.SetFillColor(color);
                foreach (var quad in quads)
                {
                    canvas.MoveTo(quad.BottomLeft.X, quad.BottomLeft.Y).LineTo(quad.BottomRight.X, quad.BottomRight.Y)
                        .LineTo(quad.TopRight.X, quad.TopRight.Y).LineTo(quad.TopLeft.X, quad.TopLeft.Y).ClosePath();
                }
                canvas.Fill();
                return;
            }
            canvas.SetStrokeColor(color);
            foreach (var quad in quads)
            {
                var height = quad.Height;
                if (height < 1e-3f) continue;
                var width = Math.Max(0.5f, height / 14f);
                canvas.SetLineWidth(width);
                switch (subtype)
                {
                    case "StrikeOut":
                        StrokeAlong(canvas, quad, 0.5f);
                        break;
                    case "Underline":
                        StrokeAlong(canvas, quad, width / height);
                        break;
                    default:
                        // Волнистая линия у нижнего края: зубцы высотой около 1/8 строки.
                        var length = quad.Length;
                        var steps = Math.Clamp((int)(length / Math.Max(1f, height / 4f)), 2, 2000);
                        const float amplitude = 0.125f;
                        var start = quad.At(0, width / height);
                        canvas.MoveTo(start.X, start.Y);
                        for (var step = 1; step <= steps; step++)
                        {
                            var point = quad.At((float)step / steps, width / height + (step % 2 == 1 ? amplitude : 0));
                            canvas.LineTo(point.X, point.Y);
                        }
                        canvas.Stroke();
                        break;
                }
            }
        };
    }

    private static void StrokeAlong(PdfCanvas canvas, Quad quad, float level)
    {
        var start = quad.At(0, level);
        var end = quad.At(1, level);
        canvas.MoveTo(start.X, start.Y).LineTo(end.X, end.Y).Stroke();
    }

    /// <summary>Четырёхугольник строки текста; <see cref="At"/> — точка на доле длины и доле высоты.</summary>
    private readonly record struct Quad((float X, float Y) BottomLeft, (float X, float Y) BottomRight, (float X, float Y) TopLeft, (float X, float Y) TopRight)
    {
        public float Height => Distance(BottomLeft, TopLeft);

        public float Length => Distance(BottomLeft, BottomRight);

        public (float X, float Y) At(float along, float up)
        {
            var bottom = (X: BottomLeft.X + (BottomRight.X - BottomLeft.X) * along, Y: BottomLeft.Y + (BottomRight.Y - BottomLeft.Y) * along);
            var top = (X: TopLeft.X + (TopRight.X - TopLeft.X) * along, Y: TopLeft.Y + (TopRight.Y - TopLeft.Y) * along);
            return (bottom.X + (top.X - bottom.X) * up, bottom.Y + (top.Y - bottom.Y) * up);
        }

        private static float Distance((float X, float Y) a, (float X, float Y) b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    }

    /// <summary>Цвет /C (1 — серый, 3 — RGB, 4 — CMYK); пустой массив означает «без цвета». Без /C — цвета по умолчанию просмотрщиков.</summary>
    private static iText.Kernel.Colors.Color? MarkupColor(PdfArray? components, string subtype)
    {
        if (components == null)
            return subtype == "Highlight" ? new iText.Kernel.Colors.DeviceRgb(1f, 1f, 0f) : new iText.Kernel.Colors.DeviceRgb(1f, 0f, 0f);
        var c = Enumerable.Range(0, components.Size()).Select(i => Math.Clamp(components.GetAsNumber(i)?.FloatValue() ?? 0f, 0f, 1f)).ToArray();
        return c.Length switch
        {
            1 => new iText.Kernel.Colors.DeviceGray(c[0]),
            3 => new iText.Kernel.Colors.DeviceRgb(c[0], c[1], c[2]),
            4 => new iText.Kernel.Colors.DeviceCmyk(c[0], c[1], c[2], c[3]),
            _ => null
        };
    }

    /// <summary>
    /// Ссылки, закладки, структура тегов, заметки, вложения в комментариях и метки страниц при пересборке не переносятся:
    /// возвращает одно справочное предупреждение со списком того, что было в исходном файле, или <c>null</c>.
    /// </summary>
    private static string? DescribeRemovedElements(PdfDocument document)
    {
        var catalog = document.GetCatalog().GetPdfObject();
        var subtypes = new HashSet<string>(StringComparer.Ordinal);
        for (var pageNumber = 1; pageNumber <= document.GetNumberOfPages(); pageNumber++)
        {
            var annotations = document.GetPage(pageNumber).GetPdfObject().GetAsArray(PdfName.Annots);
            for (var i = 0; i < (annotations?.Size() ?? 0); i++)
                if (annotations!.GetAsDictionary(i)?.GetAsName(PdfName.Subtype)?.GetValue() is { } subtype) subtypes.Add(subtype);
        }
        var kinds = new List<string>();
        if (subtypes.Contains("Link")) kinds.Add("ссылки");
        if (catalog.GetAsDictionary(PdfName.Outlines)?.Get(PdfName.First) != null) kinds.Add("закладки");
        if (catalog.Get(PdfName.StructTreeRoot) != null) kinds.Add("структура тегов");
        if (subtypes.Contains("Text")) kinds.Add("заметки");
        if (subtypes.Contains("FileAttachment")) kinds.Add("вложения в комментариях");
        if (catalog.Get(PdfName.PageLabels) != null) kinds.Add("метки страниц");
        if (kinds.Count == 0) return null;
        var list = kinds.Count == 1 ? kinds[0] : string.Join(", ", kinds.Take(kinds.Count - 1)) + " и " + kinds[^1];
        return $"Удалены элементы PDF, в которых хранятся служебные данные: {list}.";
    }

    private static PdfStream? GetNormalAppearance(PdfDictionary annotation)
    {
        var normal = annotation.GetAsDictionary(PdfName.AP)?.Get(PdfName.N);
        if (normal is PdfStream stream) return stream;
        if (normal is not PdfDictionary states) return null;
        var state = annotation.GetAsName(PdfName.AS);
        return state == null ? null : states.GetAsStream(state);
    }

    /// <summary>Матрица, отображающая преобразованный /Matrix прямоугольник BBox оформления на /Rect аннотации.</summary>
    private static float[]? AppearanceTransform(PdfStream appearance, PdfArray rect)
    {
        var bbox = appearance.GetAsArray(PdfName.BBox);
        if (bbox == null || bbox.Size() != 4) return null;
        var matrix = appearance.GetAsArray(PdfName.Matrix) is { } array && array.Size() == 6
            ? Enumerable.Range(0, 6).Select(i => array.GetAsNumber(i)?.FloatValue() ?? 0).ToArray()
            : new float[] { 1, 0, 0, 1, 0, 0 };
        var x = new[] { bbox.GetAsNumber(0)?.FloatValue() ?? 0, bbox.GetAsNumber(2)?.FloatValue() ?? 0 };
        var y = new[] { bbox.GetAsNumber(1)?.FloatValue() ?? 0, bbox.GetAsNumber(3)?.FloatValue() ?? 0 };
        var corners = (from cx in x from cy in y
            select (X: matrix[0] * cx + matrix[2] * cy + matrix[4], Y: matrix[1] * cx + matrix[3] * cy + matrix[5])).ToList();
        float minX = corners.Min(c => c.X), maxX = corners.Max(c => c.X), minY = corners.Min(c => c.Y), maxY = corners.Max(c => c.Y);
        var r = Enumerable.Range(0, 4).Select(i => rect.GetAsNumber(i)?.FloatValue() ?? 0).ToArray();
        float left = Math.Min(r[0], r[2]), right = Math.Max(r[0], r[2]), bottom = Math.Min(r[1], r[3]), top = Math.Max(r[1], r[3]);
        if (maxX - minX < 1e-3f || maxY - minY < 1e-3f || right - left < 1e-3f || top - bottom < 1e-3f) return null;
        var sx = (right - left) / (maxX - minX);
        var sy = (top - bottom) / (maxY - minY);
        return new[] { sx, 0, 0, sy, left - minX * sx, bottom - minY * sy };
    }

    /// <summary>
    /// Убирает метаданные из объектов исходного документа до копирования страниц. Проблемы, из-за которых очистка
    /// изображений неполная, добавляются в <paramref name="incompleteWarnings"/>.
    /// </summary>
    internal static void ScrubObjects(PdfDocument document, ICollection<string>? incompleteWarnings = null)
    {
        for (var number = 1; number < document.GetNumberOfPdfObjects(); number++)
        {
            if (document.GetPdfObject(number) is not PdfDictionary dictionary) continue;
            dictionary.Remove(PdfName.Metadata);
            dictionary.Remove(PieceInfo);
            dictionary.Remove(LastModified);
            // Связанные файлы (AF) и ссылки OPI на исходные изображения (пути на диске допечатной подготовки):
            // без ссылки сами вложения при пересборке не копируются.
            dictionary.Remove(AssociatedFiles);
            dictionary.Remove(Opi);
            if (PdfName.Page.Equals(dictionary.GetAsName(PdfName.Type)))
            {
                dictionary.Remove(Thumb);
                dictionary.Remove(Beads);
            }
            if (dictionary is PdfStream stream && PdfName.Image.Equals(stream.GetAsName(PdfName.Subtype)))
                StripImageMetadata(stream, incompleteWarnings);
        }
    }

    /// <summary>JPEG и JPEG 2000 хранятся в PDF целиком, вместе с EXIF (GPS, камера) и XMP — в том числе за цепочкой фильтров.</summary>
    private static void StripImageMetadata(PdfStream stream, ICollection<string>? incompleteWarnings)
    {
        var image = ReadEncodedImage(stream);
        if (image == null) return;
        var (bytes, filter, decodeParms, error) = image.Value;
        if (error != null)
        {
            incompleteWarnings?.Add($"Не удалось раскодировать изображение в PDF ({error}): его метаданные не проверены.");
            return;
        }
        var result = ImageMetadata.StripDetailed(bytes!);
        if (result.Status == ImageStripStatus.Malformed)
            incompleteWarnings?.Add("Изображение в PDF повреждено: метаданные удалены только из читаемой части, в остальной они могли остаться.");
        else if (result.Status == ImageStripStatus.UnsupportedWithMetadata)
            incompleteWarnings?.Add("В PDF есть изображение в формате, из которого MetadataDel не умеет удалять метаданные.");
        if (result.Data == null) return;
        // SetData считает данные раскодированными и убирает фильтр — возвращаем последний (DCT/JPX), чтобы байты не пережимались.
        // Предшествующие фильтры цепочки уже раскодированы и больше не нужны.
        stream.SetData(result.Data);
        stream.Put(PdfName.Filter, filter);
        if (decodeParms != null) stream.Put(PdfName.DecodeParms, decodeParms);
        else stream.Remove(PdfName.DecodeParms);
    }

    /// <summary>
    /// Возвращает байты JPEG/JPEG 2000 из потока изображения, раскодировав фильтры перед DCTDecode/JPXDecode
    /// (например, <c>[/FlateDecode /DCTDecode]</c>), последний фильтр и его DecodeParms. <c>null</c> — поток не JPEG/JPEG 2000;
    /// <c>Error</c> — цепочку раскодировать не удалось.
    /// </summary>
    internal static (byte[]? Bytes, PdfName Filter, PdfObject? DecodeParms, string? Error)? ReadEncodedImage(PdfStream stream)
    {
        var filters = stream.Get(PdfName.Filter) switch
        {
            PdfName name => new List<PdfName> { name },
            PdfArray array => Enumerable.Range(0, array.Size()).Select(array.GetAsName).ToList(),
            _ => null
        };
        if (filters == null || filters.Count == 0 || filters.Any(f => f == null)) return null;
        var last = filters[^1];
        if (!last.Equals(PdfName.DCTDecode) && !last.Equals(PdfName.JPXDecode)) return null;

        var parms = stream.Get(PdfName.DecodeParms);
        PdfObject? lastParms = parms is PdfArray parmsArray
            ? parmsArray.Size() == filters.Count ? parmsArray.Get(filters.Count - 1) : null
            : filters.Count == 1 ? parms : null;
        if (lastParms is PdfNull) lastParms = null;
        if (filters.Count == 1) return (stream.GetBytes(false), last, lastParms, null);

        try
        {
            // Раскодируем только предшествующие фильтры: словарь-заместитель содержит цепочку без последнего звена.
            var prefix = new PdfDictionary();
            prefix.Put(PdfName.Filter, new PdfArray(filters.Take(filters.Count - 1).Cast<PdfObject>().ToList()));
            if (parms is PdfArray all && all.Size() == filters.Count)
                prefix.Put(PdfName.DecodeParms, new PdfArray(Enumerable.Range(0, filters.Count - 1).Select(i => all.Get(i)).ToList()));
            return (PdfReader.DecodeBytes(stream.GetBytes(false), prefix), last, lastParms, null);
        }
        catch (Exception ex)
        {
            return (null, last, lastParms, string.Join(" ", filters.Select(f => "/" + f.GetValue())) + ": " + ex.Message);
        }
    }

    /// <summary>
    /// Имена слоёв и сведения о создавшем их приложении заменяются нейтральными. Кроме слоёв из /OCProperties
    /// переименовываются группы, на которые ссылаются только ресурсы страниц, форм и шаблонов (/Properties, /OC, OCMD).
    /// </summary>
    private static void AnonymizeLayers(PdfDocument document)
    {
        var renamed = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var properties = document.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.OCProperties);
        var groups = properties?.GetAsArray(PdfName.OCGs);
        for (var i = 0; i < (groups?.Size() ?? 0); i++)
        {
            var group = groups!.GetAsDictionary(i);
            if (group == null) continue;
            group.Put(PdfName.Name, new PdfString($"Layer {i + 1}"));
            group.Remove(Usage);
            renamed.Add(group);
        }
        if (properties != null)
        {
            var configurations = new List<PdfDictionary?> { properties.GetAsDictionary(PdfName.D) };
            var extra = properties.GetAsArray(PdfName.Configs);
            for (var i = 0; i < (extra?.Size() ?? 0); i++) configurations.Add(extra!.GetAsDictionary(i));
            foreach (var configuration in configurations.OfType<PdfDictionary>())
            {
                configuration.Remove(PdfName.Name);
                configuration.Remove(PdfName.Creator);
            }
        }

        var next = renamed.Count + 1;
        var visited = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        void Rename(PdfObject? value)
        {
            switch (value)
            {
                case PdfArray array:
                    for (var i = 0; i < array.Size(); i++) Rename(array.Get(i));
                    break;
                case PdfDictionary dictionary when visited.Add(dictionary):
                    var type = dictionary.GetAsName(PdfName.Type);
                    if (PdfName.OCG.Equals(type))
                    {
                        if (renamed.Add(dictionary))
                        {
                            dictionary.Put(PdfName.Name, new PdfString($"Layer {next++}"));
                            dictionary.Remove(Usage);
                        }
                    }
                    else if (PdfName.OCMD.Equals(type))
                    {
                        Rename(dictionary.Get(PdfName.OCGs));
                        Rename(dictionary.Get(PdfName.VE));
                    }
                    break;
            }
        }
        void Walk(PdfDictionary? resources)
        {
            if (resources == null || !visited.Add(resources)) return;
            var layerProperties = resources.GetAsDictionary(PdfName.Properties);
            foreach (var key in layerProperties?.KeySet() ?? (ICollection<PdfName>)Array.Empty<PdfName>())
                Rename(layerProperties!.Get(key));
            foreach (var category in new[] { PdfName.XObject, PdfName.Pattern, PdfName.Font })
            {
                var entries = resources.GetAsDictionary(category);
                foreach (var key in entries?.KeySet() ?? (ICollection<PdfName>)Array.Empty<PdfName>())
                {
                    if (entries!.Get(key) is not PdfDictionary child) continue;
                    Rename(child.Get(PdfName.OC));
                    Walk(child.GetAsDictionary(PdfName.Resources));
                }
            }
        }
        for (var i = 1; i <= document.GetNumberOfPages(); i++) Walk(document.GetPage(i).GetResources().GetPdfObject());
    }

    /// <summary>Заменяет содержимое Info-словаря пробелами: остаётся пустой словарь &lt;&lt; &gt;&gt;.</summary>
    private static void BlankInfoDictionary(byte[] pdf)
    {
        // Правим только объект, на который указывает xref: поиск по всему файлу задел бы содержимое страниц.
        using var inspection = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        var reference = inspection.GetTrailer().GetAsDictionary(PdfName.Info)?.GetIndirectReference();
        if (reference == null) return;
        var offset = checked((int)reference.GetOffset());
        var text = Encoding.Latin1.GetString(pdf, offset, Math.Min(pdf.Length - offset, 1 << 20));
        var end = text.IndexOf("endobj", StringComparison.Ordinal);
        if (end < 0) throw new InvalidDataException("Не найден конец Info-объекта PDF.");
        var open = text.IndexOf("<<", StringComparison.Ordinal);
        var close = text.LastIndexOf(">>", end, StringComparison.Ordinal);
        if (open < 0 || close <= open) throw new InvalidDataException("Не найден словарь Info-объекта PDF.");
        pdf.AsSpan(offset + open + 2, close - open - 2).Fill((byte)' ');
    }

    private static void BlankProducerComment(byte[] pdf)
    {
        var index = pdf.AsSpan().LastIndexOf("%iText"u8);
        if (index < 0) return;
        for (var i = index + 1; i < pdf.Length && pdf[i] is not ((byte)'\r' or (byte)'\n'); i++)
            pdf[i] = (byte)' ';
    }

    private static void RemoveEmbeddedFiles(PdfDocument doc)
    {
        var catalog = doc.GetCatalog();
        var namesDict = catalog.GetPdfObject().GetAsDictionary(PdfName.Names);
        if (namesDict == null)
        {
            return;
        }

        var embedded = namesDict.GetAsDictionary(PdfName.EmbeddedFiles);
        if (embedded != null)
        {
            RemoveEmbeddedFilesTree(embedded);
            namesDict.Remove(PdfName.EmbeddedFiles);
        }

        if (namesDict.IsEmpty())
        {
            catalog.GetPdfObject().Remove(PdfName.Names);
        }
    }

    private static void RemoveEmbeddedFilesTree(PdfDictionary tree)
    {
        if (tree == null)
        {
            return;
        }

        var names = tree.GetAsArray(PdfName.Names);
        if (names != null)
        {
            for (int i = 1; i < names.Size(); i += 2)
            {
                var fileSpec = names.GetAsDictionary(i);
                RemoveFileSpec(fileSpec);
            }
            names.Clear();
        }

        var kids = tree.GetAsArray(PdfName.Kids);
        if (kids != null)
        {
            for (int i = 0; i < kids.Size(); i++)
            {
                var kid = kids.GetAsDictionary(i);
                if (kid != null)
                {
                    RemoveEmbeddedFilesTree(kid);
                }
            }
            kids.Clear();
        }

        tree.Clear();
    }

    private static void RemoveFileSpec(PdfDictionary? fileSpec)
    {
        if (fileSpec == null)
        {
            return;
        }

        var ef = fileSpec.GetAsDictionary(PdfName.EF);
        if (ef != null)
        {
            ef.Clear();
        }

        fileSpec.Remove(PdfName.EF);
        fileSpec.Remove(PdfName.F);
        fileSpec.Remove(PdfName.UF);
        fileSpec.Remove(new PdfName("DOS"));
        fileSpec.Remove(new PdfName("Mac"));
        fileSpec.Remove(new PdfName("Unix"));
        fileSpec.Clear();
    }
}
