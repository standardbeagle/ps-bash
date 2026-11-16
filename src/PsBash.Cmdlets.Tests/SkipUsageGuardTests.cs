using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// A plain [Fact]/[Theory] that reaches Skip.If/IfNot or <c>new SkipException</c> FAILS instead of
/// skipping (Xunit.SkippableFact only intercepts the throw under [SkippableFact]/[SkippableTheory]).
/// Rule: .claude/rules/testing.md. Guard: PsBash.Testing.SkipUsageGuard.
/// </summary>
public class SkipUsageGuardTests
{
    [Fact]
    public void SkipUsage_EveryTestThatCanSkip_IsSkippable()
    {
        var violations = PsBash.Testing.SkipUsageGuard.Violations(typeof(SkipUsageGuardTests).Assembly);
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }
}
