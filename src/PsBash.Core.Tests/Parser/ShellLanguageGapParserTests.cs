using PsBash.Core.Parser;
using PsBash.Core.Parser.Ast;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// Parser and emitter layer of the shell-language gaps the coverage probe found (behavior against bash:
/// Differential.Tests/ShellLanguageGapDifferentialTests). Each case failed before its fix.
/// </summary>
public class ShellLanguageGapParserTests
{
    private static string Emit(string bash) => PsEmitter.Transpile(bash) ?? "";

    // --- operators on special parameters ----------------------------------------------------------

    /// <summary>`${!:+pid}` was read as an indirect reference (`${!name}`) and emitted a broken command.</summary>
    [Theory]
    [InlineData("${!:+pid}", "!", ":+pid")]
    [InlineData("${$:-x}", "$", ":-x")]
    [InlineData("${?:+r}", "?", ":+r")]
    [InlineData("${-:+dash}", "-", ":+dash")]
    [InlineData("${#:-z}", "#", ":-z")]
    [InlineData("${#!}", "!", "#L")]
    public void Parse_OperatorOnSpecialParameter_IsThatParameter(string word, string name, string suffix)
    {
        var cmd = Assert.IsType<Command.Simple>(BashParser.Parse("echo " + word));
        var sub = Assert.IsType<WordPart.BracedVarSub>(Assert.Single(cmd.Words[1].Parts));
        Assert.Equal(name, sub.Name);
        Assert.Equal(suffix, sub.Suffix);
    }

    /// <summary>`$?` and `$#` are ints in PowerShell; 0 is falsy there, so `${?:-q}` gave q after a success.</summary>
    [Theory]
    [InlineData("echo ${?:-q}", "([string]$global:LASTEXITCODE)")]
    [InlineData("echo ${#:+n}", "([string]")]
    public void Transpile_ColonOperatorOnNumericSpecial_TestsItAsText(string bash, string fragment)
        => Assert.Contains(fragment, Emit(bash));

    public static TheoryData<string> ComputedScalars()
    {
        var data = new TheoryData<string>();
        foreach (string name in PsBash.Core.ShellVariables.Computed)
            if (PsBuild.ShellArrayExpr(name) is null)
                data.Add(name);
        return data;
    }

    /// <summary>
    /// Guard over the whole computed-scalar table: `${SECONDS:+y}` gave "" in the first second (0 is falsy in
    /// PowerShell), and `$RANDOM` can be 0. A name added to ShellVariables.Computed is covered without a new case.
    /// </summary>
    [Theory]
    [MemberData(nameof(ComputedScalars))]
    public void Transpile_ColonOperatorOnComputedScalar_TestsItAsText(string name)
        => Assert.Contains("([string]", Emit($"echo ${{{name}:+y}}"));

    // --- positional slices as unquoted words ------------------------------------------------------

    [Theory]
    [InlineData("echo ${@:2:2}")]
    [InlineData("echo ${*: -1}")]
    public void Transpile_UnquotedPositionalSlice_IsSplitIntoWords(string bash)
        => Assert.Contains("ConvertTo-BashWords", Emit(bash));

    // --- ERR trap scope ---------------------------------------------------------------------------

    /// <summary>A function body hides the caller's ERR trap on entry (ErrTrapInheritance via VarFrameStack).</summary>
    [Fact]
    public void Transpile_FunctionBody_EntersAndExitsTheErrTrapScope()
    {
        string ps = Emit("trap 'echo e' ERR; f() { false; }; f");
        Assert.Contains("[PsBash.Cmdlets.VarFrameStack]::EnterFunction($ExecutionContext.SessionState)", ps);
        Assert.Contains("[PsBash.Cmdlets.VarFrameStack]::ExitFunction($ExecutionContext.SessionState, $__bet)", ps);
    }

    /// <summary>A substitution that sets its own ERR trap runs its statements' checks (`x=$(trap 'echo E' ERR; false)`
    /// captures E); one that does not stays exempt.</summary>
    [Theory]
    [InlineData("trap 'echo E' ERR; false", true)]
    [InlineData("{ trap \"x\" ERR; }; false", true)]
    [InlineData("trap - ERR; false", false)]
    [InlineData("( trap 'x' ERR ); false", false)]
    [InlineData("trap 'x' EXIT; false", false)]
    public void BodySetsErrTrap_OnlyItsOwnLevel(string body, bool expected)
        => Assert.Equal(expected, PsEmitter.BodySetsErrTrap(BashParser.Parse(body)));
}
