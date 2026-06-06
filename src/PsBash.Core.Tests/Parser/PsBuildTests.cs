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
        Assert.StartsWith("@(if ", result);
        Assert.Equal("@(if ([string]::IsNullOrEmpty($env:x)) { @() } else { @($env:x -split '\\s+') })", result);
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
}
