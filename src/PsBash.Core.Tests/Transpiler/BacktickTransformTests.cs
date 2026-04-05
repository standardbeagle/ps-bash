using Xunit;
using PsBash.Core.Transpiler;
using PsBash.Core.Transpiler.Transforms;

namespace PsBash.Core.Tests.Transpiler;

public class BacktickTransformTests
{
    private readonly BacktickTransform _transform = new();

    private string Apply(string input)
    {
        var ctx = new TranspileContext(input);
        _transform.Apply(ref ctx);
        return ctx.Result;
    }

    [Fact]
    public void SimpleCommand_Transforms()
    {
        Assert.Equal("$(date)", Apply("`date`"));
    }

    [Fact]
    public void CommandWithArgs_Transforms()
    {
        Assert.Equal("$(date +%Y)", Apply("`date +%Y`"));
    }

    [Fact]
    public void InsideDoubleQuotedString_Transforms()
    {
        Assert.Equal("echo \"Today is $(date)\"", Apply("echo \"Today is `date`\""));
    }

    [Fact]
    public void MultipleBackticks_Transforms()
    {
        Assert.Equal("$(date) and $(whoami)", Apply("`date` and `whoami`"));
    }

    [Fact]
    public void NoBackticks_Unchanged()
    {
        var input = "echo hello";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void DollarParenAlreadyUsed_Unchanged()
    {
        var input = "echo $(date)";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void EscapedBacktick_Unchanged()
    {
        Assert.Equal("echo \\`not a substitution\\`", Apply("echo \\`not a substitution\\`"));
    }

    [Fact]
    public void CommandWithPipe_Transforms()
    {
        Assert.Equal("$(ls | wc -l)", Apply("`ls | wc -l`"));
    }

    [Fact]
    public void AssignmentWithBacktick_Transforms()
    {
        Assert.Equal("VAR=$(date +%Y)", Apply("VAR=`date +%Y`"));
    }
}
