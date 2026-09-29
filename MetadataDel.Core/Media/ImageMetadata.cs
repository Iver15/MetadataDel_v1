using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace MetadataDel.Core.Media;

/// <summary>
/// Удаляет из изображений EXIF (включая GPS), XMP, IPTC, комментарии и встроенные миниатюры без перекодирования пикселей.
/// Поддерживаются JPEG, PNG, JPEG 2000, GIF, WebP и SVG.
/// </summary>
internal static class ImageMetadata
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    // tEXt/zTXt/iTXt — текстовые поля (автор, программа, XMP), eXIf — EXIF, tIME — дата изменения, dSIG — подпись исходного файла.
    private static readonly HashSet<string> PngMetadataChunks = new(StringComparer.Ordinal)
    {
        "tEXt", "zTXt", "iTXt", "eXIf", "tIME", "dSIG"
    };

    // JP2: XML/XMP, UUID-блоки (XMP, GeoJP2) и ссылки на внешние ресурсы.
    private static readonly HashSet<string> Jp2MetadataBoxes = new(StringComparer.Ordinal)
    {
        "xml ", "uuid", "uinf"
    };

    // Служебные пространства имён редакторов SVG: имя исходного файла, пути экспорта, данные для повторного редактирования.
    private static readonly string[] SvgEditorNamespacePrefixes =
    {
        "http://sodipodi.sourceforge.net/", "http://www.inkscape.org/", "http://ns.adobe.com/", "http://www.bohemiancoding.com/sketch",
        "http://www.serif.com/", "http://purl.org/dc/", "http://creativecommons.org/ns#", "http://web.resource.org/cc/",
        "http://www.w3.org/1999/02/22-rdf-syntax-ns#"
    };

    private static readonly Regex SvgRootPattern = new(@"^\uFEFF?\s*(?:<\?xml[^>]*\?>\s*)?(?:<!--.*?-->\s*|<!DOCTYPE[^\[>]*(?:\[.*?\])?\s*>\s*)*<svg[\s>]",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>Возвращает очищенную копию или <c>null</c>, если формат не распознан или метаданных нет.</summary>
    public static byte[]? Strip(byte[] data)
    {
        var stripped = IsJpeg(data) ? StripJpeg(data)
            : IsPng(data) ? StripPng(data)
            : IsJp2(data) ? StripJp2(data)
            : IsGif(data) ? StripGif(data)
            : IsWebp(data) ? StripWebp(data)
            : IsSvg(data) ? StripSvg(data)
            : null;
        return stripped == null || stripped.AsSpan().SequenceEqual(data) ? null : stripped;
    }

    /// <summary>Возвращает <c>true</c>, если изображение содержит удаляемые метаданные.</summary>
    public static bool HasMetadata(byte[] data) => Strip(data) != null;

    public static bool IsJpeg(ReadOnlySpan<byte> data) => data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    private static bool IsPng(ReadOnlySpan<byte> data) => data.Length > 8 && data[..8].SequenceEqual(PngSignature);

    private static bool IsJp2(ReadOnlySpan<byte> data) =>
        data.Length > 12 && BinaryPrimitives.ReadUInt32BigEndian(data) == 12 && data.Slice(4, 4).SequenceEqual("jP  "u8);

    private static bool IsGif(ReadOnlySpan<byte> data) => data.Length > 13 && (data[..6].SequenceEqual("GIF89a"u8) || data[..6].SequenceEqual("GIF87a"u8));

    private static bool IsWebp(ReadOnlySpan<byte> data) => data.Length > 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8);

    private static bool IsSvg(byte[] data) =>
        data.Length > 4 && SvgRootPattern.IsMatch(Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 4096)));

    private static byte[]? StripGif(byte[] data)
    {
        using var output = new MemoryStream(data.Length);
        var pos = 13;
        if ((data[10] & 0x80) != 0) pos += 3 << ((data[10] & 7) + 1);
        if (pos > data.Length) return null;
        output.Write(data, 0, pos);
        while (pos < data.Length)
        {
            var start = pos;
            switch (data[pos])
            {
                case 0x3B:
                    // Всё после завершающего байта отбрасывается.
                    output.WriteByte(0x3B);
                    return output.ToArray();
                case 0x21:
                    if (pos + 2 > data.Length) return null;
                    var label = data[pos + 1];
                    pos = SkipSubBlocks(data, pos + 2);
                    if (pos < 0) return null;
                    // Комментарии и блоки приложений (XMP и др.) удаляются; NETSCAPE/ANIMEXTS управляют повтором анимации.
                    var keep = label switch
                    {
                        0xFE => false,
                        0xFF => start + 14 <= data.Length && data[start + 2] == 11 &&
                                (data.AsSpan(start + 3, 11).SequenceEqual("NETSCAPE2.0"u8) || data.AsSpan(start + 3, 11).SequenceEqual("ANIMEXTS1.0"u8)),
                        _ => true
                    };
                    if (keep) output.Write(data, start, pos - start);
                    break;
                case 0x2C:
                    if (pos + 10 > data.Length) return null;
                    var packed = data[pos + 9];
                    pos += 10;
                    if ((packed & 0x80) != 0) pos += 3 << ((packed & 7) + 1);
                    pos = pos + 1 > data.Length ? -1 : SkipSubBlocks(data, pos + 1);
                    if (pos < 0) return null;
                    output.Write(data, start, pos - start);
                    break;
                default:
                    return null;
            }
        }
        return null;
    }

    private static int SkipSubBlocks(byte[] data, int pos)
    {
        while (pos < data.Length)
        {
            var size = data[pos];
            pos += 1 + size;
            if (size == 0) return pos <= data.Length ? pos : -1;
        }
        return -1;
    }

    private static byte[]? StripWebp(byte[] data)
    {
        using var output = new MemoryStream(data.Length);
        output.Write(data, 0, 12);
        var pos = 12;
        var vp8xOffset = -1;
        while (pos + 8 <= data.Length)
        {
            var type = Encoding.ASCII.GetString(data, pos, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4));
            var total = 8 + (long)size + (size & 1);
            if (pos + 8 + (long)size > data.Length) return null;
            total = Math.Min(total, data.Length - pos);
            if (type is not ("EXIF" or "XMP "))
            {
                if (type == "VP8X") vp8xOffset = (int)output.Position + 8;
                output.Write(data, pos, (int)total);
            }
            pos += (int)total;
        }
        var result = output.ToArray();
        // В VP8X сбрасываются флаги наличия EXIF (0x08) и XMP (0x04).
        if (vp8xOffset >= 0 && vp8xOffset < result.Length) result[vp8xOffset] &= unchecked((byte)~0x0C);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)(result.Length - 8));
        return result;
    }

    private static byte[]? StripSvg(byte[] data)
    {
        XDocument document;
        try
        {
            // Внутренние сущности нужны SVG из Illustrator; внешние ресурсы не загружаются.
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Parse, XmlResolver = null, MaxCharactersFromEntities = 1_000_000
            };
            using var reader = XmlReader.Create(new MemoryStream(data), settings);
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return null;
        }
        var root = document.Root;
        if (root == null) return null;

        static bool IsEditorNamespace(XNamespace ns) =>
            SvgEditorNamespacePrefixes.Any(prefix => ns.NamespaceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        var changed = false;
        foreach (var node in document.DescendantNodes().Where(n => n is XComment or XProcessingInstruction).ToList())
        {
            node.Remove();
            changed = true;
        }
        foreach (var element in root.DescendantsAndSelf()
                     .Where(e => e != root && (e.Name.LocalName == "metadata" || IsEditorNamespace(e.Name.Namespace))).ToList())
        {
            element.Remove();
            changed = true;
        }
        foreach (var attribute in root.DescendantsAndSelf().Attributes()
                     .Where(a => a.IsNamespaceDeclaration ? IsEditorNamespace(a.Value) : IsEditorNamespace(a.Name.Namespace)).ToList())
        {
            attribute.Remove();
            changed = true;
        }
        if (!changed) return null;

        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }))
            document.Save(writer);
        return output.ToArray();
    }

    private static byte[]? StripJpeg(byte[] data)
    {
        using var output = new MemoryStream(data.Length);
        output.Write(data, 0, 2);
        var pos = 2;
        while (pos + 1 < data.Length)
        {
            if (data[pos] != 0xFF) return null;
            var marker = data[pos + 1];
            if (marker == 0xFF) { pos++; continue; }
            if (marker == 0xD9)
            {
                // Всё после EOI (MPF-кадры, трейлеры камер, видео Motion Photo) отбрасывается.
                output.Write(data, pos, 2);
                return output.ToArray();
            }
            if (marker is >= 0xD0 and <= 0xD7 or 0x01)
            {
                output.Write(data, pos, 2);
                pos += 2;
                continue;
            }
            if (pos + 4 > data.Length) return null;
            var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos + 2));
            if (length < 2 || pos + 2 + length > data.Length) return null;
            var payload = data.AsSpan(pos + 4, length - 2);
            WriteJpegSegment(output, marker, payload);
            pos += 2 + length;

            if (marker != 0xDA) continue;
            // Энтропийно-кодированные данные идут до следующего маркера (кроме байтовой набивки и RSTn).
            var start = pos;
            while (pos + 1 < data.Length)
            {
                if (data[pos] != 0xFF) { pos++; continue; }
                var next = data[pos + 1];
                if (next == 0x00 || next is >= 0xD0 and <= 0xD7) { pos += 2; continue; }
                if (next == 0xFF) { pos++; continue; }
                break;
            }
            output.Write(data, start, pos - start);
        }
        // Файл без EOI: сохраняем прочитанное, как есть.
        if (pos < data.Length) output.Write(data, pos, data.Length - pos);
        return output.ToArray();
    }

    private static void WriteJpegSegment(Stream output, byte marker, ReadOnlySpan<byte> payload)
    {
        switch (marker)
        {
            case 0xE0 when payload.StartsWith("JFIF\0"u8) && payload.Length > 14:
                // Встроенная в JFIF миниатюра обнуляется, параметры плотности сохраняются.
                var jfif = payload[..14].ToArray();
                jfif[12] = 0;
                jfif[13] = 0;
                WriteSegment(output, marker, jfif);
                return;
            case 0xE0 when payload.StartsWith("JFXX\0"u8):
                return;
            case 0xE1:
                // EXIF и XMP удаляются; ориентация сохраняется, иначе фото может отобразиться повёрнутым.
                if (payload.StartsWith("Exif\0\0"u8) && TryReadExifOrientation(payload[6..], out var orientation) && orientation is >= 2 and <= 8)
                    WriteSegment(output, marker, BuildOrientationOnlyExif(orientation));
                return;
            case 0xE2 when !payload.StartsWith("ICC_PROFILE\0"u8):
                // Цветовой профиль нужен для отображения; FlashPix и MPF (индекс дополнительных кадров) — нет.
                return;
            case >= 0xE3 and <= 0xED:
            case 0xEF:
            case 0xFE:
                // APP3–APP13 (IPTC, Photoshop, Ducky и пр.), APP15 и COM-комментарии.
                return;
            default:
                WriteSegment(output, marker, payload);
                return;
        }
    }

    private static void WriteSegment(Stream output, byte marker, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[4];
        header[0] = 0xFF;
        header[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], checked((ushort)(payload.Length + 2)));
        output.Write(header);
        output.Write(payload);
    }

    private static bool TryReadExifOrientation(ReadOnlySpan<byte> tiff, out ushort orientation)
    {
        orientation = 0;
        if (tiff.Length < 8) return false;
        var littleEndian = tiff[0] == (byte)'I' && tiff[1] == (byte)'I';
        if (!littleEndian && !(tiff[0] == (byte)'M' && tiff[1] == (byte)'M')) return false;
        var ifd = littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(tiff[4..]) : BinaryPrimitives.ReadUInt32BigEndian(tiff[4..]);
        if (ifd > (uint)tiff.Length - 2) return false;
        var count = ReadU16(tiff, (int)ifd, littleEndian);
        for (var i = 0; i < count; i++)
        {
            var entry = (int)ifd + 2 + i * 12;
            if (entry + 12 > tiff.Length) return false;
            if (ReadU16(tiff, entry, littleEndian) != 0x0112) continue;
            if (ReadU16(tiff, entry + 2, littleEndian) != 3) return false;
            orientation = ReadU16(tiff, entry + 8, littleEndian);
            return true;
        }
        return false;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt16LittleEndian(data[offset..])
        : BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);

    private static byte[] BuildOrientationOnlyExif(ushort orientation)
    {
        var exif = new byte[6 + 8 + 2 + 12 + 4];
        "Exif\0\0"u8.CopyTo(exif);
        var tiff = exif.AsSpan(6);
        "MM\0*"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt32BigEndian(tiff[4..], 8);
        BinaryPrimitives.WriteUInt16BigEndian(tiff[8..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(tiff[10..], 0x0112);
        BinaryPrimitives.WriteUInt16BigEndian(tiff[12..], 3);
        BinaryPrimitives.WriteUInt32BigEndian(tiff[14..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(tiff[18..], orientation);
        return exif;
    }

    private static byte[]? StripPng(byte[] data)
    {
        using var output = new MemoryStream(data.Length);
        output.Write(data, 0, 8);
        var pos = 8;
        while (pos + 12 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            if (length > int.MaxValue || pos + 12 + (long)length > data.Length) return null;
            var type = Encoding.ASCII.GetString(data, pos + 4, 4);
            var total = 12 + (int)length;
            if (!PngMetadataChunks.Contains(type)) output.Write(data, pos, total);
            pos += total;
            if (type == "IEND") return output.ToArray();
        }
        return null;
    }

    private static byte[]? StripJp2(byte[] data)
    {
        using var output = new MemoryStream(data.Length);
        var pos = 0;
        while (pos + 8 <= data.Length)
        {
            long length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            var type = Encoding.ASCII.GetString(data, pos + 4, 4);
            if (length == 1)
            {
                if (pos + 16 > data.Length) return null;
                length = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(pos + 8));
            }
            else if (length == 0) length = data.Length - pos;
            if (length < 8 || pos + length > data.Length) return null;
            if (!Jp2MetadataBoxes.Contains(type)) output.Write(data, pos, (int)length);
            pos += (int)length;
        }
        return pos == data.Length ? output.ToArray() : null;
    }
}
