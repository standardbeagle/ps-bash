using System.Text.RegularExpressions;
using PsBash.Core.Parser;
using PsBash.Core.Parser.Ast;
using PsBash.Core.Transpiler;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// What the emitted script tells the runtime so that prompt <c>\j</c>, <c>\#</c>, <c>\!</c> and <c>fg</c> can be bash's
/// (oils prompt.spec "\j for number of jobs", "\! for history number", "\# for command number"). Hand-written: the
/// report calls and the job's <c>-Text</c> are ps-bash surface; the behaviour is oracle-checked by
/// <c>Differential.Tests/PromptCounterDifferentialTests</c> and the spec cases.
/// </summary>
public class PromptCounterEmitterTests
{
    private static string Emit(string bash) => PsEmitter.Transpile(bash)!;

    private const string Report = "[PsBash.Cmdlets.ShellReader]::Next(";

    private static int Reports(string ps) => Regex.Matches(ps, Regex.Escape(Report)).Count;

    // fg prints the job's command as written; the emitted job carried no text, so fg could only say "bash-bg".
    [Theory]
    [InlineData("sleep 5 &", "sleep 5")]
    [InlineData("{ sleep 2; echo x; } &", "{ sleep 2; echo x; }")]
    [InlineData("echo \"it's\" $(date) &", "echo \"it's\" $(date)")]
    [InlineData("for i in 1 2; do sleep $i & done", "sleep $i")]
    public void Parse_BackgroundJob_KeepsItsCommandAsWritten(string bash, string expected)
    {
        var job = FindJob(BashParser.Parse(bash)!);
        Assert.Equal(expected, job.Source);
    }

    [Fact]
    public void Transpile_BackgroundJob_PassesItsTextSingleQuoted()
        => Assert.EndsWith(" -Text 'echo \"it''s\"'", Emit("echo \"it's\" &"));

    [Fact]
    public void Parse_BackgroundJobText_IsNotPartOfNodeEquality()
    {
        var job = (Command.Background)BashParser.Parse("sleep 1 &")!;
        Assert.Equal(new Command.Background(job.Inner), job);
        Assert.Equal(new Command.Background(job.Inner).GetHashCode(), job.GetHashCode());
    }

    // bash's current_command_number counts the reader's input lines: `a; b` is one, a multi-line `if` is one.
    [Fact]
    public void Transpile_ScriptExpandingAPrompt_ReportsEachInputLineOnce()
    {
        var ps = Emit("PS1='\\#'\necho a; echo \"${PS1@P}\"\nif true; then\n  echo c\nfi\nsleep 1 & echo d\n");
        Assert.Equal(4, Reports(ps));
        Assert.Contains(Report + ")", ps);
    }

    [Fact]
    public void Transpile_LoneCommandExpandingAPrompt_ReportsOneLine()
        => Assert.Equal(1, Reports(Emit("echo \"${PS1@P}\"")));

    // Cost guard: a script that can observe neither counter emits exactly what it did before.
    [Theory]
    [InlineData("echo a\necho b")]
    [InlineData("PS1='\\#'; echo $PS1")]
    [InlineData("echo history")]
    public void Transpile_ScriptThatCannotObserveTheReader_ReportsNothing(string bash)
        => Assert.DoesNotContain("ShellReader", Emit(bash));

    // An eval's text is not read by the reader: only the line holding the eval counts.
    [Fact]
    public void Transpile_EvalText_IsNotReported()
    {
        Assert.Equal(1, Reports(Emit("eval 'echo \"${PS1@P}\"; echo x'")));
        Assert.DoesNotContain("ShellReader", PsEmitter.Transpile("echo \"${PS1@P}\"\necho y", TranspileContext.Eval));
    }

    // Under `set -o history` each input line is a history entry, as bash's cmdhist stores it.
    [Fact]
    public void Transpile_ScriptTurningHistoryOn_ReportsEachLineText()
    {
        var ps = Emit("set -o history\nfor i in 1 2\ndo echo \"x'$i\"\ndone\nhistory");
        Assert.Contains(Report + "$ExecutionContext.SessionState, 'set -o history')", ps);
        Assert.Contains(Report + "$ExecutionContext.SessionState, 'for i in 1 2; do echo \"x''$i\"; done')", ps);
        Assert.Contains(Report + "$ExecutionContext.SessionState, 'history')", ps);
    }

    // bash 5.2 (oracle, `history` after each form): cmdhist joins a command's lines with "; ", a space after a line that
    // opens a body; a backslash-newline joins without one.
    [Theory]
    [InlineData("echo a # note", "echo a # note")]
    [InlineData("for i in 1 2\ndo echo $i\ndone", "for i in 1 2; do echo $i; done")]
    [InlineData("if true; then\n  echo a\nfi", "if true; then echo a; fi")]
    [InlineData("f() {\n  echo a\n}", "f() { echo a; }")]
    [InlineData("echo a |\n  cat", "echo a | cat")]
    [InlineData("echo a \\\nb", "echo a b")]
    [InlineData("echo a\\\nb", "echo ab")]
    [InlineData("case x in\nx) echo y ;;\nesac", "case x in x) echo y ;; esac")]
    [InlineData("if true; then\ncase x in\nx) echo y ;;\nesac\nfi", "if true; then case x in x) echo y ;; esac; fi")]
    [InlineData("echo \"q\nr\"", "echo \"q\nr\"")]
    public void HistoryEntry_JoinsLinesLikeCmdhist(string line, string expected)
        => Assert.Equal(expected, CmdHist.Entry(line));

    private static Command.Background FindJob(Command c) => c switch
    {
        Command.Background b => b,
        Command.ForIn f => FindJob(f.Body),
        Command.CommandList l => l.Commands.Select(TryFind).First(x => x is not null)!,
        _ => throw new InvalidOperationException("no job in " + c.GetType().Name),
    };

    private static Command.Background? TryFind(Command c)
    {
        try { return FindJob(c); } catch (InvalidOperationException) { return null; }
    }
}
