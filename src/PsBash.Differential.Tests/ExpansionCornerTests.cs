using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>Expansion corners: <c>}</c> as an argument, slices of unset variables and of <c>$@</c>, <c>${arr[i]=d}</c>.</summary>
public class ExpansionCornerTests
{
    [Fact]
    public Task RBrace_AfterCommandWord_IsAnArgument() => AssertOracle.EqualAsync(
        "rbrace() { echo }; }; rbrace; { echo a; }; echo } {");

    [Fact]
    public Task Slice_OfUnsetVariable_IsEmpty() => AssertOracle.EqualAsync(
        "echo -${undef:1:2}- -${undef:3}-; x=abcdef; echo ${x:1:2} ${x:2}");

    [Fact]
    public Task Slice_OfUnsetVariable_UnderNounset_IsUnbound() => AssertOracle.EqualAsync(
        "( set -o nounset; echo -${undef:1:2}-; echo not-reached ) 2>/dev/null; echo status=$?");

    [Fact]
    public Task PositionalSlice_CountsFromDollarZero() => AssertOracle.EqualAsync(
        "set a b c; echo ${@:1:1}; echo ${@:2}; echo ${@: -1}; echo ${@:1}; echo \"${@:2:2}\"; echo ${@:5}; echo x ${@:9} y\n"
        + "r=$(echo ${@:0:1}); echo ${r//\"$0\"/SHELL}; f() { echo ${@:2}; echo ${*:1:2}; }; f x y z");

    [Fact]
    public Task AssignDefault_ToArrayElement_GrowsTheArray() => AssertOracle.EqualAsync(
        "arr=(); echo ${#arr[@]}; : ${arr[0]=x}; echo ${#arr[@]} ${arr[0]}; : ${arr[0]=y} ${arr[2]:=z}; echo \"${arr[@]}\"; : ${s=1} ${t:=2}; echo $s $t");
}
