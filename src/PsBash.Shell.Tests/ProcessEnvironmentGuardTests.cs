using System.Reflection;
using PsBash.Testing;
using Xunit;

namespace PsBash.Shell.Tests;

/// <summary>
/// A test that writes the process environment runs alone and restores what it changed (rule:
/// .claude/rules/testing.md; collection: <see cref="ProcessEnvironmentCollection"/>; scope: <see cref="ProcessEnv"/>).
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
    /// Scripts are not scanned: this assembly's commands run in a spawned ps-bash, whose environment is its own.
    /// </summary>
    [Fact]
    public void EveryClassThatWritesTheProcessEnvironment_RunsAlone_AndRestoresIt()
    {
        var violations = ProcessEnvironmentGuard.Violations(typeof(ProcessEnvironmentGuardTests).Assembly, scanScripts: false);
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }
}
