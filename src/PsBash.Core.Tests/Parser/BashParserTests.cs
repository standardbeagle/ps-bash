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

    // --- Error reporting tests ---

    [Fact]
    public void Parse_MissingFi_ThrowsParseExceptionWithLineAndColumn()
    {
        var ex = Assert.Throws<ParseException>(() => Parse("if true; then echo hi"));

        Assert.Equal(1, ex.Line);
        Assert.True(ex.Column > 0);
        Assert.Contains("fi", ex.Message);
        Assert.NotEmpty(ex.Rule);
    }

    [Fact]
    public void Parse_UnsupportedSelect_ThrowsParseExceptionWithSuggestion()
    {
        var ex = Assert.Throws<ParseException>(() => Parse("select x in a b c; do echo $x; done"));

        Assert.Equal(1, ex.Line);
        Assert.Equal(1, ex.Column);
        Assert.Contains("select", ex.Message);
        Assert.Contains("not supported", ex.Message);
        Assert.Equal("ParseCompoundOrSimple", ex.Rule);
    }

    [Fact]
    public void Parse_MultilineError_PointsToCorrectLine()
    {
        // Missing 'fi' on a multiline if/then — error at EOF references the right location.
        var input = "if true; then\n  echo inside";

        var ex = Assert.Throws<ParseException>(() => Parse(input));

        // The error should be at or after line 2 where EOF is hit instead of 'fi'.
        Assert.True(ex.Line >= 2, $"Expected error on line 2 or later, got line {ex.Line}");
        Assert.True(ex.Column > 0);
        Assert.Contains("line", ex.Message);
        Assert.Contains("col", ex.Message);
    }

    [Fact]
    public void Parse_MissingDone_ThrowsParseExceptionNotStackOverflow()
    {
        var ex = Assert.Throws<ParseException>(() => Parse("while true; do echo loop"));

        Assert.Equal(1, ex.Line);
        Assert.Contains("done", ex.Message);
        Assert.NotEmpty(ex.Rule);
    }

    [Fact]
    public void Parse_MissingEsac_ThrowsParseExceptionWithPosition()
    {
        var input = "case $x in\n  a) echo yes;;";

        var ex = Assert.Throws<ParseException>(() => Parse(input));

        Assert.True(ex.Line >= 1);
        Assert.Contains("esac", ex.Message);
    }

    [Fact]
    public void Parse_UnclosedBraceGroup_ThrowsParseExceptionWithPosition()
    {
        var ex = Assert.Throws<ParseException>(() => Parse("{ echo hello"));

        Assert.Equal(1, ex.Line);
        Assert.Contains("}", ex.Message);
    }

    [Fact]
    public void Parse_MultilineIfError_ReportsLine2()
    {
        // The error is on line 2 where 'then' is expected but 'echo' comes instead.
        // "if true\necho" — missing 'then' keyword.
        var input = "if true\necho hi";

        var ex = Assert.Throws<ParseException>(() => Parse(input));

        Assert.Contains("then", ex.Message);
        // The 'echo' token is on line 2
        Assert.Equal(2, ex.Line);
    }

    [Fact]
    public void Parse_GlobStar_ProducesGlobPart()
    {
        var simple = Assert.IsType<Command.Simple>(Parse("echo *.py"));

        Assert.Equal(2, simple.Words.Length);
        var parts = simple.Words[1].Parts;
        Assert.Equal(2, parts.Length);
        Assert.IsType<WordPart.GlobPart>(parts[0]);
        Assert.Equal("*", ((WordPart.GlobPart)parts[0]).Pattern);
        Assert.Equal(".py", ((WordPart.Literal)parts[1]).Value);
    }

    [Fact]
    public void Parse_GlobCharClass_ProducesGlobPart()
    {
        var simple = Assert.IsType<Command.Simple>(Parse("echo [abc]*"));

        var parts = simple.Words[1].Parts;
        Assert.Equal(2, parts.Length);
        Assert.Equal("[abc]", ((WordPart.GlobPart)parts[0]).Pattern);
        Assert.Equal("*", ((WordPart.GlobPart)parts[1]).Pattern);
    }

    [Fact]
    public void Parse_ExtGlob_ProducesGlobPart()
    {
        var simple = Assert.IsType<Command.Simple>(Parse("echo +(*.py|*.js)"));

        var parts = simple.Words[1].Parts;
        Assert.Single(parts);
        Assert.Equal("+(*.py|*.js)", ((WordPart.GlobPart)parts[0]).Pattern);
    }

    [Fact]
    public void Parse_InputProcessSub_ProducesProcessSubPart()
    {
        var simple = Assert.IsType<Command.Simple>(Parse("diff <(ls dir1) <(ls dir2)"));

        Assert.Equal(3, simple.Words.Length);
        Assert.Equal("diff", Assert.IsType<WordPart.Literal>(Assert.Single(simple.Words[0].Parts)).Value);

        var ps1 = Assert.IsType<WordPart.ProcessSub>(Assert.Single(simple.Words[1].Parts));
        Assert.True(ps1.IsInput);
        var innerCmd1 = Assert.IsType<Command.Simple>(ps1.Body);
        Assert.Equal("ls", Assert.IsType<WordPart.Literal>(Assert.Single(innerCmd1.Words[0].Parts)).Value);

        var ps2 = Assert.IsType<WordPart.ProcessSub>(Assert.Single(simple.Words[2].Parts));
        Assert.True(ps2.IsInput);
    }

    [Fact]
    public void Parse_OutputProcessSub_ProducesProcessSubPart()
    {
        var simple = Assert.IsType<Command.Simple>(Parse("cmd >(tee log.txt)"));

        Assert.Equal(2, simple.Words.Length);
        var ps = Assert.IsType<WordPart.ProcessSub>(Assert.Single(simple.Words[1].Parts));
        Assert.False(ps.IsInput);
    }

    [Fact]
    public void Parse_BasicHeredoc_ProducesHereDocNode()
    {
        var simple = Assert.IsType<Command.Simple>(Parse("cat <<EOF\nline 1\nline 2\nEOF"));

        Assert.NotNull(simple.HereDoc);
        Assert.Equal("line 1\nline 2", simple.HereDoc.Body);
        Assert.True(simple.HereDoc.Expand);
        Assert.False(simple.HereDoc.StripTabs);
    }

    [Fact]
    public void Parse_HeredocWithVariableExpansion_SetsExpandTrue()
    {
        var simple = Assert.IsType<Command.Simple>(Parse("cat <<EOF\nhello $NAME\nEOF"));

        Assert.NotNull(simple.HereDoc);
        Assert.Equal("hello $NAME", simple.HereDoc.Body);
        Assert.True(simple.HereDoc.Expand);
    }

    [Fact]
    public void Parse_QuotedDelimiter_SetsExpandFalse()
    {
        var simple = Assert.IsType<Command.Simple>(Parse("cat <<'EOF'\nhello $NAME\nEOF"));

        Assert.NotNull(simple.HereDoc);
        Assert.Contains("$NAME", simple.HereDoc.Body);
        Assert.False(simple.HereDoc.Expand);
    }

    [Fact]
    public void Parse_DLessDash_SetsStripTabsTrue()
    {
        var simple = Assert.IsType<Command.Simple>(Parse("cat <<-EOF\n\tline 1\n\tline 2\nEOF"));

        Assert.NotNull(simple.HereDoc);
        Assert.Equal("line 1\nline 2", simple.HereDoc.Body);
        Assert.True(simple.HereDoc.StripTabs);
    }

    [Fact]
    public void Parse_HeredocAsStdin_CommandWordsPreserved()
    {
        var simple = Assert.IsType<Command.Simple>(Parse("grep -i foo <<EOF\nhello foo\nbar\nEOF"));

        var words = GetWordValues(simple);
        Assert.Equal(["grep", "-i", "foo"], words);
        Assert.NotNull(simple.HereDoc);
        Assert.Equal("hello foo\nbar", simple.HereDoc.Body);
    }

    // ── Case/esac parser tests ──────────────────────────────────────────────

    [Fact]
    public void Parse_SimpleCase_ProducesCaseNode()
    {
        var result = Parse("case $x in\na) echo a;;\nesac");

        var c = Assert.IsType<Command.Case>(result);
        var lit = Assert.IsType<WordPart.SimpleVarSub>(Assert.Single(c.Expr.Parts));
        Assert.Equal("x", lit.Name);
        Assert.Single(c.Arms);
        Assert.Equal("a", Assert.Single(c.Arms[0].Patterns));
    }

    [Fact]
    public void Parse_CaseTwoArms_ProducesTwoArms()
    {
        var result = Parse("case $v in\nfoo) echo foo;;\nbar) echo bar;;\nesac");

        var c = Assert.IsType<Command.Case>(result);
        Assert.Equal(2, c.Arms.Length);
        Assert.Equal("foo", Assert.Single(c.Arms[0].Patterns));
        Assert.Equal("bar", Assert.Single(c.Arms[1].Patterns));
    }

    [Fact]
    public void Parse_CaseMultiPattern_StoresAllPatterns()
    {
        var result = Parse("case $x in\na|b|c) echo abc;;\nesac");

        var c = Assert.IsType<Command.Case>(result);
        var arm = Assert.Single(c.Arms);
        Assert.Equal(["a", "b", "c"], arm.Patterns.ToArray());
    }

    [Fact]
    public void Parse_CaseWildcardDefault_PatternIsStar()
    {
        var result = Parse("case $x in\n*) echo other;;\nesac");

        var c = Assert.IsType<Command.Case>(result);
        var arm = Assert.Single(c.Arms);
        Assert.Equal("*", Assert.Single(arm.Patterns));
    }

    [Fact]
    public void Parse_CaseArmBody_IsSimpleCommand()
    {
        var result = Parse("case $x in\nhello) echo hi;;\nesac");

        var c = Assert.IsType<Command.Case>(result);
        var body = Assert.IsType<Command.Simple>(c.Arms[0].Body);
        Assert.Equal("echo", GetWordValues(body)[0]);
    }

    [Fact]
    public void Parse_CaseLeadingParen_ArmParsedCorrectly()
    {
        // Some scripts write (pattern) instead of pattern)
        var result = Parse("case $x in\n(yes) echo y;;\nesac");

        var c = Assert.IsType<Command.Case>(result);
        Assert.Equal("yes", Assert.Single(c.Arms[0].Patterns));
    }

    // ── Standalone arithmetic command (( )) parser tests ───────────────────

    [Fact]
    public void Parse_ArithCommand_Addition_ProducesArithCommandNode()
    {
        var result = Parse("(( x + 1 ))");

        var arith = Assert.IsType<Command.ArithCommand>(result);
        Assert.Equal("x + 1", arith.Expr);
    }

    [Fact]
    public void Parse_ArithCommand_Increment_ProducesCorrectExpr()
    {
        var result = Parse("(( x++ ))");

        var arith = Assert.IsType<Command.ArithCommand>(result);
        Assert.Equal("x++", arith.Expr);
    }

    [Fact]
    public void Parse_ArithCommand_Comparison_ProducesCorrectExpr()
    {
        var result = Parse("(( x > 5 ))");

        var arith = Assert.IsType<Command.ArithCommand>(result);
        Assert.Equal("x > 5", arith.Expr);
    }

    [Fact]
    public void Parse_ArithCommand_Assignment_ProducesCorrectExpr()
    {
        var result = Parse("(( x = y + 2 ))");

        var arith = Assert.IsType<Command.ArithCommand>(result);
        Assert.Equal("x = y + 2", arith.Expr);
    }

    [Fact]
    public void Parse_ArithCommand_InList_ParsedCorrectly()
    {
        // (( x++ )); echo done  — arith cmd in a command list
        var result = Parse("(( x++ )); echo done");

        var list = Assert.IsType<Command.CommandList>(result);
        Assert.IsType<Command.ArithCommand>(list.Commands[0]);
        Assert.IsType<Command.Simple>(list.Commands[1]);
    }

    // ── Additional error reporting tests ────────────────────────────────────

    [Fact]
    public void Parse_UnclosedSubshell_ThrowsParseExceptionWithPosition()
    {
        var ex = Assert.Throws<ParseException>(() => Parse("(echo hello"));

        Assert.Equal(1, ex.Line);
        Assert.Contains(")", ex.Message);
        Assert.Equal("ParseSubshell", ex.Rule);
    }

    [Fact]
    public void Parse_ForMissingDone_ThrowsParseException()
    {
        var ex = Assert.Throws<ParseException>(() => Parse("for x in a b c; do echo $x"));

        Assert.Equal(1, ex.Line);
        Assert.Contains("done", ex.Message);
        Assert.NotEmpty(ex.Rule);
    }

    [Fact]
    public void Parse_MissingThen_ThrowsParseException()
    {
        var ex = Assert.Throws<ParseException>(() => Parse("if true; echo hi; fi"));

        Assert.Equal(1, ex.Line);
        Assert.Contains("then", ex.Message);
        Assert.Equal("Expect", ex.Rule);
    }

    [Fact]
    public void Parse_ElifMissingFi_ThrowsParseException()
    {
        // if/elif without closing fi — the parser should throw at EOF.
        var input = "if true; then echo a\nelif false; then echo b";

        var ex = Assert.Throws<ParseException>(() => Parse(input));

        Assert.True(ex.Line >= 1);
        Assert.Contains("fi", ex.Message);
    }

    [Fact]
    public void Parse_UnclosedSubshellMultiline_ReportsCorrectLine()
    {
        var input = "(\n  echo hello\n  echo world";

        var ex = Assert.Throws<ParseException>(() => Parse(input));

        Assert.True(ex.Line >= 3, $"Expected error on line 3 or later, got line {ex.Line}");
        Assert.Contains(")", ex.Message);
    }

    [Fact]
    public void Parse_CaseMissingCloseParen_ThrowsParseException()
    {
        // Pattern without closing ) before body
        var input = "case $x in\n  a echo yes;;\nesac";

        var ex = Assert.Throws<ParseException>(() => Parse(input));

        Assert.True(ex.Line >= 1);
        Assert.NotEmpty(ex.Rule);
    }

    [Fact]
    public void Parse_MissingDoInWhile_ThrowsParseException()
    {
        var ex = Assert.Throws<ParseException>(() => Parse("while true; echo hi; done"));

        Assert.Equal(1, ex.Line);
        Assert.Contains("do", ex.Message);
        Assert.Equal("Expect", ex.Rule);
    }

    [Fact]
    public void Parse_MissingDoInFor_ThrowsParseException()
    {
        var ex = Assert.Throws<ParseException>(() => Parse("for x in a b c; echo $x; done"));

        Assert.Equal(1, ex.Line);
        Assert.Contains("do", ex.Message);
        Assert.Equal("Expect", ex.Rule);
    }
}
