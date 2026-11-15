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

    /// <summary>
    /// REGRESSION: a run started from a ps-bash `-c` shell inherited PSBASH_DASH_C=1, so every in-host case ran as under
    /// `bash -c` (a bad substitution ended the script) and disagreed with its launcher run. The launcher never passes its
    /// own mark on (CaptureLauncherEnvironment); neither does the runner.
    /// </summary>
    [Fact]
    public void CaseEnvironment_NeverCarriesTheLauncherDashCMark()
    {
        var before = Environment.GetEnvironmentVariable("PSBASH_DASH_C");
        Environment.SetEnvironmentVariable("PSBASH_DASH_C", "1");
        try
        {
            var env = InHostExecutor.CaseEnvironment(@"C:\case-dir");
            Assert.DoesNotContain(env, e => string.Equals(e.Key, "PSBASH_DASH_C", StringComparison.OrdinalIgnoreCase));
        }
        finally { Environment.SetEnvironmentVariable("PSBASH_DASH_C", before); }
    }
}
