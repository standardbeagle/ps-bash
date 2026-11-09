using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>A bare array name (<c>$a</c>, <c>${a}</c>, <c>${a@Q}</c>, <c>${a:-d}</c>) reads element 0 / key "0", as in bash.</summary>
public class ArrayBareNameTests
{
    [Fact]
    public Task Indexed_BareNameIsElementZero() => AssertOracle.EqualAsync(
        "a=(p q); echo \"$a|${a}|${a@Q}|${a^^}|${a:-d}|${#a}|${a/p/Z}\"; echo $a");

    [Fact]
    public Task Assoc_BareNameIsKeyZero() => AssertOracle.EqualAsync(
        "declare -A A=([0]=zero [k]=v); echo \"[${A}] [${A@Q}] [${A:-d}] [${#A}] [${A^^}]\"; unset 'A[0]'; echo \"[${A}] [${A:-d}]\"");

    [Fact]
    public Task QuotedTransform_OfWholeArray() => AssertOracle.EqualAsync(
        "a=(x 'y z'); echo ${a[@]@Q}; echo \"${a[@]@Q}\"");

    [Fact]
    public Task UnquotedElement_UnsetIsNoWord() => AssertOracle.EqualAsync(
        "b=(0 1); unset 'b[-1]'; b+=(2 3); echo [${b[3]}] x${b[9]}y");
}
