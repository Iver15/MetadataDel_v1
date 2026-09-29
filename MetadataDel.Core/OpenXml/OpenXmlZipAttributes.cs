using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace MetadataDel.Core.OpenXml;

internal static class OpenXmlZipAttributes
{
    private const int UnixModeMask = unchecked((int)0xFFFF0000);
    private const int LowerAttributesMask = 0x0000FFFF;
    private const int RegularFileMode = unchecked((int)0x81A40000); // 0100644 << 16
    private const int DirectoryMode = 0x41ED0000; // 040755 << 16
    private static readonly DateTimeOffset SanitizedLastWriteTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static void Normalize(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        Normalize(archive);
    }

    public static void Normalize(Stream stream)
    {
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true);
        Normalize(archive);
    }

    private static void Normalize(ZipArchive archive)
    {
        RemoveUnreferencedParts(archive);
        foreach (var entry in archive.Entries)
        {
            entry.LastWriteTime = SanitizedLastWriteTime;

            if ((entry.ExternalAttributes & UnixModeMask) == 0)
            {
                var mode = entry.FullName.EndsWith("/", StringComparison.Ordinal)
                    ? DirectoryMode
                    : RegularFileMode;

                entry.ExternalAttributes = (entry.ExternalAttributes & LowerAttributesMask) | mode;
            }
        }
    }

    /// <summary>
    /// Удаляет записи архива, на которые не ведёт ни одна связь: SDK их не видит и сохраняет как есть,
    /// а в них бывают старые миниатюры, свойства и «[trash]» прежних версий Office.
    /// </summary>
    private static void RemoveUnreferencedParts(ZipArchive archive)
    {
        var entries = archive.Entries.Where(e => !e.FullName.EndsWith("/", StringComparison.Ordinal))
            .GroupBy(e => Uri.UnescapeDataString(e.FullName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        if (!entries.ContainsKey(ContentTypesName) || !entries.ContainsKey("_rels/.rels")) return;

        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ContentTypesName };
        var pending = new Queue<string>();
        pending.Enqueue("");
        while (pending.Count > 0)
        {
            var source = pending.Dequeue();
            var directory = source.Contains('/') ? source[..source.LastIndexOf('/')] : "";
            var relationships = source.Length == 0
                ? "_rels/.rels"
                : (directory.Length == 0 ? "" : directory + "/") + "_rels/" + source[(source.LastIndexOf('/') + 1)..] + ".rels";
            if (!entries.TryGetValue(relationships, out var relationshipEntries)) continue;
            reachable.Add(relationships);

            XDocument document;
            try
            {
                using var stream = relationshipEntries[0].Open();
                document = XDocument.Load(stream);
            }
            catch (XmlException)
            {
                // Нечитаемые связи: не угадываем, что используется, и ничего не удаляем.
                return;
            }

            foreach (var relationship in document.Root?.Elements() ?? Enumerable.Empty<XElement>())
            {
                if ((string?)relationship.Attribute("TargetMode") == "External") continue;
                var target = (string?)relationship.Attribute("Target");
                if (string.IsNullOrEmpty(target)) continue;
                var resolved = Resolve(directory, Uri.UnescapeDataString(target.Split('#')[0]));
                if (resolved != null && entries.ContainsKey(resolved) && reachable.Add(resolved))
                    pending.Enqueue(resolved);
            }
        }

        var removed = entries.Where(e => !reachable.Contains(e.Key)).ToList();
        if (removed.Count == 0) return;
        foreach (var entry in removed.SelectMany(e => e.Value))
            entry.Delete();

        var contentTypes = entries[ContentTypesName][0];
        XDocument types;
        using (var stream = contentTypes.Open())
            types = XDocument.Load(stream);
        var removedNames = removed.Select(e => "/" + e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        types.Root?.Elements().Where(e => e.Name.LocalName == "Override" &&
            removedNames.Contains(Uri.UnescapeDataString((string?)e.Attribute("PartName") ?? ""))).Remove();
        contentTypes.Delete();
        using var output = archive.CreateEntry(ContentTypesName, CompressionLevel.Optimal).Open();
        types.Save(output, SaveOptions.DisableFormatting);
    }

    private const string ContentTypesName = "[Content_Types].xml";

    private static string? Resolve(string directory, string target)
    {
        var segments = new List<string>();
        var path = target.StartsWith("/", StringComparison.Ordinal) ? target.TrimStart('/') : (directory.Length == 0 ? target : directory + "/" + target);
        foreach (var segment in path.Replace('\\', '/').Split('/'))
        {
            if (segment is "" or ".") continue;
            if (segment == "..")
            {
                if (segments.Count == 0) return null;
                segments.RemoveAt(segments.Count - 1);
            }
            else segments.Add(segment);
        }
        return string.Join('/', segments);
    }
}
