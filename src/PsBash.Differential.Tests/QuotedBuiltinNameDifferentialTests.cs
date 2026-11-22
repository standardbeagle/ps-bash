using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A quoted or escaped command name only escapes alias expansion: the builtin still runs (bash 5.2). REGRESSION: the
/// builtins the emitter implements were looked up as programs, <c>command not found</c> (127). Primitives:
/// <c>EscapedControlBuiltin</c> (break/continue/return/exit), <c>PsEmitter.UnquoteBuiltinName</c> (the rest; a declaration
/// builtin is the <c>builtin NAME</c> wrapper form, <c>BashParser.QuotedDeclarationStaysACommand</c>).
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

    // REGRESSION (review of the port): a quoted declaration builtin's operands are ORDINARY words, as behind `builtin`
    // (x='a b': `'export' v=$x` exports v=a and b); it took the bare declaration's path and printed [a b].
    [SkippableFact]
    public Task Declarations_QuotedWithAnExpansion_AreOrdinaryWords() => Same(
        "x='a b'; 'export' v3=$x; echo \"[$v3]\"\n"
        + "f3() { \\local w=$x; echo \"[$w]\"; \"declare\" d=$x; echo \"[$d]\"; }; f3\n"
        + "'readonly' r3=$x; echo \"[$r3]\"\n"
        + "( eval \"'declare' -a A=(x y)\" ) 2>/dev/null; echo \"arr=$?\"\n"
        + "( eval \"g() { 'local' a=(1 2); }\" ) 2>/dev/null; echo \"loc=$?\"\n"
        + "( eval '\\export Q=(1)' ) 2>/dev/null; echo \"exp=$?\"\n");

    // REGRESSION (review of the port): an aliased quoted name stayed quoted and ran as a program.
    [SkippableFact]
    public Task QuotedBuiltin_WithAnAlias_IsTheBuiltin() => Same(
        "shopt -s expand_aliases\nalias eval='echo no'\nalias local='echo LL'\n"
        + "'eval' 'echo x'; \\eval 'echo y'; c='echo z'; \"eval\" \"$c\"\n"
        + "f6() { 'local' z=5; echo \"z=$z\"; }; f6\n");
}
