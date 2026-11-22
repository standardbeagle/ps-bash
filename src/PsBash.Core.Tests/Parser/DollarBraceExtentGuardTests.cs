using System.Text;
using PsBash.Core.Parser;
using PsBash.Core.Parser.Ast;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// Guard for the ONE `${…}` extent scanner, <see cref="BashLexer.DollarBraceClose"/> (docs/specs/parser-grammar.md
/// §Parameter expansion extent). bash closes a `${` at its FIRST unquoted, unescaped `}`: a bare `{` in the operand does
/// not nest, only a nested `${` does. The lexer and the word parser each counted every `{`, so `\}` (`${x/ /{1,2\}}`)
/// ran both to end of input and ate the rest of the script. Every reader of an extent (lexer word, word parser,
/// heredoc body, arithmetic, subscripts, `:` splitting) now asks the one scanner; these tests pin lexer = parser =
/// bash for a table from bash 5.2 and for generated operands, and that the text after the expansion survives.
/// </summary>
public class DollarBraceExtentGuardTests
{
    /// <summary>(the whole `${…}` as bash reads it, the text after it). Each extent checked against bash 5.2.21.</summary>
    public static IEnumerable<object[]> OracleExtents() => new[]
    {
        new object[] { "${x/ /{1,2\\}}", "; echo after" },
        new object[] { "${x/ /\\}}", "}" },
        new object[] { "${x:-{a,b\\}}", "" },
        new object[] { "${u:-{a,b}", "}" },
        new object[] { "${u:-{a}", "b}" },
        new object[] { "${u:-{}", "" },
        new object[] { "${u:-${u:-{a}}", "}" },
        new object[] { "${u:-$(echo })}", "x" },
        new object[] { "${u:-`echo }`}", "" },
        new object[] { "${u:-$'\\'}'}", "" },
        new object[] { "${x:-'}'}", "" },
        new object[] { "${a[0]:-{z}", "}" },
        new object[] { "${u:-\"{}\"}", "" },
        new object[] { "${u:-\\{\\}}", "" },
        new object[] { "${u:-a:{b}", ":c}" },
    };

    [Theory]
    [MemberData(nameof(OracleExtents))]
    public void LexerAndParser_CloseWhereBashDoes(string expansion, string tail)
    {
        string raw = expansion + tail;
        Assert.Equal(expansion.Length - 1, BashLexer.DollarBraceClose(raw, 0));
        Assert.Equal(expansion.Length, BashLexer.ScanDollarBrace(raw, 0));
        Assert.Equal(expansion.Length, BashParser.BracedVarEndForTest(raw, 0));
        Assert.Equal(expansion.Length, BashParser.BracedVarEndForTest(raw, 0, inDoubleQuote: true));
    }

    // The R1 report: the command after the expansion was swallowed into it.
    [Fact]
    public void Tokenize_EscapedCloseInOperand_KeepsTheNextCommand()
    {
        var words = BashLexer.Tokenize("p ${x/ /{1,2\\}}; echo after")
            .Where(t => t.Kind != BashTokenKind.Eof).Select(t => (t.Kind, t.Value)).ToList();
        Assert.Equal(new (BashTokenKind, string)[]
        {
            (BashTokenKind.Word, "p"), (BashTokenKind.Word, "${x/ /{1,2\\}}"), (BashTokenKind.Semi, ";"),
            (BashTokenKind.Word, "echo"), (BashTokenKind.Word, "after"),
        }, words);
    }

    [Fact]
    public void Transpile_EscapedCloseInOperand_RunsTheNextCommand()
        => Assert.Contains("Invoke-BashEcho", PsEmitter.Transpile("p ${x/ /{1,2\\}}; echo after")!);

    // A `)` inside a `${…}` inside `$( )` is operand text, not the end of the command substitution.
    [Fact]
    public void DollarParen_ParenInsideDollarBrace_IsOperandText()
    {
        const string raw = "$(echo ${u:-)})";
        Assert.Equal(raw.Length, BashLexer.ScanDollarParen(raw, 0));
    }

    // Unclosed: every reader runs to end of input (and the lexer reports it the way it did).
    [Fact]
    public void Unclosed_IsMinusOne()
    {
        Assert.Equal(-1, BashLexer.DollarBraceClose("${u:-${v}", 0));
        Assert.Equal(-1, BashLexer.DollarBraceClose("${u:-'}", 0));
    }

    private static readonly string[] Ops = { ":-", "-", ":=", ":+", "+", "#", "##", "%", "%%", "/ /", "// /", "/#a/", ":?" };
    private static readonly string[] Atoms =
    {
        "a", "b", " ", "{", "{a,b", "{1,2", "\\}", "\\{", "\\\\", "'}'", "'{'", "\"}\"", "\"{\"", "$(echo })",
        "`echo }`", "$'\\x41}'", "${v}", "${v:-{}", "${v:-\\}}", ":", "~", "$x", "*", ",",
    };

    /// <summary>Property: for generated `${name OP word}` (operands of literal braces, escapes, quotes and nested
    /// expansions, none holding an unquoted `}`), lexer extent = parser extent = the generated text, and the text after
    /// it is a separate command.</summary>
    [Fact]
    public void Generated_ExtentIsTheSameForLexerAndParser()
    {
        var rng = new Random(0x5EED_B);
        for (int i = 0; i < 3000; i++)
        {
            var sb = new StringBuilder("${").Append(rng.Next(3) switch { 0 => "x", 1 => "a[1]", _ => "_v" });
            sb.Append(Ops[rng.Next(Ops.Length)]);
            int n = rng.Next(5);
            for (int k = 0; k < n; k++) sb.Append(Atoms[rng.Next(Atoms.Length)]);
            string expansion = sb.Append('}').ToString();
            string raw = expansion + "x}; echo after";
            string ctx = $"case {i}: {expansion}";

            Assert.True(expansion.Length - 1 == BashLexer.DollarBraceClose(raw, 0), ctx);
            Assert.True(expansion.Length == BashParser.BracedVarEndForTest(raw, 0), ctx);

            var toks = BashLexer.Tokenize("p " + raw).Where(t => t.Kind != BashTokenKind.Eof).ToList();
            Assert.True(toks.Count >= 4 && toks[^3].Kind == BashTokenKind.Semi && toks[^1].Value == "after", ctx);
            Assert.True(toks[1].Value.StartsWith(expansion + "x}", StringComparison.Ordinal), ctx);
        }
    }

    // Inside a brace expansion a `${…}` is item text: its `{` starts no expansion and its `,` splits nothing.
    [Theory]
    [InlineData("{a,${x/ /,}}", "a|${x/ /,}")]
    [InlineData("x{a,${u:-b,c}}y", "xay|x${u:-b,c}y")]
    [InlineData("{a,${x/ /\\}}}", "a|${x/ /\\}}")]
    public void BraceItem_HoldingDollarBrace_IsOneItem(string word, string items)
        => Assert.Equal(items.Split('|'), BashParser.BraceExpandForTest(word));

    // R6: `$"…"` and `$'…'` are quoting wherever they start, never a literal `$`.
    [Theory]
    [InlineData("a$\"*\"")]
    [InlineData("x$\"y\"")]
    [InlineData("a$'\\x41'b")]
    public void DollarQuote_MidWord_IsQuoting(string word)
    {
        var parts = BashParser.DecomposeWord(word);
        Assert.DoesNotContain(parts, p => p is WordPart.GlobPart);
        Assert.Contains(parts, p => p is WordPart.DoubleQuoted or WordPart.AnsiCQuoted);
        Assert.DoesNotContain(parts, p => p is WordPart.Literal { Value: var v } && v.Contains('$'));
    }
}
