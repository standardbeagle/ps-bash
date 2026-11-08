using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>`a=([2]=p [5]=q)` keeps the gaps unset; `declare -p` sees a subshell's local array and a nameref's attribute line.</summary>
public class IndexedArrayLiteralTests
{
    [Fact] public Task SubscriptedLiteral_KeepsGaps() => AssertOracle.EqualAsync("declare -a b=([2]=p [5]=\"q r\"); declare -p b; echo \"${#b[@]} ${!b[@]}\"");
    [Fact] public Task SubscriptedLiteral_ContiguousIsDense() => AssertOracle.EqualAsync("c=([0]=a [1]=b [2]=c); declare -p c; echo \"${#c[@]}\"");
    [Fact] public Task SubscriptedLiteral_ExpansionValueAndLastWins() => AssertOracle.EqualAsync("v=zz; e=([1]=$v [4]=\"a b\" [1]=w); declare -p e");
    [Fact] public Task SubscriptedLiteral_AppendContinuesAfterHighest() => AssertOracle.EqualAsync("d=([3]=x); d+=(y); declare -p d");
    [Fact] public Task AssocLiteral_NumericAndWordKeys() => AssertOracle.EqualAsync("declare -A m=([1]=one [x]=ex); declare -p m");
    [Fact] public Task DeclareP_InsideSubshell_SeesLocalArray() => AssertOracle.EqualAsync("arr=(1 2 3); x=\"$(arr=(); arr[3]= arr[4]=foo; declare -p arr)\"; echo \"$x\"; eval -- \"$x\"; declare -p arr");
    [Fact] public Task DeclareP_Nameref_PrintsAttributeLine() => AssertOracle.EqualAsync("arr=(1); declare -n r=arr; declare -p r");
    // `${a[i]+x}` tests SET, not non-empty: an element holding "" is set.
    [Fact] public Task ParamAlt_EmptyArrayElementIsSet() => AssertOracle.EqualAsync("a=(1 \"\" x); echo \"[${a[1]+set}] [${a[1]-unset}] [${a[1]:+set}] [${a[1]:-def}] [${a[5]+set}] [${a[5]-unset}]\"");
    // wc pads every count to the digit width of the files' total size.
    [Fact] public Task Wc_MultipleFiles_UsesCommonWidth() => AssertOracle.EqualAsync("cd \"$(mktemp -d)\"; printf 'a\\nb\\n' > f1; for i in $(seq 1 30); do echo \"line number $i of the bigger file\"; done > f2; wc -l f1 f2; wc f1 f2; wc -l f1; wc -lw f1");
    // A sparse array's slice offset is a SUBSCRIPT, not a position; a negative offset counts back from one past the highest.
    [Fact] public Task Slice_SparseOffsetIsSubscript() => AssertOracle.EqualAsync("a=(v{0..9}); unset -v 'a[2]' 'a[3]' 'a[4]' 'a[7]'; for o in 0 2 3 5 9 10 -1 -2 -5 -9 -11; do echo \"[$o:${a[@]:$o}]\"; done; for l in 0 1 3 20; do echo \"[${a[@]:3:$l}]\"; done");
    // `${a[@]:-w}`: a lone empty element is null, two are not.
    [Fact] public Task ParamDefault_ArrayAllEmptyElements() => AssertOracle.EqualAsync("a1=(); a2=(\"\"); a3=(\"\" \"\"); a4=(x); for n in 1 2 3 4; do eval \"echo \\\"[\\${a$n[@]-U}] [\\${a$n[@]:-E}] [\\${a$n[@]+P}] [\\${a$n[@]:+Q}]\\\"\"; done");
}
