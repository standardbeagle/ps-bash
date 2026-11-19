using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// GUARD: one table (<see cref="BashBuiltins"/>) decides how a builtin named at run time (<c>"$@"</c>, <c>$cmd</c>,
/// <c>command NAME</c>) runs, and it agrees with the static emitter. The bug class: the run-time lookup knew only the
/// run-time commands, so every builtin the EMITTER implements (<c>eval</c>, <c>.</c>, <c>set</c>, <c>exec</c>) was
/// <c>command not found</c> (127) through <c>"$@"</c>. Emitted-text surface; the behavior is compared with bash in
/// Differential.Tests/DynamicBuiltinDifferentialTests.
/// </summary>
public class BuiltinDispatchGuardTests
{
    public static TheoryData<string> AllBuiltins()
    {
        var data = new TheoryData<string>();
        foreach (var n in BashBuiltins.Names.Order(StringComparer.Ordinal)) data.Add(n);
        return data;
    }

    [Fact]
    public void Groups_AreSubsetsOfTheBuiltinNames_AndDisjoint()
    {
        Assert.Subset((ISet<string>)BashBuiltins.Names, new HashSet<string>(BashBuiltins.RunAsSource));
        Assert.Subset((ISet<string>)BashBuiltins.Names, new HashSet<string>(BashBuiltins.CallStackBuiltins));
        Assert.Empty(BashBuiltins.RunAsSource.Intersect(BashBuiltins.CallStackBuiltins));
    }

    /// <summary>
    /// A builtin in neither group is looked up at run time as <c>Invoke-Bash&lt;Name&gt;</c>; the static emitter must reach
    /// that same command, so the literal and the run-time call behave alike. A builtin the emitter gives its own form
    /// (a new <c>else if (cmd0 == …)</c> in PsEmitter) fails here until it joins <see cref="BashBuiltins.RunAsSource"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllBuiltins))]
    public void Builtin_NotRunAsSource_StaticFormIsTheRunTimeCommand(string name)
    {
        if (BashBuiltins.RunAsSource.Contains(name) || BashBuiltins.CallStackBuiltins.Contains(name)) return;
        var ps = PsEmitter.Transpile(name + " x");
        Assert.NotNull(ps);
        Assert.Contains("Invoke-Bash" + name + " ", ps, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("eval", new[] { "echo hi" }, "eval 'echo hi'")]
    [InlineData("set", new[] { "--", "a b", "it's" }, "set '--' 'a b' 'it'\\''s'")]
    [InlineData(".", new string[0], ".")]
    public void SourceFor_QuotesEveryOperand(string name, string[] operands, string expected)
        => Assert.Equal(expected, BashBuiltins.SourceFor(name, operands));

    [Fact]
    public void SourceFor_RefusesANameTheEmitterDoesNotImplement()
        => Assert.Throws<ArgumentException>(() => BashBuiltins.SourceFor("echo", ["x"]));

    /// <summary>
    /// Loop control named at run time is told what it does at the call site (behavior vs bash:
    /// Differential DynamicBuiltinDifferentialTests.LoopControl_NamedAtRunTime_LeavesOnlyALoopInReach); a unit that names
    /// no <c>break</c>/<c>continue</c> keeps the plain lookup.
    /// </summary>
    [Theory]
    [InlineData("c=break; for i in 1; do $c; done", " -LoopMode 0)")]
    [InlineData("c=break; $c", " -LoopMode 1)")]
    [InlineData("c=break; while :; do eval '$c'; done", " -LoopMode 0)")]   // static eval text, inline in the loop
    [InlineData("run() { \"$@\"; }; for i in 1; do run continue; done", "& (Resolve-BashCommand $__dyn[0] -LoopMode 1)")]
    public void Transpile_UnitNamingLoopControl_PassesTheCallSitesLoopMode(string bash, string expected)
        => Assert.Contains(expected, PsEmitter.Transpile(bash));

    /// <summary>
    /// A run-time name with only redirections may be <c>exec</c> (permanent redirects): both forms behind one branch. Not
    /// in a pipe-target stage: a subshell, whose input reached only the branch block (behavior:
    /// Differential PipelineLastStageStatusTests, DynamicBuiltinDifferentialTests.SourceExecExit_NamedAtRunTime).
    /// </summary>
    [Theory]
    [InlineData("c=exec; $c 3>f", true)]
    [InlineData("printf a | $c >/dev/null", false)]
    public void Transpile_RunTimeNameWithOnlyRedirects_BranchesOnExec(string bash, bool branches)
        => Assert.Equal(branches, PsEmitter.Transpile(bash)!.Contains("-ceq 'exec'", StringComparison.Ordinal));

    [Theory]
    [InlineData("$x echo hi")]
    [InlineData("for i in 1; do $c; break; done")]   // the keyword itself is not data
    public void Transpile_UnitNamingNoLoopControl_KeepsThePlainLookup(string bash)
        => Assert.DoesNotContain("-LoopMode", PsEmitter.Transpile(bash));
}
