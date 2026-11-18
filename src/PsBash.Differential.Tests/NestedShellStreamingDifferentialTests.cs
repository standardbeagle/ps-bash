using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A nested shell (<c>bash -c</c>, <c>sh -c</c>, <c>bash script.sh</c>) is a live process in bash: its output
/// reaches the next pipeline stage while it runs, its stdout and stderr keep their order under <c>2&gt;&amp;1</c>, and
/// piped input reaches it as it is produced. ps-bash collected all of the child's output (and all of its piped
/// input) and handed it over at exit.
/// <para>
/// Determinism: the streaming cases use a HANDSHAKE, not timing. One side waits (bounded, 10 s) for a file the other
/// side creates only after it has received the first line, then reports whether it saw it. A streamed pipe sees the
/// file within milliseconds; a collected one never does, whatever the machine speed.
/// </para>
/// </summary>
public class NestedShellStreamingDifferentialTests
{
    /// <summary>Wait (at most 10 s) until <c>[ TEST seen ]</c> holds, then say whether it did.</summary>
    private static string WaitForSeen(string test) =>
        $"i=0; while [ ! {test} seen ] && [ $i -lt 100 ]; do sleep 0.1; i=$((i+1)); done; " +
        $"if [ {test} seen ]; then echo b-after-other-side-read-a; else echo b-other-side-never-saw-a; fi";

    /// <summary>
    /// Under <c>2&gt;&amp;1</c> the child's stderr lands between its stdout lines, not all before them. The child's two
    /// streams reach ps-bash on two pipes, so lines written in the same instant have no order to keep; the pauses
    /// make the order the child wrote them in observable.
    /// </summary>
    [SkippableFact]
    public async Task Differential_NestedShell_StderrInterleavesWithStdout_Under2To1()
    {
        await AssertOracle.EqualAsync(
            "bash -c 'echo out1; sleep 0.3; echo err1 >&2; sleep 0.3; echo out2; sleep 0.3; echo err2 >&2' 2>&1; echo rc=$?",
            timeout: TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// <c>bash build.sh | tee log</c>: the log holds the child's first line while the child is still running (tee
    /// writes each line as it arrives, so the child itself can see it).
    /// </summary>
    [SkippableFact]
    public async Task Differential_NestedShell_StdoutStreamsToTheNextStage()
    {
        await AssertOracle.EqualAsync(
            "cd \"$(mktemp -d)\"\n" +
            "bash -c 'echo a; " + WaitForSeen("-s") + "' | tee seen\n" +
            "echo rc=$?",
            timeout: TimeSpan.FromSeconds(40));
    }

    /// <summary>Piped input reaches the child as the producer writes it, not when the producer ends.</summary>
    [SkippableFact]
    public async Task Differential_NestedShell_PipedStdinStreamsIntoTheChild()
    {
        await AssertOracle.EqualAsync(
            "cd \"$(mktemp -d)\"\n" +
            "{ echo a; " + WaitForSeen("-f") + "; } | bash -c 'while read -r l; do echo \"got $l\"; touch seen; done'\n" +
            "echo rc=$?",
            timeout: TimeSpan.FromSeconds(40));
    }

    /// <summary>
    /// An endless producer into a child that reads one line: the child exits, the producer stops (bash: SIGPIPE).
    /// ps-bash collected the producer's output before starting the child, so this never finished.
    /// </summary>
    [SkippableFact]
    public async Task Differential_NestedShell_EndlessProducer_ChildReadsOneLine()
    {
        await AssertOracle.EqualAsync(
            "yes | bash -c 'head -1'; echo rc=$?",
            timeout: TimeSpan.FromSeconds(30));
    }

    /// <summary>An endless child cut off by <c>head</c>: the child is killed, the pipeline ends.</summary>
    [SkippableFact]
    public async Task Differential_NestedShell_EndlessChild_HeadStopsIt()
    {
        await AssertOracle.EqualAsync(
            "bash -c 'while :; do echo y; done' | head -2; echo done",
            timeout: TimeSpan.FromSeconds(30));
    }

    /// <summary>Piped bytes reach the child exactly: an unterminated record gets no newline added.</summary>
    [SkippableFact]
    public async Task Differential_NestedShell_UnterminatedPipedInput_StaysUnterminated()
    {
        await AssertOracle.EqualAsync(
            "printf 'x' | bash -c 'cat; echo \"|\"'",
            timeout: TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// A child that closes its stdout and stderr and keeps running: bash waits for the process, not its pipes, and
    /// reports the child's own status. ps-bash ended the wait at pipe EOF and reported 124 (killing the child).
    /// </summary>
    [SkippableFact]
    public async Task Differential_NestedShell_ClosedOutput_StatusIsTheChildsOwnExit()
    {
        await AssertOracle.EqualAsync(
            "bash -c 'exec >&- 2>&-; sleep 3; exit 3'; echo rc=$?",
            timeout: TimeSpan.FromSeconds(60));
    }

    /// <summary>With no -c and no script operand the pipe is still the PROGRAM (collected, then run).</summary>
    [SkippableFact]
    public async Task Differential_NestedShell_ProgramOnStdin_StillRuns()
    {
        await AssertOracle.EqualAsync(
            "printf 'echo one\\necho two >&2\\nexit 3\\n' | sh 2>&1; echo rc=$?",
            timeout: TimeSpan.FromSeconds(30));
    }
}
