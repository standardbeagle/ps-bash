using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PsBash.Cmdlets;

/// <summary>
/// Setting file timestamps, including the one thing .NET cannot do portably: stamping a symbolic
/// link / junction ITSELF (<c>touch -h</c>) instead of what it points at. OS-interface helper for
/// <c>touch</c>; the platform branches live here, not in the cmdlet.
/// <list type="bullet">
/// <item><b>Following</b> (default): the link is resolved to its final target first, on every
/// platform, and the target is stamped — never relying on whether the runtime's own setters follow.</item>
/// <item><b>Not following</b>: Windows opens the reparse point itself
/// (<c>FILE_FLAG_OPEN_REPARSE_POINT</c>) and calls <c>SetFileTime</c>; Linux/macOS call
/// <c>utimensat(..., AT_SYMLINK_NOFOLLOW)</c>.</item>
/// </list>
/// A null time leaves that timestamp as it is.
/// </summary>
internal static class FileTimes
{
    /// <summary>True when <paramref name="path"/> is itself a symlink / junction (whether or not its target exists).</summary>
    public static bool IsLink(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.LinkTarget is not null;
        }
        catch { return false; }
    }

    /// <summary>
    /// The path whose timestamps a FOLLOWING touch changes: the final target of a link chain (even
    /// when it does not exist yet — touching a dangling link creates its target), else the path itself.
    /// </summary>
    public static string ResolveFinalTarget(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (info.LinkTarget is null) return path;
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
        }
        catch { return path; }
    }

    /// <summary>Stamps <paramref name="path"/>; for a link, <paramref name="noFollow"/> picks the link or its target.</summary>
    public static void Set(string path, DateTime? access, DateTime? modify, bool noFollow)
    {
        bool isLink = IsLink(path);
        if (noFollow && isLink)
        {
            SetOnLinkItself(path, access?.ToUniversalTime(), modify?.ToUniversalTime());
            return;
        }

        var target = isLink ? ResolveFinalTarget(path) : path;
        if (Directory.Exists(target))
        {
            if (modify is { } m) Directory.SetLastWriteTime(target, m);
            if (access is { } a) Directory.SetLastAccessTime(target, a);
        }
        else
        {
            if (modify is { } m) File.SetLastWriteTime(target, m);
            if (access is { } a) File.SetLastAccessTime(target, a);
        }
    }

    private static void SetOnLinkItself(string path, DateTime? accessUtc, DateTime? modifyUtc)
    {
        // A null time must keep the link's CURRENT value, so read it from the link itself.
        FileSystemInfo link = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        var atime = accessUtc ?? link.LastAccessTimeUtc;
        var mtime = modifyUtc ?? link.LastWriteTimeUtc;

        if (OperatingSystem.IsWindows()) SetLinkTimesWindows(path, atime, mtime);
        else SetLinkTimesUnix(path, atime, mtime);
    }

    // ───────────── Windows ─────────────

    private const uint FileWriteAttributes = 0x100;
    private const uint ShareAll = 0x7;                 // read | write | delete
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;   // required to open a directory
    private const uint OpenReparsePoint = 0x00200000;  // the link itself, not its target

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileTime(SafeFileHandle file, long[]? creation, long[]? lastAccess, long[]? lastWrite);

    private static void SetLinkTimesWindows(string path, DateTime atimeUtc, DateTime mtimeUtc)
    {
        using var handle = CreateFileW(path, FileWriteAttributes, ShareAll, IntPtr.Zero, OpenExisting,
            BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
        if (!SetFileTime(handle, null, new[] { atimeUtc.ToFileTimeUtc() }, new[] { mtimeUtc.ToFileTimeUtc() }))
            throw new IOException(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
    }

    // ───────────── Linux / macOS ─────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    [DllImport("libc", EntryPoint = "utimensat", SetLastError = true)]
    private static extern int UtimensAt(int dirFd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, Timespec[] times, int flags);

    private static void SetLinkTimesUnix(string path, DateTime atimeUtc, DateTime mtimeUtc)
    {
        // AT_FDCWD / AT_SYMLINK_NOFOLLOW differ between Linux and macOS.
        int atFdCwd = OperatingSystem.IsMacOS() ? -2 : -100;
        int noFollow = OperatingSystem.IsMacOS() ? 0x20 : 0x100;

        static Timespec Spec(DateTime utc)
        {
            var ticks = (utc - DateTime.UnixEpoch).Ticks;
            long seconds = Math.DivRem(ticks, TimeSpan.TicksPerSecond, out long rem);
            if (rem < 0) { seconds--; rem += TimeSpan.TicksPerSecond; }
            return new Timespec { Seconds = seconds, Nanoseconds = rem * 100 };
        }

        if (UtimensAt(atFdCwd, path, new[] { Spec(atimeUtc), Spec(mtimeUtc) }, noFollow) != 0)
            throw new IOException(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
    }
}
