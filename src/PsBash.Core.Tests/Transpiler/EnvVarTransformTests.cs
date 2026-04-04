using Xunit;
using PsBash.Core.Transpiler;
using PsBash.Core.Transpiler.Transforms;

namespace PsBash.Core.Tests.Transpiler;

public class EnvVarTransformTests
{
    private readonly EnvVarTransform _transform = new();

    private string Apply(string input)
    {
        var ctx = new TranspileContext(input);
        _transform.Apply(ref ctx);
        return ctx.Result;
    }

    [Fact]
    public void SimpleVar_Transforms()
    {
        Assert.Equal("echo $env:FOO", Apply("echo $FOO"));
    }

    [Fact]
    public void MultipleVars_TransformsAll()
    {
        Assert.Equal("$env:A and $env:B", Apply("$A and $B"));
    }

    [Fact]
    public void AlreadyEnvVar_Unchanged()
    {
        var input = "echo $env:FOO";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void PwshBuiltinNull_Unchanged()
    {
        var input = "echo $null";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void PwshBuiltinTrue_Unchanged()
    {
        var input = "echo $true";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void PwshBuiltinFalse_Unchanged()
    {
        var input = "echo $false";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void PwshBuiltinHOME_Unchanged()
    {
        var input = "echo $HOME";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void PwshBuiltinLASTEXITCODE_Unchanged()
    {
        var input = "echo $LASTEXITCODE";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void NoVars_Unchanged()
    {
        var input = "echo hello";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void VarWithUnderscore_Transforms()
    {
        Assert.Equal("echo $env:MY_VAR", Apply("echo $MY_VAR"));
    }
}
