using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of Invoke-BashShopt
/// from PsBash.psm1 to a binary cmdlet (PsBash.Cmdlets.dll).
///
/// Oracle: the psm1 function maintained a $script:BashShoptOptions hashtable
/// of 14 well-known bash shell options with documented defaults; -s set an
/// option, -u unset, -p printed every option as "shopt -s NAME" sorted, and
/// a bare name queried "NAME on|off". Unknown name routed Write-BashError
/// "bash: shopt: NAME: invalid shell option name".
///
/// Failure-surface axes that apply: missing target (unknown option name,
/// Directive 3 axis 14), quoting/injection (Directive 12), alias resolution.
/// Streaming / file-content / signal axes do not apply: shopt is in-process
/// state, no I/O, no pipeline input.
/// </summary>
public class InvokeBashShoptCommandTests
{
    private static System.Collections.ObjectModel.Collection<System.Management.Automation.PSObject> RunRaw(string script)
    {
        // Reset the cmdlet's static option table to oracle defaults so each
        // test sees the same initial state (the cmdlet legitimately persists
        // mutations across calls; tests must not rely on each other).
        InvokeBashShoptCommand.ResetForTests();

        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("Set-BashErrorMode -Mode PowerShell").Invoke();
        pwsh.Commands.Clear();
        pwsh.AddScript("$error.Clear()").Invoke();
        pwsh.Commands.Clear();

        return pwsh.AddScript(script).Invoke();
    }

    private static string[] RunLines(string script)
    {
        return RunRaw(script).Select(o => o?.ToString() ?? "").ToArray();
    }

    [Fact]
    public void Shopt_NoArgs_EmitsNothing()
    {
        // The psm1 oracle's no-arg path (no -p, no operands) falls through
        // the operand loop and emits nothing. Preserved here.
        var lines = RunLines("Invoke-BashShopt");
        Assert.Empty(lines);
    }

    [Fact]
    public void Shopt_PrintFlag_ListsAllOptionsAsShoptCommands()
    {
        // -p with no operands prints every option as "shopt -s NAME", sorted.
        var lines = RunLines("Invoke-BashShopt -p");
        Assert.Equal(14, lines.Length);
        Assert.All(lines, l => Assert.StartsWith("shopt -s ", l));
        // Sorted ordinal — "checkwinsize" comes before "cmdhist" comes before "dotglob".
        var idxChk = Array.IndexOf(lines, "shopt -s checkwinsize");
        var idxCmd = Array.IndexOf(lines, "shopt -s cmdhist");
        var idxDot = Array.IndexOf(lines, "shopt -s dotglob");
        Assert.True(idxChk >= 0 && idxCmd >= 0 && idxDot >= 0,
            $"expected all three options listed; got: {string.Join(", ", lines)}");
        Assert.True(idxChk < idxCmd, "checkwinsize must precede cmdhist");
        Assert.True(idxCmd < idxDot, "cmdhist must precede dotglob");
    }

    [Fact]
    public void Shopt_QueryKnownOption_EmitsNameAndState()
    {
        // Bare option name (no -s / -u / -p) queries current state.
        // Default: nullglob = false.
        var lines = RunLines("Invoke-BashShopt nullglob");
        Assert.Single(lines);
        Assert.Equal("nullglob off", lines[0]);
    }

    [Fact]
    public void Shopt_QueryDefaultOn_EmitsOn()
    {
        // globstar defaults to true (per oracle table).
        var lines = RunLines("Invoke-BashShopt globstar");
        Assert.Single(lines);
        Assert.Equal("globstar on", lines[0]);
    }

    [Fact]
    public void Shopt_SetFlag_TogglesOptionOn()
    {
        // -s nullglob should flip default-false to true.
        var lines = RunLines(
            "Invoke-BashShopt -s nullglob; Invoke-BashShopt nullglob");
        Assert.Single(lines);
        Assert.Equal("nullglob on", lines[0]);
    }

    [Fact]
    public void Shopt_UnsetFlag_TogglesOptionOff()
    {
        // -u globstar should flip default-true to false.
        var lines = RunLines(
            "Invoke-BashShopt -u globstar; Invoke-BashShopt globstar");
        Assert.Single(lines);
        Assert.Equal("globstar off", lines[0]);
    }

    [Fact]
    public void Shopt_UnknownOption_EmitsNoSuccessOutput()
    {
        // Oracle: Write-BashError "bash: shopt: NOTAREALOPT: invalid shell
        // option name" then return with no success-pipeline output. The
        // nested error stream from the psm1 Write-BashError shim does not
        // surface on the in-process test runspace (see EnvCommandTests's
        // discussion of the SDK isolation), so we assert the contract that
        // matters: zero success objects for the bad name.
        var lines = RunLines("Invoke-BashShopt notarealopt");
        Assert.Empty(lines);
    }

    [Fact]
    public void Shopt_ViaAlias_Works()
    {
        // The `shopt` alias (declared in psm1) must resolve to the cmdlet.
        var lines = RunLines("shopt globstar");
        Assert.Single(lines);
        Assert.Equal("globstar on", lines[0]);
    }

    [Fact]
    public void Shopt_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashShopt --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("shopt", StringComparison.OrdinalIgnoreCase));
    }

    // ---- injection probe (Directive 12) ----

    [Fact]
    public void Shopt_OptionNameWithScriptblockChars_TreatedAsLiteralName()
    {
        // An option name with $() / ; must not be re-parsed as PowerShell.
        // The cmdlet uses Dictionary.TryGetValue for the lookup — a literal
        // ordinal-string compare that cannot evaluate the embedded payload.
        // Asserts: zero success objects (unknown option falls into the
        // error branch) AND the pwsh.Invoke() call returns normally rather
        // than throwing a RuntimeException with "pwn" (which is what would
        // happen if the $(throw "pwn") payload had been evaluated as
        // PowerShell syntax). Negative-assertion is the security probe.
        var lines = RunLines("Invoke-BashShopt '$(throw \"pwn\");rm -rf /'");
        Assert.Empty(lines);
    }

    [Fact]
    public void Shopt_PFlag_IsExactNameMatch_NotCommonParameterPrefix()
    {
        // Regression guard for the playbook collision table: bare -p must
        // route to the cmdlet's print mode rather than being eaten by
        // -PipelineVariable / -ProgressAction. If this test starts producing
        // empty output or an "ambiguous parameter name" exception, the
        // declared SwitchParameter P has regressed.
        var lines = RunLines("Invoke-BashShopt -p");
        Assert.Equal(14, lines.Length);
    }
}
