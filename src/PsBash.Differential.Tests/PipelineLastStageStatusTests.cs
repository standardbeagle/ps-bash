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

    /// <summary>
    /// The re-assert above must only undo a stage AHEAD. It undid two later statuses: a wrapper's (<c>command</c>,
    /// <c>env</c>, <c>flock</c> set 0, then ran a command that set its own status as itself, and re-asserted the 0:
    /// <c>echo x | command ls /nosuch</c> gave 0, GNU 2; <c>command ./prog</c> lost the program's status; pipefail's
    /// stage wrapper re-asserted its 0 under an ERR trap) and one set by something that is not a cmdlet (a program, the
    /// command-not-found handler: <c>echo x | grep zzz | nosuchcmd</c> gave grep's 1, not 127).
    /// Primitives: <c>FileSystemHelpers.AdoptLastExitCode</c>; <c>BashCmdlet</c> re-asserts only a cmdlet's overwrite.
    /// </summary>
    [SkippableTheory]
    [InlineData("cd \"$(mktemp -d)\"; : > e; echo x | command grep -q zzz e; echo \"command s=$?\"; "
        + "printf x | grep zzz | command grep -q zzz e; echo \"command3 s=$?\"; "
        + "echo x | eval 'grep -q zzz e'; echo \"eval s=$?\"; echo x | grep zzz | command true; echo \"true s=$?\"")]
    [InlineData("cd \"$(mktemp -d)\"; printf '#!/bin/sh\\ncat >/dev/null\\nexit 7\\n' > seven; printf '#!/bin/sh\\necho x\\nexit 5\\n' > five; "
        + "chmod +x seven five; echo x | grep zzz | nosuchcmd 2>/dev/null; echo \"notfound s=$?\"; echo x | grep zzz | ./seven; "
        + "echo \"native s=$?\"; echo x | command ./seven; echo \"command-native s=$?\"; ./five | grep zzz; echo \"five s=$?\"; "
        + "./five | grep --bogus 2>/dev/null; echo \"five-bogus s=$?\"")]
    [InlineData("trap 'echo \"err [${PIPESTATUS[@]}]\"' ERR; echo B | grep zzz; true | (exit 3) | cat; echo \"s=$?\"")]
    public Task Status_OfALaterStage_IsNotUndone(string script) => AssertOracle.EqualAsync(script);

    /// <summary>
    /// A middle stage's status (fixed by 45d91995, pinned here): a failed stage with stages after it, a program on
    /// either side, through a redirection, under pipefail.
    /// </summary>
    private const string FiveAndQ = "cd \"$(mktemp -d)\" || exit; printf '#!/bin/sh\\nexit 5\\n' > p.sh; "
        + "printf '#!/bin/sh\\necho x\\nexit 5\\n' > q.sh; chmod +x p.sh q.sh; ";

    [SkippableTheory]
    [InlineData(FiveAndQ + "./p.sh | grep --bogus 2>/dev/null | cat; echo \"2 rc=$?\"; "
        + "echo x | grep --bogus > out 2>/dev/null; echo \"3 rc=$?\"; ./p.sh | grep x > out; echo \"4 rc=$?\"; "
        + "echo x | grep --bogus 2>/dev/null | cat > out; echo \"5 rc=$?\"; ./p.sh | cat | grep --bogus > out 2>/dev/null; echo \"6 rc=$?\"; "
        + "./q.sh | grep zzz > out; echo \"7 rc=$?\"; ./q.sh | grep x | cat > out; echo \"8 rc=$?\"")]
    [InlineData(FiveAndQ + "./q.sh | grep --bogus 2>/dev/null | wc -l; echo \"9 rc=$?\"; echo x | grep --bogus 2>/dev/null | ./p.sh; echo \"10 rc=$?\"; "
        + "./q.sh | grep --bogus 2>/dev/null | ./p.sh > out; echo \"11 rc=$?\"; ./q.sh | ./p.sh | grep --bogus > out 2>/dev/null; echo \"12 rc=$?\"; "
        + "./q.sh | grep x | ./p.sh > out; echo \"13 rc=$?\"")]
    [InlineData(FiveAndQ + "set -o pipefail; ./q.sh | grep --bogus 2>/dev/null | cat > out; echo \"14 pf rc=$?\"; "
        + "echo x | grep --bogus 2>/dev/null | cat > out; echo \"15 pf rc=$?\"; echo x | grep x | ./p.sh > out; echo \"16 pf rc=$?\"")]
    public Task Status_OfAMiddleStage_IsBashs(string script) => AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(60));

    /// <summary>
    /// A last stage with a redirection is <c>Invoke-BashX | Invoke-BashRedirect</c>: the redirect is not a stage, so X is
    /// bash's last and its status beats a program, a subshell or <c>false</c> ahead that finished after X failed. It came
    /// out 5 (1 for <c>false</c>); bash 2 (<c>BashCmdlet</c>, <c>IStatusTransparentStage</c>).
    /// </summary>
    [SkippableTheory]
    [InlineData(FiveAndQ + "./p.sh | grep --bogus > out 2>/dev/null; echo \"1 rc=$?\"; ./p.sh | grep --bogus > out; echo \"b rc=$?\"; "
        + "./p.sh | tail --bogus > out 2>/dev/null; echo \"e rc=$?\"; false | grep --bogus > out 2>/dev/null; echo \"f rc=$?\"; "
        + "(exit 5) | grep --bogus 2>/dev/null >> out; echo \"s rc=$?\"; "
        + "./q.sh | grep x > out 2>/dev/null; echo \"ok rc=$?\"")]
    public Task Status_OfALastStage_WithARedirect_BeatsTheStagesAhead(string script) => AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(60));

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
