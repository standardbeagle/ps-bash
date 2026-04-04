using Xunit;
using PsBash.Core.Transpiler;
using PsBash.Core.Transpiler.Transforms;

namespace PsBash.Core.Tests.Transpiler;

public class ArithmeticTransformTests
{
    private readonly ArithmeticTransform _transform = new();

    private string Apply(string input)
    {
        var ctx = new TranspileContext(input);
        _transform.Apply(ref ctx);
        return ctx.Result;
    }

    [Fact]
    public void LiteralAddition_Transforms()
    {
        Assert.Equal("$([int](2 + 3))", Apply("$((2 + 3))"));
    }

    [Fact]
    public void BareVariables_ConvertToEnvVar()
    {
        Assert.Equal("$([int]($env:x * $env:y))", Apply("$((x * y))"));
    }

    [Fact]
    public void LiteralDivision_Transforms()
    {
        Assert.Equal("$([int](10 / 3))", Apply("$((10 / 3))"));
    }

    [Fact]
    public void EmbeddedInEchoCommand_Transforms()
    {
        Assert.Equal("echo $([int](2 + 3))", Apply("echo $((2 + 3))"));
    }

    [Fact]
    public void EmbeddedInAssignment_Transforms()
    {
        Assert.Equal("result=$([int](10 / 3))", Apply("result=$((10 / 3))"));
    }

    [Fact]
    public void MixedVarsAndLiterals_Transforms()
    {
        Assert.Equal("$([int]($env:x + 1))", Apply("$((x + 1))"));
    }

    [Fact]
    public void NoArithmetic_Unchanged()
    {
        var input = "echo hello";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void SingleDollarParen_Unchanged()
    {
        var input = "$(date)";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void Subtraction_Transforms()
    {
        Assert.Equal("$([int](10 - 3))", Apply("$((10 - 3))"));
    }

    [Fact]
    public void Modulo_Transforms()
    {
        Assert.Equal("$([int](10 % 3))", Apply("$((10 % 3))"));
    }
}
