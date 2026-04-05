using Xunit;
using PsBash.Core.Transpiler;
using PsBash.Core.Transpiler.Transforms;

namespace PsBash.Core.Tests.Transpiler;

public class HereStringTransformTests
{
    private readonly HereStringTransform _transform = new();

    private string Apply(string input)
    {
        var ctx = new TranspileContext(input);
        _transform.Apply(ref ctx);
        return ctx.Result;
    }

    [Fact]
    public void SimpleCommand_DoubleQuoted()
    {
        Assert.Equal("\"hello world\" | cat", Apply("cat <<< \"hello world\""));
    }

    [Fact]
    public void CommandWithArgs_DoubleQuoted()
    {
        Assert.Equal("\"text\" | grep foo", Apply("grep foo <<< \"text\""));
    }

    [Fact]
    public void SingleQuotedValue()
    {
        Assert.Equal("'single quoted' | cmd", Apply("cmd <<< 'single quoted'"));
    }

    [Fact]
    public void UnquotedValue()
    {
        Assert.Equal("hello | cat", Apply("cat <<< hello"));
    }

    [Fact]
    public void CommandWithMultipleArgs()
    {
        Assert.Equal("\"text\" | grep -i foo", Apply("grep -i foo <<< \"text\""));
    }

    [Fact]
    public void VariableValue()
    {
        Assert.Equal("\"$var\" | grep foo", Apply("grep foo <<< \"$var\""));
    }

    [Fact]
    public void MultilineContent()
    {
        Assert.Equal("\"line1\\nline2\" | wc -l", Apply("wc -l <<< \"line1\\nline2\""));
    }

    [Fact]
    public void NoHereString_Unchanged()
    {
        var input = "echo hello";
        Assert.Equal(input, Apply(input));
    }
}
