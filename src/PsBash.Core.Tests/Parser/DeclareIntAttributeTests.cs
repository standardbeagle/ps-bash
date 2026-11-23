using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// bash: <c>declare -i n=5; declare n+=010; echo $n</c> is 13 and <c>declare n=010</c> is 8: the integer attribute is
/// the variable's, so a later <c>declare</c> without <c>-i</c> still evaluates. ps-bash appended the text (5010).
/// </summary>
public class DeclareIntAttributeTests
{
    [Theory]
    [InlineData("declare -i n=5; declare n+=010")]
    [InlineData("declare -i n=5; declare n=010")]
    public void Transpile_DeclareOfAnIntegerVariable_EvaluatesTheValue(string bash)
    {
        var ps = PsEmitter.Transpile(bash)!;
        int second = ps.IndexOf("$env:n = ", ps.IndexOf("$env:n = ", System.StringComparison.Ordinal) + 1, System.StringComparison.Ordinal);
        Assert.True(second > 0, ps);
        Assert.StartsWith("$env:n = [string](Invoke-BashArith", ps[second..]);
    }

    [Fact]
    public void Transpile_DeclarePlusIOfAnIntegerVariable_StoresTheText()
        => Assert.DoesNotContain("Invoke-BashArith 'n+(", PsEmitter.Transpile("declare -i n=5; declare +i n+=010")!);
}
