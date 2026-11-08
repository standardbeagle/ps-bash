using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// oils array.spec gaps: <c>len=${#a[@]}</c> glued to a literal printed <c>$a.Count</c> text, <c>${#a[N]}</c> was empty,
/// a scalar read as <c>${s[@]}</c> / <c>${s[0]}</c> / <c>${#s[@]}</c> was empty or crashed, a negative slice offset past
/// the start clamped instead of being empty, <c>unset a[-N]</c> out of range was silent, and
/// <c>[[ -v a[i] ]]</c> did not evaluate a variable holding an expression.
/// </summary>
public class ArrayLengthScalarTests
{
    [Theory]
    [InlineData("a=(0 1 2 3); echo len=${#a[@]}")]
    [InlineData("a=(0 1 2 3); unset a[-1]; echo len=${#a[@]}; unset a[-1]; echo len=${#a[@]}")]
    [InlineData("b=(x yy zzz); echo ${#b[0]} ${#b[1]} ${#b[2]}")]
    [InlineData("s=abc; echo ${s[@]}; echo ${s[*]}; echo ${s[0]}")]
    [InlineData("c='12 3'; echo ${#c[0]} ${#c[@]}")]
    [InlineData("a=(1 2 3 4); echo \"(${a[*]: -1})\" \"(${a[*]: -4})\" \"(${a[*]: -5})\"")]
    [InlineData("a=(1); unset -v 'a[-2]' 2>/dev/null; echo status=$?")]
    [InlineData("array=('' nonempty); i='0+1'; [[ -v array[i] ]]; echo $?; i='0+2'; [[ -v array[i] ]]; echo $?")]
    public Task Matches_Bash(string script) => AssertOracle.EqualAsync(script);

    // An arithmetic element write past the end leaves the gap UNSET (a sparse array), it does not pad with empty strings.
    [Fact] public Task Arith_IndexAssignPastEnd_IsSparse() => AssertOracle.EqualAsync("declare -a a; (( a[99]=1 )); echo \"${#a[@]} ${!a[@]}\"; b=(x); (( b[5]=2 )); (( b[5]+=3 )); echo \"${#b[@]} ${!b[@]} ${b[5]}\"");
    [Fact] public Task Slice_SparseArrayTail() => AssertOracle.EqualAsync("a=(x); a[9]=y; echo \"${a[@]: -1};${a[@]:0:1}\"");
}
