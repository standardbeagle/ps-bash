using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>`command`/`builtin` in front of a declaration builtin only bypass functions: `command export x=1` must assign
/// (not look `export` up as an external command and report "command not found").</summary>
public class DeclarationWrapperTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Theory]
    [InlineData("command export c=1")]
    [InlineData("builtin export c=1")]
    [InlineData("command command export c=1")]
    [InlineData("builtin builtin export c=1")]
    public void Transpile_WrappedExport_AssignsLikeBareExport(string bash) => Assert.Equal(T("export c=1"), T(bash));

    [Fact] public void Transpile_CommandVExport_StaysCommandLookup() => Assert.Contains("Invoke-BashCommand", T("command -v export"));

    [Fact] public void Transpile_BuiltinDeclareArrayLiteral_IsASyntaxError() => Assert.ThrowsAny<ParseException>(() => T("builtin declare a=(x y)"));

    // Behind a wrapper the operands are ordinary words (bash 5.2): `builtin export z=$x` with x='a b' exports z=a and b.
    // The declaration stays a plain command and runs through the run-time eval, each field %q-quoted.
    [Theory]
    [InlineData("builtin export z=$x")]
    [InlineData("command readonly c=$x")]
    [InlineData("\\builtin declare d=$x")]
    [InlineData("'builtin' declare e=$x")]
    [InlineData("command command local f=\"$x\"")]
    public void Parse_WrappedDeclarationWithExpansion_StaysACommand(string bash)
        => Assert.IsType<PsBash.Core.Parser.Ast.Command.Simple>(BashParser.Parse(bash));

    [Fact]
    public void Transpile_WrappedDeclarationWithExpansion_SplitsTheOperand()
    {
        var ps = T("builtin export z=$x");
        Assert.Contains("$__psbash_eval_src", ps);
        Assert.Contains("%q ", ps);
        Assert.DoesNotContain("$env:z =", ps);
    }

    // `declare $y` / `typeset $y` / `export $y` (y='p=1 q=2'): each field is declared at run time.
    [Theory]
    [InlineData("declare $y")]
    [InlineData("typeset $y")]
    [InlineData("export $y")]
    [InlineData("declare -x x $y")]
    public void Transpile_DeclarationOfRuntimeWords_RunsThroughEval(string bash)
        => Assert.Contains("$__psbash_eval_src", T(bash));

    [Theory]
    [InlineData("x", "'x'")]
    [InlineData("it's", "'it'\\''s'")]
    public void BashSingleQuote_QuotesForEval(string text, string expected) => Assert.Equal(expected, PsBuild.BashSingleQuote(text));
}
