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
        "del", "delText", "moveFrom", "moveFromRangeStart", "moveFromRangeEnd",
        "moveToRangeStart", "moveToRangeEnd", "commentRangeStart", "commentRangeEnd",
        "commentReference", "customXmlDelRangeStart", "customXmlDelRangeEnd",
        "customXmlInsRangeStart", "customXmlInsRangeEnd", "customXmlMoveFromRangeStart",
        "customXmlMoveFromRangeEnd", "customXmlMoveToRangeStart", "customXmlMoveToRangeEnd"
    };

    private static readonly HashSet<string> SettingsPrivacyElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "trackRevisions", "rsids", "attachedTemplate", "docVars", "mailMerge",
        "removePersonalInformation", "removeDateAndTime", "writeReservation"
    };

    private static readonly HashSet<string> ContentControlMetadataElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "alias", "tag", "dataBinding", "placeholder", "temporary", "showingPlcHdr"
    };

    public static void Sanitize(WordprocessingDocument document)
    {
        CleanPackageProperties(document.PackageProperties);
        CleanExtendedProperties(document.ExtendedFilePropertiesPart?.Properties);

        if (document.CustomFilePropertiesPart != null)
        {
            document.DeletePart(document.CustomFilePropertiesPart);
        }

        var mainPart = document.MainDocumentPart;
        if (mainPart == null)
        {
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

        if (mainPart.DocumentSettingsPart?.MailMergeRecipientDataPart != null)
        {
            mainPart.DocumentSettingsPart.DeletePart(mainPart.DocumentSettingsPart.MailMergeRecipientDataPart);
        }

        if (mainPart.DocumentSettingsPart != null)
        {
            OpenXmlPartXml.Update(mainPart.DocumentSettingsPart, SanitizeSettingsXml);
        }

        foreach (var part in EnumerateSanitizedParts(mainPart))
        {
            OpenXmlPartXml.Update(part, SanitizePartXml);
        }
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
            mainPart.WordprocessingPeoplePart != null)
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
    }

    private static bool SanitizePartXml(XDocument document)
    {
        var changed = false;

        foreach (var attribute in EnumerateElementsAndSelf(document).Attributes().Where(IsPrivacyAttribute).ToList())
        {
            attribute.Remove();
            changed = true;
        }

        foreach (var element in document.Descendants().Where(e => ContentControlMetadataElements.Contains(e.Name.LocalName)).ToList())
        {
            element.Remove();
            changed = true;
        }

        foreach (var element in document.Descendants().Where(e => RevisionDeletions.Contains(e.Name.LocalName)).ToList())
        {
            element.Remove();
            changed = true;
        }

        foreach (var element in document.Descendants().Where(e => RevisionWrappers.Contains(e.Name.LocalName)).ToList())
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
        try { properties.Pages?.Remove(); } catch { }
        try { properties.Words?.Remove(); } catch { }
        try { properties.Characters?.Remove(); } catch { }
        try { properties.Lines?.Remove(); } catch { }
        try { properties.Paragraphs?.Remove(); } catch { }
        try { properties.CharactersWithSpaces?.Remove(); } catch { }
        try { properties.DocumentSecurity?.Remove(); } catch { }
        try { properties.ScaleCrop?.Remove(); } catch { }
        try { properties.SharedDocument?.Remove(); } catch { }
        try { properties.HyperlinksChanged?.Remove(); } catch { }
    }

    private static void AuditPackageProperties(DocumentFormat.OpenXml.Packaging.IPackageProperties properties, IList<string> findings)
    {
        if (HasText(properties.Creator) || HasText(properties.LastModifiedBy) || HasText(properties.Title) ||
            HasText(properties.Subject) || HasText(properties.Keywords) || HasText(properties.Description) ||
            HasText(properties.Category) || HasText(properties.ContentStatus))
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
