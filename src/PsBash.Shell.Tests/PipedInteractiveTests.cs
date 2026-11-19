using PsBash.Shell;
using Xunit;

namespace PsBash.Shell.Tests;

/// <summary><c>echo cmds | bash -i</c>: how the input is cut into commands (one per bash prompt), stored in the
/// history, and history-expanded. Expectations are bash 5.2's (oracle: piped <c>bash --norc -i</c>, prompts on stderr).</summary>
public class PipedInteractiveTests
{
    private static List<string> Commands(string input) =>
        PipedInteractive.SplitCommands(input).Select(l => string.Join("\n", l)).ToList();

    [Fact]
    public void SplitCommands_OneLinePerCommand_BlankLinesKept() =>
        Assert.Equal(["", "echo 1", "", "echo 2"], Commands("\necho 1\n\necho 2\n"));

    [Fact]
    public void SplitCommands_OpenCompoundCommand_ContinuesUntilClosed() =>
        Assert.Equal(["for i in 1; do\necho $i\ndone", "echo after"], Commands("for i in 1; do\necho $i\ndone\necho after\n"));

    [Fact]
    public void SplitCommands_OpenQuote_Continues() =>
        Assert.Equal(["echo \"a\nb\"", "echo 'c\nd'", "echo e"], Commands("echo \"a\nb\"\necho 'c\nd'\necho e\n"));

    [Fact]
    public void SplitCommands_HereDocument_BodyBelongsToTheCommand() =>
        Assert.Equal(["cat <<EOF\nif\n'\nEOF", "cat <<-'X'\n\tbody\n\tX", "echo next"],
            Commands("cat <<EOF\nif\n'\nEOF\ncat <<-'X'\n\tbody\n\tX\necho next\n"));

    [Fact]
    public void SplitCommands_TrailingBackslash_Continues() =>
        Assert.Equal(["echo a \\\nb", "echo c"], Commands("echo a \\\nb\necho c\n"));

    [Fact]
    public void SplitCommands_ArithmeticShift_IsNotAHereDocument() =>
        Assert.Equal(["echo $((1 << 2))", "echo x"], Commands("echo $((1 << 2))\necho x\n"));

    [Fact]
    public void SplitCommands_SyntaxError_EndsTheCommand() =>
        Assert.Equal(["echo )", "echo ok"], Commands("echo )\necho ok\n"));

    [Fact]
    public void SplitCommands_UnclosedAtEof_IsOneCommand() =>
        Assert.Equal(["if true; then\necho x"], Commands("if true; then\necho x\n"));

    [Fact]
    public void SplitCommands_CrLfInput_LinesHaveNoCarriageReturn() =>
        Assert.Equal(["echo 1", "echo 2"], Commands("echo 1\r\necho 2\r\n"));

    // bash (cmdhist): `for i in 1; do / echo $i / done` is stored as `for i in 1; do echo $i; done`.
    [Theory]
    [InlineData(new[] { "for i in 1; do", "echo $i", "done" }, "for i in 1; do echo $i; done")]
    [InlineData(new[] { "f() {", "echo in", "}" }, "f() { echo in; }")]
    [InlineData(new[] { "echo a |", "cat" }, "echo a | cat")]
    [InlineData(new[] { "echo \"a", "b\"" }, "echo \"a\nb\"")]
    [InlineData(new[] { "cat <<EOF", "x", "EOF" }, "cat <<EOF\nx\nEOF")]
    public void HistoryEntry_JoinsLinesLikeCmdhist(string[] lines, string expected) =>
        Assert.Equal(expected, PsBash.Core.Parser.CmdHist.Entry(lines));

    [Theory]
    [InlineData("echo !!", "echo echo two")]
    [InlineData("echo !$", "echo two")]
    [InlineData("!ec", "echo two")]
    [InlineData("echo !-2", "echo ls")]
    [InlineData("echo '!!'", "echo '!!'")]          // not in single quotes
    [InlineData("echo \"!!\"", "echo \"echo two\"")] // but in double quotes
    [InlineData("echo \\!!", "echo \\!!")]           // nor after a backslash
    [InlineData("[ ! -f x ]", "[ ! -f x ]")]          // `!` before a blank is the negation
    [InlineData("echo $! ${!v}", "echo $! ${!v}")]
    [InlineData("x!=y", "x!=y")]
    public void ExpandHistory_LikeBash(string line, string expected)
    {
        Assert.Equal(expected, PipedInteractive.ExpandHistory(line, ["ls", "echo two"], out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("echo !!", "!!: event not found")]
    [InlineData("echo !zz", "!zz: event not found")]
    [InlineData("echo !-3", "!-3: event not found")]
    public void ExpandHistory_MissingEvent_IsAnError(string line, string message)
    {
        var history = line == "echo !!" ? new List<string>() : ["ls", "echo two"];
        Assert.Null(PipedInteractive.ExpandHistory(line, history, out var error));
        Assert.Equal(message, error);
    }

    [Fact]
    public void BuildScript_EachCommandRunsThroughEvalAfterPromptCommandAndHistory()
    {
        var script = PipedInteractive.BuildScript("echo 'it''s'\n", loadHistory: false, noSaveFile: "/h/.bash_history");
        int pc = script.IndexOf("eval \"$PROMPT_COMMAND\"", StringComparison.Ordinal);
        int hist = script.IndexOf("history -s -- 'echo '\\''it'\\'''\\''s'\\'''", StringComparison.Ordinal);
        int run = script.IndexOf("eval \"$__psbash_line\"", StringComparison.Ordinal);
        Assert.True(pc >= 0 && hist > pc && run > hist, script);
        Assert.DoesNotContain("history -r", script);
        Assert.Contains("[ \"$HISTFILE\" != '/h/.bash_history' ]", script);
        Assert.EndsWith("exit $__psbash_status\n", script);
    }

    // bash 5.2 oracle: `printf 'echo $-\n' | bash --norc -i` prints himBH (interactive, no c), and with --rcfile rc the rc
    // runs before the first prompt, already interactive (an rc's `case $- in *i*)` guard holds) and its PS1 is the prompt.
    [Fact]
    public void BuildScript_MarksTheShellInteractive_ThenSourcesTheRcfile_BeforeTheFirstPrompt()
    {
        var script = PipedInteractive.BuildScript("echo hi\n", loadHistory: false, noSaveFile: null, rcFile: "rc");
        int mark = script.IndexOf("shopt -s " + ShellArgs.InteractiveMark + "\n", StringComparison.Ordinal);
        int rc = script.IndexOf(ShellArgs.RcSourceCommand("rc"), StringComparison.Ordinal);
        int prompt = script.IndexOf("${PS1@P}", StringComparison.Ordinal);
        Assert.True(mark == 0 && rc > mark && prompt > rc, script);

        Assert.DoesNotContain(". '", PipedInteractive.BuildScript("echo hi\n", loadHistory: false, noSaveFile: null));
    }

    // Injection probe: a command's text reaches eval only as a single-quoted literal, never as script text.
    [Theory]
    [InlineData("echo '; touch pwned'")]
    [InlineData("echo $(id) `id` \"$x\"")]
    [InlineData("cat <<EOF\n'\"$x\nEOF")]
    public void BuildScript_CommandTextIsQuoted(string input)
    {
        var script = PipedInteractive.BuildScript(input + "\n", loadHistory: true, noSaveFile: null);
        Assert.Contains("history -r 2>/dev/null", script);
        Assert.Contains("__psbash_line='" + input.Replace("'", "'\\''") + "'\n", script);
    }

    [Fact]
    public void BuildScript_EventNotFound_DoesNotRunTheCommand()
    {
        var script = PipedInteractive.BuildScript("echo !!\n", loadHistory: false, noSaveFile: null);
        Assert.Contains("bash: !!: event not found", script);
        Assert.DoesNotContain("__psbash_line=", script);
    }
}
