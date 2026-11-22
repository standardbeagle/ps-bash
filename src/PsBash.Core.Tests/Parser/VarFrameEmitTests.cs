using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// bash's variable contexts (the oils <c>[bash_unset]</c> cases) are tracked at run time by
/// <c>PsBash.Cmdlets.VarFrameStack</c>; the emitter's part is to report each <c>local</c> and each temp binding that can
/// reach a <c>local</c>/<c>unset</c> (a function call, eval, source). The rules themselves are tested in the Cmdlets
/// tests (VarFrameStackTests), the end result in the differential test.
/// </summary>
public class VarFrameEmitTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Fact]
    public void Transpile_LocalWithoutValue_TakesTheFrameStacksValue()
        => Assert.Contains("$env:v = " + PsBuild.DeclareLocal("v"), T("f() { local v; echo \"$v\"; }"));

    [Fact]
    public void Transpile_LocalWithValue_RegistersTheFrameBeforeAssigning()
    {
        var ps = T("f() { local v=x; }");
        int declare = ps.IndexOf("[void]" + PsBuild.DeclareLocal("v"), StringComparison.Ordinal);
        Assert.True(declare >= 0, ps);
        Assert.True(ps.IndexOf("$env:v = ", declare, StringComparison.Ordinal) > declare, ps);
    }

    [Fact]
    public void Transpile_TempBindingOnAFunctionCall_PushesAFrameTheCalleeOwns()
    {
        var ps = T("f() { unset v; }; v=t f");
        Assert.Contains(PsBuild.PushTempFrame("v"), ps);
        Assert.Contains(PsBuild.RestoreTempBinding("v"), ps);
        Assert.DoesNotContain("$env:v = $__saved_v", ps);
    }

    [Fact]
    public void Transpile_FunctionLocal_RestoresThroughTheFrameStack()
        // unlocal() { unset -v "$1"; }; f() { local v=l; unlocal v; v=new; }: after f, v is new (the local's frame went).
        => Assert.Contains("if ($null -ne $__lsd_v) { " + PsBuild.RestoreLocal("v") + " }", T("f() { local v=l; }"));

    [Theory]
    [InlineData("v=t eval 'unset v'")]
    [InlineData("v=t source ./x.sh")]
    [InlineData("v=t . ./x.sh")]
    public void Transpile_TempBindingOnEvalOrSource_SavesOnTheRuntimeStack(string bash)
    {
        // `v=1 eval 'v=2 eval ...'`: both run in one PowerShell scope, a `$__saved_v` would be overwritten.
        var ps = T(bash);
        Assert.Contains(PsBuild.BindSameScope("v"), ps);
        Assert.Contains(PsBuild.UnbindSameScope("v"), ps);
        Assert.DoesNotContain("$__saved_v", ps);
    }

    [Fact]
    public void Transpile_LiteralEvalBodyUnderABinding_ReadsTheBoundValue()
    {
        // The inlined body runs once v=t is in force; only eval's own words (`"echo $v"`) expand before it.
        Assert.DoesNotContain("__psbash_ea_v", T("v=t eval 'echo $v'"));
        Assert.Contains("__psbash_ea_v", T("v=t eval \"echo $v\""));
    }

    private static string Eval(string bash)
        => PsBash.Core.Transpiler.BashTranspiler.Transpile(bash, PsBash.Core.Transpiler.TranspileContext.Eval)!;

    [Theory]
    [InlineData("declare d=p", "d", true)]
    [InlineData("typeset t=$1", "t", true)]
    [InlineData("declare -ix n=2*3", "n", true)]
    [InlineData("declare -r r=1", "r", true)]
    [InlineData("declare -- v=1", "v", true)]
    [InlineData("declare u", "u", false)]
    [InlineData("declare s+=x", "s", false)]
    public void Transpile_EvalTextDeclare_IsTheRunningFunctionsLocal(string bash, string name, bool keepsValue)
    {
        // `f(){ declare "$1"; }; f e=b`, `eval 'declare d=p'` in f: the text is emitted at run time, without its function,
        // so the run-time call takes the frame a run-time `local` takes (bash: e is gone once f returns).
        var ps = Eval(bash);
        int scope = ps.IndexOf(PsBuild.LocalWhenInFunction(name, keepsValue), StringComparison.Ordinal);
        Assert.True(scope >= 0, ps);
        Assert.True(ps.IndexOf("$env:" + name, scope + PsBuild.LocalWhenInFunction(name, keepsValue).Length, StringComparison.Ordinal) > 0, ps);
    }

    [Theory]
    [InlineData("declare -g g=1")]          // global by request
    [InlineData("declare -gx g=1")]
    [InlineData("declare -p g")]            // a listing declares nothing
    [InlineData("declare -a g=(1 2)")]      // arrays stay global, as a run-time local array does
    [InlineData("declare -A g")]
    [InlineData("declare -n g=x")]
    [InlineData("export g=1")]
    public void Transpile_EvalTextGlobalDeclaration_TakesNoFrame(string bash)
        => Assert.DoesNotContain("VarFrameStack]::Current.Depth -gt 0", Eval(bash));

    [Fact]
    public void Transpile_DeclareInAFunctionOutsideEval_StaysTheParsersLocal()
        // The function is known here: the static local path, no run-time branch.
        => Assert.DoesNotContain("VarFrameStack]::Current.Depth -gt 0", T("f() { declare d=p; }"));

    [Fact]
    public void RunTimeLocal_IsTheOneFrameForLocalAndDeclare()
    {
        Assert.Contains(PsBuild.RunTimeLocal("v", keepsValue: true, inWrittenFunction: false), Eval("local v=1"));
        Assert.Contains(PsBuild.RunTimeLocal("v", keepsValue: false, inWrittenFunction: false), Eval("local v"));
        Assert.Contains(PsBuild.RunTimeLocal("v", keepsValue: true, inWrittenFunction: false), PsBuild.LocalWhenInFunction("v", keepsValue: true));
        Assert.Contains(PsBuild.RunTimeLocal("v", keepsValue: true, inWrittenFunction: true), T("f() { local v=1; }"));
    }

    // Oracle: `d3(){ declare "$1"; d4 "$2"; echo "$dn"; }; d4(){ declare "$1"; }; d3 dn=3 dn=4` prints 3. Text built at run
    // time tests its OWN scope for the park slot: PowerShell's dynamic scope shows it the calling function's slot.
    // REGRESSION: d4 took d3's slot as its own and its declaration wrote d3's local (printed 4).
    [Theory]
    [InlineData("declare d=p", "d")]
    [InlineData("local d=p", "d")]
    [InlineData("local d+=p", "d")]
    public void Transpile_EvalTextLocal_LooksForItsParkSlotInItsOwnScope(string bash, string name)
    {
        var ps = Eval(bash);
        Assert.Contains(PsBuild.NotParked(name, inWrittenFunction: false), ps);
        Assert.DoesNotContain("($null -eq $__lsd_" + name + ")", ps);
    }

    [Fact]
    public void Transpile_WrittenFunctionLocal_ReadsItsOwnEntrySlot()
        // A written function sets $__lsd_v = $null on entry: the plain read is its own slot, no Get-Variable per local.
        => Assert.DoesNotContain("Get-Variable -Name '__lsd_v'", T("f() { local v=1; local w+=2; }"));

    // Oracle: `f(){ dc=declare; $dc wv=1; }; f; echo "${wv-unset}"` prints unset. The run-time call hands the field over
    // quoted (`declare 'wv=1'`): it is the written declaration, so the running function's local.
    // REGRESSION: it was a straight Set-Item of the variable, global.
    [Theory]
    [InlineData("declare 'wv=1'", "wv", true)]
    [InlineData("typeset 'e=b c'", "e", true)]
    [InlineData("declare -x 'xv=3'", "xv", true)]
    public void Transpile_EvalTextQuotedDeclare_IsTheRunningFunctionsLocal(string bash, string name, bool keepsValue)
    {
        var ps = Eval(bash);
        Assert.Contains(PsBuild.LocalWhenInFunction(name, keepsValue), ps);
        Assert.DoesNotContain("Set-Item -LiteralPath ('Env:' + $__psbN)", ps);
    }

    [Fact]
    public void Transpile_EvalTextQuotedGlobalDeclare_TakesNoFrame()
        => Assert.DoesNotContain("VarFrameStack]::Current.Depth -gt 0", Eval("declare -g 'gv=1'"));

    // Oracle: `f(){ declare -- dv=3; }; f; echo "${dv-unset}"` prints unset: `--` ends the options of a local declaration.
    // REGRESSION: it was a global declaration.
    [Fact]
    public void Transpile_DeclareDoubleDashInAFunction_IsALocal()
        => Assert.Contains(PsBuild.RestoreLocal("dv"), T("f() { declare -- dv=3; }"));

    [Theory]
    [InlineData("v=t echo hi")]
    [InlineData("IFS=, read -r a b <<< 'x,y'")]
    [InlineData("v=t notafunction")]
    public void Transpile_TempBindingOnOtherCommands_PushesNoFrame(string bash)
    {
        var ps = T(bash);
        Assert.DoesNotContain("VarFrameStack", ps);
        Assert.Contains("$__saved_", ps);
    }
}
