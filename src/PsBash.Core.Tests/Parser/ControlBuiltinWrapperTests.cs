using PsBash.Core.Parser;
using PsBash.Core.Parser.Ast;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>`builtin` / `command` in front of break, continue, return or exit only bypass functions and aliases: the
/// control builtin behind them is the bare one. `command continue` used to run `continue` as a command, so the loop
/// never saw it.</summary>
public class ControlBuiltinWrapperTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Theory]
    [InlineData("builtin break", "break", 1)]
    [InlineData("command continue", "continue", 1)]
    [InlineData("builtin command return 99", "return", 2)]
    [InlineData("command builtin exit 5", "exit", 2)]
    [InlineData("\\builtin return 3", "return", 2)]
    public void Parse_WrappedControlBuiltin_IsTheBareBuiltin(string bash, string name, int words)
    {
        var cmd = Assert.IsType<Command.Simple>(BashParser.Parse(bash));
        var first = Assert.Single(cmd.Words[0].Parts);
        Assert.Equal(name, Assert.IsType<WordPart.Literal>(first).Value);
        Assert.Equal(words, cmd.Words.Length);
    }

    [Fact]
    public void Transpile_CommandContinueInLoop_IsTheLoopKeyword()
        => Assert.Equal(T("for i in 1 2; do continue; done"), T("for i in 1 2; do command continue; done"));

    [Fact]
    public void Transpile_CommandReturnInFunction_IsTheBareReturn()
    {
        var ps = T("f() { command return 3; }");
        Assert.Contains("$global:LASTEXITCODE = 3; return", ps);
        Assert.DoesNotContain("Invoke-BashCommand", ps);
    }

    [Fact] public void Transpile_CommandVBreak_StaysCommandLookup() => Assert.Contains("Invoke-BashCommand", T("command -v break"));

    // bash opens a redirect target even on a control builtin: `break > f` creates f.
    [Fact]
    public void Transpile_BreakWithRedirect_TouchesTheTarget()
    {
        var ps = T("for x in a; do break > r.txt; done");
        Assert.Contains("r.txt", ps);
        Assert.Contains("break", ps);
    }
}
