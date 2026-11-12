using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// <c>${!a[@]-d}</c> / <c>${!a[*]:-d}</c>: with an operator, <c>[@]</c> is indirection through the joined elements
/// (an unset array: "invalid indirect expansion"; no elements: unset, so the default). It printed the keys followed
/// by the literal <c>-d}</c>. Also <c>${!u[1]-d}</c> of an unset array, which threw "Cannot index into a null array".
/// </summary>
public class IndirectThroughArrayTests
{
    [Fact]
    public Task IndirectAllElements_WithOperator() => AssertOracle.EqualAsync(
        "(echo \"${!a[@]-default}\") 2>/dev/null; echo status=$?\n"
        + "a=(x); (echo \"${!a[@]-default}\"); echo status=$?\n"
        + "a=(x y z); (echo \"${!a[@]-default}\") 2>/dev/null; echo status=$?\n"
        + "a=(); (echo \"${!a[@]-d}\"); echo s=$?\n"
        + "a=(\"\"); (echo \"${!a[@]-d}\") 2>/dev/null; echo s=$?\n"
        + "a=(b); b=; echo \"[${!a[*]:-d}]\" \"[${!a[@]+set}]\"\n"
        + "b=1; echo \"${!a[@]#1}x\"\n"
        + "IFS=:; a=(b c); (echo \"${!a[*]-d}\") 2>/dev/null; echo s=$?; IFS=' '\n"
        + "a=(x y); echo \"${!a[@]}\" \"${!a[*]}\"");

    // A failing indirect word abandons ANY command (status 1), not only echo: a function or program ran with it empty.
    [Fact]
    public Task FailedIndirect_AbandonsFunctionsAndPrograms() => AssertOracle.EqualAsync(
        "f() { echo called \"$@\"; }\na=(x y)\n(f \"${!a[@]-d}\") 2>/dev/null; echo s=$?\n(cat \"${!a[@]-d}\") 2>/dev/null; echo s=$?\n"
        + "(nosuchcmd \"${!u[@]-d}\") 2>/dev/null; echo s=$?\nr=x; x=ok; f \"${!r}\" \"pre-${!r:-d}\"; echo s=$?\n"
        + "b='bad name'; (f \"${!b}\") 2>/dev/null; echo s=$?");

    [Fact]
    public Task IndirectElementOfUnsetArray() => AssertOracle.EqualAsync(
        "(echo \"${!u[1]-d}\") 2>/dev/null; echo s=$?\nu=(); (echo \"${!u[1]-d}\"); echo s=$?\nk=(p q); p=7; echo \"${!k[0]}\" \"${!k[1]-none}\"");
}
