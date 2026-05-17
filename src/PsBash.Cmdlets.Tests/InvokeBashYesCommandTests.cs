using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of Invoke-BashYes
/// from PsBash.psm1 to a binary cmdlet (PsBash.Cmdlets.dll).
///
/// Oracle: GNU coreutils <c>yes</c>. Emits its argument string forever, or
/// the literal <c>"y"</c> with no argument. Termination is on broken pipe /
/// consumer shutdown — surfaced in PowerShell via
/// <see cref="System.Management.Automation.PSCmdlet.Stopping"/> flipping
/// <c>true</c>.
///
/// All tests bound the producer via <c>Select-Object -First N</c>, which
/// triggers PowerShell's <c>StopUpstreamCommandsException</c> and sets
/// <c>Stopping=true</c> on the cmdlet, exiting the emit loop. We deliberately
/// do NOT test <c>yes | head -N</c> in this suite: the project's
/// <c>InvokeBashHeadCommand</c> buffers all pipeline input in
/// <c>ProcessRecord</c> and processes in <c>EndProcessing</c>, so it doesn't
/// signal stop to an infinite upstream. That is a pre-existing head behavior
/// that long predates this migration (the psm1 oracle's <c>yes | head</c>
/// hangs the same way), and outside the scope of this commit.
/// </summary>
public class InvokeBashYesCommandTests
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

    [Fact]
    public void Yes_NoArgs_EmitsYRepeatedly()
    {
        var lines = RunLines("Invoke-BashYes | Select-Object -First 5");
        Assert.Equal(new[] { "y", "y", "y", "y", "y" }, lines);
    }

    [Fact]
    public void Yes_SingleArg_EmitsArgRepeatedly()
    {
        var lines = RunLines("Invoke-BashYes hello | Select-Object -First 3");
        Assert.Equal(new[] { "hello", "hello", "hello" }, lines);
    }

    [Fact]
    public void Yes_MultipleArgs_JoinsWithSpace()
    {
        var lines = RunLines("Invoke-BashYes hello world foo | Select-Object -First 2");
        Assert.Equal(new[] { "hello world foo", "hello world foo" }, lines);
    }

    [Fact]
    public void Yes_ViaAlias_EmitsYRepeatedly()
    {
        var lines = RunLines("yes | Select-Object -First 4");
        Assert.Equal(new[] { "y", "y", "y", "y" }, lines);
    }

    [Fact]
    public void Yes_HelpFlag_EmitsUsageAndDoesNotLoop()
    {
        var lines = RunLines("Invoke-BashYes --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("yes", System.StringComparison.OrdinalIgnoreCase));
        // The critical invariant: --help must not enter the infinite emit loop.
        // Bounded output (a finite usage block) is the proof.
        Assert.True(lines.Length < 100, $"--help emitted {lines.Length} lines; expected a small usage block");
    }

    // ---- injection probe (Directive 12) ----

    [Fact]
    public void Yes_ArgWithScriptblockChars_TreatedAsLiteralText()
    {
        // Adversarial arg containing PS scriptblock + $() chars. Must be
        // emitted as a literal string, never evaluated.
        var lines = RunLines("Invoke-BashYes '$(throw \"pwn\")' | Select-Object -First 2");
        Assert.Equal(new[] { "$(throw \"pwn\")", "$(throw \"pwn\")" }, lines);
    }
}
