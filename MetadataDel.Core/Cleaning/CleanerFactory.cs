using System;

namespace MetadataDel.Core.Cleaning;

/// <summary>
/// Фабрика, выбирающая подходящий очиститель метаданных по расширению файла.
/// </summary>
public static class CleanerFactory
{
	/// <summary>
	/// Возвращает реализацию <see cref="IFileCleaner"/> для указанного файла.
	/// </summary>
	/// <param name="path">Полный путь к файлу, для которого требуется очистка метаданных.</param>
	/// <param name="serviceProvider">Поставщик служб, содержащий зарегистрированные очистители.</param>
	/// <returns>
    /// Экземпляр очистителя для поддерживаемых типов (.pdf, .docx, .doc, .xlsx, .xls) или <c>null</c>,
	/// если расширение не поддерживается.
	/// </returns>
	public static IFileCleaner? Resolve(string path, IServiceProvider serviceProvider)
	{
		var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".pdf" => serviceProvider.GetService(typeof(Pdf.PdfCleaner)) as IFileCleaner,
            ".docx" => serviceProvider.GetService(typeof(Word.DocxCleaner)) as IFileCleaner,
            ".xlsx" => serviceProvider.GetService(typeof(Excel.ExcelCleaner)) as IFileCleaner,
            ".xls" => serviceProvider.GetService(typeof(Ole.OleDocumentCleaner)) as IFileCleaner,
            ".doc" => serviceProvider.GetService(typeof(Ole.OleDocumentCleaner)) as IFileCleaner,
            _ => null
        };
    }
}
