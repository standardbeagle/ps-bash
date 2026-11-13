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

    // bash forks each pipeline element from the shell, so `$_` in any element is the previous command's last word
    // (shopt -s lastpipe; seq 3 | echo last=$_ prints last=lastpipe).
    [Fact] public void Transpile_RefInsidePipeline_UsesPreviousLastWord()
    {
        var ps = T("shopt -s lastpipe\nseq 3 | echo last=$_");
        Assert.DoesNotContain("BashLastArg", ps);
        Assert.Contains("last=lastpipe", ps);
    }

    // Without lastpipe the pipeline runs outside the shell and leaves `$_` as it was: echo a; seq 2 | cat; echo $_ prints a.
    [Fact] public void Transpile_RefAfterPipeline_KeepsTheWordFromBeforeIt()
    {
        var ps = T("echo a\nseq 2 | cat >/dev/null\necho g=$_");
        Assert.Contains("g=a", ps);
        Assert.DoesNotContain("g=cat", ps);
    }

    // With lastpipe the last element runs in the shell, so `$_` is its last word: bash prints g=cat.
    [Fact] public void Transpile_RefAfterLastpipePipeline_UsesTheLastElementsWord()
        => Assert.Contains("g=cat", T("shopt -s lastpipe\necho a\nseq 2 | cat >/dev/null\necho g=$_"));

    // lastpipe is off until the shopt that sets it: bash prints h=b, then j=r.
    [Fact] public void Transpile_PipelineBeforeTheLastpipeShopt_KeepsTheWordFromBeforeIt()
    {
        var ps = T("echo b\ntrue | echo zz\necho h=$_\nshopt -s lastpipe\necho d\ntrue | echo r\necho j=$_");
        Assert.Contains("h=b", ps);
        Assert.Contains("j=r", ps);
    }
}
