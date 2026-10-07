using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <see cref="BashShellState"/> round trip, driven the way the emitted subshell scope drives it
/// (<c>PsBuild.ShellStateScope</c>): save, let the body change state, restore. Hand-written, not
/// oracle: this is the runtime half of a transpiler construct; end to end in
/// <c>SubshellScopeDifferentialTests</c>.
/// </summary>
public class BashShellStateTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public BashShellStateTests(SharedPwshFixture fixture) => _fixture = fixture;

    private CmdResult Run(string script) => CmdResult.Run(_fixture.AcquireFresh(), script);

    [Fact]
    public void SaveRestore_UndoesVariablesFlagsPositionalsAndShopt()
    {
        var r = Run(
            "$env:PSB_SS_KEEP = 'outer'; Remove-Item Env:PSB_SS_NEW -ErrorAction SilentlyContinue; "
            + "$global:__BashErrexit = $false; $global:BashPositional = @('a', 'b'); "
            + "$s = [PsBash.Cmdlets.BashShellState]::Save($ExecutionContext.SessionState); "
            // the body: change everything a subshell might
            + "$env:PSB_SS_KEEP = 'inner'; $env:PSB_SS_NEW = 'added'; $global:__BashErrexit = $true; "
            + "$global:BashPositional[0] = 'mutated'; Invoke-BashShopt -s nullglob; "
            + "$xt = [PsBash.Cmdlets.BashShellState]::Restore($ExecutionContext.SessionState, $s); "
            + "\"keep=$env:PSB_SS_KEEP new=[$env:PSB_SS_NEW] errexit=$global:__BashErrexit "
            + "pos=$($global:BashPositional -join ',') xt=[$xt]\"; "
            + "$sh = (Invoke-BashShopt nullglob | ForEach-Object { [string]$_ }) -join ''; \"shopt=[$sh]\"; "
            + "Remove-Item Env:PSB_SS_KEEP").AssertSuccess();

        Assert.Contains("keep=outer new=[] errexit=False pos=a,b xt=[]", r.Stdout);
        Assert.Matches(@"shopt=\[nullglob\s+off\]", r.Stdout);   // the body's `shopt -s` is undone
    }

    [Fact]
    public void Restore_ReportsTheXtraceLevelToReapply()
    {
        var r = Run(
            "$global:__BashXtrace = $false; $s = [PsBash.Cmdlets.BashShellState]::Save($ExecutionContext.SessionState); "
            + "$global:__BashXtrace = $true; "
            + "$xt = [PsBash.Cmdlets.BashShellState]::Restore($ExecutionContext.SessionState, $s); "
            + "\"xt=$xt flag=$global:__BashXtrace\"").AssertSuccess();

        Assert.Contains("xt=False flag=False", r.Stdout);   // the caller runs Set-PSDebug -Off
    }

    [Fact]
    public void Restore_RemovesAGlobalTheBodyCreated()
    {
        var r = Run(
            "Remove-Variable -Scope Global -Name BASH_REMATCH -ErrorAction SilentlyContinue; "
            + "$s = [PsBash.Cmdlets.BashShellState]::Save($ExecutionContext.SessionState); "
            + "$global:BASH_REMATCH = @('m'); "
            + "$null = [PsBash.Cmdlets.BashShellState]::Restore($ExecutionContext.SessionState, $s); "
            + "\"exists=$(Test-Path variable:global:BASH_REMATCH)\"").AssertSuccess();

        Assert.Contains("exists=False", r.Stdout);
    }
}
