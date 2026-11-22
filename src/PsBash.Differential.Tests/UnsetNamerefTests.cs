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

    /// <summary>
    /// unset's options cluster (<c>-vn</c>, <c>-nf</c>), <c>--</c> ends them, and so does the first name. REGRESSION
    /// (abe9d17b): the emitter and the cmdlet each matched only the words -f/-v/-n, so <c>unset -vn r</c> unset r's target
    /// and left r a nameref.
    /// </summary>
    [SkippableFact]
    public Task UnsetClusters_DashVNAndDashNF() => AssertOracle.EqualAsync(
        "declare -n a=ya; ya=1; unset -vn a; declare -p a ya\n"
        + "declare -n b=yb; yb=1; unset -nv b; declare -p b yb\n"
        + "declare -n c=yc; yc=1; unset -fn c; declare -p c yc\n"
        + "declare -n d=yd; yd=1; unset -nf d; declare -p d yd\n"
        + "declare -n e=ye; ye=1; unset -n -n e; declare -p e ye");

    /// <summary><c>-f</c> with <c>-v</c> is an error whichever comes last: status 1, nothing unset (no last-wins).</summary>
    [SkippableFact]
    public Task UnsetDashFWithDashV_UnsetsNothing() => AssertOracle.EqualAsync(
        "g() { echo G; }; g=1\n"
        + "unset -fv g; echo \"st=$?\"\n"
        + "unset -vf g; echo \"st=$?\"\n"
        + "unset -v -f g; echo \"st=$?\"\n"
        + "unset -fvn g; echo \"st=$?\"; type -t g; echo \"g=$g\"");

    /// <summary>The first name and <c>--</c> end the options: <c>unset v -f g</c> unsets the variables v and g.</summary>
    [SkippableFact]
    public Task UnsetOptions_EndAtTheFirstNameOrDoubleDash() => AssertOracle.EqualAsync(
        "h() { :; }; v=1; h=2; unset v -f h; echo \"st=$? v=[${v-unset}] h=[${h-unset}]\"; type -t h\n"
        + "w=1; unset -- -v w; echo \"st=$? w=[${w-unset}]\"\n"
        + "x=1; unset -n -- x; echo \"st=$? x=[${x-unset}]\"\n"
        + "k() { :; }; k=1; unset -- -f k; echo \"st=$? k=[${k-unset}]\"; type -t k");
}
