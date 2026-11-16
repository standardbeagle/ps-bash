using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// A pipeline bounded by the wall clock goes through <see cref="BoundedPipeline"/>, and its class runs in
/// <see cref="WallClockBoundCollection"/> (alone, after the parallel collections). A hand-rolled
/// <c>Task.Run(() =&gt; pwsh...Invoke())</c> + <c>Wait(bound)</c> in a parallel class measures the other classes
/// too: on Windows every script-to-.NET method call waits on the process-wide AMSI lock that their script
/// compiles hold, which failed <c>GrepStreamingTests.Context_Before_ManyRecords_OnlyTheWindow</c> at its 60 s
/// bound in 3 of 4 gate runs (see <see cref="WallClockBoundCollection"/>).
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent.
/// </summary>
public class WallClockBoundGuardTests
{
    private static string TestProjectDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
                return Path.Combine(dir.FullName, "src", "PsBash.Cmdlets.Tests");
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repo root (the dir with CLAUDE.md) not found.");
    }

    private static IEnumerable<(string Name, string Text)> Sources()
    {
        foreach (var file in Directory.EnumerateFiles(TestProjectDir(), "*.cs", SearchOption.TopDirectoryOnly))
            yield return (Path.GetFileName(file), File.ReadAllText(file));
    }

    /// <summary>A PowerShell invocation started so that the caller can wait on it with a timeout.</summary>
    internal static readonly Regex HandRolledAsyncInvoke = new(
        @"Task\.Run\s*\(\s*\(\s*\)\s*=>\s*\w+(?:\.\w+)*\.AddScript\b|\.BeginInvoke\s*\(",
        RegexOptions.Compiled);

    internal const string CollectionAttribute = "[Collection(WallClockBoundCollection.Name)]";

    [Fact]
    public void Collection_DisablesParallelization()
    {
        var definition = typeof(WallClockBoundCollection).GetCustomAttribute<CollectionDefinitionAttribute>();

        Assert.NotNull(definition);
        Assert.True(definition!.DisableParallelization,
            "WallClockBoundCollection must set DisableParallelization = true: a wall-clock bound measured beside "
            + "other classes includes their hold on the process-wide AMSI lock.");
    }

    [Fact]
    public void TimedPipelines_GoThroughBoundedPipeline()
    {
        var offenders = Sources()
            .Where(s => s.Name is not ("BoundedPipeline.cs" or "WallClockBoundGuardTests.cs"))
            .Where(s => HandRolledAsyncInvoke.IsMatch(s.Text))
            .Select(s => s.Name)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Run a pipeline that needs a time bound with BoundedPipeline.Invoke (it stops the pipeline on a "
            + "timeout) instead of a hand-rolled Task.Run/BeginInvoke + Wait: " + string.Join(", ", offenders));
    }

    [Fact]
    public void ClassesUsingBoundedPipeline_AreInTheWallClockCollection()
    {
        var users = Sources()
            .Where(s => s.Name is not ("BoundedPipeline.cs" or "WallClockBoundGuardTests.cs"))
            .Where(s => s.Text.Contains("BoundedPipeline.Invoke(", StringComparison.Ordinal))
            .ToArray();

        // Vacuous-pass guard: the scan must see the classes it exists for.
        Assert.Contains(users, u => u.Name == "GrepStreamingTests.cs");

        var offenders = users
            .Where(u => !u.Text.Contains(CollectionAttribute, StringComparison.Ordinal))
            .Select(u => u.Name)
            .ToArray();
        Assert.True(offenders.Length == 0,
            "A class that bounds a pipeline by the wall clock must be marked " + CollectionAttribute
            + " so it does not run beside other classes: " + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("var task = Task.Run(() => pwsh.AddScript(script).Invoke());", true)]
    [InlineData("var run = Task.Run( () => _fixture.Pwsh.AddScript(s).Invoke());", true)]
    [InlineData("var async = pwsh.BeginInvoke();", true)]
    [InlineData("var r = pwsh.AddScript(script).Invoke();", false)]
    [InlineData("var err = Task.Run(() => child.StandardError!.ReadToEnd());", false)]
    public void HandRolledAsyncInvoke_Detects(string line, bool expected)
    {
        Assert.Equal(expected, HandRolledAsyncInvoke.IsMatch(line));
    }
}
