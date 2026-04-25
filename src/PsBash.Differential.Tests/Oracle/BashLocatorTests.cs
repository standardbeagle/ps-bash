using Xunit;

namespace PsBash.Differential.Tests.Oracle;

/// <summary>
/// Tests for BashLocator and the BashHost record.
/// </summary>
[Trait("Category", "BashLocator")]
public class BashLocatorTests
{
    // ── BashHost record ───────────────────────────────────────────────────────

    [Fact]
    public void BashHost_None_IsNotAvailable()
    {
        Assert.False(BashHost.None.IsAvailable);
        Assert.Equal(BashHostKind.None, BashHost.None.Kind);
        Assert.Null(BashHost.None.Path);
    }

    [Fact]
    public void BashHost_Native_IsAvailable()
    {
        var host = new BashHost(BashHostKind.Native, "/bin/bash", "5.1.0", "C");
        Assert.True(host.IsAvailable);
        Assert.Equal(BashHostKind.Native, host.Kind);
        Assert.Equal("/bin/bash", host.Path);
        Assert.Equal("5.1.0", host.Version);
        Assert.Equal("C", host.Locale);
    }

    [Fact]
    public void BashHost_Wsl_IsAvailable()
    {
        var host = new BashHost(BashHostKind.Wsl, "wsl.exe", "5.1.8", "C.UTF-8");
        Assert.True(host.IsAvailable);
        Assert.Equal(BashHostKind.Wsl, host.Kind);
    }

    // ── BashLocator.Find() ────────────────────────────────────────────────────

    [Fact]
    public void BashLocator_Find_ReturnsCachedResultOnSecondCall()
    {
        BashLocator.ResetCache();
        var first = BashLocator.Find();
        var second = BashLocator.Find();
        // Must be the same object (cached)
        Assert.Same(first, second);
    }

    [Fact]
    public void BashLocator_Find_ReturnsValidHost()
    {
        BashLocator.ResetCache();
        var host = BashLocator.Find();

        // Kind must be one of the enum values
        Assert.True(
            host.Kind == BashHostKind.None ||
            host.Kind == BashHostKind.Native ||
            host.Kind == BashHostKind.Wsl);

        // If available, must have a non-null path and non-empty version
        if (host.IsAvailable)
        {
            Assert.NotNull(host.Path);
            Assert.NotEmpty(host.Version);
        }
    }

    [SkippableFact]
    public void BashLocator_Find_OnWindowsWithBashOnPath_ReturnsNativeOrWsl()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows only");
        BashLocator.ResetCache();
        var host = BashLocator.Find();

        // On this Windows dev machine, git-bash or WSL should provide bash.
        // We don't require it — but if found, kind must be Native or Wsl.
        if (host.IsAvailable)
        {
            Assert.True(
                host.Kind == BashHostKind.Native || host.Kind == BashHostKind.Wsl,
                $"Expected Native or Wsl, got {host.Kind}");
        }
    }

    [SkippableFact]
    public void BashLocator_Find_OnLinuxOrMac_ReturnsNative()
    {
        Skip.If(OperatingSystem.IsWindows(), "Non-Windows only");
        BashLocator.ResetCache();
        var host = BashLocator.Find();

        // bash should always be available on Linux/Mac CI
        Assert.True(host.IsAvailable, "Expected bash to be available on Linux/Mac");
        Assert.Equal(BashHostKind.Native, host.Kind);
        Assert.NotNull(host.Path);
        Assert.NotEmpty(host.Version);
        // Version must start with a digit
        Assert.True(char.IsDigit(host.Version[0]),
            $"Version should start with digit: '{host.Version}'");
    }

    // ── BashLocator.BuildPsi ─────────────────────────────────────────────────

    [Fact]
    public void BashLocator_BuildPsi_NoneHost_ReturnsNull()
    {
        var psi = BashLocator.BuildPsi(BashHost.None, "echo hi");
        Assert.Null(psi);
    }

    [Fact]
    public void BashLocator_BuildPsi_NativeHost_SetsCorrectArgs()
    {
        var host = new BashHost(BashHostKind.Native, "/bin/bash", "5.0", "C");
        var psi = BashLocator.BuildPsi(host, "echo hello");

        Assert.NotNull(psi);
        Assert.Equal("/bin/bash", psi!.FileName);
        Assert.Equal(2, psi.ArgumentList.Count);
        Assert.Equal("-c", psi.ArgumentList[0]);
        Assert.Equal("echo hello", psi.ArgumentList[1]);
    }

    [Fact]
    public void BashLocator_BuildPsi_WslHost_SetsCorrectArgs()
    {
        var host = new BashHost(BashHostKind.Wsl, "wsl.exe", "5.0", "C.UTF-8");
        var psi = BashLocator.BuildPsi(host, "echo hello");

        Assert.NotNull(psi);
        Assert.Equal("wsl.exe", psi!.FileName);
        Assert.Equal(4, psi.ArgumentList.Count);
        Assert.Equal("-e", psi.ArgumentList[0]);
        Assert.Equal("bash", psi.ArgumentList[1]);
        Assert.Equal("-c", psi.ArgumentList[2]);
        Assert.Equal("echo hello", psi.ArgumentList[3]);
    }

    // ── Integration: Find() and actually run bash if available ───────────────

    [SkippableFact]
    public async Task BashLocator_Find_CanRunEchoHello()
    {
        BashLocator.ResetCache();
        var host = BashLocator.Find();
        Skip.If(!host.IsAvailable, "oracle: no bash available");

        var psi = BashLocator.BuildPsi(host, "echo hello");
        Assert.NotNull(psi);

        psi!.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.UseShellExecute = false;

        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.StandardInput.Close();
        var output = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();

        Assert.Equal(0, proc.ExitCode);
        Assert.Contains("hello", output);
    }
}
