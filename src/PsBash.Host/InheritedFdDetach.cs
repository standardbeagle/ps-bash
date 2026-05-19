using System.Runtime.InteropServices;

namespace PsBash.Host;

/// <summary>
/// Detaches inherited stdio fds when the host is spawned as a daemon by the
/// launcher (PSBASH_HOST_DETACH=1). The launcher's stdout/stderr file
/// descriptors are inherited by this process; the launcher then exits but the
/// daemon keeps the inherited write ends open, so any caller of the launcher
/// that read its output via a pipe never sees EOF — the read hangs forever.
///
/// fd 0/1/2 are replaced with /dev/null. fds 3+ that resolve to pipes are
/// closed; .NET runtime fds (EventPipe, debugger, diagnostic port, GC sockets)
/// must NOT be touched.
///
/// Pipe detection (RC-5):
/// - Linux: readlink /proc/self/fd/{fd}, match the "pipe:[N]" target form.
/// - macOS / BSD: no /proc — fstat the fd and test S_ISFIFO on st_mode.
///   The old fallback brute-force-closed every open fd in [3,256), which on
///   macOS could close .NET runtime fds that lack FD_CLOEXEC and crash the
///   host with SIGABRT. fstat gives the same precision /proc gives on Linux.
/// </summary>
internal static class InheritedFdDetach
{
    [DllImport("libc", SetLastError = true)]
    private static extern int open(string pathname, int flags);
    [DllImport("libc", SetLastError = true)]
    private static extern int dup2(int oldfd, int newfd);
    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
    [DllImport("libc", SetLastError = true, EntryPoint = "readlink")]
    private static extern long readlink(string path, byte[] buf, ulong bufsiz);

    private const int O_RDWR = 2;

    // POSIX st_mode bit masks (stable across Linux and Darwin).
    private const uint S_IFMT = 0xF000;
    private const uint S_IFIFO = 0x1000;

    // ---- Darwin (macOS) struct stat -----------------------------------
    //
    // 64-bit Darwin uses the unified stat64 ABI. Layout for arm64 + x86_64
    // (sys/stat.h, __DARWIN_STRUCT_STAT64). Only st_mode is consumed here,
    // but the full struct must be marshalled so the buffer the kernel writes
    // into is large enough and field offsets line up. timespec is two
    // 64-bit words (tv_sec + tv_nsec) on 64-bit Darwin.
    [StructLayout(LayoutKind.Sequential)]
    private struct DarwinStat
    {
        public int st_dev;          // dev_t (int32)
        public ushort st_mode;      // mode_t (uint16)
        public ushort st_nlink;     // nlink_t (uint16)
        public ulong st_ino;        // ino_t (uint64)
        public uint st_uid;         // uid_t
        public uint st_gid;         // gid_t
        public int st_rdev;         // dev_t (int32)
        public long st_atime_sec;
        public long st_atime_nsec;
        public long st_mtime_sec;
        public long st_mtime_nsec;
        public long st_ctime_sec;
        public long st_ctime_nsec;
        public long st_birthtime_sec;
        public long st_birthtime_nsec;
        public long st_size;        // off_t
        public long st_blocks;      // blkcnt_t
        public int st_blksize;      // blksize_t
        public uint st_flags;
        public uint st_gen;
        public int st_lspare;
        public long st_qspare0;
        public long st_qspare1;
    }

    // macOS x86_64 exports the modern stat under the $INODE64 symbol variant
    // (the legacy `fstat` is the 32-bit-ino stat with a different struct
    // layout). On arm64 there is no $INODE64 alias — the only `fstat` symbol
    // is the 64-bit-ino one. Bind both and dispatch at runtime by arch.
    [DllImport("libc", SetLastError = true, EntryPoint = "fstat$INODE64")]
    private static extern int fstat_darwin_x64(int fd, out DarwinStat buf);

    [DllImport("libc", SetLastError = true, EntryPoint = "fstat")]
    private static extern int fstat_darwin_arm64(int fd, out DarwinStat buf);

    private static int fstat_darwin(int fd, out DarwinStat buf)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            return fstat_darwin_arm64(fd, out buf);
        return fstat_darwin_x64(fd, out buf);
    }

    // ---- Linux struct stat --------------------------------------------
    //
    // glibc x86_64 / arm64 struct stat layout. Only st_mode is consumed.
    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStat
    {
        public ulong st_dev;
        public ulong st_ino;
        public ulong st_nlink;
        public uint st_mode;        // mode_t (uint32 on Linux)
        public uint st_uid;
        public uint st_gid;
        public int __pad0;
        public ulong st_rdev;
        public long st_size;
        public long st_blksize;
        public long st_blocks;
        public long st_atime_sec;
        public long st_atime_nsec;
        public long st_mtime_sec;
        public long st_mtime_nsec;
        public long st_ctime_sec;
        public long st_ctime_nsec;
        public long __unused0;
        public long __unused1;
        public long __unused2;
    }

    // glibc historically routes stat through __fxstat with a struct-version
    // arg. Modern glibc (2.33+) exports fstat directly; net10 on the CI
    // runners targets that. Bind to the plain symbol.
    [DllImport("libc", SetLastError = true, EntryPoint = "fstat")]
    private static extern int fstat_linux(int fd, out LinuxStat buf);

    /// <summary>
    /// fstat-based pipe/FIFO detection. Works on every POSIX platform, so the
    /// macOS code path's core logic is exercisable on Linux too. Returns false
    /// for closed/invalid fds (fstat -1/EBADF) — never throws.
    /// </summary>
    internal static bool IsFdPipeViaFstat(int fd)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                if (fstat_darwin(fd, out var ds) != 0) return false;
                return ((uint)ds.st_mode & S_IFMT) == S_IFIFO;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (fstat_linux(fd, out var ls) != 0) return false;
                return (ls.st_mode & S_IFMT) == S_IFIFO;
            }

            return false;
        }
        catch
        {
            // Missing symbol / marshalling fault on an unforeseen platform —
            // treat as "not a pipe" so we never close an fd we can't classify.
            return false;
        }
    }

    private static string? ReadFdTarget(int fd)
    {
        var buf = new byte[256];
        var n = readlink($"/proc/self/fd/{fd}", buf, (ulong)buf.Length);
        if (n <= 0) return null;
        return System.Text.Encoding.UTF8.GetString(buf, 0, (int)n);
    }

    /// <summary>
    /// Platform-dispatched pipe detection. Linux keeps the precise
    /// /proc/self/fd readlink "pipe:" filter (byte-identical behaviour to the
    /// pre-RC-5 code). macOS uses fstat + S_ISFIFO instead of the old
    /// brute-force [3,256) close loop.
    /// </summary>
    internal static bool IsFdPipe(int fd)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var target = ReadFdTarget(fd);
            return target is not null
                && target.StartsWith("pipe:", StringComparison.Ordinal);
        }

        // macOS / BSD: no /proc. fstat the fd directly.
        return IsFdPipeViaFstat(fd);
    }

    /// <summary>
    /// Replaces fd 0/1/2 with /dev/null and closes any inherited pipe fds at
    /// 3+. Best-effort: every failure path is swallowed because a daemon that
    /// can't perfectly detach is still better than one that crashes trying.
    /// </summary>
    internal static void DetachInheritedStdioIfRequested()
    {
        if (OperatingSystem.IsWindows()) return;
        if (Environment.GetEnvironmentVariable("PSBASH_HOST_DETACH") != "1") return;
        try
        {
            var nullFd = open("/dev/null", O_RDWR);
            if (nullFd < 0) return;
            dup2(nullFd, 0);
            dup2(nullFd, 1);
            dup2(nullFd, 2);
            if (nullFd > 2) close(nullFd);

            // Close inherited pipe fds at 3+. .NET's Process.Start + Console
            // subsystem in the launcher leaks duplicates of the launcher's
            // stdout/stderr pipes into fds 3+ of this host process (verified
            // via /proc/<host>/fd while a test hung). Without closing them,
            // the daemon keeps the test runner's pipe write ends open after
            // the launcher exits and the test's ReadToEndAsync never sees EOF.
            //
            // Only close fds that IsFdPipe confirms are pipes so we never
            // touch .NET runtime fds (EventPipe, debugger, diagnostic port,
            // GC sockets) — on macOS those may lack FD_CLOEXEC and closing
            // them crashes the runtime with SIGABRT (RC-5).
            try
            {
                if (System.IO.Directory.Exists("/proc/self/fd"))
                {
                    foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries("/proc/self/fd"))
                    {
                        var name = System.IO.Path.GetFileName(entry);
                        if (!int.TryParse(name, out var fd)) continue;
                        if (fd < 3) continue;
                        if (IsFdPipe(fd))
                            close(fd);
                    }
                }
                else
                {
                    // macOS / FreeBSD have no /proc. Walk the [3,256) fd range
                    // but gate every close on fstat-confirmed S_ISFIFO so .NET
                    // runtime fds in that range are left untouched.
                    for (int fd = 3; fd < 256; fd++)
                    {
                        if (IsFdPipeViaFstat(fd))
                            close(fd);
                    }
                }
            }
            catch { /* best effort */ }
        }
        catch { /* best effort — daemon stdio detach */ }
    }
}
