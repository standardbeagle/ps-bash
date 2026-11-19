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
    [SkippableTheory]
    [InlineData("for c in 'sed s/a/b/' 'head -1' 'head -c 3' 'grep -c .' 'cat' 'tail -1' 'sort'; do printf '%d' x 2>&1 >/dev/null | $c >/dev/null; echo \"$c s=$?\"; done")]
    // The failing stage is not the first one.
    [InlineData("echo hi | { cat; false; } | sed 's/h/j/'; echo \"s=$?\"; echo hi | grep zzz | head -1; echo \"s=$?\"")]
    // The last stage's own failure still counts.
    [InlineData("echo a | sed 'q5'; echo \"s=$?\"; echo a | head -c -x 2>/dev/null; echo \"s=$?\"")]
    public Task Status_IsTheLastStages(string script) => AssertOracle.EqualAsync(script);

    /// <summary>
    /// A stage after the first that fails before the stages ahead of it finish (a usage error found while parsing, in
    /// BeginProcessing) kept its status only until those stages set theirs: <c>echo x | grep --bogus</c> was 0 (GNU 2).
    /// Primitive: <c>BashCmdlet</c> re-asserts the status a stage set once the engine disposes it (guard
    /// <c>BashCmdletBaseGuardTests</c>).
    /// </summary>
    [SkippableTheory]
    [InlineData("for c in grep sed tail tee mkdir rmdir touch date readlink realpath chmod sleep uname mktemp tr printf; do "
        + "case $c in grep) echo x | grep --bogus ;; sed) echo x | sed --bogus ;; tail) echo x | tail --bogus ;; "
        + "tee) echo x | tee --bogus ;; mkdir) echo x | mkdir --bogus ;; rmdir) echo x | rmdir --bogus ;; "
        + "touch) echo x | touch --bogus ;; date) echo x | date --bogus ;; readlink) echo x | readlink --bogus ;; "
        + "realpath) echo x | realpath --bogus ;; chmod) echo x | chmod --bogus ;; sleep) echo x | sleep --bogus ;; "
        + "uname) echo x | uname --bogus ;; mktemp) echo x | mktemp --bogus ;; tr) echo x | tr --bogus ;; "
        + "printf) echo x | printf --bogus ;; esac >/dev/null 2>&1; echo \"$c s=$?\"; done")]
    // The last stage's success wins over a failed stage ahead of it, whenever each one finishes.
    [InlineData("cat nosuch 2>/dev/null | seq 1; echo \"s=$?\"; cat nosuch 2>/dev/null | printf x; echo \" s=$?\"; "
        + "false | tee /dev/null; echo \"s=$?\"; cat nosuch 2>/dev/null | tail -1; echo \"s=$?\"")]
    // Through a redirection, a capture, and a stage after it that succeeds.
    [InlineData("cd \"$(mktemp -d)\"; echo x | grep --bogus > f 2>/dev/null; echo \"s=$?\"; v=$(echo x | grep --bogus 2>/dev/null); "
        + "echo \"s=$?\"; echo x | grep --bogus 2>&1 | cat >/dev/null; echo \"s=$?\"; echo x | grep --bogus 2>/dev/null | wc -l; "
        + "echo \"s=$?\"; seq 3 | grep 5; echo \"s=$?\"; seq 3 | grep 2 | grep 7; echo \"s=$?\"")]
    public Task Status_OfALaterStage_SurvivesTheStagesAheadOfIt(string script) => AssertOracle.EqualAsync(script);

    /// <summary>The usage error itself: grep's usage line and hint, tr's hint at status 1 (it was 2 with no hint).</summary>
    [SkippableTheory]
    [InlineData("echo x | grep --bogus 2>&1; echo \"s=$?\"; echo x | tr --bogus 2>&1; echo \"s=$?\"")]
    public Task UsageError_InAPipeline_MessageAndStatus(string script) => AssertOracle.EqualAsync(script);

    /// <summary><c>comm -</c> reads the pipeline. It refused pipeline input and took <c>-</c> for a file name.</summary>
    [SkippableTheory]
    [InlineData("cd \"$(mktemp -d)\"; printf 'a\\nc\\n' > f; printf 'a\\nb\\n' | comm - f; echo \"s=$?\"; printf 'a\\nb\\n' | comm f -; echo \"s=$?\"")]
    [InlineData("cd \"$(mktemp -d)\"; printf 'a\\nc\\n' > f; printf 'a\\0b\\0' | comm -z - f | od -An -c; printf 'b\\na\\n' | comm --check-order - f 2>/dev/null; echo \"s=$?\"")]
    // GNU opens stdin once: the second `-` fails and reads as empty, and the status is 1.
    [InlineData("echo a | comm - - 2>/dev/null; echo \"s=$?\"")]
    public Task Comm_DashIsThePipeline(string script) => AssertOracle.EqualAsync(script);
}
