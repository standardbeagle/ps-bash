using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>`unset ref` on a nameref unsets the variable it names; `unset -n ref` removes only the reference.</summary>
public class UnsetNamerefTests
{
    [Fact] public Task Unset_ThroughNameref_UnsetsTarget() => AssertOracle.EqualAsync("x=X; typeset -n ref=x; echo ref=$ref; unset ref; echo \"ref=$ref x=$x\"");
    [Fact] public Task UnsetDashN_RemovesOnlyTheReference() => AssertOracle.EqualAsync("y=Y; typeset -n r=y; unset -n r; echo \"y=$y r=$r\"");
    [Fact] public Task Unset_ChainOfNamerefs_UnsetsFinalTarget() => AssertOracle.EqualAsync("z=Z; typeset -n a=z; typeset -n b=a; unset b; echo \"z=[$z] a=[$a] b=[$b]\"");
}
