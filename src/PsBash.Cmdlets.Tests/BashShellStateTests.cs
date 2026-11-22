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
            + "Remove-Item Env:PSB_SS_KEEP; $global:LASTEXITCODE = 0").AssertSuccess();

        Assert.Contains("keep=outer new=[] errexit=False pos=a,b xt=[]", r.Stdout);
        Assert.Matches(@"shopt=\[nullglob\s+off\]", r.Stdout);   // the body's `shopt -s` is undone
    }

    [Fact]
    public void Restore_EndsReadonlyMadeInsideTheBody()
    {
        // `( readonly v=1 )` must not leave v readonly in the parent: a later `unset v` has to work.
        var r = Run(
            "$env:PSB_RO = 'x'; "
            + "$s = [PsBash.Cmdlets.BashShellState]::Save($ExecutionContext.SessionState); "
            + "[PsBash.Cmdlets.BashShellState]::MarkReadonly('PSB_RO'); "
            + "[void][PsBash.Cmdlets.BashShellState]::Restore($ExecutionContext.SessionState, $s); "
            + "Invoke-BashUnset PSB_RO; \"rc=$global:LASTEXITCODE v=[$env:PSB_RO]\"").AssertSuccess();

        Assert.Contains("rc=0 v=[]", r.Stdout);
    }

    [Fact]
    public void EveryPerShellTable_IsSavedByTheSubshellSnapshot()
    {
        // GUARD: a process-wide name table in BashShellState is no runspace state, so a subshell leaks it unless it is in
        // ShellTables (Save copies, Restore puts back, ClearReadonly empties). REGRESSION: the nameref and attribute
        // tables were missing, so `( declare -n r=y ); declare -p r` printed the subshell's reference.
        const System.Reflection.BindingFlags statics = System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        var listed = (object[])typeof(BashShellState).GetField("ShellTables", statics)!.GetValue(null)!;
        var tables = typeof(BashShellState).GetFields(statics)
            .Where(f => !f.FieldType.IsArray && f.FieldType != typeof(string)
                && typeof(System.Collections.IEnumerable).IsAssignableFrom(f.FieldType))
            .ToList();

        Assert.True(tables.Count >= 3, "found " + tables.Count + " tables: the scan no longer sees them");
        foreach (var field in tables)
            Assert.True(listed.Any(t => ReferenceEquals(t, field.GetValue(null))),
                $"BashShellState.{field.Name} is not in ShellTables: a subshell would leak it to its parent");
    }

    [Fact]
    public void Restore_EndsNamerefsAndAttributesMadeInsideTheBody_AndBringsBackOneItRemoved()
    {
        // `( declare -n r=y; declare -i n; unset -n keep )`: the parent has no r, no -i on n, and keep still refers.
        var r = Run(
            "[PsBash.Cmdlets.BashShellState]::SetNameref('psbt_ss_keep', 'psbt_ss_t'); "
            + "$s = [PsBash.Cmdlets.BashShellState]::Save($ExecutionContext.SessionState); "
            + "[PsBash.Cmdlets.BashShellState]::SetNameref('psbt_ss_r', 'psbt_ss_t'); "
            + "[PsBash.Cmdlets.BashShellState]::MarkAttribute('psbt_ss_n', 'i'); "
            + "Invoke-BashUnset -n psbt_ss_keep; "
            + "$inside = [PsBash.Cmdlets.BashShellState]::ResolveNameref('psbt_ss_keep'); "
            + "[void][PsBash.Cmdlets.BashShellState]::Restore($ExecutionContext.SessionState, $s); "
            + "\"inside=$inside r=$([PsBash.Cmdlets.BashShellState]::ResolveNameref('psbt_ss_r')) "
            + "keep=$([PsBash.Cmdlets.BashShellState]::ResolveNameref('psbt_ss_keep'))\"; "
            + "Invoke-BashDeclare -p psbt_ss_n 2>&1 | ForEach-Object { \"n: $_\" }; "
            + "[void][PsBash.Cmdlets.BashShellState]::ClearNameref('psbt_ss_keep', $null); $global:LASTEXITCODE = 0").AssertSuccess();

        Assert.Contains("inside=psbt_ss_keep r=psbt_ss_r keep=psbt_ss_t", r.Stdout);
        Assert.DoesNotContain("declare -i", r.Stdout);
    }

    [Fact]
    public void UnsetN_DropsTheReference_NotTheVariableItNames()
    {
        // `y=1; declare -n q=y; unset -n q`: y=1 stays, q is no reference any more. REGRESSION: -n was skipped, q stayed.
        var r = Run(
            "$env:psbt_un_y = '1'; [PsBash.Cmdlets.BashShellState]::SetNameref('psbt_un_q', 'psbt_un_y'); "
            + "Invoke-BashUnset -n psbt_un_q; $rc = $global:LASTEXITCODE; "
            + "\"rc=$rc y=$env:psbt_un_y q=$([PsBash.Cmdlets.BashShellState]::ResolveNameref('psbt_un_q'))\"; "
            + "Remove-Item Env:psbt_un_y").AssertSuccess();

        Assert.Contains("rc=0 y=1 q=psbt_un_q", r.Stdout);
    }

    [Fact]
    public void UnsetN_OnAPlainVariable_LeavesIt_AndOnAReadonlyOneFails()
    {
        // bash 5.2: `v=3; unset -n v` leaves v=3; `readonly w=1; unset -n w` is the readonly error, status 1.
        BashShellState.MarkReadonly("psbt_un_w");
        CmdResult r;
        try
        {
            r = Run(
                "$env:psbt_un_v = '3'; Invoke-BashUnset -n psbt_un_v; $rc = $global:LASTEXITCODE; \"rc=$rc v=[$env:psbt_un_v]\"; "
                + "$env:psbt_un_w = '1'; "
                + "Invoke-BashUnset -n psbt_un_w 2>&1 | ForEach-Object { \"err: $_\" }; \"rc2=$global:LASTEXITCODE w=[$env:psbt_un_w]\"; "
                + "Remove-Item Env:psbt_un_v, Env:psbt_un_w; $global:LASTEXITCODE = 0").AssertSuccess();
        }
        finally { BashShellState.UnmarkReadonly("psbt_un_w"); }

        Assert.Contains("rc=0 v=[3]", r.Stdout);
        Assert.Contains("unset: psbt_un_w: cannot unset: readonly variable", r.Stdout);
        Assert.Contains("rc2=1 w=[1]", r.Stdout);
    }

    [Fact]
    public void NamerefWithNoTarget_IsListed_PlusNLeavesADeclaredScalar_UnsetRemovesIt()
    {
        // bash: `declare -n r; declare -p r` prints `declare -n r`; `declare +n r` leaves `declare -- r`; `declare -n q;
        // unset q` removes q. REGRESSION: the run-time table never held a nameref with no target ("not found").
        var r = Run(
            "[PsBash.Cmdlets.BashShellState]::SetNameref('psbt_nt_r', ''); "
            + "[PsBash.Cmdlets.BashShellState]::SetNameref('psbt_nt_q', ''); "
            + "Invoke-BashDeclare -p psbt_nt_r | ForEach-Object { \"a: $_\" }; "
            + "\"resolve=$([PsBash.Cmdlets.BashShellState]::ResolveNameref('psbt_nt_r'))\"; "
            + "$env:psbt_nt_r = [PsBash.Cmdlets.BashShellState]::ClearNameref('psbt_nt_r', $env:psbt_nt_r); "
            + "Invoke-BashDeclare -p psbt_nt_r | ForEach-Object { \"b: $_\" }; "
            + "Invoke-BashUnset psbt_nt_q; Invoke-BashDeclare -p psbt_nt_q 2>&1 | ForEach-Object { \"c: $_\" }; "
            + "Invoke-BashUnset psbt_nt_r; $global:LASTEXITCODE = 0").AssertSuccess();

        Assert.Contains("a: declare -n psbt_nt_r", r.Stdout);
        Assert.DoesNotContain("a: declare -n psbt_nt_r=", r.Stdout);
        Assert.Contains("resolve=psbt_nt_r", r.Stdout);
        Assert.Contains("b: declare -- psbt_nt_r", r.Stdout);
        Assert.DoesNotContain("b: declare -- psbt_nt_r=", r.Stdout);
        Assert.Contains("c: ", r.Stdout);
        Assert.Contains("psbt_nt_q: not found", r.Stdout);
    }

    [Fact]
    public void ClearReadonly_LetsTheNextInvocationUnset()
    {
        // A daemon runs many scripts; `readonly v` in one must not block `unset v` in the next.
        var r = Run(
            "$env:PSB_RO2 = 'x'; [PsBash.Cmdlets.BashShellState]::MarkReadonly('PSB_RO2'); "
            + "[PsBash.Cmdlets.BashShellState]::ClearReadonly(); Invoke-BashUnset PSB_RO2; \"rc=$global:LASTEXITCODE v=[$env:PSB_RO2]\"").AssertSuccess();

        Assert.Contains("rc=0 v=[]", r.Stdout);
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

    // ── Run-time nameref resolution and a function's own readonly / nameref (in-process, unique names) ──
    // In this class because BashShellState is process-wide and ClearReadonly above wipes it: one class runs serially.

    [Fact]
    public void ResolveNameref_FollowsAChain_StopsAtACycle_AndKeepsASubscript()
    {
        // `declare -n a=b; declare -n b=c` makes `(( a++ ))` write c; `a[1]` is `c[1]`; a cycle ends where it closes.
        BashShellState.SetNameref("psbt_rn_a", "psbt_rn_b");
        BashShellState.SetNameref("psbt_rn_b", "psbt_rn_c");
        BashShellState.SetNameref("psbt_rn_x", "psbt_rn_y");
        BashShellState.SetNameref("psbt_rn_y", "psbt_rn_x");
        BashShellState.SetNameref("psbt_rn_e", "psbt_rn_arr[2]");
        try
        {
            Assert.Equal("psbt_rn_c", BashShellState.ResolveNameref("psbt_rn_a"));
            Assert.Equal("psbt_rn_c[1]", BashShellState.ResolveNameref("psbt_rn_a[1]"));
            Assert.Equal("psbt_rn_plain", BashShellState.ResolveNameref("psbt_rn_plain"));
            Assert.Contains(BashShellState.ResolveNameref("psbt_rn_x"), new[] { "psbt_rn_x", "psbt_rn_y" });
            Assert.Equal("psbt_rn_arr[2]", BashShellState.ResolveNameref("psbt_rn_e"));
            Assert.Equal("psbt_rn_e[0]", BashShellState.ResolveNameref("psbt_rn_e[0]"));   // an element takes no subscript
        }
        finally
        {
            foreach (var n in new[] { "psbt_rn_a", "psbt_rn_b", "psbt_rn_x", "psbt_rn_y", "psbt_rn_e" })
                BashShellState.ClearNameref(n, null);
        }
    }

    [Fact]
    public void LocalNameref_IsTheFunctionsOwn_AndTheOuterOneComesBack()
    {
        // `declare -n g=outer; f() { local -n g=$1; }; f t`: in f g names t; after f returns g names outer again.
        BashShellState.SetNameref("psbt_ln_g", "psbt_ln_outer");
        var stack = VarFrameStack.Current;
        stack.Enter();
        try
        {
            VarFrameStack.LocalNameref("psbt_ln_g", "psbt_ln_t");
            Assert.Equal("psbt_ln_t", BashShellState.ResolveNameref("psbt_ln_g"));
            VarFrameStack.LocalNameref("psbt_ln_bad", "1bad");          // no variable name: nothing recorded
            Assert.Equal("psbt_ln_bad", BashShellState.ResolveNameref("psbt_ln_bad"));
            VarFrameStack.LocalNameref("psbt_ln_self", "psbt_ln_self");  // circular: nothing recorded
            Assert.Equal("psbt_ln_self", BashShellState.ResolveNameref("psbt_ln_self"));
        }
        finally { stack.Exit(); }
        try { Assert.Equal("psbt_ln_outer", BashShellState.ResolveNameref("psbt_ln_g")); }
        finally { BashShellState.ClearNameref("psbt_ln_g", null); }
    }

    [Fact]
    public void LocalReadonly_EndsWithItsFrame_AnOuterReadonlyStays()
    {
        // `f() { local -r v=1; }; f; v=2` works; a name that was read-only before the call stays read-only.
        var stack = new VarFrameStack();
        stack.Enter();
        stack.TakeAttributes("psbt_lr_v");
        BashShellState.MarkReadonly("psbt_lr_v");
        Assert.True(BashShellState.IsReadonly("psbt_lr_v"));
        stack.Exit();
        Assert.False(BashShellState.IsReadonly("psbt_lr_v"));

        BashShellState.MarkReadonly("psbt_lr_w");
        try
        {
            stack.Enter();
            stack.TakeAttributes("psbt_lr_w");
            stack.Exit();
            Assert.True(BashShellState.IsReadonly("psbt_lr_w"));
        }
        finally { BashShellState.UnmarkReadonly("psbt_lr_w"); }
    }
}
