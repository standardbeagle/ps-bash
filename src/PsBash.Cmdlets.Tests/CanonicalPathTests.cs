using Xunit;
using static PsBash.Cmdlets.CanonicalPath;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The gnulib canonicalize_filename_mode walk on a FAKE file system (any platform). Every expectation was taken from
/// GNU coreutils 9.4 (readlink -f/-e/-m, realpath -s/-L) on the same tree:
/// <c>/d</c> dir, <c>/d/f</c> file, <c>/d/sub</c> dir, <c>/d/sub/deep</c> dir, <c>/d/dl -&gt; sub/deep</c>,
/// <c>/d/abs -&gt; /d/sub</c>, <c>/d/fl -&gt; f</c>, <c>/d/dang -&gt; nowhere</c>, <c>/d/l1 -&gt; l2</c>, <c>/d/l2 -&gt; l1</c>.
/// </summary>
public class CanonicalPathTests
{
    private static readonly Dictionary<string, Entry> Tree = new(StringComparer.Ordinal)
    {
        ["/d"] = Entry.Of(Kind.Directory),
        ["/d/f"] = Entry.Of(Kind.File),
        ["/d/sub"] = Entry.Of(Kind.Directory),
        ["/d/sub/deep"] = Entry.Of(Kind.Directory),
        ["/d/dl"] = new(Kind.Link, "sub/deep", Kind.Directory),
        ["/d/abs"] = new(Kind.Link, "/d/sub", Kind.Directory),
        ["/d/fl"] = new(Kind.Link, "f", Kind.File),
        ["/d/dang"] = new(Kind.Link, "nowhere", Kind.Missing),
        ["/d/l1"] = new(Kind.Link, "l2", Kind.Missing),
        ["/d/l2"] = new(Kind.Link, "l1", Kind.Missing),
    };

    private static Entry Probe(string path) => Tree.TryGetValue(path, out var e) ? e : Entry.Of(Kind.Missing);

    private static Result Walk(string path, Mode mode, bool followLinks = true)
        => Canonicalize(path, mode, followLinks, Probe, windows: false);

    private static void Ok(string expected, Result r)
    {
        Assert.Null(r.Error);
        Assert.Equal(expected, r.Path);
    }

    private static void Fails(string error, Result r)
    {
        Assert.Null(r.Path);
        Assert.Equal(error, r.Error);
    }

    [Theory]
    [InlineData("/d/f", "/d/f")]
    [InlineData("/d/./sub/../f", "/d/f")]
    [InlineData("/d//sub///", "/d/sub")]
    [InlineData("/d/ghost", "/d/ghost")]          // the last component may be missing
    [InlineData("/d/sub/ghost/", "/d/sub/ghost")] // ... even named as a directory
    [InlineData("/d/dl", "/d/sub/deep")]          // a relative link, spliced in place
    [InlineData("/d/dl/..", "/d/sub")]            // .. of the link's TARGET, not of /d
    [InlineData("/d/abs/deep", "/d/sub/deep")]    // an absolute link resets the root
    [InlineData("/d/dang", "/d/nowhere")]         // a dangling link resolves to its missing target
    [InlineData("/..", "/")]
    public void AllButLast_Resolves(string input, string expected) => Ok(expected, Walk(input, Mode.AllButLast));

    [Theory]
    [InlineData("/d/nosuch/x", NoSuchFile)]
    [InlineData("/d/nosuch/../f", NoSuchFile)]    // the walk meets nosuch before .. removes it
    [InlineData("/d/f/x", NotADirectory)]
    [InlineData("/d/f/", NotADirectory)]
    [InlineData("/d/f/..", NotADirectory)]
    [InlineData("/d/fl/x", NotADirectory)]
    [InlineData("/d/dang/x", NoSuchFile)]
    [InlineData("/d/l1", LinkLoop)]
    public void AllButLast_Fails(string input, string error) => Fails(error, Walk(input, Mode.AllButLast));

    [Theory]
    [InlineData("/d/ghost", NoSuchFile)]
    [InlineData("/d/dang", NoSuchFile)]
    [InlineData("/d/f/", NotADirectory)]
    public void Existing_EveryComponentMustExist(string input, string error) => Fails(error, Walk(input, Mode.Existing));

    [Fact]
    public void Existing_ExistingNameThroughLinks() => Ok("/d/sub/deep", Walk("/d/abs/../dl", Mode.Existing));

    [Theory]
    [InlineData("/d/nosuch/x/../y", "/d/nosuch/y")]
    [InlineData("/d/f/x", "/d/f/x")]
    [InlineData("/d/f/..", "/d")]
    [InlineData("/d/l1", "/d/l1")]                // a repeated link is kept as a plain name
    [InlineData("/d/dang/x", "/d/nowhere/x")]
    public void Missing_NothingNeedExist(string input, string expected) => Ok(expected, Walk(input, Mode.Missing));

    [Theory]
    [InlineData("/d/nosuch/x", "/d/nosuch/x")]   // only the last component is looked at
    [InlineData("/d/nosuch/x/y", "/d/nosuch/x/y")]
    [InlineData("/d/dang", "/d/dang")]           // links are kept, not read
    [InlineData("/d/dl/..", "/d")]               // .. folds the link NAME away
    public void NoLinks_AllButLast_Resolves(string input, string expected)
        => Ok(expected, Walk(input, Mode.AllButLast, followLinks: false));

    [Theory]
    [InlineData("/d/f/x", NotADirectory)]
    [InlineData("/d/f/x/y", NotADirectory)]
    [InlineData("/d/fl/x", NotADirectory)]
    [InlineData("/d/f/.", NotADirectory)]
    [InlineData("/d/nosuch/.", NoSuchFile)]
    public void NoLinks_AllButLast_Fails(string input, string error)
        => Fails(error, Walk(input, Mode.AllButLast, followLinks: false));

    [Theory]
    [InlineData("/d/fl", "f")]
    [InlineData("/d/abs", "/d/sub")]
    [InlineData("/d/dang", "nowhere")]
    [InlineData("/d/abs/../fl", "f")]            // leading directories are walked through links
    public void ReadLink_PrintsTargetAsStored(string input, string target)
        => Ok(target, ReadLink(input, Probe, windows: false));

    [Theory]
    [InlineData("/d/f", InvalidArgument)]
    [InlineData("/d/sub", InvalidArgument)]
    [InlineData("/d/sub/", InvalidArgument)]
    [InlineData("/d/dl/", InvalidArgument)]      // a trailing slash follows the link: a directory
    [InlineData("/d/sub/..", InvalidArgument)]
    [InlineData("/", InvalidArgument)]
    [InlineData("/d/nosuch", NoSuchFile)]
    [InlineData("/d/nosuch/x", NoSuchFile)]
    [InlineData("/d/f/", NotADirectory)]
    [InlineData("/d/fl/", NotADirectory)]
    [InlineData("/d/f/x", NotADirectory)]
    [InlineData("/d/f/..", NotADirectory)]
    public void ReadLink_Fails(string input, string error) => Fails(error, ReadLink(input, Probe, windows: false));

    [Theory]
    [InlineData("a/", 1, true)]
    [InlineData("a/.", 1, true)]
    [InlineData("a//..", 1, true)]
    [InlineData("a/../b", 1, true)]
    [InlineData("a/b", 1, false)]
    [InlineData("a/.b", 1, false)]
    [InlineData("a/..b", 1, false)]
    [InlineData("a", 1, false)]
    public void SuffixRequiresDirCheck_MatchesGnulib(string name, int end, bool expected)
        => Assert.Equal(expected, SuffixRequiresDirCheck(name, end, windows: false));

    [Fact]
    public void Absolute_JoinsWithoutFoldingDotDot()
    {
        Assert.Equal("/w/dl/..", Absolute("dl/..", "/w", windows: false));
        Assert.Equal("/x", Absolute("/x", "/w", windows: false));
        Assert.Equal(@"C:\w\dl/..", Absolute("dl/..", @"C:\w", windows: true));
        Assert.Equal(@"C:\x", Absolute(@"\x", @"C:\w", windows: true));
        Assert.Equal("D:/x", Absolute("D:/x", @"C:\w", windows: true));
    }

    [Theory]
    [InlineData(@"C:\a", 3)]
    [InlineData("C:/a", 3)]
    [InlineData("C:a", 0)]                       // drive-relative is not rooted
    [InlineData(@"\\srv\share\x", 12)]
    [InlineData("//srv/share", 11)]
    [InlineData("a", 0)]
    public void RootLength_Windows(string path, int expected) => Assert.Equal(expected, RootLength(path, windows: true));

    [Fact]
    public void Windows_DriveAndUncRoots_Walk()
    {
        static Entry WinProbe(string p) => p.Equals(@"C:\d", StringComparison.OrdinalIgnoreCase) ? Entry.Of(Kind.Directory) : Entry.Of(Kind.Missing);
        Assert.Equal(@"C:\d\new", Canonicalize("C:/d/x/../new", Mode.Missing, true, WinProbe, windows: true).Path);
        Assert.Equal(@"C:\", Canonicalize(@"C:\..\..", Mode.AllButLast, true, WinProbe, windows: true).Path);
        Assert.Equal(@"\\srv\share\a", Canonicalize("//srv/share/a/b/..", Mode.Missing, true, WinProbe, windows: true).Path);
    }
}

/// <summary>GNU quotearg shell-escape quoting of a file name in a diagnostic (coreutils 9.4, every printable ASCII probed).</summary>
public class GnuQuoteTests
{
    [Theory]
    [InlineData("nosuch", "nosuch")]
    [InlineData("a#", "a#")]
    [InlineData("-a", "-a")]
    [InlineData("{a}", "{a}")]
    [InlineData("é", "é")]
    [InlineData("", "''")]
    [InlineData("a b", "'a b'")]
    [InlineData("#a", "'#a'")]
    [InlineData("~a", "'~a'")]
    [InlineData("x:y", "'x:y'")]
    [InlineData("a=b", "'a=b'")]
    [InlineData("a\"b", "'a\"b'")]
    [InlineData("it's", "\"it's\"")]
    [InlineData("a'b c", "\"a'b c\"")]
    [InlineData("it's $x", "'it'\\''s $x'")]
    [InlineData("it's\"x", "'it'\\''s\"x'")]
    [InlineData("a\tb", "'a'$'\\t''b'")]
    [InlineData("a\nb", "'a'$'\\n''b'")]
    [InlineData("a'\tb", "'a'\\'''$'\\t''b'")]
    [InlineData("a\u0001b", "'a'$'\\001''b'")]
    public void IfNeeded_QuotesLikeQuotef(string name, string expected) => Assert.Equal(expected, GnuQuote.IfNeeded(name));

    [Theory]
    [InlineData("nosuch", "'nosuch'")]
    [InlineData("it's", "\"it's\"")]
    public void Always_QuotesLikeQuoteaf(string name, string expected) => Assert.Equal(expected, GnuQuote.Always(name));

    [Fact]
    public void DriveColon_IsNotAQuotingCharacter_OnWindowsOnly()
    {
        Assert.Equal(OperatingSystem.IsWindows() ? "C:/tmp/f" : "'C:/tmp/f'", GnuQuote.IfNeeded("C:/tmp/f"));
        Assert.Equal("'C:/a b'", GnuQuote.IfNeeded("C:/a b"));
    }
}
