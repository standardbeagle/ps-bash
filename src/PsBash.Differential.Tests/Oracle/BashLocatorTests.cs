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
        var first = BashLocator.Find();
        var second = BashLocator.Find();
        // Must be the same object (cached)
        Assert.Same(first, second);
    }

    [Fact]
    public void BashLocator_Find_ReturnsValidHost()
    {
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
        Assert.Equal(3, psi.ArgumentList.Count);
        Assert.Equal("-c", psi.ArgumentList[0]);
        Assert.Equal(BashLocator.CloseInheritedDescriptors, psi.ArgumentList[1]);
        Assert.Equal("echo hello", psi.ArgumentList[2]);
    }

    [Fact]
    public void BashLocator_BuildPsi_WslHost_SetsCorrectArgs()
    {
        var host = new BashHost(BashHostKind.Wsl, "wsl.exe", "5.0", "C.UTF-8");
        var psi = BashLocator.BuildPsi(host, "echo hello");

        Assert.NotNull(psi);
        Assert.Equal("wsl.exe", psi!.FileName);
        Assert.Equal(5, psi.ArgumentList.Count);
        Assert.Equal("-e", psi.ArgumentList[0]);
        Assert.Equal("bash", psi.ArgumentList[1]);
        Assert.Equal("-c", psi.ArgumentList[2]);
        Assert.Equal(BashLocator.CloseInheritedDescriptors, psi.ArgumentList[3]);
        Assert.Equal("echo hello", psi.ArgumentList[4]);
    }

    // The oracle's descriptors above 2 are closed before the script runs, whatever bash inherited: wsl.exe on the s24u
    // gate host handed bash an open fd 7, and `{ true >&7; } 2>/dev/null` recorded "open" where ps-bash (whose table
    // starts empty) says closed. Here the oracle's psi runs under a bash that opened 5 and 7 first; the script must see
    // neither, and still be a plain `bash -c` ($0, the `bash: line N:` prefix).
    [SkippableFact]
    public async Task BuildPsi_InheritedDescriptorAbove2_IsClosedBeforeTheScriptRuns()
    {
        var host = BashLocator.Find();
        Skip.If(!host.IsAvailable, "oracle: no bash available");

        const string probe = "for n in 3 4 5 6 7 8 9; do { : >&$n; } 2>/dev/null && echo \"open $n\"; done; echo \"0=$0\"; nosuch";
        var oracle = BashLocator.BuildPsi(host, probe)!;

        // The same command line, started by a bash that opened 5 and 7: `bash -c 'exec 5>... 7>...; exec "$@"' bash <argv>`.
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = oracle.FileName,
            RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8,
            RedirectStandardError = true, StandardErrorEncoding = System.Text.Encoding.UTF8,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        var argv = oracle.ArgumentList.ToList();
        if (host.Kind == BashHostKind.Wsl) { psi.ArgumentList.Add(argv[0]); argv.RemoveAt(0); }   // wsl.exe -e
        else argv.Insert(0, oracle.FileName);
        psi.ArgumentList.Add(host.Kind == BashHostKind.Wsl ? "bash" : "-c");
        if (host.Kind == BashHostKind.Wsl) psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("exec 5>/dev/null 7>/dev/null; exec \"$@\"");
        psi.ArgumentList.Add("bash");
        foreach (var a in argv) psi.ArgumentList.Add(a);

        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.StandardInput.Close();
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        Assert.Equal("0=bash\n", (await stdout).Replace("\r\n", "\n"));
        Assert.StartsWith("bash: line 1: nosuch", await stderr);
    }

    // ── Integration: Find() and actually run bash if available ───────────────

    [SkippableFact]
    public async Task BashLocator_Find_CanRunEchoHello()
    {
        var host = BashLocator.Find();
        Skip.If(!host.IsAvailable, "oracle: no bash available");

        var psi = BashLocator.BuildPsi(host, "echo hello");
        Assert.NotNull(psi);

        psi!.RedirectStandardOutput = true; psi!.StandardOutputEncoding ??= System.Text.Encoding.UTF8;
        psi.RedirectStandardError = true; psi.StandardErrorEncoding ??= System.Text.Encoding.UTF8;
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
