using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace MetadataDel.Core.Media;

/// <summary>Итог очистки изображения.</summary>
internal enum ImageStripStatus
{
    /// <summary>Формат не распознан или удалять нечего: данные не менялись.</summary>
    Unchanged,

    /// <summary>Метаданные удалены, <see cref="ImageStripResult.Data"/> — очищенная копия.</summary>
    Stripped,

    /// <summary>
    /// Структура файла повреждена. Из читаемой части метаданные удалены (если было что удалять, копия — в
    /// <see cref="ImageStripResult.Data"/>), но нечитаемый остаток не проверен: вызывающий код должен предупредить пользователя.
    /// </summary>
    Malformed,

    /// <summary>Формат распознан, в нём есть метаданные, но удалять их без перекодирования MetadataDel не умеет (HEIC/AVIF, BigTIFF).</summary>
    UnsupportedWithMetadata
}

/// <summary>Результат <see cref="ImageMetadata.StripDetailed"/>.</summary>
/// <param name="Data">Очищенная копия или <c>null</c>, если байты менять не нужно (или нельзя).</param>
/// <param name="Status">Что произошло с изображением.</param>
/// <param name="ResidualMetadata">В сохранённых байтах остались (или могли остаться) метаданные.</param>
internal readonly record struct ImageStripResult(byte[]? Data, ImageStripStatus Status, bool ResidualMetadata = false);

/// <summary>
/// Удаляет из изображений EXIF (включая GPS), XMP, IPTC, комментарии и встроенные миниатюры без перекодирования пикселей.
/// Поддерживаются JPEG, PNG, JPEG 2000 (JP2 и голый кодовый поток), GIF, WebP, TIFF и SVG; в HEIC/AVIF метаданные только обнаруживаются.
/// EMF, WMF и BMP не меняются: стандартных полей метаданных в них нет.
/// </summary>
internal static class ImageMetadata
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    // Белый список: блоки, нужные для отображения (пиксели, палитра, цвет, HDR, плотность, анимация APNG).
    // Всё остальное — tEXt/zTXt/iTXt, eXIf, tIME, dSIG, caBX (C2PA/JUMBF), iDOT, prVW, mkBT/mkTS (Fireworks) и неизвестные
    // блоки — удаляется: так новые и частные блоки приложений не просачиваются.
    private static readonly HashSet<string> PngAllowedChunks = new(StringComparer.Ordinal)
    {
        "IHDR", "PLTE", "IDAT", "IEND", "tRNS", "cHRM", "gAMA", "iCCP", "sBIT", "sRGB", "cICP", "mDCV", "cLLI",
        "bKGD", "hIST", "pHYs", "sPLT", "acTL", "fcTL", "fdAT"
    };

    // JP2: XML/XMP, UUID-блоки (XMP, GeoJP2) и ссылки на внешние ресурсы.
    private static readonly HashSet<string> Jp2MetadataBoxes = new(StringComparer.Ordinal)
    {
        "xml ", "uuid", "uinf"
    };

    // TIFF: описательные теги, сведения о камере и программе, автор и указатели на EXIF/GPS/Interop, XMP, IPTC, Photoshop, XP*.
    private static readonly HashSet<ushort> TiffMetadataTags = new()
    {
        269, 270, 271, 272, 305, 306, 315, 316, 700, 33432, 33723, 34377, 34665, 34853, 37724, 40091, 40092, 40093, 40094, 40095,
        40965, 50341
    };

    // Теги-указатели на вложенные IFD, содержимое которых обнуляется вместе с указателем.
    private static readonly HashSet<ushort> TiffSubIfdPointers = new() { 34665, 34853, 40965 };

    private static readonly HashSet<string> HeifBrands = new(StringComparer.Ordinal)
    {
        "heic", "heix", "heim", "heis", "hevc", "hevx", "mif1", "mif2", "msf1", "avif", "avis"
    };

    // Признаки метаданных в непроверенном остатке повреждённого файла.
    private static readonly byte[][] MetadataSignatures =
    {
        "Exif\0\0"u8.ToArray(), "<x:xmpmeta"u8.ToArray(), "<?xpacket"u8.ToArray(), "http://ns.adobe.com/xap/1.0/"u8.ToArray(),
        "Photoshop 3.0"u8.ToArray(), "8BIM"u8.ToArray(), "tEXt"u8.ToArray(), "zTXt"u8.ToArray(), "iTXt"u8.ToArray(), "eXIf"u8.ToArray(),
        "caBX"u8.ToArray(), "jumb"u8.ToArray(), "c2pa"u8.ToArray(), "<metadata"u8.ToArray(), "sodipodi"u8.ToArray(), "inkscape:"u8.ToArray()
    };

    // Служебные пространства имён редакторов SVG: имя исходного файла, пути экспорта, данные для повторного редактирования.
    private static readonly string[] SvgEditorNamespacePrefixes =
    {
        "http://sodipodi.sourceforge.net/", "http://www.inkscape.org/", "http://ns.adobe.com/", "http://www.bohemiancoding.com/sketch",
        "http://www.serif.com/", "http://purl.org/dc/", "http://creativecommons.org/ns#", "http://web.resource.org/cc/",
        "http://www.w3.org/1999/02/22-rdf-syntax-ns#"
    };

    private static readonly Regex SvgRootPattern = new(@"^﻿?\s*(?:<\?xml[^>]*\?>\s*)?(?:<!--.*?-->\s*|<!DOCTYPE[^\[>]*(?:\[.*?\])?\s*>\s*)*<svg[\s>]",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>Промежуточный итог разбора: копия (или <c>null</c>), признак повреждения и подозрение на остаток метаданных.</summary>
    private readonly record struct Outcome(byte[]? Data, bool Malformed = false, bool Residual = false);

    /// <summary>Возвращает очищенную копию или <c>null</c>, если формат не распознан, метаданных нет или удалить их нельзя.</summary>
    public static byte[]? Strip(byte[] data) => StripDetailed(data).Data;

    /// <summary>
    /// Возвращает <c>true</c>, если изображение содержит удаляемые метаданные, метаданные в неподдерживаемом формате
    /// или повреждённый остаток с признаками метаданных.
    /// </summary>
    public static bool HasMetadata(byte[] data)
    {
        var result = StripDetailed(data);
        return result.Data != null || result.ResidualMetadata;
    }

    /// <summary>
    /// Подробная очистка: кроме копии сообщает, был ли файл повреждён или метаданные остались в неподдерживаемом формате.
    /// <see cref="Strip"/> — тонкая обёртка над этим методом.
    /// </summary>
    internal static ImageStripResult StripDetailed(byte[] data)
    {
        if (IsHeif(data))
            return InspectHeif(data) ? new ImageStripResult(null, ImageStripStatus.UnsupportedWithMetadata, true) : default;
        if (IsBigTiff(data))
            return new ImageStripResult(null, ImageStripStatus.UnsupportedWithMetadata, true);

        Outcome outcome;
        if (IsJpeg(data)) outcome = StripJpeg(data);
        else if (IsPng(data)) outcome = StripPng(data);
        else if (IsJp2(data)) outcome = StripJp2(data);
        else if (IsJ2kCodestream(data)) outcome = StripJ2kCodestream(data);
        else if (IsGif(data)) outcome = StripGif(data);
        else if (IsWebp(data)) outcome = StripWebp(data);
        else if (IsTiff(data)) outcome = StripTiff(data);
        else if (IsSvg(data)) outcome = StripSvg(data);
        else return default;

        var changed = outcome.Data != null && !outcome.Data.AsSpan().SequenceEqual(data) ? outcome.Data : null;
        var status = outcome.Malformed ? ImageStripStatus.Malformed : changed != null ? ImageStripStatus.Stripped : ImageStripStatus.Unchanged;
        return new ImageStripResult(changed, status, outcome.Malformed && outcome.Residual);
    }

    public static bool IsJpeg(ReadOnlySpan<byte> data) => data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    private static bool IsPng(ReadOnlySpan<byte> data) => data.Length > 8 && data[..8].SequenceEqual(PngSignature);

    private static bool IsJp2(ReadOnlySpan<byte> data) =>
        data.Length > 12 && BinaryPrimitives.ReadUInt32BigEndian(data) == 12 && data.Slice(4, 4).SequenceEqual("jP  "u8);

    // Голый кодовый поток JPEG 2000 (так JPXDecode-изображения обычно хранятся в PDF): SOC, затем SIZ.
    private static bool IsJ2kCodestream(ReadOnlySpan<byte> data) =>
        data.Length > 4 && data[0] == 0xFF && data[1] == 0x4F && data[2] == 0xFF && data[3] == 0x51;

    private static bool IsGif(ReadOnlySpan<byte> data) => data.Length > 13 && (data[..6].SequenceEqual("GIF89a"u8) || data[..6].SequenceEqual("GIF87a"u8));

    private static bool IsWebp(ReadOnlySpan<byte> data) => data.Length > 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8);

    private static bool IsTiff(ReadOnlySpan<byte> data) =>
        data.Length >= 8 && (data[..4].SequenceEqual("II*\0"u8) || data[..4].SequenceEqual("MM\0*"u8));

    private static bool IsBigTiff(ReadOnlySpan<byte> data) =>
        data.Length >= 16 && (data[..4].SequenceEqual("II+\0"u8) || data[..4].SequenceEqual("MM\0+"u8));

    private static bool IsSvg(byte[] data) =>
        data.Length > 4 && SvgRootPattern.IsMatch(Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 4096)));

    private static bool IsHeif(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16 || !data.Slice(4, 4).SequenceEqual("ftyp"u8)) return false;
        var size = BinaryPrimitives.ReadUInt32BigEndian(data);
        if (size < 16 || size > data.Length) return false;
        if (HeifBrands.Contains(Encoding.ASCII.GetString(data.Slice(8, 4)))) return true;
        for (var offset = 16; offset + 4 <= size; offset += 4)
            if (HeifBrands.Contains(Encoding.ASCII.GetString(data.Slice(offset, 4)))) return true;
        return false;
    }

    private static bool ContainsMetadataSignature(ReadOnlySpan<byte> data)
    {
        foreach (var signature in MetadataSignatures)
            if (data.IndexOf(signature) >= 0) return true;
        return false;
    }

    /// <summary>Оборванный хвост сохраняется как есть; признаки метаданных в нём отмечаются для предупреждения.</summary>
    private static Outcome KeepTail(MemoryStream output, byte[] data, int from)
    {
        output.Write(data, from, data.Length - from);
        return new Outcome(output.ToArray(), true, ContainsMetadataSignature(data.AsSpan(from)));
    }

    private static Outcome StripGif(byte[] data)
    {
        using var output = new MemoryStream(data.Length);
        var pos = 13;
        if ((data[10] & 0x80) != 0) pos += 3 << ((data[10] & 7) + 1);
        if (pos > data.Length) return new Outcome(null, true, ContainsMetadataSignature(data));
        output.Write(data, 0, pos);

        // Оборванный служебный блок отбрасывается целиком, а файл закрывается завершающим байтом.
        Outcome Truncate()
        {
            output.WriteByte(0x3B);
            return new Outcome(output.ToArray(), true);
        }

        while (pos < data.Length)
        {
            var start = pos;
            switch (data[pos])
            {
                case 0x3B:
                    // Всё после завершающего байта отбрасывается.
                    output.WriteByte(0x3B);
                    return new Outcome(output.ToArray());
                case 0x21:
                    if (pos + 2 > data.Length) return Truncate();
                    var label = data[pos + 1];
                    // Комментарии и блоки приложений (XMP и др.) удаляются; NETSCAPE/ANIMEXTS управляют повтором анимации.
                    var keep = label switch
                    {
                        0xFE => false,
                        0xFF => start + 14 <= data.Length && data[start + 2] == 11 &&
                                (data.AsSpan(start + 3, 11).SequenceEqual("NETSCAPE2.0"u8) || data.AsSpan(start + 3, 11).SequenceEqual("ANIMEXTS1.0"u8)),
                        _ => true
                    };
                    pos = SkipSubBlocks(data, pos + 2);
                    if (pos < 0) return keep ? KeepTail(output, data, start) : Truncate();
                    if (keep) output.Write(data, start, pos - start);
                    break;
                case 0x2C:
                    if (pos + 10 > data.Length) return KeepTail(output, data, start);
                    var packed = data[pos + 9];
                    pos += 10;
                    if ((packed & 0x80) != 0) pos += 3 << ((packed & 7) + 1);
                    pos = pos + 1 > data.Length ? -1 : SkipSubBlocks(data, pos + 1);
                    // Оборванные данные кадра сохраняются: декодеры показывают прочитанную часть.
                    if (pos < 0) return KeepTail(output, data, start);
                    output.Write(data, start, pos - start);
                    break;
                default:
                    // Неизвестный байт вместо блока: остаток разобрать нельзя, он отбрасывается.
                    return Truncate();
            }
        }
        return Truncate();
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

    private static Outcome StripWebp(byte[] data)
    {
        using var output = new MemoryStream(data.Length);
        output.Write(data, 0, 12);
        var pos = 12;
        var vp8xOffset = -1;
        var malformed = false;
        var residual = false;
        while (pos + 8 <= data.Length)
        {
            var type = Encoding.ASCII.GetString(data, pos, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4));
            var metadata = type is "EXIF" or "XMP ";
            if (pos + 8 + (long)size > data.Length)
            {
                // Оборванный блок: EXIF/XMP отбрасываются, данные изображения сохраняются как есть.
                malformed = true;
                if (!metadata)
                {
                    if (type == "VP8X") vp8xOffset = (int)output.Position + 8;
                    output.Write(data, pos, data.Length - pos);
                    residual = ContainsMetadataSignature(data.AsSpan(pos));
                }
                pos = data.Length;
                break;
            }
            var total = (int)Math.Min(8 + (long)size + (size & 1), data.Length - pos);
            if (!metadata)
            {
                if (type == "VP8X") vp8xOffset = (int)output.Position + 8;
                output.Write(data, pos, total);
            }
            pos += total;
        }
        // Остаток короче заголовка блока — мусор, он отбрасывается.
        if (pos < data.Length) malformed = true;
        var result = output.ToArray();
        // В VP8X сбрасываются флаги наличия EXIF (0x08) и XMP (0x04).
        if (vp8xOffset >= 0 && vp8xOffset < result.Length) result[vp8xOffset] &= unchecked((byte)~0x0C);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)(result.Length - 8));
        return new Outcome(result, malformed, residual);
    }

    private static Outcome StripSvg(byte[] data)
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
            return new Outcome(null, true, ContainsMetadataSignature(data));
        }
        var root = document.Root;
        if (root == null) return new Outcome(null);

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
        if (!changed) return new Outcome(null);

        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }))
            document.Save(writer);
        return new Outcome(output.ToArray());
    }

    private static Outcome StripJpeg(byte[] data)
    {
        using var output = new MemoryStream(data.Length);
        output.Write(data, 0, 2);
        var pos = 2;
        var malformed = false;
        while (pos + 1 < data.Length)
        {
            if (data[pos] != 0xFF)
            {
                // Посторонние байты между сегментами декодеры пропускают до следующего маркера — так же поступаем и мы.
                malformed = true;
                var next = data.AsSpan(pos).IndexOf((byte)0xFF);
                if (next < 0) return new Outcome(output.ToArray(), true);
                pos += next;
                continue;
            }
            var marker = data[pos + 1];
            if (marker == 0xFF) { pos++; continue; }
            if (marker == 0x00) { malformed = true; pos += 2; continue; }
            if (marker == 0xD9)
            {
                // Всё после EOI (MPF-кадры, трейлеры камер, видео Motion Photo) отбрасывается.
                output.Write(data, pos, 2);
                return new Outcome(output.ToArray(), malformed);
            }
            if (marker is >= 0xD0 and <= 0xD7 or 0x01)
            {
                output.Write(data, pos, 2);
                pos += 2;
                continue;
            }
            if (pos + 4 > data.Length) return new Outcome(output.ToArray(), true);
            var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos + 2));
            if (length < 2 || pos + 2 + length > data.Length)
            {
                // Оборванный сегмент приложения или комментарий отбрасывается вместе с остатком файла
                // (изображения после него всё равно нет); оборванные таблицы и данные кадра сохраняются как есть.
                return marker is >= 0xE0 and <= 0xEF or 0xFE ? new Outcome(output.ToArray(), true) : KeepTail(output, data, pos);
            }
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
        return new Outcome(output.ToArray(), malformed);
    }

    private static void WriteJpegSegment(Stream output, byte marker, ReadOnlySpan<byte> payload)
    {
        switch (marker)
        {
            case 0xE0 when payload.StartsWith("JFIF\0"u8):
                if (payload.Length < 14)
                {
                    WriteSegment(output, marker, payload);
                    return;
                }
                // Встроенная в JFIF миниатюра обнуляется, параметры плотности сохраняются.
                var jfif = payload[..14].ToArray();
                jfif[12] = 0;
                jfif[13] = 0;
                WriteSegment(output, marker, jfif);
                return;
            case 0xE0:
                // Остальные APP0 (JFXX-миниатюры, AVI1 и пр.) для отображения не нужны.
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
                // APP3–APP13 (IPTC, Photoshop, Ducky и пр.), APP15 и COM-комментарии. APP14 (Adobe) задаёт цветовое преобразование.
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

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt32LittleEndian(data[offset..])
        : BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);

    private static void WriteU16(Span<byte> data, int offset, ushort value, bool littleEndian)
    {
        if (littleEndian) BinaryPrimitives.WriteUInt16LittleEndian(data[offset..], value);
        else BinaryPrimitives.WriteUInt16BigEndian(data[offset..], value);
    }

    private static void WriteU32(Span<byte> data, int offset, uint value, bool littleEndian)
    {
        if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(data[offset..], value);
        else BinaryPrimitives.WriteUInt32BigEndian(data[offset..], value);
    }

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

    private static Outcome StripPng(byte[] data)
    {
        using var output = new MemoryStream(data.Length);
        output.Write(data, 0, 8);
        var pos = 8;
        while (pos + 12 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            var type = Encoding.ASCII.GetString(data, pos + 4, 4);
            if (length > int.MaxValue || pos + 12 + (long)length > data.Length)
            {
                // Оборванный блок: данные изображения сохраняются (декодеры покажут прочитанное), остальное отбрасывается.
                return PngAllowedChunks.Contains(type) ? KeepTail(output, data, pos) : new Outcome(output.ToArray(), true);
            }
            var total = 12 + (int)length;
            if (PngAllowedChunks.Contains(type)) output.Write(data, pos, total);
            pos += total;
            if (type == "IEND") return new Outcome(output.ToArray());
        }
        // Нет IEND: остаток короче заголовка блока отбрасывается.
        return new Outcome(output.ToArray(), true);
    }

    private static Outcome StripJp2(byte[] data)
    {
        using var output = new MemoryStream(data.Length);
        // При таблице фрагментов (ftbl) кодовый поток адресуется смещениями — его не трогаем.
        var rewriteCodestream = data.AsSpan().IndexOf("ftbl"u8) < 0;
        var pos = 0;
        while (pos + 8 <= data.Length)
        {
            long length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            var type = Encoding.ASCII.GetString(data, pos + 4, 4);
            var header = 8;
            var toEnd = false;
            if (length == 1)
            {
                if (pos + 16 > data.Length) return new Outcome(output.ToArray(), true);
                length = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(pos + 8));
                header = 16;
            }
            else if (length == 0)
            {
                length = data.Length - pos;
                toEnd = true;
            }
            if (length < header || pos + length > data.Length)
            {
                // Оборванный блок: служебные блоки отбрасываются, кодовый поток и прочее сохраняются как есть.
                if (length < header || Jp2MetadataBoxes.Contains(type)) return new Outcome(output.ToArray(), true);
                return KeepTail(output, data, pos);
            }
            if (type == "jp2c" && rewriteCodestream)
            {
                var codestream = data.AsSpan(pos + header, (int)length - header).ToArray();
                var stripped = StripJ2kCodestream(codestream);
                var body = stripped.Data ?? codestream;
                WriteJp2Box(output, "jp2c", body, header == 16, toEnd);
                if (stripped.Malformed) return new Outcome(output.ToArray(), true, stripped.Residual);
            }
            else if (!Jp2MetadataBoxes.Contains(type)) output.Write(data, pos, (int)length);
            pos += (int)length;
        }
        return pos == data.Length ? new Outcome(output.ToArray()) : new Outcome(output.ToArray(), true);
    }

    private static void WriteJp2Box(Stream output, string type, byte[] body, bool extended, bool toEnd)
    {
        Span<byte> header = stackalloc byte[16];
        Encoding.ASCII.GetBytes(type).CopyTo(header[4..]);
        if (extended)
        {
            BinaryPrimitives.WriteUInt32BigEndian(header, 1);
            BinaryPrimitives.WriteUInt64BigEndian(header[8..], (ulong)body.Length + 16);
            output.Write(header);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(header, toEnd ? 0 : checked((uint)body.Length + 8));
            output.Write(header[..8]);
        }
        output.Write(body);
    }

    /// <summary>
    /// Удаляет COM-сегменты основного заголовка кодового потока JPEG 2000 (до первого SOT). Длины частей тайлов (Psot, TLM)
    /// и пакетов (PLM, PPM) считаются от SOT, поэтому сокращение основного заголовка их не затрагивает.
    /// </summary>
    private static Outcome StripJ2kCodestream(byte[] data)
    {
        if (!IsJ2kCodestream(data)) return new Outcome(null);
        using var output = new MemoryStream(data.Length);
        output.Write(data, 0, 2);
        var pos = 2;
        while (pos + 4 <= data.Length)
        {
            if (data[pos] != 0xFF) return KeepTail(output, data, pos);
            var marker = data[pos + 1];
            // SOT или SOD: основной заголовок закончился.
            if (marker is 0x90 or 0x93) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos + 2));
            if (length < 2 || pos + 2 + length > data.Length)
                return marker == 0x64 ? new Outcome(output.ToArray(), true) : KeepTail(output, data, pos);
            if (marker != 0x64) output.Write(data, pos, 2 + length);
            pos += 2 + length;
        }
        output.Write(data, pos, data.Length - pos);
        return new Outcome(output.ToArray());
    }

    /// <summary>
    /// TIFF чистится на месте: из каждой IFD цепочки удаляются записи тегов метаданных (список остаётся отсортированным,
    /// указатель на следующую IFD переносится вслед за ним), а их значения и вложенные EXIF/GPS-IFD обнуляются.
    /// Смещения остальных данных не меняются.
    /// </summary>
    private static Outcome StripTiff(byte[] data)
    {
        var le = data[0] == (byte)'I';
        var output = (byte[])data.Clone();
        var malformed = false;

        // Сначала собираем цепочку IFD и диапазоны, которые обнулять нельзя (сами IFD, пиксели, значения остающихся тегов).
        var ifds = new List<int>();
        var visited = new HashSet<int>();
        var protectedRanges = new List<(long Start, long End)>();
        long next = ReadU32(data, 4, le);
        while (next != 0)
        {
            if (next > data.Length - 2 || !visited.Add((int)next) || ifds.Count >= 10_000)
            {
                malformed = true;
                break;
            }
            var ifd = (int)next;
            var count = ReadU16(data, ifd, le);
            if (ifd + 2 + count * 12L + 4 > data.Length)
            {
                malformed = true;
                break;
            }
            ifds.Add(ifd);
            protectedRanges.Add((ifd, ifd + 2 + count * 12L + 4));
            for (var i = 0; i < count; i++)
            {
                var entry = ifd + 2 + i * 12;
                var tag = ReadU16(data, entry, le);
                if (TiffMetadataTags.Contains(tag)) continue;
                if (TryGetTiffValueRange(data, entry, le, out var range)) protectedRanges.Add(range);
            }
            AddTiffDataRanges(data, ifd, count, le, protectedRanges);
            next = ReadU32(data, ifd + 2 + count * 12, le);
        }

        bool IsProtected((long Start, long End) range) => protectedRanges.Any(p => range.Start < p.End && p.Start < range.End);

        void Zero((long Start, long End) range)
        {
            if (range.Start >= 0 && range.End <= output.Length && range.End > range.Start && !IsProtected(range))
                output.AsSpan((int)range.Start, (int)(range.End - range.Start)).Clear();
        }

        void ZeroSubIfd(long offset, int depth)
        {
            if (depth > 4 || offset <= 0 || offset > data.Length - 2) return;
            var count = ReadU16(data, (int)offset, le);
            var end = offset + 2 + count * 12L + 4;
            if (end > data.Length || IsProtected((offset, end))) return;
            for (var i = 0; i < count; i++)
            {
                var entry = (int)offset + 2 + i * 12;
                if (TryGetTiffValueRange(data, entry, le, out var range)) Zero(range);
                var tag = ReadU16(data, entry, le);
                if (TiffSubIfdPointers.Contains(tag)) ZeroSubIfd(ReadU32(data, entry + 8, le), depth + 1);
            }
            Zero((offset, end));
        }

        foreach (var ifd in ifds)
        {
            var count = ReadU16(data, ifd, le);
            var kept = new List<int>(count);
            for (var i = 0; i < count; i++)
            {
                var entry = ifd + 2 + i * 12;
                var tag = ReadU16(data, entry, le);
                if (!TiffMetadataTags.Contains(tag))
                {
                    kept.Add(entry);
                    continue;
                }
                if (TryGetTiffValueRange(data, entry, le, out var range)) Zero(range);
                if (TiffSubIfdPointers.Contains(tag)) ZeroSubIfd(ReadU32(data, entry + 8, le), 0);
            }
            if (kept.Count == count) continue;
            var nextIfd = ReadU32(data, ifd + 2 + count * 12, le);
            var span = output.AsSpan(ifd, 2 + count * 12 + 4);
            var entries = kept.Select(entry => data.AsSpan(entry, 12).ToArray()).ToList();
            span.Clear();
            WriteU16(span, 0, (ushort)entries.Count, le);
            for (var i = 0; i < entries.Count; i++) entries[i].CopyTo(span[(2 + i * 12)..]);
            WriteU32(span, 2 + entries.Count * 12, nextIfd, le);
        }

        return new Outcome(output, malformed, malformed);
    }

    private static int TiffTypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 or 13 => 4,
        5 or 10 or 12 or 16 or 17 or 18 => 8,
        _ => 0
    };

    /// <summary>Диапазон значения записи IFD, если оно не помещается в саму запись.</summary>
    private static bool TryGetTiffValueRange(byte[] data, int entry, bool le, out (long Start, long End) range)
    {
        range = default;
        var size = TiffTypeSize(ReadU16(data, entry + 2, le)) * (long)ReadU32(data, entry + 4, le);
        if (size <= 4) return false;
        long offset = ReadU32(data, entry + 8, le);
        if (offset + size > data.Length) return false;
        range = (offset, offset + size);
        return true;
    }

    /// <summary>Полосы, тайлы и JPEG-миниатюра остающихся IFD защищены от обнуления.</summary>
    private static void AddTiffDataRanges(byte[] data, int ifd, int count, bool le, List<(long, long)> ranges)
    {
        foreach (var (offsetTag, lengthTag) in new (ushort, ushort)[] { (273, 279), (324, 325), (513, 514) })
        {
            int offsetsEntry = -1, lengthsEntry = -1;
            for (var i = 0; i < count; i++)
            {
                var entry = ifd + 2 + i * 12;
                var tag = ReadU16(data, entry, le);
                if (tag == offsetTag) offsetsEntry = entry;
                else if (tag == lengthTag) lengthsEntry = entry;
            }
            if (offsetsEntry < 0 || lengthsEntry < 0) continue;
            var offsets = ReadTiffValues(data, offsetsEntry, le);
            var lengths = ReadTiffValues(data, lengthsEntry, le);
            for (var i = 0; i < Math.Min(offsets.Count, lengths.Count); i++) ranges.Add((offsets[i], offsets[i] + lengths[i]));
        }
    }

    private static List<long> ReadTiffValues(byte[] data, int entry, bool le)
    {
        var type = ReadU16(data, entry + 2, le);
        var count = ReadU32(data, entry + 4, le);
        var size = TiffTypeSize(type);
        var values = new List<long>();
        if (size is not (2 or 4) || count > 1_000_000) return values;
        long offset = size * count <= 4 ? entry + 8 : ReadU32(data, entry + 8, le);
        if (offset + size * (long)count > data.Length) return values;
        for (var i = 0; i < count; i++)
            values.Add(size == 2 ? ReadU16(data, (int)(offset + i * 2), le) : ReadU32(data, (int)(offset + i * 4), le));
        return values;
    }

    /// <summary>
    /// HEIC/AVIF: ищет в <c>meta/iinf</c> элемент <c>Exif</c> или XMP (<c>mime application/rdf+xml</c>).
    /// Удалить их без перестройки <c>iloc</c> нельзя, поэтому изображение только помечается.
    /// </summary>
    private static bool InspectHeif(byte[] data)
    {
        try
        {
            foreach (var (type, start, end) in IsoBoxes(data, 0, data.Length))
            {
                if (type != "meta") continue;
                foreach (var (childType, childStart, childEnd) in IsoBoxes(data, start + 4, end))
                {
                    if (childType != "iinf") continue;
                    var version = data[childStart];
                    var entries = childStart + 4 + (version == 0 ? 2 : 4);
                    foreach (var (entryType, entryStart, entryEnd) in IsoBoxes(data, entries, childEnd))
                    {
                        if (entryType != "infe" || data[entryStart] < 2) continue;
                        var at = entryStart + 4 + (data[entryStart] == 2 ? 2 : 4) + 2;
                        if (at + 4 > entryEnd) continue;
                        var itemType = Encoding.ASCII.GetString(data, at, 4);
                        if (itemType == "Exif") return true;
                        if (itemType != "mime") continue;
                        var name = Array.IndexOf(data, (byte)0, at + 4, entryEnd - at - 4);
                        if (name < 0) continue;
                        var contentType = Encoding.ASCII.GetString(data, name + 1, Math.Max(0, entryEnd - name - 1)).TrimEnd('\0');
                        if (contentType.StartsWith("application/rdf+xml", StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
        {
            // Повреждённая структура: решаем по сигнатурам ниже.
        }
        return ContainsMetadataSignature(data);
    }

    /// <summary>Перечисляет блоки ISOBMFF в диапазоне: тип, начало содержимого, конец блока.</summary>
    private static IEnumerable<(string Type, int Start, int End)> IsoBoxes(byte[] data, int from, int to)
    {
        var pos = from;
        while (pos + 8 <= to)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            var type = Encoding.ASCII.GetString(data, pos + 4, 4);
            var header = 8;
            if (size == 1)
            {
                if (pos + 16 > to) yield break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(pos + 8));
                header = 16;
            }
            else if (size == 0) size = to - pos;
            if (size < header || pos + size > to) yield break;
            yield return (type, pos + header, (int)(pos + size));
            pos += (int)size;
        }
    }
}
