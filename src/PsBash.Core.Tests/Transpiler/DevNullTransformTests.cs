using Xunit;
using PsBash.Core.Transpiler;
using PsBash.Core.Transpiler.Transforms;

namespace PsBash.Core.Tests.Transpiler;

public class DevNullTransformTests
{
    private readonly DevNullTransform _transform = new();

    private string Apply(string input)
    {
        var ctx = new TranspileContext(input);
        _transform.Apply(ref ctx);
        return ctx.Result;
    }

    [Fact]
    public void StderrToDevNull_Transforms()
    {
        Assert.Equal("cmd 2>$null", Apply("cmd 2> /dev/null"));
    }

    [Fact]
    public void StdoutToDevNull_Transforms()
    {
        Assert.Equal("cmd >$null", Apply("cmd > /dev/null"));
    }

    [Fact]
    public void BothRedirects_Transforms()
    {
        Assert.Equal("cmd >$null 2>&1", Apply("cmd > /dev/null 2>&1"));
    }

    [Fact]
    public void DevNullAsArgument_Transforms()
    {
        Assert.Equal("cat $null", Apply("cat /dev/null"));
    }

    [Fact]
    public void NoDevNull_Unchanged()
    {
        var input = "echo hello";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void StderrNoSpace_Transforms()
    {
        Assert.Equal("cmd 2>$null", Apply("cmd 2>/dev/null"));
    }
}
