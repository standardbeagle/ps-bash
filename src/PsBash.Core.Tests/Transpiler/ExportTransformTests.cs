using Xunit;
using PsBash.Core.Transpiler;
using PsBash.Core.Transpiler.Transforms;

namespace PsBash.Core.Tests.Transpiler;

public class ExportTransformTests
{
    private readonly ExportTransform _transform = new();

    private string Apply(string input)
    {
        var ctx = new TranspileContext(input);
        _transform.Apply(ref ctx);
        return ctx.Result;
    }

    [Fact]
    public void ExportUnquoted_Transforms()
    {
        Assert.Equal("$env:FOO = \"bar\"", Apply("export FOO=bar"));
    }

    [Fact]
    public void ExportQuoted_Transforms()
    {
        Assert.Equal("$env:FOO = \"bar baz\"", Apply("export FOO=\"bar baz\""));
    }

    [Fact]
    public void ExportWithPath_Transforms()
    {
        Assert.Equal("$env:PATH = \"/usr/bin:/bin\"", Apply("export PATH=\"/usr/bin:/bin\""));
    }

    [Fact]
    public void ExportWithUnderscore_Transforms()
    {
        Assert.Equal("$env:MY_VAR = \"val\"", Apply("export MY_VAR=val"));
    }

    [Fact]
    public void ExportQuoted_WithAndChain_TransformsExportOnly()
    {
        Assert.Equal(
            "$env:DEMO = \"it works\" && echo $DEMO",
            Apply("export DEMO=\"it works\" && echo $DEMO"));
    }

    [Fact]
    public void ExportUnquoted_WithAndChain_TransformsExportOnly()
    {
        Assert.Equal(
            "$env:FOO = \"bar\" && echo $FOO",
            Apply("export FOO=bar && echo $FOO"));
    }

    [Fact]
    public void ExportQuoted_WithOrChain_TransformsExportOnly()
    {
        Assert.Equal(
            "$env:FOO = \"bar\" || echo failed",
            Apply("export FOO=\"bar\" || echo failed"));
    }

    [Fact]
    public void NoExport_Unchanged()
    {
        var input = "echo hello";
        Assert.Equal(input, Apply(input));
    }

    // Bare assignment tests

    [Fact]
    public void BareUnquoted_Transforms()
    {
        Assert.Equal("$env:X = \"hello\"", Apply("X=hello"));
    }

    [Fact]
    public void BareQuoted_Transforms()
    {
        Assert.Equal("$env:NAME = \"world\"", Apply("NAME=\"world\""));
    }

    [Fact]
    public void BareUnquoted_NumberValue_Transforms()
    {
        Assert.Equal("$env:count = \"0\"", Apply("count=0"));
    }

    [Fact]
    public void BareUnquoted_WithSemicolonChain_Transforms()
    {
        Assert.Equal("$env:X = \"hello\"; echo $X", Apply("X=hello; echo $X"));
    }

    [Fact]
    public void BareQuoted_WithSemicolonChain_Transforms()
    {
        Assert.Equal("$env:NAME = \"world\"; echo $NAME", Apply("NAME=\"world\"; echo $NAME"));
    }

    [Fact]
    public void BareUnquoted_WithAndChain_Transforms()
    {
        Assert.Equal("$env:X = \"1\" && echo $X", Apply("X=1 && echo $X"));
    }

    [Fact]
    public void BareUnquoted_AfterSemicolon_Transforms()
    {
        Assert.Equal("echo hi; $env:X = \"1\"", Apply("echo hi; X=1"));
    }

    [Fact]
    public void BareUnquoted_AfterAndChain_Transforms()
    {
        Assert.Equal("echo hi && $env:X = \"1\"", Apply("echo hi && X=1"));
    }

    [Fact]
    public void BareUnquoted_AfterOrChain_Transforms()
    {
        Assert.Equal("echo hi || $env:X = \"1\"", Apply("echo hi || X=1"));
    }

    // Negative tests: must NOT match

    [Fact]
    public void ArrayAssignment_NotTransformed()
    {
        var input = "arr=(a b c)";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void InsideSingleQuotes_NotTransformed()
    {
        var input = "awk '{x=1}'";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void EchoArgument_NotTransformed()
    {
        var input = "echo X=hello";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void DoubleEquals_NotTransformed()
    {
        var input = "[ $x == 1 ]";
        Assert.Equal(input, Apply(input));
    }
}
