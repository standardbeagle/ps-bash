using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>A nameref may name one array element (<c>declare -n r='a[2]'</c>), and a write through a nameref honours its target's readonly.</summary>
public class NamerefElementTests
{
    [SkippableFact]
    public Task Nameref_ToArrayElement_ReadsAndWrites() => AssertOracle.EqualAsync(
        "a=(zero one two three); declare -n ref='a[2]'; echo ref=$ref \"$ref\" ${ref}; ref=X; echo \"${a[@]}\"");

    [SkippableFact]
    public Task Nameref_ToArrayElement_ArithmeticAndCommandSubSubscript() => AssertOracle.EqualAsync(
        "a=(zero one two three); i=1; declare -n r2='a[i+1]'; declare -n r3='a[$(echo 2) + 1]'; echo r2=$r2 r3=$r3");

    [SkippableFact]
    public Task Subscript_WithCommandSub_IsExpandedThenEvaluated() => AssertOracle.EqualAsync(
        "a=(z o t th); echo ${a[$(echo 2) + 1]} \"${a[`echo 1`]}\"");

    [SkippableFact]
    public Task Nameref_WriteToReadonlyTarget_IsRefused() => AssertOracle.EqualAsync(
        "x=X\ntypeset -n -r ref=x\nref=XX\necho ref=$ref\ntypeset -r x\n{ ref=XXX; } 2>/dev/null\necho ref=$ref x=$x");

    [SkippableFact]
    public Task Nameref_IndirectExpansion_IsTheTargetName() => AssertOracle.EqualAsync(
        "foo=FOO\nx=foo\nref=x\necho ref=$ref \"!ref=${!ref}\"\ntypeset -n ref\necho ref=$ref\necho \"!ref=${!ref}\" ${!ref}");

    [SkippableFact]
    public Task Nameref_Cycle_WriteFails() => AssertOracle.EqualAsync(
        "typeset -n ref1=ref2\ntypeset -n ref2=ref1\necho defined $?\n{ ref1=z; } 2>/dev/null\necho mutated $?");
}
