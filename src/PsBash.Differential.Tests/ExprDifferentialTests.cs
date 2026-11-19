using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle for expr's regex match. Each script runs in real bash AND ps-bash and
/// stdout/stderr/exit are diffed against GNU expr.
///
/// Covered regressions:
///   - the <c>STR : REGEX</c> operator was unimplemented ("non-integer argument")
///   - <c>match</c> read REGEX as .NET, not BRE: <c>[a-z]+</c> matched (bash: literal
///     <c>+</c>) and <c>[a-z]\+</c> did not
///   - a <c>\(…\)</c> pattern with no match printed <c>0</c> (bash: empty)
/// </summary>
public class ExprDifferentialTests
{
    private static Task Eq(string script) =>
        AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(30));

    [SkippableFact]
    public Task Expr_Colon_GroupCapture()
        => Eq("expr abc123 : '\\([a-z]*\\)'");

    [SkippableFact]
    public Task Expr_Colon_MatchLength_BreOperators()
        => Eq("expr foo : 'fo\\{2\\}'; expr abc123 : '[a-z]\\+'; expr 'a|b' : 'a|b'");

    [SkippableFact]
    public Task Expr_Match_BarePlusIsLiteral()
        => Eq("expr match abc123 '[a-z]+'; expr match abc123 '[a-z]\\+'");

    [SkippableFact]
    public Task Expr_Colon_GroupNoMatch_PrintsEmpty()
        => Eq("expr abc : '\\([0-9]*\\)x'; echo \"[$(expr abc : '\\(z\\)')]\"");

    // GNU exits 1 on a null or zero result; ps-bash always exited 0.
    [SkippableFact]
    public Task Expr_ZeroOrEmptyResult_ExitsOne()
        => Eq("expr 1 - 1; echo rc=$?; expr abc : z; echo rc=$?; expr abc : '\\(z\\)'; echo rc=$?; expr 2 + 3; echo rc=$?");

    [SkippableFact]
    public Task Expr_Colon_PunctClass()
        => Eq("expr 'x!' : 'x[[:punct:]]'");

    // Class: expr was a flat one-operator dispatch, so chains, precedence, | and & were wrong or
    // errors. Fix = the ExprEvaluator grammar (one level per precedence, left-associative).
    [SkippableFact]
    public Task Expr_Chains_PrecedenceAndAssociativity()
        => Eq("expr 1 + 2 + 3; expr 20 / 2 / 5; expr 10 - 3 - 2; expr 1 + 2 \\* 3 - 4 / 2 % 3; "
            + "expr \\( 2 + 3 \\) \\* \\( 4 - 1 \\); expr 2 \\> 1 \\> 0; expr abc : 'a\\(.\\)' = b; "
            + "expr -7 / 2; expr -5 % 3; expr 5 % -3; expr 99999999999999999999 + 1; expr 007; expr 007 + 0");

    [SkippableFact]
    public Task Expr_OrAnd_ShortCircuitAndNullness()
        => Eq("expr 2 \\| 1 / 0; echo rc=$?; expr 0 \\& 1 / 0; echo rc=$?; expr '' \\| ''; echo rc=$?; "
            + "expr 00 \\| 7; expr -0 \\& 1; echo rc=$?; expr x \\| y; expr 1 \\& 2");

    [SkippableFact]
    public Task Expr_Comparisons_NumericElseBytewise()
        => Eq("expr 3 \\< 10; expr a10 \\< a9; expr a \\< B; expr 010 = 10; expr abc != abd; expr 10 \\>= 9");

    [SkippableFact]
    public Task Expr_KeywordsAndQuote()
        => Eq("expr + length; expr length + 3; expr index abcb cb; expr substr hello 2 100; "
            + "echo \"[$(expr substr abc 0 1)]\"; expr -- 5; expr -");

    [SkippableFact]
    public Task Expr_Errors_GnuSyntaxMessages()
        => Eq("expr 1 +; echo rc=$?; expr \\( 1; echo rc=$?; expr \\( 1 2; echo rc=$?; expr 1 2; echo rc=$?; "
            + "expr \\); echo rc=$?; expr 1 + a; echo rc=$?; expr abcdef : 'a\\(..\\)' + 0; echo rc=$?; "
            + "expr length; echo rc=$?; expr 1 / 0; echo rc=$?; expr; echo rc=$?; expr ' 1' + 1; echo rc=$?");
}
