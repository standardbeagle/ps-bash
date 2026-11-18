using System.Management.Automation.Language;
using System.Text.RegularExpressions;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Host.Tests.Transpiler;

/// <summary>
/// PowerShell's tokenizer ends a string at the Unicode quote marks too: U+2018-U+201B close a single-quoted string,
/// U+201C-U+201E a double-quoted one, and a token starting with U+2013-U+2015 is a -parameter. To bash they are
/// ordinary characters. REGRESSION: every escape doubled or backticked only the ASCII mark, so <c>echo it’s</c> (a
/// pasted apostrophe) was a parse error that took the whole script down, <c>echo 'a‘’b'</c> silently printed
/// <c>a’b</c>, and <c>echo –n x</c> ran <c>echo -n</c>. One primitive per context now covers the whole set
/// (<see cref="PsBuild.SingleQuote"/>, <see cref="PsBuild.EscapeForDoubleQuote"/>,
/// <see cref="PsBuild.EscapeBareword"/>); end-to-end values: PsQuotingDifferentialTests.
/// Oracle note (qa-rubric Directive 1): the oracle is PowerShell's own parser (the emitted text must denote exactly
/// the bash value); bash parity of the printed bytes is the differential test.
/// </summary>
public class PsQuoteMarkGuardTests
{
    private const string Marks = "\u2018\u2019\u201A\u201B\u201C\u201D\u201E";
    private const string Dashes = "\u2013\u2014\u2015";

    public static TheoryData<string> Values()
    {
        var data = new TheoryData<string>
        {
            "", "plain", "'", "\"", "''", "\"\"", "`", "$x", "a'b\"c`d$e",
            Marks, "a" + Marks + "b", "\u2018\u2019", "\u201C\u201D", "\u2019\u2019\u2019", "\u201E`\u201C",
            "'\u2019'\u2018", "\"\u201D\"", "x\u201B", "\u2013n", Dashes + Marks,
        };
        foreach (char m in Marks) data.Add("it" + m + "s");
        return data;
    }

    private static Ast ParseOne(string ps)
    {
        var ast = Parser.ParseInput(ps, out _, out var errors);
        Assert.True(errors.Length == 0, $"emitted `{ps}` does not parse: {string.Join("; ", errors.Select(e => e.Message))}");
        return ast;
    }

    private static ExpressionAst OnlyExpression(string ps)
    {
        var ast = (ScriptBlockAst)ParseOne(ps);
        var pipe = Assert.IsType<PipelineAst>(Assert.Single(ast.EndBlock.Statements));
        return Assert.IsAssignableFrom<CommandExpressionAst>(Assert.Single(pipe.PipelineElements)).Expression;
    }

    [Theory]
    [MemberData(nameof(Values))]
    public void SingleQuote_DenotesExactlyTheValue(string value)
    {
        var e = Assert.IsType<StringConstantExpressionAst>(OnlyExpression(PsBuild.SingleQuote(value)));
        Assert.Equal(value, e.Value);
    }

    [Theory]
    [MemberData(nameof(Values))]
    public void DoubleQuote_DenotesExactlyTheValue_AndExpandsNothing(string value)
    {
        // A "..." with no live $ expansion parses as a constant; an ExpandableString here means a $ leaked.
        var e = Assert.IsType<StringConstantExpressionAst>(OnlyExpression(PsBuild.DoubleQuote(value)));
        Assert.Equal(value, e.Value);
    }

    [Theory]
    [InlineData("a\u2019b")]
    [InlineData("\u2013n")]
    [InlineData("\u2014la")]
    [InlineData("\u2015")]
    [InlineData("x\u201C\u201Dy")]
    [InlineData("{a}")]
    [InlineData("\u2013\u2018q")]
    public void EscapeBareword_IsOneLiteralArgument(string value)
    {
        var ast = (ScriptBlockAst)ParseOne("Write-Output " + PsBuild.EscapeBareword(value, atWordStart: true));
        var cmd = Assert.IsType<CommandAst>(Assert.IsType<PipelineAst>(Assert.Single(ast.EndBlock.Statements)).PipelineElements[0]);
        var arg = Assert.IsType<StringConstantExpressionAst>(Assert.Single(cmd.CommandElements.Skip(1)));
        Assert.Equal(value, arg.Value);
    }

    // Every word context the emitter has a separate path for. X is replaced by text holding the marks.
    private static readonly string[] Contexts =
    [
        "echo X", "echo 'X'", "echo \"X\"", "echo \"X$v\"", "echo pre\"X\"'X'", "x=X", "x='X'", "x=\"X$v\"",
        "for c in X 'X' \"X\" a\\ X; do echo \"$c\"; done", "a=(X 'X' \"X\")", "echo \"${u:-X}\" ${u:-X} ${u:+'X'}",
        "y=${u:-X}", "declare -A m=([X]=1 ['X']=2); echo \"${m[X]}\" ${m['X']}", "case X in X|'X') echo m;; esac",
        "[[ X == X* ]] && [[ \"X\" =~ X ]]", "cat <<EOF\nX\n$v X\nEOF", "cat <<'EOF'\nX\nEOF", "echo X | sed s/X/Y/",
        "f() { echo \"X$1\"; }; f X", "eval \"echo X\"; eval 'echo X'", "trap 'echo X' EXIT", "printf '%s' X \"X\"",
        "echo $(echo X) \"$(echo 'X')\" `echo X`", "test X = 'X'", "x=X; echo ${x/X/Y} ${x#X} \"${x%'X'}\"",
        "echo X > o; cat < o", "export E=X; local_x=X", "echo {X,b} a{1..2}X",
    ];

    public static TheoryData<string> Corpus()
    {
        var data = new TheoryData<string>();
        foreach (var ctx in Contexts)
        {
            data.Add(ctx.Replace("X", "a" + Marks + "b"));
            data.Add(ctx.Replace("X", Marks));
            data.Add(ctx.Replace("X", "\u2013n"));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void EveryWordContext_WithQuoteOrDashMarks_EmitsParseablePowerShell(string bash)
        => ParseabilityContract.Assert(bash, "quote marks");

    [Theory]
    [InlineData("echo \u2013n x")]
    [InlineData("ls \u2014la")]
    [InlineData("for w in \u2013n; do :; done")]
    [InlineData("echo pre \u2015")]
    public void LeadingDashMark_IsNeverAParameter(string bash)
    {
        var ps = PsBash.Core.Transpiler.BashTranspiler.Transpile(bash)!;
        var ast = ParseOne(ps);
        Assert.Empty(ast.FindAll(a => a is CommandParameterAst, searchNestedScriptBlocks: true));
    }

    // ── Source guard: PS quoting only through PsBuild ──────────────────────────────────────────────
    // `x.Replace("'", "''")` / `x.Replace("\"", "`\"")` re-derive PowerShell quoting for the ASCII mark only.
    private static readonly Regex AdHocPsQuoting = new(
        @"\.Replace\(\s*""'""\s*,\s*""''""\s*\)|\.Replace\(\s*""\\""""\s*,\s*""`\\""""\s*\)",
        RegexOptions.Compiled);

    private static readonly string[] ProductDirs =
        ["PsBash.Cmdlets", "PsBash.Transpiler", "PsBash.Host", "PsBash.Core", "PsBash.Shell"];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repo root (the dir with CLAUDE.md) not found.");
    }

    [Fact]
    public void ProductCode_QuotesForPowerShellOnlyThroughPsBuild()
    {
        var offenders = new List<string>();
        foreach (var project in ProductDirs)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", project), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                    || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                    || Path.GetFileName(file) == "PsBuild.cs") continue;
                int n = 0;
                foreach (var line in File.ReadLines(file))
                {
                    n++;
                    if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                    if (AdHocPsQuoting.IsMatch(line))
                        offenders.Add($"{Path.GetRelativePath(RepoRoot(), file)}:{n}: {line.Trim()}");
                }
            }
        }
        Assert.True(offenders.Count == 0,
            "PowerShell string quoting must go through PsBuild.SingleQuote / EscapeForSingleQuote / EscapeForDoubleQuote / "
            + "EscapeDoubleQuoteMarks (they also escape U+2018-U+201E):\n" + string.Join("\n", offenders));
    }
}
