using System.Runtime.InteropServices;
using PsBash.Core.Runtime;
using Xunit;

namespace PsBash.Core.Tests.Runtime;

/// <summary>
/// POSIX shared /tmp hardening (R03). The runtime directory (module extraction,
/// IPC sockets, sidecars, spawn locks) must be per-user and owner-only:
/// <c>$XDG_RUNTIME_DIR/ps-bash</c> or <c>$TMPDIR/ps-bash-{uid}</c>, created 0700,
/// and every use must verify the owner is the current uid and the mode is not
/// group/world-writable. Windows <c>%TEMP%</c> is already per-user and unaffected.
/// </summary>
[Collection("EnvVar")]
public class PsBashRuntimeDirectoryTests : IDisposable
{
    private static readonly UnixFileMode PrivateMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public void Dispose() => PsBashRuntimeDirectory.TempPathOverride = null;

    [Fact]
    public void ResolvePath_XdgAbsolute_UsesPsBashSubdirUnderXdg()
        => Assert.Equal(
            Path.Combine("/run/user/1000", "ps-bash"),
            PsBashRuntimeDirectory.ResolvePath("/run/user/1000", "/tmp", uid: 1000, isPosix: true));

    [Fact]
    public void ResolvePath_NoXdg_UsesPerUidTempDir()
        => Assert.Equal(
            Path.Combine("/tmp", "ps-bash-1000"),
            PsBashRuntimeDirectory.ResolvePath(null, "/tmp", uid: 1000, isPosix: true));

    [Fact]
    public void ResolvePath_BlankXdg_FallsBackToPerUidTempDir()
        => Assert.Equal(
            Path.Combine("/tmp", "ps-bash-7"),
            PsBashRuntimeDirectory.ResolvePath("   ", "/tmp", uid: 7, isPosix: true));

    [Fact]
    public void ResolvePath_NonRootedXdg_FallsBackToPerUidTempDir()
        => Assert.Equal(
            Path.Combine("/tmp", "ps-bash-7"),
            PsBashRuntimeDirectory.ResolvePath("relative/runtime", "/tmp", uid: 7, isPosix: true));

    [Fact]
    public void ResolvePath_Windows_UsesTempPsBashNestedDir()
        => Assert.Equal(
            Path.Combine(@"C:\Users\x\AppData\Local\Temp", "ps-bash"),
            PsBashRuntimeDirectory.ResolvePath(
                xdgRuntimeDir: "/run/user/1000",
                tempPath: @"C:\Users\x\AppData\Local\Temp",
                uid: 1000,
                isPosix: false));

    [Fact]
    public void ValidateOwnership_ForeignOwner_Refuses()
    {
        var ex = Assert.Throws<InsecureRuntimeDirectoryException>(
            () => PsBashRuntimeDirectory.ValidateOwnership(
                "/tmp/ps-bash-1000", actualUid: 1001, PrivateMode, isDir: true, expectedUid: 1000));

        Assert.Contains("1001", ex.Message);
        Assert.Contains("1000", ex.Message);
    }

    [Theory]
    [InlineData(UnixFileMode.GroupWrite)]
    [InlineData(UnixFileMode.OtherWrite)]
    [InlineData(UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)]
    public void ValidateOwnership_GroupOrWorldWritable_Refuses(UnixFileMode unsafeBit)
    {
        var ex = Assert.Throws<InsecureRuntimeDirectoryException>(
            () => PsBashRuntimeDirectory.ValidateOwnership(
                "/tmp/ps-bash-1000", actualUid: 1000, PrivateMode | unsafeBit, isDir: true, expectedUid: 1000));

        Assert.Contains("writable", ex.Message);
    }

    [Fact]
    public void ValidateOwnership_OwnerOnly_Passes()
        => PsBashRuntimeDirectory.ValidateOwnership(
            "/tmp/ps-bash-1000", actualUid: 1000, PrivateMode, isDir: true, expectedUid: 1000);

    [Fact]
    public void ValidateOwnership_NotADirectory_Refuses()
    {
        var ex = Assert.Throws<InsecureRuntimeDirectoryException>(
            () => PsBashRuntimeDirectory.ValidateOwnership(
                "/tmp/ps-bash-1000", actualUid: 1000, PrivateMode, isDir: false, expectedUid: 1000));

        Assert.Contains("not a directory", ex.Message);
    }

    [SkippableFact]
    [Trait("Platform", "Posix")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void EnsureDirectory_CreatesOwnerOnly0700()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "POSIX-only: File.GetUnixFileMode is a no-op on Windows");

        var root = Path.Combine(Path.GetTempPath(), "psb-rtdir-" + Guid.NewGuid().ToString("N"));
        PsBashRuntimeDirectory.TempPathOverride = () => root;
        try
        {
            var dir = PsBashRuntimeDirectory.EnsureDirectory();

            Assert.True(Directory.Exists(dir));
            Assert.Equal(PrivateMode, File.GetUnixFileMode(dir));
            Assert.StartsWith(root, dir);
            Assert.StartsWith("ps-bash-", Path.GetFileName(dir));
        }
        finally
        {
            PsBashRuntimeDirectory.TempPathOverride = null;
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    [Trait("Platform", "Posix")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void EnsureDirectory_ExistingWorldWritableDir_Refuses()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "POSIX-only: File.SetUnixFileMode is a no-op on Windows");

        var root = Path.Combine(Path.GetTempPath(), "psb-rtdir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        PsBashRuntimeDirectory.TempPathOverride = () => root;
        try
        {
            var dir = PsBashRuntimeDirectory.GetPath();
            Directory.CreateDirectory(dir);
            File.SetUnixFileMode(dir, (UnixFileMode)0x1FF); // 0777

            Assert.Throws<InsecureRuntimeDirectoryException>(
                () => PsBashRuntimeDirectory.EnsureDirectory());
        }
        finally
        {
            PsBashRuntimeDirectory.TempPathOverride = null;
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
