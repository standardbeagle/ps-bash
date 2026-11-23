using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// BashEre: THE POSIX ERE engine of <c>[[ s =~ re ]]</c> (glibc regcomp/regexec, REG_EXTENDED). Every expectation is
/// bash 5.2.21 / glibc 2.39 in C.UTF-8: <c>re='…'; [[ $s =~ $re ]]; echo $? "${BASH_REMATCH[@]}"</c> (0 match,
/// 1 no match, 2 regcomp rejects the regex). Under .NET regex most of these differed (leftmost-first alternation,
/// lazy <c>*?</c>, <c>(?:)</c>, <c>$</c> before a final newline, captures, classes beyond ASCII).
/// </summary>
public class BashEreTests
{
    private static void AssertMatch(string subject, string regex, int status, string? groups, bool icase = false)
    {
        var r = BashEre.Match(regex, icase, subject);
        Assert.Equal(status, r.Status);
        if (status == 0) Assert.Equal(groups ?? "", string.Join(",", r.Groups));
        else Assert.Empty(r.Groups);
    }

    [Theory]
    // Leftmost-longest, and POSIX/glibc subexpression rules.
    [InlineData("ab", "(a|ab)", 0, "ab,ab")]
    [InlineData("xabcx", "(a|ab)(c|bcd)?", 0, "abc,ab,c")]
    [InlineData("aaa", "a*?", 0, "aaa")]
    [InlineData("abcbc", "a.*?c", 0, "abcbc")]
    [InlineData("abc", "^(a)|(x)$", 0, "a,a,")]
    [InlineData("ab", "(a)(x)?b", 0, "ab,a,")]
    [InlineData("abcd", "(a|ab)(c|bcd)(d*)", 0, "abcd,a,bcd,")]
    [InlineData("ab", "(^a)(b$)", 0, "ab,a,b")]
    [InlineData("b", "(a*)*", 0, ",")]
    [InlineData("b", "(a*)+", 0, ",")]
    [InlineData("abc", "(a|ab)(bc|c)", 0, "abc,a,bc")]
    [InlineData("aa", "(a*)(a*)", 0, "aa,aa,")]
    [InlineData("ab", "((a)|b)*", 0, "ab,b,a")]
    [InlineData("aa", "(a?)*", 0, "aa,a")]
    [InlineData("aa", "((a?))*", 0, "aa,a,a")]
    [InlineData("ab", "(a|b)*", 0, "ab,b")]
    [InlineData("abc", "(.*)(.*)", 0, "abc,abc,")]
    [InlineData("b", "(a)|b", 0, "b,")]
    [InlineData("", "x*", 0, "")]
    [InlineData("xyz", "y*$", 0, "")]
    [InlineData("abc", "b*", 0, "")]
    [InlineData("abab", "(a|ab)(c|bcd)?(d*)", 0, "ab,ab,,")]
    [InlineData("aaa", "(a|aa)*", 0, "aaa,a")]
    [InlineData("aaa", "(aa|a)*", 0, "aaa,a")]
    [InlineData("abcabc", "(abc|ab|a)*(c)?", 0, "abcabc,abc,")]
    [InlineData("xyx", "(x|xy)(x|yx)?", 0, "xyx,x,yx")]
    [InlineData("aba", "(a|ab|b)*", 0, "aba,a")]
    [InlineData("a", "(|a)", 0, "a,a")]
    [InlineData("aa", "(a|)*", 0, "aa,a")]
    [InlineData("aa", "(|a)*", 0, "aa,a")]
    [InlineData("abb", "a(b){0,2}", 0, "abb,b")]
    [InlineData("abb", "a(b|bb){1,2}", 0, "abb,b")]
    [InlineData("ab", "(a(b)?)+", 0, "ab,ab,b")]
    [InlineData("aba", "(a(b)?)+", 0, "aba,a,b")]
    [InlineData("a", "()", 0, ",")]
    [InlineData("ab", "(a){0}b", 0, "b,")]
    // Repetition and alternation edge forms.
    [InlineData("aaa", "a{,2}", 0, "aa")]
    [InlineData("aaa", "a{2,}", 0, "aaa")]
    [InlineData("aaa", "a{,}", 0, "aaa")]
    [InlineData("a", "a**", 0, "a")]
    [InlineData("a", "a+*?", 0, "a")]
    [InlineData("a)", "a)", 0, "a)")]
    [InlineData("a", "a||b", 0, "a")]
    [InlineData("a", "a|", 0, "a")]
    [InlineData("a", "|a", 0, "a")]
    [InlineData("a", "a{0}", 0, "")]
    [InlineData("a", "a{32767}", 1, null)]
    [InlineData("a}", "a}", 0, "a}")]
    // Anchors: only the true start and end; `.` and `[^x]` take a newline.
    [InlineData("a\nb", "a.b", 0, "a\nb")]
    [InlineData("a\n", "a$", 1, null)]
    [InlineData("x\na", "^a", 1, null)]
    [InlineData("\n", "^[^a]$", 0, "\n")]
    [InlineData("a^b", "a^b", 1, null)]
    [InlineData("b", "a|^b", 0, "b")]
    [InlineData("xay", "x$y", 1, null)]
    [InlineData("a", "a$|b", 0, "a")]
    // GNU escapes glibc accepts in ERE.
    [InlineData("d", @"\d", 0, "d")]
    [InlineData("5", @"\d", 1, null)]
    [InlineData("ab c", @"\w+", 0, "ab")]
    [InlineData("ab c", @"\W", 0, " ")]
    [InlineData("a b", @"a\sb", 0, "a b")]
    [InlineData("a b", @"\S+$", 0, "b")]
    [InlineData("ab b", @"\bb", 0, "b")]
    [InlineData("ab b", @"\Bb", 0, "b")]
    [InlineData("ab b", @"\<b", 0, "b")]
    [InlineData("ab b", @"a\>", 1, null)]
    [InlineData("ab", @"\`a", 0, "a")]
    [InlineData("ab", @"b\'", 0, "b")]
    [InlineData("x62", @"\x62", 0, "x62")]
    [InlineData("a{1", @"a\{1", 0, "a{1")]
    [InlineData("ab", @"a\Kb", 1, null)]
    // Back-references.
    [InlineData("aa", @"(a)\1", 0, "aa,a")]
    [InlineData("aaaa", @"(a*)\1", 0, "aaaa,aa")]
    [InlineData("abab", @"(ab)\1", 0, "abab,ab")]
    [InlineData("xabcabcy", @"(a|ab)(c|bcd)\2", 1, null)]
    [InlineData("aaaa", @"^(a+)\1$", 0, "aaaa,aa")]
    [InlineData("aaa", @"^(a+)\1$", 1, null)]
    // Brackets.
    [InlineData(@"\", @"[\]", 0, @"\")]
    [InlineData("-", "[a-]", 0, "-")]
    [InlineData("-", "[]-a]", 1, null)]
    [InlineData("]", "[]a]", 0, "]")]
    [InlineData("x", "[^]a]", 0, "x")]
    [InlineData("a]", "[]a]+", 0, "a]")]
    [InlineData("-", "[[.-.]]", 0, "-")]
    [InlineData("a", "[[.a.]]", 0, "a")]
    [InlineData("a", "[[=a=]]", 0, "a")]
    [InlineData("é", "[[=e=]]", 1, null)]
    [InlineData("é", "[a-z]", 1, null)]
    [InlineData("a", "[[:alpha:][:digit:]]", 0, "a")]
    // Code points, not UTF-16 units or bytes; classes are glibc's C.UTF-8 tables.
    [InlineData("é", "^..$", 1, null)]
    [InlineData("é", "^.$", 0, "é")]
    [InlineData("😀", "^.$", 0, "😀")]
    [InlineData("λ", "^.$", 0, "λ")]
    [InlineData("é", "^[[:alpha:]]$", 0, "é")]
    [InlineData("a\tb", "a[[:blank:]]b", 0, "a\tb")]
    [InlineData("_", "[[:punct:]]", 0, "_")]
    [InlineData("é", "[[:lower:]]", 0, "é")]
    [InlineData("É", "[[:upper:]]", 0, "É")]
    [InlineData("٣", "[[:digit:]]", 1, null)]
    [InlineData("٣", "[[:alnum:]]", 0, "٣")]
    [InlineData("€", "[[:punct:]]", 0, "€")]
    [InlineData("é", "[^a]", 0, "é")]
    [InlineData("é", "[é]", 0, "é")]
    [InlineData("aé", "^a[^x]$", 0, "aé")]
    [InlineData("é", @"\w", 0, "é")]
    [InlineData("aé", @"\bé", 1, null)]
    public void Match_IsGlibcEre(string subject, string regex, int status, string? groups)
        => AssertMatch(subject, regex, status, groups);

    [Theory]
    // Back-references are checked exactly: the span is never one a reference-as-any-length NFA merely reaches.
    [InlineData("xy", @"(a*)*(x)\2", 1, null)]
    [InlineData("key=value; other=thing; key=value; x", @"(key=[a-z]+).*\1", 0, "key=value; other=thing; key=value,key=value")]
    [InlineData("the the cat sat sat", @"([a-z]+) \1", 0, "the the,the")]
    [InlineData("abcdbcd", @"(a|ab)(c|bcd)(d*)\2", 0, "abcdbcd,a,bcd,")]
    [InlineData("aab", @"(a*)+\1b", 0, "aab,a")]
    [InlineData("abba", @"((a)|b)*\2", 0, "abba,b,a")]
    [InlineData("b", @"(a)?\1", 1, null)]
    [InlineData("b", @"()\1*", 0, ",")]
    [InlineData("xabbay", @"(.)(.)\2\1", 0, "abba,a,b")]
    [InlineData("aabbaabbx", @"(a+)(b+)\1\2", 0, "aabbaabb,aa,bb")]
    [InlineData("abcabc", @"^(.*)\1$", 0, "abcabc,abc")]
    [InlineData("xyzxyzq", @"(.*)\1", 0, "xyzxyz,xyz")]
    [InlineData("abcabcabc", @"([a-c]*)\1", 0, "abcabc,abc")]
    [InlineData("yy", @"(x)?(y)?\2", 0, "yy,,y")]
    [InlineData("aaaa", @"(a)\1{2}", 0, "aaa,a")]
    [InlineData("aaaa", @"(a)(\1)*", 0, "aaaa,a,a")]
    [InlineData("abcab", @"(ab|a)(bc|c)?\1", 0, "abcab,ab,c")]
    public void Match_BackReferences_AreExact(string subject, string regex, int status, string? groups)
        => AssertMatch(subject, regex, status, groups);

    [Theory]
    // Brackets with hyphens and multibyte endpoints (bash 5.2.21 / glibc 2.39, C.UTF-8).
    [InlineData("-", "[a---z]", 2, null)]
    [InlineData("b", "[a---z]", 2, null)]
    [InlineData("β", "[α-ω]", 2, null)]
    [InlineData(",", "[!--]", 0, ",")]
    [InlineData(".", "[--/]", 0, ".")]
    [InlineData("+", "[%--]", 0, "+")]
    [InlineData("-", "[--@]", 0, "-")]
    [InlineData("a", "[a-a]", 0, "a")]
    [InlineData("b", "[[.-.]-z]", 0, "b")]
    [InlineData("b", "[a-[.z.]]", 0, "b")]
    [InlineData("-", "[--]", 0, "-")]
    [InlineData("-", "[^-a]", 1, null)]
    [InlineData("d", "[a-c-e]", 2, null)]
    [InlineData("-", "[a-c-]", 0, "-")]
    [InlineData("b", "[---]", 1, null)]
    [InlineData("-", "[---]", 0, "-")]
    [InlineData("a", "[[.a.]-[.c.]]", 0, "a")]
    [InlineData("é", "[[.é.]]", 2, null)]
    [InlineData("b", "[a-]b]", 1, null)]
    [InlineData("]", "[]-a]", 0, "]")]
    [InlineData("^", "[]-^]", 0, "^")]
    [InlineData("b", "[α-a]", 2, null)]
    [InlineData("b", "[a-α]", 2, null)]
    [InlineData("β", "[[.α.]-ω]", 2, null)]
    [InlineData("β", "[α-[.ω.]]", 2, null)]
    public void Match_BracketRanges_AreGlibcs(string subject, string regex, int status, string? groups)
        => AssertMatch(subject, regex, status, groups);

    [Fact]
    public void Match_InvalidByteInABracket_IsValidButNeverAMember()
    {
        // $'\xff' arrives as RawBytes' marker U+DCFF (bash 5.2.21 / glibc 2.39, C.UTF-8). A one-byte collating or
        // equivalence name is valid but adds nothing; as a class name it is REG_ECTYPE; as a range endpoint it is its
        // byte value ([a-\xff] is a..U+00FF). In the subject it is only ever itself: `.` and every bracket, negated
        // ones included, refuse it. (Fact, not InlineData: xunit's serializer mangles a lone surrogate.)
        const string ff = "\uDCFF", x80 = "\uDC80";
        AssertMatch("x", "[[." + ff + ".]]", 1, null);
        AssertMatch(ff, "[[." + ff + ".]]", 1, null);
        AssertMatch(ff, ff, 0, ff);
        AssertMatch("x", "[[=" + ff + "=]]", 1, null);
        AssertMatch("x", "[[:" + ff + ":]]", 2, null);
        AssertMatch(ff, "[" + ff + "]", 1, null);
        AssertMatch(ff, "[a-" + ff + "]", 1, null);
        AssertMatch("é", "[a-" + ff + "]", 0, "é");
        AssertMatch("ÿ", "[a-" + ff + "]", 0, "ÿ");
        AssertMatch("b", "[a-[." + ff + ".]]", 0, "b");
        AssertMatch(x80, "[" + x80 + "-" + ff + "]", 1, null);
        AssertMatch("é", "[" + x80 + "-" + ff + "]", 0, "é");
        AssertMatch("b", "[[." + ff + ".]-z]", 2, null);
        AssertMatch("b", "[" + ff + "-z]", 2, null);
        AssertMatch("\uDCFE", "[" + ff + "-\uDCFE]", 2, null);
        AssertMatch(ff, "[^" + ff + "]", 1, null);
        AssertMatch(ff, "[^a]", 1, null);
        AssertMatch(ff, ".", 1, null);
        AssertMatch("é", "[^" + ff + "]", 0, "é");
    }

    [Fact]
    public void Match_LoneSurrogateAnywhere_IsAStatusNotAnException()
    {
        // ConvertFromUtf32 threw on a lone surrogate inside [[. .]] / [[= =]] / [[: :]], and the exception escaped
        // into the script. A lone surrogate that is not a marker is a 3-byte name: REG_ECOLLATE / REG_ECTYPE.
        const string d8 = "\uD800";
        Assert.Equal(2, BashEre.Match("[[." + d8 + ".]]", false, "x").Status);
        Assert.Equal(2, BashEre.Match("[[=" + d8 + "=]]", false, "x").Status);
        Assert.Equal(2, BashEre.Match("[[:" + d8 + ":]]", false, "x").Status);
        Assert.Equal(2, BashEre.Match("[[." + d8 + d8 + ".]]", true, "x").Status);
        Assert.Equal(0, BashEre.Match("^.$", false, d8).Status);
        Assert.Equal(0, BashEre.Match("(" + d8 + ")\\1", true, d8 + d8).Status);
    }

    [Theory]
    // regcomp errors: status 2 (bash prints nothing).
    [InlineData("(?:a)b")]
    [InlineData("a(?=b)")]
    [InlineData("a(?b)")]
    [InlineData(@"a\")]
    [InlineData(@"\1(a)")]
    [InlineData(@"(a)\2")]
    [InlineData(@"(a)|\1x")]
    [InlineData("a{")]
    [InlineData("a{1")]
    [InlineData("a{x}")]
    [InlineData("a{2,1}")]
    [InlineData("{1}a")]
    [InlineData("*a")]
    [InlineData("a|*b")]
    [InlineData("(*a)")]
    [InlineData("^*")]
    [InlineData("a^*")]
    [InlineData(@"a\b*")]
    [InlineData("(a")]
    [InlineData("a{32768}")]
    [InlineData("[[:foo:]]")]
    [InlineData("[z-a]")]
    [InlineData("[[.space.]]")]
    [InlineData("[[=é=]]")]
    [InlineData("[α-ω]")]
    [InlineData("a[b")]
    public void Match_RegcompRejects_IsStatus2(string regex)
    {
        var r = BashEre.Match(regex, false, "a");
        Assert.Equal(2, r.Status);
        Assert.NotNull(r.Error);
    }

    [Theory]
    // REG_ICASE (shopt -s nocasematch): glibc folds the pattern and the subject with towupper, but an ESCAPED
    // ASCII character stays as written (so `\d` matches neither d nor D), and [:upper:]/[:lower:] become [:alpha:].
    [InlineData("D", @"\d", 1)]
    [InlineData("d", @"\d", 1)]
    [InlineData("A", "[a-z]", 0)]
    [InlineData("a", "[A-Z]", 0)]
    [InlineData("A", "[[:lower:]]", 0)]
    [InlineData("a", "[[:upper:]]", 0)]
    [InlineData("É", "é", 0)]
    [InlineData("a", "[^A]", 1)]
    [InlineData("ǅ", "ǆ", 0)]
    [InlineData("ß", "SS", 1)]
    [InlineData("Σ", "σ", 0)]
    [InlineData("ς", "Σ", 0)]
    [InlineData("é", "[É]", 0)]
    [InlineData("AB", "^ab$", 0)]
    public void Match_NoCaseMatch_FoldsLikeGlibc(string subject, string regex, int status)
        => Assert.Equal(status, BashEre.Match(regex, true, subject).Status);

    [Fact]
    public void Match_NoCaseMatch_ReportsTheSubjectsOwnText()
        => Assert.Equal(["AB", "B"], BashEre.Match("a(b)", true, "xAB").Groups);

    [Theory]
    // glibc iswctype in C.UTF-8 for code points .NET's categories disagree on (oracle dump, ere-classes-dump.py).
    [InlineData(0x0301, "print graph punct")]
    [InlineData(0x0903, "alpha alnum print graph")]
    [InlineData(0x2160, "alpha alnum upper print graph")]
    [InlineData(0x01C5, "alpha alnum upper lower print graph")]
    [InlineData(0x0085, "cntrl")]
    [InlineData(0x00A0, "print graph punct")]
    [InlineData(0x1680, "space blank print")]
    [InlineData(0x2028, "space cntrl")]
    [InlineData(0x0378, "")]
    [InlineData(0x1F600, "print graph punct")]
    [InlineData(0x00B5, "alpha alnum lower print graph")]
    public void Classes_AreGlibcs(int cp, string expected)
    {
        var have = new List<string>();
        foreach (var cls in new[] { "alpha", "digit", "alnum", "upper", "lower", "space", "blank", "cntrl", "print", "graph", "punct", "xdigit" })
            if (BashEre.Match("^[[:" + cls + ":]]$", false, char.ConvertFromUtf32(cp)).Status == 0) have.Add(cls);
        Assert.Equal(expected, string.Join(" ", have));
    }

    // bash's quote_string_for_globbing(QGLOB_REGEXP|QGLOB_CTLESC): a quoted character is escaped only when it is an
    // ERE special; any other quoted character loses its quoting (the word `\d` is the regex `d`).
    [Theory]
    [InlineData("^", "d", "$", "^d$")]
    [InlineData("a", ".", "c", @"a\.c")]
    [InlineData("", "[", "", @"\[")]
    [InlineData("", "]", "", "]")]
    [InlineData("", @"\", "", @"\\")]
    [InlineData("a", "|", "b", @"a\|b")]
    [InlineData("a", "{", "1", @"a\{1")]
    [InlineData("a", "}", "", "a}")]
    [InlineData("[", "-", "]", "[-]")]
    [InlineData("[", "^", "a]", "[^a]")]
    [InlineData("[", "a-z", "]", "[a-z]")]
    [InlineData("", "a.*", "c", @"a\.\*c")]
    public void WordToRegex_QuotedSpecialsAreEscapedOthersAreBare(string before, string quoted, string after, string expected)
        => Assert.Equal(expected, BashEre.WordToRegex(before + BashEre.MarkQuoted(quoted) + after));

    [Fact]
    public void WordToRegex_UnclosedBracket_IsRescannedAsText()
        => Assert.Equal("a[", BashEre.WordToRegex("a["));

    [Fact]
    public void WordToRegex_LiteralCtlescInAnUnquotedValue_SurvivesOnce()
        => Assert.Equal("\u0001", BashEre.WordToRegex(BashEre.MarkUnquoted("\u0001")));

    // Bounds (host-lifecycle-contract.md §No input may take the host down): every regex returns, a pathological one
    // as status 2 instead of a hang or a stack overflow.
    [Fact]
    public void Match_DeepNesting_IsBoundedNotAStackOverflow()
    {
        var regex = new string('(', 5000) + "a" + new string(')', 5000);
        Assert.Equal(2, BashEre.Match(regex, false, "a").Status);
    }

    [Fact]
    public void Match_HugeRepetition_IsBoundedNotOutOfMemory()
        => Assert.Equal(2, BashEre.Match("(a{1000}){1000}", false, "a").Status);

    [Fact]
    public void Match_ClassicBacktrackingBombs_AreLinear()
    {
        var s = new string('a', 3000);
        var p1 = BashEre.Match("^(a|aa)*$", false, s);
        Assert.Equal(0, p1.Status);
        Assert.Equal("a", p1.Groups[1]);
        Assert.Equal(1, BashEre.Match("(a|aa)*c", false, s + "b").Status);
        Assert.Equal(1, BashEre.Match("(a*)*b", false, s).Status);
        var p4 = BashEre.Match("((a*)*)*$", false, s);
        Assert.Equal(0, p4.Status);
        Assert.Equal(3000, p4.Groups[1].Length);
        Assert.Equal(1, BashEre.Match("(x+x+)+y", false, new string('x', 5000)).Status);
    }

    // One match must not allocate past this (the engine holds at most MaxMemoryBytes; the rest is the subject's code
    // points and transient growth). The old back-reference search allocated a subject-sized array per start: 1 TB for
    // a 1 MB subject.
    private const long AllocationBound = 512L << 20;

    // Far beyond any of these (each well under a second on an idle machine), far short of the old cubic search on
    // them (hours): a bound that detects that hang, not a benchmark.
    private static readonly TimeSpan WallClockBound = TimeSpan.FromSeconds(60);

    private static BashEreResult Bounded(string regex, string subject)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var r = BashEre.Match(regex, false, subject);
        clock.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < AllocationBound, $"{regex} on {subject.Length} chars allocated {allocated >> 20} MB");
        Assert.True(clock.Elapsed < WallClockBound, $"{regex} on {subject.Length} chars took {clock.Elapsed}");
        return r;
    }

    [Fact]
    public void Match_BackReferenceBombs_AreBoundedInTimeAndMemory()
    {
        // Oracle (bash 5.2.21 / glibc 2.39): each line's status and BASH_REMATCH[0].
        var abab = string.Concat(Enumerable.Repeat("ab", 500));
        Assert.Equal(1, Bounded(@"(.)\1", abab).Status);
        var r = Bounded(@"(.)\1", abab + "xx");
        Assert.Equal(0, r.Status);
        Assert.Equal("xx", r.Groups[0]);

        var mega = new string('a', 1_000_000);
        Assert.Equal(1, Bounded(@"(x)\1", mega).Status);
        r = Bounded(@"(a)\1", mega);
        Assert.Equal(0, r.Status);
        Assert.Equal("aa", r.Groups[0]);

        // A real-world reference on a 1.4 KB line: within budget, exact.
        var words = string.Concat(Enumerable.Range(1, 200).Select(i => $"word{i} "));
        r = Bounded(@"(word[0-9]+) .* \1 ", words + " word7 ");
        Assert.Equal(0, r.Status);
        Assert.Equal("word7", r.Groups[1]);

        // glibc is itself cubic on these (a 4000-character subject takes it 1-2 s): ours must stay exact there.
        var a4000 = new string('a', 4000);
        r = Bounded(@"(a*)\1$", a4000);
        Assert.Equal(0, r.Status);
        Assert.Equal(4000, r.Groups[0].Length);
        Assert.Equal(2000, r.Groups[1].Length);
        Assert.Equal(1, Bounded(@"^(a+)\1$", a4000 + "a").Status);
        Assert.Equal(1, Bounded(@"(a*)*b\1", a4000).Status);

        // Past that (glibc: about a minute for the three), the budget may answer status 2 instead, never hang.
        var a20000 = new string('a', 20000);
        r = Bounded(@"(a*)\1$", a20000);
        Assert.True(r.Status == 2 || (r.Status == 0 && r.Groups[1].Length == 10000), $"status {r.Status}");
        Assert.Contains(Bounded(@"^(a+)\1$", a20000 + "a").Status, new[] { 1, 2 });
        Assert.Equal(1, Bounded(@"(a*)*b\1", a20000).Status);
    }
}
