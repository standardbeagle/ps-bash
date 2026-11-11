using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

public class CommandWordStatusTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Fact] public void EmptyQuotedCommand_ReportsNotFound() => Assert.Contains("= 127", T("''"));
    [Fact] public void EmptyQuotedCommand_IsNotCalledWithAmpersand() => Assert.DoesNotContain("& ''", T("\"\""));
    [Fact] public void CommandSubOnlyWord_IsNotInvoked() => Assert.DoesNotContain("& $(", T("$(false)"));
    [Fact] public void BraceGroupInputRedirect_ChecksTheFileBeforeRunning() => Assert.Contains("Invoke-BashTest -e", T("{ echo b; } < nofile.txt"));
    [Fact] public void SubshellInputRedirect_ChecksTheFileBeforeRunning() => Assert.Contains("Invoke-BashTest -e", T("( echo b ) < nofile.txt"));
    [Fact] public void ReturnLiteral_IsReducedModulo256() => Assert.Contains("= 1;", T("f() { return 257; }"));

    // A bare name with PowerShell syntax in it is called as a quoted name, not emitted as an expression.
    [Theory]
    [InlineData("{ls", "& '{ls'")]
    [InlineData("a,b x", "& 'a,b' x")]
    [InlineData("%x", "& '%x'")]
    [InlineData("@foo", "& '@foo'")]
    public void Transpile_OddBareCommandName_UsesCallOperator(string bash, string expected) => Assert.Contains(expected, T(bash));

    [Theory]
    [InlineData("git-lfs version", "git-lfs version")]
    [InlineData("x86_64-gcc -v", "x86_64-gcc -v")]
    public void Transpile_PlainCommandName_StaysBare(string bash, string expected) => Assert.Contains(expected, T(bash));
}
