using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>An unquoted `${a[i]}` that is unset or empty vanishes as an argument; a quoted one stays an empty argument.</summary>
public class UnquotedElementWordTests
{
    [Fact] public Task Unset_Element_DropsTheArgument() => AssertOracle.EqualAsync("a=(0 1); printf '[%s]' x ${a[7]} y; echo");
    [Fact] public Task Empty_Element_DropsTheArgument() => AssertOracle.EqualAsync("a=(0 \"\" 2); printf '[%s]' x ${a[1]} y; echo");
    [Fact] public Task Quoted_Element_KeepsTheArgument() => AssertOracle.EqualAsync("a=(0 1); printf '[%s]' x \"${a[7]}\" y; echo");
    [Fact] public Task Element_WithSpaces_IsSplit() => AssertOracle.EqualAsync("a=(\"p q\" r); printf '[%s]' ${a[0]}; echo");
    [Fact] public Task NegativeIndex_OnSparseArray() => AssertOracle.EqualAsync("a=(0 1 2 3 4); unset 'a[1]' 'a[4]'; printf '[%s]' ${a[-1]} ${a[-3]} ${a[-4]}; echo");
    [Fact] public Task Assoc_Element_DropsWhenMissing() => AssertOracle.EqualAsync("declare -A m=([k]=v); printf '[%s]' ${m[k]} ${m[z]} end; echo");
}

public class LoneUnquotedArrayAllTests
{
    [Fact] public Task Elements_WithSpaces_AreSplit() => AssertOracle.EqualAsync("b=(\"x y\" z); printf '[%s]' ${b[@]}; echo");
    [Fact] public Task Elements_WithGlobs_AreExpanded() => AssertOracle.EqualAsync("cd \"$(mktemp -d)\"; touch p.A q.A r.B; g=('*.A' '*.B'); echo ${g[@]}");
    [Fact] public Task QuotedForm_KeepsElements() => AssertOracle.EqualAsync("b=(\"x y\" z); printf '[%s]' \"${b[@]}\"; echo");
    [Fact] public Task EmptyArray_IsNoArgument() => AssertOracle.EqualAsync("c=(); printf '[%s]' ${c[@]}; echo");
    [Fact] public Task Assoc_Values_AreSplit() => AssertOracle.EqualAsync("declare -A m=([k]=\"p q\"); printf '[%s]' ${m[@]}; echo");
    [Fact] public Task Indices_AreListed() => AssertOracle.EqualAsync("b=(u v w); printf '[%s]' ${!b[@]}; echo");
}
