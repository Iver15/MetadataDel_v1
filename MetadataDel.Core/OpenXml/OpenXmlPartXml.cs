using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;

namespace MetadataDel.Core.OpenXml;

internal static class OpenXmlPartXml
{
    public static bool Update(OpenXmlPart part, Func<XDocument, bool> mutate)
    {
        using var stream = part.GetStream(FileMode.Open, FileAccess.ReadWrite);
        if (stream.Length == 0)
        {
            return false;
        }

        var document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        var changed = mutate(document);
        if (!changed)
        {
            return false;
        }

        stream.SetLength(0);
        stream.Position = 0;
        document.Save(stream, SaveOptions.DisableFormatting);
        return true;
    }

    public static XDocument? TryLoad(OpenXmlPart part)
    {
        using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
        if (stream.Length == 0)
        {
            return null;
        }

        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }
}
