using PsBash.Core.Transpiler;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// <c>${arr[i]:-WORD}</c> expands WORD like the plain-variable form does. It used to stay a raw slice, so
/// <c>${BASH_SOURCE[0]:-$0}</c> (the common "my own path" fallback) printed a literal <c>$0</c>.
/// </summary>
public class ArrayElementDefaultWordTests
{
    [Theory]
    [InlineData("echo \"${a[3]:-$0}\"")]
    [InlineData("echo \"${BASH_SOURCE[0]:-$0}\"")]
    [InlineData("echo \"${a[3]:-$(echo cs)}\"")]
    [InlineData("echo \"${a[3]:+$0}\"")]
    public void Transpile_ArrayElementDefaultWord_IsExpandedNotLiteral(string bash)
    {
        var ps = BashTranspiler.Transpile(bash);
        Assert.DoesNotContain("'$0'", ps);
        Assert.DoesNotContain("$(echo cs)'", ps);
        // The word is emitted through normal word expansion, so `$0` becomes the positional-0 expression.
        if (bash.Contains("$0")) Assert.Contains("BashPositional0", ps);
    }

    [Fact]
    public void Transpile_BashSourceIndex_ReadsTheGlobalArray()
    {
        var ps = BashTranspiler.Transpile("echo \"${BASH_SOURCE[0]}\" \"${#BASH_SOURCE[@]}\"");
        Assert.Contains("$global:BASH_SOURCE['0']", ps);
    }
}
