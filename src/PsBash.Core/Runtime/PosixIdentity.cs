using System.Runtime.InteropServices;

namespace PsBash.Core.Runtime;

/// <summary>
/// POSIX identity primitives used by the runtime-directory hardening (R03):
/// the current uid, the owner uid of a path, and the owner uid of a live
/// process. All are no-ops / "no opinion" on Windows, where the ACL is the
/// security boundary.
/// </summary>
internal static class PosixIdentity
{
    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint getuid();

    /// <summary>Current real uid. Only meaningful on POSIX.</summary>
    public static uint CurrentUid() => getuid();

    /// <summary>
    /// Owner uid of a live process. Linux reads the authoritative
    /// <c>/proc/{pid}/status</c> <c>Uid:</c> line (the real uid) — readable for
    /// any process, so a foreign-owned PID is detectable even when its sidecar
    /// falsely claims our username. Returns false (no opinion) on platforms
    /// without <c>/proc</c>.
    /// </summary>
    public static bool TryGetProcessUid(int pid, out uint uid)
    {
        uid = 0;
        if (!OperatingSystem.IsLinux() || pid <= 0) return false;
        try
        {
            var status = BoundedTextFile.Read(
                $"/proc/{pid}/status",
                64 * 1024,
                "Process status exceeds the maximum supported size.");
            foreach (var line in status.Split('\n'))
            {
                if (!line.StartsWith("Uid:", StringComparison.Ordinal)) continue;
                var fields = line[4..].Split(
                    (char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                return fields.Length > 0 &&
                       uint.TryParse(fields[0], out uid);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }

    /// <summary>
    /// lstat a path, returning the owning uid, its low 9 permission bits as a
    /// <see cref="UnixFileMode"/>, and whether it is a directory. Uses lstat so
    /// an attacker-planted symlink is judged by the symlink itself, never by a
    /// target it points at. Throws <see cref="InsecureRuntimeDirectoryException"/>
    /// when the path cannot be inspected.
    /// </summary>
    public static (uint Uid, UnixFileMode Mode, bool IsDir) StatPath(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // macOS x86_64 exports the 64-bit-ino struct under $INODE64; arm64
            // exposes the same layout as plain lstat.
            var rc = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? lstat_darwin_arm64(path, out var darwin)
                : lstat_darwin_x64(path, out darwin);
            if (rc != 0) throw CannotStat(path);
            return (darwin.st_uid, FromRaw(darwin.st_mode), IsDirMode(darwin.st_mode));
        }

        if (lstat_linux(path, out var linux) != 0) throw CannotStat(path);
        return (linux.st_uid, FromRaw(linux.st_mode), IsDirMode(linux.st_mode));
    }

    private static InsecureRuntimeDirectoryException CannotStat(string path)
        => new($"Cannot inspect ps-bash runtime path '{path}': {Marshal.GetLastPInvokeErrorMessage()}.");

    private static bool IsDirMode(uint rawStMode) => (rawStMode & S_IFMT) == S_IFDIR;

    private static UnixFileMode FromRaw(uint rawStMode) => (UnixFileMode)(rawStMode & 0x1FF);

    private const uint S_IFMT = 0xF000;
    private const uint S_IFDIR = 0x4000;

    // glibc x86_64 / arm64 struct stat; only st_mode / st_uid are consumed.
    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStat
    {
        public ulong st_dev;
        public ulong st_ino;
        public ulong st_nlink;
        public uint st_mode;
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

    // 64-bit Darwin (arm64 + x86_64) stat64 ABI.
    [StructLayout(LayoutKind.Sequential)]
    private struct DarwinStat
    {
        public int st_dev;
        public ushort st_mode;
        public ushort st_nlink;
        public ulong st_ino;
        public uint st_uid;
        public uint st_gid;
        public int st_rdev;
        public long st_atime_sec;
        public long st_atime_nsec;
        public long st_mtime_sec;
        public long st_mtime_nsec;
        public long st_ctime_sec;
        public long st_ctime_nsec;
        public long st_birthtime_sec;
        public long st_birthtime_nsec;
        public long st_size;
        public long st_blocks;
        public int st_blksize;
        public uint st_flags;
        public uint st_gen;
        public int st_lspare;
        public long st_qspare0;
        public long st_qspare1;
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "lstat")]
    private static extern int lstat_linux(string path, out LinuxStat buf);

    [DllImport("libc", SetLastError = true, EntryPoint = "lstat$INODE64")]
    private static extern int lstat_darwin_x64(string path, out DarwinStat buf);

    [DllImport("libc", SetLastError = true, EntryPoint = "lstat")]
    private static extern int lstat_darwin_arm64(string path, out DarwinStat buf);
}
