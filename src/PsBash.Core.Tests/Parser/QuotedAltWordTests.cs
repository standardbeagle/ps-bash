using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>Bare <c>${x+"$x"}</c> / <c>${x:+"-o=$x"}</c> / <c>${!r+"${!r}"}</c>: bash expands the quoted alternative to its words
/// when it is chosen and to NO word when it is not (x unset: <c>for e in ${x+"$x"}; do echo "[$e]"; done</c> prints nothing).
/// ps-bash passed one empty argument, so the loop ran once and a command got an extra "" operand.</summary>
public class QuotedAltWordTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Theory]
    [InlineData("for e in ${x+\"$x\"}; do echo \"[$e]\"; done")]
    [InlineData("printf '[%s]' ${x:+\"--opt=$x\"}")]
    [InlineData("r=x; for e in ${!r+\"${!r}\"}; do echo $e; done")]
    public void Transpile_UnquotedQuotedAlternative_IsNoWordWhenNotChosen(string bash)
        => Assert.Contains("} else { ,@() }", T(bash)); // the arm not chosen is an empty group: no field

    [Fact]
    public void Transpile_QuotedAlternativeOfAllParameters_KeepsEachParameter()
    {
        var ps = T("command rg ${1+\"$@\"}");
        Assert.Contains("} else { ,@() }", ps);
        Assert.Contains("{ ,@(5, (@($(if ($null -ne $global:BashPositional) { $global:BashPositional } else { $args })))) }", ps); // one field per parameter
    }

    // bash: `false; for x in $(true); do :; done; echo $?` prints 0 -- a loop whose body never ran has status 0. A failed
    // `< file` on the loop still leaves 1, so the reset sits inside the redirect wrapper.
    [Theory]
    [InlineData("for x in $(true); do :; done")]
    [InlineData("for e in ${x+\"$x\"}; do :; done")]
    public void Transpile_ForLoopWhoseBodyNeverRuns_HasStatusZero(string bash)
        => Assert.Contains("if ($__psbash_iter -eq 0) { $global:LASTEXITCODE = 0 }", T(bash));

    [Fact]
    public void Transpile_ForLoopWithInputRedirect_ResetsStatusInsideTheRedirect()
    {
        var ps = T("for x in a; do :; done < f");
        Assert.True(ps.IndexOf("$global:LASTEXITCODE = 0 }", StringComparison.Ordinal) < ps.IndexOf("No such file", StringComparison.Ordinal), ps);
    }

    [Theory]
    [InlineData("echo \"${x+\"$x\"}\"")]
    [InlineData("echo ${x:-\"$y\"}")]
    public void Transpile_QuotedOrDefaultForm_KeepsItsForm(string bash)
        => Assert.DoesNotContain(",@() }", T(bash));

    // bash (extquote, on by default): `$'…'` in the word of a double-quoted `${e:-…}` is still ANSI-C quoting, so
    // `echo "${e:-$'t\x41'}"` prints tA; a plain `'…'` there is literal text, quotes included ('t\x41').
    // The parser translates the `$'…'` first, so the first operand is quote-free and stays in the string. A quote
    // mark in the operand makes the `${…}` leave the string (BracedVarLeavesString): it is a join segment, emitted
    // in bare context as a double-quoted value that keeps the quotes.
    [Fact]
    public void Transpile_AnsiCQuoteInQuotedDefaultWord_IsDecoded()
    {
        Assert.Contains(": 'tA')", T("echo \"${e:-$'t\\x41'}\""));
        var literal = T("echo \"${e:-'t\\x41'}\"");
        Assert.Contains("(-join @(", literal);
        Assert.Contains(": \"'t\\x41'\")", literal);
    }
}
