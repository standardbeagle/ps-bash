using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// Where the emitter puts the signal poll (<see cref="PsBuild.SignalTrapPoll"/>): after each top-level statement of a
/// script that can set a trap, where a signal another shell sent runs its trap (docs/specs/background-jobs.md §13).
/// Emitted-text surface with no bash equivalent; the behavior is compared with bash in ShellSignalDifferentialTests.
/// </summary>
public class SignalTrapPollEmitterTests
{
    private static int Polls(string? emitted)
    {
        Assert.NotNull(emitted);
        int n = 0;
        for (int i = emitted.IndexOf(PsBuild.SignalTrapPoll, StringComparison.Ordinal); i >= 0;
             i = emitted.IndexOf(PsBuild.SignalTrapPoll, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    [Fact]
    public void Transpile_ScriptWithTrap_PollsAfterEveryTopLevelStatement()
    {
        var ps = PsEmitter.Transpile("trap 'echo got' USR1\necho a\necho b");
        Assert.Equal(3, Polls(ps));
        Assert.Contains("Invoke-BashEcho a; " + PsBuild.SignalTrapPoll, ps);
    }

    [Fact]
    public void Transpile_ScriptThatSourcesAFile_Polls()
    {
        // A sourced library may set the trap (`trap cleanup INT`), so the caller must poll for it.
        Assert.Equal(2, Polls(PsEmitter.Transpile(". ./lib.sh\necho a")));
        Assert.Equal(2, Polls(PsEmitter.Transpile("source ./lib.sh; echo a")));
    }

    [Theory]
    [InlineData("echo a\necho b")]
    [InlineData("find . -name x")]           // `.` as an operand, not the source command
    [InlineData("find . ! -name x -print")]
    [InlineData("eval echo hi")]              // static eval text has no trap
    [InlineData("ls ./x.sh")]
    public void Transpile_ScriptThatCannotSetATrap_HasNoPoll(string bash)
        => Assert.Equal(0, Polls(PsEmitter.Transpile(bash)));

    [Fact]
    public void Transpile_SubshellBody_HasNoPoll()
    {
        // bash's subshell is another process with its traps reset; the poll belongs to the enclosing shell only.
        var ps = PsEmitter.Transpile("trap 'echo got' USR1\n( echo in1; echo in2 )\necho out");
        Assert.Equal(3, Polls(ps));
        Assert.DoesNotContain("Invoke-BashEcho in1; " + PsBuild.SignalTrapPoll, ps);
    }

    [Fact]
    public void Transpile_PollReadsAStaticPropertyOnly()
    {
        // Per statement: a property read (no AMSI report on Windows, unlike a method call); the cmdlet runs only when a
        // signal is pending.
        Assert.StartsWith("if ([PsBash.Cmdlets.ShellSignalPoll]::Pending) {", PsBuild.SignalTrapPoll);
        Assert.Contains("Invoke-BashPendingSignals", PsBuild.SignalTrapPoll);
    }
}
