using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The one sed delimited-text scanner (<see cref="SedDelimited"/>, GNU 4.9 <c>match_slash</c>) and what the parser builds
/// from it. Oracle: GNU sed 4.9 under bash 5.2 (beagle-ab); every expectation below was read off it. REGRESSION: an
/// address regex took the first <c>/</c> as its end (<c>/a&lt;\/c&gt;/d</c> compiled <c>a&lt;\</c> and failed as
/// <c>can't read f: Invalid pattern</c>), <c>\cREc</c> addresses were "unknown command: `\'", and a delimiter inside
/// <c>[...]</c> ended an <c>s</c> regex.
/// </summary>
public class SedDelimitedTests
{
    // ---- the scanner ----------------------------------------------------------------------------------

    [Fact]
    public void Scan_EscapedDelimiter_IsTheDelimiter()
    {
        var f = SedDelimited.Scan(@"a<\/c>/d", 0, '/', regex: true);
        Assert.Equal("a</c>", f.Text);
        Assert.True(f.Closed);
        Assert.Equal(7, f.End);
    }

    [Fact]
    public void Scan_EscapedCustomDelimiter_KeepsItsRegexMeaning()
    {
        // `\|` under `|` is a bare `|` (literal in a BRE, alternation in an ERE); `\.` under `.` is any character.
        Assert.Equal("d|e", SedDelimited.Scan(@"d\|e|X|", 0, '|', true).Text);
        Assert.Equal("a.b", SedDelimited.Scan(@"a\.b.X.", 0, '.', true).Text);
    }

    [Fact]
    public void Scan_DelimiterInsideBracket_DoesNotEndTheRegex()
    {
        var f = SedDelimited.Scan("[/]/X/", 0, '/', regex: true);
        Assert.Equal("[/]", f.Text);
        Assert.Equal(4, f.End);
    }

    [Theory]
    [InlineData(@"[\/]/p", @"[\/]")]          // backslash in a bracket is a member, not an escape
    [InlineData(@"[\]/p", @"[\]")]            // `[\]` is a complete bracket (backslash)
    [InlineData("[]/]/p", "[]/]")]            // a leading `]` is a member
    [InlineData("[^]/]/p", "[^]/]")]
    [InlineData("[[:alpha:]/]b/p", "[[:alpha:]/]b")]
    [InlineData("[[.-.]/]/p", "[[.-.]/]")]
    [InlineData("[[=a=]/]/p", "[[=a=]/]")]
    public void Scan_BracketExpression_IsCopiedAsItStands(string s, string text)
    {
        var f = SedDelimited.Scan(s, 0, '/', regex: true);
        Assert.True(f.Closed);
        Assert.Equal(text, f.Text);
    }

    [Fact]
    public void Scan_BracketKeepsAnEscapedCustomDelimiter()
    {
        // `\|[\|]|p`: inside the bracket `\|` is backslash and bar (GNU matched `c\d` and `g|h`).
        Assert.Equal(@"[\|]", SedDelimited.Scan(@"[\|]|p", 0, '|', true).Text);
    }

    [Fact]
    public void Scan_ReplacementAndYStrings_HaveNoBrackets()
    {
        var f = SedDelimited.Scan("[/]/", 0, '/', regex: false);
        Assert.Equal("[", f.Text);
        Assert.Equal(2, f.End);
    }

    [Fact]
    public void Scan_NewlineEscape_StaysForTheTranslator_EvenWhenNIsTheDelimiter()
    {
        Assert.Equal(@"a\nb", SedDelimited.Scan(@"a\nb/", 0, '/', true).Text);
        Assert.Equal(@"a\nb", SedDelimited.Scan(@"a\nbn", 0, 'n', true).Text);   // `\na\nbn`: \n is the newline
        Assert.Equal("anb", SedDelimited.Scan(@"a\nbn", 0, 'n', false).Text);    // in a replacement it is the delimiter
        Assert.Equal(@"a\\n", SedDelimited.Scan(@"a\\n/", 0, '/', true).Text);   // an escaped backslash, then n
    }

    [Fact]
    public void Scan_BackslashNewline_IsANewline()
    {
        Assert.Equal("a\n", SedDelimited.Scan("a\\\n/p", 0, '/', true).Text);
    }

    [Fact]
    public void Scan_EscapedAmpersandDelimiter_StaysEscapedInAReplacement()
    {
        Assert.Equal(@"[\&]", SedDelimited.Scan(@"[\&]&", 0, '&', regex: false).Text);
        Assert.Equal("x&y", SedDelimited.Scan(@"x\&y&", 0, '&', regex: true).Text);
    }

    [Theory]
    [InlineData("abc", 3)]          // end of script
    [InlineData("abc\np", 3)]       // an unescaped newline ends it
    [InlineData("a[\n]/p", 2)]      // inside a bracket too
    [InlineData("a[/;p", 5)]        // an open bracket swallows the delimiter
    [InlineData("a\\", 2)]          // a trailing backslash
    public void Scan_Unterminated_StopsWhereGnuDoes(string s, int stop)
    {
        var f = SedDelimited.Scan(s, 0, '/', regex: true);
        Assert.False(f.Closed);
        Assert.Equal(stop, f.End);
    }

    [Theory]
    [InlineData(@"/a<\/c>/d", "a</c>", false, false, 8)]
    [InlineData(@"\,a</c>,p", "a</c>", false, false, 8)]
    [InlineData(@"\|d\|e|p", "d|e", false, false, 7)]
    [InlineData("/ab/Ip", "ab", true, false, 5)]
    [InlineData("/ab/ I p", "ab", true, false, 7)]
    [InlineData("/ab/IM,/c/p", "ab", true, true, 6)]
    [InlineData("/ab/Mp", "ab", false, true, 5)]
    [InlineData(@"\na/bnp", "a/b", false, false, 6)]
    public void ScanRegexAddress_ReadsRegexAndFlags(string s, string pattern, bool icase, bool multi, int end)
    {
        var a = SedDelimited.ScanRegexAddress(s, 0);
        Assert.True(a.Closed);
        Assert.Equal(pattern, a.Pattern);
        Assert.Equal(icase, a.IgnoreCase);
        Assert.Equal(multi, a.Multiline);
        Assert.Equal(end, a.End);
    }

    [Theory]
    [InlineData("/abc", 4)]
    [InlineData(@"\,abc", 5)]
    [InlineData(@"\", 1)]
    public void ScanRegexAddress_Unterminated(string s, int stop)
    {
        var a = SedDelimited.ScanRegexAddress(s, 0);
        Assert.False(a.Closed);
        Assert.Equal(stop, a.End);
    }

    [Fact]
    public void ScanCommandFields_SubstRegexHonoursBrackets_ReplacementDoesNot()
    {
        Assert.True(SedDelimited.ScanCommandFields("s/[/]/X/g", 0, out var re, out var repl));
        Assert.Equal("[/]", re.Text);
        Assert.Equal("X", repl.Text);
        Assert.Equal(8, repl.End);

        // y has no regex: `y/[/]/` is source `[`, dest `]`.
        Assert.True(SedDelimited.ScanCommandFields("y/[/]/", 0, out var src, out var dst));
        Assert.Equal("[", src.Text);
        Assert.Equal("]", dst.Text);
    }

    // ---- the splitter agrees with the parser ----------------------------------------------------------

    [Theory]
    [InlineData("/[;]/d;p", new[] { "/[;]/d", "p" })]
    [InlineData(@"\;a\;b;d;p", new[] { @"\;a\;b;d", "p" })]
    [InlineData("s/[;]/X/;p", new[] { "s/[;]/X/", "p" })]
    [InlineData(@"\,a,,\,c,p;=", new[] { @"\,a,,\,c,p", "=" })]
    [InlineData("/x/I{p;d}", new[] { "/x/I{", "p", "d", "}" })]
    [InlineData("s/a/b/#c;p\n=", new[] { "s/a/b/#c;p", "=" })]
    public void SplitSedCommands_CutsWhereTheScannerSaysFieldsEnd(string script, string[] parts)
    {
        Assert.Equal(parts, InvokeBashSedCommand.SplitSedCommands(script));
    }

    // ---- matching ------------------------------------------------------------------------------------

    private static List<string> Sed(string script, bool quiet, params string[] input)
    {
        Assert.True(InvokeBashSedCommand.BuildProgram(new[] { script }, false, out var cmds, out var err, out _), err);
        return SedEngine.ProcessLines(input, cmds, quiet);
    }

    [Fact]
    public void Address_EscapedDelimiter_Matches()
        => Assert.Equal(new[] { "x", "y" }, Sed(@"/a<\/c>/d", false, "x", "a</c>", "y"));

    [Fact]
    public void Address_CustomDelimiter_Matches()
    {
        Assert.Equal(new[] { "a</c>" }, Sed(@"\,a</c>,p", true, "x", "a</c>", "y"));
        Assert.Equal(new[] { "a/b", "c" }, Sed(@"\,a/b,,\|c|p", true, "a/b", "c", "d"));
        Assert.Equal(new[] { "a/b", "c" }, Sed(@"/a\/b/,/c/p", true, "a/b", "c", "d"));
    }

    [Fact]
    public void Address_IgnoreCaseAndMultilineFlags()
    {
        Assert.Equal(new[] { "AB", "ab" }, Sed("/ab/Ip", true, "a/b", "AB", "ab"));
        Assert.Equal(new[] { "AB", "ab", "c" }, Sed("/ab/I,/C/Ip", true, "x", "AB", "ab", "c", "d"));
        Assert.Equal(new[] { "a\nb" }, Sed("N;/^b/Mp", true, "a", "b"));
        Assert.Empty(Sed("N;/^b/p", true, "a", "b"));
        // The address sees the whole pattern space, not its first line.
        Assert.Equal(new[] { "a\nb" }, Sed("N;/b/p", true, "a", "b"));
    }

    [Fact]
    public void Subst_DelimiterInBracket_AndEscapedAmpersandDelimiter()
    {
        Assert.Equal(new[] { "aXb" }, Sed("s/[/]/X/", false, "a/b"));
        Assert.Equal(new[] { "[&]/b" }, Sed(@"s&a&[\&]&", false, "a/b"));
        Assert.Equal(new[] { "X" }, Sed(@"s|d\|e|X|", false, "d|e"));
    }

    [Fact]
    public void Transliterate_EscapesAndDelimiter()
    {
        Assert.Equal(new[] { "a|b" }, Sed(@"y/\//|/", false, "a/b"));
        Assert.Equal(new[] { "Z", @"cZd" }, Sed(@"y/q\\/ZZ/", false, "q", @"c\d"));
    }

    // ---- script errors: GNU's words, GNU's char N, status 1 -------------------------------------------

    [Theory]
    [InlineData(@"/a\(/d", @"sed: -e expression #1, char 5: Unmatched ( or \(")]
    [InlineData(@"/a\(/,/b/d", @"sed: -e expression #1, char 5: Unmatched ( or \(")]
    [InlineData(@"1,/a\(/d", @"sed: -e expression #1, char 7: Unmatched ( or \(")]
    [InlineData(@"/a\(/I p", @"sed: -e expression #1, char 7: Unmatched ( or \(")]
    [InlineData(@"/a/,/b\(/p", @"sed: -e expression #1, char 9: Unmatched ( or \(")]
    [InlineData(@"2{/a\(/d}", @"sed: -e expression #1, char 7: Unmatched ( or \(")]
    [InlineData(@"/a/d;/b\(/d", @"sed: -e expression #1, char 10: Unmatched ( or \(")]
    [InlineData("/a/d\n/b\\(/d", @"sed: -e expression #1, char 10: Unmatched ( or \(")]
    [InlineData(@"/x\{2,1\}/d", @"sed: -e expression #1, char 10: Invalid content of \{\}")]
    [InlineData(@"/\(a\)\2/d", "sed: -e expression #1, char 9: Invalid back reference")]
    [InlineData("/abc", "sed: -e expression #1, char 4: unterminated address regex")]
    [InlineData("/abc\np", "sed: -e expression #1, char 4: unterminated address regex")]
    [InlineData(@"\,abc", "sed: -e expression #1, char 5: unterminated address regex")]
    [InlineData("/a[/;p", "sed: -e expression #1, char 6: unterminated address regex")]
    [InlineData(@"s/a\(/x/", @"sed: -e expression #1, char 8: Unmatched ( or \(")]
    [InlineData(@"s/a\)/x/", @"sed: -e expression #1, char 8: Unmatched ) or \)")]
    [InlineData(@"s/a\(/x/;s/b/c/", @"sed: -e expression #1, char 9: Unmatched ( or \(")]
    [InlineData("s/a\\(/x/\np", @"sed: -e expression #1, char 9: Unmatched ( or \(")]
    [InlineData(@"1{s/a\(/x/}", @"sed: -e expression #1, char 10: Unmatched ( or \(")]
    [InlineData(@"s/a\(/x/gp", @"sed: -e expression #1, char 10: Unmatched ( or \(")]
    [InlineData(@"s/a/b/;/c\(/!d", @"sed: -e expression #1, char 12: Unmatched ( or \(")]
    [InlineData(@"s/a/\1/", @"sed: -e expression #1, char 7: invalid reference \1 on `s' command's RHS")]
    [InlineData(@"s/\(a\)/\2/", @"sed: -e expression #1, char 11: invalid reference \2 on `s' command's RHS")]
    [InlineData("s/a/b", "sed: -e expression #1, char 5: unterminated `s' command")]
    [InlineData("s/a", "sed: -e expression #1, char 3: unterminated `s' command")]
    [InlineData("s/[/]/X", "sed: -e expression #1, char 7: unterminated `s' command")]
    [InlineData("s/a/b/gx", "sed: -e expression #1, char 8: unknown option to `s'")]
    [InlineData("s/[/]/[/]/", "sed: -e expression #1, char 9: unknown option to `s'")]
    [InlineData("y/abc/xyz", "sed: -e expression #1, char 9: unterminated `y' command")]
    [InlineData("y/ab/x/", "sed: -e expression #1, char 7: strings for `y' command are different lengths")]
    [InlineData("y/[/]/]/", "sed: -e expression #1, char 7: extra characters after command")]
    [InlineData("/a/Xd", "sed: -e expression #1, char 4: unknown command: `X'")]
    // s flags: each at most once, a number not zero.
    [InlineData("s/a/b/gg", "sed: -e expression #1, char 8: multiple `g' options to `s' command")]
    [InlineData("s/a/b/pp", "sed: -e expression #1, char 8: multiple `p' options to `s' command")]
    [InlineData("s/a/b/2g3", "sed: -e expression #1, char 9: multiple number options to `s' command")]
    [InlineData("s/a/b/0", "sed: -e expression #1, char 7: number option to `s' command may not be zero")]
    // a missing file name or label: past the command (and the newline that ends it).
    [InlineData("r", "sed: -e expression #1, char 1: missing filename in r/R/w/W commands")]
    [InlineData("p;r", "sed: -e expression #1, char 3: missing filename in r/R/w/W commands")]
    [InlineData("s/a/b/w", "sed: -e expression #1, char 7: missing filename in r/R/w/W commands")]
    [InlineData(":", "sed: -e expression #1, char 1: \":\" lacks a label")]
    [InlineData("p;:", "sed: -e expression #1, char 3: \":\" lacks a label")]
    // the empty regex takes no flags of its own.
    [InlineData("/o/Ip;//Ip", "sed: -e expression #1, char 9: cannot specify modifiers on empty regexp")]
    [InlineData("s/o/0/;s//1/I", "sed: -e expression #1, char 13: cannot specify modifiers on empty regexp")]
    // braces
    [InlineData("s/a/b/;}", "sed: -e expression #1, char 8: unexpected `}'")]
    [InlineData("{p", "sed: -e expression #1, char 0: unmatched `{'")]
    // regcomp's own messages
    [InlineData(@"s/a\{1/x/", @"sed: -e expression #1, char 9: Unmatched \{")]
    [InlineData("s/[[:foo:]]/x/", "sed: -e expression #1, char 14: Invalid character class name")]
    [InlineData("/a**/p", "sed: -e expression #1, char 5: Invalid preceding regular expression")]
    public void BuildProgram_ScriptError_IsGnusMessageAtGnusChar(string script, string message)
    {
        Assert.False(InvokeBashSedCommand.BuildProgram(new[] { script }, false, out _, out var err, out int code));
        Assert.Equal(message, err);
        Assert.Equal(1, code);
    }

    [Fact]
    public void BuildProgram_ErrorInSecondExpression_NamesIt()
    {
        Assert.False(InvokeBashSedCommand.BuildProgram(new[] { "/a/d", @"/b\(/d" }, false, out _, out var err, out _));
        Assert.Equal(@"sed: -e expression #2, char 5: Unmatched ( or \(", err);
    }

    [Fact]
    public void BuildProgram_ExtendedRegex_UnmatchedParen()
    {
        Assert.False(InvokeBashSedCommand.BuildProgram(new[] { "/a(/d" }, true, out _, out var err, out _));
        Assert.Equal(@"sed: -e expression #1, char 4: Unmatched ( or \(", err);
    }

    // ---- where an error is: the -e expression it is in, or the line of the -f file -------------------

    private static string ScriptError(params InvokeBashSedCommand.SedScript[] scripts)
    {
        Assert.False(InvokeBashSedCommand.BuildProgram(scripts, false, out _, out var err, out int code));
        Assert.Equal(1, code);
        return err!;
    }

    [Fact]
    public void BuildProgram_ErrorInAFile_IsItsLine()
    {
        Assert.Equal("sed: file f2 line 3: missing filename in r/R/w/W commands",
            ScriptError(new InvokeBashSedCommand.SedScript("p\ns/a/b/w\n", File: "f2")));
        Assert.Equal("sed: file f5 line 2: unknown command: `k'", ScriptError(new InvokeBashSedCommand.SedScript("p\nk", File: "f5")));
        Assert.Equal("sed: file f3 line 2: unmatched `{'", ScriptError(new InvokeBashSedCommand.SedScript("p\n{\np\n", File: "f3")));
        // a leading `#n` line was taken off: the script's first line is line 2
        Assert.Equal("sed: file f4 line 2: unknown command: `k'", ScriptError(new InvokeBashSedCommand.SedScript("k\n", File: "f4", FirstLine: 2)));
    }

    [Fact]
    public void BuildProgram_BraceErrors_NameTheirOwnExpression()
    {
        Assert.Equal("sed: -e expression #2, char 1: unexpected `}'", ScriptError(new("p", ExprNo: 1), new("}", ExprNo: 2)));
        Assert.Equal("sed: -e expression #2, char 3: unexpected `}'", ScriptError(new("p", ExprNo: 1), new("p;}", ExprNo: 2)));
        Assert.Equal("sed: -e expression #1, char 0: unmatched `{'", ScriptError(new("{p", ExprNo: 1), new("p", ExprNo: 2)));
    }

    [Fact]
    public void BuildProgram_EmptyRegex_LearnsGnusNoPreviousMessage_FromTheLastScript()
    {
        Assert.True(InvokeBashSedCommand.BuildProgram(
            new InvokeBashSedCommand.SedScript[] { new("p\n\n", File: "f6"), new("//p", ExprNo: 1) },
            false, out var cmds, out _, out _));
        Assert.Equal("sed: -e expression #1, char 0: no previous regular expression", cmds[^1].EmptyRegexError);

        Assert.True(InvokeBashSedCommand.BuildProgram(
            new InvokeBashSedCommand.SedScript[] { new("//p", ExprNo: 1), new("p\n\n", File: "f6") },
            false, out cmds, out _, out _));
        Assert.Equal("sed: file f6 line 3: no previous regular expression", cmds[0].EmptyRegexError);
    }

    // ---- the engine: line numbers after N, the multi-line buffer, the last regex ---------------------

    [Fact]
    public void Engine_LineNumberAfterN_IsThePulledLine()
    {
        Assert.Empty(Sed("1~2N;1p", true, "a", "b", "c", "d"));
        Assert.Equal(new[] { "c\nd" }, Sed("N;4p", true, "a", "b", "c", "d"));
    }

    [Fact]
    public void Engine_MultiLineBuffer_DotMatchesNewline_DollarIsItsEnd()
    {
        Assert.Equal(new[] { "a\nb" }, Sed("N;/a.b/p", true, "a", "b"));
        Assert.Empty(Sed("N;/a$/p", true, "a", "b"));
        Assert.Equal(new[] { "a\nb" }, Sed("N;/a$/Mp", true, "a", "b"));
        Assert.Equal(new[] { "aXb" }, Sed(@"N;s/a.b/aXb/", false, "a", "b"));
    }

    [Fact]
    public void Engine_EmptyRegex_IsTheLastRegexUsed()
    {
        Assert.Equal(new[] { "X" }, Sed("/foo/s//X/", false, "foo"));
        Assert.Equal(new[] { "f[<o>]o" }, Sed(@"s/\(o\)/[\1]/;s//<\1>/", false, "foo"));
        Assert.Equal(new[] { "a", "b" }, Sed("/a/,//p", true, "a", "b"));
        Assert.Equal(new[] { "Xb" }, Sed("/a/Is//X/p", true, "Ab"));
    }

    [Fact]
    public void Engine_RegexRangeEnd_IsLookedForFromTheNextLine()
    {
        Assert.Equal(new[] { "a", "b", "a" }, Sed("/a/,/a/p", true, "a", "b", "a", "c"));
        Assert.Equal(new[] { "a" }, Sed("/a/,1p", true, "a", "b"));
    }

    [Theory]
    [InlineData("2 ,3p")]
    [InlineData("2\t,3p")]
    [InlineData("/b/ ,3p")]
    public void Address_BlanksBeforeTheComma(string script)
        => Assert.Equal(new[] { "b", "c" }, Sed(script, true, "a", "b", "c", "d"));

    [Fact]
    public void Transliterate_CharacterEscapes()
    {
        Assert.Equal(new[] { "a\tc" }, Sed(@"y/b/\t/", false, "abc"));
        Assert.Equal(new[] { "ABC" }, Sed(@"y/abc/\x41\x42\x43/", false, "abc"));
    }
}
