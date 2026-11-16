using Xunit;
using PsBash.Testing;

namespace PsBash.Core.Tests;

/// <summary>
/// A plain [Fact]/[Theory] that reaches Skip.If/IfNot or `new SkipException` FAILS instead of
/// skipping (Xunit.SkippableFact only intercepts the throw under [SkippableFact]/[SkippableTheory]).
/// Rule: .claude/rules/testing.md. Guard: PsBash.Testing.SkipUsageGuard.
/// </summary>
public class SkipUsageGuardTests
{
    [Fact]
    public void SkipUsage_EveryTestThatCanSkip_IsSkippable()
    {
        var violations = SkipUsageGuard.Violations(typeof(SkipUsageGuardTests).Assembly);
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    // ---- The guard's own self-test: abstract fixtures xunit never runs. ----

    [Theory]
    [InlineData(typeof(SkipGuardFixtures.DirectSkipIf), "Xunit.Skip.If")]
    [InlineData(typeof(SkipGuardFixtures.DirectSkipIfNot), "Xunit.Skip.IfNot")]
    [InlineData(typeof(SkipGuardFixtures.ThrowsSkipException), "Xunit.SkipException.ctor")]
    [InlineData(typeof(SkipGuardFixtures.ThroughTwoHelpers), "Outer -> SkipGuardFixtures.Inner -> Xunit.Skip.If")]
    [InlineData(typeof(SkipGuardFixtures.InAsyncBody), "Xunit.Skip.IfNot")]
    [InlineData(typeof(SkipGuardFixtures.InLambda), "Xunit.Skip.If")]
    [InlineData(typeof(SkipGuardFixtures.InTheory), "[Theory]")]
    [InlineData(typeof(SkipGuardFixtures.InConstructor), ".ctor")]
    [InlineData(typeof(SkipGuardFixtures.InheritedFromSkippingBase), ".ctor")]
    public void SkipUsage_PlainTestReachingASkip_IsReported(Type fixture, string expectedFragment)
    {
        var violations = SkipUsageGuard.Violations(fixture.Assembly, [fixture]);
        var v = Assert.Single(violations);
        Assert.Contains(fixture.Name, v);
        Assert.Contains(expectedFragment, v);
    }

    [Theory]
    [InlineData(typeof(SkipGuardFixtures.SkippableFactSkips))]
    [InlineData(typeof(SkipGuardFixtures.SkippableTheorySkips))]
    [InlineData(typeof(SkipGuardFixtures.PlainFactNoSkip))]
    [InlineData(typeof(SkipGuardFixtures.NonTestHelperSkips))]
    [InlineData(typeof(SkipGuardFixtures.RecursiveHelperNoSkip))]
    public void SkipUsage_SkippableOrNonSkippingTest_IsClean(Type fixture)
        => Assert.Empty(SkipUsageGuard.Violations(fixture.Assembly, [fixture]));
}

#pragma warning disable xUnit1013 // public non-test methods on fixtures are intentional
public static class SkipGuardFixtures
{
    private static bool Cond() => Environment.ProcessId < 0;

    public abstract class DirectSkipIf { [Fact] public void T() => Skip.If(Cond(), "x"); }
    public abstract class DirectSkipIfNot { [Fact] public void T() => Skip.IfNot(!Cond(), "x"); }
    public abstract class ThrowsSkipException { [Fact] public void T() { if (Cond()) throw new SkipException("x"); } }

    public abstract class ThroughTwoHelpers { [Fact] public void T() => Outer(); }
    public static void Outer() => Inner();
    public static void Inner() => Skip.If(Cond(), "x");

    public abstract class InAsyncBody
    {
        [Fact]
        public async Task T()
        {
            await Task.Yield();
            Skip.IfNot(!Cond(), "x");
        }
    }

    public abstract class InLambda
    {
        [Fact]
        public void T()
        {
            Action a = () => Skip.If(Cond(), "x");
            a();
        }
    }

    public abstract class InTheory
    {
        [Theory, InlineData(1)]
        public void T(int n) => Skip.If(n < 0 && Cond(), "x");
    }

    public abstract class InConstructor
    {
        protected InConstructor() => Skip.If(Cond(), "x");
        [Fact] public void T() { }
    }

    public abstract class SkippingBase { protected SkippingBase() => Inner(); }
    public abstract class InheritedFromSkippingBase : SkippingBase { [Fact] public void T() { } }

    public abstract class SkippableFactSkips { [SkippableFact] public void T() => Outer(); }
    public abstract class SkippableTheorySkips { [SkippableTheory, InlineData(1)] public void T(int n) => Skip.If(n < 0, "x"); }
    public abstract class PlainFactNoSkip { [Fact] public void T() => Assert.False(Cond()); }
    public abstract class NonTestHelperSkips { public void NotATest() => Outer(); [Fact] public void T() { } }

    public abstract class RecursiveHelperNoSkip { [Fact] public void T() => Ping(3); }
    public static int Ping(int n) => n <= 0 ? 0 : Pong(n - 1);
    public static int Pong(int n) => n <= 0 ? 0 : Ping(n - 1);
}
#pragma warning restore xUnit1013
