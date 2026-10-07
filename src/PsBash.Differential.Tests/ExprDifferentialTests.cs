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
}
