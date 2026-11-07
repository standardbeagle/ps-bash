using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>`declare foo/bar=1` / `export 1x=2` must report "not a valid identifier" (status 1) instead of emitting
/// `$env:foo/bar` (a PowerShell parse error that kills the whole script).</summary>
public class InvalidIdentifierEmitterTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Theory]
    [InlineData("declare foo/bar=1", "foo/bar=1")]
    [InlineData("export 1x=2", "1x=2")]
    [InlineData("readonly a-b=3", "a-b=3")]
    [InlineData("local x.y=4", "x.y=4")]
    public void Transpile_InvalidName_ReportsAndNeverAssigns(string bash, string operand)
    {
        var ps = T(bash);
        Assert.Contains("not a valid identifier", ps);
        Assert.Contains(operand, ps);
        Assert.DoesNotContain("$env:" + operand.Split('=')[0], ps);
    }

    [Fact] public void Transpile_ValidOperandBesideInvalid_StillAssigned() => Assert.Contains("ok", T("declare ok=1 a-b=2"));

    [Fact] public void Transpile_ValidName_NoError() => Assert.DoesNotContain("not a valid identifier", T("export A_1=2"));

    [Fact] public void Transpile_FunctionForm_NotTreatedAsVariable() => Assert.DoesNotContain("not a valid identifier", T("declare -f my-func"));

    [Fact] public void Transpile_PlainDeclareNoValue_MarksScalar() => Assert.Contains("MarkAttribute('x', '-')", T("declare x"));

    [Fact] public void Transpile_DeclareArrayNoValue_MarkedUnsetArray() => Assert.Contains("MarkAttribute('x', '~')", T("declare -a x"));
}