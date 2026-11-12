using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>kill with a signal whose default action is to ignore it.</summary>
public class KillSignalDifferentialTests
{
    /// <summary>SIGCHLD/SIGURG/SIGWINCH are ignored by default: the shell and a job keep running (a job does not
    /// inherit the parent's trap). ps-bash resolved the names to TERM. stderr is silenced (bash adds <c>line N:</c>).</summary>
    [Fact]
    public Task Kill_DefaultIgnoredSignals_DoNotEndTheShellOrAJob() =>
        AssertOracle.EqualAsync("trap 'echo URG' URG\nkill -URG $$\ntrap - URG\nkill -URG $$; echo \"self $?\"\nkill -WINCH $$; kill -s CHLD $$; echo \"st $?\"\n"
            + "{ sleep 0.2; echo child; } & kill -URG $!; echo \"k $?\"; wait $!; echo \"w $?\"\nkill -URG 999999 2>/dev/null; echo \"none $?\"");
}
