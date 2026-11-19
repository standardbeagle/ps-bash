using System.Text.RegularExpressions;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// A number-shaped bash word (<c>1e5</c>, <c>0x10</c>, <c>08</c>, <c>-0</c>, <c>1kb</c>, <c>.5</c>) is emitted quoted, so
/// PowerShell keeps the text instead of converting it (spec emitter-strategy.md §Number-shaped words). REGRESSION: a
/// function argument <c>f 1e5</c> reached the function as 100000, and a brace list <c>{-0,1}</c> became the range
/// <c>@(0..1)</c>. Oracle (bash 5.2): <c>f() { echo "$@"; }; f 1e5 08</c> prints <c>1e5 08</c>; end-to-end parity is
/// NumberShapedWordDifferentialTests, the predicate's guard PsNumberLiteralGuardTests.
/// </summary>
public class NumberShapedWordEmitterTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    // The word appears only quoted ('…' or "…"): never as a bare token PowerShell could read as a number.
    private static void AssertOnlyQuoted(string ps, string word)
    {
        Assert.Matches(new Regex("(['\"])" + Regex.Escape(word) + @"\1"), ps);
        Assert.DoesNotMatch(new Regex(@"(?<![\w'"".+-])" + Regex.Escape(word) + @"(?![\w'"".])"), ps);
    }

    [Theory]
    [InlineData("1e5")]
    [InlineData("33441e77")]
    [InlineData("0x10")]
    [InlineData("08")]
    [InlineData("1kb")]
    [InlineData("10d")]
    public void Transpile_FunctionArgument_NumberShapedWordIsQuoted(string word)
        => AssertOnlyQuoted(T("f() { echo \"$@\"; }; f " + word), word);

    [Theory]
    [InlineData("1e5")]
    [InlineData("0x10")]
    [InlineData("08")]
    [InlineData("-0")]
    [InlineData(".5")]
    public void Transpile_ForList_NumberShapedWordIsQuoted(string word)
        => AssertOnlyQuoted(T("for f in " + word + " 2; do echo \"$f\"; done"), word);

    [Fact]
    public void Transpile_ArrayLiteral_NumberShapedWordIsQuoted()
        => AssertOnlyQuoted(T("a=(1e5 08 3); echo \"${a[@]}\""), "1e5");

    [Fact]
    public void Transpile_BraceListWithNegativeZero_IsNotARange()
    {
        string ps = T("echo {-0,1}");
        Assert.DoesNotContain("0..1", ps);
        Assert.Contains("'-0'", ps);
    }

    [Fact]
    public void Transpile_BraceListOfCanonicalIntegers_KeepsTheRange()
        => Assert.Contains("1..3", T("echo {1,2,3}"));

    [Fact]
    public void Transpile_CanonicalInteger_StaysBare()
        => Assert.Matches(new Regex(@"\b12\b(?!')"), T("f() { echo \"$@\"; }; f 12"));
}
