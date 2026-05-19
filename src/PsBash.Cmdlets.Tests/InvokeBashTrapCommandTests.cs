using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of Invoke-BashTrap
/// from PsBash.psm1 to a binary cmdlet (PsBash.Cmdlets.dll).
///
/// Oracle: the psm1 function stored handler actions in
/// $script:BashTrapHandlers (module-scoped Dictionary), and ERR / EXIT
/// signals also published a scriptblock to $global:__BashTrapERR /
/// $global:__BashTrapEXIT so the eval pipeline fires them on non-zero
/// exit / shutdown.
///
/// Failure-surface axes that apply: signal during execute (Directive 3 axis
/// 7 — by definition the feature), quoting/injection (Directive 12 — action
/// containing scriptblock chars), and alias resolution.
/// </summary>
public class InvokeBashTrapCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashTrapCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
    }

    private System.Collections.ObjectModel.Collection<System.Management.Automation.PSObject> RunRaw(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$global:BashTrapHandlers.Clear(); $global:__BashTrapERR = $null; $global:__BashTrapEXIT = $null").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result;
    }

    private string[] RunLines(string script)
    {
        return RunRaw(script).Select(o => o?.ToString() ?? "").ToArray();
    }

    [Fact]
    public void Trap_NoArgs_EmptyTable_EmitsNothing()
    {
        var raw = RunRaw("Invoke-BashTrap");
        Assert.Empty(raw);
    }

    [Fact]
    public void Trap_RegisterErr_ListsIt()
    {
        var raw = RunRaw(
            "Invoke-BashTrap 'echo err' ERR; " +
            "Invoke-BashTrap");
        Assert.Single(raw);
        Assert.Equal("ERR", raw[0].Properties["Signal"]?.Value?.ToString());
        Assert.Equal("echo err", raw[0].Properties["Action"]?.Value?.ToString());
    }

    [Fact]
    public void Trap_RegisterErr_PublishesGlobalScriptblock()
    {
        var raw = RunRaw(
            "Invoke-BashTrap 'echo handler' ERR; " +
            "$global:__BashTrapERR -is [scriptblock]");
        Assert.Single(raw);
        Assert.True((bool)raw[0].BaseObject);
    }

    [Fact]
    public void Trap_RegisterExit_PublishesGlobalScriptblock()
    {
        var raw = RunRaw(
            "Invoke-BashTrap 'cleanup' EXIT; " +
            "$global:__BashTrapEXIT -is [scriptblock]");
        Assert.Single(raw);
        Assert.True((bool)raw[0].BaseObject);
    }

    [Fact]
    public void Trap_RegisterMultipleSignals_ListsAll()
    {
        var raw = RunRaw(
            "Invoke-BashTrap 'echo go' INT TERM HUP; " +
            "Invoke-BashTrap");
        Assert.Equal(3, raw.Count);
        var signals = raw.Select(o => o.Properties["Signal"]?.Value?.ToString()).OrderBy(s => s).ToArray();
        Assert.Equal(new[] { "HUP", "INT", "TERM" }, signals);
    }

    [Fact]
    public void Trap_PFlag_ListsHandlers()
    {
        var raw = RunRaw(
            "Invoke-BashTrap 'echo a' ERR; " +
            "Invoke-BashTrap -p");
        Assert.Single(raw);
        Assert.Equal("ERR", raw[0].Properties["Signal"]?.Value?.ToString());
    }

    [Fact]
    public void Trap_LFlag_ListsSignalNames()
    {
        var raw = RunRaw("Invoke-BashTrap -l");
        Assert.Single(raw);
        var bashText = raw[0].Properties["BashText"]?.Value?.ToString() ?? "";
        Assert.Contains("EXIT", bashText);
        Assert.Contains("ERR", bashText);
        Assert.Contains("INT", bashText);
        Assert.Contains("TERM", bashText);
    }

    [Fact]
    public void Trap_ResetSignal_ClearsHandler()
    {
        var raw = RunRaw(
            "Invoke-BashTrap 'echo err' ERR; " +
            "Invoke-BashTrap - ERR; " +
            "Invoke-BashTrap");
        Assert.Empty(raw);
    }

    [Fact]
    public void Trap_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashTrap --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("trap", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Trap_AliasIsRegistered()
    {
        // The bare `trap` token is a PowerShell language keyword (trap{}), so
        // a Set-Alias to it can be registered but cannot be invoked as a bare
        // command in a script — the parser claims it first. The contract that
        // matters: the alias entry exists so any path that does
        // Get-Command -CommandType Alias trap resolves to Invoke-BashTrap.
        // (Bash code routed through ps-bash transpiles `trap` to
        // `Invoke-BashTrap` directly, bypassing the parser keyword path.)
        var pwsh = _fixture.AcquireFresh();
        var r = pwsh.AddScript("(Get-Alias trap -ErrorAction SilentlyContinue).Definition").Invoke();
        pwsh.Commands.Clear();
        Assert.Single(r);
        Assert.Equal("Invoke-BashTrap", r[0]?.BaseObject?.ToString());
    }

    [Fact]
    public void Trap_PFlag_IsExactNameMatch_NotCommonParameterPrefix()
    {
        // Regression guard: bare -p must route to print mode rather than
        // being eaten by -PipelineVariable / -ProgressAction.
        var raw = RunRaw("Invoke-BashTrap 'echo' ERR; Invoke-BashTrap -p");
        Assert.Single(raw);
    }

    // ---- injection probe (Directive 12) ----

    [Fact]
    public void Trap_ActionContainingScriptblockChars_StoredLiteralNotExecuted()
    {
        // The action text is bound positionally through $args into the
        // InvokeScript body — it is stored in the dictionary as a literal
        // string. The eval pipeline would convert it to a scriptblock and
        // run it ONLY when the signal fires (which is the documented bash
        // semantic — the user asked for it). What this test asserts is
        // that registering the trap does NOT itself evaluate the payload:
        // the pwsh.Invoke() call must return normally, and listing the
        // registered handler must surface the literal text byte-for-byte.
        var raw = RunRaw(
            "Invoke-BashTrap '$(throw \"PWNED\")' ERR; " +
            "Invoke-BashTrap");
        Assert.Single(raw);
        var action = raw[0].Properties["Action"]?.Value?.ToString() ?? "";
        Assert.Contains("$(throw", action);
        Assert.Contains("PWNED", action);
    }
}
