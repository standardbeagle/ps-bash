using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// bash's variable contexts (the oils <c>[bash_unset]</c> cases) are tracked at run time by
/// <c>PsBash.Cmdlets.VarFrameStack</c>; the emitter's part is to report each <c>local</c> and each temp binding that can
/// reach a <c>local</c>/<c>unset</c> (a function call, eval, source). The rules themselves are tested in the Cmdlets
/// tests (VarFrameStackTests), the end result in the differential test.
/// </summary>
public class VarFrameEmitTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Fact]
    public void Transpile_LocalWithoutValue_TakesTheFrameStacksValue()
        => Assert.Contains("$env:v = " + PsBuild.DeclareLocal("v"), T("f() { local v; echo \"$v\"; }"));

    [Fact]
    public void Transpile_LocalWithValue_RegistersTheFrameBeforeAssigning()
    {
        var ps = T("f() { local v=x; }");
        int declare = ps.IndexOf("[void]" + PsBuild.DeclareLocal("v"), StringComparison.Ordinal);
        Assert.True(declare >= 0, ps);
        Assert.True(ps.IndexOf("$env:v = ", declare, StringComparison.Ordinal) > declare, ps);
    }

    [Fact]
    public void Transpile_TempBindingOnAFunctionCall_PushesAFrameTheCalleeOwns()
    {
        var ps = T("f() { unset v; }; v=t f");
        Assert.Contains(PsBuild.PushTempFrame("v"), ps);
        Assert.Contains(PsBuild.RestoreTempBinding("v"), ps);
        Assert.DoesNotContain("$env:v = $__saved_v", ps);
    }

    [Fact]
    public void Transpile_FunctionLocal_RestoresThroughTheFrameStack()
        // unlocal() { unset -v "$1"; }; f() { local v=l; unlocal v; v=new; }: after f, v is new (the local's frame went).
        => Assert.Contains("if ($null -ne $__lsd_v) { " + PsBuild.RestoreLocal("v") + " }", T("f() { local v=l; }"));

    [Theory]
    [InlineData("v=t eval 'unset v'")]
    [InlineData("v=t source ./x.sh")]
    [InlineData("v=t . ./x.sh")]
    public void Transpile_TempBindingOnEvalOrSource_SavesOnTheRuntimeStack(string bash)
    {
        // `v=1 eval 'v=2 eval ...'`: both run in one PowerShell scope, a `$__saved_v` would be overwritten.
        var ps = T(bash);
        Assert.Contains(PsBuild.BindSameScope("v"), ps);
        Assert.Contains(PsBuild.UnbindSameScope("v"), ps);
        Assert.DoesNotContain("$__saved_v", ps);
    }

    [Fact]
    public void Transpile_LiteralEvalBodyUnderABinding_ReadsTheBoundValue()
    {
        // The inlined body runs once v=t is in force; only eval's own words (`"echo $v"`) expand before it.
        Assert.DoesNotContain("__psbash_ea_v", T("v=t eval 'echo $v'"));
        Assert.Contains("__psbash_ea_v", T("v=t eval \"echo $v\""));
    }

    [Theory]
    [InlineData("v=t echo hi")]
    [InlineData("IFS=, read -r a b <<< 'x,y'")]
    [InlineData("v=t notafunction")]
    public void Transpile_TempBindingOnOtherCommands_PushesNoFrame(string bash)
    {
        var ps = T(bash);
        Assert.DoesNotContain("VarFrameStack", ps);
        Assert.Contains("$__saved_", ps);
    }
}
