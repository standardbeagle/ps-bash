using System.Collections.Generic;
using Xunit;
using static PsBash.Cmdlets.BashWordFields;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <see cref="BashWordFields.Expand"/>, the one word-expansion primitive, checked against bash 5.2 (printf '&lt;%s&gt;';
/// x='a b'). The behavior through the shell is <c>Differential.Tests/WordFieldsDifferentialTests</c>; these pin the
/// engine's rules one at a time. Fields print joined by <c>|</c>, a globbing field as <c>text~pattern</c>.
/// </summary>
public class BashWordFieldsTests
{
    private static string Run(string? ifs, params object?[] parts)
    {
        var fields = Expand(parts, ifs, extglob: false);
        var shown = new List<string>(fields.Count);
        foreach (var f in fields) shown.Add(f.Pattern is null ? f.Text : f.Text + "~" + f.Pattern);
        return fields.Count + ":" + string.Join("|", shown);
    }

    // `$x$x`: both results split, the last field of one joins the first of the next.
    [Fact]
    public void TwoSplitValues_JoinAtTheSeam() => Assert.Equal("3:a|ba|b", Run(null, Split, "a b", Split, "a b"));

    // `$e$e` is no field; `""$e` is one empty field (a quoted null anchors it).
    [Fact]
    public void EmptySplitValues_AreNoField() => Assert.Equal("0:", Run(null, Split, "", Split, ""));

    [Fact]
    public void QuotedNull_AnchorsAnEmptyField() => Assert.Equal("1:", Run(null, Quoted, "", Split, "  "));

    // `"$@$@"` with set -- 'a b' '' c.
    [Fact]
    public void QuotedLists_JoinTheirLastAndFirstElements()
        => Assert.Equal("5:a b||ca b||c", Run(null, QuotedList, new[] { "a b", "", "c" }, QuotedList, new[] { "a b", "", "c" }));

    // `"$@"` with no parameters is no field, even beside an empty split value; `k"$@"` is k.
    [Fact]
    public void EmptyQuotedList_IsNoField() => Assert.Equal("0:", Run(null, QuotedList, new string[0], Split, ""));

    [Fact]
    public void EmptyQuotedList_BesideText_IsTheText() => Assert.Equal("1:k", Run(null, Pattern, "k", QuotedList, new string[0]));

    // `"$@"$x` with set -- 1 2.
    [Fact]
    public void QuotedListThenSplitValue() => Assert.Equal("3:1|2a|b", Run(null, QuotedList, new[] { "1", "2" }, Split, "a b"));

    // `$x*` is a and b*: the split comes first, then each field globs on its own.
    [Fact]
    public void SplitThenPattern_GlobsOnlyTheFieldWithThePattern() => Assert.Equal("2:a|b*~b*", Run(null, Split, "a b", Pattern, "*"));

    // `'*'$e` is the literal * (quoted, no pattern); `"*"$g` with g='*' is the pattern \** (the quoted star escaped).
    [Fact]
    public void QuotedGlobCharacter_IsNoPattern() => Assert.Equal("1:*", Run(null, Quoted, "*", Split, ""));

    [Fact]
    public void QuotedGlobCharacter_IsEscapedInAPattern() => Assert.Equal("1:**~\\**", Run(null, Quoted, "*", Split, "*"));

    // A class's quoted member stays escaped in the pattern and unescaped in the text.
    [Fact]
    public void PatternEscape_IsKeptInThePatternOnly() => Assert.Equal("1:[a]b]~[a\\]b]", Run(null, Pattern, "[a\\]b]"));

    // Every escape in pattern text is removed from the text, whatever it escapes (bash 5.2, no such file):
    // `A["5"]=$x` (x=1) prints A[5]=1, `a[\"key\"]=$x` a["key"]=1, `[\"]$x` ["]1. REGRESSION: only an escaped
    // * ? [ ] \ | ( ) lost its backslash, so these printed A[\5]=1, a[\"key\"]=1 and [\"]1 (Spec dparen.spec.sh).
    [Theory]
    [InlineData("A[\\5]=", "1:A[5]=1~A[\\5]=1")]
    [InlineData("a[\\\"key\\\"]=", "1:a[\"key\"]=1~a[\\\"key\\\"]=1")]
    [InlineData("[\\\"]", "1:[\"]1~[\\\"]1")]
    [InlineData("[\\']", "1:[']1~[\\']1")]
    [InlineData("[\\*]", "1:[*]1~[\\*]1")]
    [InlineData("x[\\a\\-\\z]", "1:x[a-z]1~x[\\a\\-\\z]1")]
    public void PatternEscapeOfAnyCharacter_IsRemovedFromTheText(string pattern, string expected)
        => Assert.Equal(expected, Run(null, Pattern, pattern, Split, "1"));

    // IFS=: v='a::b': a non-whitespace delimiter keeps the empty field; a trailing one adds none.
    [Theory]
    [InlineData("a::b", "3:a||b")]
    [InlineData("a:", "1:a")]
    [InlineData(":a", "2:|a")]
    [InlineData(" : a ", "2:|a")]
    public void NonWhitespaceIfs_KeepsInnerEmptyFields(string value, string expected) => Assert.Equal(expected, Run(": ", Split, value));

    // IFS=1: `$((11+0))` is two empty fields, `$((10+0))` is one empty field and 0.
    [Fact]
    public void DigitIfs_SplitsNumbers()
    {
        Assert.Equal("2:|", Run("1", Split, "11"));
        Assert.Equal("2:|0", Run("1", Split, "10"));
    }

    // Unquoted text never splits: IFS=: `a:b$x` with x=c is a:bc.
    [Fact]
    public void PatternText_NeverDelimits() => Assert.Equal("1:a:bc", Run(":", Pattern, "a:b", Split, "c"));

    // `$@` unquoted: the values joined with IFS[0], then split; with an empty IFS each non-empty value is a field.
    [Fact]
    public void SplitList_JoinsWithTheFirstIfsCharacter() => Assert.Equal("3:a b||c", Run("-", SplitList, new[] { "a b", "", "c" }));

    [Fact]
    public void SplitList_EmptyIfs_DropsEmptyValues() => Assert.Equal("2:a b|c", Run("", SplitList, new[] { "a b", "", "c" }));

    // An empty IFS splits nothing: IFS= `$x$x` is one field.
    [Fact]
    public void EmptyIfs_SplitsNothing() => Assert.Equal("1:a ba b", Run("", Split, "a b", Split, "a b"));

    // `{1,2}$x`: a brace expansion's words are separated by a break.
    [Fact]
    public void Break_EndsAField()
        => Assert.Equal("4:1a|b|2a|b", Run(null, Pattern, "1", Split, "a b", Break, null, Pattern, "2", Split, "a b"));

    // A group (the chosen arm of `${v:-word}`) is spliced in place; null is an empty group.
    [Fact]
    public void Group_IsSplicedInPlace()
    {
        Assert.Equal("2:a|bx", Run(null, new object?[] { Split, "a b" }, Pattern, "x"));
        Assert.Equal("1:x", Run(null, null, Pattern, "x"));
    }

    // x=' a': `${x}{1,2}` is a1 a2. The word after a break starts like a word, so its leading IFS whitespace makes no
    // field (it gave a1, "", a2).
    [Fact]
    public void Break_SkipsTheNextWordsLeadingIfsWhitespace()
        => Assert.Equal("2:a1|a2", Run(null, Split, " a", Pattern, "1", Break, null, Split, " a", Pattern, "2"));

    // touch ab; d=.: `$d/"+(a)"*` is ./+(a)* in bash, extglob on or off: quoted extglob operators are escaped like the
    // pattern compiler escapes literal text (BashPattern.EscapeLiteral), so the quoted `(a)` is no group.
    [Fact]
    public void QuotedExtglobOperators_AreEscapedInThePattern()
    {
        var fields = Expand(new object?[] { Split, ".", Pattern, "/", Quoted, "+(a)|", Pattern, "*" }, null, extglob: true);
        Assert.Single(fields);
        Assert.Equal("./+(a)|*", fields[0].Text);
        Assert.Equal("./+\\(a\\)\\|*", fields[0].Pattern);
    }

    // An unquoted extglob after an expansion stays active: `$d/+(a)*`.
    [Fact]
    public void UnquotedExtglob_StaysActive()
    {
        var fields = Expand(new object?[] { Split, ".", Pattern, "/+(a)*" }, null, extglob: true);
        Assert.Equal("./+(a)*", fields[0].Pattern);
    }

    // `"C:\tmp\[x]"$n*` on Windows: the drive path's backslashes are separators, mapped BEFORE the quoted `[` `]` are
    // escaped (mapping after made the escape `\[` a separator, `/[`, and the class active). Elsewhere a backslash is text.
    [Fact]
    public void QuotedDrivePath_MapsSeparatorsBeforeEscaping()
        => Assert.Equal(OperatingSystem.IsWindows() ? "1:C:\\tmp\\[x]*~C:/tmp/\\[x\\]*" : "1:C:\\tmp\\[x]*~C:\\\\tmp\\\\\\[x\\]*",
            Run(null, Quoted, "C:\\tmp\\[x]", Split, "", Pattern, "*"));

    // IFS of one character outside the BMP: it delimits as a whole, and a different character sharing its high surrogate
    // is never cut in half.
    [Theory]
    [InlineData("a\uD83D\uDE00b", "2:a|b")]
    [InlineData("a\uD83D\uDE01b", "1:a\uD83D\uDE01b")]
    [InlineData("\uD83D\uDE00a\uD83D\uDE00", "2:|a")]
    public void NonBmpIfs_SplitsOnWholeCodePoints(string value, string expected)
        => Assert.Equal(expected, Run("\uD83D\uDE00", Split, value));

    // Unquoted `$@` with that IFS joins with the whole character, then splits on it.
    [Fact]
    public void NonBmpIfs_SplitListJoinsWithTheWholeCharacter()
        => Assert.Equal("2:a|b", Run("\uD83D\uDE00", SplitList, new[] { "a", "b" }));

    [Theory]
    [InlineData("\uD83D\uDE00-", "\uD83D\uDE00")]
    [InlineData("-x", "-")]
    [InlineData(" \t\n", " ")]
    public void FirstIfsCharacter_KeepsASurrogatePairWhole(string ifs, string expected) => Assert.Equal(expected, FirstIfsCharacter(ifs));

    // `set --; e=; set -- "$@$e"` is no word (bash 5.2); `"$@$e"` with e=q is q; literal text (`"x$@"`) anchors.
    [Fact]
    public void QuotedValue_BesideAnEmptyList_IsNoField()
    {
        Assert.Equal("0:", Run(null, QuotedList, new string[0], QuotedValue, ""));
        Assert.Equal("1:q", Run(null, QuotedList, new string[0], QuotedValue, "q"));
        Assert.Equal("1:", Run(null, QuotedList, new string[0], Quoted, ""));
        Assert.Equal("2:a|b", Run(null, QuotedList, new[] { "a", "b" }, QuotedValue, ""));
    }

    [Fact]
    public void UnknownKind_Throws() => Assert.Throws<System.ArgumentException>(() => Expand(new object?[] { 9, "x" }, null, false));
}
