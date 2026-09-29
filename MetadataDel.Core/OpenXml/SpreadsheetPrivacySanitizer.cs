using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using MetadataDel.Core.Cleaning;

namespace MetadataDel.Core.OpenXml;

#pragma warning disable OOXML0001

internal static class SpreadsheetPrivacySanitizer
{
    private static readonly HashSet<string> WorkbookPrivacyElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "absPath", "fileVersion", "fileSharing", "externalReferences", "smartTagPr", "smartTagTypes", "webPublishObjects",
        "revisionPtr"
    };

    // Имя пользователя и дата последнего обновления сводной таблицы.
    private static readonly HashSet<string> PivotCachePrivacyAttributes = new(StringComparer.Ordinal)
    {
        "refreshedBy", "refreshedDate", "refreshedDateIso"
    };

    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private static readonly HashSet<string> WorksheetPrivacyElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "legacyDrawing", "legacyDrawingHF"
    };

    public static void Sanitize(SpreadsheetDocument document, OpenXmlSanitizeContext context)
    {
        var workbookPart = document.WorkbookPart;
        if (workbookPart == null)
        {
            OpenXmlPackageSanitizer.Sanitize(document, context);
            return;
        }

        var keepExternalLinks = false;
        var workbookXml = OpenXmlPartXml.TryLoad(workbookPart);
        var externalNames = workbookXml?.Descendants()
            .Where(e => e.Name.LocalName == "definedName" && IsExternalReference(e.Value))
            .Select(e => (string?)e.Attribute("name")).OfType<string>().ToList() ?? new List<string>();
        foreach (var worksheet in workbookPart.WorksheetParts)
        {
            var xml = OpenXmlPartXml.TryLoad(worksheet);
            if (xml?.Descendants().Any(e => e.Name.LocalName == "f" &&
                (IsExternalReference(e.Value) || externalNames.Any(name =>
                    System.Text.RegularExpressions.Regex.IsMatch(e.Value, @"(?<![\w.])" + System.Text.RegularExpressions.Regex.Escape(name) + @"(?![\w.])")))) == true ||
                worksheet.QueryTableParts.Any())
            {
                if (context.Depth == 0)
                    throw new NotSupportedException("Книга содержит формулы с внешними ссылками или таблицы запросов. Преобразуйте их в значения перед очисткой; исходный файл сохранён.");
                // Во встроенной книге (данные диаграммы) формулы не проверить в Excel; ссылки оставляем рабочими.
                context.Warn("Встроенная книга содержит внешние ссылки или таблицы запросов; пути к внешним источникам сохранены.");
                keepExternalLinks = true;
            }
        }

        // Сводные таблицы на внешних подключениях перестанут открываться, если удалить connections.xml.
        if (workbookPart.ConnectionsPart != null && workbookPart.PivotTableCacheDefinitionParts.Any(cache =>
                OpenXmlPartXml.TryLoad(cache)?.Descendants().Any(e => e.Name.LocalName == "cacheSource" && e.Attribute("connectionId") != null) == true))
        {
            if (context.Depth == 0)
                throw new NotSupportedException("Книга содержит сводные таблицы на внешних подключениях. Преобразуйте их в значения перед очисткой; исходный файл сохранён.");
            context.Warn("Встроенная книга содержит внешние подключения; сведения о подключениях сохранены.");
            keepExternalLinks = true;
        }

        foreach (var customXmlPart in workbookPart.CustomXmlParts.ToList())
        {
            workbookPart.DeletePart(customXmlPart);
        }

        if (workbookPart.ConnectionsPart != null && !keepExternalLinks)
        {
            workbookPart.DeletePart(workbookPart.ConnectionsPart);
        }

        foreach (var externalWorkbookPart in keepExternalLinks ? new List<ExternalWorkbookPart>() : workbookPart.ExternalWorkbookParts.ToList())
        {
            workbookPart.DeletePart(externalWorkbookPart);
        }

        // Журнал изменений общей книги: имена пользователей, даты и прежние значения ячеек.
        if (workbookPart.WorkbookRevisionHeaderPart != null)
        {
            workbookPart.DeletePart(workbookPart.WorkbookRevisionHeaderPart);
        }

        if (workbookPart.WorkbookUserDataPart != null)
        {
            workbookPart.DeletePart(workbookPart.WorkbookUserDataPart);
        }

        foreach (var cache in workbookPart.PivotTableCacheDefinitionParts)
        {
            // Кэш записей хранит копию исходных данных, даже удалённых из книги. Excel сохраняет так же
            // при выключенном «Сохранять исходные данные вместе с файлом».
            var recordsRelationshipId = cache.PivotTableCacheRecordsPart == null ? null : cache.GetIdOfPart(cache.PivotTableCacheRecordsPart);
            if (cache.PivotTableCacheRecordsPart != null)
            {
                cache.DeletePart(cache.PivotTableCacheRecordsPart);
            }
            OpenXmlPartXml.Update(cache, xml => SanitizePivotCacheXml(xml, recordsRelationshipId));
        }

        foreach (var workbookPersonPart in workbookPart.WorkbookPersonParts.ToList())
        {
            workbookPart.DeletePart(workbookPersonPart);
        }

        OpenXmlPartXml.Update(workbookPart, xml => SanitizeWorkbookXml(xml, keepExternalLinks));
        SpreadsheetHiddenContent.RemoveUnreferencedHiddenSheets(workbookPart, context);
        if (SpreadsheetHiddenContent.FindDetachedPivotCaches(workbookPart, OpenXmlPartXml.TryLoad(workbookPart)).Count > 0)
        {
            context.Warn("Сводная таблица хранит копию исходных данных, которых нет в книге. Чтобы убрать эти данные, замените сводную таблицу значениями.");
        }

        foreach (var chartsheetPart in workbookPart.ChartsheetParts)
        {
            var removedPrinterIds = DeletePrinterSettings(chartsheetPart, chartsheetPart.SpreadsheetPrinterSettingsParts);
            if (removedPrinterIds.Count > 0)
                OpenXmlPartXml.Update(chartsheetPart, xml => SanitizeWorksheetXml(xml, new HashSet<string>(), removedPrinterIds));
        }

        foreach (var worksheetPart in workbookPart.WorksheetParts)
        {
            var removedPrinterIds = DeletePrinterSettings(worksheetPart, worksheetPart.SpreadsheetPrinterSettingsParts);
            var hadCommentArtifacts = worksheetPart.WorksheetCommentsPart != null || worksheetPart.WorksheetThreadedCommentsParts.Any();

            if (worksheetPart.WorksheetCommentsPart != null)
            {
                worksheetPart.DeletePart(worksheetPart.WorksheetCommentsPart);
            }

            foreach (var threadedCommentsPart in worksheetPart.WorksheetThreadedCommentsParts.ToList())
            {
                worksheetPart.DeletePart(threadedCommentsPart);
            }

            var removedDrawingIds = new HashSet<string>();
            if (hadCommentArtifacts)
            {
                foreach (var relationship in worksheetPart.Parts
                             .Where(p => p.OpenXmlPart.Uri.ToString().EndsWith(".vml", StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    var drawing = OpenXmlPartXml.TryLoad(relationship.OpenXmlPart);
                    if (drawing == null) continue;
                    var notes = drawing.Descendants().Where(e => e.Name.LocalName == "shape" &&
                        e.Descendants().Any(c => c.Name.LocalName == "ClientData" && (string?)c.Attribute("ObjectType") == "Note")).ToList();
                    if (notes.Count == 0) continue;
                    OpenXmlPartXml.Update(relationship.OpenXmlPart, xml =>
                    {
                        foreach (var note in xml.Descendants().Where(e => e.Name.LocalName == "shape" &&
                            e.Descendants().Any(c => c.Name.LocalName == "ClientData" && (string?)c.Attribute("ObjectType") == "Note")).ToList()) note.Remove();
                        return true;
                    });
                    if (!OpenXmlPartXml.TryLoad(relationship.OpenXmlPart)!.Descendants().Any(e => e.Name.LocalName == "shape"))
                    {
                        removedDrawingIds.Add(relationship.RelationshipId);
                        worksheetPart.DeletePart(relationship.OpenXmlPart);
                    }
                }
            }
            OpenXmlPartXml.Update(worksheetPart, xml => SanitizeWorksheetXml(xml, removedDrawingIds, removedPrinterIds));
        }

        OpenXmlPackageSanitizer.Sanitize(document, context);
    }

    private static HashSet<string> DeletePrinterSettings(OpenXmlPart sheet, IEnumerable<SpreadsheetPrinterSettingsPart> printerSettings)
    {
        var removed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in printerSettings.ToList())
        {
            removed.Add(sheet.GetIdOfPart(part));
            sheet.DeletePart(part);
        }
        return removed;
    }

    private static bool SanitizePivotCacheXml(XDocument document, string? recordsRelationshipId)
    {
        var root = document.Root;
        if (root == null) return false;
        foreach (var attribute in root.Attributes().Where(a => PivotCachePrivacyAttributes.Contains(a.Name.LocalName)).ToList())
        {
            attribute.Remove();
        }
        root.Attributes(XName.Get("id", RelationshipsNamespace)).Where(a => a.Value == recordsRelationshipId).Remove();
        if (recordsRelationshipId != null)
        {
            root.SetAttributeValue("saveData", "0");
            root.Attribute("recordCount")?.Remove();
        }
        return true;
    }

    internal static bool HasResidualMetadata(SpreadsheetDocument document, IList<string> findings)
    {
        AuditPackageProperties(document.PackageProperties, findings);
        AuditExtendedProperties(document.ExtendedFilePropertiesPart?.Properties, findings);

        if (document.CustomFilePropertiesPart != null)
        {
            findings.Add("кастомные свойства документа");
        }

        var workbookPart = document.WorkbookPart;
        if (workbookPart == null)
        {
            return findings.Count > 0;
        }

        if (workbookPart.ConnectionsPart != null)
        {
            findings.Add("подключения Excel");
        }

        if (workbookPart.ExternalWorkbookParts.Any())
        {
            findings.Add("внешние ссылки Excel");
        }

        if (workbookPart.WorkbookPersonParts.Any())
        {
            findings.Add("авторы threaded comments");
        }

        if (workbookPart.CustomXmlParts.Any())
        {
            findings.Add("custom XML");
        }

        if (workbookPart.WorkbookRevisionHeaderPart != null || workbookPart.WorkbookUserDataPart != null)
        {
            findings.Add("журнал изменений общей книги");
        }

        if (workbookPart.PivotTableCacheDefinitionParts.Any(p => p.PivotTableCacheRecordsPart != null))
        {
            findings.Add("кэш исходных данных сводных таблиц");
        }

        var workbookDocument = OpenXmlPartXml.TryLoad(workbookPart);
        if (workbookDocument != null && SpreadsheetHiddenContent.FindHiddenSheets(workbookDocument).Count > 0)
        {
            findings.Add("скрытые листы");
        }

        if (SpreadsheetHiddenContent.FindDetachedPivotCaches(workbookPart, workbookDocument).Count > 0)
        {
            findings.Add("кэш сводной таблицы с данными, которых нет в книге");
        }

        if (workbookPart.WorksheetParts.Any(p => p.SpreadsheetPrinterSettingsParts.Any()) ||
            workbookPart.ChartsheetParts.Any(p => p.SpreadsheetPrinterSettingsParts.Any()))
        {
            findings.Add("настройки принтера");
        }

        var workbookXml = OpenXmlPartXml.TryLoad(workbookPart);
        if (workbookXml != null)
        {
            if (workbookXml.Descendants().Any(e => WorkbookPrivacyElements.Contains(e.Name.LocalName)))
            {
                findings.Add("служебные метаданные книги");
            }

            if (workbookXml.Descendants().Any(e => e.Name.LocalName == "definedName" &&
                                                  IsExternalReference(e.Value)))
            {
                findings.Add("внешние defined names");
            }
        }

        if (workbookPart.WorksheetParts.Any(p => p.WorksheetCommentsPart != null || p.WorksheetThreadedCommentsParts.Any()))
        {
            findings.Add("комментарии Excel");
        }

        return findings.Count > 0;
    }

    private static bool SanitizeWorkbookXml(XDocument document, bool keepExternalLinks)
    {
        var changed = false;

        foreach (var element in document.Descendants().Where(e => WorkbookPrivacyElements.Contains(e.Name.LocalName) &&
                     !(keepExternalLinks && e.Name.LocalName == "externalReferences")).ToList())
        {
            element.Remove();
            changed = true;
        }

        foreach (var definedName in document.Descendants().Where(e => !keepExternalLinks && e.Name.LocalName == "definedName" &&
                                                                      IsExternalReference(e.Value)).ToList())
        {
            definedName.Remove();
            changed = true;
        }

        // absPath и revisionPtr лежат внутри mc:AlternateContent; пустые обёртки удаляем вместе с extLst-записями.
        foreach (var wrapper in document.Descendants().Where(e => e.Name.LocalName is "AlternateContent" or "ext" &&
                     !e.Descendants().Any(d => d.Name.LocalName is not ("Choice" or "Fallback"))).ToList())
        {
            wrapper.Remove();
            changed = true;
        }

        foreach (var extensions in document.Descendants().Where(e => e.Name.LocalName == "extLst" && !e.HasElements).ToList())
        {
            extensions.Remove();
            changed = true;
        }

        return changed;
    }

    private static bool SanitizeWorksheetXml(XDocument document, HashSet<string> removedDrawingIds, HashSet<string> removedPrinterIds)
    {
        var changed = false;

        foreach (var attribute in document.Descendants().Where(e => e.Name.LocalName == "pageSetup")
                     .Attributes(XName.Get("id", RelationshipsNamespace)).Where(a => removedPrinterIds.Contains(a.Value)).ToList())
        {
            attribute.Remove();
            changed = true;
        }

        foreach (var element in document.Descendants().Where(e => WorksheetPrivacyElements.Contains(e.Name.LocalName) &&
            e.Attributes().Any(a => a.Name.LocalName == "id" && removedDrawingIds.Contains(a.Value))).ToList())
        {
            element.Remove();
            changed = true;
        }

        return changed;
    }

    private static void AuditPackageProperties(DocumentFormat.OpenXml.Packaging.IPackageProperties properties, IList<string> findings)
    {
        if (HasText(properties.Creator) || HasText(properties.LastModifiedBy) || HasText(properties.Title) ||
            HasText(properties.Subject) || HasText(properties.Keywords) || HasText(properties.Description) ||
            HasText(properties.Category) || HasText(properties.ContentStatus) ||
            HasText(properties.Identifier) || HasText(properties.Version) || HasText(properties.Language))
        {
            findings.Add("core properties");
        }

        if (properties.Created != null || properties.Modified != null || properties.LastPrinted != null || HasText(properties.Revision))
        {
            findings.Add("временные и ревизионные поля");
        }
    }

    private static void AuditExtendedProperties(DocumentFormat.OpenXml.ExtendedProperties.Properties? properties, IList<string> findings)
    {
        if (properties == null)
        {
            return;
        }

        if (HasText(properties.Company?.Text) || HasText(properties.Manager?.Text) || HasText(properties.Application?.Text) ||
            HasText(properties.HyperlinkBase?.Text) ||
            properties.Template != null || properties.ApplicationVersion != null ||
            properties.HeadingPairs != null || properties.TitlesOfParts != null)
        {
            findings.Add("расширенные свойства");
        }
    }

    private static bool IsExternalReference(string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(value, @"\[[^\]]+\][^!]*!", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
        || value.Contains("://", StringComparison.Ordinal);

    private static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);
}

#pragma warning restore OOXML0001
