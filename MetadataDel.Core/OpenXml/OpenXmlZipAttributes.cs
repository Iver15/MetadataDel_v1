using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace MetadataDel.Core.OpenXml;

/// <summary>
/// Пересобирает ZIP-контейнер OpenXML-пакета в новый архив: стабильный порядок записей, одинаковое время,
/// без комментариев архива и записей и без дополнительных полей (NTFS/UT-время, данные архиваторов).
/// Имена и содержимое записей сохраняются точно; лишние записи без ссылок удаляются.
/// </summary>
internal static class OpenXmlZipAttributes
{
    private const int RegularFileMode = unchecked((int)0x81A40000); // 0100644 << 16
    private const int DirectoryMode = 0x41ED0000 | 0x10; // 040755 << 16 и атрибут каталога MS-DOS
    private static readonly DateTimeOffset SanitizedLastWriteTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string ContentTypesName = "[Content_Types].xml";
    private const string PackageRelationshipsName = "_rels/.rels";

    public static void Normalize(string path)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "." + Guid.NewGuid().ToString("N") + ".mdel-zip");
        try
        {
            using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                Rewrite(source, target);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void Normalize(Stream stream)
    {
        using var source = new MemoryStream();
        stream.Position = 0;
        stream.CopyTo(source);
        source.Position = 0;
        stream.Position = 0;
        stream.SetLength(0);
        Rewrite(source, stream);
        stream.Position = 0;
    }

    private static void Rewrite(Stream source, Stream target)
    {
        using var input = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        var entries = input.Entries.ToList();
        var removed = FindUnreferencedEntries(entries);
        var contentTypes = removed.Count == 0 ? null : RewriteContentTypes(entries, removed);

        // [Content_Types].xml и _rels/.rels — первыми, как их записывает Office; остальное — в порядке имён.
        var kept = entries.Where(e => !removed.Contains(e))
            .Select((entry, index) => (Entry: entry, Index: index))
            .OrderBy(e => Rank(e.Entry.FullName))
            .ThenBy(e => e.Entry.FullName, StringComparer.Ordinal)
            .ThenBy(e => e.Index)
            .Select(e => e.Entry)
            .ToList();

        using var output = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var entry in kept)
        {
            var directory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
            var created = output.CreateEntry(entry.FullName, directory ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            created.LastWriteTime = SanitizedLastWriteTime;
            created.ExternalAttributes = directory ? DirectoryMode : RegularFileMode;
            using var destination = created.Open();
            if (directory) continue;
            if (contentTypes != null && IsContentTypes(entry.FullName))
            {
                destination.Write(contentTypes);
                continue;
            }
            using var data = entry.Open();
            data.CopyTo(destination);
        }
    }

    private static int Rank(string name) =>
        IsContentTypes(name) ? 0 :
        string.Equals(name, PackageRelationshipsName, StringComparison.OrdinalIgnoreCase) ? 1 : 2;

    private static bool IsContentTypes(string name) => string.Equals(name, ContentTypesName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Записи архива, на которые не ведёт ни одна связь: SDK их не видит и сохраняет как есть,
    /// а в них бывают старые миниатюры, свойства и «[trash]» прежних версий Office.
    /// Имена сравниваются точно и в раскодированном виде (%20 и т. п.), но сами записи не переименовываются.
    /// </summary>
    private static HashSet<ZipArchiveEntry> FindUnreferencedEntries(List<ZipArchiveEntry> entries)
    {
        var none = new HashSet<ZipArchiveEntry>();
        var files = entries.Where(e => !e.FullName.EndsWith("/", StringComparison.Ordinal))
            .GroupBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        if (!files.ContainsKey(ContentTypesName) || !files.ContainsKey(PackageRelationshipsName)) return none;

        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ContentTypesName };
        var pending = new Queue<string>();
        pending.Enqueue("");
        while (pending.Count > 0)
        {
            var source = pending.Dequeue();
            var directory = source.Contains('/') ? source[..source.LastIndexOf('/')] : "";
            var relationships = source.Length == 0
                ? PackageRelationshipsName
                : (directory.Length == 0 ? "" : directory + "/") + "_rels/" + source[(source.LastIndexOf('/') + 1)..] + ".rels";
            if (!files.TryGetValue(relationships, out var relationshipEntries)) continue;
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
                return none;
            }

            foreach (var relationship in document.Root?.Elements() ?? Enumerable.Empty<XElement>())
            {
                if ((string?)relationship.Attribute("TargetMode") == "External") continue;
                var target = (string?)relationship.Attribute("Target");
                if (string.IsNullOrEmpty(target)) continue;
                target = target.Split('#')[0];
                foreach (var candidate in new[] { Resolve(directory, target), Resolve(directory, Uri.UnescapeDataString(target)) })
                {
                    if (candidate != null && files.ContainsKey(candidate) && reachable.Add(candidate))
                        pending.Enqueue(files[candidate][0].FullName);
                }
            }
        }

        return files.Where(f => !reachable.Contains(f.Key)).SelectMany(f => f.Value).ToHashSet();
    }

    /// <summary>[Content_Types].xml без Override для удалённых записей; null — если его не удалось прочитать.</summary>
    private static byte[]? RewriteContentTypes(List<ZipArchiveEntry> entries, HashSet<ZipArchiveEntry> removed)
    {
        var contentTypes = entries.FirstOrDefault(e => IsContentTypes(e.FullName));
        if (contentTypes == null) return null;

        XDocument types;
        try
        {
            using var stream = contentTypes.Open();
            types = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return null;
        }

        var keptNames = Names(entries.Where(e => !removed.Contains(e)));
        var removedNames = Names(removed);
        var overrides = types.Root?.Elements().Where(e => e.Name.LocalName == "Override").Where(e =>
        {
            var partName = ((string?)e.Attribute("PartName") ?? "").TrimStart('/');
            var unescaped = Uri.UnescapeDataString(partName);
            return (removedNames.Contains(partName) || removedNames.Contains(unescaped)) &&
                   !keptNames.Contains(partName) && !keptNames.Contains(unescaped);
        }).ToList();
        if (overrides == null || overrides.Count == 0) return null;
        overrides.Remove();

        using var output = new MemoryStream();
        types.Save(output, SaveOptions.DisableFormatting);
        return output.ToArray();
    }

    private static HashSet<string> Names(IEnumerable<ZipArchiveEntry> entries)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            names.Add(entry.FullName);
            names.Add(Uri.UnescapeDataString(entry.FullName));
        }
        return names;
    }

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
