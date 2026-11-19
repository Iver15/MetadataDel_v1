using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using MetadataDel.Core.Audit;
using MetadataDel.Core.Cleaning;
using MetadataDel.Core.Ole;
using Xunit;

namespace MetadataDel.Core.Tests;

// Regression tests for the legacy binary Office fixes: XLS records, VBA projects, Word FIB blocks and result flags.
public sealed partial class MetadataCleaningTests
{
    private static readonly byte[] StdHlinkClsid = new Guid("79EAC9D0-BAF9-11CE-8C82-00AA004BA90B").ToByteArray();
    private static readonly byte[] UrlMonikerClsid = new Guid("79EAC9E0-BAF9-11CE-8C82-00AA004BA90B").ToByteArray();
    private static readonly byte[] FileMonikerClsid = new Guid("00000303-0000-0000-C000-000000000046").ToByteArray();
    private static readonly byte[] HyperlinkTime = { 0x56, 0x34, 0x12, 0xEF, 0xCD, 0xAB, 0xD9, 0x01 };

    // ---------- XLS ----------

    [Fact]
    public async Task Xls_BlanksWriteReservationUserNameButKeepsPasswordVerifier()
    {
        var sharing = new List<byte> { 1, 0, 0xFE, 0xCA };      // fReadOnlyRec = 1, wResPass = 0xCAFE
        sharing.AddRange(XlString("Secret Sharer"));
        var path = CreateXls("sharing.xls", BuildXls([Record(0x005B, sharing.ToArray())]));
        Assert.Contains(MetadataAuditService.Audit(path).Findings, f => f.Description.Contains("пароль на запись"));
        var before = ReadWorkbook(path);

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.False(result.Incomplete, result.Message);
        var after = ReadWorkbook(path);
        Assert.Equal(before.Length, after.Length);
        AssertNoText(after, "Secret Sharer");
        Assert.True(after.AsSpan().IndexOf(new byte[] { 0x5B, 0x00, (byte)sharing.Count, 0x00, 1, 0, 0xFE, 0xCA }) >= 0);
        Assert.False(MetadataAuditService.Audit(path).HasSensitiveMetadata, MetadataAuditService.Audit(path).Summarize());
    }

    [Fact]
    public async Task Xls_ShortensLocalHyperlinkPathsAndKeepsWebLinks()
    {
        var fileLink = HyperlinkRecord(0x53, HyperlinkString("plan"),
            FileMoniker(@"C:\Users\secret-user\Desktop\plan.xls", @"C:\Users\secret-user\Desktop\plan.xls"), HyperlinkTime);
        var relativeLink = HyperlinkRecord(0x11, HyperlinkString("report"), FileMoniker(@"Reports\secret-dir\report.xls", null, cAnti: 1));
        var fileUrl = HyperlinkRecord(0x13, HyperlinkString("notes"), UrlMoniker("file:///C:/Users/secret-user/notes.pdf"));
        var uncUrl = HyperlinkRecord(0x13, HyperlinkString("share"), UrlMoniker(@"\\secret-server\share\budget.xls"));
        var webUrl = HyperlinkRecord(0x13, HyperlinkString("web"), UrlMoniker("https://example.com/web-user/page"));
        var path = CreateXls("links.xls", BuildXls(sheet: [fileLink, relativeLink, fileUrl, uncUrl, webUrl]));
        Assert.Contains(MetadataAuditService.Audit(path).Findings, f => f.Description == "локальные пути в гиперссылках Excel");
        Assert.Contains(MetadataAuditService.Audit(path).Findings, f => f.Description == "даты создания гиперссылок Excel");
        var before = ReadWorkbook(path);

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.False(result.Incomplete, result.Message);
        var after = ReadWorkbook(path);
        Assert.Equal(before.Length, after.Length);
        foreach (var secret in new[] { "secret-user", "secret-dir", "secret-server" }) AssertNoText(after, secret);
        Assert.True(after.AsSpan().IndexOf(HyperlinkTime) < 0);
        Assert.True(after.AsSpan().IndexOf(Encoding.Unicode.GetBytes("https://example.com/web-user/page")) >= 0);
        var latin = Encoding.Latin1.GetString(after);
        var unicode = Encoding.Unicode.GetString(after);
        Assert.Matches(@"(\.\\)+\\?plan\.xls\0", latin);
        Assert.Matches(@"(\.\\)+\\?report\.xls\0", latin);
        Assert.Matches(@"file:///(\./)+/?notes\.pdf\0", unicode + Encoding.Unicode.GetString(after, 1, after.Length - 1));
        Assert.False(MetadataAuditService.Audit(path).HasSensitiveMetadata, MetadataAuditService.Audit(path).Summarize());
    }

    [Fact]
    public async Task Xls_ReducesExternalWorkbookPathToFileName()
    {
        const string virtualPath = "\u0001\u0001C\u0003Users\u0003secret-user\u0003Budget.xls";
        var supBook = new List<byte> { 1, 0 };                       // ctab = 1
        supBook.AddRange(BitConverter.GetBytes((ushort)virtualPath.Length));
        supBook.Add(0);
        supBook.AddRange(Encoding.Latin1.GetBytes(virtualPath));
        supBook.AddRange(XlString("Sheet1"));
        var externName = new List<byte> { 0, 0, 0, 0, 0, 0 };
        const string externPath = @"C:\Users\secret-user\addin.xla";
        externName.Add((byte)externPath.Length);
        externName.Add(0);
        externName.AddRange(Encoding.Latin1.GetBytes(externPath));
        externName.AddRange(new byte[] { 2, 0, 0x1C, 0x17 });          // formula: cce + ptgErr #REF!
        var path = CreateXls("external.xls", BuildXls([Record(0x01AE, supBook.ToArray()), Record(0x0023, externName.ToArray())]));
        Assert.Contains(MetadataAuditService.Audit(path).Findings, f => f.Description == "пути к связанным книгам Excel");

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.False(result.Incomplete, result.Message);
        var after = ReadWorkbook(path);
        AssertNoText(after, "secret-user");
        var text = Encoding.Latin1.GetString(after);
        Assert.Matches("\u0001(\\.\u0003)+\u0003?Budget\\.xls\u0006\u0000\u0000Sheet1", text);
        Assert.Matches(@"(\.\\)+\\?addin\.xla", text);
        Assert.False(MetadataAuditService.Audit(path).HasSensitiveMetadata, MetadataAuditService.Audit(path).Summarize());
    }

    [Fact]
    public async Task Xls_AnonymizesPersonalViewNamesButKeepsSharedViewsAndGuids()
    {
        var guid = Guid.NewGuid().ToByteArray();
        var path = CreateXls("views.xls", BuildXls([
            Record(0x01A9, UserBView(guid, personal: true, "Secret Person - Personal View")),
            Record(0x01A9, UserBView(Guid.NewGuid().ToByteArray(), personal: false, "Print layout"))
        ]));
        Assert.Contains(MetadataAuditService.Audit(path).Findings, f => f.Description == "имена личных представлений общей книги Excel");

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        var after = ReadWorkbook(path);
        AssertNoText(after, "Secret Person");
        Assert.Contains("Print layout", Encoding.Latin1.GetString(after));
        Assert.Contains("View 1", Encoding.Latin1.GetString(after));
        Assert.True(after.AsSpan().IndexOf(guid) >= 0);
    }

    [Fact]
    public async Task Xls_RecordContinuedInContinueIsReportedAndLeftIntact()
    {
        var link = HyperlinkRecord(0x01, FileMoniker(@"C:\Users\secret-user\plan.xls", null));
        var workbook = BuildXls(sheet: [link, Record(0x003C, new byte[4])]);
        var path = CreateXls("continued.xls", workbook);

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.True(result.Incomplete);
        Assert.Contains("Continue", result.Message);
        Assert.Equal(workbook, ReadWorkbook(path));
    }

    [Fact]
    public async Task Xls_TruncatedRecordAfterGlobalsIsReported()
    {
        var workbook = BuildXls(sheet: [Record(0x0204, new byte[20])]);
        workbook = workbook[..^10];                                  // the label record and the sheet EOF are cut
        var path = CreateXls("truncated.xls", workbook);

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.True(result.Incomplete);
        Assert.Contains("обрезан", result.Message);
    }

    [Fact]
    public async Task Xls_Biff5BookStreamIsReportedAsIncomplete()
    {
        var path = Path.Combine(_tempDirectory, "excel95.xls");
        using (var root = OpenMcdf.RootStorage.Create(path))
        using (var stream = root.CreateStream("Book"))
            stream.Write(BuildXls([Record(0x005C, XlString("Secret Owner"))]));

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.True(result.Incomplete);
        Assert.Contains("Excel 5.0/95", result.Message);
        Assert.True(MetadataAuditService.Audit(path).HasSensitiveMetadata);
    }

    [Fact]
    public async Task Xls_EncryptedWorkbookIsIncomplete()
    {
        var path = Path.Combine(_tempDirectory, "encrypted-flag.xls");
        CreateBiff8Xls(path, encrypted: true);

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.True(result.Incomplete);
    }

    [Fact]
    public async Task Xls_ReportsPictureWithExif()
    {
        var path = CreateXls("photo.xls", BuildXls([Record(0x00EB, [.. new byte[12], .. JpegBlip()])]));
        Assert.Contains(MetadataAuditService.Audit(path).Findings, f => f.Description.Contains("метаданные изображений"));

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.True(result.Incomplete);
        Assert.Contains("EXIF", result.Message);
    }

    // ---------- VBA ----------

    [Fact]
    public async Task Xls_KeepsMacrosButBlanksHelpFileAndReportsReferencePaths()
    {
        var path = CreateXls("macros.xls", BuildWorkbook(encrypted: false), root =>
        {
            var project = root.CreateStorage("_VBA_PROJECT_CUR");
            using (var stream = project.CreateStream("PROJECT")) stream.Write(Encoding.ASCII.GetBytes(ProjectText(@"C:\Users\secret-user\Help\macros.chm")));
            var vba = project.CreateStorage("VBA");
            using (var stream = vba.CreateStream("dir"))
                stream.Write(CompressVba("*\\G{00020430-0000-0000-C000-000000000046}#2.0#0#C:\\Users\\secret-user\\AddIns\\tools.xlam#Tools"));
            using (var stream = vba.CreateStream("Module1")) stream.Write(Encoding.ASCII.GetBytes("Attribute VB_Name = \"Module1\""));
        });
        var audit = MetadataAuditService.Audit(path);
        Assert.Contains(audit.Findings, f => f.Description.StartsWith("путь к файлу справки проекта VBA"));
        Assert.Contains(audit.Findings, f => f.Description.StartsWith("пути к файлам в ссылках и справке проекта VBA"));

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.True(result.Incomplete);
        Assert.Contains("макросы VBA", result.Message);
        using var root = OpenMcdf.RootStorage.OpenRead(path);
        var project = root.OpenStorage("_VBA_PROJECT_CUR");
        var text = Encoding.ASCII.GetString(ReadAll(project.OpenStream("PROJECT")));
        Assert.Equal(ProjectText(""), text);
        Assert.Equal("Attribute VB_Name = \"Module1\"", Encoding.ASCII.GetString(ReadAll(project.OpenStorage("VBA").OpenStream("Module1"))));
    }

    [Fact]
    public async Task Doc_MacrosWithoutPathsAreReportedButNotIncomplete()
    {
        var path = Path.Combine(_tempDirectory, "macros.doc");
        CreateWord97Doc(path);
        using (var root = OpenMcdf.RootStorage.Open(path, FileMode.Open))
        {
            var macros = root.CreateStorage("Macros");
            using (var stream = macros.CreateStream("PROJECT")) stream.Write(Encoding.ASCII.GetBytes(ProjectText(@"\\secret-server\help\doc.hlp")));
            var vba = macros.CreateStorage("VBA");
            using (var stream = vba.CreateStream("dir"))
                stream.Write(CompressVba("*\\G{00020430-0000-0000-C000-000000000046}#2.0#0#C:\\Windows\\System32\\stdole2.tlb#OLE Automation"));
        }

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.False(result.Incomplete, result.Message);
        Assert.Contains("макросы VBA", result.Message);
        AssertNoText(File.ReadAllBytes(path), "secret-server");
        Assert.Contains(MetadataAuditService.Audit(path).Findings, f => f.Description.StartsWith("макросы VBA"));
    }

    // ---------- Word ----------

    [Fact]
    public async Task Doc_DropsInactiveTableStream()
    {
        var path = Path.Combine(_tempDirectory, "two-tables.doc");
        CreateWord97Doc(path);
        using (var root = OpenMcdf.RootStorage.Open(path, FileMode.Open))
        using (var stream = root.CreateStream("0Table"))
            stream.Write(BuildSttb(new[] { "Old Author", @"C:\Users\secret-user\old.doc" }));
        Assert.Contains(MetadataAuditService.Audit(path).Findings, f => f.Description.Contains("0Table"));

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        AssertNoText(File.ReadAllBytes(path), "Old Author");
        using var cleaned = OpenMcdf.RootStorage.OpenRead(path);
        Assert.False(cleaned.ContainsEntry("0Table"));
        Assert.True(cleaned.ContainsEntry("1Table"));
    }

    [Fact]
    public async Task Doc_FastSavedDocumentIsIncomplete()
    {
        var path = Path.Combine(_tempDirectory, "fast-saved.doc");
        CreateWord97Doc(path);
        PatchStream(path, "WordDocument", wordDocument => wordDocument[10] |= 0x04);

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.True(result.Incomplete);
        Assert.Contains("быстрого сохранения", result.Message);
        AssertNoText(File.ReadAllBytes(path), "Secret Author");
    }

    [Theory]
    [InlineData(0x0100, 0x00C1)]  // fEncrypted
    [InlineData(0x0000, 0x0065)]  // Word 6/95
    public async Task Doc_UnsupportedDocumentIsIncomplete(int flags, int nFib)
    {
        var path = Path.Combine(_tempDirectory, $"unsupported-{flags}-{nFib}.doc");
        CreateWord97Doc(path);
        PatchStream(path, "WordDocument", wordDocument =>
        {
            BinaryPrimitives.WriteUInt16LittleEndian(wordDocument.AsSpan(2), (ushort)nFib);
            wordDocument[11] |= (byte)(flags >> 8);
        });

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.True(result.Incomplete);
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(-200)]
    public async Task Doc_NegativeGrpprlSizeIsReportedWithoutHanging(short cbGrpprl)
    {
        var path = Path.Combine(_tempDirectory, $"grpprl{cbGrpprl}.doc");
        CreateWord97Doc(path);
        const int clx = 960;
        PatchStream(path, "1Table", table =>
        {
            table[clx] = 0x01;
            BinaryPrimitives.WriteInt16LittleEndian(table.AsSpan(clx + 1), cbGrpprl);
            table.AsSpan(ClxOffset, 21).CopyTo(table.AsSpan(clx + 3));
        });
        PatchStream(path, "WordDocument", wordDocument => SetFcLcb(wordDocument, 33, clx, 24));

        var result = await Task.Run(() => new OleDocumentCleaner().CleanAsync(path, new CleanOptions())).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(result.Success, result.Message);
        Assert.True(result.Incomplete);
        Assert.Contains("Служебные свойства Word не удалены", result.Message);
    }

    [Fact]
    public async Task Doc_RemovesRoutingSlipEnvelopeSaveTimeAndExternalPaths()
    {
        var path = Path.Combine(_tempDirectory, "fib-extras.doc");
        CreateWord97Doc(path);
        byte[]? tableAfterPatch = null;
        PatchStream(path, "1Table", _ => { }, table =>
        {
            var blocks = new List<byte>(table);
            int Append(byte[] block) { var fc = blocks.Count; blocks.AddRange(block); return fc; }
            var routeSlip = Encoding.Unicode.GetBytes("\0\0Secret Router <router@example.com>");
            var envelope = Encoding.Unicode.GetBytes("\0\0To: secret@example.com");
            var fileNames = BuildSttbWithExtra(8, @"C:\Users\secret-user\Linked.doc");
            var threading = new List<byte>();
            threading.AddRange(BuildSttbWithExtra(8, "<msg@secret-mail>", ""));
            threading.AddRange(BuildSttbWithExtra(0, "", ""));
            threading.AddRange(BuildSttbWithExtra(2, "email"));
            threading.AddRange(BuildSttbWithExtra(0, "secret-mail@example.com"));
            threading.AddRange(BuildSttbWithExtra(2));
            threading.AddRange(BuildSttbWithExtra(0));
            var positions = new[] { Append(routeSlip), Append(envelope), Append(fileNames), Append(threading.ToArray()) };
            tableAfterPatch = blocks.ToArray();
            PatchStream(path, "WordDocument", wordDocument =>
            {
                SetFcLcb(wordDocument, 70, positions[0], routeSlip.Length);
                SetFcLcb(wordDocument, 97, positions[1], envelope.Length);
                SetFcLcb(wordDocument, 72, positions[2], fileNames.Length);
                SetFcLcb(wordDocument, 94, positions[3], threading.Count);
                BinaryPrimitives.WriteInt64LittleEndian(wordDocument.AsSpan(FcLcbStart + 8 * 87), 0x01D9ABCDEF123456);
            });
            return tableAfterPatch;
        });
        var audit = MetadataAuditService.Audit(path).Findings.Select(f => f.Description).ToList();
        Assert.Contains("лист маршрутизации Word (отправитель и получатели)", audit);
        Assert.Contains("почтовый конверт Word (адреса e-mail)", audit);
        Assert.Contains("дата последнего сохранения в заголовке Word", audit);
        Assert.Contains("пути к внешним файлам Word", audit);
        Assert.Contains("сведения о письмах и их авторах Word", audit);

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.False(result.Incomplete, result.Message);
        var bytes = File.ReadAllBytes(path);
        foreach (var secret in new[] { "Secret Router", "secret@example.com", "secret-user", "secret-mail" })
            AssertNoText(bytes, secret);
        using var root = OpenMcdf.RootStorage.OpenRead(path);
        var wordDocument = ReadAll(root.OpenStream("WordDocument"));
        var table = ReadAll(root.OpenStream("1Table"));
        Assert.Equal(0u, Lcb(wordDocument, 70));
        Assert.Equal(0u, Lcb(wordDocument, 97));
        Assert.Equal(0L, BinaryPrimitives.ReadInt64LittleEndian(wordDocument.AsSpan(FcLcbStart + 8 * 87)));
        Assert.Matches(@"^(\.\\)+\\?Linked\.doc$", ReadSttb(table, (int)Fc(wordDocument, 72))[0]);
        Assert.NotEqual(0u, Lcb(wordDocument, 94));
        Assert.False(MetadataAuditService.Audit(path).HasSensitiveMetadata, MetadataAuditService.Audit(path).Summarize());
    }

    [Fact]
    public async Task Doc_ClearsCommentDatesAtAtrdExtraIndex()
    {
        var path = Path.Combine(_tempDirectory, "atrd.doc");
        CreateWord97Doc(path);
        const int extra = 980;
        PatchStream(path, "1Table", table => BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(extra), 0x0C2A5125));
        PatchStream(path, "WordDocument", wordDocument =>
        {
            SetFcLcb(wordDocument, 97, 0, 0);
            SetFcLcb(wordDocument, 112, extra, 18);   // FibRgFcLcb2002.fcAtrdExtra
        });

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        using var root = OpenMcdf.RootStorage.OpenRead(path);
        var table = ReadAll(root.OpenStream("1Table"));
        Assert.All(table.AsSpan(extra, 4).ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(18u, Lcb(ReadAll(root.OpenStream("WordDocument")), 112));
    }

    [Fact]
    public async Task Doc_ReportsPictureWithExifInDataStream()
    {
        var path = Path.Combine(_tempDirectory, "photo.doc");
        CreateWord97Doc(path);
        using (var root = OpenMcdf.RootStorage.Open(path, FileMode.Open))
        using (var stream = root.CreateStream("Data"))
            stream.Write([.. new byte[0x44], .. JpegBlip()]);
        Assert.Contains(MetadataAuditService.Audit(path).Findings, f => f.Description.Contains("метаданные изображений"));

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        Assert.True(result.Incomplete);
        Assert.Contains("EXIF", result.Message);
    }

    // ---------- Container ----------

    [Fact]
    public async Task Ole_RebuiltDirectoryEntriesHaveNoTimestamps()
    {
        var path = Path.Combine(_tempDirectory, "timestamps.doc");
        CreateWord97Doc(path);
        using (var root = OpenMcdf.RootStorage.Open(path, FileMode.Open))
        {
            var embedded = root.CreateStorage("ObjectPool").CreateStorage("_1234");
            using var stream = embedded.CreateStream("\u0001CompObj");
            stream.Write(Encoding.ASCII.GetBytes("Equation"));
        }

        var result = await new OleDocumentCleaner().CleanAsync(path, new CleanOptions());

        Assert.True(result.Success, result.Message);
        var entries = ReadDirectoryEntries(File.ReadAllBytes(path));
        Assert.Contains(entries, e => e.Name == "_1234");
        Assert.All(entries, e => Assert.True(e.Created == 0 && e.Modified == 0, e.Name));
    }

    // ---------- Helpers ----------

    private string CreateXls(string name, byte[] workbook, Action<OpenMcdf.RootStorage>? extra = null)
    {
        var path = Path.Combine(_tempDirectory, name);
        using var root = OpenMcdf.RootStorage.Create(path);
        using (var stream = root.CreateStream("Workbook")) stream.Write(workbook);
        extra?.Invoke(root);
        return path;
    }

    private static byte[] ReadWorkbook(string path)
    {
        using var root = OpenMcdf.RootStorage.OpenRead(path);
        return ReadAll(root.OpenStream("Workbook"));
    }

    // Rewrites a stream of the container; transform may replace the content with a longer array.
    private static void PatchStream(string path, string name, Action<byte[]> patch, Func<byte[], byte[]>? transform = null)
    {
        byte[] data;
        using (var root = OpenMcdf.RootStorage.OpenRead(path)) data = ReadAll(root.OpenStream(name));
        patch(data);
        if (transform != null) data = transform(data);
        using var writable = OpenMcdf.RootStorage.Open(path, FileMode.Open);
        using var stream = writable.OpenStream(name);
        stream.SetLength(0);
        stream.Write(data);
    }

    private static byte[] Record(ushort type, byte[] body) =>
        [.. BitConverter.GetBytes(type), .. BitConverter.GetBytes((ushort)body.Length), .. body];

    // BIFF8 globals (BOF, records, EOF) and optionally one worksheet substream.
    private static byte[] BuildXls(IEnumerable<byte[]>? globals = null, IEnumerable<byte[]>? sheet = null)
    {
        var bof = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(bof, 0x0600);
        BinaryPrimitives.WriteUInt16LittleEndian(bof.AsSpan(2), 0x0005);
        var result = new List<byte>(Record(0x0809, bof));
        foreach (var record in globals ?? []) result.AddRange(record);
        result.AddRange(Record(0x000A, []));
        if (sheet == null) return result.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(bof.AsSpan(2), 0x0010);
        result.AddRange(Record(0x0809, bof));
        foreach (var record in sheet) result.AddRange(record);
        result.AddRange(Record(0x000A, []));
        return result.ToArray();
    }

    private static byte[] XlString(string value) =>
        [.. BitConverter.GetBytes((ushort)value.Length), 0, .. Encoding.Latin1.GetBytes(value)];

    private static byte[] HyperlinkString(string value) =>
        [.. BitConverter.GetBytes((uint)value.Length + 1), .. Encoding.Unicode.GetBytes(value + "\0")];

    private static byte[] HyperlinkRecord(uint flags, params byte[][] parts)
    {
        var body = new List<byte>(new byte[8]);
        body.AddRange(StdHlinkClsid);
        body.AddRange(BitConverter.GetBytes(2u));
        body.AddRange(BitConverter.GetBytes(flags));
        foreach (var part in parts) body.AddRange(part);
        return Record(0x01B8, body.ToArray());
    }

    private static byte[] UrlMoniker(string url)
    {
        var bytes = Encoding.Unicode.GetBytes(url + "\0");
        return [.. UrlMonikerClsid, .. BitConverter.GetBytes((uint)bytes.Length), .. bytes];
    }

    private static byte[] FileMoniker(string ansi, string? unicode, ushort cAnti = 0)
    {
        var result = new List<byte>(FileMonikerClsid);
        result.AddRange(BitConverter.GetBytes(cAnti));
        result.AddRange(BitConverter.GetBytes((uint)ansi.Length + 1));
        result.AddRange(Encoding.Latin1.GetBytes(ansi + "\0"));
        result.AddRange(BitConverter.GetBytes((ushort)0xFFFF));
        result.AddRange(BitConverter.GetBytes((ushort)0xDEAD));
        result.AddRange(new byte[20]);
        if (unicode == null)
        {
            result.AddRange(BitConverter.GetBytes(0u));
            return result.ToArray();
        }
        var wide = Encoding.Unicode.GetBytes(unicode);
        result.AddRange(BitConverter.GetBytes((uint)wide.Length + 6));
        result.AddRange(BitConverter.GetBytes((uint)wide.Length));
        result.AddRange(BitConverter.GetBytes((ushort)3));
        result.AddRange(wide);
        return result.ToArray();
    }

    private static byte[] UserBView(byte[] guid, bool personal, string name)
    {
        var body = new byte[50];
        guid.CopyTo(body, 8);
        body[46] = personal ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(48), 0xFFFF);
        return [.. body, .. XlString(name)];
    }

    // OfficeArtBlipJPEG (recInstance 0x46A): header, rgbUid1, tag, then a JPEG with an EXIF APP1 segment.
    private static byte[] JpegBlip()
    {
        byte[] exif = [.. "Exif\0\0"u8, .. "MM\0*\0\0\0\b"u8, 0, 0];
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE1, 0, (byte)(exif.Length + 2), .. exif, 0xFF, 0xD9];
        var header = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(header, 0x46A << 4);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), 0xF01D);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)(17 + jpeg.Length));
        return [.. header, .. new byte[16], 0xFF, .. jpeg];
    }

    private static string ProjectText(string helpFile) =>
        "ID=\"{11111111-2222-3333-4444-555555555555}\"\r\nDocument=ThisWorkbook/&H00000000\r\nName=\"VBAProject\"\r\n" +
        $"HelpFile=\"{helpFile}\"\r\nHelpContextID=\"0\"\r\nVersionCompatible32=\"393222000\"\r\n\r\n" +
        "[Host Extender Info]\r\n&H00000001={3832D640-CF90-11CF-8E43-00A0C911005A};VBE;&H00000000\r\n\r\n" +
        "[Workspace]\r\nThisWorkbook=0, 0, 0, 0, C\r\n";

    // [MS-OVBA] CompressedContainer: "abc" + a copy token (offset 3, length 6) + the text as literals, one chunk.
    private static byte[] CompressVba(string text)
    {
        var tokens = new List<(bool Copy, byte[] Bytes)>
        {
            (false, [(byte)'a']), (false, [(byte)'b']), (false, [(byte)'c']),
            (true, BitConverter.GetBytes((ushort)((2 << 12) | 3)))
        };
        tokens.AddRange(Encoding.ASCII.GetBytes(text).Select(b => (false, new[] { b })));
        var chunk = new List<byte>();
        for (var i = 0; i < tokens.Count; i += 8)
        {
            var group = tokens.Skip(i).Take(8).ToList();
            chunk.Add((byte)group.Select((t, bit) => t.Copy ? 1 << bit : 0).Sum());
            foreach (var token in group) chunk.AddRange(token.Bytes);
        }
        var header = (ushort)(0xB000 | (chunk.Count + 2 - 3));
        return [0x01, .. BitConverter.GetBytes(header), .. chunk];
    }

    private static byte[] BuildSttbWithExtra(ushort extraSize, params string[] strings)
    {
        var result = new List<byte>();
        result.AddRange(BitConverter.GetBytes((ushort)0xFFFF));
        result.AddRange(BitConverter.GetBytes((ushort)strings.Length));
        result.AddRange(BitConverter.GetBytes(extraSize));
        foreach (var value in strings)
        {
            result.AddRange(BitConverter.GetBytes((ushort)value.Length));
            result.AddRange(Encoding.Unicode.GetBytes(value));
            result.AddRange(Enumerable.Repeat((byte)0x11, extraSize));
        }
        return result.ToArray();
    }

    // Directory entries of a compound file (header DIFAT only, enough for small test files).
    private static List<(string Name, long Created, long Modified)> ReadDirectoryEntries(byte[] file)
    {
        var sectorSize = 1 << BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(30));
        uint U32(long offset) => BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan((int)offset));
        var fatSectors = Enumerable.Range(0, 109).Select(i => U32(76 + 4 * i)).TakeWhile(s => s < 0xFFFFFFFA).ToList();
        var perSector = sectorSize / 4;
        uint Next(uint sector) => U32((fatSectors[(int)(sector / perSector)] + 1L) * sectorSize + sector % perSector * 4);
        var entries = new List<(string, long, long)>();
        for (var sector = U32(48); sector < 0xFFFFFFFA; sector = Next(sector))
        {
            for (var e = 0; e < sectorSize / 128; e++)
            {
                var offset = (int)((sector + 1L) * sectorSize) + e * 128;
                if (file[offset + 66] == 0) continue;
                var nameLength = Math.Max(0, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(offset + 64)) - 2);
                entries.Add((Encoding.Unicode.GetString(file, offset, nameLength),
                    BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(offset + 100)),
                    BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(offset + 108))));
            }
        }
        return entries;
    }
}
