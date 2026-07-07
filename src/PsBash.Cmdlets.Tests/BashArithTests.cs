using System.Collections.Generic;
using PsBash.Cmdlets;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Unit tests for <see cref="BashArith"/>, the bash arithmetic evaluator backing
/// <c>$(( ))</c> / <c>(( ))</c>. Expected values are the real-bash oracle results
/// (verified against WSL <c>bash -c 'echo $((expr))'</c>). Regression guard for
/// the 26/35 operator mismatches the old pass-to-PowerShell approach produced
/// (** , integer /, bitwise/shift, 1/0 comparisons &amp; logicals, ternary, bases).
/// </summary>
public class BashArithTests
{
    private static long Eval(string expr, Dictionary<string, long>? vars = null)
    {
        vars ??= new();
        return BashArith.Evaluate(expr,
            name => vars.TryGetValue(name, out var v) ? v : 0,
            (name, v) => vars[name] = v);
    }

    [Theory]
    // additive / multiplicative / integer division (truncate toward zero)
    [InlineData("2+3", 5)]
    [InlineData("10-4", 6)]
    [InlineData("6*7", 42)]
    [InlineData("20/3", 6)]
    [InlineData("-7/2", -3)]
    [InlineData("17%5", 2)]
    [InlineData("-7%2", -1)]
    // exponentiation (right-associative)
    [InlineData("2**10", 1024)]
    [InlineData("3**3", 27)]
    [InlineData("2**0", 1)]
    [InlineData("2**3**2", 512)]
    // bitwise / shift
    [InlineData("1<<4", 16)]
    [InlineData("256>>2", 64)]
    [InlineData("12&10", 8)]
    [InlineData("12|3", 15)]
    [InlineData("12^10", 6)]
    [InlineData("~5", -6)]
    // comparisons yield 1/0
    [InlineData("5>3", 1)]
    [InlineData("5<3", 0)]
    [InlineData("5==5", 1)]
    [InlineData("5!=3", 1)]
    [InlineData("5>=5", 1)]
    [InlineData("3<=2", 0)]
    // logical yield 1/0, short-circuit
    [InlineData("1&&1", 1)]
    [InlineData("1||0", 1)]
    [InlineData("0&&1", 0)]
    [InlineData("!0", 1)]
    [InlineData("!5", 0)]
    // ternary
    [InlineData("5>3?100:200", 100)]
    [InlineData("0?1:2", 2)]
    // number bases
    [InlineData("0xFF", 255)]
    [InlineData("0xABCDEF", 11259375)]   // uppercase hex A-F = 10-15 (not bash base 36-41)
    [InlineData("0xabcdef", 11259375)]   // lowercase hex, same value
    [InlineData("010", 8)]
    [InlineData("0755", 493)]            // multi-digit octal
    [InlineData("2#1010", 10)]
    // precedence / grouping
    [InlineData("2+3*4", 14)]
    [InlineData("(2+3)*4", 20)]
    [InlineData("10>5>1", 0)]        // (10>5)=1, then 1>1 = 0
    [InlineData("-5+2", -3)]
    [InlineData("1+2, 3+4", 7)]      // comma yields last
    public void Evaluate_Operator_MatchesBashOracle(string expr, long expected)
        => Assert.Equal(expected, Eval(expr));

    [Fact]
    public void Evaluate_HexBeyond64Bits_WrapsInsteadOfThrowing()
    {
        // bash arithmetic is 64-bit two's complement; a >64-bit literal wraps.
        // Convert.ToInt64 threw OverflowException (a raw crash escaping the
        // BashArithException contract) — ParseRadix wraps instead. 18 hex F's =
        // 72 all-ones bits; the low 64 are all-ones = -1.
        var ex = Record.Exception(() => Eval("0xFFFFFFFFFFFFFFFFFF"));
        Assert.Null(ex);
        Assert.Equal(-1L, Eval("0xFFFFFFFFFFFFFFFFFF"));
    }

    [Fact]
    public void Evaluate_ResolvesVariables()
    {
        var vars = new Dictionary<string, long> { ["a"] = 5, ["b"] = 3 };
        Assert.Equal(16, Eval("a*2 + b*2", vars));
    }

    [Fact]
    public void Evaluate_UnsetVariable_IsZero()
        => Assert.Equal(7, Eval("nope + 7"));

    [Fact]
    public void Evaluate_Assignment_PersistsAndReturnsValue()
    {
        var vars = new Dictionary<string, long>();
        Assert.Equal(10, Eval("x = 10", vars));
        Assert.Equal(10, vars["x"]);
        Assert.Equal(13, Eval("x += 3", vars));
        Assert.Equal(13, vars["x"]);
    }

    [Fact]
    public void Evaluate_PostIncrement_ReturnsOldValue_ThenBumps()
    {
        var vars = new Dictionary<string, long> { ["i"] = 5 };
        Assert.Equal(5, Eval("i++", vars));
        Assert.Equal(6, vars["i"]);
    }

    [Fact]
    public void Evaluate_PreIncrement_ReturnsNewValue()
    {
        var vars = new Dictionary<string, long> { ["i"] = 5 };
        Assert.Equal(6, Eval("++i", vars));
        Assert.Equal(6, vars["i"]);
    }

    [Fact]
    public void Evaluate_CommaWithAssignment_RunsBothReturnsLast()
    {
        var vars = new Dictionary<string, long>();
        Assert.Equal(10, Eval("x = 5, x * 2", vars));
        Assert.Equal(5, vars["x"]);
    }

    [Fact]
    public void Evaluate_DivisionByZero_Throws()
        => Assert.Throws<BashArith.BashArithException>(() => Eval("1/0"));

    [Fact]
    public void Evaluate_TernaryUntakenBranch_HasNoSideEffect()
    {
        // The untaken branch must not assign. cond is true → take 'a=1', skip 'a=99'.
        var vars = new Dictionary<string, long>();
        Eval("1 ? (a = 1) : (a = 99)", vars);
        Assert.Equal(1, vars["a"]);
    }

    [Fact]
    public void Evaluate_ShortCircuitOr_SkipsRhsSideEffect()
    {
        var vars = new Dictionary<string, long>();
        Assert.Equal(1, Eval("1 || (a = 99)", vars));
        Assert.False(vars.ContainsKey("a"));
    }
}
