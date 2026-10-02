namespace PsBash.Core.Runtime;

/// <summary>
/// The per-user ps-bash runtime directory: module extraction, IPC sockets,
/// sidecars and spawn locks all live here. On Windows it is
/// <c>%TEMP%\ps-bash</c> — <c>%TEMP%</c> is already per-user and its ACL is the
/// security boundary. On POSIX <c>%TEMP%</c> (usually <c>/tmp</c>) is
/// world-writable, so a fixed <c>ps-bash</c> name is attacker-controlled: the
/// directory is instead <c>$XDG_RUNTIME_DIR/ps-bash</c> when set, else
/// <c>$TMPDIR/ps-bash-{uid}</c>, created 0700. Every use re-verifies that the
/// directory is owned by the current uid with no group/world write bit — trust
/// is not based on the path alone.
/// </summary>
public static class PsBashRuntimeDirectory
{
    internal const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode UnsafeWriteBits =
        UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    // Test seam: the long-sun_path budget tests need a synthetic temp root
    // without touching process-wide TMPDIR. While set, XDG_RUNTIME_DIR is not
    // consulted either: otherwise a runner that exports it (GitHub's ubuntu
    // images do) resolves the REAL per-user directory, and tests that chmod or
    // fill the "synthetic" root corrupt it for every later test and process.
    // Only affects the returned string; never selects the POSIX native path.
    internal static Func<string>? TempPathOverride { get; set; }

    /// <summary>
    /// Pure path resolution. POSIX: <c>{xdg}/ps-bash</c> when
    /// <paramref name="xdgRuntimeDir"/> is a non-blank absolute path, else
    /// <c>{temp}/ps-bash-{uid}</c>. Windows: <c>{temp}/ps-bash</c>.
    /// </summary>
    internal static string ResolvePath(string? xdgRuntimeDir, string tempPath, uint uid, bool isPosix)
    {
        if (!isPosix) return Path.Combine(tempPath, "ps-bash");
        if (!string.IsNullOrWhiteSpace(xdgRuntimeDir) && Path.IsPathRooted(xdgRuntimeDir))
            return Path.Combine(xdgRuntimeDir, "ps-bash");
        return Path.Combine(tempPath, $"ps-bash-{uid}");
    }

    /// <summary>Resolve the runtime directory path without touching the filesystem.</summary>
    public static string GetPath()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(TempPath(), "ps-bash");

        // The test seam also suppresses $XDG_RUNTIME_DIR: otherwise a Linux box with XDG set
        // ignores the synthetic root, and tests that chmod "their" directory hit the REAL one.
        return ResolvePath(
            TempPathOverride is null ? Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") : null,
            TempPath(),
            PosixIdentity.CurrentUid(),
            isPosix: true);
    }

    /// <summary>
    /// Resolve, create (0700 on POSIX) and validate the runtime directory.
    /// Throws <see cref="InsecureRuntimeDirectoryException"/> when a POSIX
    /// directory is not a directory, is owned by another uid, or is group- or
    /// world-writable.
    /// </summary>
    public static string EnsureDirectory()
    {
        var path = GetPath();
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return path;
        }

        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
            // Narrow before anything is written inside; the sticky bit on the
            // shared parent (e.g. /tmp) prevents another user from swapping the
            // freshly created directory out from under us.
            try { File.SetUnixFileMode(path, PrivateDirectoryMode); }
            catch (PlatformNotSupportedException) { /* old runtime — validated below regardless */ }
        }

        var (uid, mode, isDir) = PosixIdentity.StatPath(path);
        ValidateOwnership(path, uid, mode, isDir, PosixIdentity.CurrentUid());
        return path;
    }

    /// <summary>
    /// Pure ownership/permission decision for a runtime directory. Separated
    /// from <see cref="EnsureDirectory"/> so the owner-mismatch branch is
    /// testable without root (chown) and without racing a create.
    /// </summary>
    internal static void ValidateOwnership(
        string path, uint actualUid, UnixFileMode mode, bool isDir, uint expectedUid)
    {
        if (!isDir)
            throw new InsecureRuntimeDirectoryException(
                $"ps-bash runtime path '{path}' is not a directory.");

        if (actualUid != expectedUid)
            throw new InsecureRuntimeDirectoryException(
                $"ps-bash runtime directory '{path}' is owned by uid {actualUid}, " +
                $"not the current uid {expectedUid}; refusing to use it.");

        if ((mode & UnsafeWriteBits) != 0)
            throw new InsecureRuntimeDirectoryException(
                $"ps-bash runtime directory '{path}' mode {mode} is group- or " +
                "world-writable; refusing to use it.");
    }

    private static string TempPath() => TempPathOverride?.Invoke() ?? Path.GetTempPath();
}

/// <summary>
/// The ps-bash runtime directory exists but is not safe to use: it is not a
/// directory, is owned by another user, or is writable by group/world.
/// Derives from <see cref="IOException"/> so best-effort callers that already
/// tolerate transient filesystem failures keep their existing catch blocks.
/// </summary>
public sealed class InsecureRuntimeDirectoryException : IOException
{
    public InsecureRuntimeDirectoryException(string message) : base(message) { }
}
