using PsBash.Core.Transpiler;
using Xunit;

namespace PsBash.Core.Tests.Transpiler;

/// <summary>
/// `[ -d /tmp ]` is true in bash everywhere. On Windows /tmp is the temp directory; the emitter rewrote
/// `/tmp/x` but left a bare `/tmp` to the current drive's \tmp, which exists only where someone made it
/// (absent on a CI runner). A bare /tmp operand must go through the runtime path policy like any other.
/// </summary>
public class BareTmpFileTestTests
{
    [Theory]
    [InlineData("[ -d /tmp ] && echo yes")]
    [InlineData("[ -e /tmp ]")]
    [InlineData("if [ -d /tmp ]; then echo y; fi")]
    public void Transpile_FileTestOnBareTmp_GoesThroughTheRuntimePathPolicy(string bash)
    {
        var ps = BashTranspiler.Transpile(bash);
        Assert.Contains("MapPath", ps);
    }

    [Fact]
    public void Transpile_FileTestOnAnOrdinaryLiteral_IsLeftAlone()
    {
        Assert.DoesNotContain("MapPath", BashTranspiler.Transpile("[ -d somedir ]"));
    }
}
