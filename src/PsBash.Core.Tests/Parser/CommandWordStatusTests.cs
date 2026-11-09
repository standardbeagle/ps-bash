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
}
