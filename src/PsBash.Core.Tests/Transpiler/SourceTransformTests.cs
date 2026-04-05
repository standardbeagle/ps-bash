using Xunit;
using PsBash.Core.Transpiler;
using PsBash.Core.Transpiler.Transforms;

namespace PsBash.Core.Tests.Transpiler;

public class SourceTransformTests
{
    private readonly SourceTransform _transform = new();

    private string Apply(string input)
    {
        var ctx = new TranspileContext(input);
        _transform.Apply(ref ctx);
        return ctx.Result;
    }

    [Fact]
    public void SourceRelativePath_Transforms()
    {
        Assert.Equal(". ./config.sh", Apply("source ./config.sh"));
    }

    [Fact]
    public void SourceHomePath_Transforms()
    {
        Assert.Equal(". ~/.profile", Apply("source ~/.profile"));
    }

    [Fact]
    public void DotSourced_Unchanged()
    {
        var input = ". ./setup.sh";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void SourceAbsolutePath_Transforms()
    {
        Assert.Equal(". /etc/profile", Apply("source /etc/profile"));
    }

    [Fact]
    public void NoSource_Unchanged()
    {
        var input = "echo hello";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void SourceInsideWord_Unchanged()
    {
        var input = "opensource ./file";
        Assert.Equal(input, Apply(input));
    }
}
