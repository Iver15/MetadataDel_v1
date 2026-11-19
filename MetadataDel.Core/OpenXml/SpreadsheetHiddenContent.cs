using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;

namespace MetadataDel.Core.OpenXml;

/// <summary>Скрытые листы Excel, копии данных в кэше сводных таблиц и текст удалённых ячеек в sharedStrings.</summary>
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
        // Во встроенной книге (данные диаграммы) на листы ссылаются формулы c:f диаграммы документа, которых
        // здесь не видно: удаление листа сломало бы «Изменить данные».
        if (context.Depth > 0)
        {
            context.WarnIncomplete("Во встроенной книге оставлены скрытые листы " +
                                   string.Join(", ", hidden.Select(h => "«" + h.Name + "»")) + ": на них могут ссылаться диаграммы документа.");
            return;
        }
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
        // Источник сводной таблицы может быть таблицей Excel (worksheetSource name="Таблица1").
        var tableNames = workbookPart.WorksheetParts.SelectMany(sheet => TableNames(sheet) ?? new List<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
            var missing = sheet != null ? !sheetNames.Contains(sheet) : name != null && !definedNames.Contains(name) &&
                                                               !tableNames.Contains(name) && !name.Contains('[');
            if (external || missing) detached.Add(cache.Uri.ToString());
        }
        return detached;
    }

    /// <summary>В sharedStrings есть строки, на которые не ссылается ни одна ячейка (текст удалённых ячеек и листов).</summary>
    public static bool FindUnusedSharedStrings(WorkbookPart workbookPart)
    {
        try
        {
            return BuildSharedStringMap(workbookPart, out _) != null;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    /// <summary>
    /// Оставляет в sharedStrings только строки, на которые ссылаются ячейки, и пересчитывает номера в листах.
    /// Excel хранит в таблице строк текст удалённых ячеек и листов до пересохранения файла.
    /// </summary>
    public static void PruneSharedStrings(WorkbookPart workbookPart)
    {
        int[]? map;
        int references;
        try
        {
            map = BuildSharedStringMap(workbookPart, out references);
        }
        catch (XmlException)
        {
            return;
        }
        if (map == null) return;

        OpenXmlPartXml.Update(workbookPart.SharedStringTablePart!, xml =>
        {
            var root = xml.Root!;
            var items = root.Elements().Where(e => e.Name.LocalName == "si").ToList();
            for (var i = 0; i < items.Count; i++)
            {
                if (map[i] < 0) items[i].Remove();
            }
            root.SetAttributeValue("count", references);
            root.SetAttributeValue("uniqueCount", map.Count(n => n >= 0));
            return true;
        });

        // Если удалены только строки в конце таблицы, номера остальных не меняются.
        if (map.Select((target, index) => target < 0 || target == index).All(same => same)) return;
        foreach (var sheet in CellSheets(workbookPart))
        {
            OpenXmlPartXml.Update(sheet, xml =>
            {
                var changed = false;
                foreach (var value in xml.Descendants().Where(e => e.Name.LocalName == "c" && (string?)e.Attribute("t") == "s")
                             .SelectMany(c => c.Elements().Where(e => e.Name.LocalName == "v")))
                {
                    if (string.IsNullOrWhiteSpace(value.Value)) continue;
                    var target = map[int.Parse(value.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture)];
                    if (target.ToString(CultureInfo.InvariantCulture) == value.Value) continue;
                    value.Value = target.ToString(CultureInfo.InvariantCulture);
                    changed = true;
                }
                return changed;
            });
        }
    }

    /// <summary>
    /// Новые номера строк sharedStrings (-1 — строка не используется) или null, если удалять нечего либо номера
    /// нельзя безопасно пересчитать (неожиданное значение в ячейке, журнал изменений общей книги).
    /// </summary>
    private static int[]? BuildSharedStringMap(WorkbookPart workbookPart, out int references)
    {
        references = 0;
        var table = workbookPart.SharedStringTablePart;
        // Журнал изменений общей книги тоже ссылается на строки по номерам.
        if (table == null || workbookPart.WorkbookRevisionHeaderPart != null) return null;

        var used = new bool[CountSharedStrings(table)];
        foreach (var sheet in CellSheets(workbookPart))
        {
            if (!CollectSharedStringIndexes(sheet, used, ref references)) return null;
        }
        if (used.All(u => u)) return null;

        var map = new int[used.Length];
        for (int i = 0, next = 0; i < used.Length; i++)
            map[i] = used[i] ? next++ : -1;
        return map;
    }

    // Листы с ячейками: обычные, листы макросов Excel 4.0 и диалоговые листы.
    private static IEnumerable<OpenXmlPart> CellSheets(WorkbookPart workbookPart) =>
        workbookPart.WorksheetParts.Cast<OpenXmlPart>()
            .Concat(workbookPart.GetPartsOfType<MacroSheetPart>())
            .Concat(workbookPart.GetPartsOfType<InternationalMacroSheetPart>())
            .Concat(workbookPart.GetPartsOfType<DialogsheetPart>())
            .ToList();

    private static int CountSharedStrings(SharedStringTablePart table)
    {
        using var stream = table.GetStream(FileMode.Open, FileAccess.Read);
        if (stream.Length == 0) return 0;
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var count = 0;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == 1 && reader.LocalName == "si") count++;
        }
        return count;
    }

    private static bool CollectSharedStringIndexes(OpenXmlPart sheet, bool[] used, ref int references)
    {
        using var stream = sheet.GetStream(FileMode.Open, FileAccess.Read);
        if (stream.Length == 0) return true;
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var sharedCell = false;
        var inValue = false;
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element when reader.LocalName == "c":
                    sharedCell = reader.GetAttribute("t") == "s";
                    inValue = false;
                    break;
                case XmlNodeType.Element when reader.LocalName == "v":
                    inValue = sharedCell && !reader.IsEmptyElement;
                    break;
                case XmlNodeType.EndElement when reader.LocalName is "v" or "c":
                    inValue = false;
                    if (reader.LocalName == "c") sharedCell = false;
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA when inValue:
                    if (!int.TryParse(reader.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ||
                        index < 0 || index >= used.Length) return false;
                    used[index] = true;
                    references++;
                    break;
            }
        }
        return true;
    }

    private static IEnumerable<XElement> Sheets(XDocument workbook) =>
        workbook.Descendants().Where(e => e.Name.LocalName == "sheet" && e.Parent?.Name.LocalName == "sheets");

    private static bool IsReferenced(WorkbookPart workbookPart, XDocument workbook, string name, int index, OpenXmlPart sheetPart)
    {
        // Таблицы листа (ListObject): на них ссылаются по имени, без имени листа.
        var tables = TableNames(sheetPart);
        if (tables == null) return true;
        var matcher = new SheetReferenceMatcher(name, tables);
        var definedNames = workbook.Descendants().Where(e => e.Name.LocalName == "definedName" &&
                                                            (string?)e.Attribute("localSheetId") != index.ToString());
        if (definedNames.Any(e => matcher.IsMatch(e.Value, formula: true))) return true;

        // Части самого листа (его рисунки, диаграммы, комментарии) удаляются вместе с ним и не считаются.
        var ownParts = new HashSet<OpenXmlPart> { sheetPart };
        foreach (var (_, part) in OpenXmlPackageSanitizer.Walk(sheetPart, new HashSet<OpenXmlPart>())) ownParts.Add(part);
        foreach (var (_, part) in OpenXmlPackageSanitizer.Walk(workbookPart, new HashSet<OpenXmlPart>()))
        {
            if (ownParts.Contains(part) || !OpenXmlPackageSanitizer.IsXmlContentType(part.ContentType)) continue;
            if (ReferencesSheet(part, name, matcher)) return true;
        }
        return false;
    }

    private static bool ReferencesSheet(OpenXmlPart part, string name, SheetReferenceMatcher matcher)
    {
        using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
        try
        {
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            var element = "";
            while (reader.Read())
            {
                // Текст ячеек и подписей (t) и кэшированные значения (v) — содержимое, а не формулы: имя таблицы
                // в них ссылкой не считается. Ссылки вида Лист!A1 проверяются везде, как и раньше.
                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA &&
                    matcher.IsMatch(reader.Value, formula: element is not ("t" or "v"))) return true;
                if (reader.NodeType != XmlNodeType.Element) continue;
                element = reader.LocalName;
                if (!reader.HasAttributes) continue;
                while (reader.MoveToNextAttribute())
                {
                    if (reader.LocalName == "sheet" && string.Equals(reader.Value, name, StringComparison.OrdinalIgnoreCase)) return true;
                    if (matcher.IsMatch(reader.Value, formula: true)) return true;
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

    /// <summary>Имена (name и displayName) таблиц листа; null, если описание таблицы не читается.</summary>
    private static List<string>? TableNames(OpenXmlPart sheetPart)
    {
        var names = new List<string>();
        foreach (var table in sheetPart.GetPartsOfType<TableDefinitionPart>())
        {
            XElement? root;
            try
            {
                root = OpenXmlPartXml.TryLoad(table)?.Root;
            }
            catch (XmlException)
            {
                return null;
            }
            foreach (var attributeName in new[] { "name", "displayName" })
            {
                var value = (string?)root?.Attribute(attributeName);
                if (!string.IsNullOrWhiteSpace(value)) names.Add(value);
            }
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Ссылки на лист: по имени листа и по именам его таблиц.</summary>
    private sealed class SheetReferenceMatcher
    {
        private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        // Строковые литералы формул: "текст", кавычка внутри удваивается.
        private static readonly Regex StringLiteral = new("\"(?:[^\"]|\"\")*\"", RegexOptions.CultureInvariant);

        private readonly Regex _sheet;
        private readonly Regex? _table;
        private readonly List<string> _tables;

        public SheetReferenceMatcher(string sheetName, List<string> tables)
        {
            // Ссылки вида Лист!A1, 'Мой лист'!A1 и трёхмерные диапазоны Лист1:Лист3!A1.
            var variants = new[] { sheetName, sheetName.Replace("'", "''") }.Distinct().Select(Regex.Escape);
            _sheet = new Regex(@"(?<![\p{L}\p{N}_.])'?(?:" + string.Join("|", variants) + @")'?(?=[!:])", Options);
            _tables = tables;
            // Имя таблицы целиком: tblRates и tblRates[Сумма], но не tblRatesOld (в именах бывают буквы, цифры, _ . \ ?).
            if (tables.Count > 0)
                _table = new Regex(@"(?<![\p{L}\p{N}_.\\?])(?:" + string.Join("|", tables.Select(Regex.Escape)) + @")(?![\p{L}\p{N}_.\\?])", Options);
        }

        /// <param name="value">Текст или значение атрибута части книги.</param>
        /// <param name="formula">Значение может быть формулой (а не текстом ячейки): проверять и имена таблиц.</param>
        public bool IsMatch(string value, bool formula)
        {
            if (_sheet.IsMatch(value)) return true;
            if (_table == null || !formula) return false;
            // INDIRECT("tblRates[Код]") собирает ссылку из строки: такие значения проверяем по любому вхождению имени.
            if (value.Contains("INDIRECT", StringComparison.OrdinalIgnoreCase) &&
                _tables.Any(t => value.Contains(t, StringComparison.OrdinalIgnoreCase))) return true;
            return _table.IsMatch(StringLiteral.Replace(value, "\"\""));
        }
    }
}
