using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A command that runs another command by name (<c>env</c>, <c>flock</c>, <c>command</c>, <c>nohup</c>) against bash 5.2
/// (WSL Ubuntu-24.04). The bytes of a pipe into each are pinned by <c>NativeStdioBytesTests</c>; the source rule (one
/// runner, <c>InnerCommand</c>) by <c>Core.Tests/InnerCommandGuardTests</c>.
/// </summary>
public class WrapperCommandDifferentialTests
{
    private static Task Eq(string script) => AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(60));

    // Class: each wrapper re-derived "run NAME ARGS" and lost a rule: env bound its pipeline records and dropped them,
    // flock rejected them ("The input object cannot be bound"), and the command's errors went to the host's stderr,
    // past the wrapper's `2>/dev/null`. Primitive: InnerCommand.Run (stdin = the wrapper's records, errors re-written
    // on the wrapper's stream).
    [SkippableFact]
    public Task EnvAndFlock_FeedTheirPipe_AndTheirRedirectCoversTheCommand()
        => Eq("cd \"$(mktemp -d)\" || exit; echo x | env FOO=1 cat; echo \"rc=$?\"; "
            + "echo x | env FOO=1 sh -c 'cat; echo FOO=$FOO'; echo \"rc=$?\"; "
            + "env ls /nosuch 2>/dev/null; echo \"rc=$?\"; echo x | env ls /nosuch 2>/dev/null; echo \"rc=$?\"; "
            + "echo x | flock lk cat; echo \"rc=$?\"; flock lk ls /nosuch 2>/dev/null; echo \"rc=$?\"; "
            + "echo x | flock lk ls /nosuch 2>/dev/null; echo \"rc=$?\"; flock lk sh -c 'exit 4'; echo \"rc=$?\"; "
            + "env nosuchcmd_q 2>/dev/null; echo \"rc=$?\"; flock lk nosuchcmd_q 2>/dev/null; echo \"rc=$?\"");

    // Class: the runner captures the nested error stream with PowerShell's 2>&1, which a spawning command read as a bash
    // merge (StderrMerge), so its child got ONE pipe and its stderr came back as stdout, past `2>/dev/null`. Primitive:
    // InnerCommand.ErrorCapture (Invoke-BashKeepStderr marks a capture that is not a merge). A real merge stays one.
    [SkippableFact]
    public Task WrappedChild_Stderr_GoesWhereTheWrappersDoes()
        => Eq("cd \"$(mktemp -d)\" || exit; env sh -c 'echo out; echo err >&2' 2>/dev/null; echo \"rc=$?\"; "
            + "command sh -c 'echo out; echo err >&2' 2>/dev/null | cat; echo \"rc=$?\"; "
            + "flock lk sh -c 'echo err >&2; exit 4' 2>/dev/null; echo \"rc=$?\"; "
            + "echo x | env sh -c 'cat; echo e >&2' 2>&1 | cat; env sh -c 'echo e2 >&2' 2>&1 >/dev/null | grep -x e2");

    // Class: a command a cmdlet runs by name gets the cmdlet's pipe, byte for byte. `echo y | time cat` (GNU time, not the
    // keyword: it is not first in the pipeline) printed nothing - Invoke-BashTime had no pipeline parameter, so the records
    // went nowhere - and timeout fed its pipe as a here-string, which ends it with a newline (`printf a | timeout 5 wc -c`
    // counted 2). Primitive: the runner feeds the records (a program gets ConvertTo-BashNativeStdin's bytes); guard
    // InnerCommandGuardTests.EveryCommandRunner_TakesPipelineInput.
    [SkippableFact]
    public Task TimeAndTimeout_FeedTheirPipe_ByteForByte()
        => Eq("cd \"$(mktemp -d)\" || exit; echo y | time cat 2>/dev/null; echo \"rc=$?\"; "
            + "echo w | time sh -c 'read l; echo got $l; exit 2' 2>/dev/null; echo \"rc=$?\"; printf a | time wc -c 2>/dev/null; "
            + "printf a | timeout 5 wc -c; printf 'a\\n\\n' | timeout 5 wc -l; printf 'q\\n' | timeout 5 sh -c 'cat; exit 3'; echo \"rc=$?\"; "
            + "echo u | command time cat 2>/dev/null; echo v | env time -p cat 2>/dev/null; echo \"rc=$?\"");
    // Class: nohup always detached and returned 0 at once, so a foreground `nohup CMD` lost CMD's status and output
    // (`x=$(nohup echo hi)` was empty and a nohup.out appeared). Primitive: only `nohup CMD &` detaches (the emitter's
    // --psbash-bg); a foreground one waits for CMD, and with stdout neither a terminal nor redirected runs it in place
    // (InnerCommand), creating no nohup.out. The detached child writes its redirect files itself: sh (Git's MSYS2 sh.exe
    // on the Windows runner, which wrote nothing into an append-only handle) gets `>`/`2>` truncating and `2>>` appending.
    // No `ls`: nohup runs the host's program, and BSD ls (macOS) words and codes the error its own way.
    [SkippableFact]
    public Task ForegroundNohup_WaitsAndTakesTheCommandsStatus()
        => Eq("cd \"$(mktemp -d)\" || exit; nohup sh -c 'echo out; echo \"no such file\" >&2; exit 2' > out.txt 2>&1; echo \"rc=$?\"; cat out.txt; "
            + "echo old-old-old > e.txt; nohup sh -c 'echo E1 >&2' > o4.txt 2> e.txt; cat e.txt; nohup sh -c 'echo E2 >&2' > o4.txt 2>> e.txt; cat e.txt; "
            + "nohup sh -c 'exit 3' > o2.txt; echo \"rc=$?\"; nohup sh -c 'echo hi; echo err >&2' 2>/dev/null | cat; echo \"rc=$?\"; "
            + "nohup true | cat; echo \"rc=$?\"; nohup nosuchcmd > o3.txt 2>&1; echo \"rc=$?\"; sed 's/^.*nohup: /nohup: /' o3.txt; "
            + "x=$(nohup echo captured 2>/dev/null); echo \"x=$x rc=$?\"; printf 'a\\nb\\n' | nohup cat; "
            + "nohup sh -c 'exit 5' 2>/dev/null; echo \"rc=$?\"; nohup nosuchcmd 2>&1 | sed 's/^.*nohup: /nohup: /'; "
            + "echo \"nohup.out exists: $(test -e nohup.out && echo yes || echo no)\"");
}
