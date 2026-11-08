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
