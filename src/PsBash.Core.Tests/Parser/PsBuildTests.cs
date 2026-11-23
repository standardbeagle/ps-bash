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

    // Oracle: `set -e; : "$(cat < /nonexistent 2>/dev/null || true)" < f; echo ok` prints ok. The flag is the guard's own
    // result: once the command ran, a failure in its words that was handled is lowered; a failed open raises it; with an
    // output file of the command's own, Invoke-BashRedirect reports on it after the command, so the guard leaves it be.
    [Fact]
    public void SimpleInputRedirectGuard_LowersANestedFailureOnceTheCommandRan()
    {
        string guard = PsBuild.SimpleInputRedirectGuard("f", "CMD");
        Assert.Contains("{ CMD; $global:__BashRedirectFailed = $false } else { ", guard);
        Assert.EndsWith("$global:__BashRedirectFailed = $true } }", guard);
        Assert.DoesNotContain("$false", PsBuild.SimpleInputRedirectGuard("f", "CMD | Invoke-BashRedirect -Path o", clearsNested: false));
        Assert.Contains("{ LOOP; $global:__BashRedirectFailed = $false } else { ", PsBuild.InputRedirectGuardInline("f", "LOOP"));
    }

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

    // Oracle: `set -o posix; eval() { :; }` prints "t.sh: line 1: `eval': is a special builtin" (the source prefix); the
    // emitter then ends the shell with 2 (PsEmitter.EndShell: a subshell ends only itself).
    [Fact]
    public void SpecialBuiltinRedefined_ReportsWithThePrefix() =>
        Assert.Equal(PsBuild.ShellDiagnosticLine("'`eval'': is a special builtin'"), PsBuild.SpecialBuiltinRedefined("eval"));

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

    // The separator a SIMPLE command's statement-list emission (env prefix, cd, eval) carries. Quoted text is an operand.
    [Theory]
    [InlineData("Invoke-BashBash '-c' 'cat; exit 33'")]
    [InlineData("Invoke-BashEcho \"a; b\"")]
    [InlineData("Invoke-BashEcho \"$(a; b)\"")]
    [InlineData("Invoke-BashEcho 'it''s; x'")]
    [InlineData("Invoke-BashEcho a`; b")]
    [InlineData("Invoke-BashCat f")]
    public void HasUnquotedStatementSeparator_OnlyQuotedOrEscaped_False(string text)
        => Assert.False(PsBuild.HasUnquotedStatementSeparator(text));

    [Theory]
    [InlineData("$env:X = \"1\"; Invoke-BashCat")]
    [InlineData("Invoke-BashEcho 'a; b'; Invoke-BashCat")]
    [InlineData("if ($x) { a; b }")]
    public void HasUnquotedStatementSeparator_SeparatorOutsideQuotes_True(string text)
        => Assert.True(PsBuild.HasUnquotedStatementSeparator(text));

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
        Assert.Contains(PsBuild.ShellDiagnosticLine("$__psbRo + ': readonly variable'") + "; $global:LASTEXITCODE = 1; " + PsBuild.AssignmentDiscardMark, ps);
        Assert.EndsWith(" } else { $env:m = '1' }", ps);
    }

    [Fact]
    public void ArrayAppendBase_FallsBackToTheScalarInTheEnvironment()
        => Assert.Equal("($global:s ?? ${env:s})", PsBuild.ArrayAppendBase("$global:s", "${env:s}"));

    // Oracle: `a=(1); declare -n r; v='a[1/0]'; r=$v; r=x; echo no` reports "1/0: division by 0", drops the line and ends
    // `bash -c` (a subscript expansion error); a value that is no name (`r=5`) is an assignment error, which `-c` survives.
    [Fact]
    public void AssignThroughNameref_ASubscriptErrorEndsADashCShell_AnAssignmentErrorDropsTheLine()
    {
        string ps = PsBuild.AssignThroughNameref("r", "$v", append: false);
        Assert.Contains("if ([PsBash.Cmdlets.BashArrayElement]::TakeSubscriptError()) { " + PsBuild.UsageDiscardMark + " }; ", ps);
        Assert.EndsWith(PsBuild.AssignmentDiscardMark + " }", ps);
        Assert.True(PsBuild.CanDiscardLine(ps));
    }

    // Oracle: `declare -n r; r='a[1/0]'; : ${r:=x}; echo no` reports "1/0: division by 0" and ends `bash -c`, as the
    // assignment form does. REGRESSION: the default form never took the subscript-error mark, so it outlived the
    // expansion and a later `q=1bad` (an assignment error, which `-c` survives) ended the shell.
    [Fact]
    public void AssignDefaultThroughNameref_TakesTheSubscriptError()
    {
        string ps = PsBuild.AssignDefaultThroughNameref("r", "'x'");
        Assert.Contains("if ([PsBash.Cmdlets.BashArrayElement]::TakeSubscriptError()) { " + PsBuild.SetStatus(1) + "; " + PsBuild.UsageDiscardMark + " }", ps);
        Assert.EndsWith("$__psbV)", ps);
        Assert.True(PsBuild.CanDiscardLine(ps));
    }

    // The scalar is an expression the emitter picks (PsEmitter.ScalarValueOf): a run-time nameref's is its target's.
    [Fact]
    public void ArrayAppendBase_OfARunTimeNameref_FallsBackToTheTargetsScalar()
        => Assert.Equal("($r ?? " + PsBuild.ReadThroughNameref("r") + ")", PsBuild.ArrayAppendBase("$r", PsBuild.ReadThroughNameref("r")));

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
        // Oracle: "t.sh: line 2: declare: a: cannot convert indexed to associative array" (the builtin as invoked, after
        // the source prefix). REGRESSION: it said "ps-bash: a: ...".
        Assert.Equal("if ([PsBash.Cmdlets.BashAssoc]::IsIndexedValue((Get-Variable -Name 'a' -ValueOnly -ErrorAction Ignore))) { "
            + PsBuild.ShellDiagnosticLine("'declare: a: cannot convert indexed to associative array'") + "; $global:LASTEXITCODE = 1 } else { DECL }",
            PsBuild.ArrayKindGuard("declare", "a", toAssoc: true, "DECL"));
        Assert.Contains("IsAssocValue", PsBuild.ArrayKindGuard("typeset", "a", toAssoc: false, "DECL"));
        Assert.Contains("typeset: a: cannot convert associative to indexed array", PsBuild.ArrayKindGuard("typeset", "a", toAssoc: false, "DECL"));
    }

    // `a['2']=3`: the subscript is only a key; on anything but an associative array it is an arithmetic syntax error.
    [Fact]
    public void KeyOnlySubscriptGuard_AssignsOnAssocElseRefusesWithStatus1()
    {
        Assert.Equal("if ([PsBash.Cmdlets.BashAssoc]::IsAssocValue((Get-Variable -Name 'a' -ValueOnly -ErrorAction Ignore))) { SET; $global:LASTEXITCODE = 0 } else { Write-Error -Message ([PsBash.Cmdlets.ShellDiagnostic]::Prefix($global:BASH_SOURCE, $global:BashPositional0) + 'a[''2'']: arithmetic syntax error in array subscript') -ErrorAction Continue; $global:LASTEXITCODE = 1 }",
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

    // A loop's binding writes the shell variable only, quoting the name and stringifying the value.
    [Fact]
    public void SetShellScalar_WritesTheShellVariableAsAString()
        => Assert.Equal("[PsBash.Cmdlets.BashVariableStore]::Set('it''s', [string]($__psbash_lv0))",
            PsBuild.SetShellScalar("it's", "$__psbash_lv0"));

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

    // ─────────────── Builtin usage errors / exit status / here-string ───────────────

    [Fact]
    public void UsageDiscard_ReportsWithThePrefix_SetsOne_AndMarksTheLine()
    {
        // bash no_args (`shift 1 2`, `exit 1 2`): the message with the shell prefix, status 1, the rest of the line
        // dropped (a script) or the shell ended (`bash -c`). The statement carrying it gets a line scope.
        var ps = PsBuild.UsageDiscard("exit: too many arguments");
        Assert.StartsWith(PsBuild.ShellDiagnosticLine("'exit: too many arguments'"), ps);
        Assert.Contains(PsBuild.SetStatus(1), ps);
        Assert.EndsWith(PsBuild.UsageDiscardMark, ps);
        Assert.Contains("UsageEnd()", PsBuild.UsageDiscardMark);
        Assert.True(PsBuild.CanDiscardLine(ps));
    }

    [Fact]
    public void AssignmentError_ReportsSetsOne_AndDropsTheLineButNeverEndsADashCShell()
    {
        // `r=2` with r read-only: the message with the prefix, status 1, the line dropped (a function's caller's line too).
        // Unlike a usage error, `bash -c` goes on with its next line: no UsageEnd.
        var ps = PsBuild.AssignmentError("r: readonly variable");
        Assert.StartsWith(PsBuild.ShellDiagnosticLine("'r: readonly variable'"), ps);
        Assert.Contains(PsBuild.SetStatus(1), ps);
        Assert.EndsWith(PsBuild.AssignmentDiscardMark, ps);
        Assert.DoesNotContain("UsageEnd()", PsBuild.AssignmentDiscardMark);
        Assert.DoesNotContain("PSBASH_DASH_C", PsBuild.AssignmentDiscardMark);
        // Under set -e, or in a non-interactive posix shell, the shell ends with 1 first (bash FORCE_EOF / errexit).
        Assert.StartsWith("if ($global:__BashErrexit -or (" + PsBuild.PosixOn + " -and -not [PsBash.Cmdlets.InvokeBashShoptCommand]::InteractiveShell)) "
            + "{ throw [PsBash.Cmdlets.BashParamErrorException]::AssignmentEnd() }; ", PsBuild.AssignmentDiscardMark);
        Assert.True(PsBuild.CanDiscardLine(ps));
        Assert.True(PsBuild.MayReportDiagnostic(ps));
    }

    [Fact]
    public void RuntimeReadonlyGuard_RefusalDropsTheLine()
        => Assert.True(PsBuild.CanDiscardLine(PsBuild.RuntimeReadonlyGuard(["y"], "$env:y = '2'")));

    // ─────────────── Function-local arrays (DeclareLocalArray, ArraySlot) ───────────────

    [Theory]
    [InlineData("local", true, "IsIndexedValue(", "local: m: cannot convert indexed to associative array")]
    [InlineData("declare", false, "IsAssocValue(", "declare: m: cannot convert associative to indexed array")]
    public void DeclareLocalArray_RefusesTheOtherKindWithTheBuiltinsName(string builtin, bool assoc, string otherKind, string message)
    {
        var ps = PsBuild.DeclareLocalArray(builtin, "m", assoc);
        Assert.StartsWith(PsBuild.DeclareLocalArrayMark + "'m' -Scope 0", ps);
        Assert.Contains("[PsBash.Cmdlets.BashAssoc]::" + otherKind, ps);
        Assert.Contains(message, ps);
        Assert.Contains(PsBuild.SetStatus(1), ps);
        Assert.Contains("[PsBash.Cmdlets.VarFrameStack]::LocalAttributes('m')", ps);
        Assert.EndsWith(PsBuild.SetStatus(0) + " }", ps);
    }

    [Fact]
    public void ArraySlot_IsRecognisedAndParenthesisedAsAnArgument()
    {
        // `Set-BashElement [T]::ArraySlot(...).Value` in argument mode is a string, not a call: TargetArg wraps it.
        string slot = PsBuild.ArraySlot("a");
        Assert.Equal("[PsBash.Cmdlets.VarFrameStack]::ArraySlot($ExecutionContext.SessionState, 'a').Value", slot);
        Assert.True(PsBuild.IsArraySlot(slot));
        Assert.Equal("(" + slot + ")", PsBuild.TargetArg(slot));
        Assert.False(PsBuild.IsArraySlot("$global:a"));
        Assert.Equal("$global:a", PsBuild.TargetArg("$global:a"));
    }

    [Fact]
    public void LocalArrays_ListsTheNames_OrNothing()
    {
        Assert.Equal("[PsBash.Cmdlets.VarFrameStack]::LocalArrays('a', 'b'); ", PsBuild.LocalArrays(["a", "b"]));
        Assert.Equal("", PsBuild.LocalArrays([]));
    }

    [Theory]
    [InlineData("Invoke-BashShift 1 2", true)]
    [InlineData("Invoke-BashShift $n x", true)]
    [InlineData("Invoke-BashShift 1", false)]
    [InlineData("Invoke-BashShift 1; Invoke-BashEcho x", false)]
    [InlineData("Invoke-BashShift | Invoke-BashCat", false)]
    public void CanDiscardLine_ShiftWithAnExtraOperand(string emitted, bool expected)
        => Assert.Equal(expected, PsBuild.CanDiscardLine(emitted));

    [Fact]
    public void StatusByte_NonNumeric_ReportsWithThePrefixAndYieldsTwo()
    {
        // `( exit q )`: "q: numeric argument required" named with the script and line, status 2; never a bare Write-Error.
        var ps = PsBuild.StatusByte("'q'");
        Assert.Contains("[PsBash.Cmdlets.ShellDiagnostic]::Prefix", ps);
        Assert.Contains("': numeric argument required'", ps);
        Assert.Contains("; 2 }", ps);
    }

    [Fact]
    public void HereStringValue_IsTheWordPlusOneNewline()
        => Assert.Equal("([string]($x) + \"`n\")", PsBuild.HereStringValue("$x"));

    // ─────────────── fc / RunBashTextCall ───────────────

    /// <summary>
    /// <c>fc</c> up to its re-run: the stale re-run cleared BEFORE the cmdlet runs, then what it chose moved into the
    /// eval text variable and the slot cleared (behavior vs bash: FcBuiltinTests, Differential FcRerunDifferentialTests).
    /// </summary>
    [Fact]
    public void FcTakeRerun_ClearsRunsThenTakesTheRerun()
    {
        Assert.Equal("$global:__BashFcRerun = $null; Invoke-BashFc '-s'; $__psbash_eval_src = $global:__BashFcRerun; $global:__BashFcRerun = $null; ",
            PsBuild.FcTakeRerun("Invoke-BashFc '-s'"));
        Assert.Equal("__BashFcRerun", PsBuild.FcRerunVariable);
    }

    // A return inside a redirect's `. { }` / `& { }` block leaves only the block; the flag carries it to the caller. It is
    // cleared before the block, raised when the body starts and cleared at its end, so only a return (which skips the end)
    // leaves it raised: a redirect that fails before the body runs leaves it clear (`{ return 6; } >/nonexistent/x` goes
    // on, status 1). It is raised again for an enclosing body's check.
    // REGRESSION: `f() { { return 6; } 2>/dev/null; echo no; }` printed no (bash: status 6).
    [Fact]
    public void GroupPassingReturn_RaisesTheFlagOnlyWhileTheBodyRuns()
    {
        Assert.Equal(PsBuild.GroupReturnVar + " = $true; a; b; " + PsBuild.GroupReturnVar + " = $false", PsBuild.GroupBody("a; b; "));
        Assert.Equal(PsBuild.GroupReturnVar + " = $false; . { x } 2>$null; if (" + PsBuild.GroupReturnVar + ") { return }; "
                + PsBuild.GroupReturnVar + " = $true",
            PsBuild.GroupPassingReturn(". { x } 2>$null"));
        Assert.StartsWith("$global:", PsBuild.GroupReturnVar);   // a `& { }` block (scoped fds) must reach it too
    }

    /// <summary>The one run-time call of bash text: skips blank text, bounds the nesting under the label, restores the
    /// depth, then the return check.</summary>
    [Fact]
    public void RunBashTextCall_BoundsTheDepthAndEndsWithTheReturnCheck()
    {
        var ps = PsBuild.RunBashTextCall("fc", PsBuild.LoopModeSet(0), ", $null", "CHECK");
        Assert.StartsWith("if (-not [string]::IsNullOrWhiteSpace($__psbash_eval_src)) { ", ps);
        Assert.Contains("throw 'fc: nesting depth limit (5) exceeded; possible infinite recursion'", ps);
        Assert.Contains("try { " + PsBuild.LoopModeSet(0) + "$__psbash_eval_pwsh = $null; ", ps);
        Assert.Contains("'fc: '", ps);
        Assert.EndsWith("} finally { $global:__BashEvalDepth = $__psbash_eval_depth } CHECK }", ps);
        Assert.EndsWith("} finally { $global:__BashEvalDepth = $__psbash_eval_depth } }", PsBuild.RunBashTextCall("eval", "", ", $null", ""));
    }

    [Theory]
    [InlineData("fc -s a=b")]
    [InlineData("fc -l 1 2")]
    public void Transpile_Fc_TakesTheRerunThenTheSharedCall(string bash)
    {
        var ps = PsEmitter.Transpile(bash)!;
        Assert.Contains("$global:__BashFcRerun = $null; Invoke-BashFc ", ps);
        Assert.Contains("$__psbash_eval_src = $global:__BashFcRerun; $global:__BashFcRerun = $null; if (-not [string]::IsNullOrWhiteSpace($__psbash_eval_src)) { ", ps);
        Assert.DoesNotContain(". { $global:__BashFcRerun", ps);
    }

    /// <summary>
    /// <c>fc -s &gt; out</c>: the redirect is the whole command's, so the re-run's output goes to out too (bash 5.2: out holds
    /// the re-run's <c>hello</c>; the echoed command line goes to stderr). The take of the re-run and the run-time call are
    /// both inside the one block the redirect applies to (PsEmitter.RedirectGroup), the redirect after the call.
    /// </summary>
    [Fact]
    public void Transpile_FcWithARedirect_TakesAndRunsTheRerunInsideTheRedirectedBlock()
    {
        var ps = PsEmitter.Transpile("fc -s > out")!;
        Assert.StartsWith(". { $global:__BashFcRerun = $null; Invoke-BashFc -s; $__psbash_eval_src = $global:__BashFcRerun; ", ps);
        int run = ps.IndexOf("Invoke-Expression $__psbash_eval_pwsh", StringComparison.Ordinal);
        int redirect = ps.IndexOf("} | Invoke-BashRedirect -Path out", StringComparison.Ordinal);
        Assert.True(run > 0 && redirect > run, ps);
    }
}