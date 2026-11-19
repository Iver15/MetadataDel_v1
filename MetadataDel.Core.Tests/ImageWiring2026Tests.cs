using System.Text;
using DocumentFormat.OpenXml.Packaging;
using MetadataDel.Core.Word;
using Xunit;

namespace MetadataDel.Core.Tests;

/// <summary>Повреждённые и нераспознаваемые картинки внутри DOCX/XLSX дают неполный результат, а не тихий успех.</summary>
public sealed class ImageWiring2026Tests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mdel-images2026", Guid.NewGuid().ToString("N"));

    public ImageWiring2026Tests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Docx_DamagedJpeg_IsReportedIncomplete()
    {
        var path = CreateDocxWithImage("image/jpeg", TruncatedJpeg());
        var result = await new DocxCleaner().CleanAsync(path, new());
        Assert.True(result.Success, result.Message);
        Assert.True(result.Incomplete, result.Message);
        Assert.Contains("повреждена", result.Message);
    }

    [Fact]
    public async Task Docx_HeicWithExif_IsReportedIncomplete()
    {
        var path = CreateDocxWithImage("image/heic", Heif());
        var result = await new DocxCleaner().CleanAsync(path, new());
        Assert.True(result.Success, result.Message);
        Assert.True(result.Incomplete, result.Message);
        Assert.Contains("HEIC", result.Message);
    }

    private string CreateDocxWithImage(string contentType, byte[] image)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".docx");
        using (var document = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            var imagePart = main.AddImagePart(contentType);
            using (var stream = new MemoryStream(image)) imagePart.FeedData(stream);
            var id = main.GetIdOfPart(imagePart);
            var xml = "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
                      "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
                      "xmlns:v=\"urn:schemas-microsoft-com:vml\"><w:body><w:p><w:r><w:pict><v:shape style=\"width:10pt;height:10pt\">" +
                      $"<v:imagedata r:id=\"{id}\"/></v:shape></w:pict></w:r></w:p></w:body></w:document>";
            using var output = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            main.FeedData(output);
        }
        return path;
    }

    /// <summary>JPEG, у которого сегмент APP1 с EXIF выходит за конец файла.</summary>
    private static byte[] TruncatedJpeg()
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 });
        output.Write("JFIF\0"u8);
        output.Write(new byte[] { 1, 1, 0, 0, 1, 0, 1, 0, 0 });
        output.Write(new byte[] { 0xFF, 0xE1, 0x7F, 0xFF });
        output.Write("Exif\0\0Secret Camera Owner"u8);
        return output.ToArray();
    }

    /// <summary>Минимальный HEIF с элементом Exif в meta/iinf.</summary>
    private static byte[] Heif()
    {
        static byte[] Box(string type, byte[] payload)
        {
            var result = new byte[8 + payload.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(result, (uint)result.Length);
            Encoding.ASCII.GetBytes(type).CopyTo(result, 4);
            payload.CopyTo(result, 8);
            return result;
        }
        static byte[] Full(string type, byte version, byte[] payload) => Box(type, new byte[] { version, 0, 0, 0 }.Concat(payload).ToArray());

        var infe = Full("infe", 2, new byte[] { 0, 1, 0, 0 }.Concat("Exif"u8.ToArray()).Concat(new byte[] { 0 }).ToArray());
        var iinf = Full("iinf", 0, new byte[] { 0, 1 }.Concat(infe).ToArray());
        var hdlr = Full("hdlr", 0, new byte[4].Concat("pict"u8.ToArray()).Concat(new byte[13]).ToArray());
        var meta = Full("meta", 0, hdlr.Concat(iinf).ToArray());
        var ftyp = Box("ftyp", "heic"u8.ToArray().Concat(new byte[4]).Concat("mif1heic"u8.ToArray()).ToArray());
        return ftyp.Concat(meta).Concat(Box("mdat", new byte[16])).ToArray();
    }
}

/// <summary>Встроенный OLE-объект, который не удалось очистить полностью, делает неполным и весь документ.</summary>
public sealed class EmbeddedOleWiring2026Tests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mdel-embedded-ole2026", Guid.NewGuid().ToString("N"));

    public EmbeddedOleWiring2026Tests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Docx_WithEncryptedEmbeddedXls_IsReportedIncomplete()
    {
        var path = Path.Combine(_directory, "host.docx");
        using (var document = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            var ole = main.AddEmbeddedObjectPart("application/vnd.openxmlformats-officedocument.oleObject");
            using (var stream = new MemoryStream(EncryptedXls())) ole.FeedData(stream);
            var id = main.GetIdOfPart(ole);
            var xml = "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
                      "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
                      "xmlns:o=\"urn:schemas-microsoft-com:office:office\"><w:body><w:p><w:r><w:object>" +
                      $"<o:OLEObject Type=\"Embed\" ProgID=\"Excel.Sheet.8\" r:id=\"{id}\"/></w:object></w:r></w:p></w:body></w:document>";
            using var output = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            main.FeedData(output);
        }

        var result = await new DocxCleaner().CleanAsync(path, new());

        Assert.True(result.Success, result.Message);
        Assert.True(result.Incomplete, result.Message);
    }

    private byte[] EncryptedXls()
    {
        static byte[] Record(ushort type, byte[] body) =>
            [.. BitConverter.GetBytes(type), .. BitConverter.GetBytes((ushort)body.Length), .. body];
        var bof = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bof, 0x0600);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bof.AsSpan(2), 0x0005);
        byte[] workbook = [.. Record(0x0809, bof), .. Record(0x002F, new byte[4]), .. Record(0x000A, [])];
        var file = Path.Combine(_directory, "embedded.xls");
        using (var root = OpenMcdf.RootStorage.Create(file))
        using (var stream = root.CreateStream("Workbook"))
            stream.Write(workbook);
        return File.ReadAllBytes(file);
    }
}
