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

    [Fact]
    public void Parse_BracedVarSimple_ReturnsBracedVarSub()
    {
        var result = Parse("echo ${PATH}");

        var simple = Assert.IsType<Command.Simple>(result);
        var bvs = Assert.IsType<WordPart.BracedVarSub>(Assert.Single(simple.Words[1].Parts));
        Assert.Equal("PATH", bvs.Name);
        Assert.Null(bvs.Suffix);
    }

    [Fact]
    public void Parse_BracedVarWithDefault_ReturnsBracedVarSubWithSuffix()
    {
        var result = Parse("echo ${VAR:-fallback}");

        var simple = Assert.IsType<Command.Simple>(result);
        var bvs = Assert.IsType<WordPart.BracedVarSub>(Assert.Single(simple.Words[1].Parts));
        Assert.Equal("VAR", bvs.Name);
        Assert.Equal(":-fallback", bvs.Suffix);
    }

    [Fact]
    public void Parse_BracedVarLength_ReturnsBracedVarSubWithHashSuffix()
    {
        var result = Parse("echo ${#VAR}");

        var simple = Assert.IsType<Command.Simple>(result);
        var bvs = Assert.IsType<WordPart.BracedVarSub>(Assert.Single(simple.Words[1].Parts));
        Assert.Equal("VAR", bvs.Name);
        Assert.Equal("#", bvs.Suffix);
    }

    [Fact]
    public void Parse_BracedVarAssignDefault_ReturnsSuffix()
    {
        var result = Parse("echo ${VAR:=default}");

        var simple = Assert.IsType<Command.Simple>(result);
        var bvs = Assert.IsType<WordPart.BracedVarSub>(Assert.Single(simple.Words[1].Parts));
        Assert.Equal("VAR", bvs.Name);
        Assert.Equal(":=default", bvs.Suffix);
    }

    [Fact]
    public void Parse_BracedVarSuffixRemoval_ReturnsSuffix()
    {
        var result = Parse("echo ${VAR%%pattern}");

        var simple = Assert.IsType<Command.Simple>(result);
        var bvs = Assert.IsType<WordPart.BracedVarSub>(Assert.Single(simple.Words[1].Parts));
        Assert.Equal("VAR", bvs.Name);
        Assert.Equal("%%pattern", bvs.Suffix);
    }

    [Fact]
    public void Parse_BracedVarPrefixRemoval_ReturnsSuffix()
    {
        var result = Parse("echo ${VAR##pattern}");

        var simple = Assert.IsType<Command.Simple>(result);
        var bvs = Assert.IsType<WordPart.BracedVarSub>(Assert.Single(simple.Words[1].Parts));
        Assert.Equal("VAR", bvs.Name);
        Assert.Equal("##pattern", bvs.Suffix);
    }

    [Fact]
    public void Parse_BracedVarInsideDoubleQuotes_ReturnsBracedVarSub()
    {
        var result = Parse("echo \"${USER}\"");

        var simple = Assert.IsType<Command.Simple>(result);
        var dq = Assert.IsType<WordPart.DoubleQuoted>(Assert.Single(simple.Words[1].Parts));
        var bvs = Assert.IsType<WordPart.BracedVarSub>(Assert.Single(dq.Parts));
        Assert.Equal("USER", bvs.Name);
        Assert.Null(bvs.Suffix);
    }

    [Fact]
    public void Parse_SpecialVarQuestionMark_ReturnsSimpleVarSub()
    {
        var result = Parse("echo $?");

        var simple = Assert.IsType<Command.Simple>(result);
        var vs = Assert.IsType<WordPart.SimpleVarSub>(Assert.Single(simple.Words[1].Parts));
        Assert.Equal("?", vs.Name);
    }

    [Fact]
    public void Parse_SpecialVarAt_ReturnsSimpleVarSub()
    {
        var result = Parse("echo $@");

        var simple = Assert.IsType<Command.Simple>(result);
        var vs = Assert.IsType<WordPart.SimpleVarSub>(Assert.Single(simple.Words[1].Parts));
        Assert.Equal("@", vs.Name);
    }

    [Fact]
    public void Parse_SpecialVarDollarDollar_ReturnsSimpleVarSub()
    {
        var result = Parse("echo $$");

        var simple = Assert.IsType<Command.Simple>(result);
        var vs = Assert.IsType<WordPart.SimpleVarSub>(Assert.Single(simple.Words[1].Parts));
        Assert.Equal("$", vs.Name);
    }

    [Fact]
    public void Parse_PositionalVar1_ReturnsSimpleVarSub()
    {
        var result = Parse("echo $1");

        var simple = Assert.IsType<Command.Simple>(result);
        var vs = Assert.IsType<WordPart.SimpleVarSub>(Assert.Single(simple.Words[1].Parts));
        Assert.Equal("1", vs.Name);
    }

    [Fact]
    public void Parse_BracedVarWithAlternative_ReturnsSuffix()
    {
        var result = Parse("echo ${VAR:+yes}");

        var simple = Assert.IsType<Command.Simple>(result);
        var bvs = Assert.IsType<WordPart.BracedVarSub>(Assert.Single(simple.Words[1].Parts));
        Assert.Equal("VAR", bvs.Name);
        Assert.Equal(":+yes", bvs.Suffix);
    }

    [Fact]
    public void Parse_BracedVarWithError_ReturnsSuffix()
    {
        var result = Parse("echo ${VAR:?error msg}");

        var simple = Assert.IsType<Command.Simple>(result);
        var bvs = Assert.IsType<WordPart.BracedVarSub>(Assert.Single(simple.Words[1].Parts));
        Assert.Equal("VAR", bvs.Name);
        Assert.Equal(":?error msg", bvs.Suffix);
    }

    [Fact]
    public void Parse_CommandSub_SimpleCommand_ReturnsCommandSubPart()
    {
        var result = Parse("echo $(whoami)");

        var simple = Assert.IsType<Command.Simple>(result);
        Assert.Equal(2, simple.Words.Length);
        var cs = Assert.IsType<WordPart.CommandSub>(Assert.Single(simple.Words[1].Parts));
        var inner = Assert.IsType<Command.Simple>(cs.Body);
        Assert.Equal("whoami", Assert.IsType<WordPart.Literal>(Assert.Single(inner.Words[0].Parts)).Value);
    }

    [Fact]
    public void Parse_CommandSub_InnerPipeline_ParsesRecursively()
    {
        var result = Parse("echo $(ls | grep foo)");

        var simple = Assert.IsType<Command.Simple>(result);
        var cs = Assert.IsType<WordPart.CommandSub>(Assert.Single(simple.Words[1].Parts));
        var pipeline = Assert.IsType<Command.Pipeline>(cs.Body);
        Assert.Equal(2, pipeline.Commands.Length);
    }

    [Fact]
    public void Parse_BacktickCommandSub_ReturnsCommandSubPart()
    {
        var result = Parse("echo `date`");

        var simple = Assert.IsType<Command.Simple>(result);
        Assert.Equal(2, simple.Words.Length);
        var cs = Assert.IsType<WordPart.CommandSub>(Assert.Single(simple.Words[1].Parts));
        var inner = Assert.IsType<Command.Simple>(cs.Body);
        Assert.Equal("date", Assert.IsType<WordPart.Literal>(Assert.Single(inner.Words[0].Parts)).Value);
    }

    [Fact]
    public void Parse_AssignmentWithCommandSub_ParsesCorrectly()
    {
        var result = Parse("VAR=$(cat file)");

        var assign = Assert.IsType<Command.ShAssignment>(result);
        var pair = Assert.Single(assign.Pairs);
        Assert.Equal("VAR", pair.Name);
        var cs = Assert.IsType<WordPart.CommandSub>(Assert.Single(pair.Value!.Parts));
        var inner = Assert.IsType<Command.Simple>(cs.Body);
        Assert.Equal("cat", Assert.IsType<WordPart.Literal>(Assert.Single(inner.Words[0].Parts)).Value);
        Assert.Equal("file", Assert.IsType<WordPart.Literal>(Assert.Single(inner.Words[1].Parts)).Value);
    }

    [Fact]
    public void Parse_NestedCommandSub_ParsesRecursively()
    {
        var result = Parse("echo $(echo $(whoami))");

        var simple = Assert.IsType<Command.Simple>(result);
        var cs = Assert.IsType<WordPart.CommandSub>(Assert.Single(simple.Words[1].Parts));
        var outerInner = Assert.IsType<Command.Simple>(cs.Body);
        Assert.Equal("echo", Assert.IsType<WordPart.Literal>(Assert.Single(outerInner.Words[0].Parts)).Value);
        var nestedCs = Assert.IsType<WordPart.CommandSub>(Assert.Single(outerInner.Words[1].Parts));
        var innermost = Assert.IsType<Command.Simple>(nestedCs.Body);
        Assert.Equal("whoami", Assert.IsType<WordPart.Literal>(Assert.Single(innermost.Words[0].Parts)).Value);
    }

    [Fact]
    public void Parse_TildePath_ReturnsTildeSubAndLiteral()
    {
        var result = Parse("ls ~/docs");

        var simple = Assert.IsType<Command.Simple>(result);
        Assert.Equal(2, simple.Words.Length);
        var parts = simple.Words[1].Parts;
        Assert.Equal(2, parts.Length);
        var tilde = Assert.IsType<WordPart.TildeSub>(parts[0]);
        Assert.Null(tilde.User);
        Assert.Equal("docs", Assert.IsType<WordPart.Literal>(parts[1]).Value);
    }

    [Fact]
    public void Parse_BareTilde_ReturnsTildeSubOnly()
    {
        var result = Parse("cd ~");

        var simple = Assert.IsType<Command.Simple>(result);
        Assert.Equal(2, simple.Words.Length);
        var tilde = Assert.IsType<WordPart.TildeSub>(Assert.Single(simple.Words[1].Parts));
        Assert.Null(tilde.User);
    }

    [Fact]
    public void Parse_TildeUser_ReturnsTildeSubWithUser()
    {
        var result = Parse("ls ~bob/docs");

        var simple = Assert.IsType<Command.Simple>(result);
        var parts = simple.Words[1].Parts;
        Assert.Equal(2, parts.Length);
        var tilde = Assert.IsType<WordPart.TildeSub>(parts[0]);
        Assert.Equal("bob", tilde.User);
        Assert.Equal("docs", Assert.IsType<WordPart.Literal>(parts[1]).Value);
    }

    [Fact]
    public void Parse_Semicolon_TwoCommands_ReturnsCommandList()
    {
        var result = Parse("echo a; echo b");

        var list = Assert.IsType<Command.CommandList>(result);
        Assert.Equal(2, list.Commands.Length);
        var first = Assert.IsType<Command.Simple>(list.Commands[0]);
        Assert.Equal(["echo", "a"], GetWordValues(first));
        var second = Assert.IsType<Command.Simple>(list.Commands[1]);
        Assert.Equal(["echo", "b"], GetWordValues(second));
    }

    [Fact]
    public void Parse_Semicolon_ThreeCommands_ReturnsCommandList()
    {
        var result = Parse("echo a; echo b; echo c");

        var list = Assert.IsType<Command.CommandList>(result);
        Assert.Equal(3, list.Commands.Length);
    }

    [Fact]
    public void Parse_TrailingSemicolon_ReturnsSingleCommand()
    {
        var result = Parse("echo a;");

        var simple = Assert.IsType<Command.Simple>(result);
        Assert.Equal(["echo", "a"], GetWordValues(simple));
    }

    [Fact]
    public void Parse_IfThenFi_ReturnsIfNodeWithOneArm()
    {
        var result = Parse("if cmd; then echo yes; fi");

        var ifCmd = Assert.IsType<Command.If>(result);
        Assert.Single(ifCmd.Arms);
        Assert.Null(ifCmd.ElseBody);

        var cond = Assert.IsType<Command.Simple>(ifCmd.Arms[0].Cond);
        Assert.Equal(["cmd"], GetWordValues(cond));

        var body = Assert.IsType<Command.Simple>(ifCmd.Arms[0].Body);
        Assert.Equal(["echo", "yes"], GetWordValues(body));
    }

    [Fact]
    public void Parse_IfThenElseFi_ReturnsIfNodeWithElse()
    {
        var result = Parse("if cmd; then a; else b; fi");

        var ifCmd = Assert.IsType<Command.If>(result);
        Assert.Single(ifCmd.Arms);
        Assert.NotNull(ifCmd.ElseBody);

        var cond = Assert.IsType<Command.Simple>(ifCmd.Arms[0].Cond);
        Assert.Equal(["cmd"], GetWordValues(cond));

        var body = Assert.IsType<Command.Simple>(ifCmd.Arms[0].Body);
        Assert.Equal(["a"], GetWordValues(body));

        var elseBody = Assert.IsType<Command.Simple>(ifCmd.ElseBody);
        Assert.Equal(["b"], GetWordValues(elseBody));
    }

    [Fact]
    public void Parse_IfElifElseFi_ReturnsIfNodeWithMultipleArms()
    {
        var result = Parse("if cmd1; then a; elif cmd2; then b; else c; fi");

        var ifCmd = Assert.IsType<Command.If>(result);
        Assert.Equal(2, ifCmd.Arms.Length);
        Assert.NotNull(ifCmd.ElseBody);

        var cond1 = Assert.IsType<Command.Simple>(ifCmd.Arms[0].Cond);
        Assert.Equal(["cmd1"], GetWordValues(cond1));
        var body1 = Assert.IsType<Command.Simple>(ifCmd.Arms[0].Body);
        Assert.Equal(["a"], GetWordValues(body1));

        var cond2 = Assert.IsType<Command.Simple>(ifCmd.Arms[1].Cond);
        Assert.Equal(["cmd2"], GetWordValues(cond2));
        var body2 = Assert.IsType<Command.Simple>(ifCmd.Arms[1].Body);
        Assert.Equal(["b"], GetWordValues(body2));

        var elseBody = Assert.IsType<Command.Simple>(ifCmd.ElseBody);
        Assert.Equal(["c"], GetWordValues(elseBody));
    }

    [Fact]
    public void Parse_IfTestConstruct_ParsesTestAsBoolExpr()
    {
        var result = Parse("if [ -f file ]; then echo yes; fi");

        var ifCmd = Assert.IsType<Command.If>(result);
        Assert.Single(ifCmd.Arms);

        var cond = Assert.IsType<Command.BoolExpr>(ifCmd.Arms[0].Cond);
        Assert.False(cond.Extended);
        Assert.Equal(2, cond.Inner.Length);
        Assert.Equal("-f", Assert.IsType<WordPart.Literal>(Assert.Single(cond.Inner[0].Parts)).Value);
        Assert.Equal("file", Assert.IsType<WordPart.Literal>(Assert.Single(cond.Inner[1].Parts)).Value);
    }

    [Fact]
    public void Parse_NestedIf_ParsesCorrectly()
    {
        var result = Parse("if cmd1; then if cmd2; then inner; fi; fi");

        var outer = Assert.IsType<Command.If>(result);
        Assert.Single(outer.Arms);
        Assert.Null(outer.ElseBody);

        var inner = Assert.IsType<Command.If>(outer.Arms[0].Body);
        Assert.Single(inner.Arms);
        var innerCond = Assert.IsType<Command.Simple>(inner.Arms[0].Cond);
        Assert.Equal(["cmd2"], GetWordValues(innerCond));
        var innerBody = Assert.IsType<Command.Simple>(inner.Arms[0].Body);
        Assert.Equal(["inner"], GetWordValues(innerBody));
    }

    [Fact]
    public void Parse_IfWithMultipleBodyCommands_ReturnsCommandList()
    {
        var result = Parse("if cmd; then a; b; fi");

        var ifCmd = Assert.IsType<Command.If>(result);
        var body = Assert.IsType<Command.CommandList>(ifCmd.Arms[0].Body);
        Assert.Equal(2, body.Commands.Length);
    }

    [Fact]
    public void Parse_SingleBracketTest_ReturnsBoolExpr()
    {
        var result = Parse("[ -f file ]");

        var boolExpr = Assert.IsType<Command.BoolExpr>(result);
        Assert.False(boolExpr.Extended);
        Assert.Equal(2, boolExpr.Inner.Length);
        Assert.Equal("-f", Assert.IsType<WordPart.Literal>(Assert.Single(boolExpr.Inner[0].Parts)).Value);
        Assert.Equal("file", Assert.IsType<WordPart.Literal>(Assert.Single(boolExpr.Inner[1].Parts)).Value);
    }

    [Fact]
    public void Parse_DoubleBracketTest_ReturnsBoolExprExtended()
    {
        var result = Parse("[[ -f file ]]");

        var boolExpr = Assert.IsType<Command.BoolExpr>(result);
        Assert.True(boolExpr.Extended);
        Assert.Equal(2, boolExpr.Inner.Length);
    }

    [Fact]
    public void Parse_DoubleBracketWithLogicalAnd_CapturesAndOp()
    {
        var result = Parse("[[ -f file && -d dir ]]");

        var boolExpr = Assert.IsType<Command.BoolExpr>(result);
        Assert.True(boolExpr.Extended);
        Assert.Equal(5, boolExpr.Inner.Length);
        Assert.Equal("&&", Assert.IsType<WordPart.Literal>(Assert.Single(boolExpr.Inner[2].Parts)).Value);
    }

    [Fact]
    public void Parse_DoubleBracketComparison_CapturesThreeWords()
    {
        var result = Parse("[[ $a == \"foo\" ]]");

        var boolExpr = Assert.IsType<Command.BoolExpr>(result);
        Assert.True(boolExpr.Extended);
        Assert.Equal(3, boolExpr.Inner.Length);
    }

    [Fact]
    public void Parse_SingleBracketInAndOr_ReturnsBoolExprInAndOrList()
    {
        var result = Parse("[ -f file ] && echo yes");

        var andOr = Assert.IsType<Command.AndOrList>(result);
        Assert.IsType<Command.BoolExpr>(andOr.Commands[0]);
        Assert.IsType<Command.Simple>(andOr.Commands[1]);
    }

    [Fact]
    public void Parse_ForInWords_ReturnsForInNode()
    {
        var result = Parse("for x in a b c; do echo $x; done");

        var forIn = Assert.IsType<Command.ForIn>(result);
        Assert.Equal("x", forIn.Var);
        Assert.Equal(3, forIn.List.Length);
        Assert.IsType<Command.Simple>(forIn.Body);
    }

    [Fact]
    public void Parse_ForImplicitArgs_ReturnsForInWithEmptyList()
    {
        var result = Parse("for x; do echo $x; done");

        var forIn = Assert.IsType<Command.ForIn>(result);
        Assert.Equal("x", forIn.Var);
        Assert.True(forIn.List.IsEmpty);
    }

    [Fact]
    public void Parse_ForArith_ReturnsForArithNode()
    {
        var result = Parse("for ((i=0; i<10; i++)); do echo $i; done");

        var forArith = Assert.IsType<Command.ForArith>(result);
        Assert.Equal("i=0", forArith.Init);
        Assert.Equal("i<10", forArith.Cond);
        Assert.Equal("i++", forArith.Step);
        Assert.IsType<Command.Simple>(forArith.Body);
    }

    [Fact]
    public void Parse_ForInWithNewlines_ReturnsForInNode()
    {
        var result = Parse("for x in a b c\ndo\necho $x\ndone");

        var forIn = Assert.IsType<Command.ForIn>(result);
        Assert.Equal("x", forIn.Var);
        Assert.Equal(3, forIn.List.Length);
    }

    [Fact]
    public void Parse_WhileTrue_ReturnsWhileNode()
    {
        var result = Parse("while true; do echo hi; done");

        var whileCmd = Assert.IsType<Command.While>(result);
        Assert.False(whileCmd.IsUntil);
        var cond = Assert.IsType<Command.Simple>(whileCmd.Cond);
        Assert.Equal("true", GetWordValues(cond)[0]);
        Assert.IsType<Command.Simple>(whileCmd.Body);
    }

    [Fact]
    public void Parse_WhileReadLine_ReturnsWhileNode()
    {
        var result = Parse("while read line; do echo $line; done");

        var whileCmd = Assert.IsType<Command.While>(result);
        Assert.False(whileCmd.IsUntil);
        var cond = Assert.IsType<Command.Simple>(whileCmd.Cond);
        Assert.Equal(new[] { "read", "line" }, GetWordValues(cond));
    }

    [Fact]
    public void Parse_Until_ReturnsWhileNodeWithIsUntilTrue()
    {
        var result = Parse("until cmd; do body; done");

        var whileCmd = Assert.IsType<Command.While>(result);
        Assert.True(whileCmd.IsUntil);
        var cond = Assert.IsType<Command.Simple>(whileCmd.Cond);
        Assert.Equal("cmd", GetWordValues(cond)[0]);
    }

    [Fact]
    public void Parse_WhileWithTestExpr_ReturnsWhileWithBoolExprCond()
    {
        var result = Parse("while [ -f file ]; do echo yes; done");

        var whileCmd = Assert.IsType<Command.While>(result);
        Assert.False(whileCmd.IsUntil);
        Assert.IsType<Command.BoolExpr>(whileCmd.Cond);
    }

    [Fact]
    public void Parse_WhileWithNewlines_ReturnsWhileNode()
    {
        var result = Parse("while true\ndo\necho hi\ndone");

        var whileCmd = Assert.IsType<Command.While>(result);
        Assert.False(whileCmd.IsUntil);
    }

    [Fact]
    public void Parse_FunctionKeywordForm_ReturnsShFunction()
    {
        var result = Parse("function greet { echo hello }");

        var func = Assert.IsType<Command.ShFunction>(result);
        Assert.Equal("greet", func.Name);
        var body = Assert.IsType<Command.Simple>(func.Body);
        Assert.Equal(new[] { "echo", "hello" }, GetWordValues(body));
    }

    [Fact]
    public void Parse_FunctionParensForm_ReturnsShFunction()
    {
        var result = Parse("greet() { echo hello }");

        var func = Assert.IsType<Command.ShFunction>(result);
        Assert.Equal("greet", func.Name);
        var body = Assert.IsType<Command.Simple>(func.Body);
        Assert.Equal(new[] { "echo", "hello" }, GetWordValues(body));
    }

    [Fact]
    public void Parse_FunctionParensWithSpace_ReturnsShFunction()
    {
        var result = Parse("greet () { echo hello }");

        var func = Assert.IsType<Command.ShFunction>(result);
        Assert.Equal("greet", func.Name);
        var body = Assert.IsType<Command.Simple>(func.Body);
        Assert.Equal(new[] { "echo", "hello" }, GetWordValues(body));
    }

    [Fact]
    public void Parse_FunctionWithLocalVars_ReturnsShFunctionWithLocalAssignment()
    {
        var result = Parse("function add { local result=42; echo $result }");

        var func = Assert.IsType<Command.ShFunction>(result);
        Assert.Equal("add", func.Name);
        var list = Assert.IsType<Command.CommandList>(func.Body);
        Assert.Equal(2, list.Commands.Length);
        var assign = Assert.IsType<Command.ShAssignment>(list.Commands[0]);
        Assert.True(assign.IsLocal);
        Assert.Equal("result", assign.Pairs[0].Name);
    }

    [Fact]
    public void Parse_FunctionWithMultipleCommands_ReturnsShFunctionWithCommandList()
    {
        var result = Parse("function setup {\n  echo start\n  echo end\n}");

        var func = Assert.IsType<Command.ShFunction>(result);
        Assert.Equal("setup", func.Name);
        var list = Assert.IsType<Command.CommandList>(func.Body);
        Assert.Equal(2, list.Commands.Length);
    }

    [Fact]
    public void Parse_LocalAssignment_ReturnsShAssignmentWithIsLocal()
    {
        var result = Parse("local x=10");

        var assign = Assert.IsType<Command.ShAssignment>(result);
        Assert.True(assign.IsLocal);
        Assert.Equal("x", assign.Pairs[0].Name);
    }

    [Fact]
    public void Parse_SimpleSubshell_ReturnsSubshellNode()
    {
        var result = Parse("(echo hello; echo world)");

        var subshell = Assert.IsType<Command.Subshell>(result);
        var list = Assert.IsType<Command.CommandList>(subshell.Body);
        Assert.Equal(2, list.Commands.Length);
        Assert.Empty(subshell.Redirects);
    }

    [Fact]
    public void Parse_BraceGroup_ReturnsBraceGroupNode()
    {
        var result = Parse("{ echo hello; echo world; }");

        var braceGroup = Assert.IsType<Command.BraceGroup>(result);
        var list = Assert.IsType<Command.CommandList>(braceGroup.Body);
        Assert.Equal(2, list.Commands.Length);
    }

    [Fact]
    public void Parse_SubshellWithRedirect_CapturesRedirects()
    {
        var result = Parse("(echo hello) > out.txt");

        var subshell = Assert.IsType<Command.Subshell>(result);
        Assert.IsType<Command.Simple>(subshell.Body);
        Assert.Single(subshell.Redirects);
        Assert.Equal(">", subshell.Redirects[0].Op);
    }

    [Fact]
    public void Parse_NestedSubshells_ReturnsNestedSubshellNodes()
    {
        var result = Parse("(echo a; (echo b))");

        var outer = Assert.IsType<Command.Subshell>(result);
        var list = Assert.IsType<Command.CommandList>(outer.Body);
        Assert.Equal(2, list.Commands.Length);
        var inner = Assert.IsType<Command.Subshell>(list.Commands[1]);
        Assert.IsType<Command.Simple>(inner.Body);
    }

    [Fact]
    public void Parse_SingleCommandSubshell_DoesNotWrapInList()
    {
        var result = Parse("(echo hello)");

        var subshell = Assert.IsType<Command.Subshell>(result);
        Assert.IsType<Command.Simple>(subshell.Body);
    }

    [Fact]
    public void Parse_SingleCommandBraceGroup_DoesNotWrapInList()
    {
        var result = Parse("{ echo hello; }");

        var braceGroup = Assert.IsType<Command.BraceGroup>(result);
        Assert.IsType<Command.Simple>(braceGroup.Body);
    }
}
