using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using MetadataDel.Core.Cleaning;

namespace MetadataDel.Core.OpenXml;

#pragma warning disable OOXML0001

internal static class SpreadsheetPrivacySanitizer
{
    // fileSharing не удаляется целиком: в нём рекомендация «только для чтения» и пароль на запись;
    // убирается только имя пользователя (см. SanitizeWorkbookXml).
    private static readonly HashSet<string> WorkbookPrivacyElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "absPath", "fileVersion", "externalReferences", "smartTagPr", "smartTagTypes", "webPublishObjects",
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
        var hasExternalLinks = workbookPart.ExternalWorkbookParts.Any() ||
                               workbookXml?.Descendants().Any(e => e.Name.LocalName == "externalReferences") == true;
        var externalNames = FindExternalNames(workbookXml);
        if (DependsOnExternalWorkbooks(workbookPart, hasExternalLinks, externalNames) || HasQueryTables(workbookPart))
        {
            if (context.Depth == 0)
                throw new NotSupportedException("Книга содержит формулы с внешними ссылками или таблицы запросов. Преобразуйте их в значения перед очисткой; исходный файл сохранён.");
            // Во встроенной книге (данные диаграммы) формулы не проверить в Excel; ссылки оставляем рабочими.
            context.WarnIncomplete("Встроенная книга содержит внешние ссылки или таблицы запросов; пути к внешним источникам сохранены.");
            keepExternalLinks = true;
        }

        // Сводные таблицы на внешних подключениях перестанут открываться, если удалить connections.xml.
        if (workbookPart.ConnectionsPart != null && workbookPart.PivotTableCacheDefinitionParts.Any(cache =>
                OpenXmlPartXml.TryLoad(cache)?.Descendants().Any(e => e.Name.LocalName == "cacheSource" && e.Attribute("connectionId") != null) == true))
        {
            if (context.Depth == 0)
                throw new NotSupportedException("Книга содержит сводные таблицы на внешних подключениях. Преобразуйте их в значения перед очисткой; исходный файл сохранён.");
            context.WarnIncomplete("Встроенная книга содержит внешние подключения; сведения о подключениях сохранены.");
            keepExternalLinks = true;
        }

        foreach (var customXmlPart in workbookPart.CustomXmlParts.ToList())
        {
            // Запросы Power Query хранятся в custom XML (DataMashup) и пропадают вместе с ним.
            if (IsDataMashup(customXmlPart))
                context.Warn("Удалены запросы Power Query (DataMashup): обновить данные из исходных источников в Excel больше нельзя.");
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

        OpenXmlPartXml.Update(workbookPart, xml => SanitizeWorkbookXml(xml, keepExternalLinks, keepExternalLinks ? new HashSet<string>() : externalNames));
        SpreadsheetHiddenContent.RemoveUnreferencedHiddenSheets(workbookPart, context);
        if (SpreadsheetHiddenContent.FindDetachedPivotCaches(workbookPart, OpenXmlPartXml.TryLoad(workbookPart)).Count > 0)
        {
            context.Warn("Сводная таблица хранит копию исходных данных, которых нет в книге. Чтобы убрать эти данные, замените сводную таблицу значениями.");
        }

        foreach (var chartsheetPart in workbookPart.ChartsheetParts)
        {
            var removedPrinterIds = DeletePrinterSettings(chartsheetPart, chartsheetPart.SpreadsheetPrinterSettingsParts);
            OpenXmlPartXml.Update(chartsheetPart, xml => SanitizeWorksheetXml(xml, new HashSet<string>(), removedPrinterIds));
        }

        foreach (var worksheetPart in workbookPart.WorksheetParts)
        {
            var removedPrinterIds = DeletePrinterSettings(worksheetPart, worksheetPart.SpreadsheetPrinterSettingsParts);
            // Пользовательские свойства листа (customPr) — произвольные имена и значения от надстроек и пользователя.
            foreach (var customProperty in worksheetPart.CustomPropertyParts.ToList())
            {
                worksheetPart.DeletePart(customProperty);
            }
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
                    XDocument? drawing;
                    try
                    {
                        drawing = OpenXmlPartXml.TryLoad(relationship.OpenXmlPart);
                    }
                    catch (XmlException)
                    {
                        // Сторонние генераторы пишут VML, который не является корректным XML. Такую часть оставляем
                        // как есть, чтобы не сорвать очистку всей книги.
                        context.WarnIncomplete("Не удалось проверить рисунок примечаний (VML) на листе: его разметка повреждена, " +
                                               "часть оставлена без изменений. В ней могут остаться имена авторов примечаний.");
                        continue;
                    }
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

        foreach (var sheet in workbookPart.GetPartsOfType<MacroSheetPart>().Cast<OpenXmlPart>()
                     .Concat(workbookPart.GetPartsOfType<InternationalMacroSheetPart>()).ToList())
        {
            foreach (var customProperty in sheet.GetPartsOfType<CustomPropertyPart>().ToList())
            {
                sheet.DeletePart(customProperty);
            }
            OpenXmlPartXml.Update(sheet, xml => SanitizeWorksheetXml(xml, new HashSet<string>(), new HashSet<string>()));
        }

        RemoveRevisionIdentifiersFromOtherParts(workbookPart);
        SpreadsheetHiddenContent.PruneSharedStrings(workbookPart);
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

            if (workbookXml.Descendants().Any(e => e.Name.LocalName == "fileSharing" && e.Attribute("userName") != null))
            {
                findings.Add("имя пользователя в параметрах общего доступа книги");
            }

            if (workbookXml.Descendants().Any(e => e.Name.LocalName == "definedName" &&
                                                  IsExternalReference(e.Value)))
            {
                findings.Add("внешние defined names");
            }

            if (workbookXml.Descendants().Any(e => e.Name.LocalName == "customWorkbookViews" ||
                                                  e.Name.LocalName == "definedName" && CustomViewNamePattern.IsMatch((string?)e.Attribute("name") ?? "")))
            {
                findings.Add("пользовательские представления книги");
            }
        }

        if (SpreadsheetHiddenContent.FindUnusedSharedStrings(workbookPart))
        {
            findings.Add("неиспользуемые строки в sharedStrings (текст удалённых ячеек)");
        }

        if (workbookPart.WorksheetParts.Any(p => p.WorksheetCommentsPart != null || p.WorksheetThreadedCommentsParts.Any()))
        {
            findings.Add("комментарии Excel");
        }

        return findings.Count > 0;
    }

    private static bool SanitizeWorkbookXml(XDocument document, bool keepExternalLinks, HashSet<string> externalNames)
    {
        var changed = RemoveRevisionIdentifiers(document);

        foreach (var element in document.Descendants().Where(e => WorkbookPrivacyElements.Contains(e.Name.LocalName) &&
                     !(keepExternalLinks && e.Name.LocalName == "externalReferences")).ToList())
        {
            element.Remove();
            changed = true;
        }

        // Параметры общего доступа: рекомендация «только для чтения» и пароль на запись остаются, имя того,
        // кто зарезервировал книгу, удаляется.
        foreach (var userName in document.Descendants().Where(e => e.Name.LocalName == "fileSharing")
                     .Attributes().Where(a => a.Name == "userName").ToList())
        {
            userName.Remove();
            changed = true;
        }

        // Внешние имена и скрытые имена пользовательских представлений (Z_<GUID>_.wvu.*: их области печати и фильтры).
        foreach (var definedName in document.Descendants().Where(e => e.Name.LocalName == "definedName" &&
                     (externalNames.Contains(DefinedNameKey(e)) || CustomViewNamePattern.IsMatch((string?)e.Attribute("name") ?? ""))).ToList())
        {
            definedName.Remove();
            changed = true;
        }

        // Пользовательские представления: в их именах обычно имя пользователя («Иванов — личное представление»).
        foreach (var element in document.Descendants().Where(e => e.Name.LocalName == "customWorkbookViews" ||
                     e.Name.LocalName == "definedNames" && !e.HasElements).ToList())
        {
            element.Remove();
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
        var changed = RemoveRevisionIdentifiers(document);

        // Пользовательские представления листа (их имена — в workbook.xml) и ссылки на удалённые customProperty*.bin.
        foreach (var element in document.Root?.Elements().Where(e => e.Name.LocalName is "customSheetViews" or "customProperties").ToList() ??
                                new List<XElement>())
        {
            element.Remove();
            changed = true;
        }

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

    private const string DataMashupNamespace = "http://schemas.microsoft.com/DataMashup";

    // Скрытые имена пользовательских представлений: Z_<GUID с подчёркиваниями>_.wvu.PrintArea, .wvu.FilterData и т. п.
    private static readonly Regex CustomViewNamePattern = new(
        @"^Z_[0-9A-F]{8}_[0-9A-F]{4}_[0-9A-F]{4}_[0-9A-F]{4}_[0-9A-F]{12}_\.wvu\.",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // xr:uid, xr2:uid, xr3:uid, xr10:uidLastSave и т. п.: GUID-ы, по которым Office сопоставляет копии файла.
    private static readonly Regex RevisionNamespacePattern = new(
        @"^http://schemas\.microsoft\.com/office/spreadsheetml/\d{4}/revision\d*$", RegexOptions.CultureInvariant);

    // Номер или имя книги в ссылке внутри кавычек: '[1]Мой лист'!A1, 'C:\папка\[Книга.xlsx]Лист'!A1.
    private static readonly Regex QuotedWorkbookPattern = new(@"\[([^\[\]]*)\]", RegexOptions.CultureInvariant);

    // Ячейки (f), условное форматирование (formula), проверка данных (formula1/2, xm:f), спарклайны и диаграммы (c:f, cx:f).
    private static readonly HashSet<string> FormulaElements = new(StringComparer.Ordinal) { "f", "formula", "formula1", "formula2" };

    private static readonly char[] StructuredReferenceChars = { '[', '#', '@', '\'' };

    internal static bool IsRevisionNamespace(string namespaceName) => RevisionNamespacePattern.IsMatch(namespaceName);

    /// <summary>Удаляет атрибуты xr:uid и подобные (они помечены mc:Ignorable, Excel создаёт их заново).</summary>
    private static bool RemoveRevisionIdentifiers(XDocument document)
    {
        var attributes = document.Descendants().Attributes()
            .Where(a => !a.IsNamespaceDeclaration && IsRevisionNamespace(a.Name.NamespaceName)).ToList();
        foreach (var attribute in attributes)
        {
            attribute.Remove();
        }
        return attributes.Count > 0;
    }

    /// <summary>Идентификаторы правок в остальных частях книги: таблицы, сводные таблицы, таблицы запросов.</summary>
    private static void RemoveRevisionIdentifiersFromOtherParts(WorkbookPart workbookPart)
    {
        foreach (var (_, part) in OpenXmlPackageSanitizer.Walk(workbookPart, new HashSet<OpenXmlPart>()))
        {
            if (part is WorksheetPart or ChartsheetPart or MacroSheetPart or InternationalMacroSheetPart or SharedStringTablePart) continue;
            if (!part.ContentType.Contains("spreadsheetml", StringComparison.OrdinalIgnoreCase) ||
                !OpenXmlPackageSanitizer.IsXmlContentType(part.ContentType)) continue;
            try
            {
                OpenXmlPartXml.Update(part, RemoveRevisionIdentifiers);
            }
            catch (XmlException)
            {
                // Повреждённую часть не меняем; аудит сообщит об оставшихся идентификаторах.
            }
        }
    }

    private static bool IsDataMashup(OpenXmlPart part)
    {
        try
        {
            using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            reader.MoveToContent();
            return reader.NamespaceURI == DataMashupNamespace;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    // Таблицы запросов бывают привязаны к листу или к таблице (table*.xml → queryTable*.xml).
    private static bool HasQueryTables(WorkbookPart workbookPart) =>
        OpenXmlPackageSanitizer.Walk(workbookPart, new HashSet<OpenXmlPart>()).Any(p => p.Part is QueryTablePart);

    /// <summary>
    /// Формулы, которые сломаются после удаления связей с внешними книгами: в ячейках, условном форматировании,
    /// проверке данных, спарклайнах и диаграммах книги, а также через удаляемые внешние имена.
    /// </summary>
    private static bool DependsOnExternalWorkbooks(WorkbookPart workbookPart, bool hasExternalLinks, HashSet<string> externalNames)
    {
        var namePattern = NameUsagePattern(externalNames.Select(NameOfKey));
        if (!hasExternalLinks && namePattern == null) return false;
        foreach (var (_, part) in OpenXmlPackageSanitizer.Walk(workbookPart, new HashSet<OpenXmlPart>()))
        {
            if (part is not (WorksheetPart or MacroSheetPart or InternationalMacroSheetPart or ChartPart or ExtendedChartPart)) continue;
            foreach (var formula in FormulaTexts(part))
            {
                if (hasExternalLinks && IsExternalReference(formula) || namePattern?.IsMatch(MaskLiterals(formula)) == true) return true;
            }
        }
        return false;
    }

    private static IEnumerable<string> FormulaTexts(OpenXmlPart part)
    {
        using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
        if (stream.Length == 0) yield break;
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var current = "";
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    current = reader.LocalName;
                    break;
                case XmlNodeType.EndElement:
                    current = "";
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA when FormulaElements.Contains(current):
                    yield return reader.Value;
                    break;
            }
        }
    }

    /// <summary>
    /// Ключи (имя и область) внешних имён: ссылаются на другую книгу напрямую или через другие внешние имена.
    /// </summary>
    private static HashSet<string> FindExternalNames(XDocument? workbook)
    {
        var names = workbook?.Descendants().Where(e => e.Name.LocalName == "definedName").ToList() ?? new List<XElement>();
        var external = names.Where(e => IsExternalReference(e.Value)).ToList();
        // Имя, построенное на внешнем имени, после удаления последнего превратилось бы в #ИМЯ?.
        for (var added = true; added;)
        {
            added = false;
            var pattern = NameUsagePattern(external.Select(e => (string?)e.Attribute("name") ?? ""));
            if (pattern == null) break;
            foreach (var name in names.Except(external).Where(e => pattern.IsMatch(MaskLiterals(e.Value))).ToList())
            {
                external.Add(name);
                added = true;
            }
        }
        return external.Select(DefinedNameKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string DefinedNameKey(XElement definedName) =>
        ((string?)definedName.Attribute("name") ?? "") + "\n" + ((string?)definedName.Attribute("localSheetId") ?? "");

    private static string NameOfKey(string key) => key[..key.IndexOf('\n')];

    private static Regex? NameUsagePattern(IEnumerable<string> names)
    {
        var escaped = names.Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Select(Regex.Escape).ToList();
        if (escaped.Count == 0) return null;
        return new Regex(@"(?<![\p{L}\p{N}_.\\?])(?:" + string.Join("|", escaped) + @")(?![\p{L}\p{N}_.\\?!\[])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Ссылка на другую книгу в сохранённой формуле: [1]Лист!A1, '[1]Мой лист'!A1, [1]!Имя, [1]Лист1:Лист3!A1,
    /// а также с именем файла вместо номера ('C:\папка\[Книга.xlsx]Лист'!A1). [0] — сама книга. Строковые
    /// константы ("https://...") и структурированные ссылки на таблицы (Таблица1[[#This Row],[Сумма]]) не считаются.
    /// </summary>
    internal static bool IsExternalReference(string formula)
    {
        for (var i = 0; i < formula.Length;)
        {
            switch (formula[i])
            {
                case '"':
                    i = SkipQuoted(formula, i);
                    break;
                case '\'':
                    var end = SkipQuoted(formula, i);
                    if (end < formula.Length && formula[end] == '!')
                    {
                        var workbook = QuotedWorkbookPattern.Match(formula[(i + 1)..(end - 1)]);
                        if (workbook.Success && workbook.Groups[1].Value != "0") return true;
                    }
                    i = end;
                    break;
                case '[':
                    var close = MatchingBracket(formula, i);
                    if (close < 0) return false;
                    var content = formula[(i + 1)..close];
                    var next = close + 1;
                    while (next < formula.Length && (char.IsLetterOrDigit(formula[next]) || formula[next] is '_' or '.' or ':')) next++;
                    if (next < formula.Length && formula[next] == '!' && content.Length > 0 && content != "0" &&
                        content.IndexOfAny(StructuredReferenceChars) < 0) return true;
                    i = close + 1;
                    break;
                default:
                    i++;
                    break;
            }
        }
        return false;
    }

    /// <summary>Позиция после закрывающей кавычки (удвоенная кавычка — экранированная).</summary>
    private static int SkipQuoted(string text, int start)
    {
        var quote = text[start];
        for (var i = start + 1; i < text.Length; i++)
        {
            if (text[i] != quote) continue;
            if (i + 1 < text.Length && text[i + 1] == quote)
            {
                i++;
                continue;
            }
            return i + 1;
        }
        return text.Length;
    }

    private static int MatchingBracket(string text, int start)
    {
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '\'':
                    i++; // экранирование в структурированных ссылках: Таблица1['[Код']]
                    break;
                case '[':
                    depth++;
                    break;
                case ']':
                    if (--depth == 0) return i;
                    break;
            }
        }
        return -1;
    }

    /// <summary>Заменяет пробелами строковые константы и имена листов в кавычках, чтобы искать в формуле только имена.</summary>
    private static string MaskLiterals(string formula)
    {
        var chars = formula.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] == '[')
            {
                var close = MatchingBracket(formula, i);
                if (close < 0) break;
                i = close;
                continue;
            }
            if (chars[i] is not ('"' or '\'')) continue;
            var end = SkipQuoted(formula, i);
            for (var j = i + 1; j < end - 1; j++) chars[j] = ' ';
            i = end - 1;
        }
        return new string(chars);
    }

    private static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);
}

#pragma warning restore OOXML0001
