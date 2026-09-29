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

    private static readonly Regex UnquotedPath = new(@"(?<=^|\s)(?:[A-Za-z]:\\\\|\\\\\\\\|file:)\S+", RegexOptions.CultureInvariant);

    /// <summary>Части с текстом документа, где встречаются скрытый текст и поля.</summary>
    public static IEnumerable<OpenXmlPart> EnumerateContentParts(MainDocumentPart mainPart)
    {
        yield return mainPart;
        foreach (var part in mainPart.HeaderParts) yield return part;
        foreach (var part in mainPart.FooterParts) yield return part;
        if (mainPart.FootnotesPart != null) yield return mainPart.FootnotesPart;
        if (mainPart.EndnotesPart != null) yield return mainPart.EndnotesPart;
        if (mainPart.GlossaryDocumentPart != null) yield return mainPart.GlossaryDocumentPart;
    }

    /// <summary>Идентификаторы стилей, скрывающих текст (с учётом наследования basedOn).</summary>
    public static HashSet<string> LoadHiddenStyles(StyleDefinitionsPart? stylesPart)
    {
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        var styles = stylesPart == null ? null : OpenXmlPartXml.TryLoad(stylesPart);
        if (styles == null) return hidden;

        var definitions = styles.Descendants().Where(e => e.Name.LocalName == "style")
            .Select(e => (Id: Attribute(e, "styleId"), BasedOn: Attribute(e.Elements().FirstOrDefault(c => c.Name.LocalName == "basedOn"), "val"),
                Vanish: Vanish(e.Elements().FirstOrDefault(c => c.Name.LocalName == "rPr"))))
            .Where(d => d.Id != null)
            .GroupBy(d => d.Id!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var id in definitions.Keys)
        {
            var current = id;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (current != null && seen.Add(current) && definitions.TryGetValue(current, out var definition))
            {
                if (definition.Vanish != null)
                {
                    if (definition.Vanish == true) hidden.Add(id);
                    break;
                }
                current = definition.BasedOn;
            }
        }
        return hidden;
    }

    /// <summary>Прогоны скрытого текста, которые можно удалить, не нарушая структуру документа.</summary>
    public static List<XElement> FindHiddenRuns(XDocument document, HashSet<string> hiddenStyles) =>
        document.Descendants().Where(e => e.Name.LocalName == "r" && IsHidden(e, hiddenStyles) &&
                                          !e.Descendants().Any(d => StructuralRunContent.Contains(d.Name.LocalName)))
            .ToList();

    public static bool RemoveHiddenRuns(XDocument document, HashSet<string> hiddenStyles, out int removed)
    {
        var runs = FindHiddenRuns(document, hiddenStyles);
        removed = runs.Count;
        foreach (var run in runs) run.Remove();
        return removed > 0;
    }

    private static bool IsHidden(XElement run, HashSet<string> hiddenStyles)
    {
        var properties = run.Elements().FirstOrDefault(e => e.Name.LocalName == "rPr");
        var direct = Vanish(properties);
        if (direct != null) return direct.Value;
        var runStyle = Attribute(properties?.Elements().FirstOrDefault(e => e.Name.LocalName == "rStyle"), "val");
        if (runStyle != null && hiddenStyles.Contains(runStyle)) return true;
        var paragraph = run.Ancestors().FirstOrDefault(e => e.Name.LocalName == "p");
        var paragraphStyle = Attribute(paragraph?.Elements().FirstOrDefault(e => e.Name.LocalName == "pPr")?
            .Elements().FirstOrDefault(e => e.Name.LocalName == "pStyle"), "val");
        return paragraphStyle != null && hiddenStyles.Contains(paragraphStyle);
    }

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
            return OpenXmlPackageSanitizer.LooksLikeLocalPath(path) ? "\"" + OpenXmlPackageSanitizer.FileNameOf(path) + "\"" : match.Value;
        });
        return UnquotedPath.Replace(rewritten, match => OpenXmlPackageSanitizer.FileNameOf(match.Value.Replace(@"\\", @"\")));
    }
}
