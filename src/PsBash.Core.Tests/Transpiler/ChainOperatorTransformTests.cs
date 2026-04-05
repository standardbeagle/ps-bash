using Xunit;
using PsBash.Core.Transpiler;
using PsBash.Core.Transpiler.Transforms;

namespace PsBash.Core.Tests.Transpiler;

public class ChainOperatorTransformTests
{
    private readonly ChainOperatorTransform _transform = new();

    private string Apply(string input)
    {
        var ctx = new TranspileContext(input);
        _transform.Apply(ref ctx);
        return ctx.Result;
    }

    [Fact]
    public void ParenExpr_WithAnd_WrapsInVoid()
    {
        Assert.Equal(
            "[void](Test-Path \"./README.md\" -PathType Leaf) && echo \"exists\"",
            Apply("(Test-Path \"./README.md\" -PathType Leaf) && echo \"exists\""));
    }

    [Fact]
    public void ParenExpr_WithOr_WrapsInVoid()
    {
        Assert.Equal(
            "[void](Test-Path \"./src\" -PathType Container) || echo \"missing\"",
            Apply("(Test-Path \"./src\" -PathType Container) || echo \"missing\""));
    }

    [Fact]
    public void Assignment_WithAnd_WrapsInVoid()
    {
        Assert.Equal(
            "[void]($env:FOO = \"bar\") && echo done",
            Apply("$env:FOO = \"bar\" && echo done"));
    }

    [Fact]
    public void Assignment_WithOr_WrapsInVoid()
    {
        Assert.Equal(
            "[void]($env:FOO = \"bar\") || echo failed",
            Apply("$env:FOO = \"bar\" || echo failed"));
    }

    [Fact]
    public void IsNullOrEmpty_WithAnd_WrapsInVoid()
    {
        Assert.Equal(
            "[void]([string]::IsNullOrEmpty($VAR)) && echo \"empty\"",
            Apply("([string]::IsNullOrEmpty($VAR)) && echo \"empty\""));
    }

    [Fact]
    public void NoChainOperator_Unchanged()
    {
        var input = "(Test-Path \"./README.md\" -PathType Leaf)";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void PlainCommand_Unchanged()
    {
        var input = "echo hello && echo world";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void AlreadyWrapped_NotDoubleWrapped()
    {
        var input = "[void](Test-Path \"./README.md\" -PathType Leaf) && echo ok";
        Assert.Equal(input, Apply(input));
    }
}
