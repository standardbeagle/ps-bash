using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>`a[1 + 2]=x`: a subscript with blanks is still ONE assignment word. Oracle (bash 5.2): it assigns element 3.</summary>
public class SpacedSubscriptAssignTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Fact] public void Transpile_SpacedSubscript_IsElementAssignment() => Assert.Contains("Set-BashElement", T("a[1 + 2]=x"));
    [Fact] public void Transpile_SpacedSubscriptAppend_IsElementAssignment() => Assert.Contains("-Append", T("a[ i + 1 ]+=x"));
    // Not an assignment: no `=` after the `]`, so `a[1` stays a command word (bash runs it and fails with 127).
    [Fact] public void Transpile_SpacedSubscriptWithoutEquals_IsNotAssignment() => Assert.DoesNotContain("Set-BashElement", T("a[1 + 2] x"));
    [Fact] public void Transpile_ArgumentWordIsNotAnAssignment() => Assert.DoesNotContain("Set-BashElement", T("echo a[1 + 2]=x"));
}

/// <summary>`a[1]=v cmd`: an element prefix is not an environment binding; the command still runs and keeps its own status.</summary>
public class ElementPrefixAssignTests
{
    [Fact] public void Transpile_ElementPrefix_RunsTheCommandWithoutEnvWrap()
    {
        var ps = PsEmitter.Transpile("a[1]=v nosuchcmd")!;
        Assert.DoesNotContain("__psbash_ea_", ps);
        Assert.Contains("nosuchcmd", ps);
    }
}
