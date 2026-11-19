using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A stage that has what it needs (<c>head</c>) inside a pipeline whose stages report their own status (a script that
/// reads PIPESTATUS, or sets pipefail): every stage is an Invoke-BashPipeStage, and head's request to stop the stages
/// before it came out of the stage's steppable pipeline raw. The engine could not find head in the outer pipeline, so it
/// ended NONE of the stages: the ones after head lost their end (<c>… | head -c 3 | od -c</c> printed nothing; it looked
/// like <c>printenv -0</c> printing nothing) and no stage recorded its status (<c>nosuchcmd 2&gt;&amp;1 | head -1</c> gave
/// PIPESTATUS <c>0 0</c>, bash <c>127 0</c>). The stage now asks again as itself (InvokeBashPipeStageCommand).
/// Writers stopped early are left out of the statuses compared: bash reports 141 (SIGPIPE) there, ps-bash 0
/// (intentional-differences.md).
/// </summary>
public class PipeStageUpstreamStopDifferentialTests
{
    [SkippableFact]
    public Task PipeStatus_StagesAfterHead_StillEnd() => AssertOracle.EqualAsync(
        "printf 'ab\\0cd\\0' | head -c 3 | od -c; echo \"a ${PIPESTATUS[*]}\"\n"
        + "printf 'ab\\0cd\\0' | head -c 3 | od -c | cat; echo \"b ${PIPESTATUS[*]}\"\n"
        + "printenv -0 | head -c 3 | wc -c; echo \"c ${PIPESTATUS[*]:1}\"\n"
        + "printenv -0 | head -c 3 | od -c | wc -l; echo \"d ${PIPESTATUS[*]:1}\"\n"
        + "seq 5 | head -n 2 | wc -l; echo \"e ${PIPESTATUS[*]:1}\"\n"
        + "seq 5 | head -n 2 | sort -r | uniq -c; echo \"f ${PIPESTATUS[*]:1}\"\n"
        + "x=$(seq 9 | head -n 2 | tr '\\n' ,); echo \"g $x\"\n");

    [SkippableFact]
    public Task PipeStatus_NotFoundStageStoppedByHead_Is127() => AssertOracle.EqualAsync(
        "{ nosuchcmd 2>&1 | head -1; echo \"a ${PIPESTATUS[*]}\"; } | sed 's/.*nosuchcmd/nosuchcmd/'\n"
        + "nosuchcmd 2>&1 | head -1 | sed 's/.*nosuchcmd/nosuchcmd/'; echo \"b ${PIPESTATUS[*]}\"\n"
        + "nosuchcmd 2>&1 | head -1 >/dev/null; echo \"c ${PIPESTATUS[*]}\"\n");

    [SkippableFact]
    public Task Pipefail_StagesAfterHead_StillEnd() => AssertOracle.EqualAsync(
        "set -o pipefail\n"
        + "printf 'ab\\0cd\\0' | head -c 3 | od -c | head -1; echo \"a $?\"\n"
        + "nosuchcmd 2>/dev/null | head -1; echo \"b $?\"\n");
}
