using Xunit;
using PsBash.Core.Transpiler;
using PsBash.Core.Transpiler.Transforms;

namespace PsBash.Core.Tests.Transpiler;

public class HomePathTransformTests
{
    private readonly HomePathTransform _transform = new();

    private string Apply(string input)
    {
        var ctx = new TranspileContext(input);
        _transform.Apply(ref ctx);
        return ctx.Result;
    }

    [Fact]
    public void HomePath_Transforms()
    {
        Assert.Equal("ls $HOME\\.config", Apply("ls ~/.config"));
    }

    [Fact]
    public void HomePathAtStart_Transforms()
    {
        Assert.Equal("cat $HOME\\file", Apply("cat ~/file"));
    }

    [Fact]
    public void HomeInsideQuotes_Unchanged()
    {
        var input = "echo \"~/path\"";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void NoHomePath_Unchanged()
    {
        var input = "echo hello";
        Assert.Equal(input, Apply(input));
    }
}
