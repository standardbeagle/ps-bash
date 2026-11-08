using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>`exit` operand errors and `exec --`. Oracle (bash 5.2): `exit 7 8 9` is "too many arguments" with status 1 and the
/// shell stays; `exit foo` is "numeric argument required" and leaves with 2; `exec -- cmd` runs cmd.</summary>
public class ExitExecArgsEmitterTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Fact] public void Transpile_ExitTooManyArgs_ReportsAndDoesNotExit()
    {
        var ps = T("exit 7 8 9");
        Assert.Contains("exit: too many arguments", ps);
        Assert.DoesNotContain("exit 7", ps);
    }

    [Fact] public void Transpile_ExitNonNumeric_LeavesWithTwo()
    {
        var ps = T("exit invalid");
        Assert.Contains("invalid: numeric argument required", ps);
        Assert.Contains("exit 2", ps);
    }

    [Theory]
    [InlineData("exit 3")]
    [InlineData("exit -1")]
    public void Transpile_ExitNumeric_NoDiagnostic(string bash)
        => Assert.DoesNotContain("numeric argument required", T(bash));

    [Fact] public void Transpile_ExecDashDash_DropsTheDashes()
        => Assert.DoesNotContain("'--'", T("exec -- echo hi"));
}
