using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using MetadataDel.Core.Cleaning;

namespace MetadataDel.Core.OpenXml;

#pragma warning disable OOXML0001

internal static class WordPrivacySanitizer
{
    private static readonly HashSet<string> RevisionWrappers = new(StringComparer.OrdinalIgnoreCase)
    {
        "ins", "moveTo", "customXml", "smartTag", "conflictIns"
    };

    // Свойства обёрток customXml/smartTag допустимы только внутри самой обёртки; после её снятия они нарушили бы схему.
    private static readonly HashSet<string> WrapperProperties = new(StringComparer.Ordinal)
    {
        "customXmlPr", "smartTagPr"
    };

    private const string Bibliography = "http://schemas.openxmlformats.org/officeDocument/2006/bibliography";

    private static readonly HashSet<string> RevisionDeletions = new(StringComparer.OrdinalIgnoreCase)
    {
        "rPrChange", "pPrChange", "sectPrChange", "tblPrChange", "tblGridChange", "trPrChange", "tcPrChange",
        "del", "delText", "moveFrom", "moveFromRangeStart", "moveFromRangeEnd",
        "moveToRangeStart", "moveToRangeEnd", "commentRangeStart", "commentRangeEnd",
        "printerSettings",
        "commentReference", "customXmlDelRangeStart", "customXmlDelRangeEnd",
        "customXmlInsRangeStart", "customXmlInsRangeEnd", "customXmlMoveFromRangeStart",
        "customXmlMoveFromRangeEnd", "customXmlMoveToRangeStart", "customXmlMoveToRangeEnd",
        // Метки правок таблиц и нумерации: обязательные author/date удаляются, поэтому оставлять сами метки нельзя.
        "cellIns", "numberingChange", "tblPrExChange", "conflictDel"
    };

    private static readonly HashSet<string> SettingsPrivacyElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "trackRevisions", "rsids", "attachedTemplate", "docVars", "mailMerge",
        "removePersonalInformation", "removeDateAndTime", "writeReservation", "docId", "saveThroughXslt"
    };

    // showingPlcHdr и placeholder не удаляются: без них текст-заполнитель («Щёлкните здесь…») стал бы содержимым документа.
    private static readonly HashSet<string> ContentControlMetadataElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "alias", "tag", "dataBinding", "temporary"
    };

    public static void Sanitize(WordprocessingDocument document, OpenXmlSanitizeContext context)
    {
        var mainPart = document.MainDocumentPart;
        if (mainPart == null)
        {
            OpenXmlPackageSanitizer.Sanitize(document, context);
            return;
        }

        // Глоссарий (стандартные блоки) — отдельный документ со своими настройками, колонтитулами и сносками.
        foreach (var scope in EnumerateDocumentScopes(mainPart))
        {
            SanitizeDocumentScope(scope, context);
        }

        OpenXmlPackageSanitizer.Sanitize(document, context);
    }

    private static void SanitizeDocumentScope(OpenXmlPart scope, OpenXmlSanitizeContext context)
    {
        DeleteCustomXmlParts(scope, context);
        foreach (var part in scope.GetPartsOfType<WordprocessingCommentsPart>().Cast<OpenXmlPart>()
                     .Concat(scope.GetPartsOfType<WordprocessingCommentsExPart>())
                     .Concat(scope.GetPartsOfType<WordprocessingPeoplePart>())
                     .Concat(scope.GetPartsOfType<WordCommentsExtensiblePart>())
                     .Concat(scope.GetPartsOfType<WordprocessingCommentsIdsPart>())
                     .Concat(scope.GetPartsOfType<WordprocessingPrinterSettingsPart>())
                     .ToList())
        {
            scope.DeletePart(part);
        }

        foreach (var settings in scope.GetPartsOfType<DocumentSettingsPart>().ToList())
        {
            if (settings.MailMergeRecipientDataPart != null)
            {
                settings.DeletePart(settings.MailMergeRecipientDataPart);
            }

            OpenXmlPartXml.Update(settings, SanitizeSettingsXml);

            // Ссылки на шаблон и источники слияния после удаления элементов не нужны, но хранят локальные пути.
            foreach (var relationship in settings.ExternalRelationships.ToList())
            {
                settings.DeleteExternalRelationship(relationship);
            }
        }

        foreach (var part in EnumerateSanitizedParts(scope))
        {
            OpenXmlPartXml.Update(part, SanitizePartXml);
        }

        var hiddenStyles = WordHiddenContent.LoadStyleVisibility(scope.GetPartsOfType<StyleDefinitionsPart>().FirstOrDefault());
        var removedHiddenRuns = 0;
        foreach (var part in WordHiddenContent.EnumerateContentParts(scope).ToList())
        {
            var removedInPart = 0;
            OpenXmlPartXml.Update(part, xml =>
            {
                var removed = WordHiddenContent.RemoveHiddenRuns(xml, hiddenStyles, out removedInPart);
                return WordHiddenContent.RewriteFieldPaths(xml) | removed;
            });
            removedHiddenRuns += removedInPart;
            if (removedInPart > 0)
            {
                // Картинки и объекты удалённого скрытого текста не должны оставаться в пакете.
                WordHiddenContent.DeleteUnreferencedMedia(part);
            }
        }
        if (removedHiddenRuns > 0)
        {
            context.Warn("Удалён скрытый текст Word.");
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

        foreach (var scope in EnumerateDocumentScopes(mainPart))
        {
            AuditDocumentScope(scope, findings);
        }

        return findings.Count > 0;
    }

    private static void AuditDocumentScope(OpenXmlPart scope, IList<string> findings)
    {
        void Add(string finding)
        {
            if (!findings.Contains(finding)) findings.Add(finding);
        }

        if (scope.GetPartsOfType<WordprocessingCommentsPart>().Any() ||
            scope.GetPartsOfType<WordprocessingCommentsExPart>().Any() ||
            scope.GetPartsOfType<WordprocessingPeoplePart>().Any() ||
            scope.GetPartsOfType<WordCommentsExtensiblePart>().Any() ||
            scope.GetPartsOfType<WordprocessingCommentsIdsPart>().Any())
        {
            Add("комментарии или авторы комментариев");
        }

        if (scope.GetPartsOfType<CustomXmlPart>().Any())
        {
            Add("custom XML");
        }

        foreach (var settings in scope.GetPartsOfType<DocumentSettingsPart>())
        {
            if (settings.MailMergeRecipientDataPart != null)
            {
                Add("данные mail merge");
            }

            if (settings.ExternalRelationships.Any())
            {
                Add("ссылка на шаблон или источник слияния");
            }

            var settingsXml = OpenXmlPartXml.TryLoad(settings);
            if (settingsXml != null &&
                settingsXml.Descendants().Any(e => SettingsPrivacyElements.Contains(e.Name.LocalName)))
            {
                Add("служебные настройки Word");
            }
        }

        if (scope.GetPartsOfType<WordprocessingPrinterSettingsPart>().Any())
        {
            Add("настройки принтера");
        }

        foreach (var part in EnumerateSanitizedParts(scope))
        {
            var documentXml = OpenXmlPartXml.TryLoad(part);
            if (documentXml == null)
            {
                continue;
            }

            if (documentXml.Descendants().Any(e => RevisionWrappers.Contains(e.Name.LocalName) || RevisionDeletions.Contains(e.Name.LocalName) ||
                                                   e.Name.LocalName is "cellDel" or "cellMerge"))
            {
                Add("следы правок или комментариев");
            }

            if (EnumerateElementsAndSelf(documentXml).Attributes().Any(IsPrivacyAttribute) ||
                documentXml.Descendants().Any(IsRsidElement))
            {
                Add("атрибуты редактора/сессии");
            }
        }

        var hiddenStyles = WordHiddenContent.LoadStyleVisibility(scope.GetPartsOfType<StyleDefinitionsPart>().FirstOrDefault());
        foreach (var part in WordHiddenContent.EnumerateContentParts(scope))
        {
            var documentXml = OpenXmlPartXml.TryLoad(part);
            if (documentXml == null)
            {
                continue;
            }

            if (WordHiddenContent.FindHiddenRuns(documentXml, hiddenStyles).Count > 0)
            {
                Add("скрытый текст");
            }

            if (WordHiddenContent.HasLocalFieldPaths(documentXml))
            {
                Add("локальные пути в кодах полей");
            }
        }
    }

    /// <summary>Основной документ и, если есть, документ глоссария (стандартные блоки).</summary>
    private static IEnumerable<OpenXmlPart> EnumerateDocumentScopes(MainDocumentPart mainPart)
    {
        yield return mainPart;
        if (mainPart.GlossaryDocumentPart != null)
        {
            yield return mainPart.GlossaryDocumentPart;
        }
    }

    private static IEnumerable<OpenXmlPart> EnumerateSanitizedParts(OpenXmlPart scope)
    {
        yield return scope;

        foreach (var part in scope.GetPartsOfType<HeaderPart>()) yield return part;
        foreach (var part in scope.GetPartsOfType<FooterPart>()) yield return part;
        foreach (var part in scope.GetPartsOfType<FootnotesPart>()) yield return part;
        foreach (var part in scope.GetPartsOfType<EndnotesPart>()) yield return part;
        foreach (var part in scope.GetPartsOfType<StyleDefinitionsPart>()) yield return part;
        foreach (var part in scope.GetPartsOfType<StylesWithEffectsPart>()) yield return part;
        foreach (var part in scope.GetPartsOfType<NumberingDefinitionsPart>()) yield return part;
        foreach (var part in scope.GetPartsOfType<WebSettingsPart>()) yield return part;
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

        changed |= AcceptParagraphMarkDeletions(document);
        changed |= AcceptTableRevisions(document);

        foreach (var element in document.Descendants().Where(e => RevisionDeletions.Contains(e.Name.LocalName)).ToList())
        {
            element.Remove();
            changed = true;
        }

        foreach (var element in document.Descendants().Where(e => RevisionWrappers.Contains(e.Name.LocalName)).Reverse().ToList())
        {
            element.Elements().Where(e => WrapperProperties.Contains(e.Name.LocalName)).Remove();
            element.ReplaceWith(element.Nodes());
            changed = true;
        }

        foreach (var element in document.Descendants().Where(IsRsidElement).ToList())
        {
            element.Remove();
            changed = true;
        }

        changed |= ReplaceListIdentifiers(document);

        return changed;
    }

    // Разметка диапазонов между абзацами не мешает слиянию: следующим абзацем считается первый абзац после неё.
    private static readonly HashSet<string> RangeMarkup = new(StringComparer.Ordinal)
    {
        "bookmarkStart", "bookmarkEnd", "permStart", "permEnd", "proofErr", "commentRangeStart", "commentRangeEnd",
        "moveFromRangeStart", "moveFromRangeEnd", "moveToRangeStart", "moveToRangeEnd",
        "customXmlDelRangeStart", "customXmlDelRangeEnd", "customXmlInsRangeStart", "customXmlInsRangeEnd",
        "customXmlMoveFromRangeStart", "customXmlMoveFromRangeEnd", "customXmlMoveToRangeStart", "customXmlMoveToRangeEnd"
    };

    /// <summary>
    /// Принимает удаление знака абзаца (w:pPr/w:rPr/w:del или w:moveFrom), как «Принять все» в Word: оставшееся
    /// содержимое абзаца переносится в начало следующего абзаца, который сохраняет свои свойства, а сам абзац удаляется.
    /// Если дальше не абзац (таблица, конец контейнера), пустой абзац удаляется, а непустой остаётся без метки.
    /// Абзац с разрывом раздела (w:sectPr) не удаляется никогда. Сами метки удаляются вместе с прочими следами правок.
    /// </summary>
    private static bool AcceptParagraphMarkDeletions(XDocument document)
    {
        var changed = false;
        // Порядок документа: в цепочке удалённых знаков содержимое каждого абзаца накапливается в следующем.
        foreach (var paragraph in document.Descendants().Where(IsParagraphWithDeletedMark).ToList())
        {
            if (paragraph.Parent == null) continue;
            var properties = paragraph.Elements().FirstOrDefault(e => e.Name.LocalName == "pPr");
            if (properties?.Elements().Any(e => e.Name.LocalName == "sectPr") == true) continue;

            var next = NextBlock(paragraph);
            if (next != null && next.Name == paragraph.Name)
            {
                var content = paragraph.Nodes().Where(n => n is not XElement { Name.LocalName: "pPr" }).ToList();
                foreach (var node in content) node.Remove();
                var nextProperties = next.Elements().FirstOrDefault(e => e.Name.LocalName == "pPr");
                if (nextProperties != null) nextProperties.AddAfterSelf(content);
                else next.AddFirst(content);
                paragraph.Remove();
                changed = true;
                continue;
            }

            // Пустой абзац удаляется, только если контейнер по-прежнему заканчивается абзацем (этого требует Word).
            var previous = PreviousBlock(paragraph);
            if (!HasRemainingContent(paragraph) && (next != null || previous?.Name == paragraph.Name))
            {
                paragraph.Remove();
                changed = true;
            }
        }
        return changed;
    }

    private static bool IsParagraphWithDeletedMark(XElement element) =>
        element.Name.LocalName == "p" &&
        element.Elements().FirstOrDefault(e => e.Name.LocalName == "pPr")?
            .Elements().FirstOrDefault(e => e.Name.LocalName == "rPr")?
            .Elements().Any(e => e.Name.LocalName is "del" or "moveFrom") == true;

    private static XElement? NextBlock(XElement element) =>
        element.ElementsAfterSelf().FirstOrDefault(e => !RangeMarkup.Contains(e.Name.LocalName));

    private static XElement? PreviousBlock(XElement element) =>
        element.ElementsBeforeSelf().LastOrDefault(e => !RangeMarkup.Contains(e.Name.LocalName));

    /// <summary>Остаётся ли в абзаце что-то кроме свойств, разметки диапазонов и удаляемых следов правок.</summary>
    private static bool HasRemainingContent(XElement paragraph) =>
        paragraph.Elements().Any(e =>
            e.Name.LocalName != "pPr" && !RangeMarkup.Contains(e.Name.LocalName) && !RevisionDeletions.Contains(e.Name.LocalName) &&
            !(e.Name.LocalName == "r" && e.Elements().All(c => c.Name.LocalName == "rPr" || RevisionDeletions.Contains(c.Name.LocalName))));

    /// <summary>
    /// Принимает исправления структуры таблиц: удалённые ячейки и строки удаляются вместе с содержимым
    /// (как при «Принять все»), изменённое вертикальное объединение ячеек применяется.
    /// </summary>
    private static bool AcceptTableRevisions(XDocument document)
    {
        var changed = false;
        var tables = new HashSet<XElement>();

        foreach (var marker in document.Descendants().Where(e => e.Name.LocalName == "cellDel" && e.Parent?.Name.LocalName == "tcPr").ToList())
        {
            var cell = marker.Parent!.Parent;
            if (cell?.Name.LocalName != "tc" || cell.Parent == null) continue;
            var row = cell.Ancestors().FirstOrDefault(e => e.Name.LocalName == "tr");
            cell.Remove();
            changed = true;
            if (row != null && row.Parent != null && !row.Descendants().Any(e => e.Name.LocalName == "tc"))
            {
                var table = row.Ancestors().FirstOrDefault(e => e.Name.LocalName == "tbl");
                row.Remove();
                if (table != null) tables.Add(table);
            }
        }

        foreach (var marker in document.Descendants().Where(e => e.Name.LocalName == "del" && e.Parent?.Name.LocalName == "trPr").ToList())
        {
            var row = marker.Parent!.Parent;
            if (row?.Name.LocalName != "tr" || row.Parent == null) continue;
            var table = row.Ancestors().FirstOrDefault(e => e.Name.LocalName == "tbl");
            row.Remove();
            changed = true;
            if (table != null) tables.Add(table);
        }

        // Таблица без строк недопустима: на её месте остаётся пустой абзац (он нужен и тогда, когда таблица была последней в ячейке).
        foreach (var table in tables.Where(t => t.Parent != null && !t.Descendants().Any(e => e.Name.LocalName == "tr")).ToList())
        {
            table.ReplaceWith(new XElement(table.Name.Namespace + "p"));
        }

        foreach (var marker in document.Descendants().Where(e => e.Name.LocalName == "cellMerge").ToList())
        {
            if (marker.Parent?.Name.LocalName == "tcPr")
            {
                ApplyVerticalMerge(marker.Parent, marker);
            }
            marker.Remove();
            changed = true;
        }

        return changed;
    }

    /// <summary>Принимает изменение вертикального объединения: значение vMerge из метки становится текущим.</summary>
    private static void ApplyVerticalMerge(XElement properties, XElement marker)
    {
        var w = marker.Name.Namespace;
        var revised = marker.Attributes().FirstOrDefault(a => a.Name.LocalName == "vMerge")?.Value;
        var merge = properties.Elements().FirstOrDefault(e => e.Name.LocalName == "vMerge");
        if (revised is not ("rest" or "cont"))
        {
            merge?.Remove();
            return;
        }

        if (merge == null)
        {
            merge = new XElement(w + "vMerge");
            var previous = properties.Elements().LastOrDefault(e => e.Name.LocalName is "cnfStyle" or "tcW" or "gridSpan" or "hMerge");
            if (previous != null) previous.AddAfterSelf(merge);
            else properties.AddFirst(merge);
        }
        merge.Attributes().Where(a => a.Name.LocalName == "val").Remove();
        if (revised == "rest") merge.SetAttributeValue(w + "val", "restart");
    }

    /// <summary>
    /// nsid и tmpl списков — случайные числа, одинаковые во всех копиях документа. Удалять их нельзя (nsid связывает
    /// продолжение списка), поэтому они заменяются значениями, вычисленными из номера определения списка.
    /// </summary>
    private static bool ReplaceListIdentifiers(XDocument document)
    {
        var changed = false;
        var index = 0u;
        foreach (var definition in document.Descendants().Where(e => e.Name.LocalName == "abstractNum"))
        {
            var idText = definition.Attributes().FirstOrDefault(a => a.Name.LocalName == "abstractNumId")?.Value;
            var id = uint.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : index;
            index++;
            // Умножение на нечётную константу — биекция на uint, поэтому разные номера дают разные nsid.
            var mixed = unchecked(id * 2654435761u);
            changed |= SetHexValue(definition, "nsid", mixed ^ 0x4D444C31u);
            changed |= SetHexValue(definition, "tmpl", mixed ^ 0x4D444C32u);
        }
        return changed;
    }

    private static bool SetHexValue(XElement definition, string localName, uint value)
    {
        var element = definition.Elements().FirstOrDefault(e => e.Name.LocalName == localName);
        var attribute = element?.Attributes().FirstOrDefault(a => a.Name.LocalName == "val");
        var text = value.ToString("X8", CultureInfo.InvariantCulture);
        if (attribute == null || attribute.Value == text) return false;
        attribute.Value = text;
        return true;
    }

    /// <summary>Элемент w:rsid (например, в определении стиля); список w:rsids в настройках удаляется целиком.</summary>
    private static bool IsRsidElement(XElement element) =>
        element.Name.LocalName == "rsid" && element.Parent?.Name.LocalName != "rsids";

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
        !attribute.IsNamespaceDeclaration &&
        (attribute.Name.LocalName.StartsWith("rsid", StringComparison.OrdinalIgnoreCase) ||
         attribute.Name.LocalName is "author" or "date" or "initials" or "paraId" or "textId" or "dateUtc" or "durableId");

    private static void DeleteCustomXmlParts(OpenXmlPart scope, OpenXmlSanitizeContext context)
    {
        foreach (var part in scope.GetPartsOfType<CustomXmlPart>().ToList())
        {
            WarnIfBibliography(part, context);
            scope.DeletePart(part);
        }
    }

    /// <summary>Список источников Word хранится в custom XML; его удаление меняет поведение документа, об этом нужно сказать.</summary>
    internal static void WarnIfBibliography(OpenXmlPart part, OpenXmlSanitizeContext context)
    {
        XDocument? xml;
        try
        {
            xml = OpenXmlPartXml.TryLoad(part);
        }
        catch (XmlException)
        {
            return;
        }

        if (xml?.Root?.Name.NamespaceName == Bibliography)
        {
            context.Warn("Удалён список источников Word (библиография): в нём хранятся авторы и названия цитируемых работ. " +
                         "Ссылки и список литературы в тексте сохранены, но обновить их из списка источников больше нельзя.");
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
