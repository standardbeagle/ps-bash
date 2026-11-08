using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>`$_` after a plain-literal last word in the same list: `mkdir -p a/b && cd $_`. Oracle (bash 5.2): cd lands in a/b.</summary>
public class LastArgInListTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Fact] public void Transpile_AndList_UsesPreviousLastWord()
    {
        var ps = T("mkdir -p a/b && cd $_");
        Assert.DoesNotContain("BashLastArg", ps);
        Assert.Contains("a/b", ps);
    }

    [Fact] public void Transpile_SemicolonList_QuotedRefUsesPreviousLastWord()
        => Assert.DoesNotContain("BashLastArg", T("mkdir d; cd \"$_\""));

    // A last word that expands (command sub) must not be re-evaluated: fall back to the runtime variable.
    [Fact] public void Transpile_PreviousLastWordExpands_KeepsRuntimeVariable()
        => Assert.Contains("BashLastArg", T("echo $(date); echo $_"));

    [Fact] public void Transpile_NoPreviousCommand_KeepsRuntimeVariable()
        => Assert.Contains("BashLastArg", T("echo $_"));
}
