namespace PsBash.Spec.Tests;

/// <summary>
/// The environment a spec case runs with. REGRESSION (fleet g17): OLDPWD came from however the test process was started,
/// so builtin-cd "cd - without OLDPWD" passed here (inherited) and failed on beagle-ab (ssh, none).
/// </summary>
public class InHostExecutorEnvironmentTests
{
    [Fact]
    public void CaseEnvironment_OldpwdIsTheCaseDirectory_WhateverTheRunnerInherited()
    {
        var env = InHostExecutor.CaseEnvironment(@"C:\case-dir");
        var oldpwd = env.Where(e => string.Equals(e.Key, "OLDPWD", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(oldpwd);
        Assert.Equal(@"C:\case-dir", oldpwd[0].Value);
    }
}
