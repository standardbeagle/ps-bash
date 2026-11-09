using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>Without <c>shopt -s lastpipe</c> the last pipeline stage is a subshell, so <c>read</c> there sets nothing; with it, the variable survives.</summary>
public class PipelineLastStageReadTests
{
    [Fact]
    public Task Read_AsLastStage_DoesNotSetVariable() => AssertOracle.EqualAsync(
        "echo hi | read line\necho \"line=$line\"\nprintf 'a\\nb\\n' | read -r x y; echo \"x=$x\"\nf() { echo hi | read v; echo \"v=$v\"; }; f");

    [Fact]
    public Task Read_AsLastStage_WithLastpipe_SetsVariable() => AssertOracle.EqualAsync(
        "shopt -s lastpipe\necho hi | read line\necho \"line=$line\"");

    [Fact]
    public Task WhileAsLastStage_WithLastpipe_KeepsAssignments() => AssertOracle.EqualAsync(
        "shopt -s lastpipe\ni=0\nseq 3 | while read line; do (( i++ )); done\necho i=$i");

    [Fact]
    public Task WhileAsLastStage_WithoutLastpipe_IsIsolated() => AssertOracle.EqualAsync(
        "i=0\nseq 3 | while read line; do (( i++ )); done\necho i=$i");
}
