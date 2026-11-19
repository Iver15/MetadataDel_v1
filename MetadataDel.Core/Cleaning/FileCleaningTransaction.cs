using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using MetadataDel.Core.CommandLine;

namespace MetadataDel.Core.Cleaning;

// Keep every destructive step on a sibling copy; publish only a complete result.
internal sealed partial class FileCleaningTransaction : IDisposable
{
    /// <summary>Working copies older than this are leftovers of a killed or crashed run.</summary>
    internal static readonly TimeSpan StaleTemporaryAge = TimeSpan.FromMinutes(10);

    private readonly string _path;
    public string WorkingPath { get; }
    public string? BackupPath { get; }

    public FileCleaningTransaction(string path, CleanOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _path = Path.GetFullPath(path);
        if ((File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Очистка символических ссылок не поддерживается. Укажите исходный файл.");
        if (options.Backup) BackupPath = CreateBackup(_path);
        RemoveStaleTemporaryFiles(_path);
        WorkingPath = Path.Combine(Path.GetDirectoryName(_path)!,
            $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp{Path.GetExtension(_path)}");
        try
        {
            File.Copy(_path, WorkingPath);
            // File.Copy keeps the original's mtime; a live copy must not look like a stale leftover to a parallel run.
            File.SetLastWriteTimeUtc(WorkingPath, DateTime.UtcNow);
        }
        catch { Dispose(); throw; }
    }

    /// <summary>
    /// Copies the original into the private per-user backups folder (never next to the document, where it would
    /// travel with the folder). Never replaces an earlier backup: a busy name gets a " (2)", " (3)"… suffix.
    /// </summary>
    /// <returns>Full path of the created backup.</returns>
    internal static string CreateBackup(string path)
    {
        path = Path.GetFullPath(path);
        AppDataPaths.EnsurePrivateDirectory(AppDataPaths.BackupsDirectory);
        var folder = AppDataPaths.EnsurePrivateDirectory(Path.Combine(AppDataPaths.BackupsDirectory, DateTime.Now.ToString("yyyy-MM-dd")));
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 1; ; index++)
        {
            var candidate = Path.Combine(folder, index == 1 ? name + extension : $"{name} ({index}){extension}");
            if (File.Exists(candidate) || Directory.Exists(candidate)) continue;
            // Copy under a private name first: an interrupted copy (disk full) must never look like a complete backup.
            var partial = Path.Combine(folder, $".{Guid.NewGuid():N}.partial");
            try
            {
                File.Copy(path, partial, overwrite: false);
                File.Move(partial, candidate, overwrite: false);
                return candidate;
            }
            // Another process took the same name between the check and the move.
            catch (IOException) when (index < 10_000 && (File.Exists(candidate) || Directory.Exists(candidate))) { }
            finally
            {
                try { if (File.Exists(partial)) File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    public void Commit(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Respect existing readers/locks before replacing the directory entry.
        using (new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Publish(WorkingPath, _path, _path);
    }

    /// <summary>
    /// Moves a finished temporary file onto <paramref name="destination"/> without widening access and without
    /// carrying file-system metadata of the original: Windows keeps the replaced file's ACL via ReplaceFile and then
    /// drops every alternate data stream except a minimal Zone.Identifier; Unix copies mode and owner, and macOS drops
    /// every extended attribute except com.apple.quarantine (download URL, sender, tags, Finder comments).
    /// </summary>
    internal static void Publish(string temporaryPath, string destination, string permissionSource)
    {
        RemoveStalePdfTemporaryFiles(temporaryPath);
        if (OperatingSystem.IsWindows())
        {
            var zoneId = WindowsStreams.ReadZoneId(permissionSource) ?? WindowsStreams.ReadZoneId(temporaryPath);
            WindowsStreams.Sanitize(temporaryPath, zoneId);
            if (File.Exists(destination))
                File.Replace(temporaryPath, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(temporaryPath, destination);
            try { WindowsStreams.Sanitize(destination, zoneId); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not an IOException: callers retry IOExceptions, but the document has already been replaced.
                throw new InvalidOperationException(
                    "Документ записан, но не удалось удалить скрытые потоки NTFS (например, адрес загрузки): " + ex.Message, ex);
            }
            return;
        }
        UnixOwnership.TryCopy(permissionSource, temporaryPath);
        File.SetUnixFileMode(temporaryPath, File.GetUnixFileMode(permissionSource));
        ExtendedAttributes.Sanitize(temporaryPath, permissionSource);
        File.Move(temporaryPath, destination, overwrite: true);
    }

    public void Dispose()
    {
        if (WorkingPath != null && File.Exists(WorkingPath)) File.Delete(WorkingPath);
    }

    internal static string? WipeTimestamps(string path, bool enabled)
    {
        if (!enabled) return null;
        try
        {
            var timestamp = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetCreationTimeUtc(path, timestamp);
            File.SetLastWriteTimeUtc(path, timestamp);
            File.SetLastAccessTimeUtc(path, timestamp);
            return null;
        }
        catch (Exception ex) { return "Не удалось сбросить даты файла: " + ex.Message; }
    }

    /// <summary>
    /// Deletes working copies of THIS document left by a killed run: exactly ".&lt;name&gt;.&lt;32 hex&gt;.tmp&lt;ext&gt;"
    /// (and its ".rebuilt" sibling), regular files only, untouched for at least <see cref="StaleTemporaryAge"/>.
    /// </summary>
    internal static int RemoveStaleTemporaryFiles(string documentPath, DateTime? utcNow = null)
    {
        documentPath = Path.GetFullPath(documentPath);
        var name = Path.GetFileName(documentPath).Normalize(NormalizationForm.FormC);
        var pattern = new Regex(
            "^" + Regex.Escape("." + name + ".") + "[0-9a-f]{32}" + Regex.Escape(".tmp" + Path.GetExtension(name)) + @"(\.rebuilt)?$",
            RegexOptions.CultureInvariant | (OperatingSystem.IsLinux() ? RegexOptions.None : RegexOptions.IgnoreCase));
        return SweepStale(Path.GetDirectoryName(documentPath)!, ".*", pattern, exclude: null, utcNow);
    }

    // PdfCleaner writes ".mdel-<32 hex>.pdf" next to the document; the name does not identify the document,
    // so only exact MetadataDel names that are old enough to be certainly abandoned are removed.
    internal static int RemoveStalePdfTemporaryFiles(string temporaryPath, DateTime? utcNow = null)
    {
        if (!PdfTemporaryName().IsMatch(Path.GetFileName(temporaryPath))) return 0;
        var full = Path.GetFullPath(temporaryPath);
        return SweepStale(Path.GetDirectoryName(full)!, ".mdel-*.pdf", PdfTemporaryName(), full, utcNow);
    }

    [GeneratedRegex(@"^\.mdel-[0-9a-f]{32}\.pdf$", RegexOptions.CultureInvariant)]
    private static partial Regex PdfTemporaryName();

    private static int SweepStale(string directory, string searchPattern, Regex exact, string? exclude, DateTime? utcNow)
    {
        var threshold = (utcNow ?? DateTime.UtcNow) - StaleTemporaryAge;
        var removed = 0;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
                MatchType = MatchType.Simple, MatchCasing = MatchCasing.PlatformDefault
            };
            foreach (var file in Directory.EnumerateFiles(directory, searchPattern, options))
            {
                if (!exact.IsMatch(Path.GetFileName(file).Normalize(NormalizationForm.FormC))) continue;
                if (exclude != null && string.Equals(file, exclude, StringComparison.Ordinal)) continue;
                try
                {
                    var info = new FileInfo(file);
                    if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) continue;
                    if (info.LastWriteTimeUtc >= threshold) continue;
                    info.Delete();
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return removed;
    }
}

/// <summary>macOS/Linux extended attributes of a file that is about to be published.</summary>
internal static class ExtendedAttributes
{
    internal const string Quarantine = "com.apple.quarantine";
    // System-managed attributes the user cannot remove; they carry no document metadata.
    private static readonly HashSet<string> Protected = new(StringComparer.Ordinal)
    {
        "com.apple.provenance", "com.apple.macl", "com.apple.rootless"
    };

    /// <summary>
    /// Attributes that identify the user or the source of the file (download URL, sender, tags, Finder comments,
    /// Linux user.* data). If one of them cannot be removed the file is not published as cleaned. Other attributes
    /// that the volume or a sync provider refuses to remove (File Provider, SMB, FAT) carry no document metadata.
    /// </summary>
    internal static bool MustRemove(string name) =>
        !Protected.Contains(name) &&
        (name.StartsWith("com.apple.metadata:", StringComparison.Ordinal) || name.StartsWith("user.", StringComparison.Ordinal) ||
         name is "com.apple.FinderInfo" or "com.apple.ResourceFork");

    private const int XattrNoFollow = 0x0001;
    private const int MacEperm = 1, MacEacces = 13, MacErange = 34, MacEnotsup = 45, MacEnoattr = 93;
    private const int LinuxEperm = 1, LinuxEacces = 13, LinuxErange = 34, LinuxEnodata = 61, LinuxEnotsup = 95;

    /// <summary>Removes every attribute of <paramref name="path"/> except Gatekeeper's quarantine flag, which is
    /// copied from <paramref name="original"/> when the fresh file lacks it.</summary>
    public static void Sanitize(string path, string original)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;
        try
        {
            var names = List(path);
            foreach (var name in names)
            {
                if (OperatingSystem.IsMacOS() && name == Quarantine) continue;
                // On Linux only the user namespace holds user data; security.* / system.* are ACLs and labels.
                if (OperatingSystem.IsLinux() && !name.StartsWith("user.", StringComparison.Ordinal)) continue;
                if (Remove(path, name) is var errno and not 0 && !IsMissing(errno) && !(IsDenied(errno) && !MustRemove(name)))
                    throw new IOException($"Не удалось удалить расширенный атрибут файла «{name}» (код {errno}).");
            }
            if (OperatingSystem.IsMacOS() && !names.Contains(Quarantine) && Get(original, Quarantine) is { } quarantine)
                Set(path, Quarantine, quarantine);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    /// <summary>Names of the attributes of <paramref name="path"/> (not following links).</summary>
    public static List<string> List(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var size = OperatingSystem.IsMacOS() ? MacList(path, null, 0, XattrNoFollow) : LinuxList(path, null, 0);
            if (size < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno == (OperatingSystem.IsMacOS() ? MacEnotsup : LinuxEnotsup)) return [];
                throw new IOException($"Не удалось прочитать расширенные атрибуты файла (код {errno}).");
            }
            if (size == 0) return [];
            var buffer = new byte[size];
            var read = OperatingSystem.IsMacOS() ? MacList(path, buffer, size, XattrNoFollow) : LinuxList(path, buffer, size);
            if (read < 0)
            {
                if (Marshal.GetLastPInvokeError() == (OperatingSystem.IsMacOS() ? MacErange : LinuxErange)) continue;
                throw new IOException("Не удалось прочитать расширенные атрибуты файла.");
            }
            return Encoding.UTF8.GetString(buffer, 0, (int)read).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        throw new IOException("Расширенные атрибуты файла меняются во время чтения.");
    }

    public static byte[]? Get(string path, string name)
    {
        try
        {
            var size = OperatingSystem.IsMacOS() ? MacGet(path, name, null, 0, 0, XattrNoFollow) : LinuxGet(path, name, null, 0);
            if (size < 0) return null;
            var buffer = new byte[size];
            var read = OperatingSystem.IsMacOS() ? MacGet(path, name, buffer, size, 0, XattrNoFollow) : LinuxGet(path, name, buffer, size);
            return read < 0 ? null : buffer[..(int)read];
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    public static bool Set(string path, string name, byte[] value) =>
        (OperatingSystem.IsMacOS() ? MacSet(path, name, value, value.Length, 0, XattrNoFollow) : LinuxSet(path, name, value, value.Length, 0)) == 0;

    private static int Remove(string path, string name) =>
        (OperatingSystem.IsMacOS() ? MacRemove(path, name, XattrNoFollow) : LinuxRemove(path, name)) == 0 ? 0 : Math.Max(1, Marshal.GetLastPInvokeError());

    private static bool IsMissing(int errno) => errno == (OperatingSystem.IsMacOS() ? MacEnoattr : LinuxEnodata);
    private static bool IsDenied(int errno) => OperatingSystem.IsMacOS()
        ? errno is MacEperm or MacEacces or MacEnotsup
        : errno is LinuxEperm or LinuxEacces or LinuxEnotsup;

    [DllImport("libc", EntryPoint = "listxattr", SetLastError = true)]
    private static extern nint MacList([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[]? buffer, nint size, int options);
    [DllImport("libc", EntryPoint = "getxattr", SetLastError = true)]
    private static extern nint MacGet([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte[]? value, nint size, uint position, int options);
    [DllImport("libc", EntryPoint = "setxattr", SetLastError = true)]
    private static extern int MacSet([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte[] value, nint size, uint position, int options);
    [DllImport("libc", EntryPoint = "removexattr", SetLastError = true)]
    private static extern int MacRemove([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int options);

    [DllImport("libc", EntryPoint = "llistxattr", SetLastError = true)]
    private static extern nint LinuxList([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[]? buffer, nint size);
    [DllImport("libc", EntryPoint = "lgetxattr", SetLastError = true)]
    private static extern nint LinuxGet([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte[]? value, nint size);
    [DllImport("libc", EntryPoint = "lsetxattr", SetLastError = true)]
    private static extern int LinuxSet([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte[] value, nint size, int flags);
    [DllImport("libc", EntryPoint = "lremovexattr", SetLastError = true)]
    private static extern int LinuxRemove([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
}

/// <summary>Keeps the original owner/group on Unix; best effort, a regular user may only pick own groups.</summary>
internal static class UnixOwnership
{
    public static void TryCopy(string source, string target)
    {
        try
        {
            if (Stat(source) is not { } wanted || Stat(target) is not { } current || wanted == current) return;
            if (Lchown(target, wanted.Uid, wanted.Gid) != 0 && wanted.Gid != current.Gid)
                Lchown(target, uint.MaxValue, wanted.Gid); // EPERM for a foreign owner: keep at least the group.
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    internal static (uint Uid, uint Gid)? Stat(string path)
    {
        // struct stat offsets of st_uid/st_gid: macOS (64-bit inode) 16/20, Linux x64 28/32, Linux arm64 24/28.
        (int uid, int gid)? offsets = (OperatingSystem.IsMacOS(), RuntimeInformation.ProcessArchitecture) switch
        {
            (true, Architecture.Arm64 or Architecture.X64) => (16, 20),
            (false, Architecture.X64) when OperatingSystem.IsLinux() => (28, 32),
            (false, Architecture.Arm64) when OperatingSystem.IsLinux() => (24, 28),
            _ => null
        };
        if (offsets is not { } at) return null;
        var buffer = new byte[512];
        var status = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? MacLstatInode64(path, buffer) : Lstat(path, buffer);
        if (status != 0) return null;
        return (BitConverter.ToUInt32(buffer, at.uid), BitConverter.ToUInt32(buffer, at.gid));
    }

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int Lstat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);
    [DllImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true)]
    private static extern int MacLstatInode64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);
    [DllImport("libc", EntryPoint = "lchown", SetLastError = true)]
    private static extern int Lchown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint owner, uint group);
}

/// <summary>NTFS alternate data streams. File.Replace merges the replaced file's streams into the result.</summary>
internal static class WindowsStreams
{
    private const string ZoneStream = ":Zone.Identifier:$DATA";

    /// <summary>ZoneId of the Mark of the Web, 3 (Internet) when the stream exists without a readable value.</summary>
    public static int? ReadZoneId(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            if (!EnumerateNamedStreams(path).Any(s => s.Equals(ZoneStream, StringComparison.OrdinalIgnoreCase))) return null;
        }
        catch (IOException) { return null; }
        try
        {
            foreach (var line in File.ReadAllLines(path + ":Zone.Identifier"))
            {
                var parts = line.Split('=', 2);
                if (parts.Length == 2 && parts[0].Trim().Equals("ZoneId", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(parts[1].Trim(), out var zone) && zone is >= 0 and <= 4)
                    return zone;
            }
            return 3;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 3; }
    }

    /// <summary>Deletes every named stream; writes back only "[ZoneTransfer] ZoneId=N" (no ReferrerUrl/HostUrl).</summary>
    public static void Sanitize(string path, int? zoneId)
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var stream in EnumerateNamedStreams(path))
        {
            var name = stream.EndsWith(":$DATA", StringComparison.OrdinalIgnoreCase) ? stream[..^":$DATA".Length] : stream;
            if (!DeleteFileW(ExtendedLengthPath(path) + name))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error != 2) throw new IOException($"Не удалось удалить поток «{name.TrimStart(':')}» (код {error}).");
            }
        }
        if (zoneId is { } zone)
            File.WriteAllText(path + ":Zone.Identifier", $"[ZoneTransfer]\r\nZoneId={zone}\r\n", Encoding.ASCII);
    }

    /// <summary>Win32 path that is not limited to MAX_PATH: \\?\C:\… or \\?\UNC\server\share\….</summary>
    internal static string ExtendedLengthPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal)) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    /// <summary>Stream names like ":Zone.Identifier:$DATA"; the unnamed main stream "::$DATA" is skipped.</summary>
    internal static List<string> EnumerateNamedStreams(string path)
    {
        var streams = new List<string>();
        var handle = FindFirstStreamW(ExtendedLengthPath(path), 0, out var data, 0);
        if (handle == InvalidHandle)
        {
            var error = Marshal.GetLastPInvokeError();
            // 38 = ERROR_HANDLE_EOF (no streams); 1 = ERROR_INVALID_FUNCTION, 50 = ERROR_NOT_SUPPORTED,
            // 87 = ERROR_INVALID_PARAMETER, 124 = ERROR_INVALID_LEVEL: the volume (FAT, NAS, some SMB) has no named streams.
            if (error is 38 or 1 or 50 or 87 or 124) return streams;
            throw new IOException($"Не удалось прочитать потоки файла (код {error}).");
        }
        try
        {
            do
            {
                if (!string.Equals(data.StreamName, "::$DATA", StringComparison.OrdinalIgnoreCase)) streams.Add(data.StreamName);
            } while (FindNextStreamW(handle, out data));
            var last = Marshal.GetLastPInvokeError();
            if (last != 38) throw new IOException($"Не удалось прочитать потоки файла (код {last}).");
        }
        finally { FindClose(handle); }
        return streams;
    }

    private static readonly nint InvalidHandle = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FindStreamData
    {
        public long StreamSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string StreamName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindFirstStreamW(string fileName, int infoLevel, out FindStreamData data, int flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStreamW(nint handle, out FindStreamData data);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteFileW(string fileName);
}
