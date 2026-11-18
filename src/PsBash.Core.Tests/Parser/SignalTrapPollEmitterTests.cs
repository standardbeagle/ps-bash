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

    // Code the emitter cannot see may install a trap, so the script polls: a trap set only through run-time eval text
    // never ran when another shell signalled (no `trap` word in the source to find).
    [Theory]
    [InlineData("t=tr; eval \"${t}ap 'echo T' USR1\"\necho a")]
    [InlineData("eval \"$x\"\necho a")]
    [InlineData("command eval \"$x\"\necho a")]
    [InlineData("$cmd 'echo T' USR1\necho a")]
    [InlineData("eval \"trap 'echo T' USR2\"\necho a")]       // static eval text that sets a trap
    [InlineData("eval eval \"'trap x USR1'\"\necho a")]
    [InlineData("f() { trap 'echo T' USR1; }\nf")]
    [InlineData("builtin trap 'echo T' USR1\necho a")]
    [InlineData("'trap' 'echo T' USR1\necho a")]
    [InlineData("$t'ap' 'echo T' USR1\necho a")]
    [InlineData("alias t='trap \"echo T\" USR1'\necho a")]
    public void Transpile_ScriptThatCanSetATrapAtRunTime_Polls(string bash)
        => Assert.True(Polls(PsEmitter.Transpile(bash)) >= 2);   // each top-level statement (and inlined eval text's)

    // Guard: the decision is made on the parsed commands, not by scanning the text for `trap` (which both missed the
    // run-time eval above and polled here).
    [Theory]
    [InlineData("echo trap\necho b")]
    [InlineData("echo 'trap x USR1'; echo b")]
    [InlineData("( trap 'echo T' USR1 )\necho b")]           // a subshell's trap dies with it
    [InlineData("x=$(trap -p)\necho b")]
    [InlineData("$dir/$name x\necho b")]                     // a run-time path names a file, never a builtin
    [InlineData("~/bin/foo a\necho b")]
    [InlineData("'/usr/local/bin/node' -e 1\necho b")]
    public void Transpile_TrapOnlyInTextOrASubshell_HasNoPoll(string bash)
        => Assert.Equal(0, Polls(PsEmitter.Transpile(bash)));

    [Fact]
    public void Guard_TheTrapPollGateIsTheAstPredicate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var parser = Path.Combine(dir.FullName, "src", "PsBash.Transpiler", "Parser");
        foreach (var file in Directory.EnumerateFiles(parser, "PsEmitter*.cs"))
            foreach (var line in File.ReadAllLines(file))
                if (System.Text.RegularExpressions.Regex.IsMatch(line, @"\b_canSetTrap\s*=[^=]"))
                    Assert.Matches(@"_canSetTrap\s*=\s*(priorCanSetTrap\s*\|\|\s*)?CanSetTrap\(\w+\)|_canSetTrap\s*=\s*priorCanSetTrap;", line);
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
