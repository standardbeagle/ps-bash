using PsBash.Testing;
using Xunit;

namespace PsBash.Canary.Tests;

/// <summary>
/// A test that writes the process environment runs alone and restores what it changed (rule:
/// .claude/rules/testing.md; scope: <see cref="ProcessEnv"/>). Every class here runs alone already
/// (<c>CollectionBehavior(DisableTestParallelization = true)</c>, CanaryParallelization.cs), so no collection.
/// </summary>
public class ProcessEnvironmentGuardTests
{
    /// <summary>
    /// Scripts are not scanned: no C# literal here writes <c>$env:</c>; the canary scripts are corpus files.
    /// </summary>
    [Fact]
    public void EveryClassThatWritesTheProcessEnvironment_RunsAlone_AndRestoresIt()
    {
        var violations = ProcessEnvironmentGuard.Violations(
            typeof(ProcessEnvironmentGuardTests).Assembly,
            scanScripts: false,
            // Puts the SDK's Modules dir on PSModulePath for every in-process runspace, once, and keeps it.
            typeof(CanaryPwshFixture));
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }
}
