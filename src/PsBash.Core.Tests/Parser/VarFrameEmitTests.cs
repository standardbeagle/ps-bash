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
    [InlineData("declare -ga g=(1 2)")]
    [InlineData("readonly -a g=(1 2)")]
    [InlineData("declare -gA g")]
    [InlineData("export g=1")]
    public void Transpile_EvalTextGlobalDeclaration_TakesNoFrame(string bash)
        => Assert.DoesNotContain("VarFrameStack]::Current.Depth -gt 0", Eval(bash));

    // Oracle: `f(){ local -a "$1"; declare -A "$2"; declare -n "$3"; }; f 'a=(1 2)' m 'r=x'; declare -p a m r` finds none
    // of them. The text cannot know whether a function runs it, so both forms are emitted and the run-time depth picks one
    // (PsBuild.WhenInFunction): in a function the array is that scope's PowerShell variable, listed as its local array.
    // REGRESSION: arrays and references declared by such text were global.
    [Theory]
    [InlineData("declare -a g=(1 2)", "g")]
    [InlineData("local -a g=(1 2)", "g")]
    [InlineData("declare -A g=([k]=v)", "g")]
    [InlineData("declare -A g", "g")]
    [InlineData("local -a g", "g")]
    [InlineData("local -a 'g=(1 2)'", "g")]   // quoted, as a run-time `local -a "$1"` hands it over
    public void Transpile_EvalTextArrayDeclaration_IsTheRunningFunctionsLocalArray(string bash, string name)
    {
        var ps = Eval(bash);
        Assert.Contains("if ([PsBash.Cmdlets.VarFrameStack]::Current.Depth -gt 0) { ", ps);
        Assert.Contains(PsBuild.LocalArrays([name]), ps);
        Assert.Contains("} else { ", ps);
        // The top-level form (`local` there is an error) is the shell's array: the global, or the slot that finds it so a
        // subshell's journal puts it back (`( eval 'declare -a g=(1 2)' )` leaves no g).
        if (bash.StartsWith("declare"))
            Assert.True(ps.Contains("$global:" + name, StringComparison.Ordinal)
                || ps.Contains("VarFrameStack]::ArraySlot($ExecutionContext.SessionState, '" + name + "')", StringComparison.Ordinal), ps);
    }

    // Oracle: `f(){ eval "$1"; }; f 'local -a a=(1 2); a+=(3); declare -A m; m[k]=v'` leaves no a or m after f, and so
    // for a sourced file. The text cannot know whether a function runs it, so a later write in it is the run-time slot
    // (VarFrameStack.ArraySlot: the running function's local array when it holds one, else the global).
    // REGRESSION: it was `$global:a`, while the declaration had made a the function's local.
    [Theory]
    [InlineData("local -a a=(1 2); a+=(3)", "a")]
    [InlineData("declare -A m; m[k]=v", "m")]
    [InlineData("local -a e; e[3]=x", "e")]
    [InlineData("S+=(2)", "S")]
    public void Transpile_EvalTextArrayWrite_IsTheRunTimeSlot(string bash, string name)
        => Assert.Contains(PsBuild.ArraySlot(name)[..^".Value".Length], Eval(bash));

    // Oracle: `f(){ local -a g=(l); eval 'declare -ga g=(1 2)'; echo ${g[*]}; }; f; echo ${g[*]}` prints l, then 1 2: -g
    // is the global even over the function's local array of the name (VarFrameStack.GlobalArraySlot), and so without
    // eval. `readonly -a g=(1 2)` there sets the local (prints 1 2, then nothing): the array in sight, the run-time slot.
    // REGRESSION: -g wrote the local.
    [Theory]
    [InlineData("declare -ga g=(1 2)")]
    [InlineData("declare -gA g=([k]=v)")]
    [InlineData("declare -ga g+=(3)")]
    public void Transpile_GlobalArrayDeclaration_WritesTheGlobalSlot(string bash)
    {
        string slot = PsBuild.GlobalArraySlot("g")[..^".Value".Length];
        Assert.Contains(slot, Eval(bash));
        Assert.Contains(slot, T("f() { local -a g=(l); " + bash + "; }"));
    }

    [Fact]
    public void Transpile_ReadonlyArrayDeclaration_WritesTheArrayInSight()
    {
        Assert.Contains(PsBuild.ArraySlot("g")[..^".Value".Length], Eval("readonly -a g=(1 2)"));
        Assert.DoesNotContain("GlobalArraySlot(", T("f() { local -a g=(l); readonly -a g=(1 2); }"));
    }

    // Oracle: `v=n; declare -i "$v"; source ./w.sh` with `n=2+3` in w.sh prints 5, and `declare -i n; source ./w.sh` too:
    // eval and sourced text cannot see its caller's attributes, so it writes through the run-time table, and a unit that
    // runs such text records its static attributes there.
    [Fact]
    public void Transpile_EvalOrSourcedText_WritesThroughTheAttributeTable()
    {
        Assert.Contains("[PsBash.Cmdlets.BashShellState]::AssignScalar('n', ", Eval("n=2+3"));
        Assert.Contains("[PsBash.Cmdlets.BashShellState]::MarkAttribute('n', 'i')", T("declare -i n\nsource ./w.sh"));
        Assert.DoesNotContain("MarkAttribute('n', 'i')", T("declare -i n\nn=1"));
    }

    [Theory]
    [InlineData("declare -n g=x")]
    [InlineData("local -n g=x")]
    public void Transpile_EvalTextNameref_TakesTheRunningFunctionsFrame(string bash)
        => Assert.Contains("[PsBash.Cmdlets.VarFrameStack]::", Eval(bash));

    // Oracle: `f(){ declare -a "$1"; }; f 'a=(1 2)'` declares the array a=(1 2): with -a/-A the expanded word is an array
    // assignment. REGRESSION: the quoted value was the scalar "(1 2)".
    [Fact]
    public void Transpile_EvalTextQuotedArrayValue_IsACompoundAssignment()
    {
        var ps = Eval("declare -a 'a=(1 2)'");
        Assert.Contains(PsBuild.LocalArrays(["a"]), ps);
        Assert.DoesNotContain("'(1 2)'", ps);
    }

    // Oracle: `f(){ local -a 'b=(3 4)'; declare -p b; }; f` prints declare -a b=([0]="3" [1]="4"); `declare e='(1 2)'`
    // (no -a) stays the scalar. The parser reads the quoted operand as the compound (BashParser.QuotedCompoundArray).
    [Theory]
    [InlineData("f(){ local -a 'b=(3 4)'; }")]
    [InlineData("f(){ declare -a \"b=(3 4)\"; }")]
    [InlineData("declare -a b='(3 4)'")]
    [InlineData("f(){ typeset -A 'b=([3]=4)'; }")]
    public void Transpile_QuotedCompoundWithAnArrayFlag_IsTheArray(string bash)
    {
        var ps = T(bash);
        Assert.DoesNotContain("'(3 4)'", ps);     // the scalar form
        Assert.DoesNotContain("'([3]=4)'", ps);
    }

    [Fact]
    public void Transpile_QuotedCompoundWithoutAnArrayFlag_StaysTheScalar()
        => Assert.Contains("'(1 2)'", T("f(){ declare 'e=(1 2)'; }"));

    [Fact]
    public void Transpile_QuotedArrayValueClosingTheList_IsNotRebuilt()
        // `declare -a 'a=(1); echo pwned)'`: bash reads the value as words of the array, never as commands.
        => Assert.DoesNotContain("Invoke-BashEcho", Eval("declare -a 'a=(1); echo pwned)'"));

    // Oracle: `f(){ eval 'declare -i iv=2*3'; iv+=1; echo $iv; }; f` prints 7: a static eval's text in a function is the
    // function's own code (its declare is local) and shares the attributes it sets with the code after it.
    // REGRESSION: the text's declarations were its own (iv+=1 appended: 61) and global.
    [Fact]
    public void Transpile_StaticEvalInAFunction_SharesItsAttributesWithTheBody()
    {
        var ps = T("f(){ eval 'declare -i iv=2*3'; iv+=1; echo $iv; }");
        Assert.Contains("Invoke-BashArith ('iv+(' + ", ps);
        Assert.DoesNotContain("$env:iv += ", ps);
    }

    // Oracle: `f(){ declare -i v; declare -n r=v; r=3*3; echo $v; }; f` prints 9: a write through a nameref takes its
    // target's attributes. REGRESSION: the reference's own name was looked up, so 3*3 was stored as written.
    [Fact]
    public void Transpile_WriteThroughANameref_UsesTheTargetsAttributes()
        => Assert.Contains("$env:v = [string](Invoke-BashArith", T("f(){ declare -i v; declare -n r=v; r=3*3; }"));

    // Oracle: `declare -l s=AB; s+=CD; echo $s` prints abcd: the appended whole is converted.
    [Fact]
    public void Transpile_CaseAttributeAppend_ConvertsTheWholeValue()
        => Assert.Contains("([string]($env:s + (", T("declare -l s=AB; s+=CD"));

    // Oracle: `f(){ local -i "$1"; n=2*3; echo $n; }; f n` prints 6. The emitter cannot see an attribute set by text built
    // at run time, so in a unit that runs such text a plain write consults the run-time table (PsBuild.AssignAttributed).
    [Theory]
    [InlineData("f(){ local -i \"$1\"; n=2*3; }")]
    [InlineData("f(){ eval \"$1\"; n=2*3; }")]
    [InlineData("d=declare; $d -i n; n=2*3")]
    [InlineData("source ./lib; n=2*3")]
    [InlineData("eval 'eval \"$c\"'; n=2*3")]
    public void Transpile_UnitWithRunTimeDeclarations_WritesThroughTheAttributeTable(string bash)
        => Assert.Contains("[PsBash.Cmdlets.BashShellState]::AssignScalar('n', ", T(bash));

    // The hot path: a unit whose declarations are all known now keeps the plain write.
    [Theory]
    [InlineData("n=2*3; m+=x")]
    [InlineData("f(){ local -i k; n=2*3; }")]
    [InlineData("eval 'declare -i k=1'; n=2*3")]
    [InlineData("declare -i k; n=$(eval \"$c\")")]   // a substitution's declarations stay in its subshell
    public void Transpile_UnitWithOnlyStaticDeclarations_KeepsThePlainWrite(string bash)
        => Assert.DoesNotContain("AssignScalar", T(bash));

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
