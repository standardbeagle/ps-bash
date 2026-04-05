using Xunit;
using PsBash.Core.Transpiler;
using PsBash.Core.Transpiler.Transforms;

namespace PsBash.Core.Tests.Transpiler;

public class WhileLoopTransformTests
{
    private readonly WhileLoopTransform _transform = new();

    private string Apply(string input)
    {
        var ctx = new TranspileContext(input);
        _transform.Apply(ref ctx);
        return ctx.Result;
    }

    [Fact]
    public void WhileRead_TransformsToForEachObject()
    {
        Assert.Equal(
            "ForEach-Object { if ($_.PSObject.Properties['BashText']) { $_.BashText } else { \"$_\" } } | ForEach-Object { $_ -split \"`n\" } | ForEach-Object { echo $_ }",
            Apply("while read line; do echo $env:line; done"));
    }

    [Fact]
    public void WhileRead_ReplacesVarReferences()
    {
        Assert.Equal(
            "ForEach-Object { if ($_.PSObject.Properties['BashText']) { $_.BashText } else { \"$_\" } } | ForEach-Object { $_ -split \"`n\" } | ForEach-Object { process $_ }",
            Apply("while read item; do process $env:item; done"));
    }

    [Fact]
    public void WhileRead_DoesNotReplaceSimilarVarNames()
    {
        Assert.Equal(
            "ForEach-Object { if ($_.PSObject.Properties['BashText']) { $_.BashText } else { \"$_\" } } | ForEach-Object { $_ -split \"`n\" } | ForEach-Object { echo $env:liner $_ }",
            Apply("while read line; do echo $env:liner $env:line; done"));
    }

    [Fact]
    public void WhileCondition_WrapsInParens()
    {
        Assert.Equal(
            "while ($env:count -lt 10) { echo $env:count }",
            Apply("while $env:count -lt 10; do echo $env:count; done"));
    }

    [Fact]
    public void WhileCondition_PreservesExistingParens()
    {
        Assert.Equal(
            "while (Test-Path /tmp/file) { sleep 1 }",
            Apply("while (Test-Path /tmp/file); do sleep 1; done"));
    }

    [Fact]
    public void Until_NegatesCondition()
    {
        Assert.Equal(
            "while (-not (Test-Path /tmp/done)) { sleep 1 }",
            Apply("until (Test-Path /tmp/done); do sleep 1; done"));
    }

    [Fact]
    public void Until_WrapsAndNegates()
    {
        Assert.Equal(
            "while (-not ($env:x -eq 0)) { echo waiting }",
            Apply("until $env:x -eq 0; do echo waiting; done"));
    }

    [Fact]
    public void NoWhileLoop_Unchanged()
    {
        var input = "echo hello";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void FullPipeline_WhileReadLine()
    {
        var result = BashTranspiler.Transpile("while read line; do echo $line; done");
        Assert.Equal("ForEach-Object { if ($_.PSObject.Properties['BashText']) { $_.BashText } else { \"$_\" } } | ForEach-Object { $_ -split \"`n\" } | ForEach-Object { echo $_ }", result);
    }

    [Fact]
    public void FullPipeline_WhileCondition()
    {
        var result = BashTranspiler.Transpile(
            "while (Test-Path \"/var/lock\" -PathType Leaf); do sleep 1; done");
        Assert.Equal(
            "while (Test-Path \"/var/lock\" -PathType Leaf) { sleep 1 }", result);
    }

    [Fact]
    public void FullPipeline_UntilWithFileTest()
    {
        var result = BashTranspiler.Transpile("until [ -f /tmp/done ]; do sleep 1; done");
        Assert.Contains("while (-not (", result);
        Assert.Contains("Test-Path", result);
    }
}
