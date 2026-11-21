using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// An in-process background job inherits the same shell state a job in its own process does: every
/// <see cref="BashShellState.InheritedGlobalNames"/> entry and the shell's arrays, as COPIES (the job shares the process,
/// not the parent's objects). The bug class: <see cref="BashStateSnapshot.JobGlobals"/> was a hand-picked subset (the
/// <c>set</c> options and the ERR trap), so a job saw no arrays, no <c>BASH_REMATCH</c> and no positional parameters; a
/// new shell-state global is now inherited by deriving from the inventory, never by adding it here.
/// ps-bash-specific plumbing; the bash behaviour is pinned in
/// <c>ShellLanguageGapDifferentialTests.BackgroundJob_InheritsArraysPositionalsAndRematch</c>.
/// </summary>
public class BashStateSnapshotJobInheritanceTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    public BashStateSnapshotJobInheritanceTests(SharedPwshFixture fixture) { _fixture = fixture; }

    private const string Globals = "[PsBash.Cmdlets.BashStateSnapshot]::JobGlobals($ExecutionContext.SessionState)";

    [Fact]
    public void JobGlobals_CarryEveryInheritedShellStateGlobal()
    {
        var names = BashShellState.InheritedGlobalNames.ToList();
        Assert.NotEmpty(names);
        // Each set to a marker the job must receive; a name missing from the job's globals prints MISSING.
        var set = string.Join("; ", names.Select(n => $"$global:{n} = 'mark-{n}'"));
        var r = CmdResult.Run(_fixture.AcquireFresh(),
            set + $"; $__g = {Globals}; foreach ($__n in @({string.Join(", ", names.Select(n => "'" + n + "'"))})) " +
            "{ if ($__g.ContainsKey($__n)) { \"$__n=$($__g[$__n])\" } else { \"$__n=MISSING\" } }").AssertSuccess();
        // The set options are then written on or off and the errexit suppression depth reset (JobOptions tests): those are
        // present with their own values; every other name carries the parent's value.
        var rewritten = new HashSet<string>(BashStateSnapshot.JobOptionGlobals) { "__BashErrexitSuppress" };
        var lines = r.Lines.Where(l => l.Contains('=')).ToList();
        Assert.Equal(names.Count, lines.Count);
        for (int i = 0; i < names.Count; i++)
        {
            if (rewritten.Contains(names[i])) Assert.DoesNotContain("=MISSING", lines[i]);
            else Assert.Equal($"{names[i]}=mark-{names[i]}", lines[i]);
        }
    }

    [Fact]
    public void JobGlobals_CopyArraysAndPositionals_NotShareThem()
    {
        var r = CmdResult.Run(_fixture.AcquireFresh(),
            "$global:BashPositional = @('p1','p2'); $global:BASH_REMATCH = @('bc','c'); $arr = @('x','y'); " +
            "$m = [PsBash.Cmdlets.BashAssoc]::FromPairs('k', 'v'); $__g = " + Globals + "; " +
            "\"pos=$($__g['BashPositional'] -join ',') rem=$($__g['BASH_REMATCH'][1]) arr=$($__g['arr'] -join ',') m=$($__g['m']['k'])\"; " +
            "\"m-type=$($__g['m'].GetType().Name)\"; " +
            "\"shared=$([object]::ReferenceEquals($__g['BashPositional'], $global:BashPositional) -or [object]::ReferenceEquals($__g['arr'], $arr) -or [object]::ReferenceEquals($__g['m'], $m))\"")
            .AssertSuccess();
        Assert.Equal(new[] { "pos=p1,p2 rem=c arr=x,y m=v", "m-type=BashAssoc", "shared=False" }, r.Lines);
    }

    /// <summary>No shell list (top level, no <c>set --</c>): the job's <c>$1</c>.. are the script's arguments, the caller's <c>$args</c>.</summary>
    [Fact]
    public void JobGlobals_WithoutAShellList_TakeTheScriptArguments()
    {
        var r = CmdResult.Run(_fixture.AcquireFresh(),
            "$global:BashPositional = $null; & { $__g = " + Globals + "; \"pos=$($__g['BashPositional'] -join ',')\" } A B").AssertSuccess();
        Assert.Equal(new[] { "pos=A,B" }, r.Lines);
    }
}
