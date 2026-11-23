using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The one GNU regex translator (<see cref="GnuRegex"/>) per tool flavor, and sed's character escapes
/// (<see cref="SedEscape"/>). Every expectation was measured on GNU sed 4.9 / grep 3.11 / findutils 4.9 / coreutils
/// expr; the end-to-end behaviour is pinned against bash by <c>GnuRegexDifferentialTests</c> and
/// <c>SedDifferentialTests</c>. These unit rows exist to name the translator's own contract (qa-rubric Directive 1).
/// </summary>
public class GnuRegexTests
{
    private static GnuRegexSyntax Syntax(string name) => name switch
    {
        "sed" => GnuRegexSyntax.SedBasic,
        "sed-E" => GnuRegexSyntax.SedExtended,
        "grep" => GnuRegexSyntax.GrepBasic,
        "grep-E" => GnuRegexSyntax.GrepExtended,
        "expr" => GnuRegexSyntax.ExprBasic,
        "find" => GnuRegexSyntax.FindEmacs,
        "find-basic" => GnuRegexSyntax.FindBasic,
        "find-E" => GnuRegexSyntax.FindExtended,
        _ => throw new ArgumentException(name),
    };

    private static bool Matches(string pattern, string flavor, string input, bool newlineSensitive = false)
    {
        Assert.True(GnuRegex.TryTranslate(pattern, Syntax(flavor), out var net, out var error, newlineSensitive), error);
        return new Regex(net).IsMatch(input);
    }

    [Theory]
    // BRE operators are the escaped forms; the bare ones are text.
    [InlineData(@"foo\|bar", "grep", "bar", true)]
    [InlineData(@"\(fo\)\1", "grep", "fofo", true)]
    [InlineData(@"\(fo\)\1", "grep", "fofx", false)]
    [InlineData(@"o\{2,3\}", "grep", "foo", true)]
    [InlineData(@"o\{2,3\}", "grep", "fo", false)]
    [InlineData(@"a\+b\?", "grep", "aa", true)]
    [InlineData("a|b(c){2}+?", "grep", "a|b(c){2}+?", true)]
    [InlineData("a|b(c){2}+?", "grep", "ab", false)]
    [InlineData(@"a\\|b", "grep", @"a\|b", true)]
    // Anchors are positional in a BRE; a leading `*` is text.
    [InlineData("a$(b", "grep", "a$(b", true)]
    [InlineData("a$b", "grep", "a$b", true)]
    [InlineData("^a^b", "grep", "a^b", true)]
    [InlineData(@"\(^a$\)\|^b", "grep", "b", true)]
    [InlineData(@"\(^a$\)\|^b", "grep", "xa", false)]
    [InlineData("*a", "grep", "*a", true)]
    [InlineData("*a", "grep", "a", false)]
    [InlineData("^*a", "sed", "*a", true)]
    [InlineData(@"\(*a\)", "sed", "*a", true)]
    [InlineData("[^$]x[a^]", "grep", "bx^", true)]
    [InlineData("x[[:punct:]]", "grep", "x!", true)]
    [InlineData("[]a]", "grep", "]", true)]
    [InlineData(@"[\n]", "grep", @"\", true)]   // a backslash in a bracket is a member (grep)
    // grep / expr apply a stacked repetition and read a leading interval as text; `\d` is `d`.
    [InlineData("a**", "grep", "aaa", true)]
    [InlineData(@"\{1\}a", "grep", "{1}a", true)]
    [InlineData(@"x\{1\}\{2\}", "grep", "xx", true)]
    [InlineData(@"\d", "grep", "d", true)]
    [InlineData(@"\d", "grep", "1", false)]
    [InlineData("a**", "expr", "aa", true)]
    // grep -E: a malformed interval and an unmatched `)` are text.
    [InlineData("a{1,2", "grep-E", "a{1,2", true)]
    [InlineData("a{1", "grep-E", "a{1", true)]
    [InlineData("a)", "grep-E", "a)", true)]
    [InlineData("a{2}", "grep-E", "aa", true)]
    // sed / find accept a repetition stacked on a repetition in an ERE.
    [InlineData("a**", "sed-E", "aa", true)]
    [InlineData("a+*", "sed-E", "aa", true)]
    [InlineData("(a)*{2}", "sed-E", "aa", true)]
    [InlineData("a{1}{2}", "sed-E", "aa", true)]
    [InlineData("a)", "find-E", "a)", true)]
    // emacs (find's default): + ? are operators, \{ is text.
    [InlineData("a+", "find", "aa", true)]
    [InlineData(@"a\{", "find", "a{", true)]
    [InlineData("a**", "find", "aa", true)]
    public void Translate_MatchesLikeGnu(string pattern, string flavor, string input, bool expected)
        => Assert.Equal(expected, Matches(pattern, flavor, input));

    [Theory]
    [InlineData(@"a\{1", "sed", @"Unmatched \{")]
    [InlineData(@"a\{1", "grep", @"Unmatched \{")]
    [InlineData(@"a\{1,x\}", "sed", @"Invalid content of \{\}")]
    [InlineData(@"a\{2,1\}", "expr", @"Invalid content of \{\}")]
    [InlineData(@"\{1\}", "sed", "Invalid preceding regular expression")]
    [InlineData("a**", "sed", "Invalid preceding regular expression")]
    [InlineData(@"a*\{2\}", "sed", "Invalid preceding regular expression")]
    [InlineData(@"x\{1\}\{2\}", "find-basic", "Invalid preceding regular expression")]
    [InlineData("[[:foo:]]", "sed", "Invalid character class name")]
    [InlineData("[[:foo:]]", "grep", "Invalid character class name")]
    [InlineData("[[.ab.]]", "sed", "Invalid collation character")]
    [InlineData("[b-a]", "grep", "Invalid range end")]
    [InlineData(@"\(a\)\2", "sed", "Invalid back reference")]
    [InlineData(@"\(a\)\2", "expr", "Invalid back reference")]
    [InlineData(@"a\", "grep", "Trailing backslash")]
    [InlineData("[a", "grep", "Unmatched [, [^, [:, [., or [=")]
    [InlineData(@"\(a", "sed", @"Unmatched ( or \(")]
    [InlineData("[:space:]", "sed", GnuRegex.ColonClassMessage)]
    [InlineData("[:space:]", "grep", GnuRegex.ColonClassMessage)]
    [InlineData("*a", "sed-E", "Invalid preceding regular expression")]
    [InlineData("a|+b", "sed-E", "Invalid preceding regular expression")]
    [InlineData("{1}a", "find-E", "Invalid preceding regular expression")]
    [InlineData("a{", "sed-E", @"Unmatched \{")]
    [InlineData("a{1,2", "find-E", @"Unmatched \{")]
    [InlineData("a{x}", "sed-E", @"Invalid content of \{\}")]
    [InlineData("a{2,1}", "grep-E", @"Invalid content of \{\}")]
    [InlineData("a)", "sed-E", @"Unmatched ) or \)")]
    public void Translate_InvalidPattern_IsGnusMessage(string pattern, string flavor, string message)
    {
        Assert.False(GnuRegex.TryTranslate(pattern, Syntax(flavor), out _, out var error));
        Assert.Equal(message, error);
    }

    [Theory]
    [InlineData("[[.a.]]", "a")]
    [InlineData("[[=a=]]", "a")]
    [InlineData(@"a*\?", "a")]
    public void Translate_SedBasic_AcceptsWhatGnuAccepts(string pattern, string input)
        => Assert.True(Matches(pattern, "sed", input));

    [Theory]
    [InlineData("*a", "*")]
    [InlineData("+a", "+")]
    [InlineData("a|?b", "?")]
    [InlineData("{1}a", "{...}")]
    public void Translate_GrepExtended_LeadingRepetition_IsDroppedWithAWarning(string pattern, string what)
    {
        var warnings = new List<string>();
        Assert.True(GnuRegex.TryTranslate(pattern, GnuRegexSyntax.GrepExtended, out var net, out _, warnings: warnings));
        Assert.Equal(new[] { what + " at start of expression" }, warnings);
        Assert.Matches(new Regex(net), "a");
    }

    // ---- sed's pattern space: one buffer, possibly several lines -----------------------------------------

    [Theory]
    [InlineData("a.b", "a\nb", false, true)]       // `.` matches the newline
    [InlineData("a[^x]b", "a\nb", false, true)]
    [InlineData("a$", "a\nb", false, false)]       // `$` is the end of the buffer only
    [InlineData("a$", "a\n", false, false)]
    [InlineData("^b", "a\nb", false, false)]
    [InlineData("a.b", "a\nb", true, false)]       // M: line-wise
    [InlineData("a[^x]b", "a\nb", true, false)]
    [InlineData("a$", "a\nb", true, true)]
    [InlineData("^b", "a\nb", true, true)]
    [InlineData(@"a\nb", "a\nb", false, true)]
    [InlineData(@"a[\n]b", "a\nb", false, true)]
    [InlineData(@"\`a", "ba\na", true, false)]     // \` and \' stay the buffer's ends under M
    [InlineData(@"b\'", "b\na", true, false)]
    public void Translate_SedBuffer(string pattern, string input, bool lineWise, bool expected)
        => Assert.Equal(expected, Matches(pattern, "sed", input, lineWise));

    // ---- sed's character escapes ----------------------------------------------------------------------------

    [Theory]
    [InlineData(@"\x2e", ".")]
    [InlineData(@"a\tb", "a\tb")]
    [InlineData(@"\d065\o102\x43", "ABC")]
    [InlineData(@"\cA", "\u0001")]
    [InlineData(@"\q\.\1", @"\q\.\1")]   // not a character escape: kept for the translator
    [InlineData(@"\\x41", @"\\x41")]     // an escaped backslash
    [InlineData(@"\x", @"\x")]           // no hex digit
    [InlineData(@"\n", @"\n")]           // left to the translator
    public void SedEscape_NormalizeRegex(string pattern, string expected)
        => Assert.Equal(expected, SedEscape.NormalizeRegex(pattern));

    [Theory]
    [InlineData(@"\x41\t", "A\t")]
    [InlineData(@"\q12", "q12")]
    [InlineData(@"a\bc", "abc")]
    [InlineData(@"a\\b", @"a\b")]
    [InlineData(@"a\nb", "a\nb")]
    public void SedEscape_TransliterationText(string field, string expected)
        => Assert.Equal(expected, SedEscape.TransliterationText(field));

    [Theory]
    [InlineData(@"\x26", "&")]          // a decoded character is literal, not the whole match
    [InlineData("&", "$0")]
    [InlineData("$1", "$$1")]
    [InlineData(@"\$", "$$")]
    [InlineData(@"\10", "${1}0")]        // group 1, then 0
    [InlineData(@"[\n]", "[\n]")]
    [InlineData(@"\q\&\\", @"q&\")]
    [InlineData(@"\t\x41", "\tA")]
    public void SedReplacement_EscapesAreCharacters(string replacement, string expected)
        => Assert.Equal(expected, InvokeBashSedCommand.BuildReplacement(replacement));
}
