using System.Collections.Immutable;
using Xunit;
using PsBash.Core.Parser;
using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Tests.Parser;

public class BashParserTests
{
    private static Command? Parse(string input) => BashParser.Parse(input);

    /// <summary>
    /// Extract the literal string values from a SimpleCommand's words.
    /// </summary>
    private static string[] GetWordValues(Command.Simple cmd)
    {
        return cmd.Words.Select(w =>
        {
            var literal = Assert.IsType<WordPart.Literal>(Assert.Single(w.Parts));
            return literal.Value;
        }).ToArray();
    }

    [Fact]
    public void Parse_EchoHello_ReturnsSimpleCommandWithTwoWords()
    {
        var result = Parse("echo hello");

        var simple = Assert.IsType<Command.Simple>(result);
        var words = GetWordValues(simple);
        Assert.Equal(["echo", "hello"], words);
    }

    [Fact]
    public void Parse_LsLaTmp_ReturnsSimpleCommandWithThreeWords()
    {
        var result = Parse("ls -la /tmp");

        var simple = Assert.IsType<Command.Simple>(result);
        var words = GetWordValues(simple);
        Assert.Equal(["ls", "-la", "/tmp"], words);
    }

    [Fact]
    public void Parse_GitCommitWithQuotedMessage_ReturnsSimpleCommandWithFourWords()
    {
        var result = Parse("git commit -m \"message\"");

        var simple = Assert.IsType<Command.Simple>(result);
        Assert.Equal(4, simple.Words.Length);

        // First three words are bare literals.
        Assert.Equal("git", Assert.IsType<WordPart.Literal>(Assert.Single(simple.Words[0].Parts)).Value);
        Assert.Equal("commit", Assert.IsType<WordPart.Literal>(Assert.Single(simple.Words[1].Parts)).Value);
        Assert.Equal("-m", Assert.IsType<WordPart.Literal>(Assert.Single(simple.Words[2].Parts)).Value);

        // Fourth word is a double-quoted string with literal content.
        var dq = Assert.IsType<WordPart.DoubleQuoted>(Assert.Single(simple.Words[3].Parts));
        var inner = Assert.IsType<WordPart.Literal>(Assert.Single(dq.Parts));
        Assert.Equal("message", inner.Value);
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsNull()
    {
        var result = Parse("");

        Assert.Null(result);
    }

    [Fact]
    public void Parse_WhitespaceOnly_ReturnsNull()
    {
        var result = Parse("   \t  ");

        Assert.Null(result);
    }

    [Fact]
    public void Parse_NewlinesOnly_ReturnsNull()
    {
        var result = Parse("\n\n\n");

        Assert.Null(result);
    }

    [Fact]
    public void Parse_SimpleCommand_HasEmptyEnvPairsAndRedirects()
    {
        var result = Parse("echo hello");

        var simple = Assert.IsType<Command.Simple>(result);
        Assert.True(simple.EnvPairs.IsEmpty);
        Assert.True(simple.Redirects.IsEmpty);
    }

    [Fact]
    public void Parse_SingleWord_ReturnsSimpleCommandWithOneWord()
    {
        var result = Parse("ls");

        var simple = Assert.IsType<Command.Simple>(result);
        var words = GetWordValues(simple);
        Assert.Equal(["ls"], words);
    }

    [Fact]
    public void Parse_LeadingNewlines_SkipsToCommand()
    {
        var result = Parse("\n\necho hello");

        var simple = Assert.IsType<Command.Simple>(result);
        var words = GetWordValues(simple);
        Assert.Equal(["echo", "hello"], words);
    }

    [Fact]
    public void Parse_SingleQuoted_ProducesSingleQuotedPart()
    {
        var result = Parse("echo 'hello world'");

        var simple = Assert.IsType<Command.Simple>(result);
        Assert.Equal(2, simple.Words.Length);

        var sq = Assert.IsType<WordPart.SingleQuoted>(Assert.Single(simple.Words[1].Parts));
        Assert.Equal("hello world", sq.Value);
    }

    [Fact]
    public void Parse_DoubleQuotedWithVar_ProducesDoubleQuotedWithParts()
    {
        var result = Parse("echo \"hello $USER\"");

        var simple = Assert.IsType<Command.Simple>(result);
        Assert.Equal(2, simple.Words.Length);

        var dq = Assert.IsType<WordPart.DoubleQuoted>(Assert.Single(simple.Words[1].Parts));
        Assert.Equal(2, dq.Parts.Length);
        Assert.Equal("hello ", Assert.IsType<WordPart.Literal>(dq.Parts[0]).Value);
        Assert.Equal("USER", Assert.IsType<WordPart.SimpleVarSub>(dq.Parts[1]).Name);
    }

    [Fact]
    public void Parse_BackslashEscape_ProducesEscapedLiteral()
    {
        var result = Parse("echo hello\\ world");

        var simple = Assert.IsType<Command.Simple>(result);
        Assert.Equal(2, simple.Words.Length);

        // "hello\ world" is one WORD token, decomposed into: Literal("hello"), EscapedLiteral(" "), Literal("world")
        Assert.Equal(3, simple.Words[1].Parts.Length);
        Assert.Equal("hello", Assert.IsType<WordPart.Literal>(simple.Words[1].Parts[0]).Value);
        Assert.Equal(" ", Assert.IsType<WordPart.EscapedLiteral>(simple.Words[1].Parts[1]).Value);
        Assert.Equal("world", Assert.IsType<WordPart.Literal>(simple.Words[1].Parts[2]).Value);
    }

    [Fact]
    public void Parse_DoubleQuotedWithApostrophe_PreservesApostrophe()
    {
        var result = Parse("echo \"it's fine\"");

        var simple = Assert.IsType<Command.Simple>(result);
        var dq = Assert.IsType<WordPart.DoubleQuoted>(Assert.Single(simple.Words[1].Parts));
        var lit = Assert.IsType<WordPart.Literal>(Assert.Single(dq.Parts));
        Assert.Equal("it's fine", lit.Value);
    }

    [Fact]
    public void Parse_SingleQuotedWithDoubleQuotes_PreservesDoubleQuotes()
    {
        var result = Parse("echo 'say \"hi\"'");

        var simple = Assert.IsType<Command.Simple>(result);
        var sq = Assert.IsType<WordPart.SingleQuoted>(Assert.Single(simple.Words[1].Parts));
        Assert.Equal("say \"hi\"", sq.Value);
    }

    [Fact]
    public void Parse_BareVarRef_ProducesSimpleVarSub()
    {
        var result = Parse("echo $HOME");

        var simple = Assert.IsType<Command.Simple>(result);
        var vs = Assert.IsType<WordPart.SimpleVarSub>(Assert.Single(simple.Words[1].Parts));
        Assert.Equal("HOME", vs.Name);
    }

    [Fact]
    public void Parse_MixedQuoteWord_ProducesMultipleParts()
    {
        // hello'world'"$USER" -> Literal("hello"), SingleQuoted("world"), DoubleQuoted([SimpleVarSub("USER")])
        var result = Parse("echo hello'world'\"$USER\"");

        var simple = Assert.IsType<Command.Simple>(result);
        var parts = simple.Words[1].Parts;
        Assert.Equal(3, parts.Length);
        Assert.Equal("hello", Assert.IsType<WordPart.Literal>(parts[0]).Value);
        Assert.Equal("world", Assert.IsType<WordPart.SingleQuoted>(parts[1]).Value);
        var dq = Assert.IsType<WordPart.DoubleQuoted>(parts[2]);
        Assert.Equal("USER", Assert.IsType<WordPart.SimpleVarSub>(Assert.Single(dq.Parts)).Name);
    }
}
