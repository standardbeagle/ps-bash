using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pathname expansion (the shell's filename-expansion step): the pure <see cref="BashGlob"/> engine and the
/// <c>ConvertTo-BashGlob</c> cmdlet the transpiler emits for every unquoted glob word. Expected values are
/// bash 5.2 in a C.UTF-8 locale (oracle-checked; end to end in <c>PathnameExpansionDifferentialTests</c>).
/// </summary>
public class BashGlobTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;

    public BashGlobTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), "psb-g-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        foreach (var f in new[] { "x", "y.txt", ".hid", "Zed", "apple", Path.Combine("sub", "a1"),
                     Path.Combine("sub", "a[1]"), Path.Combine("sub", "b1"), Path.Combine("sub", ".dot") })
            File.WriteAllText(Path.Combine(_dir, f), "v\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string Glob(string pattern, bool dotGlob = false) =>
        string.Join("|", BashGlob.Expand(pattern, _dir, dotGlob));

    // ───────────── engine ─────────────

    [Theory]
    [InlineData("*", "Zed|apple|sub|x|y.txt")]                 // ordinal (C locale) order, no hidden files
    [InlineData("*.txt", "y.txt")]
    [InlineData("?.txt", "y.txt")]
    [InlineData("[xy]*", "x|y.txt")]
    [InlineData("[!x]*", "Zed|apple|sub|y.txt")]
    [InlineData("[^x]*", "Zed|apple|sub|y.txt")]
    [InlineData("[[:upper:]]*", "Zed")]
    [InlineData("[[:alpha:]]*", "Zed|apple|sub|x|y.txt")]
    [InlineData("[a-s]*", "apple|sub")]
    [InlineData(".*", ".hid")]                                // `.` and `..` are never matched by a glob
    [InlineData("sub/*", "sub/a1|sub/a[1]|sub/b1")]            // names keep the form the pattern was written in
    [InlineData("sub/a?", "sub/a1")]
    [InlineData("*/", "sub/")]                                 // trailing slash: directories only, slash kept
    [InlineData("*/a1", "sub/a1")]
    [InlineData("s*/*1", "sub/a1|sub/b1")]
    [InlineData("./*.txt", "./y.txt")]
    [InlineData("sub/../*.txt", "sub/../y.txt")]
    [InlineData("*.zz", "")]
    [InlineData("nosuch/*", "")]
    [InlineData("sub/x*", "")]
    public void Expand_MatchesLikeBash(string pattern, string expected) =>
        Assert.Equal(expected, Glob(pattern));

    [Fact]
    public void Expand_RepeatedGlobStarListsEachPathOnce() =>
        Assert.Equal("sub/a1|sub/b1",
            string.Join("|", BashGlob.Expand("**/**/*1", _dir, false, globStar: true)
                .Where(x => x.StartsWith("sub/"))));

    [Fact]
    public void Expand_DotGlobIncludesHiddenNamesButNeverDotOrDotDot() =>
        Assert.Equal(".hid|Zed|apple|sub|x|y.txt", Glob("*", dotGlob: true));

    [Fact]
    public void Expand_HiddenNameNeedsALiteralLeadingDotInThePattern()
    {
        Assert.Equal("", Glob("?hid"));
        Assert.Equal("", Glob("[.]hid"));
        Assert.Equal(".hid", Glob(".h*"));
        Assert.Equal("sub/.dot", Glob("sub/.*"));
    }

    [Fact]
    public void Expand_AbsolutePatternGivesAbsolutePaths()
    {
        string root = _dir.Replace('\\', '/');

        Assert.Equal(root + "/y.txt", Glob(root + "/*.txt"));
        Assert.Equal(root + "/sub/a1|" + root + "/sub/a[1]|" + root + "/sub/b1", Glob(root + "/sub/*"));
    }

    /// <summary>
    /// A literal backslash (quoted: <c>'C:\dir\'*.txt</c>, so <c>\\</c> in the pattern) is a separator on Windows: no
    /// file name can hold one. It was matched as a name character, so the word never expanded (and <c>ls</c> then
    /// listed bare names). The matches keep the separators as typed.
    /// </summary>
    [SkippableFact]
    [Trait("Platform", "Windows")]
    public void Expand_LiteralBackslashIsASeparatorOnWindows()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "a backslash is a file name character elsewhere");
        string root = BashGlobText.Escape(_dir);   // C:\\Users\\…  (each backslash literal)

        Assert.Equal(_dir + @"\y.txt", Glob(root + @"\\*.txt"));
        Assert.Equal(@"sub\a1|sub\b1", Glob(@"sub\\?1"));
        Assert.Equal(@"sub\", Glob(@"s*\\"));                       // trailing: directories only, separator kept
        Assert.Equal(@"sub/a1|sub/b1", Glob(@"sub/?1"));            // `/` stays `/`
        Assert.Equal(@"sub\a1|sub\b1", Glob(@"sub\\\\?1"));         // a doubled separator collapses like `//`
        // The drive alone: `C:\` + pattern resolves from the root.
        string drive = _dir[..2];
        Assert.Contains(drive + @"\" + _dir[3..].Split('\\')[0], Glob(drive + @"\\*").Split('|'));
    }

    [Fact]
    public void Expand_EscapedGlobCharactersMatchThemselves()
    {
        // `sub/a\[*` : the bracket is a literal, the star a glob.
        Assert.Equal("sub/a[1]", Glob(@"sub/a\[*"));
        // A quoted star in the middle of a pattern: `a\*` has no glob character left.
        Assert.False(BashGlob.HasPattern(@"a\*"));
        Assert.Equal("a*", BashGlob.Unescape(@"a\*"));
    }

    [Theory]
    [InlineData("*", true)]
    [InlineData("a?c", true)]
    [InlineData("[ab]", true)]
    [InlineData("[a", false)]                                 // an unterminated class is literal
    [InlineData("[]", false)]
    [InlineData(@"\*", false)]
    [InlineData(@"\\*", true)]
    [InlineData("plain", false)]
    public void HasPattern_DetectsUnescapedGlobCharacters(string pattern, bool expected) =>
        Assert.Equal(expected, BashGlob.HasPattern(pattern));

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a*b", @"a\*b")]
    [InlineData("a?b[c]", @"a\?b\[c]")]
    [InlineData(@"a\b", @"a\\b")]
    [InlineData("", "")]
    public void Escape_MakesQuotedTextMatchItself(string text, string expected)
    {
        Assert.Equal(expected, BashGlobText.Escape(text));
        Assert.Equal(text, BashGlob.Unescape(BashGlobText.Escape(text)));
        Assert.False(BashGlob.HasPattern(BashGlobText.Escape(text)));
    }

    // ───────────── cmdlet ─────────────

    private CmdResult Run(string script) => CmdResult.Run(_fixture.AcquireFresh(), script);

    private string InDir(string body) =>
        $"Invoke-BashShopt -u nullglob; Invoke-BashShopt -u failglob; Invoke-BashShopt -u dotglob; " +
        $"Push-Location '{_dir}'; [Environment]::CurrentDirectory = '{_dir}'; try {{ {body} }} finally {{ Pop-Location }}";

    [Fact]
    public void Cmdlet_ExpandsRelativeAndSorted()
    {
        var r = Run(InDir("ConvertTo-BashGlob '*'")).AssertSuccess();

        Assert.Equal(new[] { "Zed", "apple", "sub", "x", "y.txt" }, r.Lines);
    }

    [Fact]
    public void Cmdlet_NoMatchKeepsTheLiteralWithEscapesRemoved()
    {
        var r = Run(InDir("ConvertTo-BashGlob '*.zz'; ConvertTo-BashGlob 'a\\*b*'")).AssertSuccess();

        Assert.Equal(new[] { "*.zz", "a*b*" }, r.Lines);
    }

    [Fact]
    public void Cmdlet_NullglobGivesNoWord_DotglobShowsHidden()
    {
        var r = Run(InDir(
            "Invoke-BashShopt -s nullglob; @(ConvertTo-BashGlob '*.zz').Count; " +
            "Invoke-BashShopt -s dotglob; ConvertTo-BashGlob '.*'; ConvertTo-BashGlob '*'")).AssertSuccess();

        Assert.Equal(new[] { "0", ".hid", ".hid", "Zed", "apple", "sub", "x", "y.txt" }, r.Lines);
    }

    [Fact]
    public void Cmdlet_Failglob_ErrorsWithStatus1AndNoOutput()
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript(InDir("$global:LASTEXITCODE = 0; Invoke-BashShopt -s failglob; " +
            "try { ConvertTo-BashGlob '*.zz' } catch { $_.Exception.Message }; $global:LASTEXITCODE"));
        var output = pwsh.Invoke().Select(o => o?.ToString() ?? "").ToList();
        pwsh.Commands.Clear();

        Assert.Equal(new[] { "bash: no match: *.zz", "1" }, output);
    }

    [Fact]
    public void Cmdlet_MatchedNameThatLooksLikeAClassIsReturnedAsIs()
    {
        var r = Run(InDir("ConvertTo-BashGlob 'sub/a*'")).AssertSuccess();

        Assert.Equal(new[] { "sub/a1", "sub/a[1]" }, r.Lines);
    }

    /// <summary>What the transpiler emits for <c>ls 'C:\dir\'*.txt</c>: the quoted prefix escaped, each backslash doubled.</summary>
    [SkippableFact]
    [Trait("Platform", "Windows")]
    public void Cmdlet_QuotedWindowsDirectoryPrefixExpands()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "a backslash is a file name character elsewhere");
        string pattern = BashGlobText.Escape(_dir + @"\") + "*.txt";

        var r = Run(InDir($"ConvertTo-BashGlob '{pattern}'; ConvertTo-BashGlob '{BashGlobText.Escape(_dir + @"\")}*.zz'")).AssertSuccess();

        Assert.Equal(new[] { _dir + @"\y.txt", _dir + @"\*.zz" }, r.Lines);
    }

    [Fact]
    public void Cmdlet_ShoptStateIsPerRunspace()
    {
        InvokeBashShoptCommand.ResetForTests();
        var r = Run("Invoke-BashShopt -s nullglob; Invoke-BashShopt nullglob").AssertSuccess();
        Assert.Equal("nullglob       \ton", Assert.Single(r.Lines));

        // another session (runspace) starts from the defaults; one command's `shopt -s` must not leak.
        using var other = PwshTestFixture.Create();
        var r2 = CmdResult.Run(other, "Invoke-BashShopt nullglob");
        Assert.Equal("nullglob       \toff", Assert.Single(r2.Lines));
    }
}
