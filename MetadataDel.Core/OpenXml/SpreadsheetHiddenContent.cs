using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;

namespace MetadataDel.Core.OpenXml;

/// <summary>Скрытые листы Excel и копии данных в кэше сводных таблиц.</summary>
internal static class SpreadsheetHiddenContent
{
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static List<(XElement Sheet, int Index, string Name, string RelationshipId)> FindHiddenSheets(XDocument workbook) =>
        Sheets(workbook).Select((sheet, index) => (Sheet: sheet, Index: index))
            .Where(s => (string?)s.Sheet.Attribute("state") is "hidden" or "veryHidden")
            .Select(s => (s.Sheet, s.Index, (string?)s.Sheet.Attribute("name") ?? "",
                (string?)s.Sheet.Attribute(XName.Get("id", RelationshipsNamespace)) ?? ""))
            .ToList();

    /// <summary>
    /// Удаляет скрытые листы, на которые ничего не ссылается. Листы с зависимостями (формулы, имена, диаграммы,
    /// сводные таблицы, внутренние гиперссылки) остаются: без них книга перестанет считаться.
    /// </summary>
    public static void RemoveUnreferencedHiddenSheets(WorkbookPart workbookPart, OpenXmlSanitizeContext context)
    {
        var workbook = OpenXmlPartXml.TryLoad(workbookPart);
        if (workbook == null) return;
        var hidden = FindHiddenSheets(workbook);
        if (hidden.Count == 0) return;
        var total = Sheets(workbook).Count();

        var removable = new List<(int Index, string Name, string RelationshipId, OpenXmlPart Part)>();
        foreach (var (_, index, name, relationshipId) in hidden)
        {
            if (!workbookPart.TryGetPartById(relationshipId, out var part)) continue;
            if (IsReferenced(workbookPart, workbook, name, index, part))
            {
                context.Warn($"Скрытый лист «{name}» используется формулами или другими объектами книги и оставлен.");
                continue;
            }
            removable.Add((index, name, relationshipId, part));
        }
        if (removable.Count == 0 || removable.Count == total) return;

        var removedIndexes = removable.Select(r => r.Index).ToHashSet();
        int? Remap(int index) => removedIndexes.Contains(index) ? null : index - removedIndexes.Count(r => r < index);
        OpenXmlPartXml.Update(workbookPart, xml =>
        {
            var relationshipIds = removable.Select(r => r.RelationshipId).ToHashSet(StringComparer.Ordinal);
            Sheets(xml).Where(s => relationshipIds.Contains((string?)s.Attribute(XName.Get("id", RelationshipsNamespace)) ?? "")).Remove();
            foreach (var definedName in xml.Descendants().Where(e => e.Name.LocalName == "definedName").ToList())
            {
                if (!int.TryParse((string?)definedName.Attribute("localSheetId"), out var scope)) continue;
                var mapped = Remap(scope);
                if (mapped == null) definedName.Remove();
                else definedName.SetAttributeValue("localSheetId", mapped);
            }
            foreach (var view in xml.Descendants().Where(e => e.Name.LocalName == "workbookView"))
            {
                foreach (var attributeName in new[] { "activeTab", "firstSheet" })
                {
                    if (int.TryParse((string?)view.Attribute(attributeName), out var tab))
                        view.SetAttributeValue(attributeName, Remap(tab) ?? 0);
                }
            }
            return true;
        });

        foreach (var (_, _, _, part) in removable)
            workbookPart.DeletePart(part);
        // Цепочка вычислений ссылается на листы по номерам; Excel пересоздаёт её сам.
        if (workbookPart.CalculationChainPart != null)
            workbookPart.DeletePart(workbookPart.CalculationChainPart);
        context.Warn("Удалены скрытые листы: " + string.Join(", ", removable.Select(r => "«" + r.Name + "»")) + ".");
    }

    /// <summary>Сводные таблицы, кэш которых — единственная копия исходных данных (источник вне книги).</summary>
    public static List<string> FindDetachedPivotCaches(WorkbookPart workbookPart, XDocument? workbook)
    {
        var sheetNames = workbook == null ? new HashSet<string>() : Sheets(workbook).Select(s => (string?)s.Attribute("name") ?? "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var definedNames = workbook?.Descendants().Where(e => e.Name.LocalName == "definedName")
            .Select(e => (string?)e.Attribute("name") ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>();
        var detached = new List<string>();
        foreach (var cache in workbookPart.PivotTableCacheDefinitionParts)
        {
            var source = OpenXmlPartXml.TryLoad(cache)?.Descendants().FirstOrDefault(e => e.Name.LocalName == "cacheSource");
            if (source == null) continue;
            var worksheetSource = source.Elements().FirstOrDefault(e => e.Name.LocalName == "worksheetSource");
            var external = (string?)source.Attribute("type") is "external" or "consolidation" ||
                           worksheetSource?.Attribute(XName.Get("id", RelationshipsNamespace)) != null;
            var sheet = (string?)worksheetSource?.Attribute("sheet");
            var name = (string?)worksheetSource?.Attribute("name");
            var missing = sheet != null ? !sheetNames.Contains(sheet) : name != null && !definedNames.Contains(name) && !name.Contains('[');
            if (external || missing) detached.Add(cache.Uri.ToString());
        }
        return detached;
    }

    private static IEnumerable<XElement> Sheets(XDocument workbook) =>
        workbook.Descendants().Where(e => e.Name.LocalName == "sheet" && e.Parent?.Name.LocalName == "sheets");

    private static bool IsReferenced(WorkbookPart workbookPart, XDocument workbook, string name, int index, OpenXmlPart sheetPart)
    {
        var pattern = ReferencePattern(name);
        var definedNames = workbook.Descendants().Where(e => e.Name.LocalName == "definedName" &&
                                                            (string?)e.Attribute("localSheetId") != index.ToString());
        if (definedNames.Any(e => pattern.IsMatch(e.Value))) return true;

        // Части самого листа (его рисунки, диаграммы, комментарии) удаляются вместе с ним и не считаются.
        var ownParts = new HashSet<OpenXmlPart> { sheetPart };
        foreach (var (_, part) in OpenXmlPackageSanitizer.Walk(sheetPart, new HashSet<OpenXmlPart>())) ownParts.Add(part);
        foreach (var (_, part) in OpenXmlPackageSanitizer.Walk(workbookPart, new HashSet<OpenXmlPart>()))
        {
            if (ownParts.Contains(part) || !OpenXmlPackageSanitizer.IsXmlContentType(part.ContentType)) continue;
            if (ReferencesSheet(part, name, pattern)) return true;
        }
        return false;
    }

    private static bool ReferencesSheet(OpenXmlPart part, string name, Regex pattern)
    {
        using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
        try
        {
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            while (reader.Read())
            {
                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA && pattern.IsMatch(reader.Value)) return true;
                if (reader.NodeType != XmlNodeType.Element || !reader.HasAttributes) continue;
                while (reader.MoveToNextAttribute())
                {
                    if (reader.LocalName == "sheet" && string.Equals(reader.Value, name, StringComparison.OrdinalIgnoreCase)) return true;
                    if (pattern.IsMatch(reader.Value)) return true;
                }
                reader.MoveToElement();
            }
        }
        catch (XmlException)
        {
            // Непрочитанную часть считаем ссылающейся: лучше оставить скрытый лист, чем сломать книгу.
            return true;
        }
        return false;
    }

    // Ссылки вида Лист!A1, 'Мой лист'!A1 и трёхмерные диапазоны Лист1:Лист3!A1.
    private static Regex ReferencePattern(string name)
    {
        var variants = new[] { name, name.Replace("'", "''") }.Distinct().Select(Regex.Escape);
        return new Regex(@"(?<![\p{L}\p{N}_.])'?(?:" + string.Join("|", variants) + @")'?(?=[!:])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
