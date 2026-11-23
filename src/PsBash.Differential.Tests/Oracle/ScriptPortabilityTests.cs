using Xunit;

namespace PsBash.Differential.Tests.Oracle;

/// <summary>
/// A differential row whose file names differ only in case is refused (ScriptPortability): on Windows and macOS
/// <c>2&gt;E</c> and <c>2&gt;e</c> are one file, so <c>{ echo y &gt;&amp;5 2&gt;e; } 2&gt;E; [ -e e ]</c> saw the outer E
/// and failed the s24u gate as if ps-bash had made a redirect after a failed one. A file Windows cannot name
/// (<c>touch 'x(a|b)y'</c>) is refused too. ps-bash-specific (the harness).
/// </summary>
public class ScriptPortabilityTests
{
    [Theory]
    [InlineData("{ echo y >&5 2>e; echo \"s=$?\"; } 2>E\nerr E\n[ -e e ] && echo e-made", "E", "e")]
    [InlineData("echo a >F; echo b >>f", "F", "f")]
    [InlineData("touch Ab ab", "Ab", "ab")]
    [InlineData("mkdir -p Sub; [ -d sub ] && echo y", "Sub", "sub")]
    [InlineData("cat <Data; echo x 2>data", "Data", "data")]
    public void CaseCollision_TwoNamesThatDifferOnlyInCase_AreRefused(string script, string a, string b)
    {
        var pair = ScriptPortability.CaseCollision(script);
        Assert.NotNull(pair);
        Assert.Equal(new HashSet<string> { a, b }, new HashSet<string> { pair!.Value.A, pair.Value.B });
        Assert.NotNull(ScriptPortability.Refusal(script));
    }

    [Theory]
    [InlineData("{ echo y >&5 2>inner; echo \"s=$?\"; } 2>E\nerr E\n[ -e inner ] && echo inner-made")]
    [InlineData("echo a >f; echo b >>f; cat f")]                 // one name, written twice
    [InlineData("exec 3>&1; echo x >&3 2>&1; exec 3>&-")]        // dups name no file
    [InlineData("cat <<E\ne\nE\ncat <<< e")]                     // here-doc delimiter and here-string text
    [InlineData("diff <(echo a) <(echo A)")]                     // process substitution
    [InlineData("touch ab ac ba; echo !(a)*")]
    public void CaseCollision_NamesThatAreOneFileEverywhere_AreKept(string script)
        => Assert.Null(ScriptPortability.CaseCollision(script));

    [Theory]
    [InlineData("touch 'x(a|b)y' xa; echo x*", "x(a|b)y")]
    [InlineData("echo a >'q?'", "q?")]
    [InlineData("mkdir -p \"c:d\"", "c:d")]
    [InlineData("[ -e 'a*b' ] || echo none", "a*b")]
    public void NotAWindowsName_AFileWindowsCannotName_IsRefused(string script, string name)
    {
        Assert.Equal(name, ScriptPortability.NotAWindowsName(script));
        Assert.NotNull(ScriptPortability.Refusal(script));
    }

    [Theory]
    [InlineData("touch 'x@(a)y' xa xb; d='@(a)'; echo x\"$d\"*; echo x@(a|b)*; e='(a|b)'; [[ 'x(a|b)y' == x\"$e\"* ]] && echo lit")]
    [InlineData("echo a | cat >f; [ -e f ] && echo y")]           // a pipe is not a name
    [InlineData("echo '*' >star.txt; cat star.txt")]               // the glob is data, the file name is plain
    [InlineData("set -- u 'v w'; printf '<%s>' \"<$@>\"; echo; printf '<%s>' k\"$@\"; echo")] // quoted < > are text
    [InlineData("echo \"x >F\" 'touch A' \\>B; echo y >f; touch a b")]  // quoted or escaped: no file F, A or B
    public void NotAWindowsName_PortableNames_AreKept(string script)
        => Assert.Null(ScriptPortability.Refusal(script));
}
