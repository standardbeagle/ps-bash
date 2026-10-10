using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PsBash.Cmdlets;

/// <summary>
/// The rename / replace / copy primitives behind <c>mv</c> and <c>cp</c>, made to behave like POSIX on
/// Windows. Linux <c>rename(2)</c> replaces whatever the destination name holds and never cares who has a
/// file open; Windows refuses a read-only destination, one a process holds open, and a running program.
/// So, in this order:
/// <list type="bullet">
/// <item><b>Replace by moving aside</b>: a destination Windows will not overwrite in place is renamed out of
/// the way (a running <c>.exe</c> CAN be renamed), the new file takes the name, and the old one is deleted —
/// later, if its process still has it. Every step is undone if the next one fails.</item>
/// <item><b>Hard limits fail fast</b>: a file another process holds open without sharing delete cannot be
/// renamed or removed at all. Restart Manager names the process, and the error says so at once
/// (<c>Device or resource busy (open in beagle-term.exe, pid 4242)</c>) instead of a raw .NET message.</item>
/// <item><b>Transient locks are waited out</b>: with no process to blame (an antivirus or indexer scan),
/// the operation is retried for about a second.</item>
/// <item><b>Directories across volumes</b> are copied and then removed, after a check that every file in the
/// tree can be removed — so a held file is reported before anything is copied.</item>
/// </list>
/// Off Windows every call is the plain .NET operation.
/// </summary>
public static class FileTransfer
{
    private const int AccessDenied = 5, SharingViolation = 32, LockViolation = 33, NotSameDevice = 17,
        WriteProtect = 19, HandleDiskFull = 39, DiskFull = 112, DirNotEmpty = 145, NameTooLong = 206,
        FileNotFound = 2, PathNotFound = 3, InvalidName = 123;

    /// <summary>Total wait for a lock nobody can be blamed for: a sharing violation, and an access
    /// denial (which is also what a real ACL refusal looks like, so it gets less).</summary>
    private const int SharingBudgetMs = 1000, DeniedBudgetMs = 250;

    /// <summary>Processes whose handles come and go by themselves (scanners): never a hard limit.</summary>
    private static readonly string[] TransientHolders =
        { "MsMpEng", "MsSense", "SearchProtocolHost", "SearchIndexer", "SearchFilterHost", "NisSrv" };

    // ───────────── operations ─────────────

    /// <summary>
    /// <c>mv</c> of one non-directory: the POSIX rename, replacing an existing destination even when it is
    /// read-only or in use. Crosses volumes (File.Move copies and deletes).
    /// </summary>
    public static void MoveFile(string src, string dest) =>
        WithLockRetry(() =>
        {
            try { File.Move(src, dest, overwrite: true); }
            catch (Exception ex) when (CanReplaceAside(ex, src, dest))
            {
                ReplaceAside(dest, () => File.Move(src, dest, overwrite: true), () => File.Move(src, dest),
                    clearReadOnly: true, ex);
            }
        }, () => ExistingOf(src, dest));

    /// <summary>
    /// <c>cp</c> of one file's bytes onto <paramref name="dest"/>. GNU writes into an existing destination;
    /// when Windows refuses that (a process has it open without sharing writes, or it is a running program),
    /// the old file is moved aside and the copy takes its name — what <c>cp --remove-destination</c> does.
    /// A READ-ONLY destination is not forced: that is GNU's "Permission denied" without <c>-f</c>. Neither is
    /// one with other hard links.
    /// </summary>
    public static void CopyFile(string src, string dest)
    {
        if (File.Exists(dest) && IsReadOnly(dest))
        {
            File.Copy(src, dest, overwrite: true);   // a certain refusal: report it without waiting
            return;
        }
        WithLockRetry(() =>
        {
            try { File.Copy(src, dest, overwrite: true); }
            catch (Exception ex) when (CanReplaceAside(ex, src, dest) && !IsReadOnly(dest) && !HasOtherLinks(dest))
            {
                ReplaceAside(dest, () => File.Copy(src, dest, overwrite: true), () => File.Copy(src, dest, overwrite: false),
                    clearReadOnly: false, ex);
            }
        }, () => ExistingOf(src, dest));
    }

    /// <summary>
    /// <c>mv</c> of a directory to a name that does not exist. A rename on one volume; across volumes, a
    /// checked copy then removal of the source (<see cref="SourceNotRemovedException"/> when that removal
    /// fails part-way — the copy is complete, as GNU leaves it).
    /// </summary>
    public static void MoveDirectory(string src, string dest)
    {
        if (!SameVolume(src, dest))
        {
            MoveDirectoryAcrossVolumes(src, dest);
            return;
        }
        try
        {
            WithLockRetry(() => Directory.Move(src, dest), () => HeldFilesUnder(src));
        }
        catch (IOException ex) when (Code(ex) == NotSameDevice)
        {
            MoveDirectoryAcrossVolumes(src, dest);   // a mount point inside one drive letter
        }
    }

    /// <summary>
    /// Renames <paramref name="path"/> (file or directory) to a hidden sibling so its name is free, for a
    /// caller that must be able to put it back (<see cref="Restore"/>) or drop it (<see cref="Discard"/>).
    /// Null when it cannot be renamed.
    /// </summary>
    public static string? MoveAside(string path)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var aside = Path.Combine(parent, AsideName(path));
        try
        {
            if (Directory.Exists(path)) Directory.Move(path, aside);
            else RenameNoCopy(path, aside);
            return aside;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Puts a <see cref="MoveAside"/> entry back under its name (best-effort: the undo path).</summary>
    public static void Restore(string aside, string path)
    {
        try
        {
            if (Directory.Exists(aside)) Directory.Move(aside, path);
            else RenameNoCopy(aside, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Removes a <see cref="MoveAside"/> entry. One still in use (a running program) cannot be deleted on
    /// Windows: it stays hidden and is removed by the next replace in the same directory once free.
    /// </summary>
    public static void Discard(string aside)
    {
        try
        {
            if (Directory.Exists(aside)) FileSystemHelpers.DeleteDirectoryForce(aside);
            else FileSystemHelpers.DeleteFileForce(aside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.SetAttributes(aside, File.GetAttributes(aside) | FileAttributes.Hidden); }
            catch (Exception ex2) when (ex2 is IOException or UnauthorizedAccessException) { }
        }
    }

    // ───────────── errors ─────────────

    /// <summary>
    /// The bash-style reason for a failed transfer: the errno text GNU would print, and for a file held
    /// open by another process, which process (Windows has no errno for "in use"; EBUSY is the nearest).
    /// <paramref name="root"/>/<paramref name="rootDisplay"/> name a held file inside a directory operand
    /// as the user typed the directory.
    /// </summary>
    public static string Reason(Exception ex, string? root = null, string? rootDisplay = null)
    {
        if (ex is FileBusyException busy) return busy.Describe(root, rootDisplay);
        if (ex is SourceNotRemovedException removed) return Reason(removed.InnerException ?? removed, root, rootDisplay);
        if (ex is FileNotFoundException or DirectoryNotFoundException) return "No such file or directory";
        if (ex is UnauthorizedAccessException) return "Permission denied";
        if (ex is not IOException) return ex.Message;
        return Code(ex) switch
        {
            FileNotFound or PathNotFound or InvalidName => "No such file or directory",
            SharingViolation or LockViolation => "Device or resource busy (open in another process)",
            AccessDenied => "Permission denied",
            HandleDiskFull or DiskFull => "No space left on device",
            NameTooLong => "File name too long",
            WriteProtect => "Read-only file system",
            NotSameDevice => "Invalid cross-device link",
            DirNotEmpty => "Directory not empty",
            _ => ex.Message,
        };
    }

    /// <summary>A file another process holds open — a hard limit, raised as soon as it is seen.</summary>
    public sealed class FileBusyException : IOException
    {
        internal FileBusyException(Exception inner, IReadOnlyList<LockHolders.Holder> holders, string path)
            : base(inner.Message, inner)
        {
            Holders = holders;
            Path = path;
            HResult = inner.HResult;
        }

        internal IReadOnlyList<LockHolders.Holder> Holders { get; }

        /// <summary>The held file (absolute).</summary>
        public string Path { get; }

        internal string Describe(string? root, string? rootDisplay)
        {
            var sb = new StringBuilder("Device or resource busy (");
            // Name the held file unless it IS the operand the message is about: a file inside a directory
            // operand as typed, any other (the destination) by its path.
            if (root is not null && rootDisplay is not null && IsUnder(Path, root))
                sb.Append('\'').Append(FileSystemHelpers.ToBashPath(rootDisplay.TrimEnd('/', '\\') + "/"
                    + System.IO.Path.GetRelativePath(root, Path))).Append("' ");
            else if (root is not null && !TransferValidation.IsSamePath(Path, root))
                sb.Append('\'').Append(FileSystemHelpers.ToBashPath(Path)).Append("' ");
            sb.Append("open in ");
            if (Holders.Count == 0) sb.Append("another process");
            for (int i = 0; i < Holders.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Holders[i].Name).Append(", pid ").Append(Holders[i].Pid);
            }
            return sb.Append(')').ToString();
        }

        private static bool IsUnder(string path, string root)
        {
            var r = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
            return path.Length > r.Length && path.StartsWith(r, StringComparison.OrdinalIgnoreCase)
                && path[r.Length] is '\\' or '/';
        }
    }

    /// <summary>A directory moved across volumes was copied, but removing the source failed.</summary>
    public sealed class SourceNotRemovedException : IOException
    {
        internal SourceNotRemovedException(Exception inner) : base(inner.Message, inner) => HResult = inner.HResult;
    }

    // ───────────── lock handling ─────────────

    /// <summary>
    /// Runs <paramref name="op"/>. A lock-shaped failure (sharing / lock violation, access denied) is a hard
    /// limit at once when a process other than a scanner holds one of <paramref name="candidates"/>; with
    /// nobody to blame it is retried with backoff for a short budget, then rethrown.
    /// </summary>
    internal static void WithLockRetry(Action op, Func<IReadOnlyList<string>> candidates)
    {
        var clock = Stopwatch.StartNew();
        int delay = 20;
        bool asked = false;
        while (true)
        {
            try
            {
                op();
                return;
            }
            catch (Exception ex) when (IsLockShaped(ex))
            {
                if (!asked)
                {
                    asked = true;
                    foreach (var path in candidates())
                    {
                        var holders = LockHolders.Find(path);
                        if (holders.Count > 0 && !holders.All(h => IsTransient(h.Name)))
                            throw new FileBusyException(ex, holders, path);
                    }
                }
                int budget = Code(ex) == AccessDenied ? DeniedBudgetMs : SharingBudgetMs;
                if (clock.ElapsedMilliseconds + delay > budget) throw;
                Thread.Sleep(delay);
                delay = Math.Min(delay * 2, 200);
            }
        }
    }

    internal static bool IsTransient(string processName)
    {
        var name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;
        return Array.Exists(TransientHolders, t => t.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsLockShaped(Exception ex) =>
        OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException
        && ex is not FileBusyException && Code(ex) is AccessDenied or SharingViolation or LockViolation;

    private static int Code(Exception ex) => ex.HResult & 0xFFFF;

    private static bool CanReplaceAside(Exception ex, string src, string dest) =>
        IsLockShaped(ex) && File.Exists(dest) && !TransferValidation.IsSamePath(src, dest);

    private static bool IsReadOnly(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;

    // GNU cp writes INTO a hard-linked destination, so every name sees the new bytes; a replaced file
    // would leave the other names on the old content. Not done: that one stays a "busy" error.
    private static bool HasOtherLinks(string path) => FileIdentity.TryGetLinkCount(path, out var n) && n > 1;

    /// <summary>
    /// Frees <paramref name="dest"/> and runs <paramref name="place"/> to fill it. For a read-only destination
    /// (mv), clearing the attribute and re-running <paramref name="overwrite"/> is tried first (no residue);
    /// then the old file is renamed aside. Nothing is deleted until the new file is in place, and any
    /// failure puts the old one back.
    /// </summary>
    private static void ReplaceAside(string dest, Action overwrite, Action place, bool clearReadOnly, Exception original)
    {
        var attributes = File.GetAttributes(dest);
        bool readOnly = (attributes & FileAttributes.ReadOnly) != 0;
        if (clearReadOnly && readOnly)
        {
            File.SetAttributes(dest, attributes & ~FileAttributes.ReadOnly);
            try
            {
                overwrite();
                return;
            }
            catch (Exception ex) when (IsLockShaped(ex))
            {
                // Still in use as well: fall through to the move aside.
            }
        }

        SweepAsides(dest);
        var aside = File.Exists(dest) ? MoveAside(dest) : null;
        if (aside is null && File.Exists(dest))
        {
            if (clearReadOnly && readOnly) TrySetAttributes(dest, attributes);
            ExceptionDispatchInfo.Throw(original);
        }
        try
        {
            place();
        }
        catch
        {
            if (aside is not null)
            {
                Restore(aside, dest);
                if (clearReadOnly && readOnly) TrySetAttributes(dest, attributes);
            }
            throw;
        }
        if (aside is not null) Discard(aside);
    }

    private static void TrySetAttributes(string path, FileAttributes attributes)
    {
        try { File.SetAttributes(path, attributes); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private const string AsideMarker = ".psbash-replaced-";

    private static string AsideName(string path) =>
        "." + Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) + AsideMarker + Guid.NewGuid().ToString("N")[..8];

    /// <summary>Removes earlier move-aside leftovers next to <paramref name="path"/> whose process has let go.</summary>
    private static void SweepAsides(string path)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (parent is null) return;
        try
        {
            foreach (var old in Directory.EnumerateFiles(parent, "." + "*" + AsideMarker + "*",
                         new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }))
            {
                try { FileSystemHelpers.DeleteFileForce(old); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static IReadOnlyList<string> ExistingOf(string a, string b)
    {
        var list = new List<string>(2);
        if (File.Exists(a)) list.Add(Path.GetFullPath(a));
        if (File.Exists(b)) list.Add(Path.GetFullPath(b));
        return list;
    }

    // ───────────── directories ─────────────

    /// <summary>Files under <paramref name="dir"/> that block moving or removing it: ones a process holds
    /// open without sharing delete, else (a mapped image blocks the directory but not a delete-access open)
    /// its programs and libraries. Bounded: a huge tree is sampled, not walked.</summary>
    internal static IReadOnlyList<string> HeldFilesUnder(string dir)
    {
        const int MaxScanned = 20000, MaxHeld = 8, MaxImages = 64;
        var held = new List<string>();
        var images = new List<string>();
        if (!OperatingSystem.IsWindows()) return held;
        int scanned = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var file in Directory.EnumerateFiles(dir, "*", options))
        {
            if (++scanned > MaxScanned || held.Count >= MaxHeld) break;
            if (IsHeldOpen(file)) held.Add(file);
            else if (images.Count < MaxImages && (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                         || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                images.Add(file);
        }
        return held.Count > 0 ? held : images;
    }

    /// <summary>The copy-then-remove move (internal: a unit-test seam — one volume cannot reach it).</summary>
    internal static void MoveDirectoryAcrossVolumes(string src, string dest)
    {
        // Check BEFORE copying: a held file would otherwise be found only when removing the source, with
        // the tree already on both volumes.
        foreach (var path in HeldFilesUnder(src))
        {
            if (!IsHeldOpen(path)) continue;
            var holders = LockHolders.Find(path);
            throw new FileBusyException(new IOException("in use", unchecked((int)0x80070020)), holders, path);
        }
        CopyTree(src, dest);
        try
        {
            FileSystemHelpers.DeleteDirectoryForce(src);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SourceNotRemovedException(ex);
        }
    }

    /// <summary>A full copy for a move: links as links, attributes and all three timestamps kept.</summary>
    private static void CopyTree(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var entry in new DirectoryInfo(src).EnumerateFileSystemInfos("*",
                     new EnumerationOptions { AttributesToSkip = 0 }))
        {
            var target = Path.Combine(dest, entry.Name);
            if (entry.LinkTarget is { } link)
            {
                var reason = FileLinks.TryCreateSymlink(target, link, entry is DirectoryInfo);
                if (reason is not null) throw new IOException($"cannot create symbolic link '{target}': {reason}");
            }
            else if (entry is DirectoryInfo sub)
            {
                CopyTree(sub.FullName, target);
            }
            else
            {
                File.Copy(entry.FullName, target, overwrite: false);
                CopyTimes(entry, new FileInfo(target));
            }
        }
        var from = new DirectoryInfo(src);
        var to = new DirectoryInfo(dest);
        CopyTimes(from, to);
        to.Attributes = from.Attributes;
    }

    private static void CopyTimes(FileSystemInfo from, FileSystemInfo to)
    {
        to.CreationTimeUtc = from.CreationTimeUtc;
        to.LastWriteTimeUtc = from.LastWriteTimeUtc;
        to.LastAccessTimeUtc = from.LastAccessTimeUtc;
    }

    private static bool SameVolume(string src, string dest)
    {
        var destParent = Path.GetDirectoryName(Path.GetFullPath(dest)) ?? dest;
        var a = FileIdentity.TryGetDeviceId(src);
        var b = FileIdentity.TryGetDeviceId(destParent);
        if (a is not null && b is not null) return a == b;
        return string.Equals(Path.GetPathRoot(Path.GetFullPath(src)), Path.GetPathRoot(Path.GetFullPath(dest)),
            StringComparison.OrdinalIgnoreCase);
    }

    // ───────────── Win32 ─────────────

    /// <summary>True when another process has <paramref name="path"/> open without sharing delete: the
    /// handle that makes a rename or delete fail. Opens for DELETE access only; nothing is changed.</summary>
    internal static bool IsHeldOpen(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var handle = CreateFileW(path, DELETE | FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero,
            OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (!handle.IsInvalid) return false;
        return Marshal.GetLastWin32Error() is SharingViolation or LockViolation;
    }

    /// <summary>A same-volume rename (never a copy, unlike File.Move): moving an in-use file aside.</summary>
    private static void RenameNoCopy(string from, string to)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.Move(from, to);
            return;
        }
        if (!MoveFileExW(from, to, 0))
            throw new IOException($"cannot rename '{from}'", Marshal.GetHRForLastWin32Error());
    }

    private const uint DELETE = 0x00010000, FILE_READ_ATTRIBUTES = 0x80, FILE_SHARE_ALL = 7, OPEN_EXISTING = 3,
        FILE_FLAG_BACKUP_SEMANTICS = 0x02000000, FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string existing, string? replacement, int flags);
}

/// <summary>
/// Which processes hold a file open — Windows Restart Manager, the API behind "this file is in use by …"
/// dialogs. Used only on a failure path, so the per-call session cost does not matter.
/// </summary>
internal static class LockHolders
{
    internal readonly record struct Holder(int Pid, string Name);

    /// <summary>The processes with <paramref name="path"/> open; empty off Windows or when unknown.</summary>
    public static IReadOnlyList<Holder> Find(string path)
    {
        var found = new List<Holder>();
        if (!OperatingSystem.IsWindows()) return found;
        var key = new StringBuilder(CCH_RM_SESSION_KEY + 1);
        if (RmStartSession(out uint session, 0, key) != 0) return found;
        try
        {
            if (RmRegisterResources(session, 1, new[] { path }, 0, IntPtr.Zero, 0, IntPtr.Zero) != 0) return found;
            uint needed = 0, count = 0, reasons = 0;
            int rc = RmGetList(session, out needed, ref count, null, ref reasons);
            if (rc == ERROR_MORE_DATA && needed > 0)
            {
                var info = new RM_PROCESS_INFO[needed];
                count = needed;
                rc = RmGetList(session, out needed, ref count, info, ref reasons);
                if (rc == 0)
                {
                    for (int i = 0; i < count; i++)
                        found.Add(new Holder(info[i].Process.dwProcessId, NameOf(info[i])));
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        finally
        {
            RmEndSession(session);
        }
        return found;
    }

    private static string NameOf(RM_PROCESS_INFO info)
    {
        try
        {
            using var p = Process.GetProcessById(info.Process.dwProcessId);
            return p.ProcessName + ".exe";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return info.strAppName;
        }
    }

    private const int CCH_RM_SESSION_KEY = 32, ERROR_MORE_DATA = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
        uint nApplications, IntPtr rgApplications, uint nServices, IntPtr rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, ref uint lpdwRebootReasons);
}
