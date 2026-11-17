using PsBash.Core.Parser;
using PsBash.Core.Transpiler;
using Xunit;

namespace PsBash.Core.Tests.Transpiler;

/// <summary>
/// Emitter regressions for the oils corpus cases arith "Side Effect in Array Indexing", loop "$b break, ...",
/// errexit-osh inherit_errexit, sh-func "Scope of global variable when sourced in function", nix-idioms
/// "export with dynamic var name +=" and builtin-special "(set -o posix)". The run-time half is covered by those cases.
/// </summary>
public class ShellSemanticsRegressionTests
{
    // Oracle: `a=(4 5 6); echo "${a[b=2]} b=$b"` prints "6 b=2". The subscript was read as the text b=2 (element 0).
    [Fact]
    public void Transpile_ArraySubscriptWithAssignment_IsEvaluatedAsArithmetic()
    {
        var ps = BashTranspiler.Transpile("a=(1 2 3); echo ${a[b=2]}");
        Assert.Contains("[long](Invoke-BashArith 'b=2')", ps);
    }

    // Oracle: `b=break; for i in 1 2; do $b; echo no; done` prints nothing. A command name known only at run time goes
    // through Resolve-BashCommand; in a function (or eval text) a `return` it ran must leave the function too.
    [Fact]
    public void Transpile_DynamicCommandInFunction_ChecksForAReturnItRan()
    {
        var ps = BashTranspiler.Transpile("f(){ $b; }");
        Assert.Contains("& (Resolve-BashCommand -Fed -Words @(ConvertTo-BashWords $env:b)); if ($global:__BashEvalReturn) { $global:__BashEvalReturn = $false; return }", ps);
        // At top level there is no function to leave.
        Assert.DoesNotContain("__BashEvalReturn", BashTranspiler.Transpile("$b"));
    }

    // Oracle: `g(){ . ./lib.sh; }; g; test_func` runs test_func (defined by lib.sh); eval and sourced text define a
    // function for the whole shell, not for the PowerShell scope of the Invoke-Expression that ran it.
    [Fact]
    public void Transpile_FunctionInEvalText_IsGlobal()
    {
        Assert.StartsWith("function global:f ", BashTranspiler.Transpile("f() { :; }", TranspileContext.Eval));
        Assert.StartsWith("function f ", BashTranspiler.Transpile("f() { :; }"));
    }

    // Oracle: `main() { . ./tmp.sh; }; main; echo "${L-unset}"` with `local L=local` in tmp.sh prints unset: the
    // file's local belongs to main, and main's return puts the caller's value back (L outlived main).
    [Fact]
    public void Transpile_FunctionThatSources_RestoresTheLocalsTheFileDeclared()
    {
        const string restore = "Get-Variable -Name '__lsd_*' -Scope 0";
        Assert.Contains(restore, BashTranspiler.Transpile("main() { . ./tmp.sh; }"));
        Assert.Contains(restore, BashTranspiler.Transpile("main() { source ./tmp.sh; }"));
        Assert.DoesNotContain(restore, BashTranspiler.Transpile("main() { echo hi; }"));
    }

    // Oracle: `f(){ x=1; v=x; export "$v"; }; f; declare -p x` prints `declare -x x="1"`. The declare -p is in the
    // caller, so eval text always records the x attribute.
    [Fact]
    public void Transpile_ExportInEvalText_RecordsTheExportAttribute()
    {
        Assert.Contains("[PsBash.Cmdlets.BashVariableStore]::Export('XX')", BashTranspiler.Transpile("export XX", TranspileContext.Eval));
        Assert.Contains("[PsBash.Cmdlets.BashVariableStore]::Export($__psbN)",
            BashTranspiler.Transpile("export \"$k=$v\"", TranspileContext.Eval));
    }

    // Oracle: `pfx=_foo; export NIX${pfx}+=" -L"` exports NIX_foo; it was "export: command not found". The operand runs
    // through the run-time eval, each field quoted (${w@Q}) so its value is never re-parsed as code.
    [Fact]
    public void Transpile_ExportOfAnExpandedName_RunsThroughTheQuotedEval()
    {
        var ps = BashTranspiler.Transpile("pfx=AB; export Q${pfx}=v");
        Assert.Contains("$__psbash_eval_src = @('export', ", ps);
        Assert.Contains("Invoke-BashPrintf '%s ' \"$(if ($null -eq $env:__psbash_declw) { '' } else { [PsBash.Cmdlets.BashQuoteText]::ForReuse($env:__psbash_declw) })\"", ps);
        Assert.DoesNotContain("Invoke-BashExport", ps);
    }

    // Oracle: `v=QQ; export "$v"; declare -p QQ` and `export NIX${pfx}+=" -L"` (twice) give declare -x. The run-time
    // eval quotes each field, so the eval text is `export 'QQ'` / `export 'NIX_foo+= -L'`: a quoted plain name is a
    // declaration (it was "export: command not found") and a quoted NAME+=VALUE appends (it set a variable "NIX_foo+").
    [Fact]
    public void Transpile_QuotedDeclarationOperandInEvalText_IsAPlainNameAndCanAppend()
    {
        var name = BashTranspiler.Transpile("export 'QQ'", TranspileContext.Eval);
        Assert.Contains("[PsBash.Cmdlets.BashVariableStore]::Export('QQ')", name);
        Assert.DoesNotContain("export 'QQ'", name);
        var append = BashTranspiler.Transpile("export 'QQ+= -L'", TranspileContext.Eval);
        // After quote removal it is the assignment NAME+=VALUE (BashParser.QuotedDeclarationAssignment).
        Assert.Contains("$env:QQ += ' -L'", append);
        Assert.Contains("if ($__psbN.EndsWith('+')) { $__psbN = $__psbN.Substring(0, $__psbN.Length - 1); $__psbV = [string][Environment]::GetEnvironmentVariable($__psbN) + $__psbV }",
            BashTranspiler.Transpile("export \"$n+= -L\"", TranspileContext.Eval));
        // Oracle: `two='b c'; readonly $two; b=new` is status 1; the eval text `readonly 'b' 'c'` marked nothing.
        Assert.Contains("[PsBash.Cmdlets.BashShellState]::MarkReadonly('c')", BashTranspiler.Transpile("readonly 'b' 'c'", TranspileContext.Eval));
        // A quoted operand that is not a plain name keeps its old path (no declaration is made of it).
        Assert.DoesNotContain("Export('a-b'", BashTranspiler.Transpile("export 'a-b'", TranspileContext.Eval));
    }

    // Oracle: `shopt -s inherit_errexit; set -e; x=$(false; echo no)` leaves x empty and exits 1. The substitution
    // body gets its errexit checks, armed at run time only while the option is on.
    [Fact]
    public void Transpile_CaptureUnderInheritErrexit_KeepsItsErrexitChecks()
    {
        var ps = BashTranspiler.Transpile("shopt -s inherit_errexit; set -e; x=$(false; echo no)");
        Assert.Contains("ShoptOn('inherit_errexit')", ps);
        Assert.Contains("-ne 0) { return }; Invoke-BashEcho no", ps);
        // Without the option in the script the substitution is not checked.
        Assert.DoesNotContain("ShoptOn('inherit_errexit')", BashTranspiler.Transpile("set -e; x=$(false; echo no)"));
    }

    // Oracle (bash 5.2, set -o posix): `foo=bar :; echo foo=$foo` prints foo=bar; `z=Z builtin :` leaves z unset.
    [Fact]
    public void Transpile_PrefixBeforeSpecialBuiltin_PersistsInPosixMode()
    {
        var ps = BashTranspiler.Transpile("set -o posix; foo=bar :");
        // The persisting assignment is still a binding frame while the builtin runs (`foo=bar unset foo` takes it).
        Assert.Contains("if ([PsBash.Cmdlets.InvokeBashShoptCommand]::SetOptionOn('posix')) { [PsBash.Cmdlets.VarFrameStack]::BindSameScope('foo', $env:foo); $env:foo = \"bar\"; ", ps);
        Assert.Contains("finally { [PsBash.Cmdlets.VarFrameStack]::PersistSameScope('foo'); }", ps);
        Assert.Contains("} else { $__saved_foo = $env:foo; ", ps);
        // `builtin` is not a special builtin; a script that never names the option gets the usual form only.
        Assert.DoesNotContain("SetOptionOn('posix')", BashTranspiler.Transpile("set -o posix; z=Z builtin :"));
        Assert.DoesNotContain("SetOptionOn('posix')", BashTranspiler.Transpile("foo=bar :"));
    }

    // Oracle (set -o posix): `eval() { :; }` prints "`eval': is a special builtin" and the shell ends with status 2;
    // a function defined before the option is passed over by a call once it is on (`eval 'echo after posix'`).
    [Fact]
    public void Transpile_SpecialBuiltinFunction_InPosixMode_IsRefusedAndPassedOver()
    {
        var def = BashTranspiler.Transpile("set -o posix; eval() { :; }");
        Assert.Contains("SetOptionOn('posix')) { Write-Error -Message 'bash: `eval'': is a special builtin' -ErrorAction Continue; $global:LASTEXITCODE = 2; exit 2 } else { function eval ", def);

        var call = BashTranspiler.Transpile("eval() { echo f; }\nset -o posix\neval 'echo hi'");
        int split = call.IndexOf("SetOptionOn('posix')) {", StringComparison.Ordinal);
        Assert.True(split > 0, call);
        int otherwise = call.IndexOf("} else {", split, StringComparison.Ordinal);
        // POSIX form: the builtin eval (transpiled at run time); otherwise the function is called.
        Assert.DoesNotContain("-CommandType Function", call[split..otherwise]);
        Assert.Contains("-CommandType Function", call[otherwise..]);
    }
}
