using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>`unset ref` on a nameref unsets the variable it names; `unset -n ref` removes only the reference.</summary>
public class UnsetNamerefTests
{
    [SkippableFact] public Task Unset_ThroughNameref_UnsetsTarget() => AssertOracle.EqualAsync("x=X; typeset -n ref=x; echo ref=$ref; unset ref; echo \"ref=$ref x=$x\"");
    [SkippableFact] public Task UnsetDashN_RemovesOnlyTheReference() => AssertOracle.EqualAsync("y=Y; typeset -n r=y; unset -n r; echo \"y=$y r=$r\"");
    [SkippableFact] public Task Unset_ChainOfNamerefs_UnsetsFinalTarget() => AssertOracle.EqualAsync("z=Z; typeset -n a=z; typeset -n b=a; unset b; echo \"z=[$z] a=[$a] b=[$b]\"");

    /// <summary>REGRESSION: Invoke-BashUnset skipped <c>-n</c>, so the run-time table still held q: <c>set</c> and
    /// <c>declare -p</c> listed <c>q=y</c> after <c>unset -n q</c>.</summary>
    [SkippableFact]
    public Task UnsetDashN_TheReferenceIsListedNoMore() => AssertOracle.EqualAsync(
        "y=1; declare -n q=y; unset -n q; set | grep -c '^q='; declare -p q >/dev/null 2>&1; echo \"st=$? y=$y\"; q=7; echo \"q=$q y=$y\"");

    /// <summary>bash 5.2: <c>unset -n</c> on a name that is no nameref changes nothing (no variable, no function).</summary>
    [SkippableFact]
    public Task UnsetDashN_OnAPlainName_ChangesNothing() => AssertOracle.EqualAsync(
        "v=3; unset -n v; echo \"[${v-unset}] st=$?\"; f() { echo fn; }; unset -n f; f");

    [SkippableFact]
    public Task UnsetDashN_ReferenceToAnUnsetVariable_GoesAway() => AssertOracle.EqualAsync(
        "declare -n ref=nothere; unset -n ref; echo \"[${ref-unset}] st=$?\"; declare -p ref >/dev/null 2>&1; echo \"st=$?\"; "
        + "ref=v; echo \"ref=$ref nothere=[${nothere-unset}]\"");

    /// <summary>-n holds for the names after it whatever -v says; -f overrides it (<c>unset -n -f p</c> keeps p a reference).</summary>
    [SkippableFact]
    public Task UnsetDashN_WithDashVAndDashF() => AssertOracle.EqualAsync(
        "y=1; declare -n q=y; unset -v -n q; echo \"y=$y q=[${q-unset}]\"; declare -n p=y; unset -n -f p; p=2; echo \"y=$y\"");

    /// <summary>REGRESSION: the subshell snapshot did not hold the nameref table, so a reference made in <c>( )</c>
    /// outlived it (<c>declare -p r</c> found it, and <c>r=3</c> wrote y).</summary>
    [SkippableFact]
    public Task Subshell_NamerefMadeInside_EndsWithIt() => AssertOracle.EqualAsync(
        "y=1; ( declare -n r=y; r=2; echo \"in y=$y\" ); declare -p r >/dev/null 2>&1; echo \"st=$? y=$y\"; r=3; echo \"r=$r y=$y\"");

    /// <summary>REGRESSION: the attribute table (run time) and the emitter's -i / -u / -A sets outlived the subshell, so
    /// <c>x=1+1</c> after <c>( declare -i x )</c> stored 2.</summary>
    [SkippableFact]
    public Task Subshell_AttributesMadeInside_EndWithIt() => AssertOracle.EqualAsync(
        "x=1; ( declare -i x; declare -u z; declare -A m ); x=1+1; z=abc; m=(k v); echo \"x=$x z=$z\"; declare -p m");

    /// <summary>
    /// <c>typeset +n ref</c> leaves ref a plain variable holding the target NAME: <c>typeset -n ref=x</c> kept the name
    /// only in a side table, so ref was left unset (<c>declare -p</c>: not found). <c>local +n</c> ran as a command.
    /// </summary>
    [SkippableFact]
    public Task PlusN_LeavesTheTargetNameAsTheValue() => AssertOracle.EqualAsync(
        "x=foo\ntypeset -n ref=x; typeset +n ref; echo \"a: [$ref]\"\n"
        + "declare -n r2=x; declare +n r2; declare -p r2\n"
        + "declare -n r3=x; r3=new; echo \"x=$x\"; declare +n r3; echo \"r3=$r3\"\n"
        + "f() { local -n lr=x; local +n lr; echo \"lr=$lr\"; }; f; echo \"x=$x lr=[${lr-unset}]\"\n"
        + "p=plain; typeset +n p; echo \"p=$p\"");
}
