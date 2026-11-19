using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>kill with a signal whose default action is to ignore it.</summary>
public class KillSignalDifferentialTests
{
    /// <summary>SIGCHLD/SIGURG/SIGWINCH are ignored by default: the shell and a job keep running (a job does not
    /// inherit the parent's trap). ps-bash resolved the names to TERM. stderr is silenced (bash adds <c>line N:</c>).</summary>
    [SkippableFact]
    public Task Kill_DefaultIgnoredSignals_DoNotEndTheShellOrAJob() =>
        AssertOracle.EqualAsync("trap 'echo URG' URG\nkill -URG $$\ntrap - URG\nkill -URG $$; echo \"self $?\"\nkill -WINCH $$; kill -s CHLD $$; echo \"st $?\"\n"
            + "{ sleep 0.2; echo child; } & kill -URG $!; echo \"k $?\"; wait $!; echo \"w $?\"\nkill -URG 999999 2>/dev/null; echo \"none $?\"");

    /// <summary>The dash form of a real-time signal (<c>-RTMIN+1</c>, <c>-SIGRTMAX-3</c>) is a signal spec, not a pid.
    /// REGRESSION: the <c>-NAME</c> pattern stopped at the <c>+</c> / <c>-</c>, so it was refused as "arguments must be
    /// process or job IDs". stderr is silenced (bash adds <c>line N:</c>).</summary>
    [SkippableFact]
    public Task Kill_DashRealTimeSignal_IsASignalSpec() =>
        AssertOracle.EqualAsync("trap 'echo got' RTMIN+1 RTMAX-3\nkill -RTMIN+1 $$; echo \"rc=$?\"\nkill -SIGRTMAX-3 $$; echo \"rc=$?\"\n"
            + "kill -RTMIN+99 $$ 2>/dev/null; echo \"rc=$?\"");

    /// <summary><c>trap</c> names signals from kill's table: real-time names and numbers (50 is <c>SIGRTMAX-14</c>), the
    /// nameless 32 listed as a number, all in signal-number order. REGRESSION: trap kept its own 1-31 table, so
    /// <c>trap … RTMIN+1</c> was "invalid signal specification" and 50 was named <c>RTMIN+16</c>.</summary>
    [SkippableFact]
    public Task Trap_RealTimeSignals_ShareKillsNames() =>
        AssertOracle.EqualAsync("trap 'echo a' rtmin+1 50 32 SIGRTMAX USR1 0\necho \"rc=$?\"\ntrap -p\ntrap - 35 SIGRTMAX-14\ntrap -p\n"
            + "trap 'echo b' RTMIN+31 2>/dev/null; echo \"bad=$?\"");
}
