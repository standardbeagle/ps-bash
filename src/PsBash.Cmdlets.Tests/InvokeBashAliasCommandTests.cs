using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of Invoke-BashAlias
/// from PsBash.psm1 to a binary cmdlet (PsBash.Cmdlets.dll).
///
/// Oracle: the psm1 function stored alias definitions in
/// $script:BashUserAliases (a module-scoped Dictionary[string,string]) and
/// created dynamic PowerShell functions via Set-Item Function:\NAME so that
/// transpiled bash code calling the alias name routes to the alias body.
///
/// Failure-surface axes that apply: missing target (unknown alias name,
/// Directive 3 axis 14), quoting/injection (Directive 12 — alias name with
/// scriptblock chars), alias resolution. Streaming / file-content / signal
/// axes do not apply: alias is in-process state, no I/O.
/// </summary>
public class InvokeBashAliasCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashAliasCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
    }

    private System.Collections.ObjectModel.Collection<System.Management.Automation.PSObject> RunRaw(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        // Reset alias state so tests don't leak. The psm1-owned dictionary is
        // the only state; clear it via the unalias -u -a path.
        pwsh.AddScript("Invoke-BashAlias -u -a").Invoke();
        pwsh.Commands.Clear();
        return pwsh.AddScript(script).Invoke();
    }

    private string[] RunLines(string script)
    {
        return RunRaw(script).Select(o => o?.ToString() ?? "").ToArray();
    }

    [Fact]
    public void Alias_NoArgs_EmptyDict_EmitsNothing()
    {
        var lines = RunLines("Invoke-BashAlias");
        Assert.Empty(lines);
    }

    [Fact]
    public void Alias_SetSimpleAlias_ThenList_ReturnsIt()
    {
        var raw = RunRaw("Invoke-BashAlias \"ll=ls -la\"; Invoke-BashAlias");
        Assert.Single(raw);
        var name = raw[0].Properties["Name"]?.Value?.ToString();
        var value = raw[0].Properties["Value"]?.Value?.ToString();
        var bashText = raw[0].Properties["BashText"]?.Value?.ToString();
        Assert.Equal("ll", name);
        Assert.Equal("ls -la", value);
        Assert.Equal("alias ll='ls -la'", bashText);
    }

    [Fact]
    public void Alias_SetWithEqualsContainingSpaces_StoresValueIncludingArgs()
    {
        var raw = RunRaw("Invoke-BashAlias \"g=grep --color=auto -i\"; Invoke-BashAlias g");
        Assert.Single(raw);
        Assert.Equal("grep --color=auto -i", raw[0].Properties["Value"]?.Value?.ToString());
    }

    [Fact]
    public void Alias_QueryUnknownAlias_NoSuccessOutput()
    {
        // Unknown alias: oracle calls Write-BashError "alias: NAME: not found".
        // The success pipeline must be empty.
        var lines = RunLines("Invoke-BashAlias doesnotexist");
        Assert.Empty(lines);
    }

    [Fact]
    public void Alias_PFlag_NoOp_EmptyDictStillEmpty()
    {
        // -p is an oracle no-op (listing happens via the no-operand path
        // regardless). With an empty dict, -p emits nothing.
        var lines = RunLines("Invoke-BashAlias -p");
        Assert.Empty(lines);
    }

    [Fact]
    public void Alias_PFlag_WithEntries_ListsAll()
    {
        var raw = RunRaw("Invoke-BashAlias a=1; Invoke-BashAlias b=2; Invoke-BashAlias -p");
        Assert.Equal(2, raw.Count);
    }

    [Fact]
    public void Alias_UnsetSingle_RemovesFromDict()
    {
        var raw = RunRaw(
            "Invoke-BashAlias x=1; " +
            "Invoke-BashAlias -u x; " +
            "Invoke-BashAlias");
        Assert.Empty(raw);
    }

    [Fact]
    public void Alias_UnsetUnknown_NoSuccessOutput()
    {
        // Oracle: Write-BashError "unalias: NAME: not found". No success output.
        var lines = RunLines("Invoke-BashAlias -u nope");
        Assert.Empty(lines);
    }

    [Fact]
    public void Alias_UnsetAll_ClearsDict()
    {
        var raw = RunRaw(
            "Invoke-BashAlias a=1; Invoke-BashAlias b=2; " +
            "Invoke-BashAlias -u -a; " +
            "Invoke-BashAlias");
        Assert.Empty(raw);
    }

    [Fact]
    public void Alias_OverwriteSameName_LastWins()
    {
        var raw = RunRaw(
            "Invoke-BashAlias x=first; " +
            "Invoke-BashAlias x=second; " +
            "Invoke-BashAlias x");
        Assert.Single(raw);
        Assert.Equal("second", raw[0].Properties["Value"]?.Value?.ToString());
    }

    [Fact]
    public void Alias_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashAlias --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("alias", StringComparison.OrdinalIgnoreCase));
    }

    // ---- injection probe (Directive 12) ----

    [Fact]
    public void Alias_NameContainingScriptblockChars_TreatedAsLiteralKey()
    {
        // The alias name is bound positionally through $args into the
        // InvokeScript body — it never enters the script text. The
        // dictionary key compare is an ordinal string match, which cannot
        // re-parse the embedded $(throw 'pwn'). The query path returns nothing
        // (key not present); the pwsh invocation must return normally (no
        // RuntimeException with "pwn" — which is what an evaluation of the
        // injection payload would produce).
        var lines = RunLines("Invoke-BashAlias '$(throw \"pwn\");evil'");
        Assert.Empty(lines);
    }
}
