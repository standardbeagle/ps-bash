using System.Text;
using System.Text.RegularExpressions;
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
        // A named array's subscript is one unit (bash 5.2): its `}` is key text.
        new object[] { "${m[}]}", "" },
        new object[] { "${#m['}']}", "}" },
        new object[] { "${m[}]:-d}", "" },
        new object[] { "${m[`echo ]`]}", "" },
        new object[] { "${m[\"}\"]/w/z}", "}" },
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
    // bash brace_gobbler: a `${…}` is ONE nesting level, so the braces and commas of its operand are not expanded.
    [InlineData("{a,${x:-{b,c}}}", "a|${x:-{b,c}}")]
    [InlineData("x{a,${u:-{b}}}y", "xay|x${u:-{b}}y")]
    public void BraceItem_HoldingDollarBrace_IsOneItem(string word, string items)
        => Assert.Equal(items.Split('|'), BashParser.BraceExpandForTest(word));

    /// <summary>A hand-rolled `{`/`}` depth counter: the shapes `c == '{') depth++`, `case '{': n++`,
    /// `== open) nest++` and `!= '}') i++`.</summary>
    private static readonly Regex RawBraceCounter = new(
        @"'\{'\)\s*\{?\s*\w+\+\+|case\s+'\{'\s*:\s*\w+\+\+|==\s*open\)\s*\w+\+\+|!=\s*'\}'\)\s*\w+\+\+", RegexOptions.CultureInvariant);

    /// <summary>file -> why its one counter is not a second `${…}` extent scanner.</summary>
    private static readonly Dictionary<string, string> AllowedBraceCounters = new(StringComparer.Ordinal)
    {
        ["BashParser.Words.cs"] = "BraceClose: bash's brace_gobbler for BRACE expansion, the one brace-depth counter of that grammar",
        ["PsBuild.cs"] = "counts braces of emitted POWERSHELL text, not bash",
    };

    // GUARD: a `${…}` (or `$(…)`) extent comes from BashLexer.DollarBraceClose / ScanDollarParen only. A `{` depth
    // counter elsewhere nests a bare `{` that bash does not (`[[ x =~ ${u:-"}"} ]]`, `16#${h}`, a case-pattern arm).
    [Fact]
    public void Transpiler_CountsNoBraceDepthOutsideTheOneScanner()
    {
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "PsBash.Transpiler"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue;
            string name = Path.GetFileName(file);
            var lines = File.ReadAllLines(file, Encoding.Latin1);
            int count = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string code = lines[i].TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal) || !RawBraceCounter.IsMatch(code)) continue;
                count++;
                if (!AllowedBraceCounters.ContainsKey(name)) hits.Add($"{name}:{i + 1}: {code}");
            }
            if (AllowedBraceCounters.ContainsKey(name))
                Assert.True(count == 1, $"{name} is allow-listed for ONE brace counter but has {count}: route the others through BashLexer.DollarBraceClose (or remove it from the list).");
        }
        Assert.True(hits.Count == 0, "Find a `${…}` extent with BashLexer.DollarBraceClose (a `$(…)` with ScanDollarParen):\n" + string.Join("\n", hits));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CODE_MAP.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root (CODE_MAP.md) not found");
    }


    // A subscript closes at its `]` with quotes, `$'…'`, backticks and expansions taken whole (BashLexer.SubscriptClose,
    // the reader the `]=` writers use): `${m[}]}` keys `}`, `${m[`echo ]`]}` keys the command's output.
    [Theory]
    [InlineData("${m[}]}", "[}]")]
    [InlineData("${m[`echo ]`]}", "[`echo ]`]")]
    [InlineData("${m[$'a]b']}", "[$'a]b']")]
    [InlineData("${!m[$'a]b']}", null)]
    public void Subscript_QuotedClose_IsKeyText(string word, string? suffix)
    {
        var bvs = Assert.IsType<WordPart.BracedVarSub>(Assert.Single(BashParser.DecomposeWord(word)));
        if (suffix is null) Assert.Equal("m[$'a]b']", bvs.Name);
        else { Assert.Equal("m", bvs.Name); Assert.Equal(suffix, bvs.Suffix); }
    }

    // shopt extquote: inside "…" bash translates a `$'…'` of a `${…}` first and expands the translated TEXT.
    [Fact]
    public void DoubleQuoted_AnsiCInBracedVar_IsTranslatedText()
    {
        var dq = Assert.IsType<WordPart.DoubleQuoted>(Assert.Single(BashParser.DecomposeWord("\"${u:-$'\\x24x'}\"")));
        var bvs = Assert.IsType<WordPart.BracedVarSub>(Assert.Single(dq.Parts));
        Assert.Contains(bvs.ArgWord!.Value, p => p is WordPart.SimpleVarSub { Name: "x" });
        // `${m[$'\x5d']}` reads `${m[]]}`: an empty subscript, a bad substitution (bash 5.2).
        var bad = Assert.IsType<WordPart.DoubleQuoted>(Assert.Single(BashParser.DecomposeWord("\"${m[$'\\x5d']}\"")));
        Assert.StartsWith("#BAD:", Assert.IsType<WordPart.BracedVarSub>(bad.Parts[0]).Suffix);
        // Unquoted, the `$'…'` is the key (no translation first).
        Assert.Equal("[$'\\x5d']", Assert.IsType<WordPart.BracedVarSub>(Assert.Single(BashParser.DecomposeWord("${m[$'\\x5d']}"))).Suffix);
    }

    // A here-document's `${…}` operand reads as in double quotes but does NOT translate `$'…'` (bash 5.2: the text
    // `$'a\x41'`); a `'` is text and `\x` keeps its backslash.
    [Fact]
    public void HereDoc_BracedVarOperand_KeepsAnsiCText()
    {
        var bvs = Assert.IsType<WordPart.BracedVarSub>(Assert.Single(BashParser.DecomposeHereDocBracedVar("${u:-$'a\\x41'}")));
        Assert.DoesNotContain(bvs.ArgWord!.Value, p => p is WordPart.AnsiCQuoted);
        string ps = PsEmitter.Transpile("cat <<EOF\n${u:-$'a\\x41'} ${u:-'q'} ${u:-\\x}\nEOF")!;
        Assert.DoesNotContain("aA", ps, StringComparison.Ordinal);
    }

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
