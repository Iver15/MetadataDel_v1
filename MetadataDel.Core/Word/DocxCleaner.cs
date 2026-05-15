using DocumentFormat.OpenXml.Packaging;
using MetadataDel.Core.Audit;
using MetadataDel.Core.Cleaning;
using MetadataDel.Core.OpenXml;

namespace MetadataDel.Core.Word;

/// <summary>
/// Очиститель метаданных для файлов Word в формате OpenXML (.docx).
/// </summary>
public sealed class DocxCleaner : IFileCleaner
{
	private const int MaxRetries = 5;
	private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

	/// <inheritdoc />
	public async Task<CleanResult> CleanAsync(string path, CleanOptions options, CancellationToken ct = default)
	{
		try
		{
			// Резервная копия до изменения
			if (options.Backup)
			{
				File.Copy(path, path + ".bak", overwrite: true);
			}

			// Открываем оригинал с retry — на сетевых дисках Explorer/антивирус
			// могут кратковременно блокировать файл
			for (int attempt = 0; ; attempt++)
			{
				try
				{
					using (var document = WordprocessingDocument.Open(path, true))
					{
						WordPrivacySanitizer.Sanitize(document);
					}
					OpenXmlZipAttributes.Normalize(path);
					break;
				}
				catch (IOException) when (attempt < MaxRetries)
				{
					await Task.Delay(RetryDelay, ct);
				}
			}

			WipeFileSystemTimestamps(path, options.WipeFsTimestamps);

			string? warning = null;
			try
			{
				var audit = MetadataAuditService.Audit(path);
				if (audit.HasSensitiveMetadata)
					warning = "После очистки остались признаки метаданных: " + audit.Summarize();
			}
			catch
			{
				// Аудит — проверочный шаг, не должен валить успешную очистку
			}

			return new CleanResult(path, true, warning);
		}
		catch (Exception ex)
		{
			return new CleanResult(path, false, ex.Message);
		}
	}

	private static void WipeFileSystemTimestamps(string path, bool wipeFsTimestamps)
	{
		if (!wipeFsTimestamps) return;
		try
		{
			var ts = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
			File.SetCreationTimeUtc(path, ts);
			File.SetLastWriteTimeUtc(path, ts);
			File.SetLastAccessTimeUtc(path, ts);
		}
		catch { }
	}
}
