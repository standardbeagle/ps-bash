using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle tests for process substitution.
/// </summary>
public class ProcessSubstitutionDifferentialTests
{
    [SkippableFact]
    public async Task Differential_ProcessSub_DiffSeqIdentical_MatchesBash()
    {
        await AssertOracle.EqualAsync(
            "diff <(seq 1 10) <(seq 1 10)",
            timeout: TimeSpan.FromSeconds(20));
    }
}
