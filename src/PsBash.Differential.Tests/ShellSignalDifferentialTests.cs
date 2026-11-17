using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A nested shell signals its enclosing script: <c>sh -c "kill -USR1 $$"</c>. The nested shell runs in another ps-bash
/// host, so this signal crosses hosts as a message (docs/specs/background-jobs.md §13). Before that, the kill was refused
/// with "signal not delivered": the trap never ran and an untrapped USR1 did not end the script. Earlier still, it killed
/// the enclosing host.
/// </summary>
public class ShellSignalDifferentialTests
{
    /// <summary>
    /// Runs <paramref name="body"/> as a child script and prints the status its parent sees. A script that dies of a
    /// signal has no exit status of its own (the oracle harness reports the bare signal number); its parent sees 128+N.
    /// stderr is silenced: bash's parent reports "line N: PID User defined signal 1". The child prints a line first:
    /// it and its nested <c>sh -c</c> each start a ps-bash host, and on a loaded machine two silent host starts in a
    /// row outlast the launcher's 15 s no-output window (PSBASH_TIMEOUT), which the oracle fixture pins.
    /// </summary>
    private static string AsChild(string body)
        => "sf=$(mktemp)\ncat > \"$sf\" <<'EOF'\necho child\n" + body + "EOF\n{ bash \"$sf\"; echo \"status=$?\"; } 2>/dev/null\nrm -f \"$sf\"\n";

    [SkippableFact]
    public Task NestedKill_TrappedSignal_RunsTheTrapAndKeepsStatus() =>
        AssertOracle.EqualAsync("trap 'echo USR1 trap status=$?; ( exit 42 )' USR1\necho before=$?\nsh -c \"kill -USR1 $$\"\necho after=$?\n");

    [SkippableFact]
    public Task NestedKill_UntrappedSignal_EndsTheScriptWith128PlusN() =>
        AssertOracle.EqualAsync(AsChild("echo a\nsh -c \"kill -USR1 $$\"\necho notreached\n"));

    [SkippableFact]
    public Task NestedKill_UntrappedSignalAfterAnotherWasTrapped_StillEndsTheScript() =>
        AssertOracle.EqualAsync(AsChild("trap 'echo usr1' USR1\nsh -c \"kill -TERM $$\"\necho notreached\n"));

    [SkippableFact]
    public Task NestedKill_Kill9_EndsTheScriptEvenWithTraps() =>
        AssertOracle.EqualAsync(AsChild("trap 'echo usr1' USR1\nsh -c \"kill -9 $$\"\necho notreached\n"));

    [SkippableFact]
    public Task NestedKill_ResetTrap_AppliesTheDefault() =>
        AssertOracle.EqualAsync(AsChild("trap 'echo t' USR2\ntrap - USR2\nsh -c \"kill -USR2 $$\"\necho notreached\n"));

    [SkippableFact]
    public Task NestedKill_EmptyTrap_IgnoresTheSignal() =>
        AssertOracle.EqualAsync("trap '' USR1\nsh -c \"kill -USR1 $$\"\necho survived=$?\n");

    [SkippableFact]
    public Task NestedKill_IgnoredByDefault_ScriptCarriesOn() =>
        AssertOracle.EqualAsync("sh -c \"kill -WINCH $$\"\necho survived=$?\n");

    [SkippableFact]
    public Task NestedKill_SeveralSignals_EachTrapRunsOnceInSignalOrder() =>
        AssertOracle.EqualAsync("trap 'echo u2' USR2\ntrap 'echo u1' USR1\nsh -c \"kill -USR2 $$; kill -USR1 $$; kill -USR2 $$\"\necho after=$?\n");

    [SkippableFact]
    public Task NestedKill_TrapWithDebugTrap_LinenoCountsInsideTheAction() =>
        AssertOracle.EqualAsync("trap 'false; echo $LINENO usr1' USR1\ntrap 'false; echo $LINENO dbg' DEBUG\n\nsh -c \"kill -USR1 $$\"\necho after=$?\n");

    [SkippableFact]
    public Task NestedKill_TrapWithErrTrap_ErrFiresInsideTheAction() =>
        AssertOracle.EqualAsync("trap 'false; echo $LINENO usr1' USR1\ntrap 'false; echo $LINENO err' ERR\n\nsh -c \"kill -USR1 $$\"\necho after=$?\n");
}
