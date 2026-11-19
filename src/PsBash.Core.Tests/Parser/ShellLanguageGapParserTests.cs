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
