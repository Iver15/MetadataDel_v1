using System.IO.Compression;

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
}
