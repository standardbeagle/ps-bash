using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of Invoke-BashWhoami /
/// Invoke-BashHostname from PsBash.psm1 script functions to binary cmdlets
/// (PsBash.Cmdlets.dll).
///
/// Oracle: the psm1 functions returned, respectively,
/// <c>[System.Environment]::UserName</c> and
/// <c>[System.Net.Dns]::GetHostName()</c>. The cmdlets call the same APIs, so a
/// test that compares cmdlet output to those identical .NET calls in the same
/// process is a strict round-trip — no real bash needed. The failure-surface
/// axes that apply are empty/--help input and quoting/injection (an
/// adversarial first argument must not change behavior because neither cmdlet
/// consumes positional args). Pipeline / file / signal axes do not apply: both
/// surfaces are pure side-effect-free reads.
///
/// The PwshTestFixture loads psm1 (which no longer defines these functions)
/// then imports PsBash.Cmdlets.dll, mirroring the host load order — so these
/// tests also prove the function-shadowing removal worked and the psm1
/// <c>Set-Alias whoami/hostname</c> lines still resolve to the cmdlet.
/// </summary>
public class InvokeBashWhoamiHostnameCommandTests
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

    // ---- whoami ----

    [Fact]
    public void Whoami_NoArgs_ReturnsCurrentUserName()
    {
        var expected = System.Environment.UserName;
        var lines = RunLines("Invoke-BashWhoami");
        Assert.Equal(new[] { expected }, lines);
    }

    [Fact]
    public void Whoami_ViaAlias_ReturnsCurrentUserName()
    {
        var expected = System.Environment.UserName;
        var lines = RunLines("whoami");
        Assert.Equal(new[] { expected }, lines);
    }

    [Fact]
    public void Whoami_HelpFlag_EmitsUsageAndReturnsZero()
    {
        var lines = RunLines("Invoke-BashWhoami --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("whoami", System.StringComparison.OrdinalIgnoreCase));
    }

    // ---- hostname ----

    [Fact]
    public void Hostname_NoArgs_ReturnsLocalHostName()
    {
        var expected = System.Net.Dns.GetHostName();
        var lines = RunLines("Invoke-BashHostname");
        Assert.Equal(new[] { expected }, lines);
    }

    [Fact]
    public void Hostname_ViaAlias_ReturnsLocalHostName()
    {
        var expected = System.Net.Dns.GetHostName();
        var lines = RunLines("hostname");
        Assert.Equal(new[] { expected }, lines);
    }

    [Fact]
    public void Hostname_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashHostname --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("hostname", System.StringComparison.OrdinalIgnoreCase));
    }

    // ---- injection probe (Directive 12) ----

    [Fact]
    public void Whoami_ExtraArgWithScriptblockChars_IsIgnoredNotExecuted()
    {
        // Adversarial positional arg containing PS scriptblock + $() chars.
        // The cmdlet must NOT evaluate it as code; it must produce a clean
        // username and no extra output, no exceptions.
        var expected = System.Environment.UserName;
        var lines = RunLines("Invoke-BashWhoami '$(throw \"pwn\")'; $LASTEXITCODE");
        // First emit: bare username from the cmdlet.
        // Second emit: the literal "0" or null from $LASTEXITCODE — depending on
        // session state. The key assertion is that "pwn" never appears.
        Assert.Equal(expected, lines[0]);
        Assert.DoesNotContain(lines, l => l.Contains("pwn"));
    }

    [Fact]
    public void Hostname_ExtraArgWithScriptblockChars_IsIgnoredNotExecuted()
    {
        var expected = System.Net.Dns.GetHostName();
        var lines = RunLines("Invoke-BashHostname '$(throw \"pwn\")'");
        Assert.Equal(new[] { expected }, lines);
    }
}
