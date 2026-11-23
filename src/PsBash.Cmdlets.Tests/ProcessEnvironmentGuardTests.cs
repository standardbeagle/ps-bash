using System.Reflection;
using PsBash.Testing;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// A test that writes the process environment runs alone and restores what it changed (rule:
/// .claude/rules/testing.md; collection: <see cref="ProcessEnvironmentCollection"/>; scope: <see cref="ProcessEnv"/>).
/// REGRESSION (build3 full runs): GrepOptionsGnuTests set TERM/GREP_COLORS in parallel and nulled them after every
/// test, so <c>InvokeBashTputCommandTests.Tput_ViaAlias_ResolvesToCmdlet</c> and
/// <c>GrepGnuBehaviorTests.Color_AndLineBuffered_AreAccepted</c> failed on whichever run they overlapped.
/// </summary>
public class ProcessEnvironmentGuardTests
{
    [Fact]
    public void Collection_DisablesParallelization()
    {
        var definition = typeof(ProcessEnvironmentCollection).GetCustomAttribute<CollectionDefinitionAttribute>();
        Assert.NotNull(definition);
        Assert.True(definition!.DisableParallelization,
            "ProcessEnvironmentCollection must set DisableParallelization = true: its classes write the process "
            + "environment, which every other class reads.");
    }

    /// <summary>
    /// Scripts count: a runspace's <c>$env:X = ...</c> writes the process environment. Not walked: the module
    /// initializer (runs before any test), the fixtures (every class's identical PSModulePath/nested-exe setup) and
    /// ModuleInstallLayoutTests (its <c>$env:PSModulePath</c> script runs in a child pwsh).
    /// </summary>
    [Fact]
    public void EveryClassThatWritesTheProcessEnvironment_RunsAlone_AndRestoresIt()
    {
        var violations = ProcessEnvironmentGuard.Violations(typeof(ProcessEnvironmentGuardTests).Assembly, scanScripts: true,
            typeof(CanonicalTestEnvironment), typeof(PwshTestFixture), typeof(SharedPwshFixture),
            typeof(ModuleInstallLayoutTests));
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    /// <summary>The guard sees each shape it exists for (the samples are abstract: xunit never runs them).</summary>
    [Fact]
    public void Guard_FlagsUnsafeWriters_AndPassesScopedOnes()
    {
        IReadOnlyList<string> Of(Type t) => ProcessEnvironmentGuard.Violations(t.Assembly, [t], scanScripts: true);

        var direct = Of(typeof(DirectWriterSample));
        Assert.Contains(direct, v => v.Contains("writes the process environment directly"));
        Assert.Contains(direct, v => v.Contains("runs beside other classes"));

        var script = Of(typeof(UnrestoredScriptSample));
        Assert.Contains(script, v => v.Contains("$env:PSB_GUARD_SAMPLE") && v.Contains("ProcessEnv.Preserve"));

        Assert.Empty(Of(typeof(PreservedScriptSample)));
        Assert.Empty(Of(typeof(PreserveAllSample)));

        // A name built at run time can only be covered by restoring everything.
        Assert.Contains(Of(typeof(DynamicNameSample)), v => v.Contains("built at run time"));
        Assert.Contains(Of(typeof(DynamicItemSample)), v => v.Contains("built at run time"));
    }

    [Collection(ProcessEnvironmentCollection.Name)]
    public abstract class DynamicNameSample
    {
        [Fact]
        public void Writes()
        {
            using var env = ProcessEnv.Preserve("PSB_GUARD_SAMPLE");
            var name = "PSB_GUARD_SAMPLE";
            Script($"$env:{name} = 'seed'");
        }
    }

    [Collection(ProcessEnvironmentCollection.Name)]
    public abstract class DynamicItemSample
    {
        [Fact]
        public void Writes()
        {
            using var env = ProcessEnv.Preserve("PSB_GUARD_SAMPLE");
            var name = "PSB_GUARD_SAMPLE";
            Script($"Remove-Item Env:{name}");
        }
    }

    private static void Script(string script) => _ = script;

    public abstract class DirectWriterSample
    {
        [Fact] public void Writes() => Environment.SetEnvironmentVariable("PSB_GUARD_SAMPLE", "1");
    }

    [Collection(ProcessEnvironmentCollection.Name)]
    public abstract class UnrestoredScriptSample
    {
        [Fact] public void Writes() => Script("$env:PSB_GUARD_SAMPLE = '1'");
    }

    [Collection(ProcessEnvironmentCollection.Name)]
    public abstract class PreservedScriptSample
    {
        [Fact]
        public void Writes()
        {
            using var env = ProcessEnv.Preserve("PSB_GUARD_SAMPLE");
            Script("$env:PSB_GUARD_SAMPLE = '1'; Remove-Item env:PSB_GUARD_SAMPLE");
        }
    }

    [Collection(ProcessEnvironmentCollection.Name)]
    public abstract class PreserveAllSample
    {
        [Fact]
        public void Writes()
        {
            using var env = ProcessEnv.PreserveAll();
            Script("$env:PSB_GUARD_SAMPLE = '1'; ${env:PSB_GUARD_OTHER} = '2'");
        }
    }
}
