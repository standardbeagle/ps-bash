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

    // A sparse/assoc table is an IDictionary: `@($table)` would wrap it as ONE element, so a slice must read .Values.
    [Fact] public void Transpile_ArraySlice_ReadsDictionaryValuesNotTheWrapper()
    {
        var ps = T("echo \"${a[@]: -1}\"");
        Assert.Contains("IDictionary", ps);
        Assert.Contains(".Values", ps);
    }

    [Fact] public void Transpile_IndirectKeysForm_StillListsKeys() => Assert.Contains(".Keys", T("echo \"${!a[@]}\""));

    [Theory]
    [InlineData("echo \"${!a[1]:-d}\"", ":-d}")]
    [InlineData("echo \"${!a[1]#x}\"", "#x}")]
    public void Transpile_IndirectElementWithOperator_AppliesTheOperator(string bash, string leaked)
        => Assert.DoesNotContain(leaked, T(bash), StringComparison.Ordinal);

    // `${!ref}` with ref=LINENO: the runtime cannot know the bash line, so the emitter passes the statement's.
    [Fact] public void Transpile_IndirectRef_PassesTheStatementLine()
    {
        Assert.Contains("-Line 2", T("ref=LINENO\necho ${!ref}"));
        Assert.Contains("-Line 3", T("ref=LINENO\n\necho ${!ref:-x}"));
    }
}
