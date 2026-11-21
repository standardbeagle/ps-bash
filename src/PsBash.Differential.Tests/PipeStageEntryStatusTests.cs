using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Every pipe stage is a subshell that starts with the status from before the pipeline: a bare <c>exit</c> or
/// <c>return</c> there, or <c>$?</c>, sees it (<c>false; { exit; } | cat</c> → PIPESTATUS <c>1 0</c>). ps-bash printed
/// <c>0 0</c>: the stages started ahead of a stage's body had reset (a cmdlet's start, Invoke-BashPipeStage) or set
/// (<c>true; false | { exit; }</c> was 1, bash 0) the status first. Fix: PsEmitter.SeedStageStatus.
/// </summary>
public class PipeStageEntryStatusTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // Reads PIPESTATUS, so every stage is an Invoke-BashPipeStage.
    [SkippableFact]
    public async Task Differential_BareExitInPipeStage_ExitsWithTheStatusBeforeThePipeline()
        => await AssertOracle.EqualAsync(
            "false; { exit; } | cat; echo \"a ${PIPESTATUS[*]}\"; false; ( exit ) | cat; echo \"b ${PIPESTATUS[*]}\"\n"
            + "false; exit | cat; echo \"c ${PIPESTATUS[*]}\"; false; { true; exit; } | cat; echo \"d ${PIPESTATUS[*]}\"\n"
            + "(exit 3); { exit; } | { exit; }; echo \"e ${PIPESTATUS[*]}\"; false; (exit) | (exit) | cat; echo \"f ${PIPESTATUS[*]}\"\n"
            + "f() { return; }; false; f | cat; echo \"g ${PIPESTATUS[*]}\"",
            timeout: Timeout);

    // No PIPESTATUS and no pipefail: the plain pipeline, whose last stage's status is $?.
    [SkippableFact]
    public async Task Differential_BareExitInLastPipeStage_ExitsWithTheStatusBeforeThePipeline()
        => await AssertOracle.EqualAsync(
            "true; false | { exit; }; echo \"a $?\"; false; true | { exit; }; echo \"b $?\"\n"
            + "false; true | ( exit ); echo \"c $?\"; false; { exit; } | { exit; }; echo \"d $?\"",
            timeout: Timeout);

    [SkippableFact]
    public async Task Differential_StatusReadInPipeStage_IsTheStatusBeforeThePipeline()
        => await AssertOracle.EqualAsync(
            "false; { echo \"a $?\"; } | cat; false; true | { echo \"b $?\"; }\n"
            + "false; for i in 1; do echo \"c $?\"; done | cat\n"
            + "g() { echo \"d $?\"; }; false; g | cat; false; true | g",
            timeout: Timeout);

    // A simple command stage's words: under PIPESTATUS each stage is an Invoke-BashPipeStage, whose start reset the
    // status before the words were expanded (`false; echo $? | cat` printed 0).
    [SkippableFact]
    public async Task Differential_StatusWordOfASimpleStage_IsTheStatusBeforeThePipeline()
        => await AssertOracle.EqualAsync(
            "false; echo \"a $?\" | cat; echo \"ps=${PIPESTATUS[*]}\"; false; true | echo \"b $?\"; (exit 3); echo \"c $?\" | cat | cat",
            timeout: Timeout);

    [SkippableFact]
    public async Task Differential_BareExitInPipeStage_UnderPipefail()
        => await AssertOracle.EqualAsync(
            "set -o pipefail; false; { exit; } | cat; echo \"a $?\"; false; cat </dev/null | { exit; }; echo \"b $?\"\n"
            + "set +o pipefail; false; { exit; } | cat; echo \"c $?\"",
            timeout: Timeout);
}
