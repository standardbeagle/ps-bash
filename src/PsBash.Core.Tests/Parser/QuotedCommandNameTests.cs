using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// A quoted or escaped command name is the bare name minus alias expansion (bash 5.2: <c>ls() { ...; }; "ls"</c> runs
/// the function). REGRESSION: <c>"ls"</c> / <c>$'ls'</c> emitted <c>&amp; "ls"</c> and <c>\ls</c> emitted <c>ls</c>,
/// and PowerShell takes an alias of the name (ps-bash's <c>ls</c>, its own <c>h</c>, <c>r</c>, <c>where</c>) before
/// the script's function. The one rule is <c>PsEmitter.QuotedCommandName</c>: the spelling of a builtin, a ps-bash
/// command or a function of the script becomes the bare word (<c>UnquoteBuiltinName</c>); any other name is looked up
/// at run time by <c>Resolve-BashCommand</c>, which skips aliases and returns a bash function as itself. Behavior vs
/// bash: Differential <c>QuotingValueDifferentialTests.QuotedCommandName_ResolvesLikeTheBareName</c>.
/// </summary>
public class QuotedCommandNameTests
{
    // Source marks carry the source text, which differs between the spellings.
    private static string T(string bash)
        => System.Text.RegularExpressions.Regex.Replace(PsEmitter.Transpile(bash)!, "<#bash:[A-Za-z0-9+/=]*#> ", "");

    [Theory]
    [InlineData("\"nosuch_tool\" a", "nosuch_tool")]
    [InlineData("\\greet a", "greet")]
    [InlineData("$'mytool' a", "mytool")]
    [InlineData("my\"\"tool a", "mytool")]
    [InlineData("\"h\" a", "h")]
    [InlineData("\"my tool\" a", "my tool")]
    [InlineData("printf x | \"mytool\" a", "mytool")]
    public void Transpile_QuotedUnknownName_IsLookedUpAtRunTime(string bash, string name)
    {
        var ps = PsEmitter.Transpile(bash)!;
        Assert.Contains("(Resolve-BashCommand ", ps);
        Assert.Contains(PsBuild.SingleQuote(name) + ")", ps);
        Assert.DoesNotContain("& \"" + name + "\"", ps);
    }

    // A ps-bash command, or a function of the script, spelled with quotes is the bare word: it keeps the static path
    // (stdin feeding, the function-first call for a name PowerShell aliases).
    [Theory]
    [InlineData("\"ls\" a", "ls a")]
    [InlineData("'ls' a", "ls a")]
    [InlineData("\\ls a", "ls a")]
    [InlineData("$'ls' a", "ls a")]
    [InlineData("printf x | 'cat' a", "printf x | cat a")]
    [InlineData("ls() { :; }; \"ls\" a", "ls() { :; }; ls a")]
    [InlineData("where() { :; }; \\where a", "where() { :; }; where a")]
    [InlineData("tool() { :; }; 'tool' a", "tool() { :; }; tool a")]
    public void Transpile_QuotedKnownName_IsTheBareName(string quoted, string bare)
        => Assert.Equal(T(bare), T(quoted));

    // A builtin's spelling is the bare builtin (alias expansion aside): the AnsiC spelling too, which also sets no signal
    // poll (a bare `cd` sets none; SignalTrapPollEmitterTests).

    [Theory]
    [InlineData("$'cd' /x", "cd /x")]
    [InlineData("$'eval' 'echo x'", "eval 'echo x'")]
    public void Transpile_AnsiCQuotedBuiltinName_IsTheBareBuiltin(string quoted, string bare)
        => Assert.Equal(T(bare), T(quoted));

    // A name with a path separator is a path, not a name to look up.
    [Theory]
    [InlineData("\"./tool\" a")]
    [InlineData("'/usr/bin/env' a")]
    public void Transpile_QuotedPath_IsNotLookedUp(string bash)
        => Assert.DoesNotContain("Resolve-BashCommand", PsEmitter.Transpile(bash)!);
}
