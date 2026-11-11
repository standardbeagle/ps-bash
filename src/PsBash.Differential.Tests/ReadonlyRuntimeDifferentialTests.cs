using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Names made read-only where the emitter cannot follow them statically are still refused on assignment:
/// <c>readonly $names</c> (the names are run-time data) and <c>readonly</c> inside a function (its static set
/// ended with the function). Every name of a <c>readonly a=(1 2) nv c=(3 4)</c> list is read-only, and
/// <c>declare -p nv</c> shows the unset name as <c>declare -r nv</c>. The fields of <c>readonly $names</c> are
/// names, never code: <c>names='a;echo pwned'</c> ran <c>echo pwned</c>.
/// Each refused assignment runs in its own <c>( … ) 2>/dev/null</c>: the refusal's prefix differs
/// (<c>ps-bash: x:</c> vs <c>bash: line N: x:</c>, a known gap), and bash abandons the rest of the enclosing
/// compound command after it. The status and the value left behind are compared.
/// </summary>
public class ReadonlyRuntimeDifferentialTests
{
    [Theory]
    [InlineData("names='m n'\nreadonly $names\nother=ok\necho \"other=$other\"\n( m=1 n=2 ) 2>/dev/null\necho \"s=$? m=$m n=$n\"")]
    [InlineData("f() { readonly g=1; }\nf\n( g=2 ) 2>/dev/null\necho \"s=$? g=$g\"")]
    [InlineData("h() { local -r q=1; echo \"in q=$q\"; }\nh\nq=5\necho \"q=$q s=$?\"")]
    [InlineData("readonly x=(1 2) nv y=(3 4)\n( nv=x ) 2>/dev/null\necho \"s=$?\"\ndeclare -p x nv y")]
    [InlineData("readonly nv\ndeclare -p nv\nk=x; readonly k; declare -p k")]
    // Run-time words are names, not code: `;`, `$( )` and quotes in the value stay data.
    [InlineData("names='a;echo pwned b'\nreadonly $names 2>/dev/null\necho \"s=$?\"\nv='c=$(echo pwned2)'\nreadonly $v 2>/dev/null\necho \"s=$? c=$c\"")]
    [InlineData("for w in 1a=3 =x 'a b' 'b[7]=8' 'z;'; do readonly \"$w\" 2>/dev/null; echo \"$w s=$?\"; done\nreadonly 1a=3 2>/dev/null; echo s=$?")]
    public Task Readonly_RuntimeNamesAreRefused(string script) => AssertOracle.EqualAsync(script);
}
