using Xunit;
using PsBash.Core.Transpiler;

namespace PsBash.Core.Tests.Transpiler;

public class ParlotParserTests
{
    [Fact]
    public void ParseWord_SingleWord_ReturnsWordNode()
    {
        var result = ParlotParser.ParseWord("hello");

        Assert.NotNull(result);
        Assert.Equal("hello", result.Value);
    }

    [Fact]
    public void ParseWord_EmptyInput_ReturnsNull()
    {
        var result = ParlotParser.ParseWord("");

        Assert.Null(result);
    }

    [Fact]
    public void ParseWord_WhitespaceOnly_ReturnsNull()
    {
        var result = ParlotParser.ParseWord("   ");

        Assert.Null(result);
    }

    [Fact]
    public void ParseWord_LeadingWhitespace_ParsesWord()
    {
        var result = ParlotParser.ParseWord("  hello");

        Assert.NotNull(result);
        Assert.Equal("hello", result.Value);
    }
}
