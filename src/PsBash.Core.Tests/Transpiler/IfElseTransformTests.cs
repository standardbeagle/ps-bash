using Xunit;
using PsBash.Core.Transpiler;
using PsBash.Core.Transpiler.Transforms;

namespace PsBash.Core.Tests.Transpiler;

public class IfElseTransformTests
{
    private readonly IfElseTransform _transform = new();

    private string Apply(string input)
    {
        var ctx = new TranspileContext(input);
        _transform.Apply(ref ctx);
        return ctx.Result;
    }

    [Fact]
    public void IfThenFi_Transforms()
    {
        Assert.Equal(
            "if ((Test-Path \"file\" -PathType Leaf)) { echo \"exists\" }",
            Apply("if (Test-Path \"file\" -PathType Leaf); then echo \"exists\"; fi"));
    }

    [Fact]
    public void IfElseFi_Transforms()
    {
        Assert.Equal(
            "if ((Test-Path \"file\" -PathType Leaf)) { echo \"yes\" } else { echo \"no\" }",
            Apply("if (Test-Path \"file\" -PathType Leaf); then echo \"yes\"; else echo \"no\"; fi"));
    }

    [Fact]
    public void IfElifElseFi_Transforms()
    {
        Assert.Equal(
            "if ((Test-Path \"a\" -PathType Leaf)) { echo \"a\" } elseif ((Test-Path \"b\" -PathType Leaf)) { echo \"b\" } else { echo \"none\" }",
            Apply("if (Test-Path \"a\" -PathType Leaf); then echo \"a\"; elif (Test-Path \"b\" -PathType Leaf); then echo \"b\"; else echo \"none\"; fi"));
    }

    [Fact]
    public void NoIfStatement_Unchanged()
    {
        var input = "echo hello";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void FullPipeline_IfThenFi()
    {
        var result = BashTranspiler.Transpile("if [ -f file ]; then echo \"exists\"; fi");
        Assert.Equal("if (Test-Path \"file\" -PathType Leaf) { echo \"exists\" }", result);
    }

    [Fact]
    public void FullPipeline_IfElseFi()
    {
        var result = BashTranspiler.Transpile("if [ -f file ]; then echo \"yes\"; else echo \"no\"; fi");
        Assert.Equal("if (Test-Path \"file\" -PathType Leaf) { echo \"yes\" } else { echo \"no\" }", result);
    }

    [Fact]
    public void FullPipeline_IfElifElseFi()
    {
        var result = BashTranspiler.Transpile("if [ -f a ]; then echo \"a\"; elif [ -f b ]; then echo \"b\"; else echo \"none\"; fi");
        Assert.Equal(
            "if (Test-Path \"a\" -PathType Leaf) { echo \"a\" } elseif (Test-Path \"b\" -PathType Leaf) { echo \"b\" } else { echo \"none\" }",
            result);
    }
}
