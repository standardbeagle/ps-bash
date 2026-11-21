using Xunit;
using PsBash.Core.Parser;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// The emitted shape of the shell diagnostics of <c>let</c> and <c>.</c>/<c>source</c>, and the arithmetic parser's
/// syntax errors in bash's words (docs/specs/error-handling.md "Diagnostic prefix"). bash 5.2 (WSL) is the oracle for
/// every expectation; end to end: Differential <c>LetSourceDiagnosticDifferentialTests</c>.
/// </summary>
public class LetSourceDiagnosticEmitTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Theory]
    [InlineData("echo a\nlet x=1", 2)]
    [InlineData("echo a\nv=1+; let \"$v\"", 2)]
    [InlineData("echo a\n.", 2)]
    [InlineData("echo a\nsource ./lib.sh", 2)]
    public void Transpile_BuiltinThatReportsWithThePrefix_RecordsItsLine(string bash, int line)
        => Assert.Contains(PsBuild.SetCallLine(line), T(bash));

    // ── let ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Transpile_LetWithASyntaxError_IsReportedWhenItRuns()
    {
        // bash: `let: 1+: syntax error: operand expected (error token is "+")`, status 1, and the script goes on.
        // The transpile threw BashArithmeticParseException: no line of the script ran.
        var ps = T("let '1+'; echo after");
        Assert.Contains("Invoke-BashLet", ps);
        Assert.Contains("Invoke-BashEcho after", ps);
    }

    [Fact]
    public void Transpile_StaticLet_EvaluatesWithTheLetLabel()
        // `let x=1/0`: `let: x=1/0: division by 0` -- the static form reports as the run-time one does.
        => Assert.Contains("Invoke-BashArith 'x=1/0' -Label 'let'", T("let x=1/0"));

    [Fact]
    public void Transpile_ArithCommand_EvaluatesWithTheArithCommandLabel()
        // bash: ((: x = 1/0 : division by 0 (error token is "0 ") -- ((, the expression left-trimmed only.
        => Assert.Contains("Invoke-BashArith 'x = 1 ' -Label '(('", T("(( x = 1 ))"));

    // ── . / source with no operand ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(".")]
    [InlineData("source")]
    public void Transpile_SourceWithNoOperand_ReportsWithPrefixThenUsage(string name)
    {
        // bash: `s.sh: line 1: .: filename argument required`, `.: usage: . filename [arguments]` (no prefix), status 2.
        var ps = T(name);
        Assert.Contains(PsBuild.ShellDiagnosticLine(PsBuild.SingleQuote(name + ": filename argument required")), ps);
        Assert.Contains(PsBuild.StderrLine(name + ": usage: " + name + " filename [arguments]"), ps);
        Assert.Contains(PsBuild.SetStatus(2), ps);
    }

    // ── arithmetic syntax errors in bash's words ────────────────────────────────────────────────

    // bash 5.2 (WSL) `let 'EXPR'`: the message after `let: EXPR: `.
    [Theory]
    [InlineData("1+", "syntax error: operand expected (error token is \"+\")")]
    [InlineData("1+ ", "syntax error: operand expected (error token is \"+ \")")]
    [InlineData("x==", "syntax error: operand expected (error token is \"==\")")]
    [InlineData(")", "syntax error: operand expected (error token is \")\")")]
    [InlineData("1+*2", "syntax error: operand expected (error token is \"*2\")")]
    [InlineData("a b", "syntax error in expression (error token is \"b\")")]
    [InlineData("a b c", "syntax error in expression (error token is \"b c\")")]
    [InlineData("(1+2", "missing `)' (error token is \"2\")")]
    [InlineData("1?2", "`:' expected for conditional expression (error token is \"2\")")]
    [InlineData("1 @ 2", "syntax error: invalid arithmetic operator (error token is \"@ 2\")")]
    public void ParseArithmetic_SyntaxError_IsBashsMessage(string expr, string message)
        => Assert.Equal(message, Assert.Throws<BashArithmeticParseException>(() => BashArithmeticParser.Parse(expr)).Message);
}
