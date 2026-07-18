using System.Collections.Generic;
using PsBash.Cmdlets;
using PsBash.Core.Parser;
using PsBash.Core.Parser.Ast;
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
    public void Evaluate_DollarNamedVariable_UsesLegacyBareVariableName()
    {
        var vars = new Dictionary<string, long> { ["value"] = 12 };

        Assert.Equal(13, Eval("$value + 1", vars));
    }

    [Theory]
    [InlineData("$1", "1")]
    [InlineData("$0", "0")]
    [InlineData("$9", "9")]
    [InlineData("$#", "#")]
    [InlineData("$?", "?")]
    [InlineData("$@", "@")]
    [InlineData("$*", "*")]
    [InlineData("$$", "$")]
    [InlineData("$!", "!")]
    public void Evaluate_PositionalAndSpecialParameter_ReadsBareDelegateKey(string expression, string key)
    {
        string? requested = null;
        long value = BashArith.Evaluate(expression,
            name => { requested = name; return name == key ? 17 : 0; },
            (_, _) => { });

        Assert.Equal(17, value);
        Assert.Equal(key, requested);
    }

    [Fact]
    public void Evaluate_UnsetSpecialParameter_IsZeroWithoutThrowing()
        => Assert.Equal(0, Eval("$?"));

    [Theory]
    [InlineData("$10", "1", 70)]
    [InlineData("${1}", "1", 7)]
    [InlineData("${10}", "10", 7)]
    [InlineData("${x}", "x", 7)]
    [InlineData("${?}", "?", 7)]
    [InlineData("$1+2", "1", 9)]
    public void Evaluate_ParameterSpelling_UsesNormalizedLookupKey(
        string expression, string expectedKey, long expected)
    {
        string? requested = null;
        long value = BashArith.Evaluate(expression,
            key => { requested = key; return 7; },
            (_, _) => { });

        Assert.Equal(expected, value);
        Assert.Equal(expectedKey, requested);
    }

    [Fact]
    public void Evaluate_UnbracedDigitSuffix_ConcatenatesRawTextBeforeEvaluation()
    {
        long result = BashArith.Evaluate("$10",
            key => key == "1" ? 5 : 0,
            (_, _) => { },
            key => key == "1" ? "2+3" : null);

        Assert.Equal(32, result); // expansion is 2+30, not evaluated(2+3) + "0"
    }

    [Fact]
    public void Evaluate_UnbracedDigitSuffix_ExpandsWholeSourceBeforeParsingPrecedence()
    {
        long result = BashArith.Evaluate("$10 * 2",
            key => key == "1" ? 5 : 0,
            (_, _) => { },
            key => key == "1" ? "2+3" : null);

        Assert.Equal(62, result); // expanded source is 2+30 * 2
    }

    [Fact]
    public void Evaluate_UnbracedDigitSuffix_RawExpansionDepthGrowthThrowsControlledError()
    {
        var error = Assert.Throws<BashArith.BashArithException>(() =>
            BashArith.Evaluate("$10",
                _ => 0,
                (_, _) => { },
                key => key == "1" ? "$10" : null));

        Assert.Contains("maximum depth", error.Message);
    }

    [Fact]
    public void Evaluate_UnbracedDigitSuffix_RawAmplificationThrowsBeforeOversizedAllocation()
    {
        var error = Assert.Throws<BashArith.BashArithException>(() =>
            BashArith.Evaluate("$10",
                _ => 0,
                (_, _) => { },
                key => key == "1" ? "$10$10" : null));

        Assert.Contains("maximum length", error.Message);
    }

    [Fact]
    public void Evaluate_BracedMultiDigit_DoesNotUseUnbracedSuffixRule()
    {
        string? requested = null;
        long result = BashArith.Evaluate("${10}",
            key => { requested = key; return 11; },
            (_, _) => { },
            _ => "2+3");

        Assert.Equal(11, result);
        Assert.Equal("10", requested);
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

    [Fact]
    public void Parse_ProducesTypedReusableSyntaxAndPreservesSource()
    {
        ArithmeticSyntax syntax = BashArithmeticParser.Parse("x += 2 ** 3");

        Assert.Equal("x += 2 ** 3", syntax.Source);
        var assignment = Assert.IsType<ArithmeticExpr.Assignment>(syntax.Root);
        Assert.Equal("x", assignment.Name);
        Assert.Equal(ArithmeticAssignmentOp.Add, assignment.Op);
        Assert.Equal(ArithmeticBinaryOp.Power,
            Assert.IsType<ArithmeticExpr.Binary>(assignment.Value).Op);

        var vars = new Dictionary<string, long> { ["x"] = 1 };
        Assert.Equal(9, BashArith.Evaluate(syntax,
            name => vars.TryGetValue(name, out long value) ? value : 0,
            (name, value) => vars[name] = value));
        Assert.Equal(9, vars["x"]);
    }

    [Fact]
    public void Evaluate_TernaryTrueBranch_AcceptsCommaExpressionAndSideEffects()
    {
        var vars = new Dictionary<string, long>();

        Assert.Equal(3, Eval("1 ? (x = 2), x + 1 : (x = 99)", vars));
        Assert.Equal(2, vars["x"]);
    }

    [Fact]
    public void Parse_Ternary_IsNestedAndFalseBranchIsRightAssociative()
    {
        var outer = Assert.IsType<ArithmeticExpr.Conditional>(
            BashArithmeticParser.Parse("0 ? 1 : 1 ? 2 : 3").Root);

        Assert.IsType<ArithmeticExpr.Conditional>(outer.WhenFalse);
        Assert.Equal(2, Eval("0 ? 1 : 1 ? 2 : 3"));
    }

    [Fact]
    public void Parse_PrecedenceAndPowerAssociativity_HaveExpectedShape()
    {
        var add = Assert.IsType<ArithmeticExpr.Binary>(BashArithmeticParser.Parse("2 + 3 * 4").Root);
        Assert.Equal(ArithmeticBinaryOp.Add, add.Op);
        Assert.Equal(ArithmeticBinaryOp.Multiply, Assert.IsType<ArithmeticExpr.Binary>(add.Right).Op);

        var power = Assert.IsType<ArithmeticExpr.Binary>(BashArithmeticParser.Parse("2 ** 3 ** 2").Root);
        Assert.Equal(ArithmeticBinaryOp.Power, power.Op);
        Assert.Equal(ArithmeticBinaryOp.Power, Assert.IsType<ArithmeticExpr.Binary>(power.Right).Op);
    }

    [Fact]
    public void Evaluate_LazyBranches_AreParsedButUntakenSideEffectsDoNotRun()
    {
        var vars = new Dictionary<string, long>();
        Assert.Equal(7, Eval("1 ? 7 : (x = 9)", vars));
        Assert.False(vars.ContainsKey("x"));

        Assert.Throws<BashArith.BashArithException>(() => Eval("1 ? 7 : (x = )", vars));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1 +")]
    [InlineData("(1 + 2")]
    public void Evaluate_EmptyOrMalformedInput_ThrowsBashArithException(string expression)
        => Assert.Throws<BashArith.BashArithException>(() => Eval(expression));

    [Fact]
    public void Evaluate_NegativeExponent_PreservesZeroResult()
        => Assert.Equal(0, Eval("2 ** -1"));
}

public class InvokeBashArithmeticRawReaderTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashArithmeticRawReaderTests(SharedPwshFixture fixture) => _fixture = fixture;

    [Fact]
    public void InvokeBashArith_ExistingEmptyPowerShellVariable_WinsOverEnvironment()
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(
            "$env:x = '9'; $x = ''; Invoke-BashArith '$x'").Invoke();

        Assert.Equal("0", Assert.Single(result).ToString());
    }
}
