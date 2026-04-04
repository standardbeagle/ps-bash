using Xunit;
using PsBash.Core.Transpiler;
using PsBash.Core.Transpiler.Transforms;

namespace PsBash.Core.Tests.Transpiler;

public class ParameterExpansionTransformTests
{
    private readonly ParameterExpansionTransform _transform = new();

    private string Apply(string input)
    {
        var ctx = new TranspileContext(input);
        _transform.Apply(ref ctx);
        return ctx.Result;
    }

    [Fact]
    public void DefaultValue_Transforms()
    {
        Assert.Equal("$($env:HOME ?? '/home/user')", Apply("${HOME:-/home/user}"));
    }

    [Fact]
    public void DefaultValue_EmptyDefault_Transforms()
    {
        Assert.Equal("$($env:VAR ?? '')", Apply("${VAR:-}"));
    }

    [Fact]
    public void StringLength_Transforms()
    {
        Assert.Equal("$(($env:name).Length)", Apply("${#name}"));
    }

    [Fact]
    public void ReplaceAll_Transforms()
    {
        Assert.Equal("$($env:path -replace ':',';')", Apply("${path//:/;}"));
    }

    [Fact]
    public void ReplaceFirst_Transforms()
    {
        Assert.Equal("$($env:file -replace 'old','new')", Apply("${file/old/new}"));
    }

    [Fact]
    public void RemoveShortestPrefix_Transforms()
    {
        Assert.Equal("$($env:file -replace '^*.','')", Apply("${file#*.}"));
    }

    [Fact]
    public void RemoveLongestPrefix_Transforms()
    {
        Assert.Equal("$($env:path -replace '^*/','')", Apply("${path##*/}"));
    }

    [Fact]
    public void RemoveShortestSuffix_Transforms()
    {
        Assert.Equal("$($env:file -replace '.*$','')", Apply("${file%.*}"));
    }

    [Fact]
    public void RemoveLongestSuffix_Transforms()
    {
        Assert.Equal("$($env:file -replace '.*$','')", Apply("${file%%.*}"));
    }

    [Fact]
    public void UppercaseAll_Transforms()
    {
        Assert.Equal("$(($env:name).ToUpper())", Apply("${name^^}"));
    }

    [Fact]
    public void LowercaseAll_Transforms()
    {
        Assert.Equal("$(($env:name).ToLower())", Apply("${name,,}"));
    }

    [Fact]
    public void NoExpansion_Unchanged()
    {
        var input = "echo hello";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void EmbeddedInEcho_Transforms()
    {
        Assert.Equal("echo $($env:HOME ?? '/tmp')", Apply("echo ${HOME:-/tmp}"));
    }

    [Fact]
    public void MultipleExpansions_Transforms()
    {
        Assert.Equal(
            "echo $($env:USER ?? 'nobody') $(($env:name).ToUpper())",
            Apply("echo ${USER:-nobody} ${name^^}"));
    }
}
