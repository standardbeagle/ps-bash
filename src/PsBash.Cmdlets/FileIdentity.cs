using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PsBash.Cmdlets;

/// <summary>
/// "Are these the same file?" beyond comparing spellings: two NAMES for one file (a hard link), and
/// a symbolic link that leads to the other operand. OS-interface helper for <c>cp</c> / <c>mv</c>, so
/// the platform branches live here (the copy commands only ask the question).
/// <para>
/// GNU's rules, oracle-checked: <b>cp</b> follows links on both sides, so <c>cp a b</c> is refused when
/// <c>b</c> is a hard link to <c>a</c>, or either is a symlink resolving to the other.
/// <b>mv</b> compares the ENTRIES themselves (no following): hard links of one inode are "the same
/// file", and so is a symlink moved onto its own target (<c>mv link target</c>); but moving a real
/// file onto a symlink that merely points at it is fine — the symlink is replaced.
/// </para>
/// <para>Identity source: Windows asks the volume serial + file index of an open handle; Unix asks
/// <c>stat</c> for device:inode through <see cref="BashRuntime.RunChildProcess(string, IReadOnlyList{string}?, TimeSpan?)"/>
/// (bounded, kill-tree), after a cheap size + mtime prefilter — hard links always agree on both —
/// so a copy into an existing tree does not spawn a process per file.</para>
/// </summary>
internal static class FileIdentity
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>cp semantics: the two operands are one file once every link on either side is followed.</summary>
    public static bool SameFileFollowingLinks(string source, string destination)
    {
        if (!IsExistingFile(source) || !IsExistingFile(destination)) return false;
        var a = FileTimes.ResolveFinalTarget(source);
        var b = FileTimes.ResolveFinalTarget(destination);
        return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), PathComparison) || SameInode(a, b);
    }

    /// <summary>mv semantics: hard links of one inode, or a symlink moved onto the file it points at.</summary>
    public static bool SameEntryNotFollowing(string source, string destination)
    {
        bool sourceIsLink = FileTimes.IsLink(source);
        bool destinationIsLink = FileTimes.IsLink(destination);

        if (sourceIsLink)
        {
            // `mv link target`: the link resolves to the destination itself.
            var resolved = FileTimes.ResolveFinalTarget(source);
            return !destinationIsLink
                && string.Equals(Path.GetFullPath(resolved), Path.GetFullPath(destination), PathComparison);
        }

        // A real file onto a symlink that points at it: the symlink is replaced, not "the same file".
        if (destinationIsLink) return false;
        return IsExistingFile(source) && IsExistingFile(destination) && SameInode(source, destination);
    }

    private static bool IsExistingFile(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }

    /// <summary>True when the two existing files share one inode / file index (hard links).</summary>
    private static bool SameInode(string a, string b)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return TryGetWindowsId(a, out var ida) && TryGetWindowsId(b, out var idb) && ida == idb;
            }

            // Hard links agree on size and modification time: rule out the overwhelmingly common
            // "different files" case without spawning anything.
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (fa.Length != fb.Length || fa.LastWriteTimeUtc != fb.LastWriteTimeUtc) return false;

            var ia = TryGetUnixId(a);
            return ia is not null && ia == TryGetUnixId(b);
        }
        catch
        {
            return false; // unknown: fall back to "different", never block a copy on a probe failure
        }
    }

    /// <summary>
    /// The inode number <c>ls -i</c> prints: st_ino on Unix (via <c>stat</c>), the NTFS file index on
    /// Windows. False when it cannot be read.
    /// </summary>
    public static bool TryGetInode(string path, out ulong inode)
    {
        inode = 0;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!TryGetWindowsId(path, out var id)) return false;
                inode = id.Index;
                return true;
            }

            var linux = BashRuntime.RunChildProcess("stat", new[] { "-c", "%i", "--", path }, TimeSpan.FromSeconds(10));
            if (linux.ExitCode == 0 && !linux.TimedOut && ulong.TryParse(linux.Stdout.Trim(), out inode)) return true;
            var bsd = BashRuntime.RunChildProcess("stat", new[] { "-f", "%i", path }, TimeSpan.FromSeconds(10));
            return bsd.ExitCode == 0 && !bsd.TimedOut && ulong.TryParse(bsd.Stdout.Trim(), out inode);
        }
        catch
        {
            return false;
        }
    }

    // ───────────── Unix ─────────────

    private static string? TryGetUnixId(string path)
    {
        // Linux/coreutils: -c FORMAT; BSD/macOS: -f FORMAT. `--` ends options for a path starting with '-'.
        var linux = BashRuntime.RunChildProcess("stat", new[] { "-c", "%d:%i", "--", path }, TimeSpan.FromSeconds(10));
        if (linux.ExitCode == 0 && !linux.TimedOut && linux.Stdout.Trim() is { Length: > 0 } li) return li;

        var bsd = BashRuntime.RunChildProcess("stat", new[] { "-f", "%d:%i", path }, TimeSpan.FromSeconds(10));
        return bsd.ExitCode == 0 && !bsd.TimedOut && bsd.Stdout.Trim() is { Length: > 0 } bi ? bi : null;
    }

    // ───────────── Windows ─────────────

    private const uint ShareAll = 0x7;            // read | write | delete
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    private static bool TryGetWindowsId(string path, out (uint Volume, ulong Index) id)
    {
        id = default;
        // Access 0: ask for metadata only, so a file another process holds open is still readable.
        using var handle = CreateFileW(path, 0, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) return false;
        id = (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
        return true;
    }
}
