using System.Runtime.InteropServices;
using PsBash.Core.Platform;

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
        // PosixStat owns the per-OS/per-arch struct stat layout (Platform/).
        if (!PosixStat.TryLStat(path, out var st))
            throw new InsecureRuntimeDirectoryException(
                $"Cannot inspect ps-bash runtime path '{path}': {Marshal.GetLastPInvokeErrorMessage()}.");
        return (st.Uid, st.Permissions & (UnixFileMode)0x1FF, st.IsDirectory);
    }
}
