using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of Invoke-BashUname
/// from PsBash.psm1 to a binary cmdlet (PsBash.Cmdlets.dll).
///
/// Oracle: the psm1 <c>Invoke-BashUname</c> function. Its output is computed
/// entirely from .NET APIs (<c>System.Environment.OSVersion</c>,
/// <c>System.Environment.MachineName</c>, <c>System.Environment.Is64BitProcess</c>)
/// — the same APIs the cmdlet calls in the same process. So comparing cmdlet
/// output to in-process recomputation of those identical .NET calls is a strict
/// round-trip and does not require real bash. The failure-surface axes that
/// apply: empty input (no flags = default <c>-s</c>), missing target (unknown
/// flag silently ignored), and quoting/injection (Directive 12). Pipeline /
/// file / signal / large / CRLF / unicode axes do not apply — uname has no
/// pipeline input and no file operands.
///
/// The PwshTestFixture loads psm1 (which no longer defines this function) then
/// imports PsBash.Cmdlets.dll, mirroring the host load order — so these tests
/// also prove the function-shadowing removal worked and the psm1
/// <c>Set-Alias uname</c> line still resolves to the cmdlet.
/// </summary>
public class InvokeBashUnameCommandTests
{
    private static string[] RunLines(string script)
    {
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$error.Clear()").Invoke();
        pwsh.Commands.Clear();

        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();

        var err = pwsh.AddScript("$error | Select-Object -First 1").Invoke();
        pwsh.Commands.Clear();
        Assert.True(err.Count == 0 || err[0] == null,
            $"Unexpected error running [{script}]: {(err.Count > 0 ? err[0]?.ToString() : "none")}");

        return result.Select(o => o?.ToString() ?? "").ToArray();
    }

    // Reproduce the oracle's derivation in test space.
    private static string ExpectedSysName()
    {
        var v = System.Environment.OSVersion.Version;
        return $"MINGW64_NT-{v.Major}.{v.Minor}.{v.Build}";
    }
    private static string ExpectedHostName() => System.Environment.MachineName.ToLowerInvariant();
    private static string ExpectedRelease()
    {
        var v = System.Environment.OSVersion.Version;
        return $"{v.Major}.{v.Minor}.{v.Build}";
    }
    private static string ExpectedArch() =>
        System.Environment.Is64BitProcess ? "x86_64" : "i686";

    [Fact]
    public void Uname_NoFlags_ReturnsKernelName()
    {
        // Default == -s.
        var lines = RunLines("Invoke-BashUname");
        Assert.Equal(new[] { ExpectedSysName() }, lines);
    }

    [Fact]
    public void Uname_FlagS_ReturnsKernelName()
    {
        var lines = RunLines("Invoke-BashUname -s");
        Assert.Equal(new[] { ExpectedSysName() }, lines);
    }

    [Fact]
    public void Uname_FlagN_ReturnsLowerHostName()
    {
        var lines = RunLines("Invoke-BashUname -n");
        Assert.Equal(new[] { ExpectedHostName() }, lines);
    }

    [Fact]
    public void Uname_FlagR_ReturnsRelease()
    {
        var lines = RunLines("Invoke-BashUname -r");
        Assert.Equal(new[] { ExpectedRelease() }, lines);
    }

    [Fact]
    public void Uname_FlagM_ReturnsMachineArch()
    {
        var lines = RunLines("Invoke-BashUname -m");
        Assert.Equal(new[] { ExpectedArch() }, lines);
    }

    [Fact]
    public void Uname_FlagA_ReturnsAllJoined()
    {
        var expected = $"{ExpectedSysName()} {ExpectedHostName()} {ExpectedRelease()} {ExpectedArch()} MINGW64";
        var lines = RunLines("Invoke-BashUname -a");
        Assert.Equal(new[] { expected }, lines);
    }

    [Fact]
    public void Uname_BundledFlags_SnrEmitsThreeFieldsInOrder()
    {
        // Oracle order is s, n, r, m regardless of bundle order.
        var expected = $"{ExpectedSysName()} {ExpectedHostName()} {ExpectedRelease()}";
        var lines = RunLines("Invoke-BashUname -snr");
        Assert.Equal(new[] { expected }, lines);
    }

    [Fact]
    public void Uname_BundledFlags_AllShort_SameAsFlagA_ButWithoutMingw64Suffix()
    {
        // -snrm = s + n + r + m (no trailing "MINGW64" — that's -a only).
        var expected = $"{ExpectedSysName()} {ExpectedHostName()} {ExpectedRelease()} {ExpectedArch()}";
        var lines = RunLines("Invoke-BashUname -snrm");
        Assert.Equal(new[] { expected }, lines);
    }

    [Fact]
    public void Uname_ViaAlias_ReturnsKernelName()
    {
        var lines = RunLines("uname");
        Assert.Equal(new[] { ExpectedSysName() }, lines);
    }

    [Fact]
    public void Uname_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashUname --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("uname", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Uname_UnknownFlag_IsIgnoredAndDefaultsToKernelName()
    {
        // Oracle silently drops unknown flags and falls through to default -s.
        var lines = RunLines("Invoke-BashUname -x");
        Assert.Equal(new[] { ExpectedSysName() }, lines);
    }

    // ---- injection probe (Directive 12) ----

    [Fact]
    public void Uname_ExtraArgWithScriptblockChars_IsIgnoredNotExecuted()
    {
        // Adversarial positional arg containing PS scriptblock + $() chars.
        // The cmdlet must not evaluate it as code — token is not a recognized
        // flag bundle, so the loop drops it and the default -s output is emitted.
        var lines = RunLines("Invoke-BashUname '$(throw \"pwn\")'");
        Assert.Equal(new[] { ExpectedSysName() }, lines);
        Assert.DoesNotContain(lines, l => l.Contains("pwn"));
    }

    [Fact]
    public void Uname_InjectionInBundledLookalike_IsIgnored()
    {
        // A dash-prefixed token that contains forbidden chars must not be
        // treated as a flag bundle (the IsShortFlagBundle predicate rejects it)
        // and must not be evaluated. Output stays at the -s default.
        var lines = RunLines("Invoke-BashUname '-s;rm'");
        Assert.Equal(new[] { ExpectedSysName() }, lines);
    }
}
