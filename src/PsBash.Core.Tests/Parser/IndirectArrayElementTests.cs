using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>`${!a[1]}` is indirection through one array element (`a[1]` binds first); only `[@]` / `[*]` keep the
/// keys meaning. Oracle (bash 5.2): a=(asdf x) x=xx → `${!a[1]}` = xx.</summary>
public class IndirectArrayElementTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Fact] public void Transpile_IndirectThroughElement_ReadsTheElementNotTheKeys()
    {
        var ps = T("echo \"${!a[1]}\"");
        Assert.Contains("Get-BashIndirectValue", ps);
        Assert.DoesNotContain(".Keys", ps);
    }

    [Fact] public void Transpile_IndirectKeysForm_StillListsKeys() => Assert.Contains(".Keys", T("echo \"${!a[@]}\""));

    [Theory]
    [InlineData("echo \"${!a[1]:-d}\"", ":-d}")]
    [InlineData("echo \"${!a[1]#x}\"", "#x}")]
    public void Transpile_IndirectElementWithOperator_AppliesTheOperator(string bash, string leaked)
        => Assert.DoesNotContain(leaked, T(bash), StringComparison.Ordinal);
}