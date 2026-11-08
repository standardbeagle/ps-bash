using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>`unset ref` on a nameref unsets its target; `unset -n ref` drops the reference. Oracle (bash 5.2):
/// x=X; typeset -n ref=x; unset ref → ref= x= ; typeset -n r=y; unset -n r → y keeps its value.</summary>
public class UnsetNamerefEmitterTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Fact] public void Transpile_UnsetThroughNameref_UnsetsTheTarget()
    {
        var ps = T("x=X; typeset -n ref=x; unset ref");
        Assert.Matches(@"Invoke-BashUnset\s+['""]?x['""]?", ps);
        Assert.DoesNotMatch(@"Invoke-BashUnset\s+['""]?ref", ps);
    }

    [Fact] public void Transpile_UnsetDashN_KeepsTheTargetAndDropsTheReference()
    {
        var ps = T("y=Y; typeset -n r=y; unset -n r; echo $r");
        Assert.DoesNotContain("$y", ps.Substring(ps.IndexOf("echo", StringComparison.Ordinal) < 0 ? 0 : ps.LastIndexOf("Invoke-BashEcho", StringComparison.Ordinal)));
    }

    [Fact] public void Transpile_UnsetPlainVariable_Unchanged()
        => Assert.Matches(@"Invoke-BashUnset\s+['""]?z", T("typeset -n ref=x; unset z"));
}
