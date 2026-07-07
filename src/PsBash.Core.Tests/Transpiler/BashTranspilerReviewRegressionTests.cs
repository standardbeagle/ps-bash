using Xunit;
using PsBash.Core.Parser;
using PsBash.Core.Transpiler;

namespace PsBash.Core.Tests.Transpiler;

/// <summary>
/// Regression tests for the 2026-07 transpiler subsystem review. Each test names the
/// finding it guards. Crash-class findings assert a clean <see cref="ParseException"/>
/// (or no throw), never a raw .NET exception — the fuzz-layer invariant. Semantic
/// findings assert the emitted PowerShell shape.
/// </summary>
public class BashTranspilerReviewRegressionTests
{
    // ── Finding: heredoc command-substitution injection ────────────────────
    // An expanding heredoc's $(...) / `...` were passed RAW into the interpolating
    // @"..."@ here-string, so PowerShell executed them as PowerShell (Get-Date for
    // $(date)) and backticks were swallowed as PS escapes. They must be transpiled.

    [Fact]
    public void Transpile_HereDocDollarParen_TranspilesCommandSubNotRawPowerShell()
    {
        var ps = BashTranspiler.Transpile("cat <<EOF\n$(echo hi)\nEOF");
        Assert.Contains("Invoke-BashEcho", ps); // transpiled bash, not raw execution
    }

    [Fact]
    public void Transpile_HereDocBacktick_TranspilesCommandSub()
    {
        var ps = BashTranspiler.Transpile("cat <<EOF\n`echo hi`\nEOF");
        Assert.Contains("Invoke-BashEcho", ps);
    }

    [Fact]
    public void Transpile_HereDocEscapedDollarParen_IsLiteralNotExecuted()
    {
        // bash: \$(...) in an expanding heredoc is a literal $(...), no expansion.
        var ps = BashTranspiler.Transpile("cat <<EOF\n\\$(echo hi)\nEOF");
        Assert.DoesNotContain("Invoke-BashEcho", ps);
        Assert.Contains("`$", ps); // literal dollar, backtick-escaped for @"..."@
    }

    // ── Finding: brace-expansion items hand-quoted with no escaping ─────────
    // echo {a,b'c'd} emitted @('a','b'c'd') — a PowerShell parse error. Items must
    // route through PsBuild.SingleQuote (which doubles the embedded quote).

    [Fact]
    public void Transpile_BraceExpansionItemWithQuote_EscapesQuote()
    {
        var ps = BashTranspiler.Transpile("echo {a,b'c'd}");
        Assert.Contains("'b''c''d'", ps);      // ' doubled inside the PS literal
        Assert.DoesNotContain("'b'c'd'", ps);  // never the broken un-escaped form
    }

    // ── Finding: subshell input redirect fell to the raw 0<file fallback ────
    // (cmd) < file -> "'<' is reserved for future use". Must become Get-Content | & {}.

    [Fact]
    public void Transpile_SubshellInputRedirect_UsesGetContentPipe()
    {
        var ps = BashTranspiler.Transpile("(cat foo) < bar");
        Assert.Contains("Get-Content", ps);
        Assert.DoesNotContain("0<", ps);
    }

    // ── Finding: crashes must become clean ParseExceptions (fuzz invariant) ─

    [Fact]
    public void Transpile_ForLoopWithDollarVar_ThrowsCleanParseException()
    {
        // `for $i in ...` previously emitted the broken `foreach ($$i ...)`.
        Assert.Throws<ParseException>(
            () => BashTranspiler.Transpile("for $i in a b; do echo $i; done"));
    }

    [Fact]
    public void Transpile_RedirectWithNoTarget_ThrowsCleanParseException()
    {
        // `echo > ; ls` previously swallowed the `;` as the redirect target,
        // silently merging two statements.
        Assert.Throws<ParseException>(
            () => BashTranspiler.Transpile("echo > ; ls"));
    }

    [Fact]
    public void Transpile_HugeFileDescriptor_ThrowsCleanParseException()
    {
        // A digit run past int range previously threw OverflowException.
        Assert.Throws<ParseException>(
            () => BashTranspiler.Transpile("1234567890123>x"));
    }

    [Fact]
    public void Transpile_HereStringLoneQuote_DoesNotRawCrash()
    {
        // `cat <<< '` previously did raw[1..^1] on a length-1 quote word ->
        // ArgumentOutOfRangeException. Must be a clean reject or valid emission.
        var ex = Record.Exception(() => BashTranspiler.Transpile("cat <<< '"));
        Assert.True(ex is null or ParseException, $"unexpected {ex?.GetType().Name}");
    }

    [Fact]
    public void Transpile_HugePositionalParam_DoesNotOverflow()
    {
        // ${10000000000} is an unset positional (empty in bash); int.Parse overflowed.
        var ex = Record.Exception(() => BashTranspiler.Transpile("echo ${10000000000}"));
        Assert.True(ex is null or ParseException, $"unexpected {ex?.GetType().Name}");
    }

    [Fact]
    public void Transpile_HugeBraceTupleInteger_DoesNotOverflow()
    {
        // {10000000000,2} — a tuple integer past int range; int.Parse overflowed
        // in FormatBraceArray's sequential-range check.
        var ex = Record.Exception(() => BashTranspiler.Transpile("echo {10000000000,2}"));
        Assert.True(ex is null or ParseException, $"unexpected {ex?.GetType().Name}");
    }

    // ── Finding: quote-blind ${...} boundary scan ──────────────────────────
    // ${x:-"}"} — the `}` inside the quotes must not close the expansion early.

    [Fact]
    public void Transpile_BracedDefaultWithBraceInQuotes_DoesNotCloseEarly()
    {
        var ex = Record.Exception(() => BashTranspiler.Transpile("echo ${x:-\"}\"}"));
        Assert.True(ex is null or ParseException, $"unexpected {ex?.GetType().Name}");
    }
}
