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
public class PsBashRuntimeDirectoryTests : IDisposable
{
    private static readonly UnixFileMode PrivateMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public void Dispose() => Reset();

    private static void Reset()
    {
        PsBashRuntimeDirectory.IsPosixOverride = null;
        PsBashRuntimeDirectory.XdgRuntimeDirOverride = null;
        PsBashRuntimeDirectory.TempPathOverride = null;
        PsBashRuntimeDirectory.CurrentUidOverride = null;
        PsBashRuntimeDirectory.StatProbeOverride = null;
    }

    [Fact]
    public void GetPath_XdgSet_UsesPsBashSubdirUnderXdg()
    {
        PsBashRuntimeDirectory.IsPosixOverride = () => true;
        PsBashRuntimeDirectory.XdgRuntimeDirOverride = () => "/run/user/1000";
        PsBashRuntimeDirectory.CurrentUidOverride = () => 1000;

        Assert.Equal(
            Path.Combine("/run/user/1000", "ps-bash"),
            PsBashRuntimeDirectory.GetPath());
    }

    [Fact]
    public void GetPath_NoXdg_UsesPerUidTempDir()
    {
        PsBashRuntimeDirectory.IsPosixOverride = () => true;
        PsBashRuntimeDirectory.XdgRuntimeDirOverride = () => null;
        PsBashRuntimeDirectory.TempPathOverride = () => "/tmp";
        PsBashRuntimeDirectory.CurrentUidOverride = () => 1000;

        Assert.Equal(
            Path.Combine("/tmp", "ps-bash-1000"),
            PsBashRuntimeDirectory.GetPath());
    }

    [Fact]
    public void GetPath_BlankXdg_FallsBackToPerUidTempDir()
    {
        PsBashRuntimeDirectory.IsPosixOverride = () => true;
        PsBashRuntimeDirectory.XdgRuntimeDirOverride = () => "   ";
        PsBashRuntimeDirectory.TempPathOverride = () => "/tmp";
        PsBashRuntimeDirectory.CurrentUidOverride = () => 7;

        Assert.Equal(Path.Combine("/tmp", "ps-bash-7"), PsBashRuntimeDirectory.GetPath());
    }

    [Fact]
    public void GetPath_NonRootedXdg_FallsBackToPerUidTempDir()
    {
        PsBashRuntimeDirectory.IsPosixOverride = () => true;
        PsBashRuntimeDirectory.XdgRuntimeDirOverride = () => "relative/runtime";
        PsBashRuntimeDirectory.TempPathOverride = () => "/tmp";
        PsBashRuntimeDirectory.CurrentUidOverride = () => 7;

        Assert.Equal(Path.Combine("/tmp", "ps-bash-7"), PsBashRuntimeDirectory.GetPath());
    }

    [Fact]
    public void GetPath_Windows_UsesTempPsBashNestedDir()
    {
        PsBashRuntimeDirectory.IsPosixOverride = () => false;
        PsBashRuntimeDirectory.TempPathOverride = () => @"C:\Users\x\AppData\Local\Temp";

        Assert.Equal(
            Path.Combine(@"C:\Users\x\AppData\Local\Temp", "ps-bash"),
            PsBashRuntimeDirectory.GetPath());
    }

    [Fact]
    public void ValidateDirectory_ForeignOwner_Refuses()
    {
        PsBashRuntimeDirectory.CurrentUidOverride = () => 1000u;
        PsBashRuntimeDirectory.StatProbeOverride = _ => (1001u, PrivateMode, true);

        var ex = Assert.Throws<InsecureRuntimeDirectoryException>(
            () => PsBashRuntimeDirectory.ValidateDirectory("/tmp/ps-bash-1000"));
        Assert.Contains("1001", ex.Message);
        Assert.Contains("1000", ex.Message);
    }

    [Theory]
    [InlineData(UnixFileMode.GroupWrite)]
    [InlineData(UnixFileMode.OtherWrite)]
    [InlineData(UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)]
    public void ValidateDirectory_GroupOrWorldWritable_Refuses(UnixFileMode unsafeBit)
    {
        PsBashRuntimeDirectory.CurrentUidOverride = () => 1000u;
        PsBashRuntimeDirectory.StatProbeOverride = _ => (1000u, PrivateMode | unsafeBit, true);

        var ex = Assert.Throws<InsecureRuntimeDirectoryException>(
            () => PsBashRuntimeDirectory.ValidateDirectory("/tmp/ps-bash-1000"));
        Assert.Contains("writable", ex.Message);
    }

    [Fact]
    public void ValidateDirectory_OwnerOnly_Passes()
    {
        PsBashRuntimeDirectory.CurrentUidOverride = () => 1000u;
        PsBashRuntimeDirectory.StatProbeOverride = _ => (1000u, PrivateMode, true);

        PsBashRuntimeDirectory.ValidateDirectory("/tmp/ps-bash-1000");
    }

    [Fact]
    public void ValidateDirectory_NotADirectory_Refuses()
    {
        PsBashRuntimeDirectory.CurrentUidOverride = () => 1000u;
        PsBashRuntimeDirectory.StatProbeOverride = _ => (1000u, PrivateMode, false);

        var ex = Assert.Throws<InsecureRuntimeDirectoryException>(
            () => PsBashRuntimeDirectory.ValidateDirectory("/tmp/ps-bash-1000"));
        Assert.Contains("not a directory", ex.Message);
    }

    [SkippableFact]
    [Trait("Platform", "Posix")]
    public void EnsureDirectory_CreatesOwnerOnly0700()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "POSIX-only: File.GetUnixFileMode is a no-op on Windows");

        var root = Path.Combine(Path.GetTempPath(), "psb-rtdir-" + Guid.NewGuid().ToString("N"));
        PsBashRuntimeDirectory.TempPathOverride = () => root;
        PsBashRuntimeDirectory.XdgRuntimeDirOverride = () => null;
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
            Reset();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    [Trait("Platform", "Posix")]
    public void EnsureDirectory_ExistingWorldWritableDir_Refuses()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "POSIX-only: File.SetUnixFileMode is a no-op on Windows");

        var root = Path.Combine(Path.GetTempPath(), "psb-rtdir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        PsBashRuntimeDirectory.TempPathOverride = () => root;
        PsBashRuntimeDirectory.XdgRuntimeDirOverride = () => null;
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
            Reset();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
