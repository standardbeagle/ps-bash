using Xunit;
using PsBash.Core.Transpiler;
using PsBash.Core.Transpiler.Transforms;

namespace PsBash.Core.Tests.Transpiler;

public class StderrRedirectTransformTests
{
    private readonly StderrRedirectTransform _transform = new();

    private string Apply(string input)
    {
        var ctx = new TranspileContext(input);
        _transform.Apply(ref ctx);
        return ctx.Result;
    }

    [Fact]
    public void AmpersandGt_ToStarGt()
    {
        Assert.Equal("cmd *> file.log", Apply("cmd &> file.log"));
    }

    [Fact]
    public void GtAmpersand_ToStarGt()
    {
        Assert.Equal("cmd *> file.log", Apply("cmd >& file.log"));
    }

    [Fact]
    public void AmpersandGtGt_ToStarGtGt()
    {
        Assert.Equal("cmd *>> file.log", Apply("cmd &>> file.log"));
    }

    [Fact]
    public void StderrToStdout_Passthrough()
    {
        var input = "cmd 2>&1";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void StderrToStdout_WithPipe_Passthrough()
    {
        var input = "cmd 2>&1 | grep err";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void NoRedirect_Unchanged()
    {
        var input = "echo hello";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void StdoutRedirect_Unchanged()
    {
        var input = "cmd > file.log";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void AppendRedirect_Unchanged()
    {
        var input = "cmd >> file.log";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void FdMerge_1To2_Passthrough()
    {
        var input = "cmd 1>&2";
        Assert.Equal(input, Apply(input));
    }
}
