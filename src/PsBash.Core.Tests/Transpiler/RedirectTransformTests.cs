using Xunit;
using PsBash.Core.Transpiler;
using PsBash.Core.Transpiler.Transforms;

namespace PsBash.Core.Tests.Transpiler;

public class RedirectTransformTests
{
    private readonly RedirectTransform _transform = new();

    private string Apply(string input)
    {
        var ctx = new TranspileContext(input);
        _transform.Apply(ref ctx);
        return ctx.Result;
    }

    [Fact]
    public void StderrToStdout_Passthrough()
    {
        var input = "cmd 2>&1";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void AppendRedirect_Passthrough()
    {
        var input = "cmd >> file.log";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void StderrToFile_Passthrough()
    {
        var input = "cmd 2> error.log";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void NoRedirect_Unchanged()
    {
        var input = "echo hello";
        Assert.Equal(input, Apply(input));
    }
}
