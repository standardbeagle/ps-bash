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
    public void Parse_AmpGreatRedirect_ProducesRedirectOp()
    {
        var result = Parse("cmd &> out.log");

        var simple = Assert.IsType<Command.Simple>(result);
        Assert.Equal(["cmd"], GetWordValues(simple));
        var redirect = Assert.Single(simple.Redirects);
        Assert.Equal("&>", redirect.Op);
    }

    [Fact]
    public void Parse_AmpDGreatRedirect_ProducesAppendRedirectOp()
    {
        var result = Parse("cmd &>> out.log");

        var simple = Assert.IsType<Command.Simple>(result);
        var redirect = Assert.Single(simple.Redirects);
        Assert.Equal("&>>", redirect.Op);
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
    public void Parse_LocaleTranslationQuote_MatchesDoubleQuotedInterpolationAndEscaping()
    {
        var localized = Parse("echo $\"say \\\"hi\\\" to $USER; cost \\$5; slash \\\\\"");
        var plain = Parse("echo \"say \\\"hi\\\" to $USER; cost \\$5; slash \\\\\"");

        var localizedSimple = Assert.IsType<Command.Simple>(localized);
        var plainSimple = Assert.IsType<Command.Simple>(plain);
        var localizedQuote = Assert.IsType<WordPart.DoubleQuoted>(Assert.Single(localizedSimple.Words[1].Parts));
        var plainQuote = Assert.IsType<WordPart.DoubleQuoted>(Assert.Single(plainSimple.Words[1].Parts));

        static string Describe(WordPart part) => part switch
        {
            WordPart.Literal literal => $"literal:{literal.Value}",
            WordPart.SimpleVarSub variable => $"variable:{variable.Name}",
            _ => $"{part.GetType().Name}:{part}"
        };

        Assert.Equal(plainQuote.Parts.Select(Describe), localizedQuote.Parts.Select(Describe));
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
    public void Parse_NegatedSimpleCommand_ReturnsPipelineWithNegatedTrue()
    {
        var result = Parse("! grep -q pattern file");

        var pipeline = Assert.IsType<Command.Pipeline>(result);
        Assert.True(pipeline.Negated);
        Assert.Single(pipeline.Commands);
        var inner = Assert.IsType<Command.Simple>(pipeline.Commands[0]);
        Assert.Equal(["grep", "-q", "pattern", "file"], GetWordValues(inner));
    }

    [Fact]
    public void Parse_NegatedPipeline_ReturnsPipelineWithNegatedTrue()
    {
        var result = Parse("! cmd1 | cmd2");

        var pipeline = Assert.IsType<Command.Pipeline>(result);
        Assert.True(pipeline.Negated);
        Assert.Equal(2, pipeline.Commands.Length);
        Assert.Equal(new[] { "|" }, pipeline.Ops.ToArray());
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
    public void Parse_NamedFdRedirect_CapturesFdVar()
    {
        var result = Parse("exec {fd}> output.txt");

        var simple = Assert.IsType<Command.Simple>(result);
        Assert.Equal(["exec"], GetWordValues(simple));
        var redir = Assert.Single(simple.Redirects);
        Assert.Equal(">", redir.Op);
        Assert.Equal(1, redir.Fd);
        Assert.Equal("fd", redir.FdVar);
        Assert.Equal("output.txt", Assert.IsType<WordPart.Literal>(Assert.Single(redir.Target.Parts)).Value);
    }

    [Fact]
    public void Parse_NamedFdInputRedirect_CapturesFdVar()
    {
        var result = Parse("exec {fd}< input.txt");

        var simple = Assert.IsType<Command.Simple>(result);
        Assert.Equal(["exec"], GetWordValues(simple));
        var redir = Assert.Single(simple.Redirects);
        Assert.Equal("<", redir.Op);
        Assert.Equal(0, redir.Fd);
        Assert.Equal("fd", redir.FdVar);
        Assert.Equal("input.txt", Assert.IsType<WordPart.Literal>(Assert.Single(redir.Target.Parts)).Value);
    }

    [Theory]
    [InlineData("<<", false)]
    [InlineData("<<-", true)]
    public void Parse_NamedFdHeredoc_PreservesBodyAndFdVar(string op, bool stripTabs)
    {
        var indent = stripTabs ? "\t" : "";
        var result = Parse($"exec {{fd}}{op}EOF\n{indent}body\nEOF");

        var simple = Assert.IsType<Command.Simple>(result);
        Assert.Empty(simple.Redirects);
        var hereDoc = Assert.Single(simple.HereDocs);
        Assert.Equal("fd", hereDoc.FdVar);
        Assert.Equal("body\n", hereDoc.Body);
        Assert.Equal(stripTabs, hereDoc.StripTabs);
    }

    [Fact]
    public void Parse_NamedFdRedirectAfterBraceGroup_CapturesFdVar()
    {
        var result = Parse("{ echo hi; } {fd}>out");

        var group = Assert.IsType<Command.BraceGroup>(result);
        var redirect = Assert.Single(group.Redirects);
        Assert.Equal("fd", redirect.FdVar);
        Assert.Equal("out", Assert.IsType<WordPart.Literal>(Assert.Single(redirect.Target.Parts)).Value);
    }

    [Fact]
    public void Parse_NamedFdRedirectAfterSubshell_CapturesFdVar()
    {
        var result = Parse("(echo hi) {fd}>out");

        var subshell = Assert.IsType<Command.Subshell>(result);
        Assert.Equal("fd", Assert.Single(subshell.Redirects).FdVar);
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
    public void Parse_AssignmentSubscriptContainingEquals_SplitsOnAssignmentEqualsNotSubscriptEquals()
    {
        // bash: map[a=b]=c assigns key "a=b" the value "c" (confirmed against real bash via
        // WSL: `declare -A map; map[a=b]=c` yields KEY:[a=b] VAL:[c]). Splitting on the FIRST
        // '=' instead (the old bug) produces name "map[a" / value "b]=c".
        var result = Parse("map[a=b]=c");

        var assign = Assert.IsType<Command.ShAssignment>(result);
        var pair = Assert.Single(assign.Pairs);
        Assert.Equal("map[a=b]", pair.Name);
        var lit = Assert.IsType<WordPart.Literal>(Assert.Single(pair.Value!.Parts));
        Assert.Equal("c", lit.Value);
    }

    [Fact]
    public void Parse_AssignmentSubscriptContainingEquals_PlusEqualStillDetected()
    {
        var result = Parse("map[a=b]+=c");

        var assign = Assert.IsType<Command.ShAssignment>(result);
        var pair = Assert.Single(assign.Pairs);
        Assert.Equal("map[a=b]", pair.Name);
        Assert.Equal(AssignOp.PlusEqual, pair.Op);
    }

    [Fact]
    public void Parse_PlainArraySubscriptAssignment_StillSplitsCorrectly()
    {
        // Regression guard: ordinary arr[0]=val (no '=' inside the subscript) must be unaffected.
        var result = Parse("arr[0]=val");

        var assign = Assert.IsType<Command.ShAssignment>(result);
        var pair = Assert.Single(assign.Pairs);
        Assert.Equal("arr[0]", pair.Name);
        var lit = Assert.IsType<WordPart.Literal>(Assert.Single(pair.Value!.Parts));
        Assert.Equal("val", lit.Value);
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
    public void Parse_BracedVarColonlessDefault_ReturnsBareOperatorSuffix()
    {
        // Colon-less ${VAR-w} must capture the bare `-` operator in the suffix,
        // not let it fall into the value (which dropped the operator at emit time).
        var result = Parse("echo ${VAR-fallback}");

        var simple = Assert.IsType<Command.Simple>(result);
        var bvs = Assert.IsType<WordPart.BracedVarSub>(Assert.Single(simple.Words[1].Parts));
        Assert.Equal("VAR", bvs.Name);
        Assert.Equal("-fallback", bvs.Suffix);
    }

    [Fact]
    public void Parse_BracedVarColonlessError_ReturnsBareOperatorSuffix()
    {
        var result = Parse("echo ${VAR?must be set}");

        var simple = Assert.IsType<Command.Simple>(result);
        var bvs = Assert.IsType<WordPart.BracedVarSub>(Assert.Single(simple.Words[1].Parts));
        Assert.Equal("VAR", bvs.Name);
        Assert.Equal("?must be set", bvs.Suffix);
    }

    [Fact]
    public void Parse_BracedVarTransform_ReturnsAtOperatorSuffix()
    {
        // ${VAR@Q} transform: the `@Q` must be captured as the suffix, not dropped.
        var result = Parse("echo ${VAR@Q}");

        var simple = Assert.IsType<Command.Simple>(result);
        var bvs = Assert.IsType<WordPart.BracedVarSub>(Assert.Single(simple.Words[1].Parts));
        Assert.Equal("VAR", bvs.Name);
        Assert.Equal("@Q", bvs.Suffix);
    }

    [Fact]
    public void Parse_BracedParamCount_ReturnsSpecialNameNullSuffix()
    {
        // ${#} must parse as the special parameter `#` (null suffix), not as a
        // zero-length-name length operator.
        var result = Parse("echo ${#}");

        var simple = Assert.IsType<Command.Simple>(result);
        var bvs = Assert.IsType<WordPart.BracedVarSub>(Assert.Single(simple.Words[1].Parts));
        Assert.Equal("#", bvs.Name);
        Assert.Null(bvs.Suffix);
    }

    [Fact]
    public void Parse_BracedPositional10_ReturnsDigitNameNullSuffix()
    {
        var result = Parse("echo ${10}");

        var simple = Assert.IsType<Command.Simple>(result);
        var bvs = Assert.IsType<WordPart.BracedVarSub>(Assert.Single(simple.Words[1].Parts));
        Assert.Equal("10", bvs.Name);
        Assert.Null(bvs.Suffix);
    }

    [Fact]
    public void Parse_BracedVarSubscriptWithSliceOp_CapturesSubscriptAndOperator()
    {
        // ${a[@]:1:2}: the subscript branch must capture the trailing `:1:2` operator
        // into the suffix, not return early and drop it.
        var result = Parse("echo ${a[@]:1:2}");

        var simple = Assert.IsType<Command.Simple>(result);
        var bvs = Assert.IsType<WordPart.BracedVarSub>(Assert.Single(simple.Words[1].Parts));
        Assert.Equal("a", bvs.Name);
        Assert.Equal("[@]:1:2", bvs.Suffix);
    }

    [Fact]
    public void Parse_BracedVarSubscriptWithRemovalOp_CapturesSubscriptAndOperator()
    {
        var result = Parse("echo ${arr[0]##*/}");

        var simple = Assert.IsType<Command.Simple>(result);
        var bvs = Assert.IsType<WordPart.BracedVarSub>(Assert.Single(simple.Words[1].Parts));
        Assert.Equal("arr", bvs.Name);
        Assert.Equal("[0]##*/", bvs.Suffix);
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
        Assert.Equal("i=0", Assert.IsType<ArithmeticSyntax>(forArith.Init).Source);
        Assert.Equal(" i<10", Assert.IsType<ArithmeticSyntax>(forArith.Cond).Source);
        Assert.Equal(" i++", Assert.IsType<ArithmeticSyntax>(forArith.Step).Source);
        Assert.IsType<ArithmeticExpr.Assignment>(forArith.Init.Root);
        Assert.IsType<ArithmeticExpr.Binary>(forArith.Cond.Root);
        Assert.IsType<ArithmeticExpr.Increment>(forArith.Step.Root);
        Assert.IsType<Command.Simple>(forArith.Body);
    }

    // H3 (review finding) claimed a quoted/escaped `)` inside a case pattern
    // would terminate the pattern early (because ConsumeCasePattern stops at the
    // first RParen token and joins token .Value). Verified NOT reproducible: the
    // lexer keeps a quoted string, an escaped paren, and an extglob group as a
    // SINGLE Word token, so the only RParen the parser sees is the genuine arm
    // terminator (a bare unquoted `)` in a bash case pattern is always the
    // terminator). These tests lock in that correct behavior.
    [Theory]
    [InlineData("case $x in \"(a)\") echo m;; esac", "\"(a)\"")]   // quoted paren stays in one token
    [InlineData("case $x in @(foo|bar)) echo m;; esac", "@(foo|bar)")] // extglob group not split on its )
    [InlineData("case $x in a\\)b) echo m;; esac", "a\\)b")]      // escaped ) retained, not a terminator
    public void Parse_CasePattern_QuotedEscapedExtglobParen_NotTruncated(string input, string expectedPattern)
    {
        var result = Parse(input);
        var c = Assert.IsType<Command.Case>(result);
        Assert.Equal(expectedPattern, c.Arms[0].Patterns[0]);
        // The arm body and esac still parse — proves the `)` did not desync.
        Assert.Single(c.Arms);
    }

    [Fact]
    public void Parse_CasePattern_MultiPatternPipe_SplitsCorrectly()
    {
        var result = Parse("case $x in foo|bar|baz) echo m;; esac");
        var c = Assert.IsType<Command.Case>(result);
        Assert.Equal(new[] { "foo", "bar", "baz" }, c.Arms[0].Patterns.ToArray());
    }

    [Fact]
    public void Parse_CaseFallThroughTerminators_RecordedNoDesync()
    {
        // H4: `;&` (fall-through) and `;;&` (continue-test) used to desync the
        // arm (lexed as Semi+Amp). They now parse cleanly with the terminator
        // recorded on each arm.
        var result = Parse("case $x in a) echo a ;& b) echo b ;;& c) echo c ;; esac");

        var c = Assert.IsType<Command.Case>(result);
        Assert.Equal(3, c.Arms.Length);
        Assert.Equal(CaseTerminator.FallThrough, c.Arms[0].Terminator);
        Assert.Equal(CaseTerminator.ContinueTest, c.Arms[1].Terminator);
        Assert.Equal(CaseTerminator.Break, c.Arms[2].Terminator);
    }

    [Fact]
    public void Parse_CaseBreakTerminator_DefaultsToBreak()
    {
        var result = Parse("case $x in a) echo a;; esac");
        var c = Assert.IsType<Command.Case>(result);
        Assert.Equal(CaseTerminator.Break, c.Arms[0].Terminator);
    }

    [Fact]
    public void Parse_CaseEmptyArmBody_DoesNotSwallowTerminator()
    {
        // An empty arm body (`x) ;;`) is legal bash (verified against the oracle:
        // `case x in x) ;; *) echo d ;; esac` runs clean, rc=0). ParseCaseArm used
        // to call SkipTerminators() after `)`, which ate the `;;`; the parser then
        // read the NEXT arm's pattern as a command word and died on its `)`.
        // This shape is on line 30 of the Claude Code Bash-tool shell snapshot
        // (`--signal=*|-e|--echo) ;;`), so it broke EVERY Bash-tool command.
        var result = Parse("case $a in x) ;; *) echo d ;; esac");

        var c = Assert.IsType<Command.Case>(result);
        Assert.Equal(2, c.Arms.Length);
        Assert.Equal("x", c.Arms[0].Patterns[0]);
        Assert.Equal("*", c.Arms[1].Patterns[0]);
    }

    [Fact]
    public void Parse_CaseEmptyArmBodies_MultipleConsecutive_AllArmsParsed()
    {
        var result = Parse("case $a in a) ;; b) ;; c) ;; *) echo d ;; esac");

        var c = Assert.IsType<Command.Case>(result);
        Assert.Equal(4, c.Arms.Length);
        Assert.Equal("a", c.Arms[0].Patterns[0]);
        Assert.Equal("b", c.Arms[1].Patterns[0]);
        Assert.Equal("c", c.Arms[2].Patterns[0]);
        Assert.Equal("*", c.Arms[3].Patterns[0]);
    }

    [Fact]
    public void Parse_CaseEmptyArmBody_BracketGlobPattern_ParsesNextArm()
    {
        // The exact snapshot shape: a bracket-class pattern arm with an empty body
        // followed by more arms.
        var result = Parse(
            "case \"$a\" in --signal=*|-e|--echo) ;; -[0-9]*) ;; *) echo p ;; esac");

        var c = Assert.IsType<Command.Case>(result);
        Assert.Equal(3, c.Arms.Length);
        Assert.Equal(new[] { "--signal=*", "-e", "--echo" }, c.Arms[0].Patterns);
        Assert.Equal("-[0-9]*", c.Arms[1].Patterns[0]);
    }

    [Fact]
    public void Parse_CaseEmptyPattern_Throws()
    {
        // `case x in ) esac` is a bash syntax error; an empty pattern must not
        // become a silently-match-nothing arm.
        Assert.Throws<ParseException>(() => Parse("case x in ) esac"));
        Assert.Throws<ParseException>(() => Parse("case x in a|) echo a;; esac"));
    }

    [Fact]
    public void Parse_ForArith_InnerParensInClause_NotTruncated()
    {
        // H2: the old per-token collector stopped at the FIRST `)`, truncating
        // the header at the inner paren of `i=(a+b)` and corrupting the loop.
        // The raw-slice approach keeps the whole clause and splits on top-level `;`.
        var result = Parse("for ((i=(a+b); i<n; i++)); do echo $i; done");

        var forArith = Assert.IsType<Command.ForArith>(result);
        Assert.Equal("i=(a+b)", Assert.IsType<ArithmeticSyntax>(forArith.Init).Source);
        Assert.Equal(" i<n", Assert.IsType<ArithmeticSyntax>(forArith.Cond).Source);
        Assert.Equal(" i++", Assert.IsType<ArithmeticSyntax>(forArith.Step).Source);
        var assignment = Assert.IsType<ArithmeticExpr.Assignment>(forArith.Init.Root);
        Assert.IsType<ArithmeticExpr.Binary>(assignment.Value);
        Assert.IsType<Command.Simple>(forArith.Body);
    }

    [Fact]
    public void Parse_ForArith_NestedDoubleParens_DoNotCloseHeaderEarly()
    {
        var result = Parse("for ((i=((a+b)); i<n; i++)); do echo $i; done");

        var forArith = Assert.IsType<Command.ForArith>(result);
        Assert.Equal("i=((a+b))", Assert.IsType<ArithmeticSyntax>(forArith.Init).Source);
        Assert.Equal(" i<n", Assert.IsType<ArithmeticSyntax>(forArith.Cond).Source);
        Assert.Equal(" i++", Assert.IsType<ArithmeticSyntax>(forArith.Step).Source);
        Assert.IsType<Command.Simple>(forArith.Body);
    }

    [Fact]
    public void Parse_ForArith_PositionalParameter_ProducesTypedParameter()
    {
        var forArith = Assert.IsType<Command.ForArith>(Parse("for ((i=$1; i<$#; i++)); do echo $i; done"));

        Assert.IsType<ArithmeticExpr.Parameter>(Assert.IsType<ArithmeticExpr.Assignment>(forArith.Init!.Root).Value);
        var condition = Assert.IsType<ArithmeticExpr.Binary>(forArith.Cond!.Root);
        Assert.Equal("$#", Assert.IsType<ArithmeticExpr.Parameter>(condition.Right).Name);
    }

    [Fact]
    public void Parse_ForArith_EmptyClauses_YieldAbsentSyntax()
    {
        var result = Parse("for ((;;)); do echo hi; done");

        var forArith = Assert.IsType<Command.ForArith>(result);
        Assert.Null(forArith.Init);
        Assert.Null(forArith.Cond);
        Assert.Null(forArith.Step);
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
    public void Parse_Select_ParsesAsSelectNode_DoesNotThrow()
    {
        // H5: `select` used to throw and ABORT the whole transpile. It now
        // parses into a Select node (same grammar as for-in) so the surrounding
        // script still transpiles; the emitter degrades it to a comment.
        var result = Parse("select x in a b c; do echo $x; done");

        var sel = Assert.IsType<Command.Select>(result);
        Assert.Equal("x", sel.Var);
        Assert.Equal(3, sel.List.Length);
        Assert.IsType<Command.Simple>(sel.Body);
    }

    [Fact]
    public void Parse_SelectInScript_OtherStatementsStillParse()
    {
        // Degradation: a select in the middle must not abort the whole parse.
        var result = Parse("echo before; select x in a b; do echo $x; done; echo after");

        var list = Assert.IsType<Command.CommandList>(result);
        Assert.Equal(3, list.Commands.Length);
        Assert.IsType<Command.Select>(list.Commands[1]);
    }

    [Fact]
    public void Parse_ParensFunction_SubshellBody()
    {
        // bash allows any compound body, e.g. f() ( subshell ). Used to require { }.
        var result = Parse("f() ( echo hi )");
        var fn = Assert.IsType<Command.ShFunction>(result);
        Assert.Equal("f", fn.Name);
        Assert.IsType<Command.Subshell>(fn.Body);
    }

    [Fact]
    public void Parse_ParensFunction_ForLoopBody()
    {
        var result = Parse("f() for x in a b; do echo $x; done");
        var fn = Assert.IsType<Command.ShFunction>(result);
        Assert.IsType<Command.ForIn>(fn.Body);
    }

    [Fact]
    public void Parse_FunctionKeyword_BraceBody_StillWorks()
    {
        // Regression: the common `function f { ... }` form is unchanged.
        var result = Parse("function f { echo hi; }");
        var fn = Assert.IsType<Command.ShFunction>(result);
        Assert.Equal("f", fn.Name);
    }

    [Fact]
    public void Parse_SingleNegation_WrapsInNegatedPipeline()
    {
        var result = Parse("! true");
        var pipeline = Assert.IsType<Command.Pipeline>(result);
        Assert.True(pipeline.Negated);
    }

    [Fact]
    public void Parse_DoubleNegation_IsIdentity_NotDropped()
    {
        // `! ! true` = identity: the command survives (not an empty/negated
        // wrapper). Two `!` cancel, so it's the bare command.
        var result = Parse("! ! true");
        var simple = Assert.IsType<Command.Simple>(result);
        Assert.Equal(["true"], GetWordValues(simple));
    }

    [Fact]
    public void Parse_TripleNegation_NegatesOnce()
    {
        var result = Parse("! ! ! true");
        var pipeline = Assert.IsType<Command.Pipeline>(result);
        Assert.True(pipeline.Negated);
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

    // Advance-guard (H16): a stray close-token inside a compound body used to
    // make the body loop spin forever (ParseAndOr returns an empty command
    // without consuming the token). The ParseAndOrProgress guard now throws a
    // clean ParseException instead of hanging. If the guard regresses, these
    // tests HANG rather than fail — which is itself the signal.
    [Fact]
    public void Parse_StrayParenInBraceGroup_ThrowsNotHang()
    {
        Assert.Throws<ParseException>(() => Parse("{ ) }"));
    }

    [Fact]
    public void Parse_StrayParenInCaseBody_ThrowsNotHang()
    {
        Assert.Throws<ParseException>(() => Parse("case x in a) ;; ) esac"));
    }

    [Fact]
    public void Parse_StrayCloseBraceInSubshell_ThrowsNotHang()
    {
        Assert.Throws<ParseException>(() => Parse("( } )"));
    }

    [Fact]
    public void Parse_StrayCloseParenAtTopLevel_ThrowsNotHang()
    {
        Assert.Throws<ParseException>(() => Parse(")"));
    }

    [Fact]
    public void Parse_StrayCloseBraceMidList_ThrowsNotHang()
    {
        // Exercises the guard at the ParseList LOOP call site (the second
        // command position), not just the first-command site.
        Assert.Throws<ParseException>(() => Parse("echo a; } echo b"));
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

    [Theory]
    [InlineData("[[:alpha:]]")]
    [InlineData("[[:digit:][:upper:]]")]
    public void Parse_PosixGlobCharClass_ProducesSingleGlobPart(string pattern)
    {
        var simple = Assert.IsType<Command.Simple>(Parse($"echo {pattern}"));

        var glob = Assert.IsType<WordPart.GlobPart>(Assert.Single(simple.Words[1].Parts));
        Assert.Equal(pattern, glob.Pattern);
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

        var hereDoc = Assert.Single(simple.HereDocs);
        // bash heredoc body terminates the last line with a newline.
        Assert.Equal("line 1\nline 2\n", hereDoc.Body);
        Assert.True(hereDoc.Expand);
        Assert.False(hereDoc.StripTabs);
    }

    [Fact]
    public void Parse_HeredocWithVariableExpansion_SetsExpandTrue()
    {
        var simple = Assert.IsType<Command.Simple>(Parse("cat <<EOF\nhello $NAME\nEOF"));

        var hereDoc = Assert.Single(simple.HereDocs);
        Assert.Equal("hello $NAME\n", hereDoc.Body);
        Assert.True(hereDoc.Expand);
    }

    [Fact]
    public void Parse_QuotedDelimiter_SetsExpandFalse()
    {
        var simple = Assert.IsType<Command.Simple>(Parse("cat <<'EOF'\nhello $NAME\nEOF"));

        var hereDoc = Assert.Single(simple.HereDocs);
        Assert.Contains("$NAME", hereDoc.Body);
        Assert.False(hereDoc.Expand);
    }

    [Fact]
    public void Parse_DLessDash_SetsStripTabsTrue()
    {
        var simple = Assert.IsType<Command.Simple>(Parse("cat <<-EOF\n\tline 1\n\tline 2\nEOF"));

        var hereDoc = Assert.Single(simple.HereDocs);
        Assert.Equal("line 1\nline 2\n", hereDoc.Body);
        Assert.True(hereDoc.StripTabs);
    }

    [Fact]
    public void Parse_HeredocAsStdin_CommandWordsPreserved()
    {
        var simple = Assert.IsType<Command.Simple>(Parse("grep -i foo <<EOF\nhello foo\nbar\nEOF"));

        var words = GetWordValues(simple);
        Assert.Equal(["grep", "-i", "foo"], words);
        var hereDoc = Assert.Single(simple.HereDocs);
        Assert.Equal("hello foo\nbar\n", hereDoc.Body);
    }

    [Fact]
    public void Parse_MultipleHeredocs_CollectsAllBodies()
    {
        var simple = Assert.IsType<Command.Simple>(
            Parse("cat <<EOF1 <<EOF2\nfirst\nEOF1\nsecond\nEOF2"));

        Assert.Equal(2, simple.HereDocs.Length);
        Assert.Equal("first\n", simple.HereDocs[0].Body);
        Assert.Equal("second\n", simple.HereDocs[1].Body);
        Assert.True(simple.HereDocs[0].Expand);
        Assert.True(simple.HereDocs[1].Expand);
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
        Assert.Equal(" x + 1 ", arith.Expr.Source);
        var binary = Assert.IsType<ArithmeticExpr.Binary>(arith.Expr.Root);
        Assert.Equal(ArithmeticBinaryOp.Add, binary.Op);
    }

    [Fact]
    public void Parse_ArithCommand_Increment_ProducesCorrectExpr()
    {
        var result = Parse("(( x++ ))");

        var arith = Assert.IsType<Command.ArithCommand>(result);
        Assert.Equal(" x++ ", arith.Expr.Source);
        Assert.IsType<ArithmeticExpr.Increment>(arith.Expr.Root);
    }

    [Fact]
    public void Parse_ArithCommand_Comparison_ProducesCorrectExpr()
    {
        var result = Parse("(( x > 5 ))");

        var arith = Assert.IsType<Command.ArithCommand>(result);
        Assert.Equal(" x > 5 ", arith.Expr.Source);
        Assert.Equal(ArithmeticBinaryOp.Greater, Assert.IsType<ArithmeticExpr.Binary>(arith.Expr.Root).Op);
    }

    [Fact]
    public void Parse_ArithCommand_Assignment_ProducesCorrectExpr()
    {
        var result = Parse("(( x = y + 2 ))");

        var arith = Assert.IsType<Command.ArithCommand>(result);
        Assert.Equal(" x = y + 2 ", arith.Expr.Source);
        var assignment = Assert.IsType<ArithmeticExpr.Assignment>(arith.Expr.Root);
        Assert.IsType<ArithmeticExpr.Binary>(assignment.Value);
    }

    [Theory]
    [InlineData("$1")]
    [InlineData("$#")]
    [InlineData("$?")]
    [InlineData("$@")]
    [InlineData("$*")]
    [InlineData("$$")]
    [InlineData("$!")]
    public void Parse_ArithCommand_PositionalAndSpecialParameters_AreTyped(string parameter)
    {
        var arith = Assert.IsType<Command.ArithCommand>(Parse($"(( {parameter} ))"));

        Assert.Equal($" {parameter} ", arith.Expr.Source);
        Assert.Equal(parameter, Assert.IsType<ArithmeticExpr.Parameter>(arith.Expr.Root).Name);
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

    [Fact]
    public void Parse_ArithCommand_NestedConsecutiveCloses_PreservesExpressionAndNextCommand()
    {
        var result = Assert.IsType<Command.CommandList>(Parse("(( ((1+2)) + 3 )); echo done"));

        var arith = Assert.IsType<Command.ArithCommand>(result.Commands[0]);
        Assert.Equal(" ((1+2)) + 3 ", arith.Expr.Source);
        var add = Assert.IsType<ArithmeticExpr.Binary>(arith.Expr.Root);
        Assert.Equal(ArithmeticBinaryOp.Add, add.Op);
        Assert.IsType<ArithmeticExpr.Binary>(add.Left);
        Assert.IsType<Command.Simple>(result.Commands[1]);
    }

    [Fact]
    public void Parse_ArithSub_PreservesSourceAndProducesTypedNestedTree()
    {
        var result = Assert.IsType<Command.Simple>(Parse("echo $(( (x + 1) * 2 ))"));

        var arith = Assert.IsType<WordPart.ArithSub>(Assert.Single(result.Words[1].Parts));
        Assert.Equal(" (x + 1) * 2 ", arith.Expr.Source);
        var multiply = Assert.IsType<ArithmeticExpr.Binary>(arith.Expr.Root);
        Assert.Equal(ArithmeticBinaryOp.Multiply, multiply.Op);
        Assert.Equal(ArithmeticBinaryOp.Add, Assert.IsType<ArithmeticExpr.Binary>(multiply.Left).Op);
    }

    [Fact]
    public void Parse_ArithSub_SpecialParameter_ProducesTypedParameter()
    {
        var result = Assert.IsType<Command.Simple>(Parse("echo $(( $? ))"));

        var arith = Assert.IsType<WordPart.ArithSub>(Assert.Single(result.Words[1].Parts));
        Assert.Equal(" $? ", arith.Expr.Source);
        Assert.Equal("$?", Assert.IsType<ArithmeticExpr.Parameter>(arith.Expr.Root).Name);
    }

    [Theory]
    [InlineData("$10", "1", "$10", "0")]
    [InlineData("${1}", "1", "${1}", "")]
    [InlineData("${10}", "10", "${10}", "")]
    [InlineData("${x}", "x", "${x}", "")]
    [InlineData("${?}", "?", "${?}", "")]
    public void Parse_ArithmeticParameter_NormalizesLookupAndPreservesSpelling(
        string source, string key, string spelling, string suffix)
    {
        var parameter = Assert.IsType<ArithmeticExpr.Parameter>(BashArithmeticParser.Parse(source).Root);

        Assert.Equal(key, parameter.LookupKey);
        Assert.Equal(spelling, parameter.Spelling);
        Assert.Equal(suffix, parameter.UnbracedSuffix);
    }

    [Fact]
    public void ArithmeticNodes_LegacyStringConstruction_PreservesSourceAndTypedProperties()
    {
        var command = new Command.ArithCommand(" x + 1 ");
        var substitution = new WordPart.ArithSub(" $1 ");
        var body = new Command.Simple([], [], []);
        var loop = new Command.ForArith("i=0", "", "i++", body);

        Assert.Equal(" x + 1 ", command.Expr.Source);
        Assert.Equal(" $1 ", substitution.Expr.Source);
        Assert.Equal("i=0", loop.Init!.Source);
        Assert.Equal("i++", loop.Step!.Source);
        Assert.Null(loop.Cond);
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

    // ===================== Trailing redirects on compound commands =====================
    // Regression: only Command.Simple/Subshell carried a Redirects field, so a trailing
    // redirect on a while/for/if/case/brace-group was orphaned and silently dropped
    // (e.g. `while ...; done < input.txt` read nothing). Each compound node now parses
    // its trailing redirects.

    [Fact]
    public void Parse_WhileWithInputRedirect_AttachesRedirectToWhile()
    {
        var result = Parse("while read line; do echo $line; done < input.txt");

        var whileCmd = Assert.IsType<Command.While>(result);
        var redirect = Assert.Single(whileCmd.Redirects);
        Assert.Equal("<", redirect.Op);
        Assert.Equal(0, redirect.Fd);
    }

    [Fact]
    public void Parse_ForInWithOutputRedirect_AttachesRedirectToFor()
    {
        var result = Parse("for x in 1 2; do echo $x; done > out.txt");

        var forIn = Assert.IsType<Command.ForIn>(result);
        var redirect = Assert.Single(forIn.Redirects);
        Assert.Equal(">", redirect.Op);
        Assert.Equal(1, redirect.Fd);
    }

    [Fact]
    public void Parse_IfWithOutputRedirect_AttachesRedirectToIf()
    {
        var result = Parse("if true; then echo hi; fi > log.txt");

        var ifCmd = Assert.IsType<Command.If>(result);
        Assert.Equal(">", Assert.Single(ifCmd.Redirects).Op);
    }

    [Fact]
    public void Parse_BraceGroupWithOutputRedirect_AttachesRedirectToGroup()
    {
        var result = Parse("{ echo a; echo b; } > out.txt");

        var group = Assert.IsType<Command.BraceGroup>(result);
        Assert.Equal(">", Assert.Single(group.Redirects).Op);
    }

    [Fact]
    public void Parse_CaseWithOutputRedirect_AttachesRedirectToCase()
    {
        var result = Parse("case $x in a) echo hi;; esac > out.txt");

        var caseCmd = Assert.IsType<Command.Case>(result);
        Assert.Equal(">", Assert.Single(caseCmd.Redirects).Op);
    }

    [Fact]
    public void Parse_WhileWithInputRedirectThenPipe_KeepsBothRedirectAndPipeStage()
    {
        // Cascade repro: the orphaned redirect used to also swallow the `| sort` stage.
        var result = Parse("while read a; do echo $a; done < f | sort");

        var pipeline = Assert.IsType<Command.Pipeline>(result);
        Assert.Equal(2, pipeline.Commands.Length);
        var whileCmd = Assert.IsType<Command.While>(pipeline.Commands[0]);
        Assert.Equal("<", Assert.Single(whileCmd.Redirects).Op);
    }

    [Fact]
    public void Parse_ForWithoutRedirect_HasEmptyRedirects()
    {
        var result = Parse("for x in 1 2; do echo $x; done");

        var forIn = Assert.IsType<Command.ForIn>(result);
        Assert.True(forIn.Redirects.IsDefaultOrEmpty);
    }
}
