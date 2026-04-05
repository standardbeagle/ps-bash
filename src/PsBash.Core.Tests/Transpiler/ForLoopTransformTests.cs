using Xunit;
using PsBash.Core.Transpiler;
using PsBash.Core.Transpiler.Transforms;

namespace PsBash.Core.Tests.Transpiler;

public class ForLoopTransformTests
{
    private readonly ForLoopTransform _transform = new();

    private string Apply(string input)
    {
        var ctx = new TranspileContext(input);
        _transform.Apply(ref ctx);
        return ctx.Result;
    }

    [Fact]
    public void Numbers_CommaSeparated()
    {
        Assert.Equal(
            "foreach ($i in 1,2,3) { echo $i }",
            Apply("for i in 1 2 3; do echo $i; done"));
    }

    [Fact]
    public void Words_QuotedCommaSeparated()
    {
        Assert.Equal(
            "foreach ($f in 'apple','banana','cherry') { echo $f }",
            Apply("for f in apple banana cherry; do echo $f; done"));
    }

    [Fact]
    public void GlobPattern_WrappedInResolvePath()
    {
        Assert.Equal(
            "foreach ($f in (Resolve-Path *.txt)) { cat $f }",
            Apply("for f in *.txt; do cat $f; done"));
    }

    [Fact]
    public void SingleItem_PassedThrough()
    {
        Assert.Equal(
            "foreach ($x in items) { echo $x }",
            Apply("for x in items; do echo $x; done"));
    }

    [Fact]
    public void NoForLoop_Unchanged()
    {
        var input = "echo hello";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void FullPipeline_NumbersList()
    {
        var result = BashTranspiler.Transpile("for i in 1 2 3; do echo $i; done");
        Assert.Equal("foreach ($i in 1,2,3) { echo $i }", result);
    }

    [Fact]
    public void FullPipeline_WordsList()
    {
        var result = BashTranspiler.Transpile("for f in apple banana; do echo $f; done");
        Assert.Equal("foreach ($f in 'apple','banana') { echo $f }", result);
    }

    [Fact]
    public void FullPipeline_GlobList()
    {
        var result = BashTranspiler.Transpile("for f in *.txt; do cat $f; done");
        Assert.Equal("foreach ($f in (Resolve-Path *.txt)) { cat $f }", result);
    }

    [Fact]
    public void FullPipeline_LoopVarNotEnvVar()
    {
        var result = BashTranspiler.Transpile("for i in 1 2 3; do echo $i; done");
        Assert.Contains("$i", result);
        Assert.DoesNotContain("$env:i", result);
    }

    [Fact]
    public void FullPipeline_SimilarVarNameNotClobbered()
    {
        var result = BashTranspiler.Transpile("for i in 1 2; do echo $idx $i; done");
        Assert.Contains("$env:idx", result);
        Assert.Contains("echo $env:idx $i", result);
    }
}
