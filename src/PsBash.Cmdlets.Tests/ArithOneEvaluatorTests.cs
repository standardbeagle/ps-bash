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

    /// <summary>
    /// A compound assignment and <c>++</c> resolve their target ONCE, then read and write it (bash 5.2: <c>i=0;
    /// a=(10 20 30); (( a[i++] += i ))</c> is i=1, a=(11 20 30)). REGRESSION: read and write each got <c>a[i++]</c>.
    /// </summary>
    [Theory]
    [InlineData("a[i++] += i", 1L, 11L, 20L)]
    [InlineData("a[i++]++", 1L, 11L, 20L)]
    [InlineData("++a[i++]", 1L, 11L, 20L)]
    [InlineData("a[i++] *= 2", 1L, 20L, 20L)]
    public void Evaluate_CompoundTarget_IsResolvedOnce(string expr, long i, long a0, long a1)
    {
        var vars = new Dictionary<string, long> { ["i"] = 0, ["a[0]"] = 10, ["a[1]"] = 20 };
        int resolved = 0;
        string Resolve(string name)
        {
            if (name != "a[i++]") return name;
            resolved++;
            long at = vars["i"];
            vars["i"] = at + 1;
            return "a[" + at + "]";
        }
        BashArith.Evaluate(expr, n => vars.TryGetValue(n, out var v) ? v : 0, (n, v) => vars[n] = v, null, Resolve);
        Assert.Equal(1, resolved);
        Assert.Equal(i, vars["i"]);
        Assert.Equal(a0, vars["a[0]"]);
        Assert.Equal(a1, vars["a[1]"]);
    }

    [Fact]
    public void Evaluate_PlainAssignment_LeavesItsSubscriptToTheWrite()
    {
        // `(( a[i] = i++ ))` sets a[1] in bash: a plain `=` evaluates the subscript after the right side, at the write.
        var written = new List<string>();
        BashArith.Evaluate("a[i] = 5", _ => 0, (n, _) => written.Add(n), null, n => throw new InvalidOperationException("resolved " + n));
        Assert.Equal(new[] { "a[i]" }, written);
    }

    /// <summary>
    /// A .NET fault raised inside a read or write callback is the variable layer's, not an arithmetic error: it passes
    /// on unchanged. REGRESSION: the evaluator's catch turned it into "EXPR: Object reference not set …".
    /// </summary>
    [Fact]
    public void Evaluate_CallbackFault_PassesOnUnchanged()
    {
        Assert.Throws<NullReferenceException>(() =>
            BashArith.Evaluate("x + 1", _ => throw new NullReferenceException("store"), (_, _) => { }));
        Assert.Throws<ArgumentException>(() =>
            BashArith.Evaluate("x = 1", _ => 0, (_, _) => throw new ArgumentException("store")));
        Assert.Throws<InvalidCastException>(() =>
            BashArith.Evaluate("16#$h", _ => 0, (_, _) => { }, _ => throw new InvalidCastException("store")));
    }

    [Fact]
    public void Evaluate_NestedEvaluationErrorInACallback_IsStillAnArithmeticError()
    {
        // A callback that evaluates a value (x='1/0') fails with the inner expression's arithmetic error, not a fault.
        var ex = Assert.Throws<BashArith.BashArithException>(() =>
            BashArith.Evaluate("x + 1", _ => BashArith.Evaluate("1/0", _ => 0, (_, _) => { }), (_, _) => { }));
        Assert.Equal("division by 0", ex.Message);
    }

    /// <summary>The runtime evaluator parses what an expanded value holds: 100k levels of any recursive edge fail cleanly.
    /// REGRESSION: <c>e=0?0:0?0:…1; $((e))</c> overflowed the stack and took the host down.</summary>
    [Theory]
    [InlineData("0?0:", "1")]
    [InlineData("(", "1")]
    [InlineData("- ", "1")]
    [InlineData("!", "1")]
    [InlineData("~", "1")]
    [InlineData("2**", "1")]
    [InlineData("a=", "1")]
    public void Evaluate_VeryDeepValue_FailsCleanly(string unit, string tail)
    {
        string expr = string.Concat(System.Linq.Enumerable.Repeat(unit, 100_000)) + tail
                      + (unit == "(" ? string.Concat(System.Linq.Enumerable.Repeat(")", 100_000)) : "");
        var ex = Assert.Throws<BashArith.BashArithException>(() => Eval(expr));
        Assert.Contains("nesting too deep", ex.Message);
    }

    // bash: e=$(printf '(%.0s' {1..1000})1$(printf ')%.0s' {1..1000}); echo $((e)) -> 1. A run-time value is parsed on
    // whatever thread evaluates it: one that runs out of stack first retries on a large stack (as a transpile does), so
    // the limit is the depth cap on every thread. REGRESSION: 300 parens were "nesting too deep".
    [Theory]
    [InlineData(300)]
    [InlineData(1000)]
    public void Evaluate_NestedParensAsDeepAsBashTakes_OnSmallStackThread_IsTheValue(int depth)
    {
        string expr = new string('(', depth) + "1" + new string(')', depth);
        long? value = null;
        Exception? caught = null;
        var t = new System.Threading.Thread(() =>
        {
            try { value = Eval(expr); }
            catch (Exception e) { caught = e; }
        }, 256 * 1024);
        t.Start();
        t.Join();
        Assert.Null(caught);
        Assert.Equal(1L, value);
    }

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

    // A fault in a read / write callback is the CALLER's fault, not an arithmetic error of the expression: it passes on
    // unchanged (Evaluate_CallbackFault_PassesOnUnchanged). REGRESSION: IsEvaluatorFault also caught the callbacks'.
    [Fact]
    public void Evaluate_CallbackOverflow_IsNotAnArithmeticError()
        => Assert.Throws<OverflowException>(() =>
            BashArith.Evaluate("y + 1", _ => throw new OverflowException("boom"), (_, _) => { }));

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
        "try { " + script + " } finally { Remove-Item env:zzh, env:zzx, env:zzr1, env:zzr2, env:zza, env:zzb, env:zzc, env:zzi -ErrorAction Ignore }");

    // bash: i=0; a=(10 20 30); (( a[i++] += i )) -> i=1, a=(11 20 30); (( a[i++]++ )) -> i=1, a=(11 20 30) from (10 20 30).
    // REGRESSION: the read and the write each evaluated the subscript (i=2, a[1] written).
    [Theory]
    [InlineData("zzq[zzi++] += zzi", "1 11 20 30")]
    [InlineData("zzq[zzi++]++", "1 11 20 30")]
    [InlineData("zzq[zzi] = zzi++", "1 10 0 30")]
    public void InvokeBashArith_ElementTarget_SubscriptEvaluatedOnce(string expr, string expected)
        => Assert.Equal(expected, Run("$zzq = @('10', '20', '30'); $env:zzi = '0'; $null = Invoke-BashArith '" + expr
                                      + "'; \"$env:zzi $($zzq -join ' ')\"").AssertSuccess().Stdout.Trim());

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
