using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>A sourced file's pipeline output ended each record with CR LF (the fused batch), so <c>. f | wc -c</c> was off.</summary>
public class SourcedPipelineOutputTests
{
    [SkippableFact]
    public Task SourcedPipeline_ByteCount_MatchesBash() => AssertOracle.EqualAsync(
        "d=$(mktemp -d); echo 'seq 1 3 | tail -1' > $d/f.sh; . $d/f.sh | wc -c; x=$(. $d/f.sh); echo \"[$x]\"; rm -rf $d");

    [SkippableFact]
    public Task EvalPipeline_ByteCount_MatchesBash() => AssertOracle.EqualAsync(
        "eval 'seq 1 3 | tail -1' | wc -c; eval 'printf \"a\\nb\\n\" | sort -r' | wc -c");
}
