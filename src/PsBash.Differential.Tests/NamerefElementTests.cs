using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>A nameref may name one array element (<c>declare -n r='a[2]'</c>), and a write through a nameref honours its target's readonly.</summary>
public class NamerefElementTests
{
    [Fact]
    public Task Nameref_ToArrayElement_ReadsAndWrites() => AssertOracle.EqualAsync(
        "a=(zero one two three); declare -n ref='a[2]'; echo ref=$ref \"$ref\" ${ref}; ref=X; echo \"${a[@]}\"");

    [Fact]
    public Task Nameref_ToArrayElement_ArithmeticAndCommandSubSubscript() => AssertOracle.EqualAsync(
        "a=(zero one two three); i=1; declare -n r2='a[i+1]'; declare -n r3='a[$(echo 2) + 1]'; echo r2=$r2 r3=$r3");

    [Fact]
    public Task Subscript_WithCommandSub_IsExpandedThenEvaluated() => AssertOracle.EqualAsync(
        "a=(z o t th); echo ${a[$(echo 2) + 1]} \"${a[`echo 1`]}\"");

    [Fact]
    public Task Nameref_WriteToReadonlyTarget_IsRefused() => AssertOracle.EqualAsync(
        "x=X\ntypeset -n -r ref=x\nref=XX\necho ref=$ref\ntypeset -r x\n{ ref=XXX; } 2>/dev/null\necho ref=$ref x=$x");
}
