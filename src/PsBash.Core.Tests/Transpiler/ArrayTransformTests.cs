using Xunit;
using PsBash.Core.Transpiler;
using PsBash.Core.Transpiler.Transforms;

namespace PsBash.Core.Tests.Transpiler;

public class ArrayTransformTests
{
    private readonly ArrayTransform _transform = new();

    private string Apply(string input)
    {
        var ctx = new TranspileContext(input);
        _transform.Apply(ref ctx);
        return ctx.Result;
    }

    [Fact]
    public void ArrayAssignment_Transforms()
    {
        Assert.Equal("$arr = @('apple','banana','cherry')", Apply("arr=(apple banana cherry)"));
    }

    [Fact]
    public void ArrayAssignment_SingleItem_Transforms()
    {
        Assert.Equal("$x = @('one')", Apply("x=(one)"));
    }

    [Fact]
    public void ArrayElementAccess_Transforms()
    {
        Assert.Equal("$arr[0]", Apply("${arr[0]}"));
    }

    [Fact]
    public void ArrayElementAccess_HigherIndex_Transforms()
    {
        Assert.Equal("$arr[42]", Apply("${arr[42]}"));
    }

    [Fact]
    public void ArrayExpandAll_AtSign_Transforms()
    {
        Assert.Equal("$arr", Apply("${arr[@]}"));
    }

    [Fact]
    public void ArrayExpandAll_Star_Transforms()
    {
        Assert.Equal("$arr", Apply("${arr[*]}"));
    }

    [Fact]
    public void ArrayLength_Transforms()
    {
        Assert.Equal("$arr.Count", Apply("${#arr[@]}"));
    }

    [Fact]
    public void ArrayAppend_Transforms()
    {
        Assert.Equal("$arr += 'date'", Apply("arr+=(date)"));
    }

    [Fact]
    public void EmbeddedInEcho_Transforms()
    {
        Assert.Equal("echo $arr[0]", Apply("echo ${arr[0]}"));
    }

    [Fact]
    public void MultiplePatterns_Transforms()
    {
        Assert.Equal(
            "echo $arr[0] $arr.Count",
            Apply("echo ${arr[0]} ${#arr[@]}"));
    }

    [Fact]
    public void NoArraySyntax_Unchanged()
    {
        var input = "echo hello";
        Assert.Equal(input, Apply(input));
    }
}
