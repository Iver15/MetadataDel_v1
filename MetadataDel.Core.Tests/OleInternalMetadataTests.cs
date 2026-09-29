using System.Buffers.Binary;
using System.Text;
using MetadataDel.Core.Audit;
using MetadataDel.Core.Cleaning;
using MetadataDel.Core.Ole;
using MetadataDel.Core.Pdf;
using MetadataDel.Core.Word;
using Xunit;

namespace MetadataDel.Core.Tests;

public sealed partial class MetadataCleaningTests
{
    private const int FcLcbStart = 154; // FibBase + csw(14) + cslw(22) + cbRgFcLcb
    private const int DopSize = 0x40;
    private const int CommentRefs = 800;
    private const int CommentExtra = 860;
    private const int ClxOffset = 900;
    private const int ChpxPlcOffset = 940;
    private const string DocumentText =
        "document text \u0013 INCLUDEPICTURE \"C:\\Users\\secret-user\\pic.png\" \\d \u0014\u0001\u0015" +
        " \u0013 HYPERLINK \"https://example.com/users/a\" \u0014link\u0015";

    [Fact]
    public async Task Doc_RemovesAuthorsDatesAndSaveHistoryInsideWordStreams()
    {
        var path = Path.Combine(_tempDirectory, "internal.doc");
        CreateWord97Doc(path);
        Assert.Contains(MetadataAuditService.Audit(path).Findings, f => f.Code == "ole.internal");

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.Null(result.Message);
        Assert.False(MetadataAuditService.Audit(path).HasSensitiveMetadata);
        var bytes = File.ReadAllBytes(path);
        foreach (var secret in new[] { "Secret Author", "Secret Editor", "secret-user", "Secret Reviewer", "Olga" })
            AssertNoText(bytes, secret);

        using var root = OpenMcdf.RootStorage.OpenRead(path);
        var wordDocument = ReadAll(root.OpenStream("WordDocument"));
        var table = ReadAll(root.OpenStream("1Table"));
        Assert.Equal(6 + 18 * 2, (int)Lcb(wordDocument, 32));
        Assert.Equal(0u, Lcb(wordDocument, 71));
        Assert.All(table.AsSpan(0x14, 0x12).ToArray(), b => Assert.Equal(0, b));
        Assert.Equal("document text", Encoding.ASCII.GetString(wordDocument, 1536, 13));
        var text = Encoding.Latin1.GetString(wordDocument, 1536, DocumentText.Length);
        Assert.Contains("INCLUDEPICTURE \"pic.png\"", text);
        Assert.Contains("HYPERLINK \"https://example.com/users/a\"", text);
        Assert.Equal(DocumentText.Length, text.Length);
        // "Olga" is shorter than "Unknown", so the comment owners table no longer fits and moves to the end.
        Assert.True(Fc(wordDocument, 36) >= 1024);
        Assert.Equal("Unknown", ReadXst(table, (int)Fc(wordDocument, 36)));
        Assert.Equal(new[] { "Unknown", "Unknown" }, ReadSttb(table, (int)Fc(wordDocument, 51)));
        Assert.Equal("U", ReadXst(table, CommentRefs + 8));
        Assert.All(table.AsSpan(CommentExtra, 4).ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task Xls_RemovesLastSavedByUserName()
    {
        var path = Path.Combine(_tempDirectory, "owner.xls");
        CreateBiff8Xls(path, encrypted: false);
        Assert.Contains(MetadataAuditService.Audit(path).Findings, f => f.Code == "ole.internal");

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.Null(result.Message);
        Assert.False(MetadataAuditService.Audit(path).HasSensitiveMetadata);
        AssertNoText(File.ReadAllBytes(path), "Secret Owner");
        AssertNoText(File.ReadAllBytes(path), "Note Writer");
        using var root = OpenMcdf.RootStorage.OpenRead(path);
        Assert.Equal(BuildWorkbook(encrypted: false).Length, ReadAll(root.OpenStream("Workbook")).Length);
    }

    [Fact]
    public async Task Xls_EncryptedWorkbookIsKeptAndReported()
    {
        var path = Path.Combine(_tempDirectory, "encrypted.xls");
        CreateBiff8Xls(path, encrypted: true);

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.Contains("зашифрована", result.Message);
        using var root = OpenMcdf.RootStorage.OpenRead(path);
        Assert.Equal(BuildWorkbook(encrypted: true), ReadAll(root.OpenStream("Workbook")));
    }

    [Fact]
    public async Task Doc_RemovesPropertiesOfEmbeddedObjects()
    {
        var path = Path.Combine(_tempDirectory, "embedded.doc");
        CreateWord97Doc(path);
        using (var root = OpenMcdf.RootStorage.Open(path, FileMode.Open))
        {
            var embedded = root.CreateStorage("ObjectPool").CreateStorage("_1234");
            using (var stream = embedded.CreateStream("\u0005SummaryInformation")) stream.Write(Encoding.Unicode.GetBytes("Embedded Secret"));
            using (var stream = embedded.CreateStream("\u0001CompObj")) stream.Write(Encoding.ASCII.GetBytes("Equation"));
        }
        Assert.Contains(MetadataAuditService.Audit(path).Findings, f => f.Description.Contains("ObjectPool/_1234/"));

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.False(MetadataAuditService.Audit(path).HasSensitiveMetadata);
        AssertNoText(File.ReadAllBytes(path), "Embedded Secret");
        using var cleaned = OpenMcdf.RootStorage.OpenRead(path);
        var cleanedObject = cleaned.OpenStorage("ObjectPool").OpenStorage("_1234");
        Assert.Equal(new[] { "\u0001CompObj" }, cleanedObject.EnumerateEntries().Select(e => e.Name));
        Assert.Equal("Equation", Encoding.ASCII.GetString(ReadAll(cleanedObject.OpenStream("\u0001CompObj"))));
    }

    [Fact]
    public void ScrubCompoundFile_CleansEmbeddedOleObjectInMemory()
    {
        var path = Path.Combine(_tempDirectory, "oleObject1.bin");
        CreateWord97Doc(path);
        using (var root = OpenMcdf.RootStorage.Open(path, FileMode.Open))
        using (var stream = root.CreateStream("\u0005SummaryInformation"))
            stream.Write(Encoding.Unicode.GetBytes("Embedded Secret"));
        var warnings = new List<string>();

        var scrubbed = OleDocumentCleaner.ScrubCompoundFile(File.ReadAllBytes(path), warnings);

        Assert.Empty(warnings);
        foreach (var secret in new[] { "Embedded Secret", "Secret Author", "Secret Reviewer", "Olga" })
            AssertNoText(scrubbed, secret);
        using var root2 = OpenMcdf.RootStorage.Open(new MemoryStream(scrubbed));
        Assert.Empty(OleDocumentCleaner.Inspect(root2));
        Assert.Equal("document text", Encoding.ASCII.GetString(ReadAll(root2.OpenStream("WordDocument")), 1536, 13));
    }

    [Fact]
    public void ScrubCompoundFile_ShortensOlePackagePathsToFileName()
    {
        var path = Path.Combine(_tempDirectory, "package.bin");
        using (var root = OpenMcdf.RootStorage.Create(path))
        using (var stream = root.CreateStream("\u0001Ole10Native"))
            stream.Write(BuildOle10Native());
        var warnings = new List<string>();

        var scrubbed = OleDocumentCleaner.ScrubCompoundFile(File.ReadAllBytes(path), warnings);

        Assert.Empty(warnings);
        AssertNoText(scrubbed, "secret-user");
        using var root2 = OpenMcdf.RootStorage.Open(new MemoryStream(scrubbed));
        Assert.Empty(OleDocumentCleaner.Inspect(root2));
        var native = ReadAll(root2.OpenStream("\u0001Ole10Native"));
        Assert.Equal((uint)native.Length - 4, BinaryPrimitives.ReadUInt32LittleEndian(native));
        var text = Encoding.Latin1.GetString(native);
        Assert.Contains("report.docx\0report.docx\0", text);
        Assert.Contains("PAYLOAD", text);
        Assert.True(native.AsSpan().IndexOf(Encoding.Unicode.GetBytes("report.docx")) >= 0);
    }

    // Byte search in both encodings: decoding a whole file as UTF-16 misses strings at odd offsets.
    private static void AssertNoText(byte[] data, string secret)
    {
        Assert.True(data.AsSpan().IndexOf(Encoding.Latin1.GetBytes(secret)) < 0, secret);
        Assert.True(data.AsSpan().IndexOf(Encoding.Unicode.GetBytes(secret)) < 0, secret);
    }

    private static byte[] BuildOle10Native()
    {
        using var body = new MemoryStream();
        using var writer = new BinaryWriter(body);
        void Ansi(string value) { writer.Write(Encoding.Latin1.GetBytes(value)); writer.Write((byte)0); }
        void Wide(string value) { writer.Write((uint)value.Length); writer.Write(Encoding.Unicode.GetBytes(value)); }
        writer.Write((ushort)2);
        Ansi("report.docx");
        Ansi(@"C:\Users\secret-user\Desktop\report.docx");
        writer.Write(0x00030000u);
        var temp = @"C:\Users\secret-user\AppData\Local\Temp\report.docx";
        writer.Write((uint)temp.Length + 1);
        Ansi(temp);
        writer.Write(7u);
        writer.Write(Encoding.ASCII.GetBytes("PAYLOAD"));
        Wide(temp);
        Wide("report.docx");
        Wide(@"C:\Users\secret-user\Desktop\report.docx");
        writer.Flush();
        var result = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)body.Length);
        body.ToArray().CopyTo(result, 4);
        return result;
    }

    [Fact]
    public async Task Doc_ReplacesHyperlinkPathsWithRelativeFileName()
    {
        const string ansi = @"C:\Users\secret-user\Desktop\plan.docx";
        const string wide = "/C:/Users/secret-user/Desktop/plan.docx";
        var path = Path.Combine(_tempDirectory, "hyperlink.doc");
        CreateWord97Doc(path, hyperlinkData: BuildHyperlinkData(ansi, wide));
        Assert.Contains(MetadataAuditService.Audit(path).Findings, f => f.Description == "локальные пути в гиперссылках Word");

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.False(MetadataAuditService.Audit(path).HasSensitiveMetadata);
        AssertNoText(File.ReadAllBytes(path), "secret-user");
        using var root = OpenMcdf.RootStorage.OpenRead(path);
        var data = ReadAll(root.OpenStream("Data"));
        var ansiText = Encoding.Latin1.GetString(data, 0x44 + 7, ansi.Length);
        var wideText = Encoding.Unicode.GetString(data, 0x44 + 7 + ansi.Length + 1 + 5, wide.Length * 2);
        Assert.Equal(ansi.Length, ansiText.Length);
        Assert.Matches(@"^(\.\\)+\\?plan\.docx$", ansiText);
        Assert.Matches(@"^(\./)+/?plan\.docx$", wideText);
    }

    [Fact]
    public async Task Doc_ReportsHiddenTextWithoutRemovingIt()
    {
        var path = Path.Combine(_tempDirectory, "hidden.doc");
        CreateWord97Doc(path, hiddenText: true);
        Assert.Contains(MetadataAuditService.Audit(path).Findings, f => f.Description == "скрытый текст Word");

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.Contains("скрытый текст Word", result.Message);
    }

    [Fact]
    public async Task Xls_ReportsHiddenSheetWithoutRemovingIt()
    {
        var path = Path.Combine(_tempDirectory, "hidden.xls");
        using (var root = OpenMcdf.RootStorage.Create(path))
        using (var stream = root.CreateStream("Workbook"))
            stream.Write(BuildWorkbook(encrypted: false, hiddenSheet: true));

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.Contains("скрытый лист Excel «Salaries»", result.Message);
        Assert.Contains(MetadataAuditService.Audit(path).Findings, f => f.Description.Contains("«Salaries»"));
    }

    [Theory]
    [InlineData("doc")]
    [InlineData("pdf")]
    [InlineData("docx")]
    public async Task Cleaning_KeepsRestrictiveFilePermissions(string extension)
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_tempDirectory, "private." + extension);
        IFileCleaner cleaner;
        switch (extension)
        {
            case "doc": CreateWord97Doc(path); cleaner = new OleDocumentCleaner(); break;
            case "pdf": CreatePdf(path); cleaner = new PdfCleaner(); break;
            default: CreateDocx(path); cleaner = new DocxCleaner(); break;
        }
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.SetUnixFileMode(path, ownerOnly);

        var result = await cleaner.CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.Equal(ownerOnly, File.GetUnixFileMode(path));
    }

    private static void CreateWord97Doc(string path, bool hiddenText = false, byte[]? hyperlinkData = null)
    {
        var wordDocument = new byte[2560];
        BinaryPrimitives.WriteUInt16LittleEndian(wordDocument, 0xA5EC);
        BinaryPrimitives.WriteUInt16LittleEndian(wordDocument.AsSpan(2), 0x00C1);
        BinaryPrimitives.WriteUInt16LittleEndian(wordDocument.AsSpan(10), 0x0200); // fWhichTblStm → 1Table
        BinaryPrimitives.WriteUInt16LittleEndian(wordDocument.AsSpan(32), 14);
        BinaryPrimitives.WriteUInt16LittleEndian(wordDocument.AsSpan(62), 22);
        BinaryPrimitives.WriteUInt16LittleEndian(wordDocument.AsSpan(152), 136); // FibRgFcLcb2002
        Encoding.Latin1.GetBytes(DocumentText).CopyTo(wordDocument, 1536);

        var table = new byte[1024];
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(0x14), 0x0C2A5123); // dttmCreated
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(0x18), 0x0C2A5124); // dttmRevised
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(0x20), 7);          // nRevision
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(0x22), 55);         // tmEdited
        SetFcLcb(wordDocument, 31, 0, DopSize);

        var assoc = new string[18];
        Array.Fill(assoc, "");
        assoc[1] = @"C:\Users\secret-user\Normal.dotm";
        assoc[6] = "Secret Author";
        assoc[7] = "Secret Editor";
        var assocBytes = BuildSttb(assoc);
        assocBytes.CopyTo(table, DopSize);
        SetFcLcb(wordDocument, 32, DopSize, assocBytes.Length);

        var savedBy = BuildSttb(new[] { "Secret Editor", @"C:\Users\secret-user\report.doc" });
        savedBy.CopyTo(table, 512);
        SetFcLcb(wordDocument, 71, 512, savedBy.Length);

        var revisionAuthors = BuildSttb(new[] { "Unknown", "Secret Reviewer" });
        revisionAuthors.CopyTo(table, 640);
        SetFcLcb(wordDocument, 51, 640, revisionAuthors.Length);

        var commentOwner = new byte[2 + 8];
        BinaryPrimitives.WriteUInt16LittleEndian(commentOwner, 4);
        Encoding.Unicode.GetBytes("Olga").CopyTo(commentOwner, 2);
        commentOwner.CopyTo(table, 720);
        SetFcLcb(wordDocument, 36, 720, commentOwner.Length);

        // PlcfandRef with one comment: two CPs, then ATRDPre10 whose xstUsrInitl is "OS".
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(CommentRefs + 8), 2);
        Encoding.Unicode.GetBytes("OS").CopyTo(table, CommentRefs + 10);
        SetFcLcb(wordDocument, 4, CommentRefs, 8 + 30);

        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(CommentExtra), 0x0C2A5125); // ATRDPost10.dttm
        SetFcLcb(wordDocument, 97, CommentExtra, 18);

        // Clx with one compressed piece (8-bit text) at byte 1536: Pcdt, CPs {0, n}, Pcd { flags, fc | fCompressed, prm }.
        table[ClxOffset] = 0x02;
        BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(ClxOffset + 1), 16);
        BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(ClxOffset + 9), DocumentText.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(ClxOffset + 15), (1536u * 2) | 0x40000000u);
        SetFcLcb(wordDocument, 33, ClxOffset, 21);

        if (hiddenText || hyperlinkData != null)
        {
            // PlcfBteChpx → CHPX FKP on page 4 with one run: sprmCFVanish = 1 or sprmCFData = 1 + sprmCPicLocation = 0.
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(ChpxPlcOffset), 1536);
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(ChpxPlcOffset + 4), 1536 + (uint)DocumentText.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(ChpxPlcOffset + 8), 4);
            SetFcLcb(wordDocument, 12, ChpxPlcOffset, 12);
            var fkp = wordDocument.AsSpan(2048, 512);
            BinaryPrimitives.WriteUInt32LittleEndian(fkp, 1536);
            BinaryPrimitives.WriteUInt32LittleEndian(fkp[4..], 1536 + (uint)DocumentText.Length);
            fkp[8] = 0x80;
            byte[] grpprl = hiddenText ? [0x3C, 0x08, 0x01] : [0x06, 0x08, 0x01, 0x03, 0x6A, 0, 0, 0, 0];
            fkp[256] = (byte)grpprl.Length;
            grpprl.CopyTo(fkp[257..]);
            fkp[511] = 1;
        }

        using var root = OpenMcdf.RootStorage.Create(path);
        using (var stream = root.CreateStream("WordDocument")) stream.Write(wordDocument);
        using (var stream = root.CreateStream("1Table")) stream.Write(table);
        if (hyperlinkData != null)
            using (var stream = root.CreateStream("Data")) stream.Write(hyperlinkData);
    }

    // NilPICFAndBinData: lcb, cbHeader = 0x44, header, then a file moniker-like payload with ANSI and UTF-16 paths.
    private static byte[] BuildHyperlinkData(string ansiPath, string unicodePath)
    {
        var payload = new List<byte>();
        payload.AddRange(new byte[7]);
        payload.AddRange(Encoding.Latin1.GetBytes(ansiPath + "\0"));
        payload.AddRange(new byte[5]);
        payload.AddRange(Encoding.Unicode.GetBytes(unicodePath));
        var block = new byte[0x44 + payload.Count];
        BinaryPrimitives.WriteInt32LittleEndian(block, block.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(4), 0x44);
        payload.CopyTo(block, 0x44);
        return block;
    }

    private static byte[] BuildSttb(IReadOnlyList<string> strings)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        writer.Write((ushort)0xFFFF);
        writer.Write((ushort)strings.Count);
        writer.Write((ushort)0);
        foreach (var value in strings)
        {
            writer.Write((ushort)value.Length);
            writer.Write(Encoding.Unicode.GetBytes(value));
        }
        return buffer.ToArray();
    }

    private static void SetFcLcb(byte[] wordDocument, int index, int fc, int lcb)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(wordDocument.AsSpan(FcLcbStart + 8 * index), (uint)fc);
        BinaryPrimitives.WriteUInt32LittleEndian(wordDocument.AsSpan(FcLcbStart + 8 * index + 4), (uint)lcb);
    }

    private static uint Fc(byte[] wordDocument, int index) =>
        BinaryPrimitives.ReadUInt32LittleEndian(wordDocument.AsSpan(FcLcbStart + 8 * index));

    private static string ReadXst(byte[] data, int offset) =>
        Encoding.Unicode.GetString(data, offset + 2, 2 * BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset)));

    private static string[] ReadSttb(byte[] data, int offset)
    {
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2));
        var result = new string[count];
        var position = offset + 6;
        for (var i = 0; i < count; i++)
        {
            result[i] = ReadXst(data, position);
            position += 2 + 2 * result[i].Length;
        }
        return result;
    }

    private static uint Lcb(byte[] wordDocument, int index) =>
        BinaryPrimitives.ReadUInt32LittleEndian(wordDocument.AsSpan(FcLcbStart + 8 * index + 4));

    private static void CreateBiff8Xls(string path, bool encrypted)
    {
        using var root = OpenMcdf.RootStorage.Create(path);
        using var stream = root.CreateStream("Workbook");
        stream.Write(BuildWorkbook(encrypted));
    }

    private static byte[] BuildWorkbook(bool encrypted, bool hiddenSheet = false)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        void Record(ushort type, byte[] body)
        {
            writer.Write(type);
            writer.Write((ushort)body.Length);
            writer.Write(body);
        }
        Record(0x0809, new byte[16]);                   // BOF
        if (encrypted) Record(0x002F, new byte[6]);     // FILEPASS
        var writeAccess = Enumerable.Repeat((byte)' ', 112).ToArray();
        var name = Encoding.Latin1.GetBytes("Secret Owner");
        BinaryPrimitives.WriteUInt16LittleEndian(writeAccess, (ushort)name.Length);
        writeAccess[2] = 0;
        name.CopyTo(writeAccess, 3);
        Record(0x005C, writeAccess);                    // WriteAccess
        if (hiddenSheet)
        {
            var sheet = Encoding.Latin1.GetBytes("Salaries");
            var boundSheet = new byte[8 + sheet.Length];
            boundSheet[4] = 1;                          // hsState = hidden
            boundSheet[6] = (byte)sheet.Length;
            sheet.CopyTo(boundSheet, 8);
            Record(0x0085, boundSheet);                 // BoundSheet8
        }
        Record(0x000A, Array.Empty<byte>());            // EOF of globals
        Record(0x0809, new byte[16]);                   // BOF of a worksheet
        var author = Encoding.Latin1.GetBytes("Note Writer");
        var note = new byte[8 + 3 + author.Length + 1];
        BinaryPrimitives.WriteUInt16LittleEndian(note.AsSpan(6), 1); // idObj
        BinaryPrimitives.WriteUInt16LittleEndian(note.AsSpan(8), (ushort)author.Length);
        author.CopyTo(note, 11);
        Record(0x001C, note);                           // Note
        Record(0x000A, Array.Empty<byte>());            // EOF of worksheet
        return buffer.ToArray();
    }
}
