using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// bash expands an array subscript it reads from a VALUE as a word, so a command substitution in it runs: an element
/// named in an arithmetic variable's value (<c>y='a[$(cmd)]+5'; $((y))</c>), an indirect target
/// (<c>x='a[$(cmd)]'; ${!x}</c>), an <c>unset</c> operand (<c>unset 'a[$(cmd)]'</c>). ps-bash treated the text as
/// arithmetic only and failed ("command substitution is not evaluable here"). Only subscripts expand: a value that is
/// itself <c>$(...)</c> is still an arithmetic syntax error, and an unterminated quote fails the expansion.
/// </summary>
public class SubscriptCommandSubstitutionTests
{
    private static Task Eq(string script) => AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(30));

    [SkippableFact]
    public Task SubscriptFromValue_RunsItsCommandSubstitution() => Eq(
        "cd \"$(mktemp -d)\"\n"
        + "a=(10 20 30)\n"
        + "x='a[$(echo 1 | tee log)]'; echo \"${!x}\"; cat log\n"
        + "q='a[${c:-2}]'; echo \"${!q}\"; q2='a[$(( 3 - 2 ))]'; echo \"${!q2}\"\n"
        + "y='a[$(echo 2)]+5'; echo $((y))\n"
        + "z='b[$(echo 3)]=7'; echo $((z)) \"${b[3]}\"\n"
        + "declare -A m=([k]=v [j]=w)\n"
        + "r='m[$(echo k)]'; echo \"${!r}\"\n"
        + "unset -v 'm[$(echo j)]'; echo \"${#m[@]}\"\n"
        + "unset 'a[$(echo 0)]'; echo \"${#a[@]}\" \"${a[1]}\"\n");

    [SkippableFact]
    public Task NonSubscriptValueOrBadWord_DoesNotRun() => Eq(
        "cd \"$(mktemp -d)\"\n"
        + "a=(10 20 30)\n"
        + "v='$(echo 5 > ran)'; ( echo $((v)) ) 2>/dev/null; echo s=$?; test -f ran && echo RAN\n"
        + "w='a[1\"]'; ( echo \"${!w}\" ) 2>/dev/null; echo s=$?\n"
        + "p='a[<(echo x)]'; ( echo \"${!p}\" ) 2>/dev/null; echo s=$?\n"
        + "echo end\n");
}
