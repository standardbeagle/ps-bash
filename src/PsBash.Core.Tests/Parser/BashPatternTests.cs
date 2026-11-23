using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// BashPattern: THE bash pattern compiler (canonical text: quoted characters backslash-escaped) and the candidate
/// choices of the removal / substitution / case operators. Every expectation is bash 5.2 (`shopt -s extglob`,
/// C.UTF-8); end to end in PatternContextDifferentialTests and ExtGlobDifferentialTests.
/// </summary>
public class BashPatternTests
{
    private static bool Match(string subject, string pattern, BashPatternOptions options = BashPatternOptions.None)
        => BashPattern.Compile(pattern, options).IsMatch(subject);

    [Theory]
    [InlineData("b", "@(a|b)", true)]
    [InlineData("c", "@(a|b)", false)]
    [InlineData("", "?(a)", true)]
    [InlineData("a", "?(a)", true)]
    [InlineData("aa", "?(a)", false)]
    [InlineData("", "*(ab)", true)]
    [InlineData("abab", "*(ab)", true)]
    [InlineData("aba", "*(ab)", false)]
    [InlineData("", "+(ab)", false)]
    [InlineData("abab", "+(ab)", true)]
    [InlineData("foo.txt", "!(*.log)", true)]
    [InlineData("foo.log", "!(*.log)", false)]
    [InlineData("foo.log.txt", "!(*.log)", true)]
    // The exact-negation cases the usual lookahead approximation gets wrong.
    [InlineData("a", "!(a)*", true)]
    [InlineData("ab", "!(a)b", false)]
    [InlineData("b", "!(a)b", true)]
    [InlineData("ab", "@(a)!(c)", true)]
    [InlineData("ac", "@(a)!(c)", false)]
    [InlineData("foo", "!(f*|b*)", false)]
    [InlineData("zoo", "!(f*|b*)", true)]
    [InlineData("xyz", "x@(y|q)z", true)]
    [InlineData("xz", "x?(y)z", true)]
    [InlineData("a)", "@(a\\)|z)", true)]
    [InlineData("x(y", "@(x\\(y)", true)]
    [InlineData("a|b", "@(a\\|b)", true)]
    [InlineData("axc", "@(a@(b|x)c)", true)]
    [InlineData("file.c", "*.@(c|h)", true)]
    [InlineData("file.o", "*.@(c|h)", false)]
    [InlineData("A", "@(a)", false)]            // case-sensitive
    [InlineData("a\nb", "a*(?)", true)]         // ? spans a newline
    [InlineData("a\nb", "a?b", true)]
    [InlineData("a\nb", "a*b", true)]
    [InlineData("ab\n", "ab", false)]           // \z, not $: a trailing newline is a character
    [InlineData("a]", "@([]a])*", true)]        // `]` first in a class is literal
    [InlineData("x", "@([!a-c])", true)]
    [InlineData("b", "@([!a-c])", false)]
    [InlineData("7", "@([[:digit:]])", true)]
    [InlineData("a*b", "@(a\\*b)", true)]       // an escaped (quoted) * is literal
    [InlineData("axb", "@(a\\*b)", false)]
    [InlineData("]", "[a\\]b]", true)]          // an escaped ] is a member
    [InlineData("-", "[a\\-z]", true)]          // so is an escaped -: no range
    [InlineData("m", "[a\\-z]", false)]
    [InlineData("m", "[a-z]", true)]
    [InlineData("@(a)", "@\\(a\\)", true)]      // an escaped paren cannot open an extglob
    [InlineData("a", "@\\(a\\)", false)]
    [InlineData("@(a)", "@(a", false)]
    [InlineData("@(a", "@(a", true)]            // an unterminated list is literal
    public void Compile_MatchesLikeBash(string subject, string pattern, bool expected)
        => Assert.Equal(expected, Match(subject, pattern));

    /// <summary>Bracket expressions (bash 5.2, C.UTF-8 oracle, probe o1.sh / r1).</summary>
    [Theory]
    [InlineData("]", "[!]]", false)]
    [InlineData("x", "[!]]", true)]            // `case x in [!]])` matches
    [InlineData("d", "[\\d]", true)]           // `\d` is the letter d, not a digit class
    [InlineData("5", "[\\d]", false)]
    [InlineData("m", "[z-a]", false)]          // a reversed range is empty, never an error
    [InlineData("x", "[z-ax]", true)]
    [InlineData("x", "[[:foo:]x]", true)]      // an unknown class matches nothing, the rest still counts
    [InlineData(" ", "[[.space.]]", true)]
    [InlineData("x", "[[.x.]]", true)]
    [InlineData("x", "[[=x=]y]", true)]
    [InlineData("-", "[]-a]", false)]          // `]-a` is a range
    [InlineData("^", "[]-a]", true)]
    [InlineData("x", "[!]-a]", true)]
    [InlineData("\\", "[\\\\]", true)]
    [InlineData("x", "[^x]", false)]
    [InlineData("X", "[[:upper:][:digit:]]", true)]
    [InlineData("[a-", "[a-", true)]           // unclosed: literal
    [InlineData("é", "[[:alpha:]]", true)]     // C.UTF-8: classes follow the locale
    [InlineData("ẞ", "[[:upper:]]", true)]
    [InlineData("é", "[[:lower:]]", true)]
    public void Compile_Brackets(string subject, string pattern, bool expected)
        => Assert.Equal(expected, Match(subject, pattern));

    [Theory]
    [InlineData("é", "[[:alpha:]]", false)]    // C locale: ASCII classes
    [InlineData("a", "[[:alpha:]]", true)]
    public void Compile_ByteLocale_ClassesAreAscii(string subject, string pattern, bool expected)
        => Assert.Equal(expected, Match(subject, pattern, BashPatternOptions.Bytes));

    /// <summary>nocaseglob / nocasematch fold characters, ranges and members, never a POSIX class (bash 5.2: under
    /// nocaseglob `[[:upper:]]*` matches Ab, `[[:lower:]]b` does not).</summary>
    [Theory]
    [InlineData("ABC", "abc", true)]
    [InlineData("Ab", "[[:upper:]]*", true)]
    [InlineData("Ab", "[[:lower:]]b", false)]
    [InlineData("a", "[[:upper:]]", false)]
    [InlineData("A", "[[:lower:]]", false)]
    [InlineData("B", "[a-c]", true)]
    [InlineData("B", "@(b|x)", true)]
    public void Compile_NoCase_FoldsCharactersNotClasses(string subject, string pattern, bool expected)
        => Assert.Equal(expected, Match(subject, pattern, BashPatternOptions.NoCase));

    [Fact]
    public void Compile_DeeplyNestedGroups_IsAParseErrorNotAStackOverflow()
    {
        const int n = 20000;
        var pattern = string.Concat(Enumerable.Repeat("@(", n)) + "a" + new string(')', n);
        Assert.Throws<ParseException>(() => BashPattern.Compile(pattern).IsMatch("a"));
    }

    /// <summary>A GLOBIGNORE pattern matches with FNM_PATHNAME: <c>*</c>, <c>?</c> and a bracket stop at <c>/</c>,
    /// but a <c>*</c> that ends the pattern takes the rest (bash 5.2: GLOBIGNORE=*.txt keeps foo/two.txt, f* drops it).</summary>
    [Theory]
    [InlineData("foo/two.txt", "*.txt", false)]
    [InlineData("one.txt", "*.txt", true)]
    [InlineData("foo/two.md", "f*", true)]
    [InlineData("foo/two.md", "*o*", true)]
    [InlineData("foo/two.md", "*d", false)]
    [InlineData("foo/two.md", "*/*.md", true)]
    [InlineData("foo/two.md", "?oo/*", true)]
    [InlineData("foo/two.md", "foo?two.md", false)]
    [InlineData("foo/two.md", "foo[/]two.md", false)]
    [InlineData("foo/two.md", "*[d]", false)]
    public void Compile_Pathname_SlashIsOnlyCrossedByATrailingStar(string text, string pattern, bool expected)
        => Assert.Equal(expected, Match(text, pattern, BashPatternOptions.Pathname));

    [Theory]
    [InlineData("a*b", "a\\*b")]
    [InlineData("@(x|y)", "@\\(x\\|y\\)")]
    [InlineData("[a]\\", "\\[a\\]\\\\")]
    [InlineData("plain", "plain")]
    public void EscapeLiteral_MakesEveryPatternCharacterInert(string literal, string expected)
    {
        Assert.Equal(expected, BashPattern.EscapeLiteral(literal));
        Assert.True(Match(literal, BashPattern.EscapeLiteral(literal)));
    }

    // ${v#p} ${v##p} ${v%p} ${v%%p}: the value left after the removal.
    private static string Remove(string value, string pattern, bool suffix, bool longest)
    {
        var p = BashPattern.Compile(pattern);
        if (suffix) { int st = p.MatchSuffix(value, longest); return st < 0 ? value : value[..st]; }
        int n = p.MatchPrefix(value, longest);
        return n < 0 ? value : value[n..];
    }

    [Theory]
    [InlineData("/usr/lib/x", "*/", false, true, "x")]
    [InlineData("/usr/lib/x", "*/", false, false, "usr/lib/x")]
    [InlineData("a.tar.gz", ".*", true, false, "a.tar")]
    [InlineData("a.tar.gz", ".*", true, true, "a")]
    [InlineData("a.b.c", "*.", false, false, "b.c")]
    [InlineData("a.b.c", "*.", false, true, "c")]
    [InlineData("x\ny/z", "*/", false, true, "z")]
    [InlineData("ab\n", "b", true, false, "ab\n")]
    [InlineData("abc", "@(a|ab)", false, true, "c")]     // extglob: longest, not leftmost-first
    [InlineData("abc", "@(ab|a)", false, false, "bc")]   // shortest
    [InlineData("abab", "+(a|ab)", false, true, "")]
    [InlineData("mx", "[z-a]", false, false, "mx")]
    [InlineData("5x", "[\\d]", false, false, "5x")]
    [InlineData("a(b)c", "a\\(", false, false, "b)c")]
    [InlineData("file.tar.gz", ".@(gz|bz2)", true, false, "file.tar")]
    [InlineData("abc", "", false, true, "abc")]
    public void MatchPrefixSuffix_RemovesLikeBash(string value, string pattern, bool suffix, bool longest, string expected)
        => Assert.Equal(expected, Remove(value, pattern, suffix, longest));

    private static string Sub(string value, string pattern, string template, BashPattern.SubstituteMode mode,
        bool amp = true, BashPatternOptions options = BashPatternOptions.None)
        => BashPattern.Substitute(value, BashPattern.Compile(pattern, options), pattern.Length == 0, template, amp, mode);

    [Theory]
    [InlineData("123x", "*([0-9])", "X", BashPattern.SubstituteMode.All, "XXx")]     // no extra match at the end
    [InlineData("abc", "?(z)", "X", BashPattern.SubstituteMode.All, "XaXbXc")]
    [InlineData("abc", "@(a|ab)", "X", BashPattern.SubstituteMode.First, "Xc")]
    [InlineData("*x", "\\*", "X", BashPattern.SubstituteMode.Start, "Xx")]
    [InlineData("*x", "x", "Y", BashPattern.SubstituteMode.End, "*Y")]
    [InlineData("a b", "a\\ ", "X", BashPattern.SubstituteMode.Start, "Xb")]
    [InlineData("ab\n", "", "P", BashPattern.SubstituteMode.End, "ab\nP")]
    [InlineData("abc", "", "X", BashPattern.SubstituteMode.First, "abc")]
    [InlineData("abc", "", "[&]", BashPattern.SubstituteMode.Start, "[]abc")]
    [InlineData("abc", "", "[\\&]", BashPattern.SubstituteMode.End, "abc[&]")]
    [InlineData("", "*", "[&]", BashPattern.SubstituteMode.First, "[]")]
    [InlineData("", "*", "[\\&]", BashPattern.SubstituteMode.All, "[&]")]
    [InlineData("abcabc", "b", "&&", BashPattern.SubstituteMode.All, "abbcabbc")]
    [InlineData("abcabc", "@(b|bc)", "<&>", BashPattern.SubstituteMode.All, "a<bc>a<bc>")]
    [InlineData("aaa", "a", "&b", BashPattern.SubstituteMode.All, "ababab")]
    [InlineData("abc", "b", "$&", BashPattern.SubstituteMode.First, "a$bc")]          // never a .NET substitution
    [InlineData("abc", "b", "$1", BashPattern.SubstituteMode.First, "a$1c")]
    [InlineData("abc", "b", "\\\\", BashPattern.SubstituteMode.First, "a\\c")]
    [InlineData("abc", "b", "\\\\&", BashPattern.SubstituteMode.First, "a\\bc")]
    [InlineData("abc", "b", "a\\b", BashPattern.SubstituteMode.First, "aa\\bc")]
    [InlineData("abc", "b", "\\\\\\&", BashPattern.SubstituteMode.First, "a\\&c")]
    [InlineData("abc", "a*", "X", BashPattern.SubstituteMode.First, "X")]
    [InlineData("abc", "[ac]", "", BashPattern.SubstituteMode.All, "b")]
    public void Substitute_ReplacesLikeBash(string value, string pattern, string template, BashPattern.SubstituteMode mode, string expected)
        => Assert.Equal(expected, Sub(value, pattern, template, mode));

    [Fact]
    public void Substitute_PatsubReplacementOff_AmpersandIsText()
    {
        Assert.Equal("a[&]c", Sub("abc", "b", "[&]", BashPattern.SubstituteMode.First, amp: false));
        Assert.Equal("a&c", Sub("abc", "b", "\\&", BashPattern.SubstituteMode.First, amp: false));
    }

    [Fact]
    public void Substitute_NoCase_Folds()
    {
        Assert.Equal("AxC", Sub("ABC", "b", "x", BashPattern.SubstituteMode.First, options: BashPatternOptions.NoCase));
        Assert.Equal("abc", Sub("abc", "[[:upper:]]", "x", BashPattern.SubstituteMode.All, options: BashPatternOptions.NoCase));
    }

    [Theory]
    [InlineData("abc", "b", true, false, "abc")]     // ^ looks only at the first character
    [InlineData("abc", "ab", true, true, "abc")]     // each character against the WHOLE pattern
    [InlineData("abc", "[ab]", true, true, "ABc")]
    [InlineData("ABC", "b", false, false, "ABC")]
    [InlineData("ABC", "BC", false, true, "ABC")]
    [InlineData("ABC", "[BC]", false, true, "Abc")]
    [InlineData("abc", null, true, false, "Abc")]
    [InlineData("éa", null, true, false, "Éa")]
    [InlineData("ẞx", null, true, true, "ẞX")]
    public void ModifyCase_TestsEachCharacterAgainstThePattern(string value, string? pattern, bool upper, bool all, string expected)
        => Assert.Equal(expected, BashPattern.ModifyCase(value, pattern is null ? null : BashPattern.Compile(pattern), upper, all, asciiOnly: false));

    [Fact]
    public void ModifyCase_ByteLocale_OnlyAscii()
        => Assert.Equal("éA", BashPattern.ModifyCase("éa", null, upper: true, all: true, asciiOnly: true));

    [Fact]
    public void Compile_SamePatternTwice_IsCached()
        => Assert.Same(BashPattern.Compile("a*b"), BashPattern.Compile("a*b"));

    // The cache is bounded by the pattern text it holds as well as by entries: a pattern past MaxCacheablePattern is
    // compiled for its one use, so a script that builds huge patterns cannot pin them in the long-lived host.
    [Fact]
    public void Compile_PatternTooLongToCache_IsNotKept()
    {
        string big = new string('a', BashPattern.MaxCacheablePattern + 1) + "*";
        Assert.NotSame(BashPattern.Compile(big), BashPattern.Compile(big));
        Assert.True(BashPattern.CacheSize.Chars <= 256 * 1024);
    }

    // Quoted text in a pattern escapes the bracket syntax too: ["$x"a] with x=! is the set {!, a} (bash: lit).
    [Theory]
    [InlineData("!", "z", false)]
    [InlineData("!", "!", true)]
    [InlineData("^", "z", false)]
    [InlineData("^", "^", true)]
    public void EscapeLiteral_QuotedNegationInABracket_IsAMember(string quoted, string subject, bool expected)
        => Assert.Equal(expected, BashPattern.Compile("[" + BashPattern.EscapeLiteral(quoted) + "a]").IsMatch(subject));

    [Theory]
    [InlineData("a-c", "b", false)]   // ["$x"] with x=a-c: three members, no range
    [InlineData("a-c", "-", true)]
    public void EscapeLiteral_QuotedDashInABracket_IsNoRange(string quoted, string subject, bool expected)
        => Assert.Equal(expected, BashPattern.Compile("[" + BashPattern.EscapeLiteral(quoted) + "]").IsMatch(subject));

    [Fact]
    public void EscapeWordText_KeepsBracketSyntaxActive()
    {
        // [$c] with c=abc: the unquoted `[` and `]` around the spliced value make a bracket expression.
        Assert.Equal("[", BashPattern.EscapeWordText("["));
        Assert.True(BashPattern.Compile(BashPattern.EscapeWordText("[") + "abc" + BashPattern.EscapeWordText("]")).IsMatch("b"));
        Assert.Equal("\\(", BashPattern.EscapeWordText("("));
    }

    [Theory]
    [InlineData("+(a)", "+\\(a\\)")]
    [InlineData("*(a|b)", "*\\(a\\|b\\)")]
    [InlineData("\\(x", "\\(x")]
    [InlineData("a*", "a*")]
    public void ValueWithoutExtGlob_MakesTheListSyntaxLiteral(string value, string expected)
        => Assert.Equal(expected, BashPattern.ValueWithoutExtGlob(value));

    // FNM_PERIOD on a dot-file name (bash 5.2, oracle: touch .a b; echo @(.a|b) ?(.)a +(.)a !(b) prints
    // `.a b .a`, `.a`, `!(b)`): the leading period needs a literal period, also inside an extglob arm.
    [Theory]
    [InlineData("@(.a|b)", ".a", true)]
    [InlineData("?(.)a", ".a", true)]
    [InlineData("+(.)a", ".a", true)]
    [InlineData("!(b)", ".a", false)]
    [InlineData("*", ".a", false)]
    [InlineData("?a", ".a", false)]
    [InlineData("[.]a", ".a", false)]
    [InlineData(".*", ".a", true)]
    [InlineData("\\.a", ".a", true)]
    [InlineData("?(x)*", ".a", false)]   // the group matched nothing: the star still faces the period
    [InlineData("?(x).a", ".a", true)]
    [InlineData("@(.|x)*", ".abc", true)]
    [InlineData("*(.)a*", ".abc", true)]
    public void LeadingPeriod_NeedsALiteralPeriod(string pattern, string name, bool expected)
        => Assert.Equal(expected, BashPattern.Compile(pattern, BashPatternOptions.LeadingPeriod).IsMatch(name));

    // A list that never closes is found by one scan: `@(@(@(…` used to cost 2^n (the host hung on 40), and 2,000 of
    // them recursed past the nesting bound (an error, status 0). bash 5.2: h1 1, h2 0, at once.
    [Theory]
    [InlineData("@(", 40)]
    [InlineData("+(a|", 40)]
    [InlineData("!(", 40)]
    [InlineData("@(", 300)]
    [InlineData("@(", 5000)]
    public void Compile_UnterminatedExtGlobLists_AreLinear(string unit, int count)
    {
        string p = string.Concat(System.Linq.Enumerable.Repeat(unit, count));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var program = BashPattern.Compile(p);
        Assert.False(program.IsMatch("x"));
        Assert.True(program.IsMatch(p));               // every operator is literal text
        Assert.Equal(-1, program.MatchPrefix("x", longest: true));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"took {sw.Elapsed}");
    }

    // bash 5.2 (lib/glob/sm_loop.c PATSCAN, EXTMATCH): any `(` in a list nests, a bracket hides its `)` and `|`, and an
    // operator whose list never closes compares the rest of the pattern with the rest of the subject as plain text.
    [Theory]
    [InlineData("@(a(b)c)", "a(b)c", true)]
    [InlineData("@(a(b)c)", "a(bc)", false)]
    [InlineData("@(a(x|y))", "a(x|y)", true)]
    [InlineData("@(a(x|y))", "ax", false)]
    [InlineData("@(a[)]b)", "a)b", true)]
    [InlineData("@(a\\)b)", "a)b", true)]
    [InlineData("@(a\\|b)", "a|b", true)]
    [InlineData("@(a(b)", "a(b", false)]
    [InlineData("@(a(b)", "@(a(b)", true)]
    [InlineData("@(a*(b)", "@(abb", false)]
    [InlineData("@(a*(b)", "@(a*(b)", true)]
    [InlineData("@(a[)]b", "@(a)b", false)]
    [InlineData("@(a[)]b", "@(a[)]b", true)]
    [InlineData("@(a?", "@(ab", false)]
    [InlineData("*@(a", "z@(a", true)]
    [InlineData("*@(a", "za", false)]
    [InlineData("@(a\\\\", "@(a\\", false)]
    public void ExtGlob_ListBoundsAreBashPatscan(string pattern, string subject, bool matches)
        => Assert.Equal(matches, BashPattern.Compile(pattern).IsMatch(subject));
    // The host never hangs on a pattern. Without `!( )` the program runs on the NonBacktracking engine, linear in the
    // subject: these bombs (bash 5.2 itself did not finish *(a|aa) on 45 a's and a b within 300 s) answer at once.
    // The bound is a hang detector far above the expected milliseconds, not a benchmark.
    [Theory]
    // `*(a|aa)` matches the empty string: ${v%%…} keeps v whole (the empty suffix), ${v/#…/X} takes the a's.
    [InlineData("*(a|aa)", 45, "b", 46, "Xb")]
    [InlineData("*a*a*a*a*a*a*a*a*a*b", 300, "", -1, null)]
    [InlineData("+(a|aa|aaa)c", 60, "b", -1, null)]
    public void PatternBombs_RunInLinearTime(string pattern, int count, string tail, int suffixAt, string? substituted)
    {
        string s = new string('a', count) + tail;
        var program = BashPattern.Compile(pattern);
        Assert.True(program.IsLinear);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(program.IsMatch(s));
        Assert.Equal(suffixAt, program.MatchSuffix(s, longest: true));
        Assert.Equal(substituted ?? s, BashPattern.Substitute(s, program, false, "X", true, BashPattern.SubstituteMode.Start));
        program.MatchPrefix(s, longest: true);
        program.MatchPrefix(s, longest: false);
        program.Find(s, 0, out _, out _);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"took {sw.Elapsed}");
    }

    // `!( )` needs the backtracking engine (a lookbehind and backreferences); a bomb behind one runs out of
    // BashPatternProgram.MatchBudget, counts as no match and is reported (the host log) instead of hanging the host.
    [Fact]
    public void BacktrackingBomb_GivesUpWithinTheBudgetAndIsReported()
    {
        var program = BashPattern.Compile("!(x)*a*a*a*a*a*a*a*a*a*b");
        Assert.False(program.IsLinear);
        string s = new string('a', 300);
        var seen = new List<string>();
        var before = BashPattern.Diagnostic;
        BashPattern.Diagnostic = m => { lock (seen) seen.Add(m); };
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Assert.False(program.IsMatch(s));
            Assert.Equal(-1, program.MatchPrefix(s, longest: true));
            Assert.Equal(-1, program.MatchSuffix(s, longest: false));
            Assert.Equal(s, BashPattern.Substitute(s, program, false, "X", true, BashPattern.SubstituteMode.All));
            // four operations, each stopped by one budget (a loop of candidate lengths stops at the first timeout)
            Assert.True(sw.Elapsed < BashPatternProgram.MatchBudget * 4 + TimeSpan.FromSeconds(40), $"took {sw.Elapsed}");
        }
        finally
        {
            BashPattern.Diagnostic = before;
        }
        lock (seen) Assert.Contains(seen, m => m.Contains("gave up", StringComparison.Ordinal));
    }
}
