using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;

namespace MetadataDel.Core.OpenXml;

/// <summary>
/// Скрытый текст Word и локальные пути в кодах полей (INCLUDEPICTURE, INCLUDETEXT, LINK, HYPERLINK и др.).
/// </summary>
internal static class WordHiddenContent
{
    // Прогоны с этими элементами — часть структуры поля, сноски или комментария; их удаление повредило бы документ.
    private static readonly HashSet<string> StructuralRunContent = new(StringComparer.Ordinal)
    {
        "fldChar", "instrText", "delInstrText", "footnoteReference", "endnoteReference", "commentReference",
        "annotationRef", "footnoteRef", "endnoteRef", "separator", "continuationSeparator", "object"
    };

    private static readonly Regex QuotedArgument = new("\"((?:\\\\\"|[^\"])*)\"", RegexOptions.CultureInvariant);

    private static readonly Regex UnquotedPath = new(@"(?<=^|\s)(?:[A-Za-z]:\\\\|\\\\\\\\|file:|\.{1,2}(?:\\\\|/))\S+", RegexOptions.CultureInvariant);

    /// <summary>
    /// Части с текстом документа, где встречаются скрытый текст и поля: сам документ (основной или глоссарий),
    /// его колонтитулы и сноски.
    /// </summary>
    public static IEnumerable<OpenXmlPart> EnumerateContentParts(OpenXmlPart scope)
    {
        yield return scope;
        foreach (var part in scope.GetPartsOfType<HeaderPart>()) yield return part;
        foreach (var part in scope.GetPartsOfType<FooterPart>()) yield return part;
        foreach (var part in scope.GetPartsOfType<FootnotesPart>()) yield return part;
        foreach (var part in scope.GetPartsOfType<EndnotesPart>()) yield return part;
    }

    /// <summary>
    /// Видимость текста, заданная стилями: итоговое значение vanish каждого стиля (с учётом наследования basedOn),
    /// стиль абзаца по умолчанию и значение из docDefaults.
    /// </summary>
    public static WordStyleVisibility LoadStyleVisibility(StyleDefinitionsPart? stylesPart)
    {
        var styles = stylesPart == null ? null : OpenXmlPartXml.TryLoad(stylesPart);
        if (styles == null) return WordStyleVisibility.Empty;

        var definitions = styles.Descendants().Where(e => e.Name.LocalName == "style")
            .Select(e => (Id: Attribute(e, "styleId"), Type: Attribute(e, "type"), Default: Attribute(e, "default"),
                BasedOn: Attribute(e.Elements().FirstOrDefault(c => c.Name.LocalName == "basedOn"), "val"),
                Vanish: Vanish(e.Elements().FirstOrDefault(c => c.Name.LocalName == "rPr"))))
            .Where(d => d.Id != null)
            .GroupBy(d => d.Id!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var effective = new Dictionary<string, bool?>(StringComparer.Ordinal);
        foreach (var id in definitions.Keys)
        {
            bool? vanish = null;
            var current = id;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            // Ближайшее явное значение в цепочке basedOn определяет свойство стиля, в том числе явное «не скрывать».
            while (current != null && seen.Add(current) && definitions.TryGetValue(current, out var definition))
            {
                if (definition.Vanish != null)
                {
                    vanish = definition.Vanish;
                    break;
                }
                current = definition.BasedOn;
            }
            effective[id] = vanish;
        }

        var defaultParagraphStyle = definitions.Values
            .FirstOrDefault(d => d.Type == "paragraph" && d.Default is "1" or "true" or "on").Id;
        var defaults = Vanish(styles.Descendants().FirstOrDefault(e => e.Name.LocalName == "docDefaults")?
            .Elements().FirstOrDefault(e => e.Name.LocalName == "rPrDefault")?
            .Elements().FirstOrDefault(e => e.Name.LocalName == "rPr"));
        return new WordStyleVisibility(effective, defaultParagraphStyle, defaults);
    }

    /// <summary>Прогоны скрытого текста, которые можно удалить, не нарушая структуру документа.</summary>
    public static List<XElement> FindHiddenRuns(XDocument document, WordStyleVisibility styles) =>
        document.Descendants().Where(e => e.Name.LocalName == "r" && IsHidden(e, styles) &&
                                          !e.Descendants().Any(d => StructuralRunContent.Contains(d.Name.LocalName)))
            .ToList();

    public static bool RemoveHiddenRuns(XDocument document, WordStyleVisibility styles, out int removed)
    {
        var runs = FindHiddenRuns(document, styles);
        removed = runs.Count;
        foreach (var run in runs) run.Remove();
        return removed > 0;
    }

    /// <summary>
    /// Приоритет как в Word: свойство прогона, затем стиль знака, затем стиль абзаца (или стиль абзаца по умолчанию),
    /// затем docDefaults. Явное vanish=0/false/off на любом уровне делает текст видимым.
    /// </summary>
    private static bool IsHidden(XElement run, WordStyleVisibility styles)
    {
        var properties = run.Elements().FirstOrDefault(e => e.Name.LocalName == "rPr");
        var direct = Vanish(properties);
        if (direct != null) return direct.Value;

        var runStyle = Attribute(properties?.Elements().FirstOrDefault(e => e.Name.LocalName == "rStyle"), "val");
        if (styles.StyleVanish(runStyle) is { } characterStyle) return characterStyle;

        var paragraph = run.Ancestors().FirstOrDefault(e => e.Name.LocalName == "p");
        var paragraphStyle = Attribute(paragraph?.Elements().FirstOrDefault(e => e.Name.LocalName == "pPr")?
            .Elements().FirstOrDefault(e => e.Name.LocalName == "pStyle"), "val");
        // Ссылка на несуществующий стиль абзаца в Word означает стиль по умолчанию.
        if (paragraphStyle == null || !styles.Contains(paragraphStyle)) paragraphStyle = styles.DefaultParagraphStyle;
        if (styles.StyleVanish(paragraphStyle) is { } paragraphValue) return paragraphValue;

        return styles.DefaultVanish ?? false;
    }

    private static readonly HashSet<string> RelationshipNamespaces = new(StringComparer.Ordinal)
    {
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships",
        "http://purl.oclc.org/ooxml/officeDocument/relationships"
    };

    /// <summary>
    /// Удаляет связи части с картинками, OLE-объектами, вложенными пакетами, диаграммами и SmartArt, на которые
    /// больше не ссылается её XML (после удаления скрытого текста). Сама целевая часть удаляется из пакета,
    /// только если на неё не ссылается никакая другая часть. Связи колонтитулов, стилей, нумерации и т. п. не трогаются.
    /// </summary>
    public static int DeleteUnreferencedMedia(OpenXmlPart part)
    {
        var xml = OpenXmlPartXml.TryLoad(part);
        if (xml?.Root == null) return 0;

        var referenced = ReferencedRelationshipIds(xml);
        // Часть отрисовки SmartArt (diagramDrawing) связана с документом, но ссылается на неё relId в части данных SmartArt.
        foreach (var data in part.Parts.Where(p => RelationshipName(p.OpenXmlPart.RelationshipType) == "diagramData"))
        {
            var dataXml = OpenXmlPartXml.TryLoad(data.OpenXmlPart);
            if (dataXml?.Root != null) referenced.UnionWith(ReferencedRelationshipIds(dataXml));
        }

        var removed = 0;
        foreach (var pair in part.Parts.ToList())
        {
            if (!IsMediaRelationship(pair.OpenXmlPart.RelationshipType) || referenced.Contains(pair.RelationshipId)) continue;
            part.DeletePart(pair.RelationshipId);
            removed++;
        }
        foreach (var external in part.ExternalRelationships.ToList())
        {
            if (!IsMediaRelationship(external.RelationshipType) || referenced.Contains(external.Id)) continue;
            part.DeleteExternalRelationship(external);
            removed++;
        }
        return removed;
    }

    /// <summary>Значения всех атрибутов из пространства имён связей (r:embed, r:link, r:id, r:pict…) и атрибутов relid/relId (o:relid, dsp:dataModelExt).</summary>
    private static HashSet<string> ReferencedRelationshipIds(XDocument xml) =>
        xml.Root!.DescendantsAndSelf().Attributes()
            .Where(a => !a.IsNamespaceDeclaration &&
                        (RelationshipNamespaces.Contains(a.Name.NamespaceName) || a.Name.LocalName.Equals("relid", StringComparison.OrdinalIgnoreCase)))
            .Select(a => a.Value)
            .ToHashSet(StringComparer.Ordinal);

    private static bool IsMediaRelationship(string relationshipType)
    {
        var name = RelationshipName(relationshipType);
        return name is "image" or "oleObject" or "package" or "chart" or "chartEx" ||
               name.StartsWith("diagram", StringComparison.Ordinal);
    }

    private static string RelationshipName(string relationshipType) =>
        relationshipType[(relationshipType.LastIndexOf('/') + 1)..];

    private static bool? Vanish(XElement? runProperties)
    {
        var vanish = runProperties?.Elements().FirstOrDefault(e => e.Name.LocalName == "vanish");
        if (vanish == null) return null;
        return Attribute(vanish, "val") is null or "1" or "true" or "on";
    }

    private static string? Attribute(XElement? element, string localName) =>
        element?.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;

    /// <summary>Сокращает локальные пути в кодах полей до имени файла.</summary>
    public static bool RewriteFieldPaths(XDocument document)
    {
        var changed = false;
        foreach (var simple in document.Descendants().Where(e => e.Name.LocalName == "fldSimple"))
        {
            var instruction = simple.Attributes().FirstOrDefault(a => a.Name.LocalName == "instr");
            if (instruction == null) continue;
            var rewritten = RewriteInstruction(instruction.Value);
            if (rewritten == instruction.Value) continue;
            instruction.Value = rewritten;
            changed = true;
        }

        // Инструкция сложного поля может быть разбита на несколько instrText; вложенные поля обрабатываются отдельно.
        var open = new Stack<(List<XElement> Parts, bool Separated)>();
        foreach (var element in document.Descendants().Where(e => e.Name.LocalName is "fldChar" or "instrText"))
        {
            if (element.Name.LocalName == "instrText")
            {
                if (open.Count > 0 && !open.Peek().Separated) open.Peek().Parts.Add(element);
                continue;
            }
            switch (Attribute(element, "fldCharType"))
            {
                case "begin":
                    open.Push((new List<XElement>(), false));
                    break;
                case "separate" when open.Count > 0:
                    var current = open.Pop();
                    open.Push((current.Parts, true));
                    break;
                case "end" when open.Count > 0:
                    changed |= RewriteComplexField(open.Pop().Parts);
                    break;
            }
        }
        return changed;
    }

    public static bool HasLocalFieldPaths(XDocument document) =>
        document.Descendants().Any(e => e.Name.LocalName == "fldSimple" &&
                                        RewriteInstruction(Attribute(e, "instr") ?? "") != (Attribute(e, "instr") ?? "")) ||
        document.Descendants().Where(e => e.Name.LocalName == "instrText").Any(e => RewriteInstruction(e.Value) != e.Value);

    private static bool RewriteComplexField(List<XElement> parts)
    {
        if (parts.Count == 0) return false;
        var instruction = string.Concat(parts.Select(p => p.Value));
        var rewritten = RewriteInstruction(instruction);
        if (rewritten == instruction) return false;
        parts[0].Value = rewritten;
        parts[0].SetAttributeValue(XNamespace.Xml + "space", "preserve");
        foreach (var part in parts.Skip(1)) part.Value = "";
        return true;
    }

    internal static string RewriteInstruction(string instruction)
    {
        var rewritten = QuotedArgument.Replace(instruction, match =>
        {
            var path = match.Groups[1].Value.Replace(@"\\", @"\");
            return OpenXmlPackageSanitizer.LooksLikeLocalOrRelativePath(path) ? "\"" + OpenXmlPackageSanitizer.FileNameOf(path) + "\"" : match.Value;
        });
        return UnquotedPath.Replace(rewritten, match => OpenXmlPackageSanitizer.FileNameOf(match.Value.Replace(@"\\", @"\")));
    }
}

/// <summary>Значения vanish стилей документа (null — стиль не задаёт свойство).</summary>
internal sealed class WordStyleVisibility(IReadOnlyDictionary<string, bool?> styles, string? defaultParagraphStyle, bool? defaultVanish)
{
    public static readonly WordStyleVisibility Empty = new(new Dictionary<string, bool?>(StringComparer.Ordinal), null, null);

    public string? DefaultParagraphStyle { get; } = defaultParagraphStyle;

    public bool? DefaultVanish { get; } = defaultVanish;

    public bool Contains(string styleId) => styles.ContainsKey(styleId);

    public bool? StyleVanish(string? styleId) => styleId != null && styles.TryGetValue(styleId, out var value) ? value : null;
}
