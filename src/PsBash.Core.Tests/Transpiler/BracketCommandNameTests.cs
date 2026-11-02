using PsBash.Core.Transpiler;
using Xunit;

namespace PsBash.Core.Tests.Transpiler;

/// <summary>
/// A command word that starts with `[` (but is not the test builtin) is a command name bash reports as
/// "not found" (status 127). Emitted bare, PowerShell read it as a type literal and the whole script failed to parse.
/// </summary>
public class BracketCommandNameTests
{
    [Theory]
    [InlineData("['a bc d']")]
    [InlineData("[abc]; echo after")]
    [InlineData("['abc']\n['   ']")]
    public void Transpile_BracketLeadingCommandName_IsCalledAsAQuotedName(string bash)
    {
        var ps = BashTranspiler.Transpile(bash);
        Assert.Contains("& '[", ps);
    }

    [Fact]
    public void Transpile_TestBuiltin_StillATest()
    {
        var ps = BashTranspiler.Transpile("[ -n x ] && echo y");
        Assert.DoesNotContain("& '[", ps);
    }
}
