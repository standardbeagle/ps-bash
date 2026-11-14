using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>`[ ]` words where a `-a` / `-o` follows another one: one of them is an operand or a dangling connector, a shape
/// only bash's test grammar resolves (`[ -a -a -a -a -a -a ]` is "argument expected", status 2). The static translation read
/// every `-a` as a connector and returned 1 silently; the runtime `test` evaluates these.</summary>
public class SingleBracketRuntimeShapeTests
{
    [Theory]
    [InlineData("[ -a -a -a -a -a -a ]")]
    [InlineData("[ x -o -o -o y ]")]
    public void Transpile_AdjacentConnectorWords_UseTheRuntimeTest(string bash)
        => Assert.Contains("Invoke-BashTest", PsEmitter.Transpile(bash));

    [Fact]
    public void Transpile_PlainAndChain_StaysStatic()
        => Assert.DoesNotContain("Invoke-BashTest", PsEmitter.Transpile("[ a = a -a b = b ]"));
}
