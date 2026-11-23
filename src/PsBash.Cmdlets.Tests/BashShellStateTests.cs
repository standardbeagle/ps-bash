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

    // `x=1; ( shopt -s failglob; echo zz* ); echo $?`: the body's failglob ends the body only. The line mark it set
    // skipped the parent's `echo $?` (and every later command on the line, a command substitution's too).
    [Fact]
    public void Restore_PutsBackTheLineMark()
    {
        var r = Run(
            "$global:__BashFailGlob = $false; "
            + "$s = [PsBash.Cmdlets.BashShellState]::Save($ExecutionContext.SessionState); "
            + "$global:__BashFailGlob = $true; "
            + "[void][PsBash.Cmdlets.BashShellState]::Restore($ExecutionContext.SessionState, $s); "
            + "\"mark=$global:__BashFailGlob\"").AssertSuccess();

        Assert.Contains("mark=False", r.Stdout);
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
    public void NamerefWithNoTarget_EveryWriterNamesTheTarget_AndAValueThatIsNoNameFails()
    {
        // bash: `declare -n r; read r <<< t` / `printf -v r %s t` / `getopts ab r -a` / `r=$v` make the value r's target;
        // `printf -v r %s 'a b'` reports "`a b': not a valid identifier", status 1, r keeps no target. REGRESSION (8bd50899):
        // only a literal `r=name` the emitter saw named it; every other writer stored the value in r.
        var r = Run(
            "foreach ($n in 'psbt_wt_a','psbt_wt_b','psbt_wt_c','psbt_wt_d','psbt_wt_e') { [PsBash.Cmdlets.BashShellState]::SetNameref($n, '') }; "
            + "'psbt_wt_ta' | Invoke-BashRead psbt_wt_a; "
            + "Invoke-BashPrintf '-v' psbt_wt_b '%s' psbt_wt_tb; "
            // `'-a'` quoted, as the emitter writes it: a bare -a binds to the cmdlet's -Arguments parameter.
            + "Invoke-BashGetopts ab psbt_wt_c '-a'; "
            + "\"assign=[$([PsBash.Cmdlets.BashVariableLookup]::AssignThroughNameref($ExecutionContext.SessionState, 'psbt_wt_d', 'psbt_wt_td', $false))]\"; "
            + "[void][PsBash.Cmdlets.BashVariableLookup]::AssignThroughNameref($ExecutionContext.SessionState, 'psbt_wt_d', 'five', $false); "
            + "[void][PsBash.Cmdlets.BashVariableLookup]::AssignThroughNameref($ExecutionContext.SessionState, 'psbt_wt_d', '+', $true); "
            + "\"td=$env:psbt_wt_td read=$([PsBash.Cmdlets.BashVariableLookup]::ReadThroughNameref($ExecutionContext.SessionState, 'psbt_wt_d'))\"; "
            + "Invoke-BashPrintf '-v' psbt_wt_e '%s' 'a b' 2>&1 | ForEach-Object { \"e: $_\" }; \"est=$global:LASTEXITCODE\"; "
            + "\"bad=[$([PsBash.Cmdlets.BashVariableLookup]::AssignThroughNameref($ExecutionContext.SessionState, 'psbt_wt_e', '5', $false))]\"; "
            + "Invoke-BashDeclare -p psbt_wt_a psbt_wt_b psbt_wt_c psbt_wt_d psbt_wt_e | ForEach-Object { \"p: $_\" }; "
            + "Invoke-BashUnset -n psbt_wt_a psbt_wt_b psbt_wt_c psbt_wt_d psbt_wt_e; Invoke-BashUnset psbt_wt_td; $global:LASTEXITCODE = 0").AssertSuccess();

        Assert.Contains("assign=[]", r.Stdout);
        Assert.Contains("td=five+ read=five+", r.Stdout);
        Assert.Contains("p: declare -n psbt_wt_a=\"psbt_wt_ta\"", r.Stdout);
        Assert.Contains("p: declare -n psbt_wt_b=\"psbt_wt_tb\"", r.Stdout);
        Assert.Contains("p: declare -n psbt_wt_c=\"a\"", r.Stdout);
        Assert.Contains("p: declare -n psbt_wt_d=\"psbt_wt_td\"", r.Stdout);
        Assert.Contains("e: ", r.Stdout);
        Assert.Contains("printf: `a b': not a valid identifier", r.Stdout);
        Assert.Contains("est=1", r.Stdout);
        Assert.Contains("bad=[`5': not a valid identifier]", r.Stdout);
        Assert.Contains("p: declare -n psbt_wt_e\n", r.Stdout.Replace("\r\n", "\n") + "\n");
    }

    [Fact]
    public void NamerefToAnElementWithABadSubscript_ReturnsBashsDiagnostic_AndMarksASubscriptError()
    {
        // bash: `a=(1); declare -n r; v='a[1/0]'; r=$v; r=x` reports `1/0: division by 0 (error token is "0")`, an expansion
        // error. REGRESSION: the arithmetic exception escaped AssignThroughNameref as a PowerShell error.
        var r = Run(
            "$global:psbt_se_a = @('1');[PsBash.Cmdlets.BashShellState]::SetNameref('psbt_se_r', 'psbt_se_a[1/0]'); "
            + "\"msg=[$([PsBash.Cmdlets.BashVariableLookup]::AssignThroughNameref($ExecutionContext.SessionState, 'psbt_se_r', 'x', $false))]\"; "
            + "\"sub=$([PsBash.Cmdlets.BashArrayElement]::TakeSubscriptError()) again=$([PsBash.Cmdlets.BashArrayElement]::TakeSubscriptError())\"; "
            + "[PsBash.Cmdlets.BashShellState]::SetNameref('psbt_se_r', 'psbt_se_a[0]'); "
            + "\"ok=[$([PsBash.Cmdlets.BashVariableLookup]::AssignThroughNameref($ExecutionContext.SessionState, 'psbt_se_r', 'y', $false))] sub=$([PsBash.Cmdlets.BashArrayElement]::TakeSubscriptError()) a0=$($global:psbt_se_a[0])\"; "
            + "Invoke-BashUnset -n psbt_se_r; Remove-Variable -Scope Global psbt_se_a; $global:LASTEXITCODE = 0").AssertSuccess();
        Assert.Contains("msg=[1/0: division by 0 (error token is \"0\")]", r.Stdout);
        Assert.Contains("sub=True again=False", r.Stdout);
        Assert.Contains("ok=[] sub=False a0=y", r.Stdout);
    }

    [Fact]
    public void NamerefWithATarget_ReadAndPrintfV_WriteTheTarget()
    {
        // bash: `declare -n r=x; read r <<< hi` sets x and leaves r a reference. REGRESSION: read and printf -v set r.
        var r = Run(
            "[PsBash.Cmdlets.BashShellState]::SetNameref('psbt_rt_r', 'psbt_rt_x'); [PsBash.Cmdlets.BashShellState]::SetNameref('psbt_rt_q', 'psbt_rt_y'); "
            + "'hi' | Invoke-BashRead psbt_rt_r; Invoke-BashPrintf '-v' psbt_rt_q '%s' yo; "
            + "\"x=$env:psbt_rt_x y=$env:psbt_rt_y r=[$env:psbt_rt_r] q=[$env:psbt_rt_q]\"; "
            + "Invoke-BashUnset -n psbt_rt_r psbt_rt_q; Invoke-BashUnset psbt_rt_x psbt_rt_y; $global:LASTEXITCODE = 0").AssertSuccess();
        Assert.Contains("x=hi y=yo r=[] q=[]", r.Stdout);
    }

    [Fact]
    public void BindLoopNameref_RetargetsAnyNameref_AndRefusesAWordThatIsNoName()
    {
        var r = Run(
            "[PsBash.Cmdlets.BashShellState]::SetNameref('psbt_bl_r', 'psbt_bl_x'); "
            + "\"a=$([PsBash.Cmdlets.BashShellState]::BindLoopNameref('psbt_bl_r', 'psbt_bl_a'))\"; "
            + "\"b=$([PsBash.Cmdlets.BashShellState]::BindLoopNameref('psbt_bl_r', 'b c'))\"; "
            + "\"e=$([PsBash.Cmdlets.BashShellState]::BindLoopNameref('psbt_bl_r', 'psbt_bl_e[1]'))\"; "
            + "\"to=$([PsBash.Cmdlets.BashShellState]::ResolveNameref('psbt_bl_r'))\"; "
            + "Invoke-BashUnset -n psbt_bl_r; $global:LASTEXITCODE = 0").AssertSuccess();
        Assert.Contains("a=True", r.Stdout);
        Assert.Contains("b=False", r.Stdout);
        Assert.Contains("e=True", r.Stdout);
        Assert.Contains("to=psbt_bl_e[1]", r.Stdout);
    }

    [Fact]
    public void ViewNameref_TheVariableFollowsTheReference_ReadAndWrite()
    {
        // bash: `declare -n r; w=arr; r=$w; arr=(a b)` makes ${r[@]} `a b` and `r+=(z)` grow arr. Every array form the emitter
        // writes reads `$r`, so `$r` itself follows the reference. REGRESSION: `${r[@]}` read r's own (unset) array.
        var r = Run(
            "[PsBash.Cmdlets.BashShellState]::SetNameref('psbt_vw_r', ''); [PsBash.Cmdlets.BashVariableLookup]::ViewNameref($ExecutionContext.SessionState, 'psbt_vw_r'); "
            + "\"none=[$($null -eq $psbt_vw_r)]\"; "
            + "[void][PsBash.Cmdlets.BashVariableLookup]::AssignThroughNameref($ExecutionContext.SessionState, 'psbt_vw_r', 'psbt_vw_arr', $false); "
            + "$global:psbt_vw_arr = @('a', 'b'); \"read=[$($psbt_vw_r -join ',')] n=$($psbt_vw_r.Count)\"; "
            + "$psbt_vw_r = @('x', 'y', 'z'); \"wrote=[$($global:psbt_vw_arr -join ',')]\"; "
            + "\"listed=$(@(Invoke-BashDeclare -p | Where-Object { $_ -like 'declare -a psbt_vw_r=*' }).Count)\"; "
            + "Invoke-BashTest '-v' psbt_vw_r; \"v=$global:LASTEXITCODE\"; Invoke-BashTest '-v' 'psbt_vw_r[2]'; \"v2=$global:LASTEXITCODE\"; "
            + "\"name=$([PsBash.Cmdlets.BashVariableLookup]::NamerefName('psbt_vw_r'))\"; "
            + "Invoke-BashUnset -n psbt_vw_r; \"after=[$($null -eq $psbt_vw_r)] arr=[$($global:psbt_vw_arr -join ',')]\"; "
            + "Remove-Variable -Name psbt_vw_arr -Scope Global; $global:LASTEXITCODE = 0").AssertSuccess();
        Assert.Contains("none=[True]", r.Stdout);
        Assert.Contains("read=[a,b] n=2", r.Stdout);
        Assert.Contains("wrote=[x,y,z]", r.Stdout);
        Assert.Contains("listed=0", r.Stdout);
        Assert.Contains("v=0", r.Stdout);
        Assert.Contains("v2=0", r.Stdout);
        Assert.Contains("name=psbt_vw_arr", r.Stdout);
        Assert.Contains("after=[True] arr=[x,y,z]", r.Stdout);
    }

    [Theory]
    [InlineData("-vn")]
    [InlineData("-nv")]
    [InlineData("-v -n")]   // the binder takes -v (the V decoy): a direct call must still read it as an option
    [InlineData("-n -n")]
    public void UnsetClusterWithN_DropsTheReference_NotTheTarget(string options)
    {
        // bash: `declare -n r=y; y=1; unset -vn r` leaves y=1 and r "not found". REGRESSION (abe9d17b): `-vn` was skipped.
        var r = Run(
            "[PsBash.Cmdlets.BashShellState]::SetNameref('psbt_uc_r', 'psbt_uc_y'); $env:psbt_uc_y = '1'; "
            + $"Invoke-BashUnset {options} psbt_uc_r; \"st=$global:LASTEXITCODE y=$env:psbt_uc_y\"; "
            + "Invoke-BashDeclare -p psbt_uc_r 2>&1 | ForEach-Object { \"p: $_\" }; "
            + "Invoke-BashUnset psbt_uc_y; $global:LASTEXITCODE = 0").AssertSuccess();
        Assert.Contains("st=0 y=1", r.Stdout);
        Assert.Contains("psbt_uc_r: not found", r.Stdout);
    }

    [Theory]
    [InlineData("-fv")]
    [InlineData("-vf")]
    [InlineData("-f -v")]
    [InlineData("-v -f")]
    [InlineData("-fvn")]
    public void UnsetFunctionAndVariable_IsAnError_AndUnsetsNothing(string options)
    {
        // bash: "unset: cannot simultaneously unset a function and a variable", status 1, whichever comes last.
        var r = Run(
            "$env:psbt_fv_v = '1'; "
            + $"Invoke-BashUnset {options} psbt_fv_v 2>&1 | ForEach-Object {{ \"e: $_\" }}; \"st=$global:LASTEXITCODE v=$env:psbt_fv_v\"; "
            + "Invoke-BashUnset psbt_fv_v; $global:LASTEXITCODE = 0").AssertSuccess();
        Assert.Contains("cannot simultaneously unset a function and a variable", r.Stdout);
        Assert.Contains("st=1 v=1", r.Stdout);
    }

    [Fact]
    public void Unset_ANameAfterTheFirstOperand_IsNoOption()
    {
        // bash: `unset hv -f hfn` unsets the variables hv and hfn; the function hfn stays.
        var r = Run(
            "function psbt_op_f { 'F' }; $env:psbt_op_v = '1'; $env:psbt_op_f = '2'; "
            + "Invoke-BashUnset psbt_op_v -f psbt_op_f; "
            + "\"v=[$env:psbt_op_v] f=[$env:psbt_op_f] fn=$(Test-Path Function:\\psbt_op_f)\"; $global:LASTEXITCODE = 0").AssertSuccess();
        Assert.Contains("v=[] f=[] fn=True", r.Stdout);
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

    // The run-time attribute writer (PsBuild.AssignAttributed) over the one attribute table. End to end (bash oracle):
    // RunTimeArrayAttributeDifferentialTests.AttributesSetAtRunTime_HoldForTheWritesAfterThem.
    [Fact]
    public void AssignScalar_ConvertsCaseAttributesAndRefusesAnInteger()
    {
        try
        {
            BashShellState.MarkAttribute("psbt_as_u", "u");
            Assert.True(BashShellState.AssignScalar("psbt_as_u", "ab", append: false));
            Assert.True(BashShellState.AssignScalar("psbt_as_u", "cd", append: true));
            Assert.Equal("ABCD", BashVariableStore.Get("psbt_as_u"));

            BashShellState.MarkAttribute("psbt_as_l", "l");
            BashVariableStore.Set("psbt_as_l", "x");
            Assert.True(BashShellState.AssignScalar("psbt_as_l", "YZ", append: true));
            Assert.Equal("xyz", BashVariableStore.Get("psbt_as_l"));

            // -i: nothing written, the caller evaluates the arithmetic.
            BashShellState.MarkAttribute("psbt_as_i", "i");
            BashVariableStore.Set("psbt_as_i", "5");
            Assert.False(BashShellState.AssignScalar("psbt_as_i", "2*3", append: false));
            Assert.Equal("5", BashVariableStore.Get("psbt_as_i"));

            // No attributes: the plain write, append included.
            BashVariableStore.Set("psbt_as_p", "a");
            Assert.True(BashShellState.AssignScalar("psbt_as_p", "B", append: true));
            Assert.Equal("aB", BashVariableStore.Get("psbt_as_p"));
        }
        finally
        {
            foreach (var n in new[] { "psbt_as_u", "psbt_as_l", "psbt_as_i", "psbt_as_p" })
            {
                BashShellState.TakeAttributes(n);
                BashVariableStore.Set(n, null);
            }
        }
    }

    [Fact]
    public void AssignScalar_WritesThroughANameref()
    {
        try
        {
            BashShellState.SetNameref("psbt_as_r", "psbt_as_t");
            BashShellState.MarkAttribute("psbt_as_t", "u");
            Assert.True(BashShellState.AssignScalar("psbt_as_r", "via", append: false));
            Assert.Equal("VIA", BashVariableStore.Get("psbt_as_t"));
            Assert.Null(BashVariableStore.Get("psbt_as_r"));

            // An integer target: AssignEvaluated stores the caller's result in the target too.
            BashShellState.MarkAttribute("psbt_as_t", "i");
            Assert.False(BashShellState.AssignScalar("psbt_as_r", "1+1", append: false));
            BashShellState.AssignEvaluated("psbt_as_r", "2");
            Assert.Equal("2", BashVariableStore.Get("psbt_as_t"));

            // A nameref with no target yet: the value names the target, nothing is stored.
            BashShellState.SetNameref("psbt_as_e", "");
            Assert.True(BashShellState.AssignScalar("psbt_as_e", "psbt_as_t", append: false));
            Assert.Equal("psbt_as_t", BashShellState.NamerefTarget("psbt_as_e"));
            Assert.Equal("2", BashVariableStore.Get("psbt_as_t"));
        }
        finally
        {
            BashShellState.TakeNameref("psbt_as_r");
            BashShellState.TakeNameref("psbt_as_e");
            BashShellState.TakeAttributes("psbt_as_t");
            BashVariableStore.Set("psbt_as_t", null);
        }
    }
}
