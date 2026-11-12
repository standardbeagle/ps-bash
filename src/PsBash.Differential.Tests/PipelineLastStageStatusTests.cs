using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A pipeline's status is its LAST stage's. Every stage's BeginProcessing runs before any record flows, so a cmdlet
/// that set its 0 only there (sed, streaming head) let a failure of an earlier stage leak through:
/// <c>printf %d x 2&gt;&amp;1 | sed …</c> was 1.
/// </summary>
public class PipelineLastStageStatusTests
{
    [Theory]
    [InlineData("for c in 'sed s/a/b/' 'head -1' 'head -c 3' 'grep -c .' 'cat' 'tail -1' 'sort'; do printf '%d' x 2>&1 >/dev/null | $c >/dev/null; echo \"$c s=$?\"; done")]
    // The failing stage is not the first one.
    [InlineData("echo hi | { cat; false; } | sed 's/h/j/'; echo \"s=$?\"; echo hi | grep zzz | head -1; echo \"s=$?\"")]
    // The last stage's own failure still counts.
    [InlineData("echo a | sed 'q5'; echo \"s=$?\"; echo a | head -c -x 2>/dev/null; echo \"s=$?\"")]
    public Task Status_IsTheLastStages(string script) => AssertOracle.EqualAsync(script);

    /// <summary><c>comm -</c> reads the pipeline. It refused pipeline input and took <c>-</c> for a file name.</summary>
    [Theory]
    [InlineData("cd \"$(mktemp -d)\"; printf 'a\\nc\\n' > f; printf 'a\\nb\\n' | comm - f; echo \"s=$?\"; printf 'a\\nb\\n' | comm f -; echo \"s=$?\"")]
    [InlineData("cd \"$(mktemp -d)\"; printf 'a\\nc\\n' > f; printf 'a\\0b\\0' | comm -z - f | od -An -c; printf 'b\\na\\n' | comm --check-order - f 2>/dev/null; echo \"s=$?\"")]
    // GNU opens stdin once: the second `-` fails and reads as empty, and the status is 1.
    [InlineData("echo a | comm - - 2>/dev/null; echo \"s=$?\"")]
    public Task Comm_DashIsThePipeline(string script) => AssertOracle.EqualAsync(script);
}
