using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using MetadataDel.Core.Cleaning;

namespace MetadataDel.Core.OpenXml;

#pragma warning disable OOXML0001

internal static class WordPrivacySanitizer
{
    private static readonly HashSet<string> RevisionWrappers = new(StringComparer.OrdinalIgnoreCase)
    {
        "ins", "moveTo", "customXml", "smartTag"
    };

    private static readonly HashSet<string> RevisionDeletions = new(StringComparer.OrdinalIgnoreCase)
    {
        "rPrChange", "pPrChange", "sectPrChange", "tblPrChange", "tblGridChange", "trPrChange", "tcPrChange",
        "del", "delText", "moveFrom", "moveFromRangeStart", "moveFromRangeEnd",
        "moveToRangeStart", "moveToRangeEnd", "commentRangeStart", "commentRangeEnd",
        "printerSettings",
        "commentReference", "customXmlDelRangeStart", "customXmlDelRangeEnd",
        "customXmlInsRangeStart", "customXmlInsRangeEnd", "customXmlMoveFromRangeStart",
        "customXmlMoveFromRangeEnd", "customXmlMoveToRangeStart", "customXmlMoveToRangeEnd"
    };

    private static readonly HashSet<string> SettingsPrivacyElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "trackRevisions", "rsids", "attachedTemplate", "docVars", "mailMerge",
        "removePersonalInformation", "removeDateAndTime", "writeReservation", "docId", "saveThroughXslt"
    };

    private static readonly HashSet<string> ContentControlMetadataElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "alias", "tag", "dataBinding", "placeholder", "temporary", "showingPlcHdr"
    };

    public static void Sanitize(WordprocessingDocument document, OpenXmlSanitizeContext context)
    {
        var mainPart = document.MainDocumentPart;
        if (mainPart == null)
        {
            OpenXmlPackageSanitizer.Sanitize(document, context);
            return;
        }

        DeleteCustomXmlParts(mainPart);
        if (mainPart.WordprocessingCommentsPart != null)
        {
            mainPart.DeletePart(mainPart.WordprocessingCommentsPart);
        }

        if (mainPart.WordprocessingCommentsExPart != null)
        {
            mainPart.DeletePart(mainPart.WordprocessingCommentsExPart);
        }

        if (mainPart.WordprocessingPeoplePart != null)
        {
            mainPart.DeletePart(mainPart.WordprocessingPeoplePart);
        }

        if (mainPart.WordCommentsExtensiblePart != null)
        {
            mainPart.DeletePart(mainPart.WordCommentsExtensiblePart);
        }

        if (mainPart.WordprocessingCommentsIdsPart != null)
        {
            mainPart.DeletePart(mainPart.WordprocessingCommentsIdsPart);
        }

        foreach (var printerSettings in mainPart.WordprocessingPrinterSettingsParts.ToList())
        {
            mainPart.DeletePart(printerSettings);
        }

        if (mainPart.DocumentSettingsPart?.MailMergeRecipientDataPart != null)
        {
            mainPart.DocumentSettingsPart.DeletePart(mainPart.DocumentSettingsPart.MailMergeRecipientDataPart);
        }

        if (mainPart.DocumentSettingsPart != null)
        {
            OpenXmlPartXml.Update(mainPart.DocumentSettingsPart, SanitizeSettingsXml);

            // Ссылки на шаблон и источники слияния после удаления элементов не нужны, но хранят локальные пути.
            foreach (var relationship in mainPart.DocumentSettingsPart.ExternalRelationships.ToList())
            {
                mainPart.DocumentSettingsPart.DeleteExternalRelationship(relationship);
            }
        }

        foreach (var part in EnumerateSanitizedParts(mainPart))
        {
            OpenXmlPartXml.Update(part, SanitizePartXml);
        }

        var hiddenStyles = WordHiddenContent.LoadHiddenStyles(mainPart.StyleDefinitionsPart);
        var removedHiddenRuns = 0;
        foreach (var part in WordHiddenContent.EnumerateContentParts(mainPart))
        {
            OpenXmlPartXml.Update(part, xml =>
            {
                var removed = WordHiddenContent.RemoveHiddenRuns(xml, hiddenStyles, out var count);
                removedHiddenRuns += count;
                return WordHiddenContent.RewriteFieldPaths(xml) | removed;
            });
        }
        if (removedHiddenRuns > 0)
        {
            context.Warn("Удалён скрытый текст Word.");
        }

        OpenXmlPackageSanitizer.Sanitize(document, context);
    }

    internal static bool HasResidualMetadata(WordprocessingDocument document, IList<string> findings)
    {
        AuditPackageProperties(document.PackageProperties, findings);
        AuditExtendedProperties(document.ExtendedFilePropertiesPart?.Properties, findings);

        if (document.CustomFilePropertiesPart != null)
        {
            findings.Add("кастомные свойства документа");
        }

        var mainPart = document.MainDocumentPart;
        if (mainPart == null)
        {
            return findings.Count > 0;
        }

        if (mainPart.WordprocessingCommentsPart != null ||
            mainPart.WordprocessingCommentsExPart != null ||
            mainPart.WordprocessingPeoplePart != null ||
            mainPart.WordCommentsExtensiblePart != null ||
            mainPart.WordprocessingCommentsIdsPart != null)
        {
            findings.Add("комментарии или авторы комментариев");
        }

        if (mainPart.CustomXmlParts.Any())
        {
            findings.Add("custom XML");
        }

        if (mainPart.DocumentSettingsPart?.MailMergeRecipientDataPart != null)
        {
            findings.Add("данные mail merge");
        }

        if (mainPart.DocumentSettingsPart?.ExternalRelationships.Any() == true)
        {
            findings.Add("ссылка на шаблон или источник слияния");
        }

        if (mainPart.WordprocessingPrinterSettingsParts.Any())
        {
            findings.Add("настройки принтера");
        }

        var settingsXml = mainPart.DocumentSettingsPart is null ? null : OpenXmlPartXml.TryLoad(mainPart.DocumentSettingsPart);
        if (settingsXml != null &&
            settingsXml.Descendants().Any(e => SettingsPrivacyElements.Contains(e.Name.LocalName)))
        {
            findings.Add("служебные настройки Word");
        }

        foreach (var part in EnumerateSanitizedParts(mainPart))
        {
            var documentXml = OpenXmlPartXml.TryLoad(part);
            if (documentXml == null)
            {
                continue;
            }

            if (documentXml.Descendants().Any(e => RevisionWrappers.Contains(e.Name.LocalName) || RevisionDeletions.Contains(e.Name.LocalName)))
            {
                findings.Add("следы правок или комментариев");
                break;
            }
        }

        var hiddenStyles = WordHiddenContent.LoadHiddenStyles(mainPart.StyleDefinitionsPart);
        foreach (var part in WordHiddenContent.EnumerateContentParts(mainPart))
        {
            var documentXml = OpenXmlPartXml.TryLoad(part);
            if (documentXml == null)
            {
                continue;
            }

            if (WordHiddenContent.FindHiddenRuns(documentXml, hiddenStyles).Count > 0)
            {
                findings.Add("скрытый текст");
            }

            if (WordHiddenContent.HasLocalFieldPaths(documentXml))
            {
                findings.Add("локальные пути в кодах полей");
            }
        }

        foreach (var part in EnumerateSanitizedParts(mainPart))
        {
            var documentXml = OpenXmlPartXml.TryLoad(part);
            if (documentXml == null)
            {
                continue;
            }

            if (EnumerateElementsAndSelf(documentXml).Attributes().Any(IsPrivacyAttribute))
            {
                findings.Add("атрибуты редактора/сессии");
                break;
            }
        }

        return findings.Count > 0;
    }

    private static IEnumerable<OpenXmlPart> EnumerateSanitizedParts(MainDocumentPart mainPart)
    {
        yield return mainPart;

        foreach (var headerPart in mainPart.HeaderParts)
        {
            yield return headerPart;
        }

        foreach (var footerPart in mainPart.FooterParts)
        {
            yield return footerPart;
        }

        if (mainPart.FootnotesPart != null)
        {
            yield return mainPart.FootnotesPart;
        }

        if (mainPart.EndnotesPart != null)
        {
            yield return mainPart.EndnotesPart;
        }

        if (mainPart.StyleDefinitionsPart != null)
        {
            yield return mainPart.StyleDefinitionsPart;
        }

        if (mainPart.StylesWithEffectsPart != null)
        {
            yield return mainPart.StylesWithEffectsPart;
        }

        if (mainPart.NumberingDefinitionsPart != null)
        {
            yield return mainPart.NumberingDefinitionsPart;
        }

        var glossary = mainPart.GlossaryDocumentPart;
        if (glossary != null)
        {
            yield return glossary;
            if (glossary.StyleDefinitionsPart != null) yield return glossary.StyleDefinitionsPart;
            if (glossary.NumberingDefinitionsPart != null) yield return glossary.NumberingDefinitionsPart;
        }
    }

    private static bool SanitizePartXml(XDocument document)
    {
        var changed = false;

        foreach (var attribute in EnumerateElementsAndSelf(document).Attributes().Where(IsPrivacyAttribute).ToList())
        {
            attribute.Remove();
            changed = true;
        }

        foreach (var element in document.Descendants().Where(e => e.Parent?.Name.LocalName == "sdtPr" && ContentControlMetadataElements.Contains(e.Name.LocalName)).ToList())
        {
            element.Remove();
            changed = true;
        }

        // Исключения из защиты от редактирования, выданные конкретным пользователям, содержат их учётные записи.
        var personalPermissions = document.Descendants()
            .Where(e => e.Name.LocalName == "permStart" && e.Attributes().Any(a => a.Name.LocalName == "ed"))
            .ToList();
        var personalPermissionIds = personalPermissions
            .Select(e => e.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value)
            .OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var element in personalPermissions.Concat(document.Descendants().Where(e => e.Name.LocalName == "permEnd" &&
                     personalPermissionIds.Contains(e.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value ?? "")).ToList()))
        {
            element.Remove();
            changed = true;
        }

        foreach (var element in document.Descendants().Where(e => RevisionDeletions.Contains(e.Name.LocalName)).ToList())
        {
            element.Remove();
            changed = true;
        }

        foreach (var element in document.Descendants().Where(e => RevisionWrappers.Contains(e.Name.LocalName)).Reverse().ToList())
        {
            element.ReplaceWith(element.Nodes());
            changed = true;
        }

        return changed;
    }

    private static bool SanitizeSettingsXml(XDocument document)
    {
        var changed = false;

        foreach (var attribute in EnumerateElementsAndSelf(document).Attributes().Where(IsPrivacyAttribute).ToList())
        {
            attribute.Remove();
            changed = true;
        }

        foreach (var element in document.Descendants().Where(e => SettingsPrivacyElements.Contains(e.Name.LocalName) || ContentControlMetadataElements.Contains(e.Name.LocalName)).ToList())
        {
            element.Remove();
            changed = true;
        }

        return changed;
    }

    private static bool IsPrivacyAttribute(XAttribute attribute) =>
        attribute.Name.LocalName.StartsWith("rsid", StringComparison.OrdinalIgnoreCase) ||
        attribute.Name.LocalName is "author" or "date" or "initials" or "paraId" or "textId";

    private static void DeleteCustomXmlParts(MainDocumentPart mainPart)
    {
        foreach (var part in mainPart.CustomXmlParts.ToList())
        {
            mainPart.DeletePart(part);
        }
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

    private static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);

    private static IEnumerable<XElement> EnumerateElementsAndSelf(XDocument document)
    {
        if (document.Root == null)
        {
            return Array.Empty<XElement>();
        }

        return document.Root.DescendantsAndSelf();
    }
}

#pragma warning restore OOXML0001
