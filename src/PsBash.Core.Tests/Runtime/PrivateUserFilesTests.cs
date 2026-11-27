using PsBash.Core.Runtime;
using Xunit;

namespace PsBash.Core.Tests.Runtime;

/// <summary>
/// <see cref="PrivateUserFiles"/>: ~/.psbash and the history, frecency and host log in it are the user's alone on POSIX,
/// as bash's ~/.bash_history is 0600 (bash 5.2). Earlier versions created them 0775 / 0644, so existing ones are
/// narrowed too. Hand-written: file modes, below any shell surface.
/// </summary>
[Trait("Platform", "Unix")]
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class PrivateUserFilesTests : IDisposable
{
    private const UnixFileMode Rw = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode Rwx = Rw | UnixFileMode.UserExecute;
    private const UnixFileMode Loose = Rwx | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private readonly string _root = Directory.CreateTempSubdirectory("psbash-private-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [SkippableFact]
    public void EnsureDirectory_CreatesItPrivate_AndNarrowsAnOldOne()
    {
        Skip.If(OperatingSystem.IsWindows(), "POSIX file modes");
        string fresh = Path.Combine(_root, "fresh");
        PrivateUserFiles.EnsureDirectory(fresh);
        Assert.Equal(Rwx, File.GetUnixFileMode(fresh));

        string old = Path.Combine(_root, "old");
        Directory.CreateDirectory(old);
        File.SetUnixFileMode(old, Loose);
        PrivateUserFiles.EnsureDirectory(old);
        Assert.Equal(Rwx, File.GetUnixFileMode(old));
    }

    [SkippableFact]
    public void EnsureFile_CreatesItPrivate_AndNarrowsAnOldOne_KeepingItsContent()
    {
        Skip.If(OperatingSystem.IsWindows(), "POSIX file modes");
        string fresh = Path.Combine(_root, "fresh.log");
        PrivateUserFiles.EnsureFile(fresh);
        Assert.Equal(Rw, File.GetUnixFileMode(fresh));
        Assert.Equal(0, new FileInfo(fresh).Length);

        string old = Path.Combine(_root, "old.log");
        File.WriteAllText(old, "kept\n");
        File.SetUnixFileMode(old, Rw | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        PrivateUserFiles.EnsureFile(old);
        Assert.Equal(Rw, File.GetUnixFileMode(old));
        Assert.Equal("kept\n", File.ReadAllText(old));
    }

    [SkippableFact]
    public void EnsureDatabase_NarrowsAnOldWalAndShm_WithoutCreatingThem()
    {
        Skip.If(OperatingSystem.IsWindows(), "POSIX file modes");
        string db = Path.Combine(_root, "history.db");
        File.WriteAllText(db + "-wal", "");
        File.SetUnixFileMode(db + "-wal", Rw | UnixFileMode.OtherRead);
        PrivateUserFiles.EnsureDatabase(db);
        Assert.Equal(Rw, File.GetUnixFileMode(db));
        Assert.Equal(Rw, File.GetUnixFileMode(db + "-wal"));
        Assert.False(File.Exists(db + "-shm"));
    }
}
