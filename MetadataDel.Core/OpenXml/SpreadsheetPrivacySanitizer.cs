using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using MetadataDel.Core.Cleaning;

namespace MetadataDel.Core.OpenXml;

#pragma warning disable OOXML0001

internal static class SpreadsheetPrivacySanitizer
{
    private static readonly HashSet<string> WorkbookPrivacyElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "absPath", "fileVersion", "fileSharing", "externalReferences", "smartTagPr", "smartTagTypes", "webPublishObjects"
    };

    private static readonly HashSet<string> WorksheetPrivacyElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "legacyDrawing", "legacyDrawingHF"
    };

    public static void Sanitize(SpreadsheetDocument document)
    {
        CleanPackageProperties(document.PackageProperties);
        CleanExtendedProperties(document.ExtendedFilePropertiesPart?.Properties);

        if (document.CustomFilePropertiesPart != null)
        {
            document.DeletePart(document.CustomFilePropertiesPart);
        }

        var workbookPart = document.WorkbookPart;
        if (workbookPart == null)
        {
            return;
        }

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
                throw new NotSupportedException("Книга содержит формулы с внешними ссылками или таблицы запросов. Преобразуйте их в значения перед очисткой; исходный файл сохранён.");
        }

        foreach (var customXmlPart in workbookPart.CustomXmlParts.ToList())
        {
            workbookPart.DeletePart(customXmlPart);
        }

        if (workbookPart.ConnectionsPart != null)
        {
            workbookPart.DeletePart(workbookPart.ConnectionsPart);
        }

        foreach (var externalWorkbookPart in workbookPart.ExternalWorkbookParts.ToList())
        {
            workbookPart.DeletePart(externalWorkbookPart);
        }

        foreach (var workbookPersonPart in workbookPart.WorkbookPersonParts.ToList())
        {
            workbookPart.DeletePart(workbookPersonPart);
        }

        OpenXmlPartXml.Update(workbookPart, SanitizeWorkbookXml);

        foreach (var worksheetPart in workbookPart.WorksheetParts)
        {
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
            OpenXmlPartXml.Update(worksheetPart, xml => SanitizeWorksheetXml(xml, removedDrawingIds));
        }
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

    private static bool SanitizeWorkbookXml(XDocument document)
    {
        var changed = false;

        foreach (var element in document.Descendants().Where(e => WorkbookPrivacyElements.Contains(e.Name.LocalName)).ToList())
        {
            element.Remove();
            changed = true;
        }

        foreach (var definedName in document.Descendants().Where(e => e.Name.LocalName == "definedName" &&
                                                                      IsExternalReference(e.Value)).ToList())
        {
            definedName.Remove();
            changed = true;
        }

        return changed;
    }

    private static bool SanitizeWorksheetXml(XDocument document, HashSet<string> removedDrawingIds)
    {
        var changed = false;

        foreach (var element in document.Descendants().Where(e => WorksheetPrivacyElements.Contains(e.Name.LocalName) &&
            e.Attributes().Any(a => a.Name.LocalName == "id" && removedDrawingIds.Contains(a.Value))).ToList())
        {
            element.Remove();
            changed = true;
        }

        return changed;
    }

    private static void CleanPackageProperties(DocumentFormat.OpenXml.Packaging.IPackageProperties properties)
    {
        properties.Creator = null;
        properties.LastModifiedBy = null;
        properties.Title = null;
        properties.Subject = null;
        properties.Keywords = null;
        properties.Description = null;
        properties.Category = null;
        properties.ContentStatus = null;
        properties.Identifier = null;
        properties.Version = null;
        properties.Language = null;

        try { properties.Created = null; } catch { }
        try { properties.Modified = null; } catch { }
        try { properties.LastPrinted = null; } catch { }
        try { properties.Revision = null; } catch { }
    }

    private static void CleanExtendedProperties(DocumentFormat.OpenXml.ExtendedProperties.Properties? properties)
    {
        if (properties == null)
        {
            return;
        }

        try { properties.Company?.Remove(); } catch { }
        try { properties.Manager?.Remove(); } catch { }
        try { properties.Application?.Remove(); } catch { }
        try { properties.HyperlinkBase?.Remove(); } catch { }
        try { properties.TotalTime?.Remove(); } catch { }
        try { properties.LinksUpToDate?.Remove(); } catch { }
        try { properties.Template?.Remove(); } catch { }
        try { properties.ApplicationVersion?.Remove(); } catch { }
        try { properties.HeadingPairs?.Remove(); } catch { }
        try { properties.TitlesOfParts?.Remove(); } catch { }
        try { properties.DocumentSecurity?.Remove(); } catch { }
        try { properties.ScaleCrop?.Remove(); } catch { }
        try { properties.SharedDocument?.Remove(); } catch { }
        try { properties.HyperlinksChanged?.Remove(); } catch { }
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
