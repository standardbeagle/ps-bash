using Xunit;
using PsBash.Core.Transpiler;
using PsBash.Core.Transpiler.Transforms;

namespace PsBash.Core.Tests.Transpiler;

public class InputRedirectTransformTests
{
    private readonly InputRedirectTransform _transform = new();

    private string Apply(string input)
    {
        var ctx = new TranspileContext(input);
        _transform.Apply(ref ctx);
        return ctx.Result;
    }

    [Fact]
    public void SimpleCommand_WcL()
    {
        Assert.Equal("Get-Content file.txt | wc -l", Apply("wc -l < file.txt"));
    }

    [Fact]
    public void SimpleCommand_Sort()
    {
        Assert.Equal("Get-Content data.csv | sort", Apply("sort < data.csv"));
    }

    [Fact]
    public void CommandWithArgs_Grep()
    {
        Assert.Equal("Get-Content input.txt | grep foo", Apply("grep foo < input.txt"));
    }

    [Fact]
    public void CommandWithArg()
    {
        Assert.Equal("Get-Content file.txt | cmd arg", Apply("cmd arg < file.txt"));
    }

    [Fact]
    public void QuotedFilePath_DoubleQuotes()
    {
        Assert.Equal("Get-Content \"my file.txt\" | cmd", Apply("cmd < \"my file.txt\""));
    }

    [Fact]
    public void QuotedFilePath_SingleQuotes()
    {
        Assert.Equal("Get-Content 'my file.txt' | cmd", Apply("cmd < 'my file.txt'"));
    }

    [Fact]
    public void DoesNotMatch_HereString()
    {
        var input = "cat <<< \"hello\"";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void DoesNotMatch_OutputRedirect()
    {
        var input = "echo hello > file.txt";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void DoesNotMatch_StderrRedirect()
    {
        var input = "cmd 2> error.log";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void DoesNotMatch_Append()
    {
        var input = "echo hello >> file.txt";
        Assert.Equal(input, Apply(input));
    }

    [Fact]
    public void NoRedirect_Unchanged()
    {
        var input = "echo hello";
        Assert.Equal(input, Apply(input));
    }
}
