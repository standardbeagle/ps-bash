using System.Management.Automation.Language;
using System.Text.RegularExpressions;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Host.Tests.Transpiler;

/// <summary>
/// A bash word is a string; PowerShell's tokenizer reads an unquoted <c>1e5</c>, <c>0x10</c>, <c>1kb</c>, <c>10d</c>,
/// <c>08</c>, <c>.5</c> as a NUMBER. REGRESSION: <c>for f in 33441e77</c> printed <c>3.3441E+81</c> and <c>f 1e5</c> handed
/// the function 100000. The decision "would PowerShell read this text as a number" is ONE predicate,
/// <see cref="PsNumberLiteral"/>, used through <see cref="PsBuild.QuoteArgumentNumber"/> (an argument) and
/// <see cref="PsBuild.IsBareIntegerValue"/> (a list item that may stay bare). Oracle (qa-rubric Directive 1): PowerShell's
/// own tokenizer, over a fuzzed corpus, so a grammar change in a new PowerShell fails here instead of converting a word.
/// End-to-end bash parity: NumberShapedWordDifferentialTests.
/// </summary>
public class PsNumberLiteralGuardTests
{
    private static readonly string[] Alphabet =
    {
        "0", "1", "8", "9", ".", "e", "E", "x", "X", "b", "B", "d", "l", "u", "s", "y", "n", "k", "m", "g", "t", "p",
        "+", "-", "_", "a", "f", "–",
    };

    private static readonly string[] Named =
    {
        "33441e77", "1e5", "0x10", "1kb", "10d", "5l", "7u", "3ul", "2y", "1s", "1n", "+5", "-5", "1.", ".5", "1_000",
        "0b101", "1e5x", "0x", "1.2.3", "08", "1kbx", "1mb", "1E5", "0X1F", "1.5e3", "-0", "00", "0b", "1e+5", "1.5n",
        "0x1d", "1..3", "1.e5", ".5e3", "1.5e+3kb", "1e5dkb", "1dkb", "1ulkb", "0x1ukb", "0b1kb", "0x1.5", "1e–5",
        "1e—5", "1e―5", "123456789012345678", "1234567890123456789", "-123456789012345678", "0001",
    };

    private static IEnumerable<string> Corpus()
    {
        foreach (var w in Named) yield return w;
        foreach (var a in Alphabet)
        {
            yield return a;
            foreach (var b in Alphabet) yield return a + b;
        }
        var rnd = new Random(20261118);
        for (int k = 0; k < 20000; k++)
        {
            int len = rnd.Next(2, 9);
            var chars = new string[len];
            // Digit-heavy: most of what matters starts with a digit or a dot.
            chars[0] = rnd.Next(3) == 0 ? Alphabet[rnd.Next(Alphabet.Length)] : Alphabet[rnd.Next(5)];
            for (int i = 1; i < len; i++) chars[i] = Alphabet[rnd.Next(Alphabet.Length)];
            yield return string.Concat(chars);
        }
    }

    private static bool TokenizerSaysArgumentNumber(string word)
    {
        Parser.ParseInput("Write-Output " + word, out Token[] tokens, out _);
        // Write-Output, the word, EndOfInput.
        return tokens.Length == 3 && tokens[1].Kind == TokenKind.Number;
    }

    private static bool EndsWithTypeSuffixOrMultiplier(string word)
        => word.Length > 0 && (char.ToLowerInvariant(word[^1]) is 'y' or 's' or 'l' or 'n' or 'd' or 'u' or 'b');

    [Fact]
    public void IsArgumentNumber_AgreesWithThePowerShellTokenizer()
    {
        var mismatches = new List<string>();
        int checkedCount = 0;
        foreach (var word in Corpus().Distinct())
        {
            checkedCount++;
            bool ps = TokenizerSaysArgumentNumber(word);
            bool ours = PsNumberLiteral.IsArgumentNumber(word);
            // One allowed difference, in the safe direction: a suffixed literal whose value does not fit its type
            // (`9e91d`, `9E11s`) is a generic token to PowerShell, and the predicate quotes it anyway (same text).
            // A word PowerShell reads as a number must never be left bare.
            if (ps != ours && !(ours && !ps && EndsWithTypeSuffixOrMultiplier(word))) mismatches.Add($"'{word}' tokenizer={ps}");
            if (mismatches.Count >= 40) break;
        }
        Assert.True(checkedCount > 10000, $"corpus too small: {checkedCount}");
        Assert.True(mismatches.Count == 0, "PsNumberLiteral.IsArgumentNumber disagrees with PowerShell:\n" + string.Join("\n", mismatches));
    }

    // A canonical integer may stay bare anywhere: as an argument AND inside @(...) / a foreach list it denotes the
    // same text. Everything else the predicate rejects.
    [Fact]
    public void IsCanonicalInteger_KeepsItsTextInArgumentAndExpressionMode()
    {
        foreach (var word in Corpus().Distinct().Take(6000).Concat(Named))
        {
            if (!PsNumberLiteral.IsCanonicalInteger(word)) continue;
            var expr = Parser.ParseInput("@(" + word + ")", out _, out var errors);
            Assert.Empty(errors);
            var array = (ArrayExpressionAst)((CommandExpressionAst)((PipelineAst)expr.EndBlock.Statements[0]).PipelineElements[0]).Expression;
            var value = ((ConstantExpressionAst)((CommandExpressionAst)((PipelineAst)array.SubExpression.Statements[0]).PipelineElements[0]).Expression).Value;
            Assert.Equal(word, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
        }
        foreach (var w in new[] { "08", "-0", "+5", "00", "0001", "1234567890123456789", "1e5", "0x10", "", "-" })
            Assert.False(PsNumberLiteral.IsCanonicalInteger(w), w);
    }

    [Theory]
    [InlineData("1e5", "'1e5'")]
    [InlineData("33441e77", "'33441e77'")]
    [InlineData("0x10", "'0x10'")]
    [InlineData("1kb", "'1kb'")]
    [InlineData("08", "'08'")]
    [InlineData(".5", "'.5'")]
    [InlineData("1.", "'1.'")]
    [InlineData("10d", "'10d'")]
    [InlineData("12", "12")]
    [InlineData("-5", "-5")]
    [InlineData("1e5x", "1e5x")]
    [InlineData("1_000", "1_000")]
    public void QuoteArgumentNumber_QuotesOnlyWhatPowerShellWouldConvert(string emitted, string expected)
        => Assert.Equal(expected, PsBuild.QuoteArgumentNumber(emitted));

    // The emitter decides "bare number or not" ONLY through PsBuild/PsNumberLiteral: no TryParse of an emitted word
    // and no private integer-shape helper (the old IsPlainInteger / double.TryParse in for-lists let 1e5 through).
    [Fact]
    public void Emitter_HasNoOtherBareNumberDecision()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PsBash.Transpiler", "Parser", "PsEmitter.cs"),
            System.Text.Encoding.Latin1);
        Assert.DoesNotMatch(new Regex(@"double\.TryParse"), src);
        Assert.DoesNotMatch(new Regex(@"\bIsPlainInteger\b"), src);
        Assert.DoesNotMatch(new Regex(@"long\.TryParse\(\s*(emitted|item|word|text)\b"), src);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CODE_MAP.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
