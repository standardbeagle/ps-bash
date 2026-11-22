using PsBash.Core.Parser;
using PsBash.Core.Parser.Ast;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// An unquoted-delimiter here-document's backslash-newline is a line continuation, removed as bash reads each line:
/// before the <c>&lt;&lt;-</c> tab strip, the delimiter match and expansion (bash 5.2: <c>a $x\</c> + <c>b</c> prints
/// <c>a </c> - <c>$xb</c> is unset; <c>t1\</c> + TAB <c>t2</c> under <c>&lt;&lt;-</c> is <c>t1&lt;TAB&gt;t2</c>; <c>a\</c> then
/// <c>EOF</c> is the body line <c>aEOF</c>, not the end). Only <c>\$</c>, <c>\`</c>, <c>\\</c> and backslash-newline are
/// special; <c>\\</c> before a newline is a literal backslash and the newline stays. A quoted delimiter keeps them all.
/// REGRESSION: the body kept every <c>\</c>+newline (h01, h02, c15 in the redirect proof).
/// </summary>
public class HereDocContinuationTests
{
    private static HereDoc Doc(string bash)
    {
        var ast = BashParser.Parse(bash)!;
        while (ast is Command.CommandList { Commands.Length: 1 } l) ast = l.Commands[0];
        var simple = Assert.IsType<Command.Simple>(ast);
        return Assert.Single(simple.HereDocs);
    }

    [Theory]
    [InlineData("a\\\nb", "ab")]
    [InlineData("a\\\\\nb", "a\\\\\nb")]       // `\\` is a pair: the newline stays
    [InlineData("a\\\\\\\nb", "a\\\\b")]       // `\\` then `\`+newline
    [InlineData("\\$x \\` \\a", "\\$x \\` \\a")] // left for expansion
    [InlineData("a\\\r\nb", "ab")]
    [InlineData("no backslash", "no backslash")]
    public void JoinHereDocContinuations_RemovesOnlyUnpairedBackslashNewline(string text, string expected)
    {
        Assert.Equal(expected, BashLexer.JoinHereDocContinuations(text));
    }

    [Fact]
    public void Parse_UnquotedHereDoc_BackslashNewlineJoinsTheLines()
    {
        var doc = Doc("cat <<EOF\na $x\\\nb\nc\\$x\\\nd\nEOF\n");

        Assert.Equal("a $xb\nc\\$xd\n", doc.Body);
    }

    [Fact]
    public void Parse_StripTabsHereDoc_JoinsBeforeStrippingTheLinesStart()
    {
        var doc = Doc("cat <<-EOF\n\tt1\\\n\tt2\n\tEOF\n");

        Assert.Equal("t1\tt2\n", doc.Body);
    }

    [Fact]
    public void Parse_ContinuedLine_IsNotTheDelimiter()
    {
        var doc = Doc("cat <<EOF\na\\\nEOF\nEOF\n");

        Assert.Equal("aEOF\n", doc.Body);
    }

    [Theory]
    [InlineData("cat <<'EOF'\nq\\\nr\nEOF\n")]
    [InlineData("cat <<\"EOF\"\nq\\\nr\nEOF\n")]
    [InlineData("cat <<\\EOF\nq\\\nr\nEOF\n")]
    public void Parse_QuotedDelimiter_KeepsBackslashNewline(string bash)
    {
        Assert.Equal("q\\\nr\n", Doc(bash).Body);
    }

    [Fact]
    public void Parse_DoubledBackslashBeforeNewline_KeepsTheLineBreak()
    {
        Assert.Equal("end\\\\\n", Doc("cat <<EOF\nend\\\\\nEOF\n").Body);
    }
}
