using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A <c>while read</c> loop as a pipe stage reads each line as the producer writes it: the loop body runs while the
/// producer is still running. ps-bash ran the loop over the producer's COLLECTED output, after the producer had ended
/// (a PowerShell script block after a pipe only starts once its upstream finishes), and an endless producer
/// (<c>yes | while read …; break; done</c>) never ended at all.
/// <para>
/// Determinism: the streaming cases use a HANDSHAKE, not timing (as <see cref="NestedShellStreamingDifferentialTests"/>).
/// The producer waits (bounded, 10 s) for a file the loop body creates only after it has read the first line, then
/// reports whether it saw it. A streamed loop creates the file within milliseconds; a collected one never does.
/// </para>
/// </summary>
public class ReadLoopStreamingDifferentialTests
{
    private static Task Eq(string script) =>
        AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(40));

    /// <summary>Wait (at most 10 s) until <c>[ -f seen ]</c> holds, then say whether it did.</summary>
    private const string WaitForSeen =
        "i=0; while [ ! -f seen ] && [ $i -lt 100 ]; do sleep 0.1; i=$((i+1)); done; " +
        "if [ -f seen ]; then echo b-after-loop-read-a; else echo b-loop-never-saw-a; fi";

    /// <summary>The headline case: a brace group producing into a read loop.</summary>
    [SkippableFact]
    public Task Differential_ReadLoopStage_BraceGroupProducer_LoopRunsWhileProducerRuns() => Eq(
        "cd \"$(mktemp -d)\"\n" +
        "{ echo a; " + WaitForSeen + "; } | while read -r l; do echo \"got $l\"; touch seen; done\n" +
        "echo rc=$?");

    /// <summary>A nested shell producer (its output already streams into a cmdlet stage).</summary>
    [SkippableFact]
    public Task Differential_ReadLoopStage_NestedShellProducer_LoopRunsWhileProducerRuns() => Eq(
        "cd \"$(mktemp -d)\"\n" +
        "bash -c 'echo a; " + WaitForSeen + "' | while read -r l; do echo \"got $l\"; touch seen; done\n" +
        "echo rc=$?");

    /// <summary>A function producer, and the loop's own output piped on: the next stage sees each line as it comes.</summary>
    [SkippableFact]
    public Task Differential_ReadLoopStage_FunctionProducer_StreamsThroughToTheNextStage() => Eq(
        "cd \"$(mktemp -d)\"\n" +
        "produce() { echo a; " + WaitForSeen + "; }\n" +
        "produce | while read -r l; do echo \"got $l\"; touch seen; done | cat\n" +
        "echo rc=$?");

    /// <summary>Under pipefail every stage reports its own status (a per-stage wrapper); the loop still streams.</summary>
    [SkippableFact]
    public Task Differential_ReadLoopStage_Pipefail_StillStreams() => Eq(
        "cd \"$(mktemp -d)\"\nset -o pipefail\n" +
        "{ echo a; " + WaitForSeen + "; } | while read -r l; do echo \"got $l\"; touch seen; done\n" +
        "echo \"rc=$? ps=${PIPESTATUS[*]}\"");

    /// <summary>An endless producer: the loop's <c>break</c> / <c>exit</c> ends it (bash: SIGPIPE on its next write).</summary>
    [SkippableTheory]
    [InlineData("yes | while read l; do echo \"$l\"; break; done; echo rc=$?")]
    [InlineData("yes | while read l; do echo \"x$l\"; exit 7; done; echo rc=$?")]
    [InlineData("yes | while read l; do n=$((n+1)); [ $n -ge 3 ] && break; echo \"$n$l\"; done; echo rc=$?")]
    [InlineData("yes | while read l; do echo \"$l\"; break; done | cat; echo rc=$?")]
    [InlineData("seq 1 1000000 | while read n; do [ $n -eq 2 ] && break; echo $n; done; echo after")]
    public Task Differential_ReadLoopStage_EndlessProducer_LoopEndsIt(string script) => Eq(script);

    /// <summary>The loop's status: its last body command's, or 0 when the body never ran — never the producer's.</summary>
    [SkippableTheory]
    [InlineData("{ echo a; false; } | while read l; do true; done; echo \"st=$?\"")]
    [InlineData("false | while read l; do :; done; echo \"st=$?\"")]
    [InlineData("printf 'a\\n' | while read l; do false; done; echo \"st=$?\"")]
    [InlineData("{ echo a; echo \"in=$?\"; } | while read l; do false; echo \"$l\"; done")]
    [InlineData("printf '1\\n2\\n' | while read l; do echo \"[$?]$l\"; false; done")]
    [InlineData("{ echo a; exit 3; } | while read l; do echo \"got $l\"; done; echo \"st=$?\"")]
    public Task Differential_ReadLoopStage_Status(string script) => Eq(script);

    /// <summary>What the loop reads: empty input, an unterminated last line, IFS splitting, several lines per record.</summary>
    [SkippableTheory]
    [InlineData("printf '' | while read l; do echo \"got $l\"; done; echo end")]
    [InlineData("true | while read l; do echo \"got $l\"; done; echo end")]
    [InlineData("printf 'a\\nb\\n' | while read l; do echo \"[$l]\"; done")]
    [InlineData("printf 'a\\nb' | while read l; do echo \"[$l]\"; done; echo \"st=$?\"")]
    [InlineData("printf 'x' | while read l; do echo \"[$l]\"; done; echo \"st=$?\"")]
    [InlineData("printf 'a\\n\\n' | while read l; do echo \"[$l]\"; done")]
    [InlineData("{ echo a; echo; echo; } | while read l; do echo \"[$l]\"; done")]
    [InlineData("{ printf 'a'; printf 'b\\nc'; echo d; printf '\\n\\ne'; } | while read l; do echo \"[$l]\"; done")]
    [InlineData("{ echo -n 'a '; echo b; } | while read x y; do echo \"$x|$y\"; done")]
    [InlineData("printf 'a:b:c\\n1:2\\n' | while IFS=: read x y; do echo \"x=$x y=$y\"; done")]
    [InlineData("printf '  pad  \\n' | while IFS= read -r l; do echo \"[$l]\"; done")]
    [InlineData("printf '  pad  \\n' | while read -r l; do echo \"[$l]\"; done")]
    [InlineData("printf 'a b c\\n' | while read x rest; do echo \"$x|$rest\"; done")]
    [InlineData("printf '1\\n2\\n3\\n' | while read l; do [ \"$l\" = 2 ] && continue; echo \"c$l\"; done")]
    [InlineData("printf 'a\\nb\\n' | while read l; do printf '%s-1\\n%s-2\\n' \"$l\" \"$l\" | while read m; do echo \"$m\"; done; done")]
    [InlineData("printf '3\\n1\\n2\\n' | while read l; do echo \"n$l\"; done | sort")]
    [InlineData("printf '1\\n2\\n3\\n' | while read l; do echo \"n$l\"; done | head -1; echo rc=$?")]
    [InlineData("printf 'x;y $(echo inj) `echo bt`\\n' | while read -r l; do echo \"[$l]\"; done")]
    public Task Differential_ReadLoopStage_Input(string script) => Eq(script);

    /// <summary>The loop is a subshell: its assignments do not reach the shell, and its own redirect replaces the pipe.</summary>
    [SkippableTheory]
    [InlineData("x=0; printf '1\\n2\\n' | while read l; do x=$l; done; echo \"x=$x\"")]
    [InlineData("printf '1\\n' | while read l; do cd /; done; [ \"$PWD\" = / ] && echo moved || echo stayed")]
    [InlineData("printf 'zz\\n' > /tmp/psb_rls_a.txt; echo ignored | while read l; do echo \"got $l\"; done < /tmp/psb_rls_a.txt; rm -f /tmp/psb_rls_a.txt")]
    [InlineData("echo ignored | while read l; do echo \"got $l\"; done <<< 'here'")]
    [InlineData("( set -u; printf 'a\\nb\\n' | while read l; do echo \"$l\"; echo \"$nope_unset\"; done; echo \"after=$?\" ) 2>/dev/null")]
    [InlineData("( set -e; printf '1\\n2\\n' | while read l; do echo \"$l\"; false; echo no; done; echo after ); echo \"sub=$?\"")]
    [InlineData("( set -e; printf '1\\n' | while read l; do false; done || echo caught; echo end )")]
    [InlineData("f() { printf 'a\\nb\\n' | while read l; do echo \"$l\"; return 3; done; echo \"after=$?\"; }; f; echo \"f=$?\"")]
    public Task Differential_ReadLoopStage_SubshellAndRedirects(string script) => Eq(script);

    /// <summary>
    /// A pipe stage is a subshell, which starts with no loop: a loop around the pipeline is out of its reach. A
    /// <c>break</c> in the stage ended the enclosing loop.
    /// </summary>
    [SkippableTheory]
    [InlineData("for i in 1 2; do printf 'a\\nb\\n' | while read l; do break 2; done; echo \"w$i\"; done")]
    [InlineData("for i in 1 2; do echo x | { cat; break; echo no; } 2>/dev/null; echo \"after$i\"; done")]
    [InlineData("printf 'a\\nb\\nc\\n' | while read l; do printf 'q\\nr\\n' | while read m; do [ \"$m\" = r ] && break 2; echo \"$l$m\"; done; echo \"after-inner-$l\"; done; echo \"rc=$?\"")]
    [InlineData("for i in 1 2; do printf 'x\\ny\\n' | while read l; do [ \"$l\" = y ] && break; echo \"$i$l\"; done; done")]
    public Task Differential_ReadLoopStage_LoopControlStaysInTheStage(string script) => Eq(script);
}
