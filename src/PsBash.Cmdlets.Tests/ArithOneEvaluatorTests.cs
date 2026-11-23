using System;
using System.Collections.Generic;
using PsBash.Cmdlets;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The one arithmetic evaluator (<see cref="BashArith"/> over <c>BashArithmeticParser</c>) against bash 5.2.21: every
/// expected value and message here is the oracle's (`bash -c 'echo $((EXPR))'`, beagle-ab). Each case failed before the
/// one-evaluator fix: <c>**</c> looped once per unit of the exponent (`(-1)**9223372036854775807` hung the host), -2**63 / -1
/// threw a .NET OverflowException out of the evaluator, a compound assignment read its target AFTER the right side,
/// <c>16#FF</c> read F as 41, <c>++5</c> was an increment of a constant, and several errors had the wrong message.
/// The emitter contexts (slices, subscripts, unset, declare -i) are in <c>Differential.Tests/ArithOneEvaluatorDifferentialTests</c>.
/// </summary>
public class ArithOneEvaluatorTests
{
    private static long Eval(string expr, Dictionary<string, long>? vars = null)
    {
        vars ??= new();
        return BashArith.Evaluate(expr,
            name => vars.TryGetValue(name, out var v) ? v : 0,
            (name, v) => vars[name] = v);
    }

    private static string Error(string expr, Dictionary<string, long>? vars = null)
        => Assert.Throws<BashArith.BashArithException>(() => Eval(expr, vars)).Describe(expr);

    [Theory]
    // Exponentiation by squaring, wrapping like bash's 64-bit ipow.
    [InlineData("(-1)**9223372036854775807", -1L)]
    [InlineData("(-1)**9223372036854775806", 1L)]
    [InlineData("2**62", 4611686018427387904L)]
    [InlineData("2**63", long.MinValue)]
    [InlineData("2**64", 0L)]
    [InlineData("0**0", 1L)]
    [InlineData("1**9223372036854775807", 1L)]
    [InlineData("(-2)**63", long.MinValue)]
    // The one quotient that does not fit wraps; its remainder is 0.
    [InlineData("(-2**63)/-1", long.MinValue)]
    [InlineData("(-2**63)%-1", 0L)]
    [InlineData("-9223372036854775807-1", long.MinValue)]
    [InlineData("7/-1", -7L)]
    [InlineData("7%-1", 0L)]
    [InlineData("2**62*4/-1", 0L)]
    // BASE#DIGITS: up to base 36 a letter of either case is 10-35.
    [InlineData("16#FF", 255L)]
    [InlineData("16#Ff", 255L)]
    [InlineData("16#ff", 255L)]
    [InlineData("36#Z", 35L)]
    [InlineData("36#z", 35L)]
    [InlineData("20#J", 19L)]
    // Above base 36: a-z 10-35, A-Z 36-61, @ 62, _ 63.
    [InlineData("64#z", 35L)]
    [InlineData("64#Z", 61L)]
    [InlineData("64#@", 62L)]
    [InlineData("64#_", 63L)]
    [InlineData("37#A", 36L)]
    // `++` / `--` not beside a name is two signs.
    [InlineData("++5", 5L)]
    [InlineData("--5", 5L)]
    [InlineData("-- 5", 5L)]
    [InlineData("1--1", 2L)]
    [InlineData("1++1", 2L)]
    [InlineData("-(-5)", 5L)]
    public void Evaluate_MatchesBash(string expr, long expected)
        => Assert.Equal(expected, Eval(expr));

    [Theory]
    // A compound assignment reads its target before the right side runs.
    [InlineData("x += (x=10)", 1L, 11L)]
    [InlineData("x += x++", 1L, 2L)]
    [InlineData("x+=x*=2", 3L, 9L)]
    [InlineData("x -= (x=10)", 1L, -9L)]
    [InlineData("x *= (x=10)", 2L, 20L)]
    // /= and %= by -1 at the bottom of the range.
    [InlineData("x /= -1", long.MinValue, long.MinValue)]
    [InlineData("x %= -1", long.MinValue, 0L)]
    [InlineData("x /= -1", 7L, -7L)]
    public void Evaluate_CompoundAssignment_MatchesBash(string expr, long before, long after)
    {
        var vars = new Dictionary<string, long> { ["x"] = before };
        Assert.Equal(after, Eval(expr, vars));
        Assert.Equal(after, vars["x"]);
    }

    [Theory]
    [InlineData("x=1, ++x", 2L)]
    [InlineData("x=1, ++ x", 2L)]
    [InlineData("x=1, x++ + x", 3L)]
    [InlineData("x=5, x+++1", 6L)]
    [InlineData("x=5, --x", 4L)]
    public void Evaluate_IncrementBesideAName_StillIncrements(string expr, long expected)
        => Assert.Equal(expected, Eval(expr));

    [Theory]
    [InlineData("0 ? 1 : x = 5", "0 ? 1 : x = 5: attempted assignment to non-variable (error token is \"= 5\")")]
    [InlineData("1=2", "1=2: attempted assignment to non-variable (error token is \"=2\")")]
    [InlineData("1 += 2", "1 += 2: attempted assignment to non-variable (error token is \"+= 2\")")]
    [InlineData("5++", "5++: syntax error: operand expected (error token is \"+\")")]
    [InlineData("--4++", "--4++: syntax error: operand expected (error token is \"+\")")]
    [InlineData("1 ? 2 : ", "1 ? 2 : : expression expected (error token is \": \")")]
    [InlineData("1 ? ", "1 ? : expression expected (error token is \"? \")")]
    [InlineData("1--x", "1--x: syntax error in expression (error token is \"--x\")")]
    [InlineData("2**-1", "2**-1: exponent less than 0 (error token is \"1\")")]
    public void Evaluate_Error_MatchesBashMessage(string expr, string message)
        => Assert.Equal(message, Error(expr));

    [Fact]
    public void Evaluate_ConditionalWithNothingBeforeTheColon_IsExpressionExpected()
        => Assert.Contains(": expression expected", Error("1 ? : 2"));

    [Fact]
    public void Evaluate_ConditionalTakesAnAssignmentInItsMiddleOperand()
    {
        var vars = new Dictionary<string, long>();
        Assert.Equal(5, Eval("1 ? x = 5 : 0", vars));
        Assert.Equal(5, vars["x"]);
    }

    // No .NET exception leaves the evaluator: a callback's failure of the evaluator's own kind becomes its error.
    [Fact]
    public void Evaluate_DotNetFault_IsABashArithmeticError()
    {
        var ex = Assert.Throws<BashArith.BashArithException>(() =>
            BashArith.Evaluate("y + 1", _ => throw new OverflowException("boom"), (_, _) => { }));
        Assert.Equal("y + 1", ex.Expression);
        Assert.IsType<OverflowException>(ex.InnerException);
    }

    // A callback's own signal (an unbound variable under set -u, the recursion limit) is not swallowed.
    [Fact]
    public void Evaluate_CallbackSignal_PassesThrough()
        => Assert.Throws<TimeoutException>(() =>
            BashArith.Evaluate("y + 1", _ => throw new TimeoutException(), (_, _) => { }));
}

/// <summary>
/// The cmdlet's text substitution and recursion report against bash 5.2.21: <c>$x</c> is replaced by its value's TEXT,
/// with nothing around it (it was padded with blanks, so <c>16#$h</c> became <c>16# ff</c>: "invalid integer constant").
/// </summary>
public class ArithOneEvaluatorCmdletTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public ArithOneEvaluatorCmdletTests(SharedPwshFixture fixture) => _fixture = fixture;

    private CmdResult Run(string script) => CmdResult.Run(_fixture.AcquireFresh(),
        "try { " + script + " } finally { Remove-Item env:zzh, env:zzx, env:zzr1, env:zzr2, env:zza, env:zzb, env:zzc -ErrorAction Ignore }");

    [Theory]
    [InlineData("$env:zzh = 'ff'; Invoke-BashArith '16#$zzh'", "255")]
    [InlineData("$env:zzh = 'ff'; Invoke-BashArith '16#${zzh}'", "255")]
    [InlineData("$env:zzh = 'ff'; Invoke-BashArith '16#$zzh + 1'", "256")]
    [InlineData("$env:zzh = 'ff'; Invoke-BashArith 'zzx = 16#$zzh'; $env:zzx", "255\n255")]
    [InlineData("$env:zzx = '2'; Invoke-BashArith '1$zzx'", "12")]
    [InlineData("$env:zzx = '-1'; Invoke-BashArith '5-$zzx'", "6")]
    [InlineData("$env:zzx = '-1'; Invoke-BashArith '5$zzx'", "4")]
    [InlineData("$env:zzx = '1 + 2'; Invoke-BashArith '$zzx * 3'", "7")]
    [InlineData("$env:zzx = '010'; Invoke-BashArith 'zzx + 1'", "9")]
    [InlineData("$env:zzx = '-010'; Invoke-BashArith 'zzx'", "-8")]
    public void InvokeBashArith_SubstitutesTheValueText(string script, string expected)
        => Assert.Equal(expected, Run(script).AssertSuccess().Stdout.Replace("\r\n", "\n"));

    [Fact]
    public void InvokeBashArith_DivisionInASubstitutedValue_NamesTheWholeText()
        => Run("$env:zzx = '1/0'; Invoke-BashArith '$zzx + 1 '")
            .AssertFailed(1, "1/0 + 1 : division by 0 (error token is \"0 + 1 \")");

    [Fact]
    public void InvokeBashArith_OctalValueWithAnEightIsAnError()
        => Run("$env:zzx = '08'; Invoke-BashArith 'zzx'").AssertFailed(1, "value too great for base");

    // bash: r1=r2 r2=r1; $((r1+1)) -> `r2: expression recursion level exceeded (error token is "r2")`.
    [Fact]
    public void InvokeBashArith_Recursion_NamesTheValueThatWentTooDeep()
        => Run("$env:zzr1 = 'zzr2'; $env:zzr2 = 'zzr1'; Invoke-BashArith 'zzr1+1'")
            .AssertFailed(1, "zzr2: expression recursion level exceeded (error token is \"zzr2\")");

    // bash: r1='r2+1' r2='r1+1'; $((r1)) -> `r2+1: expression recursion level exceeded (error token is "r2+1")`.
    [Fact]
    public void InvokeBashArith_Recursion_NamesTheValueExpressionFromTheOperand()
        => Run("$env:zzr1 = 'zzr2+1'; $env:zzr2 = 'zzr1+1'; Invoke-BashArith 'zzr1'")
            .AssertFailed(1, "zzr2+1: expression recursion level exceeded (error token is \"zzr2+1\")");

    // bash: a=b b=c c=a; $((a)) -> `a: expression recursion level exceeded (error token is "a")`.
    [Fact]
    public void InvokeBashArith_RecursionOfThree_NamesTheFirst()
        => Run("$env:zza = 'zzb'; $env:zzb = 'zzc'; $env:zzc = 'zza'; Invoke-BashArith 'zza'")
            .AssertFailed(1, "zza: expression recursion level exceeded (error token is \"zza\")");
}
