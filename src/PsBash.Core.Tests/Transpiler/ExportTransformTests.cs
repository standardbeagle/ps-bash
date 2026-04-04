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
    public void NoExport_Unchanged()
    {
        var input = "echo hello";
        Assert.Equal(input, Apply(input));
    }
}
