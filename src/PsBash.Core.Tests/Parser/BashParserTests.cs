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
    public void Parse_SimplePipeline_ReturnsPipelineNode()
    {
        var result = Parse("ls | grep foo");

        var pipeline = Assert.IsType<Command.Pipeline>(result);
        Assert.Equal(2, pipeline.Commands.Length);
        Assert.Equal(new[] { "|" }, pipeline.Ops.ToArray());
        Assert.False(pipeline.Negated);

        var left = Assert.IsType<Command.Simple>(pipeline.Commands[0]);
        Assert.Equal(["ls"], GetWordValues(left));

        var right = Assert.IsType<Command.Simple>(pipeline.Commands[1]);
        Assert.Equal(["grep", "foo"], GetWordValues(right));
    }

    [Fact]
    public void Parse_ThreeCommandPipeline_ReturnsThreeChildren()
    {
        var result = Parse("cat file | head -n 5 | sort");

        var pipeline = Assert.IsType<Command.Pipeline>(result);
        Assert.Equal(3, pipeline.Commands.Length);
        Assert.Equal(new[] { "|", "|" }, pipeline.Ops.ToArray());
    }

    [Fact]
    public void Parse_PipeAmpersand_RecognizedAsStderrPipe()
    {
        var result = Parse("cmd |& other");

        var pipeline = Assert.IsType<Command.Pipeline>(result);
        Assert.Equal(2, pipeline.Commands.Length);
        Assert.Equal(new[] { "|&" }, pipeline.Ops.ToArray());
    }

    [Fact]
    public void Parse_SingleCommand_NoPipe_ReturnsSimple()
    {
        var result = Parse("echo hello");

        Assert.IsType<Command.Simple>(result);
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

    [Fact]
    public void Parse_OutputRedirect_ParsesRedirectNode()
    {
        var result = Parse("cmd > file");

        var simple = Assert.IsType<Command.Simple>(result);
        Assert.Equal(["cmd"], GetWordValues(simple));
        var redir = Assert.Single(simple.Redirects);
        Assert.Equal(">", redir.Op);
        Assert.Equal(1, redir.Fd);
        Assert.Equal("file", Assert.IsType<WordPart.Literal>(Assert.Single(redir.Target.Parts)).Value);
    }

    [Fact]
    public void Parse_AppendRedirect_ParsesRedirectNode()
    {
        var result = Parse("cmd >> file");

        var simple = Assert.IsType<Command.Simple>(result);
        Assert.Equal(["cmd"], GetWordValues(simple));
        var redir = Assert.Single(simple.Redirects);
        Assert.Equal(">>", redir.Op);
        Assert.Equal(1, redir.Fd);
        Assert.Equal("file", Assert.IsType<WordPart.Literal>(Assert.Single(redir.Target.Parts)).Value);
    }

    [Fact]
    public void Parse_StderrRedirect_ParsesWithFd2()
    {
        var result = Parse("cmd 2> /dev/null");

        var simple = Assert.IsType<Command.Simple>(result);
        Assert.Equal(["cmd"], GetWordValues(simple));
        var redir = Assert.Single(simple.Redirects);
        Assert.Equal(">", redir.Op);
        Assert.Equal(2, redir.Fd);
        Assert.Equal("/dev/null", Assert.IsType<WordPart.Literal>(Assert.Single(redir.Target.Parts)).Value);
    }

    [Fact]
    public void Parse_StderrToStdout_ParsesGreatAnd()
    {
        var result = Parse("cmd 2>&1");

        var simple = Assert.IsType<Command.Simple>(result);
        Assert.Equal(["cmd"], GetWordValues(simple));
        var redir = Assert.Single(simple.Redirects);
        Assert.Equal(">&", redir.Op);
        Assert.Equal(2, redir.Fd);
        Assert.Equal("1", Assert.IsType<WordPart.Literal>(Assert.Single(redir.Target.Parts)).Value);
    }

    [Fact]
    public void Parse_InputRedirect_ParsesWithFd0()
    {
        var result = Parse("cmd < input.txt");

        var simple = Assert.IsType<Command.Simple>(result);
        Assert.Equal(["cmd"], GetWordValues(simple));
        var redir = Assert.Single(simple.Redirects);
        Assert.Equal("<", redir.Op);
        Assert.Equal(0, redir.Fd);
        Assert.Equal("input.txt", Assert.IsType<WordPart.Literal>(Assert.Single(redir.Target.Parts)).Value);
    }

    [Fact]
    public void Parse_MultipleRedirects_ParsesBoth()
    {
        var result = Parse("cmd > /dev/null 2>&1");

        var simple = Assert.IsType<Command.Simple>(result);
        Assert.Equal(["cmd"], GetWordValues(simple));
        Assert.Equal(2, simple.Redirects.Length);

        Assert.Equal(">", simple.Redirects[0].Op);
        Assert.Equal(1, simple.Redirects[0].Fd);
        Assert.Equal("/dev/null", Assert.IsType<WordPart.Literal>(Assert.Single(simple.Redirects[0].Target.Parts)).Value);

        Assert.Equal(">&", simple.Redirects[1].Op);
        Assert.Equal(2, simple.Redirects[1].Fd);
        Assert.Equal("1", Assert.IsType<WordPart.Literal>(Assert.Single(simple.Redirects[1].Target.Parts)).Value);
    }

    [Fact]
    public void Parse_IoNumber3_ParsesWithFd3()
    {
        var result = Parse("cmd 3> file");

        var simple = Assert.IsType<Command.Simple>(result);
        Assert.Equal(["cmd"], GetWordValues(simple));
        var redir = Assert.Single(simple.Redirects);
        Assert.Equal(">", redir.Op);
        Assert.Equal(3, redir.Fd);
        Assert.Equal("file", Assert.IsType<WordPart.Literal>(Assert.Single(redir.Target.Parts)).Value);
    }

    [Fact]
    public void Parse_AndIf_ReturnsAndOrList()
    {
        var result = Parse("mkdir dir && cd dir");

        var andOr = Assert.IsType<Command.AndOrList>(result);
        Assert.Equal(2, andOr.Commands.Length);
        Assert.Equal(["&&"], andOr.Ops.ToArray());

        var left = Assert.IsType<Command.Simple>(andOr.Commands[0]);
        Assert.Equal(["mkdir", "dir"], GetWordValues(left));

        var right = Assert.IsType<Command.Simple>(andOr.Commands[1]);
        Assert.Equal(["cd", "dir"], GetWordValues(right));
    }

    [Fact]
    public void Parse_OrIf_ReturnsAndOrList()
    {
        var result = Parse("test -f file || echo missing");

        var andOr = Assert.IsType<Command.AndOrList>(result);
        Assert.Equal(2, andOr.Commands.Length);
        Assert.Equal(["||"], andOr.Ops.ToArray());

        var left = Assert.IsType<Command.Simple>(andOr.Commands[0]);
        Assert.Equal(["test", "-f", "file"], GetWordValues(left));

        var right = Assert.IsType<Command.Simple>(andOr.Commands[1]);
        Assert.Equal(["echo", "missing"], GetWordValues(right));
    }

    [Fact]
    public void Parse_MixedAndOrOps_ReturnsCorrectPrecedence()
    {
        var result = Parse("cmd1 && cmd2 || cmd3");

        var andOr = Assert.IsType<Command.AndOrList>(result);
        Assert.Equal(3, andOr.Commands.Length);
        Assert.Equal(["&&", "||"], andOr.Ops.ToArray());
    }

    [Fact]
    public void Parse_SingleCommand_NoAndOr_ReturnsSimple()
    {
        var result = Parse("echo hello");

        Assert.IsType<Command.Simple>(result);
    }

    [Fact]
    public void Parse_ExportFooBar_ReturnsShAssignment()
    {
        var result = Parse("export FOO=bar");

        var assign = Assert.IsType<Command.ShAssignment>(result);
        var pair = Assert.Single(assign.Pairs);
        Assert.Equal("FOO", pair.Name);
        Assert.Equal(AssignOp.Equal, pair.Op);
        var lit = Assert.IsType<WordPart.Literal>(Assert.Single(pair.Value!.Parts));
        Assert.Equal("bar", lit.Value);
    }

    [Fact]
    public void Parse_ExportWithQuotedValue_ReturnsShAssignment()
    {
        var result = Parse("export FOO=\"hello world\"");

        var assign = Assert.IsType<Command.ShAssignment>(result);
        var pair = Assert.Single(assign.Pairs);
        Assert.Equal("FOO", pair.Name);
        var dq = Assert.IsType<WordPart.DoubleQuoted>(Assert.Single(pair.Value!.Parts));
        var lit = Assert.IsType<WordPart.Literal>(Assert.Single(dq.Parts));
        Assert.Equal("hello world", lit.Value);
    }

    [Fact]
    public void Parse_BareAssignment_ReturnsShAssignment()
    {
        var result = Parse("FOO=bar");

        var assign = Assert.IsType<Command.ShAssignment>(result);
        var pair = Assert.Single(assign.Pairs);
        Assert.Equal("FOO", pair.Name);
        var lit = Assert.IsType<WordPart.Literal>(Assert.Single(pair.Value!.Parts));
        Assert.Equal("bar", lit.Value);
    }

    [Fact]
    public void Parse_AssignmentWithCommand_ReturnsSimpleWithEnvPairs()
    {
        var result = Parse("FOO=bar baz");

        var simple = Assert.IsType<Command.Simple>(result);
        var envPair = Assert.Single(simple.EnvPairs);
        Assert.Equal("FOO", envPair.Name);
        var lit = Assert.IsType<WordPart.Literal>(Assert.Single(envPair.Value!.Parts));
        Assert.Equal("bar", lit.Value);
        Assert.Equal(["baz"], GetWordValues(simple));
    }

    [Fact]
    public void Parse_MultipleAssignmentsWithCommand_ReturnsSimpleWithEnvPairs()
    {
        var result = Parse("FOO=1 BAR=2 cmd");

        var simple = Assert.IsType<Command.Simple>(result);
        Assert.Equal(2, simple.EnvPairs.Length);
        Assert.Equal("FOO", simple.EnvPairs[0].Name);
        Assert.Equal("BAR", simple.EnvPairs[1].Name);
        Assert.Equal(["cmd"], GetWordValues(simple));
    }

    [Fact]
    public void Parse_ExportPathWithExpansion_ReturnsShAssignment()
    {
        var result = Parse("export PATH=\"$PATH:/new\"");

        var assign = Assert.IsType<Command.ShAssignment>(result);
        var pair = Assert.Single(assign.Pairs);
        Assert.Equal("PATH", pair.Name);
        var dq = Assert.IsType<WordPart.DoubleQuoted>(Assert.Single(pair.Value!.Parts));
        Assert.Equal(2, dq.Parts.Length);
        Assert.Equal("PATH", Assert.IsType<WordPart.SimpleVarSub>(dq.Parts[0]).Name);
        Assert.Equal(":/new", Assert.IsType<WordPart.Literal>(dq.Parts[1]).Value);
    }

    [Fact]
    public void Parse_PipelineWithAndOr_PipelineBindsTighter()
    {
        var result = Parse("ls | grep foo && echo found");

        var andOr = Assert.IsType<Command.AndOrList>(result);
        Assert.Equal(2, andOr.Commands.Length);
        Assert.Equal(["&&"], andOr.Ops.ToArray());

        var left = Assert.IsType<Command.Pipeline>(andOr.Commands[0]);
        Assert.Equal(2, left.Commands.Length);

        var right = Assert.IsType<Command.Simple>(andOr.Commands[1]);
        Assert.Equal(["echo", "found"], GetWordValues(right));
    }
}
