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
        // (`Normalize($env:d/e)`) the `/` would divide.
        => Assert.Equal(
            "$(& { [PsBash.Core.WindowsPath]::Normalize([string]$args[0]) } $env:d/e)",
            PsBuild.RuntimeWindowsPath("$env:d/e"));

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
}
