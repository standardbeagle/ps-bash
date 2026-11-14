using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>A simple command's failing redirection. `echo potato &lt; /does/not/exist` never runs echo in bash (status 1,
/// "No such file or directory"); ps-bash ran it with empty stdin. `x=def &gt; /nodir/f` assigns x and then fails the
/// redirect (status 1); ps-bash wrapped the assignment in the redirect, so it was never made.</summary>
public class SimpleCommandRedirectFailureTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Theory]
    [InlineData("echo potato < /does/not/exist")]
    [InlineData("<filename echo")]
    [InlineData("cmd < \"$f\"")]
    public void Transpile_InputRedirectFromFile_ChecksTheFileFirst(string bash)
    {
        var ps = T(bash);
        Assert.Contains("Invoke-BashTest -e $__psbash_ir", ps);
        Assert.Contains("No such file or directory", ps);
    }

    [Fact]
    public void Transpile_InputRedirectFromDevice_HasNoFileCheck()
        => Assert.DoesNotContain("$__psbash_ir", T("cat < /dev/null"));

    [Fact]
    public void Transpile_BareAssignmentWithOutputRedirect_AssignsThenOpensTheTarget()
    {
        var ps = T("x=def > /does/not/exist1");
        int assign = ps.IndexOf("def", System.StringComparison.Ordinal);
        int touch = ps.IndexOf("does/not/exist1", System.StringComparison.Ordinal);
        Assert.True(assign >= 0 && touch > assign, ps);
    }
}
