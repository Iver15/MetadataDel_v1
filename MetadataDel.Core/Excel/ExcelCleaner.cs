using DocumentFormat.OpenXml.Packaging;
using MetadataDel.Core.Audit;
using MetadataDel.Core.Cleaning;
using MetadataDel.Core.OpenXml;

namespace MetadataDel.Core.Excel;

/// <summary>Очищает метаданные OpenXML, сохраняя оригинал при ошибке.</summary>
public sealed class ExcelCleaner : IFileCleaner
{
    /// <inheritdoc />
    public async Task<CleanResult> CleanAsync(string path, CleanOptions options, CancellationToken ct = default)
    {
        try
        {
            using var transaction = new FileCleaningTransaction(path, options, ct);
            var context = new OpenXmlSanitizeContext(ct);
            using (var document = SpreadsheetDocument.Open(transaction.WorkingPath, true))
                SpreadsheetPrivacySanitizer.Sanitize(document, context);
            OpenXmlZipAttributes.Normalize(transaction.WorkingPath);
            var audit = MetadataAuditService.Audit(transaction.WorkingPath);
            for (var attempt = 0; ; attempt++)
            {
                try { transaction.Commit(ct); break; }
                catch (IOException) when (attempt < 5)
                {
                    await Task.Delay(500, ct);
                }
            }
            var warnings = new List<string>(context.Warnings);
            if (audit.HasSensitiveMetadata)
                warnings.Add("После очистки остались признаки метаданных: " + audit.Summarize());
            var timestampWarning = FileCleaningTransaction.WipeTimestamps(path, options.WipeFsTimestamps);
            if (timestampWarning != null) warnings.Add(timestampWarning);
            return new CleanResult(path, true, warnings.Count == 0 ? null : string.Join(" ", warnings));
        }
        catch (Exception ex)
        {
            return new CleanResult(path, false, ex.Message);
        }
    }
}
