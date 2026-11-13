using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>`unset ref` on a nameref unsets the variable it names; `unset -n ref` removes only the reference.</summary>
public class UnsetNamerefTests
{
    [Fact] public Task Unset_ThroughNameref_UnsetsTarget() => AssertOracle.EqualAsync("x=X; typeset -n ref=x; echo ref=$ref; unset ref; echo \"ref=$ref x=$x\"");
    [Fact] public Task UnsetDashN_RemovesOnlyTheReference() => AssertOracle.EqualAsync("y=Y; typeset -n r=y; unset -n r; echo \"y=$y r=$r\"");
    [Fact] public Task Unset_ChainOfNamerefs_UnsetsFinalTarget() => AssertOracle.EqualAsync("z=Z; typeset -n a=z; typeset -n b=a; unset b; echo \"z=[$z] a=[$a] b=[$b]\"");

    /// <summary>
    /// <c>typeset +n ref</c> leaves ref a plain variable holding the target NAME: <c>typeset -n ref=x</c> kept the name
    /// only in a side table, so ref was left unset (<c>declare -p</c>: not found). <c>local +n</c> ran as a command.
    /// </summary>
    [Fact]
    public Task PlusN_LeavesTheTargetNameAsTheValue() => AssertOracle.EqualAsync(
        "x=foo\ntypeset -n ref=x; typeset +n ref; echo \"a: [$ref]\"\n"
        + "declare -n r2=x; declare +n r2; declare -p r2\n"
        + "declare -n r3=x; r3=new; echo \"x=$x\"; declare +n r3; echo \"r3=$r3\"\n"
        + "f() { local -n lr=x; local +n lr; echo \"lr=$lr\"; }; f; echo \"x=$x lr=[${lr-unset}]\"\n"
        + "p=plain; typeset +n p; echo \"p=$p\"");
}
