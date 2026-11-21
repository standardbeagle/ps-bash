using System.Collections.Generic;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// An arithmetic error and a builtin's own error carry bash's source prefix (<c>ShellDiagnostic.Prefix</c>)
/// and bash's words: <c>s.sh: line 7: ((: 1/0 : division by 0 (error token is "0 ")</c>,
/// <c>s.sh: line 7: type: x: not found</c>. They had no prefix (arithmetic) or a hard-coded <c>bash: </c> (type, alias,
/// help): no script name, no line. Oracle: bash 5.2 (WSL), the lines below; end to end: Differential
/// <c>ArithDiagnosticDifferentialTests</c>, <c>BuiltinDiagnosticDifferentialTests</c>.
/// </summary>
public class ShellBuiltinDiagnosticTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public ShellBuiltinDiagnosticTests(SharedPwshFixture fixture) { _fixture = fixture; }

    private const string AtLine7 = "$global:BASH_SOURCE = @('s.sh'); [PsBash.Cmdlets.VarFrameStack]::SetLine(7); ";

    private static BashArith.BashArithException Fail(string expr)
    {
        var vars = new Dictionary<string, long>();
        return Assert.Throws<BashArith.BashArithException>(() => BashArith.Evaluate(expr,
            name => vars.TryGetValue(name, out var v) ? v : 0, (name, v) => vars[name] = v));
    }

    // bash 5.2 `echo $(( EXPR ))`: the report after `s.sh: line N: ` (expr.c evalerror). The token of a division or a
    // negative exponent runs from the right operand (or the token after it) to the end of the expression.
    [Theory]
    [InlineData("1/0+2", "1/0+2: division by 0 (error token is \"0+2\")")]
    [InlineData("1 / 0 + 2 ", "1 / 0 + 2 : division by 0 (error token is \"0 + 2 \")")]
    [InlineData("10/(1-1) ", "10/(1-1) : division by 0 (error token is \"(1-1) \")")]
    [InlineData("1 + (2/0) ", "1 + (2/0) : division by 0 (error token is \"0) \")")]
    [InlineData("1, 2/0, 3 ", "1, 2/0, 3 : division by 0 (error token is \"0, 3 \")")]
    [InlineData("1 ? 2/0 : 3 ", "1 ? 2/0 : 3 : division by 0 (error token is \"0 : 3 \")")]
    [InlineData("5 / x + 1 ", "5 / x + 1 : division by 0 (error token is \"x + 1 \")")]
    [InlineData("7 % 0 ", "7 % 0 : division by 0 (error token is \"0 \")")]
    [InlineData("x /= 0 ", "x /= 0 : division by 0 (error token is \"0 \")")]
    [InlineData("2**-1 ", "2**-1 : exponent less than 0 (error token is \"1 \")")]
    [InlineData("2 ** -1 + 3 ", "2 ** -1 + 3 : exponent less than 0 (error token is \"+ 3 \")")]
    [InlineData("08", "08: value too great for base (error token is \"08\")")]
    [InlineData("3#9", "3#9: value too great for base (error token is \"3#9\")")]
    [InlineData("2 + ", "2 + : syntax error: operand expected (error token is \"+ \")")]
    [InlineData("2 + 3 4 ", "2 + 3 4 : syntax error in expression (error token is \"4 \")")]
    public void Evaluate_Error_DescribesItInBashsWords(string expr, string report)
        => Assert.Equal(report, Fail(expr).Describe(expr));

    // bash: s.sh line 7 `(( 1/0 ))` -> `s.sh: line 7: ((: 1/0 : division by 0 (error token is "0 ")`, status 1.
    [Fact]
    public void InvokeBashArith_ArithCommandError_HasThePrefixAndTheLabel()
        => CmdResult.Run(_fixture.AcquireFresh(), AtLine7 + "Invoke-BashArith '1/0 ' -Label '(('")
            .AssertFailed(1, "s.sh: line 7: ((: 1/0 : division by 0 (error token is \"0 \")");

    // bash: s.sh line 7 `x=$(( 2 + ))` -> `s.sh: line 7: 2 + : syntax error: operand expected (error token is "+ ")`:
    // no label for an expansion, the expression without its leading blanks.
    [Fact]
    public void InvokeBashArith_ExpansionSyntaxError_HasThePrefixAndNoLabel()
        => CmdResult.Run(_fixture.AcquireFresh(), AtLine7 + "Invoke-BashArith ' 2 + '")
            .AssertFailed(1, "s.sh: line 7: 2 + : syntax error: operand expected (error token is \"+ \")");

    // bash: `type nosuchcmd_zz` on line 7 -> `s.sh: line 7: type: nosuchcmd_zz: not found`, 1 (it read `bash: type: ...`).
    [Fact]
    public void Type_NotFound_HasThePrefix()
        => CmdResult.Run(_fixture.AcquireFresh(), AtLine7 + "Invoke-BashType nosuchcmd_zz")
            .AssertFailed(1, "s.sh: line 7: type: nosuchcmd_zz: not found");

    // bash: `alias nosuchal_zz` on line 7 -> `s.sh: line 7: alias: nosuchal_zz: not found`, 1.
    [Fact]
    public void Alias_NotFound_HasThePrefix()
        => CmdResult.Run(_fixture.AcquireFresh(), AtLine7 + "Invoke-BashAlias nosuchal_zz")
            .AssertFailed(1, "s.sh: line 7: alias: nosuchal_zz: not found");

    // bash: `help -Q` on line 7 -> `s.sh: line 7: help: -Q: invalid option`, then the usage line unprefixed, 2.
    [Fact]
    public void Help_InvalidOption_HasThePrefixAndAnUnprefixedUsage()
    {
        var r = CmdResult.Run(_fixture.AcquireFresh(), AtLine7 + "Invoke-BashHelp '-Q'")
            .AssertFailed(2, "s.sh: line 7: help: -Q: invalid option", "help: usage: help [-dms] [pattern ...]");
        Assert.DoesNotContain("bash: help:", r.Stderr);
    }
}
