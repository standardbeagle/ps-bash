namespace PsBash.Core.Runtime;

/// <summary>
/// Files only the user may read: the interactive history (<c>~/.psbash/history.db</c>, the legacy
/// <c>.psbash_history</c>), the directory frecency database and the host log. A command line holds whatever was typed
/// on it, tokens included, so on a shared POSIX machine these are what bash's <c>~/.bash_history</c> is: mode 0600,
/// in a directory nobody else can list. ps-bash created <c>~/.psbash</c> 0775 and its files 0644 (the umask's
/// default), readable by every local user. On Windows the profile directory's ACL is the boundary; nothing changes there.
/// </summary>
public static class PrivateUserFiles
{
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// Creates <paramref name="path"/> when absent and narrows it to 0700 (POSIX). An existing directory is narrowed
    /// too: it was created 0775 by earlier versions. A directory owned by someone else is left as it is (chmod fails).
    /// </summary>
    public static void EnsureDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path, PsBashRuntimeDirectory.PrivateDirectoryMode);
        Narrow(path, PsBashRuntimeDirectory.PrivateDirectoryMode);
    }

    /// <summary>
    /// Makes <paramref name="path"/> a 0600 file (POSIX), creating it empty when absent, so the content written next
    /// is never readable by others even for a moment. Used before SQLite opens a database (its <c>-wal</c> / <c>-shm</c>
    /// take the database file's mode) and before a log or history file is written.
    /// </summary>
    public static void EnsureFile(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        if (!File.Exists(path))
        {
            try
            {
                using var _ = new FileStream(path, new FileStreamOptions
                {
                    Mode = System.IO.FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = FileMode,
                });
            }
            catch (IOException) { /* created by another process first: narrowed below */ }
        }
        Narrow(path, FileMode);
    }

    /// <summary>
    /// <see cref="EnsureFile"/> for a SQLite database, and narrows the <c>-wal</c> / <c>-shm</c> / <c>-journal</c>
    /// files an earlier version left at 0644 (SQLite creates new ones with the database file's mode).
    /// </summary>
    public static void EnsureDatabase(string dbPath)
    {
        if (OperatingSystem.IsWindows()) return;
        EnsureFile(dbPath);
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            if (File.Exists(dbPath + suffix)) Narrow(dbPath + suffix, FileMode);
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void Narrow(string path, UnixFileMode mode)
    {
        try
        {
            if ((File.GetUnixFileMode(path) & ~mode) != 0)
                File.SetUnixFileMode(path, mode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Not ours (another owner) or gone: nothing to narrow.
        }
    }
}
