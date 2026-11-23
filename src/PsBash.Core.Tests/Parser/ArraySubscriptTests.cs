using System.Text;
using System.Text.RegularExpressions;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// An array subscript ends at the <c>]</c> that closes it with quoted text taken whole (bash <c>skipsubscript</c>).
/// REGRESSION: <c>declare -A m=(['a]=b']=c)</c> stored key <c>a</c>, value <c>'b]=c</c> (bash: key <c>a]=b</c>, value
/// <c>c</c>): the parser split the element at a regex <c>^\[[^\[\]]+\]=</c> and the emitter at the first <c>]=</c> of the
/// joined text, both blind to quotes. Primitive: <see cref="BashLexer.SubscriptClose"/>, read for an array-literal element
/// through <c>ArraySubscript.TrySplit</c>. End-to-end bash parity: QuotingValueDifferentialTests.
/// </summary>
public class ArraySubscriptTests
{
    [Theory]
    [InlineData("[k]", 2)]
    [InlineData("[a[1]]=x", 5)]
    [InlineData("['a]=b']=c", 7)]
    [InlineData("[\"a]=b\"]=c", 7)]
    [InlineData("[$'a\\']'b]=c", 9)]
    [InlineData("[a\\]b]=c", 5)]
    [InlineData("[$(echo ])]=c", 10)]
    [InlineData("[${v%]}]=c", 7)]
    [InlineData("[`echo ]`]=c", 9)]
    [InlineData("['a]", -1)]
    [InlineData("[a", -1)]
    [InlineData("[}]", 2)]
    public void SubscriptClose_TakesQuotedTextWhole(string text, int close)
        => Assert.Equal(close, BashLexer.SubscriptClose(text, 0));

    [Fact]
    public void Transpile_AssocLiteralKeyWithQuotedCloseAndEquals_IsTheWholeQuotedText()
    {
        string ps = PsEmitter.Transpile("declare -A m=(['a]=b']=c)")!;
        Assert.Contains("FromPairs('a]=b', ", ps, StringComparison.Ordinal);
        Assert.DoesNotContain("b]=c", ps, StringComparison.Ordinal);
    }

    [Fact]
    public void Transpile_AssocLiteralDoubleQuotedKeyWithClose_IsTheWholeQuotedText()
    {
        string ps = PsEmitter.Transpile("declare -A m=([\"x]y\"]=1 [k]=2)")!;
        Assert.Contains("FromPairs('x]y', ", ps, StringComparison.Ordinal);
        Assert.Contains("'k', ", ps, StringComparison.Ordinal);
    }

    [Fact]
    public void Transpile_IndexedLiteral_StillSparse()
    {
        string ps = PsEmitter.Transpile("a=([2]=p [5]=q)")!;
        Assert.Contains("FromEntries('2', ", ps, StringComparison.Ordinal);
        Assert.Contains("'5', ", ps, StringComparison.Ordinal);
    }

    [Fact]
    public void Transpile_ElementAssignmentQuotedKeyWithCloseAndEquals_KeysTheWholeText()
    {
        string ps = PsEmitter.Transpile("declare -A m; m['a]=b']=c")!;
        Assert.Contains("Set-BashElement", ps, StringComparison.Ordinal);
        Assert.Contains("a]=b", ps, StringComparison.Ordinal);
        Assert.DoesNotContain("b']", ps, StringComparison.Ordinal);
    }

    // GUARD: the subscript of a `NAME[...]=` word or a `[KEY]=` element is found by BashLexer.SubscriptClose only. A
    // search for `]=` (a string literal "]=", or a regex `...+\]=`) in transpiler code is a quote-blind second reader.
    // The READ side too (`${m[k]}`, `${#m[k]}`, `${!m[k]}`, an arithmetic `a[i]`): a `]` depth counter or a skip-to-`]`
    // loop of its own (`c == ']' && --depth == 0`, `while (… != ']') pos++`) ends `${m[$'a]b']}` / `${m[`echo ]`]}` early.
    [Fact]
    public void Transpiler_FindsNoSubscriptEndOutsideTheOneScanner()
    {
        var forbidden = new Regex(@"""\]=""|[+*\]]\\\]=", RegexOptions.CultureInvariant);
        var readSide = new Regex(@"'\]'\s*&&\s*--\w+\s*==\s*0|while\s*\(.*!=\s*'\]'\)\s*$|!=\s*'\]'\)\s*\w+\+\+", RegexOptions.CultureInvariant);
        var hits = new List<string>();
        string root = Path.Combine(RepoRoot(), "src", "PsBash.Transpiler");
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Path.GetFileName(file) == "ArraySubscript.cs")
                continue;
            // BashParser.Simple.cs: the scan for a subscript still open across WORDS (`a[5 + 1]=`) stops at a command
            // metacharacter, which the subscript-only scanner cannot know.
            bool checkReadSide = Path.GetFileName(file) != "BashParser.Simple.cs";
            var lines = File.ReadAllLines(file, Encoding.Latin1);
            for (int i = 0; i < lines.Length; i++)
            {
                string code = lines[i].TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal)) continue;
                if (forbidden.IsMatch(code) || (checkReadSide && readSide.IsMatch(code))) hits.Add($"{Path.GetFileName(file)}:{i + 1}: {code}");
            }
        }
        Assert.True(hits.Count == 0, "Find a subscript's end with BashLexer.SubscriptClose / ArraySubscript.TrySplit:\n" + string.Join("\n", hits));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CODE_MAP.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root (CODE_MAP.md) not found");
    }
}
