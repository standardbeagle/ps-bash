using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// R22 differential oracle for grep. Each script runs in real bash AND ps-bash
/// and the canonicalized stdout/stderr/exit are diffed against GNU grep.
///
/// Covered regressions:
///   - repeated <c>-e A -e B</c> crashed the PowerShell binder ("parameter 'E'
///     is specified more than once")
///   - the <c>-ie</c> / <c>-ve</c> / <c>-we</c> bundles: <c>-ve</c> was taken
///     as <c>-Verbose</c>, <c>-ie</c> / <c>-we</c> reported "invalid option"
///   - <c>-A</c>/<c>-B</c>/<c>-C</c> were ignored on pipeline input
/// </summary>
public class GrepDifferentialTests
{
    // 30 s: every ps-bash spawn starts a fresh host that is ~8 s cold on a
    // loaded Windows box (see XargsBasenameDifferentialTests).
    private static Task Eq(string script) =>
        AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(30));

    [SkippableFact]
    public Task Grep_RepeatedDashE_OrPatterns()
        => Eq("printf 'apple\\nbanana\\ncherry\\n' | grep -e apple -e cherry");

    [SkippableFact]
    public Task Grep_DashIeBundle_IgnoreCase()
        => Eq("printf 'Apple\\nbanana\\nAPPLE\\n' | grep -ie apple");

    [SkippableFact]
    public Task Grep_DashVeBundle_Inverts()
        => Eq("printf 'apple\\nbanana\\ncherry\\n' | grep -ve apple");

    [SkippableFact]
    public Task Grep_DashWeBundle_WordRegexp()
        => Eq("printf 'cat\\ncatastrophe\\nwildcat\\n' | grep -we cat");

    [SkippableFact]
    public Task Grep_PipelineContextA_EmitsAfter()
        => Eq("printf 'a\\nb\\nc\\n' | grep -A1 a");

    [SkippableFact]
    public Task Grep_PipelineContextB_EmitsBefore()
        => Eq("printf 'a\\nb\\nc\\n' | grep -B1 c");

    [SkippableFact]
    public Task Grep_PipelineContextC_EmitsAround()
        => Eq("printf 'a\\nb\\nc\\nd\\ne\\n' | grep -C1 c");
}
