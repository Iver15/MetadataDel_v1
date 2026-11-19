using System.Buffers.Binary;
using MetadataDel.Core.Media;

namespace MetadataDel.Core.Ole;

/// <summary>
/// Изображения внутри документов Word и Excel 97–2003 хранятся записями OfficeArt (BLIP): в потоках Data
/// и WordDocument, в таблице OfficeArt Word и в записи MsoDrawingGroup книги Excel. JPEG и PNG в них могут
/// нести EXIF (с GPS), XMP и комментарии. Удалить их без переразметки контейнеров OfficeArt (размеры записей,
/// смещения BStore) нельзя, поэтому здесь они только обнаруживаются.
/// </summary>
internal static class OleImages
{
    private const ushort BlipJpeg = 0xF01D;
    private const ushort BlipPng = 0xF01E;
    private const ushort BlipJpegCmyk = 0xF02A;

    /// <summary>Есть ли среди записей BLIP изображение JPEG/PNG с метаданными.</summary>
    public static bool HasMetadata(ReadOnlySpan<byte> data)
    {
        for (var position = 0; position + 8 <= data.Length; position++)
        {
            var recordType = BinaryPrimitives.ReadUInt16LittleEndian(data[(position + 2)..]);
            if (recordType is not (BlipJpeg or BlipPng or BlipJpegCmyk)) continue;
            var verInstance = BinaryPrimitives.ReadUInt16LittleEndian(data[position..]);
            if ((verInstance & 0x000F) != 0) continue;
            // recInstance: 0x46A/0x6E2/0x6E0 — one 16-byte UID, 0x46B/0x6E3/0x6E1 — two; then a 1-byte tag.
            var uids = (verInstance >> 4) switch
            {
                0x46A or 0x6E2 or 0x6E0 => 1,
                0x46B or 0x6E3 or 0x6E1 => 2,
                _ => 0
            };
            if (uids == 0) continue;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(data[(position + 4)..]);
            var start = position + 8 + 16 * uids + 1;
            var end = position + 8 + (long)length;
            if (end > data.Length || start + 8 > end) continue;
            var image = data[start..(int)end];
            if (!IsJpegOrPng(image)) continue;
            try
            {
                if (ImageMetadata.HasMetadata(image.ToArray())) return true;
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidDataException or OverflowException)
            {
                // A damaged picture is not evidence of metadata.
            }
            position = (int)end - 1;
        }
        return false;
    }

    private static bool IsJpegOrPng(ReadOnlySpan<byte> image) =>
        ImageMetadata.IsJpeg(image) || image.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
}
