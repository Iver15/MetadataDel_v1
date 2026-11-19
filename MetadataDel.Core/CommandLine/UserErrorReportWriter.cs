using System.Text;

namespace MetadataDel.Core.CommandLine;

/// <summary>
/// Plain-language explanation of a failed file. Written to the private logs folder, never next to the document:
/// the report contains full paths and must neither travel with the folder nor overwrite the user's files.
/// </summary>
public static class UserErrorReportWriter
{
    /// <summary>Writes a new uniquely named report; returns its path or null when it could not be written.</summary>
    public static string? TryWrite(string path, string reason, Exception? exception = null)
    {
        try
        {
            var directory = AppDataPaths.EnsurePrivateDirectory(AppDataPaths.LogsDirectory);
            var name = SafeName(Path.GetFileName(path));
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var reportPath = Path.Combine(directory,
                    $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}-{name}.MetadataDel-ошибка.txt");
                try
                {
                    using var stream = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                    writer.Write(BuildReport(path, reason, exception));
                    return reportPath;
                }
                catch (IOException) when (File.Exists(reportPath)) { }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    // Keeps the report recognisable without letting the document name break the file name on any platform.
    private static string SafeName(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
        var safe = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (safe.Length > 80) safe = safe[..80];
        return safe.Length == 0 ? "файл" : safe;
    }

    private static string BuildReport(string path, string reason, Exception? exception)
    {
        var (title, explanation, actions) = DescribeProblem(path, reason, exception);
        var builder = new StringBuilder();
        builder.AppendLine("Не удалось обработать файл");
        builder.AppendLine();
        builder.AppendLine("Файл:");
        builder.AppendLine(path);
        builder.AppendLine();
        builder.AppendLine("Что случилось:");
        builder.AppendLine(title);
        builder.AppendLine();
        builder.AppendLine("Почему это могло произойти:");
        builder.AppendLine(explanation);
        builder.AppendLine();
        builder.AppendLine("Что сделать:");

        foreach (var action in actions)
        {
            builder.AppendLine("- " + action);
        }

        builder.AppendLine();
        builder.AppendLine("Если ошибка повторяется, передайте этот файл тому, кто поддерживает программу.");
        builder.AppendLine();
        builder.AppendLine("Служебная информация:");
        builder.AppendLine(reason);

        if (exception != null)
        {
            builder.AppendLine();
            builder.AppendLine("Подробности:");
            builder.AppendLine(exception.Message);
        }

        builder.AppendLine();
        builder.AppendLine("Когда это произошло:");
        builder.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        return builder.ToString();
    }

    private static (string Title, string Explanation, string[] Actions) DescribeProblem(string path, string reason, Exception? exception)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var text = ((reason ?? string.Empty) + " " + exception?.Message).ToLowerInvariant();

        if (text.Contains("файл не найден"))
        {
            return (
                "Файл не найден.",
                "Программа искала документ в указанной папке, но не смогла его там открыть.",
                new[]
                {
                    "Проверьте, есть ли файл в этой папке.",
                    "Если файл был перемещён или переименован, запустите очистку заново."
                });
        }

        if (text.Contains("расширение не поддерживается"))
        {
            return (
                "Этот тип файла программа пока не умеет очищать.",
                "Такой формат документа сейчас не поддерживается.",
                new[]
                {
                    "Проверьте расширение файла.",
                    "Если это документ Office, попробуйте сначала сохранить его как .docx или .xlsx, а потом повторите попытку."
                });
        }

        if (text.Contains("word не найден") || text.Contains("microsoft office word"))
        {
            return (
                "На компьютере не найден Microsoft Word.",
                "Чтобы очистить старый файл Word формата .doc, программе нужен установленный Microsoft Word.",
                new[]
                {
                    "Установите Microsoft Word и повторите попытку.",
                    "Либо откройте документ в Word, сохраните его как .docx и очистите уже новый файл."
                });
        }

        if (text.Contains("excel не найден") || text.Contains("microsoft excel"))
        {
            return (
                "На компьютере не найден Microsoft Excel.",
                "Чтобы очистить старый файл Excel формата .xls, программе нужен установленный Microsoft Excel.",
                new[]
                {
                    "Установите Microsoft Excel и повторите попытку.",
                    "Либо откройте таблицу в Excel, сохраните её как .xlsx и очистите уже новый файл."
                });
        }

        if (text.Contains("access") || text.Contains("доступ") || text.Contains("used by another process") || text.Contains("занят"))
        {
            return (
                "Файл сейчас используется другой программой.",
                "Обычно это значит, что документ открыт в Word, Excel, PDF-программе, просмотрщике или ещё не закончил синхронизироваться.",
                new[]
                {
                    "Закройте файл везде, где он может быть открыт.",
                    "Если файл лежит в облачной папке, дождитесь окончания синхронизации.",
                    "После этого попробуйте ещё раз."
                });
        }

        if (text.Contains("password") || text.Contains("encrypted") || text.Contains("шифр"))
        {
            return (
                "Файл защищён паролем или шифрованием.",
                "Пока на документе стоит защита, программа не может его изменить.",
                new[]
                {
                    "Откройте файл в той программе, в которой он был создан, и снимите защиту.",
                    "Сохраните обычную копию файла и повторите очистку."
                });
        }

        if (extension is ".docx" or ".xlsx" && (text.Contains("package") || text.Contains("xml") || text.Contains("openxml")))
        {
            return (
                "Документ Office повреждён или сохранён в необычном виде.",
                "Программа не смогла нормально открыть внутреннюю структуру файла.",
                new[]
                {
                    "Откройте файл в Word или Excel и сохраните его заново.",
                    "После этого повторите очистку."
                });
        }

        if (extension == ".pdf" && (text.Contains("pdf") || text.Contains("xref") || text.Contains("trailer")))
        {
            return (
                "PDF повреждён или устроен нестандартно.",
                "Программа не смогла безопасно пересобрать этот PDF-файл.",
                new[]
                {
                    "Откройте PDF в просмотрщике или редакторе и сохраните его как новый файл.",
                    "После этого повторите очистку."
                });
        }

        return (
            "Не удалось закончить обработку файла.",
            "Во время очистки произошла ошибка, из-за которой программа остановилась.",
            new[]
            {
                "Закройте файл во всех программах и попробуйте ещё раз.",
                "Если не помогло, пересохраните файл и снова запустите очистку."
            });
    }
}
