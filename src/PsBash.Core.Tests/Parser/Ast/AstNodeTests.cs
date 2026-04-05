using System.Collections.Immutable;
using Xunit;
using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Tests.Parser.Ast;

public class AstNodeTests
{
    [Fact]
    public void Literal_StoresValue()
    {
        var node = new WordPart.Literal("hello");

        Assert.Equal("hello", node.Value);
    }

    [Fact]
    public void SingleQuoted_StoresValue()
    {
        var node = new WordPart.SingleQuoted("hello world");

        Assert.Equal("hello world", node.Value);
    }

    [Fact]
    public void DoubleQuoted_StoresParts()
    {
        var parts = ImmutableArray.Create<WordPart>(
            new WordPart.Literal("hello "),
            new WordPart.SimpleVarSub("name"));

        var node = new WordPart.DoubleQuoted(parts);

        Assert.Equal(2, node.Parts.Length);
        Assert.IsType<WordPart.Literal>(node.Parts[0]);
        Assert.IsType<WordPart.SimpleVarSub>(node.Parts[1]);
    }

    [Fact]
    public void SimpleVarSub_StoresName()
    {
        var node = new WordPart.SimpleVarSub("HOME");

        Assert.Equal("HOME", node.Name);
    }

    [Fact]
    public void BracedVarSub_StoresNameAndSuffix()
    {
        var node = new WordPart.BracedVarSub("foo", ":-default");

        Assert.Equal("foo", node.Name);
        Assert.Equal(":-default", node.Suffix);
    }

    [Fact]
    public void BracedVarSub_NullSuffix()
    {
        var node = new WordPart.BracedVarSub("foo", null);

        Assert.Null(node.Suffix);
    }

    [Fact]
    public void CommandSub_StoresBody()
    {
        var inner = new Command.Simple(
            ImmutableArray.Create(new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal("ls")))),
            ImmutableArray<EnvPair>.Empty,
            ImmutableArray<Redirect>.Empty);

        var node = new WordPart.CommandSub(inner);

        Assert.IsType<Command.Simple>(node.Body);
    }

    [Fact]
    public void TildeSub_NullUser_RepresentsBareHome()
    {
        var node = new WordPart.TildeSub(null);

        Assert.Null(node.User);
    }

    [Fact]
    public void TildeSub_WithUser()
    {
        var node = new WordPart.TildeSub("bob");

        Assert.Equal("bob", node.User);
    }

    [Fact]
    public void CompoundWord_StoresParts()
    {
        var word = new CompoundWord(ImmutableArray.Create<WordPart>(
            new WordPart.Literal("hello"),
            new WordPart.SimpleVarSub("world")));

        Assert.Equal(2, word.Parts.Length);
    }

    [Fact]
    public void SimpleCommand_StoresAllFields()
    {
        var words = ImmutableArray.Create(
            new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal("echo"))),
            new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal("hi"))));
        var envPairs = ImmutableArray.Create(
            new EnvPair("FOO", new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal("bar")))));
        var redirects = ImmutableArray.Create(
            new Redirect(">", 1, new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal("out.txt")))));

        var cmd = new Command.Simple(words, envPairs, redirects);

        Assert.Equal(2, cmd.Words.Length);
        Assert.Single(cmd.EnvPairs);
        Assert.Single(cmd.Redirects);
    }

    [Fact]
    public void Pipeline_StoresCommandsAndOps()
    {
        var ls = new Command.Simple(
            ImmutableArray.Create(new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal("ls")))),
            ImmutableArray<EnvPair>.Empty,
            ImmutableArray<Redirect>.Empty);
        var grep = new Command.Simple(
            ImmutableArray.Create(new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal("grep")))),
            ImmutableArray<EnvPair>.Empty,
            ImmutableArray<Redirect>.Empty);

        var pipeline = new Command.Pipeline(
            ImmutableArray.Create<Command>(ls, grep),
            ImmutableArray.Create("|"),
            Negated: false);

        Assert.Equal(2, pipeline.Commands.Length);
        Assert.Single(pipeline.Ops);
        Assert.False(pipeline.Negated);
    }

    [Fact]
    public void Pipeline_Negated()
    {
        var cmd = new Command.Simple(
            ImmutableArray.Create(new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal("false")))),
            ImmutableArray<EnvPair>.Empty,
            ImmutableArray<Redirect>.Empty);

        var pipeline = new Command.Pipeline(
            ImmutableArray.Create<Command>(cmd),
            ImmutableArray<string>.Empty,
            Negated: true);

        Assert.True(pipeline.Negated);
    }

    [Fact]
    public void AndOrList_StoresCommandsAndOps()
    {
        var left = new Command.Simple(
            ImmutableArray.Create(new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal("make")))),
            ImmutableArray<EnvPair>.Empty,
            ImmutableArray<Redirect>.Empty);
        var right = new Command.Simple(
            ImmutableArray.Create(new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal("install")))),
            ImmutableArray<EnvPair>.Empty,
            ImmutableArray<Redirect>.Empty);

        var andOr = new Command.AndOrList(
            ImmutableArray.Create<Command>(left, right),
            ImmutableArray.Create("&&"));

        Assert.Equal(2, andOr.Commands.Length);
        Assert.Equal("&&", andOr.Ops[0]);
    }

    [Fact]
    public void CommandList_StoresCommands()
    {
        var a = new Command.Simple(
            ImmutableArray.Create(new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal("a")))),
            ImmutableArray<EnvPair>.Empty,
            ImmutableArray<Redirect>.Empty);
        var b = new Command.Simple(
            ImmutableArray.Create(new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal("b")))),
            ImmutableArray<EnvPair>.Empty,
            ImmutableArray<Redirect>.Empty);

        var list = new Command.CommandList(ImmutableArray.Create<Command>(a, b));

        Assert.Equal(2, list.Commands.Length);
    }

    [Fact]
    public void Redirect_StoresOpFdTarget()
    {
        var target = new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal("/dev/null")));

        var redir = new Redirect(">>", 2, target);

        Assert.Equal(">>", redir.Op);
        Assert.Equal(2, redir.Fd);
        Assert.Equal("/dev/null", ((WordPart.Literal)redir.Target.Parts[0]).Value);
    }

    [Fact]
    public void Assignment_Equal()
    {
        var value = new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal("bar")));

        var assign = new Assignment("foo", AssignOp.Equal, value);

        Assert.Equal("foo", assign.Name);
        Assert.Equal(AssignOp.Equal, assign.Op);
        Assert.NotNull(assign.Value);
    }

    [Fact]
    public void Assignment_PlusEqual()
    {
        var value = new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal("extra")));

        var assign = new Assignment("PATH", AssignOp.PlusEqual, value);

        Assert.Equal(AssignOp.PlusEqual, assign.Op);
    }

    [Fact]
    public void Assignment_NullValue()
    {
        var assign = new Assignment("x", AssignOp.Equal, null);

        Assert.Null(assign.Value);
    }

    [Fact]
    public void EnvPair_StoresNameAndValue()
    {
        var value = new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal("1")));

        var pair = new EnvPair("DEBUG", value);

        Assert.Equal("DEBUG", pair.Name);
        Assert.NotNull(pair.Value);
    }

    [Fact]
    public void ShAssignment_StoresPairs()
    {
        var pairs = ImmutableArray.Create(
            new Assignment("x", AssignOp.Equal, new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal("1")))),
            new Assignment("y", AssignOp.Equal, new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal("2")))));

        var cmd = new Command.ShAssignment(pairs);

        Assert.Equal(2, cmd.Pairs.Length);
    }

    [Fact]
    public void AllNodeTypes_AreRecords_SupportEquality()
    {
        var a = new WordPart.Literal("x");
        var b = new WordPart.Literal("x");

        Assert.Equal(a, b);
    }

    [Fact]
    public void AllNodeTypes_InheritBashNode()
    {
        BashNode literal = new WordPart.Literal("x");
        BashNode word = new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal("x")));
        BashNode redirect = new Redirect(">", 1, new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal("f"))));
        BashNode assign = new Assignment("x", AssignOp.Equal, null);
        BashNode envPair = new EnvPair("X", null);
        BashNode simple = new Command.Simple(
            ImmutableArray<CompoundWord>.Empty,
            ImmutableArray<EnvPair>.Empty,
            ImmutableArray<Redirect>.Empty);
        BashNode pipeline = new Command.Pipeline(
            ImmutableArray<Command>.Empty,
            ImmutableArray<string>.Empty,
            false);
        BashNode andOr = new Command.AndOrList(
            ImmutableArray<Command>.Empty,
            ImmutableArray<string>.Empty);
        BashNode cmdList = new Command.CommandList(ImmutableArray<Command>.Empty);
        BashNode shAssign = new Command.ShAssignment(ImmutableArray<Assignment>.Empty);

        Assert.IsAssignableFrom<BashNode>(literal);
        Assert.IsAssignableFrom<BashNode>(word);
        Assert.IsAssignableFrom<BashNode>(redirect);
        Assert.IsAssignableFrom<BashNode>(assign);
        Assert.IsAssignableFrom<BashNode>(envPair);
        Assert.IsAssignableFrom<BashNode>(simple);
        Assert.IsAssignableFrom<BashNode>(pipeline);
        Assert.IsAssignableFrom<BashNode>(andOr);
        Assert.IsAssignableFrom<BashNode>(cmdList);
        Assert.IsAssignableFrom<BashNode>(shAssign);
    }

    [Fact]
    public void ExhaustivePatternMatch_WordPart()
    {
        WordPart part = new WordPart.Literal("test");

        var result = part switch
        {
            WordPart.Literal l => l.Value,
            WordPart.EscapedLiteral el => el.Value,
            WordPart.SingleQuoted sq => sq.Value,
            WordPart.DoubleQuoted dq => dq.Parts.Length.ToString(),
            WordPart.SimpleVarSub sv => sv.Name,
            WordPart.BracedVarSub bv => bv.Name,
            WordPart.CommandSub cs => cs.Body.ToString()!,
            WordPart.ArithSub ar => ar.Expr,
            WordPart.TildeSub ts => ts.User ?? "~",
            WordPart.GlobPart gp => gp.Pattern,
            _ => throw new InvalidOperationException("Unknown word part type"),
        };

        Assert.Equal("test", result);
    }

    [Fact]
    public void ExhaustivePatternMatch_Command()
    {
        Command cmd = new Command.Simple(
            ImmutableArray<CompoundWord>.Empty,
            ImmutableArray<EnvPair>.Empty,
            ImmutableArray<Redirect>.Empty);

        var result = cmd switch
        {
            Command.Simple => "simple",
            Command.Pipeline => "pipeline",
            Command.AndOrList => "andor",
            Command.CommandList => "list",
            Command.ShAssignment => "assign",
            _ => throw new InvalidOperationException("Unknown command type"),
        };

        Assert.Equal("simple", result);
    }
}
