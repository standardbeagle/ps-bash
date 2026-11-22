using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A quoted or escaped command name only escapes alias expansion: the builtin still runs (bash 5.2). REGRESSION: the
/// builtins the emitter implements were looked up as programs, <c>command not found</c> (127). Primitives:
/// <c>BashParser.CommandWordName</c> (declaration builtins), <c>EscapedControlBuiltin</c> (break/continue/return/exit),
/// <c>PsEmitter.UnquoteBuiltinName</c> (the rest).
/// </summary>
public class QuotedBuiltinNameDifferentialTests
{
    private static Task Same(string script) => AssertOracle.EqualAsync(script, TimeSpan.FromSeconds(30));

    [SkippableFact]
    public Task Declarations_QuotedOrEscaped() => Same(
        "f() { 'local' w=3; \\local w2=4; \"declare\" -i n=2+3; echo \"w=$w $w2 n=$n\"; }; f\n"
        + "echo \"out=${w-unset}${w2-unset}${n-unset}\"\n"
        + "\\export QX=1; 'export' QY=2; echo \"QX=$QX QY=$QY\"\n"
        + "\"declare\" -i di=2+3; echo \"di=$di\"\n");

    [SkippableFact]
    public Task ControlAndOtherBuiltins_Quoted() => Same(
        "f() { 'return' 6; }; f; echo \"r=$?\"\n"
        + "for i in 1 2; do \"break\"; done; echo \"b=$i\"\n"
        + "for i in 1 2; do \\continue; echo no; done; echo \"c=$i\"\n"
        + "\\set -- p q; echo \"set: $*\"\n"
        + "'shift'; echo \"shift: $*\"\n"
        + "\\eval 'echo ev'\n"
        + "'read' rv <<< 'rr'; echo \"read: $rv\"\n"
        + "( 'exit' 4 ); echo \"e=$?\"\n");

    [SkippableFact]
    public Task Quoted_SkipsTheAlias() => Same(
        "shopt -s expand_aliases\nalias echo='printf \"A:%s\\n\"'\n"
        + "echo hi; \\echo hi2; 'echo' hi3; \"echo\" hi4\n");
}
