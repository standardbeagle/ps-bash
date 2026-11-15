using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// Unit tests for <see cref="PsBuild"/>, the PowerShell source builder. These probe the
/// escaping/quoting failure axes (embedded quote / backtick / <c>$</c>) and the structural
/// invariants of the exit-code / void / splat primitives that the emitter depends on.
/// </summary>
public class PsBuildTests
{
    // ─────────────── RuntimeWindowsPath ───────────────

    [Fact]
    public void RuntimeWindowsPath_PassesWordAsScriptBlockArgument()
        // The word stays in ARGUMENT mode after the script block: spliced into the method call
        // (`MapPath($env:d/e)`) the `/` would divide. The body is the shared runtime path policy.
        => Assert.Equal(
            "$(& { [PsBash.Cmdlets.BashRuntime]::MapPath([string]($args[0])) } $env:d/e)",
            PsBuild.RuntimeWindowsPath("$env:d/e"));

    [Fact]
    public void RuntimeMapPath_WrapsAValueExpressionInThePolicy()
        => Assert.Equal(
            "[PsBash.Cmdlets.BashRuntime]::MapPath([string]($x))",
            PsBuild.RuntimeMapPath("$x"));

    // ─────────────── RedirectFailureChecked ───────────────

    [Fact]
    public void RedirectFailureChecked_ResetsTheFlagFirstAndRunsTheCheckOnlyOnAFailedOpen()
        => Assert.Equal(
            "$global:__BashRedirectFailed = $false; & { x } | Invoke-BashRedirect -Path f; "
            + "if ($global:__BashRedirectFailed) { $global:__BashRedirectFailed = $false; $global:LASTEXITCODE = 1; CHECK }",
            PsBuild.RedirectFailureChecked("& { x } | Invoke-BashRedirect -Path f", "CHECK"));

    // ─────────────── InheritErrexitCapture / PosixElse / SpecialBuiltinRedefined ───────────────

    // `shopt -s inherit_errexit`: a substitution keeps set -e only while the option is on; otherwise it runs suppressed,
    // and the counter is put back however the body ends.
    [Fact]
    public void InheritErrexitCapture_SuppressesErrexitUnlessTheOptionIsOn() =>
        Assert.Equal(
            "$__psbash_ie = [PsBash.Cmdlets.InvokeBashShoptCommand]::ShoptOn('inherit_errexit'); if (-not $__psbash_ie) { "
            + PsBuild.ErrexitSuppressVar + "++ }; try { BODY } finally { if (-not $__psbash_ie) { " + PsBuild.ErrexitSuppressVar + "-- } }",
            PsBuild.InheritErrexitCapture("BODY"));

    [Fact]
    public void PosixElse_TestsTheRunTimeOption() =>
        Assert.Equal("if ([PsBash.Cmdlets.InvokeBashShoptCommand]::SetOptionOn('posix')) { P } else { N }", PsBuild.PosixElse("P", "N"));

    // Oracle: `set -o posix; eval() { :; }` prints "`eval': is a special builtin" and the shell ends with status 2.
    [Fact]
    public void SpecialBuiltinRedefined_ReportsAndEndsWithStatusTwo() =>
        Assert.Equal("Write-Error -Message 'bash: `eval'': is a special builtin' -ErrorAction Continue; $global:LASTEXITCODE = 2; exit 2",
            PsBuild.SpecialBuiltinRedefined("eval"));

    // ─────────────── ParenSafeLiteral / UnlessExpansionFailed ───────────────

    [Theory]
    [InlineData("'a)b'", "('a' + [char]41 + 'b')")]
    [InlineData("'(x'", "('' + [char]40 + 'x')")]
    [InlineData("'plain'", "'plain'")]          // no paren: unchanged
    [InlineData("$x", "$x")]                    // not a literal: unchanged
    [InlineData("'it''s (x'", "('it''s ' + [char]40 + 'x')")]
    public void ParenSafeLiteral_HidesParensFromTheQuoteBlindSubexpressionScan(string expr, string expected)
        => Assert.Equal(expected, PsBuild.ParenSafeLiteral(expr));

    [Fact]
    public void UnlessExpansionFailed_ConsumesTheMarkOrRunsTheStatements()
        => Assert.Equal(
            "if ($global:__BashExpansionFailed) { $global:__BashExpansionFailed = $false } else { S }",
            PsBuild.UnlessExpansionFailed("S"));
    // ─────────────── SingleQuote ───────────────

    [Fact]
    public void SingleQuote_WrapsInSingleQuotes()
        => Assert.Equal("'hello'", PsBuild.SingleQuote("hello"));

    [Fact]
    public void SingleQuote_DoublesEmbeddedSingleQuote()
        => Assert.Equal("'it''s'", PsBuild.SingleQuote("it's"));

    [Fact]
    public void SingleQuote_LeavesDollarAndBacktickLiteral()
        => Assert.Equal("'$x `n'", PsBuild.SingleQuote("$x `n"));

    [Fact]
    public void SingleQuote_Empty()
        => Assert.Equal("''", PsBuild.SingleQuote(""));

    // ─────────────── EscapeForDoubleQuote / DoubleQuote ───────────────

    [Fact]
    public void EscapeForDoubleQuote_EscapesDollar()
        => Assert.Equal("`$x", PsBuild.EscapeForDoubleQuote("$x"));

    [Fact]
    public void EscapeForDoubleQuote_EscapesDoubleQuote()
        => Assert.Equal("a`\"b", PsBuild.EscapeForDoubleQuote("a\"b"));

    [Fact]
    public void EscapeForDoubleQuote_EscapesBacktickFirst()
    {
        // Backtick must be escaped before $ and " so the escapes those introduce are not
        // themselves re-escaped. A literal backtick becomes a doubled backtick.
        Assert.Equal("a``b", PsBuild.EscapeForDoubleQuote("a`b"));
    }

    [Fact]
    public void EscapeForDoubleQuote_AllThreeTogether_OrderIsStable()
    {
        // Input: backtick, dollar, quote. Expected: `` `$ `" — each escaped exactly once.
        Assert.Equal("``" + "`$" + "`\"", PsBuild.EscapeForDoubleQuote("`$\""));
    }

    [Fact]
    public void DoubleQuote_WrapsAndEscapes()
        => Assert.Equal("\"a`$b\"", PsBuild.DoubleQuote("a$b"));

    // ─────────────── Void / Subshell / Subexpr / VoidStatement ───────────────

    [Fact]
    public void Void_WrapsInVoidCast()
        => Assert.Equal("[void](cmd a b)", PsBuild.Void("cmd a b"));

    [Fact]
    public void Subshell_WrapsInScriptblockInvocation()
        => Assert.Equal("& { body }", PsBuild.Subshell("body"));

    [Fact]
    public void Subexpr_WrapsInSubexpression()
        => Assert.Equal("$(expr)", PsBuild.Subexpr("expr"));

    [Fact]
    public void ArgValue_EvaluatesTheArgumentInCommandMode()
        => Assert.Equal("& { $args[0] } x=$(y)", PsBuild.ArgValue("x=$(y)"));

    [Fact]
    public void NullSafeElement_IndexesOnlyASetArray()
        => Assert.Equal("$(if ($null -ne $a) { $a['1'] })", PsBuild.NullSafeElement("$a", "'1'"));

    [Fact]
    public void SeedArrayFromScalar_MakesTheScalarElementZero()
        => Assert.Equal("if ($null -eq $c -and $null -ne $env:c) { $c = @($env:c) }", PsBuild.SeedArrayFromScalar("$c", "$env:c"));

    [Fact]
    public void WriteElementZeroOrScalar_WritesElementZeroOfAnArray()
    {
        Assert.Equal("if ($a -is [array] -or $a -is [System.Collections.IDictionary]) { $a = Set-BashElement $a '0' (\"z\") } else { $env:a = \"z\" }",
            PsBuild.WriteElementZeroOrScalar("$a", "\"z\"", append: false, "$env:a = \"z\""));
        Assert.Contains("'0' (\"z\") -Append", PsBuild.WriteElementZeroOrScalar("$a", "\"z\"", append: true, "$env:a += \"z\""));
    }
    [Fact]
    public void VoidStatement_SingleStatement_UsesGrouping()
        => Assert.Equal("[void]($env:x = 1)", PsBuild.VoidStatement("$env:x = 1"));

    [Fact]
    public void VoidStatement_StatementList_UsesSubexpression()
    {
        // (...) cannot hold a statement list; only $(...) can.
        Assert.Equal("[void]$($env:x = 1; $env:y = 2)", PsBuild.VoidStatement("$env:x = 1; $env:y = 2"));
    }

    // ─────────────── Hoisted conditions / status ───────────────
    // A condition that runs a command executes as a STATEMENT (its output streams, as bash prints
    // `if echo in; then …`'s `in`), then the branch tests the status it left.

    [Fact]
    public void HoistedCondition_IsTheStatementsThenSeparator()
        => Assert.Equal("echo in; ", PsBuild.HoistedCondition("echo in"));

    [Theory]
    [InlineData(false, "$global:LASTEXITCODE -eq 0")]
    [InlineData(true, "$global:LASTEXITCODE -ne 0")]
    public void LastStatusTest_TestsTheExitCodeLeftBehind(bool negate, string expected)
        => Assert.Equal(expected, PsBuild.LastStatusTest(negate));

    [Fact]
    public void SetStatus_SetsLastExitCode()
        => Assert.Equal("$global:LASTEXITCODE = 0", PsBuild.SetStatus(0));

    [Fact]
    public void SignalFailIfFailed_ChecksBothDollarQuestionAndExitCode()
    {
        // A cmdlet error clears $?; a function's `return 3` sets only the exit code.
        Assert.Equal("$(if (-not $? -or $global:LASTEXITCODE -ne 0) { Write-Error '' -ErrorAction SilentlyContinue })",
            PsBuild.SignalFailIfFailed());
    }

    // ─────────────── ExitCodeTest ───────────────

    [Fact]
    public void ExitCodeTest_Success_VoidsCommandAndTestsEqZero()
    {
        // The [void] is the load-bearing part: it stops the command's output from joining
        // the boolean into a (truthy) array.
        Assert.Equal("(& { [void](grep -q x); $global:LASTEXITCODE -eq 0 })",
            PsBuild.ExitCodeTest("grep -q x"));
    }

    [Fact]
    public void ExitCodeTest_Negated_TestsNeZero()
        => Assert.Equal("(& { [void](cmd); $global:LASTEXITCODE -ne 0 })",
            PsBuild.ExitCodeTest("cmd", negate: true));

    [Fact]
    public void ExitCodeTest_AlwaysVoidsRegardlessOfNegation()
    {
        Assert.Contains("[void](", PsBuild.ExitCodeTest("cmd"));
        Assert.Contains("[void](", PsBuild.ExitCodeTest("cmd", negate: true));
    }

    // ─────────────── Chain exit-code propagation ───────────────

    [Fact]
    public void SetExitFromBool_SetsExitAndSignalsFailure()
        => Assert.Equal(
            "$(if ((cond)) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1; Write-Error '' -ErrorAction SilentlyContinue })",
            PsBuild.SetExitFromBool("(cond)"));

    [Fact]
    public void SignalFailIfNonZero_OnlySignalsDoesNotSetExit()
        => Assert.Equal(
            "$(if ($global:LASTEXITCODE -ne 0) { Write-Error '' -ErrorAction SilentlyContinue })",
            PsBuild.SignalFailIfNonZero());

    [Fact]
    public void SilentExitFromBool_SetsExitWithoutWriteError()
    {
        // A standalone test does not signal $? — so no Write-Error, unlike SetExitFromBool.
        var result = PsBuild.SilentExitFromBool("(cond)");
        Assert.Equal("$(if ((cond)) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1 })", result);
        Assert.DoesNotContain("Write-Error", result);
    }

    // ─────────────── WordSplitArray ───────────────

    [Fact]
    public void WordSplitArray_WrapsInOuterArraySubexpression()
    {
        // The OUTER @(...) is required so the empty branch stays an empty array (not $null,
        // which would splat one spurious empty argument).
        var result = PsBuild.WordSplitArray("$env:x");
        // ConvertTo-BashWords splits on $IFS (leading/trailing IFS whitespace discarded, blank = no
        // word) and pathname-expands each word, matching bash word splitting + globbing.
        Assert.Equal("@(ConvertTo-BashWords $env:x)", result);
    }

    [Fact]
    public void QuotedAffixArray_GluesTextToTheElementsWithoutSplitting()
        => Assert.Equal("@([PsBash.Cmdlets.BashQuotedWords]::Affix(\"-\", '', @($a)))", PsBuild.QuotedAffixArray("\"-\"", "''", "@($a)"));

    // ─────────────── NullSafeBashText ───────────────

    [Fact]
    public void NullSafeBashText_GuardsNullBeforePropertyProbe()
    {
        // The $null -ne $_ guard must precede the property access (short-circuit), else
        // $_.PSObject.Properties[...] throws "Cannot index into a null array" on a $null item.
        Assert.Equal(
            "if ($null -ne $_ -and $_.PSObject.Properties['BashText']) { $_.BashText } else { \"$_\" }",
            PsBuild.NullSafeBashText);
        int guard = PsBuild.NullSafeBashText.IndexOf("$null -ne $_", System.StringComparison.Ordinal);
        int probe = PsBuild.NullSafeBashText.IndexOf("PSObject.Properties", System.StringComparison.Ordinal);
        Assert.True(guard >= 0 && probe >= 0 && guard < probe, "null guard must precede the property probe");
    }

    // ─────────────── IsStatementList ───────────────
    //
    // Decides whether emitted text can HEAD a pipeline. A false NEGATIVE emits
    // "An empty pipe element is not allowed" and breaks the whole file's parse;
    // a false POSITIVE only costs a redundant `& { }` wrap. The scan must be
    // quote- and nesting-aware — a quote-blind scan is a recurring bug family
    // in this transpiler.

    [Theory]
    [InlineData("Invoke-BashCat f")]
    [InlineData("Invoke-BashGrep a b | Invoke-BashHead -1")]
    [InlineData("cmd")]
    [InlineData("")]
    public void IsStatementList_SinglePipeline_False(string text)
        => Assert.False(PsBuild.IsStatementList(text));

    [Theory]
    [InlineData("$a = 1; cmd")]
    [InlineData("cmd1; cmd2")]
    [InlineData("$__saved = $env:X; try { cmd } finally { $env:X = $__saved; }")]
    public void IsStatementList_StatementList_True(string text)
        => Assert.True(PsBuild.IsStatementList(text));

    [Theory]
    // ';' inside a single-quoted operand is NOT a statement separator.
    [InlineData("Invoke-BashEcho 'a;b'")]
    // ...nor inside a double-quoted operand...
    [InlineData("Invoke-BashEcho \"a;b\"")]
    // ...nor inside a nested $( … ) within a double-quoted string...
    [InlineData("Invoke-BashEcho \"$(cmd1; cmd2)\"")]
    // ...nor inside an already-wrapped child scope or any bracket nesting.
    [InlineData("& { a; b }")]
    [InlineData("Foo -Bar @('a;b','c')")]
    [InlineData("$x[0;1]")]
    public void IsStatementList_SemicolonIsNested_False(string text)
        => Assert.False(PsBuild.IsStatementList(text),
            "quote-/nesting-blind scan misfired on: " + text);

    [Fact]
    public void IsStatementList_TopLevelSemicolonAfterQuotedSpan_True()
    {
        // The scanner must RESUME depth tracking after a quoted span closes —
        // not treat everything past the first quote as quoted.
        Assert.True(PsBuild.IsStatementList("Invoke-BashEcho 'a;b'; cmd2"));
    }

    [Fact]
    public void IsStatementList_EscapedQuoteInsideDoubleQuotes_DoesNotEndTheString()
    {
        // `" is an escaped quote, so the ';' after it is still inside the string.
        Assert.False(PsBuild.IsStatementList("Invoke-BashEcho \"a`\"b;c\""));
    }

    [Fact]
    public void RedirectStage_StdoutOnly_IsThePathForm()
        => Assert.Equal(" | Invoke-BashRedirect -Path f -Append",
            PsBuild.RedirectStage("f", true, null, false, false, [], []));

    [Fact]
    public void RedirectStage_AllParts_InFixedOrder()
        => Assert.Equal(
            " | Invoke-BashRedirect -Path o -ErrorPath e -ErrorAppend -PassErrors"
            + " -Truncate @(& { $args } a b) -Touch @(& { $args } s)",
            PsBuild.RedirectStage("o", false, "e", true, true, ["a", "b"], ["s"]));

    [Fact]
    public void ArgWordArray_KeepsArgumentModeWordsAsArguments()
    {
        // `$env:d/a` must stay an ARGUMENT: spliced into an expression the `/` would divide.
        // Run-time evaluation is covered by Differential_Redirect_SameFdTwice_Files_LastWins.
        Assert.Equal("@(& { $args } $env:d/a 'b c')", PsBuild.ArgWordArray(["$env:d/a", "'b c'"]));
    }

    // ─────────────── Timed (`time` keyword) ───────────────

    [Fact]
    public void Timed_RunsThePipelineAsStatementsThenReportsInFinally()
    {
        var ps = PsBuild.Timed("x; y", posix: true, "$__psbash_time0");
        // Statement scope (no script block), so `time x=1` / `time { x=1; }` keep the assignment.
        Assert.StartsWith("$__psbash_time0 = [PsBash.Cmdlets.BashTimeKeyword]::Start(); try { x; y } finally { ", ps);
        Assert.Contains("::Report($__psbash_time0, $true)", ps);
        // The report goes to the redirectable error stream through the writer that leaves $? alone.
        Assert.Contains("Write-BashHostStderr -Message", ps);
        Assert.DoesNotContain("[Console]", ps);
    }

    [Fact]
    public void RuntimeReadonlyGuard_RefusesWhenANameIsReadonlyElseAssigns()
    {
        string ps = PsBuild.RuntimeReadonlyGuard(["m", "it's"], "$env:m = '1'");
        Assert.StartsWith("if ($__psbRo = [PsBash.Cmdlets.BashShellState]::FirstReadonly('m', 'it''s')) { ", ps);
        Assert.Contains("Write-Error ('ps-bash: ' + $__psbRo + ': readonly variable') -ErrorAction Continue; $global:LASTEXITCODE = 1", ps);
        Assert.EndsWith(" } else { $env:m = '1' }", ps);
    }

    [Fact]
    public void ArrayAppendBase_FallsBackToTheScalarInTheEnvironment()
        => Assert.Equal("($global:s ?? ${env:s})", PsBuild.ArrayAppendBase("$global:s", "s"));

    [Fact]
    public void AssocOrIndexed_RunsOnlyTheBranchForTheRunTimeKind()
    {
        string ps = PsBuild.AssocOrIndexed("$a", "ASSOC", "INDEXED");
        Assert.Equal("$(if ($a -is [PsBash.Cmdlets.BashAssoc]) { ASSOC } else { ,(INDEXED) })", ps);
    }

    // `declare -A a` on an indexed a (and `declare -a` on an associative one) is refused at run time.
    [Fact]
    public void ArrayKindGuard_RefusesTheOtherKindElseDeclares()
    {
        Assert.Equal("if ([PsBash.Cmdlets.BashAssoc]::IsIndexedValue((Get-Variable -Name 'a' -ValueOnly -ErrorAction Ignore))) { Write-Error 'ps-bash: a: cannot convert indexed to associative array' -ErrorAction Continue; $global:LASTEXITCODE = 1 } else { DECL }",
            PsBuild.ArrayKindGuard("a", toAssoc: true, "DECL"));
        Assert.Contains("IsAssocValue", PsBuild.ArrayKindGuard("a", toAssoc: false, "DECL"));
        Assert.Contains("cannot convert associative to indexed array", PsBuild.ArrayKindGuard("a", toAssoc: false, "DECL"));
    }

    // `a['2']=3`: the subscript is only a key; on anything but an associative array it is an arithmetic syntax error.
    [Fact]
    public void KeyOnlySubscriptGuard_AssignsOnAssocElseRefusesWithStatus1()
    {
        Assert.Equal("if ([PsBash.Cmdlets.BashAssoc]::IsAssocValue((Get-Variable -Name 'a' -ValueOnly -ErrorAction Ignore))) { SET; $global:LASTEXITCODE = 0 } else { Write-Error 'ps-bash: a[''2'']: arithmetic syntax error in array subscript' -ErrorAction Continue; $global:LASTEXITCODE = 1 }",
            PsBuild.KeyOnlySubscriptGuard("a", "a['2']", "SET"));
    }

    [Fact]
    public void RunTimeTest_RestoresTheStatusAndFlagsABadExpression()
    {
        // A test used as a condition leaves $? alone (`while [ $n -lt 4 ]` ended with 1): the runtime test's status
        // is read, then LASTEXITCODE is put back.
        Assert.Equal("$(& { $__psbT = @(A; B); $__psbS = $global:LASTEXITCODE; Invoke-BashTest @__psbT; "
            + "$__psbR = $global:LASTEXITCODE; $global:LASTEXITCODE = $__psbS; "
            + "if ($__psbR -eq 2) { $global:__BashTestBad = $true }; $__psbR -eq 0 })", PsBuild.RunTimeTest("A; B"));
    }

    [Fact]
    public void EvalReturnCheck_SpendsTheFlagOnlyInAFunction()
    {
        // In a function the eval site is where the eval text's return ends; in eval text it passes on outward.
        Assert.Equal("if ($global:__BashEvalReturn) { $global:__BashEvalReturn = $false; return }", PsBuild.EvalReturnCheck(inFunction: true));
        Assert.Equal("if ($global:__BashEvalReturn) { return }", PsBuild.EvalReturnCheck(inFunction: false));
        Assert.Equal("$global:__BashEvalReturn = $true; ", PsBuild.EvalReturnSignal);
    }

    [Fact]
    public void UnlessGlobFailed_RunsTheStatementOnlyWithoutAFailedGlob()
        => Assert.Equal("if (-not $global:__BashFailGlob) { echo x }", PsBuild.UnlessGlobFailed("echo x"));

    [Fact]
    public void ChainOperandUnlessGlobFailed_IsAPipelineThatRunsOnlyWithoutAFailure()
        => Assert.Equal("$(if (-not $global:__BashFailGlob) { echo alt })", PsBuild.ChainOperandUnlessGlobFailed("echo alt"));

    [Fact]
    public void EndOfEvalText_ClearsTheLineMarkOnlyForTextThatCanSetIt()
    {
        // eval 'echo *.ZZ'; echo $?: the failure abandons the eval's own line; the line that ran the eval goes on.
        Assert.Equal("B\n$global:__BashFailGlob = $false", PsBuild.EndOfEvalText("B"));
        Assert.True(PsBuild.CanAbandonLine("@(ConvertTo-BashGlob '*.ZZ')"));
        Assert.True(PsBuild.CanAbandonLine(PsBuild.ExpansionError("'x: '", "'bad substitution'")));
        Assert.False(PsBuild.CanAbandonLine("Invoke-BashEcho x"));
    }
    [Fact]
    public void ErrexitSuppressedIfOn_SuppressesOnlyWhenErrexitIsOnAsItStarts()
    {
        // `f() { set -e; false; }; ! f` exits: bash ignores errexit under `!` only when -e was on before the pipeline.
        Assert.Equal("$m = [bool]$global:__BashErrexit; if ($m) { $global:__BashErrexitSuppress++ }; try { f } finally { if ($m) { $global:__BashErrexitSuppress-- } }",
            PsBuild.ErrexitSuppressedIfOn("f", "$m"));
    }

    [Fact]
    public void SetCallLine_RecordsTheStatementLineForBashLineno()
        => Assert.Equal("[PsBash.Cmdlets.VarFrameStack]::SetLine(7); ", PsBuild.SetCallLine(7));

    [Fact]
    public void ErrTrapCheck_DoesNotRunTheActionInsideItself()
    {
        string check = PsBuild.ErrTrapCheck();
        Assert.Contains("-and -not $global:__BashInErr", check);
        Assert.Contains("$global:__BashInErr = $true; try { & $global:__BashTrapERR } finally { $global:__BashInErr = $false }", check);
    }

    [Fact]
    public void TrapRuns_InsideTrapActionText_KeepTheFiringCommandsLine()
    {
        int priorLine = PsBuild.TrapLine;
        PsBuild.TrapLine = 9;
        try
        {
            Assert.Contains("$env:__psbLine = 9;", PsBuild.ErrTrapCheck());
            Assert.Contains("$env:__psbLine = 9;", PsBuild.DebugTrapRun(9));
            PsBuild.InTrapAction = true;
            Assert.DoesNotContain("__psbLine", PsBuild.ErrTrapCheck());
            Assert.DoesNotContain("__psbLine", PsBuild.DebugTrapRun(9));
        }
        finally { PsBuild.InTrapAction = false; PsBuild.TrapLine = priorLine; }
    }
}
