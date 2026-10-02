using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// grep options that used to be refused (exit 2): <c>-z -Z -a -I --binary-files -b -u -d -D -T --label
/// --group-separator --no-group-separator --color=always -P</c>, plus the binary-file notice and the
/// per-file / -o / -r details found on the way. Oracle: GNU grep 3.11 (`wsl bash`, UTF-8 locale); every
/// expected value below was read from it. Records are compared as BashText (an unterminated
/// <c>-z</c> record keeps its NUL).
/// </summary>
[Collection("PsBashSearchEnv")]
public class GrepOptionsGnuTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private const string Esc = "\u001b";
    private static string Sgr(string c) => $"{Esc}[{c}m{Esc}[K";
    private const string End = Esc + "[m" + Esc + "[K";
    private static string Col(string c, string t) => Sgr(c) + t + End;
    private static string Match(string t) => Col("01;31", t);
    private static string Sep(string t) => Col("36", t);

    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;

    public GrepOptionsGnuTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), "psbash-grepo-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        Environment.SetEnvironmentVariable("GREP_COLORS", null);
        Environment.SetEnvironmentVariable("GREP_COLOR", null);
        Environment.SetEnvironmentVariable("PSBASH_GREP_TTY", null);
        Environment.SetEnvironmentVariable("TERM", null);
    }

    private sealed record R(string[] Lines, string Stderr, int Exit);

    private R Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.Commands.Clear();
        pwsh.Streams.ClearStreams();
        pwsh.AddScript("$global:LASTEXITCODE = $null").Invoke();
        pwsh.Commands.Clear();
        var output = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        var errors = pwsh.Streams.Error.Select(e => e.ToString()).ToArray();
        pwsh.Streams.ClearStreams();
        var exit = pwsh.AddScript("$global:LASTEXITCODE").Invoke();
        pwsh.Commands.Clear();
        int code = exit.Count > 0 && exit[0]?.BaseObject is { } o ? Convert.ToInt32(o) : 0;
        var lines = output.Select(x => x?.Properties["BashText"]?.Value as string ?? x?.ToString() ?? "").ToArray();
        return new R(lines, string.Join("\n", errors), code);
    }

    private string Path_(string name) => Path.Combine(_dir, name);

    private string F(string name, string content)
    {
        File.WriteAllText(Path_(name), content);
        return "'" + Path_(name).Replace("'", "''") + "'";
    }

    private string FB(string name, params byte[] bytes)
    {
        File.WriteAllBytes(Path_(name), bytes);
        return "'" + Path_(name).Replace("'", "''") + "'";
    }

    private static byte[] B(string s) => System.Text.Encoding.Latin1.GetBytes(s);

    private string BinFile() => FB("bin.dat", B("abc\nfoo\0bar\nfoo2\n"));

    private R G(string args, string files) => Run($"Invoke-BashGrep {args} {files}");

    // ---- binary files -------------------------------------------------------------------------

    [Fact]
    public void Binary_Match_PrintsNoticeOnStderr_Exit0()
    {
        var bin = BinFile();
        var r = G("'foo'", bin);
        Assert.Empty(r.Lines);
        Assert.Equal(0, r.Exit);
        Assert.Contains("bin.dat: binary file matches", r.Stderr);
        Assert.StartsWith("grep: ", r.Stderr);
    }

    [Theory]
    [InlineData("'-o' 'foo'")]
    [InlineData("'-n' 'foo'")]
    [InlineData("'-v' 'zzz'")]
    [InlineData("'-H' 'foo'")]
    [InlineData("'-s' 'foo'")]       // -s does not silence the notice
    [InlineData("'--binary-files=binary' 'foo'")]
    public void Binary_Match_AnyOutputMode_GivesTheNoticeOnly(string args)
    {
        var r = G(args, BinFile());
        Assert.Empty(r.Lines);
        Assert.Equal(0, r.Exit);
        Assert.Contains("binary file matches", r.Stderr);
    }

    [Fact]
    public void Binary_NoMatch_IsSilentExit1()
    {
        var r = G("'zzz'", BinFile());
        Assert.Empty(r.Lines);
        Assert.Equal(1, r.Exit);
        Assert.Equal("", r.Stderr);
    }

    [Fact]
    public void Binary_CountListQuiet_AreUnaffected()
    {
        var bin = BinFile();
        var c = G("'-c' 'foo'", bin);
        Assert.Equal(new[] { "2" }, c.Lines);
        Assert.Equal("", c.Stderr);
        var l = G("'-l' 'foo'", bin);
        Assert.Equal(new[] { Path_("bin.dat") }, l.Lines);
        Assert.Equal("", l.Stderr);
        var L = G("'-L' 'zzz'", bin);
        Assert.Equal(new[] { Path_("bin.dat") }, L.Lines);
        var q = G("'-q' 'foo'", bin);
        Assert.Empty(q.Lines);
        Assert.Equal(0, q.Exit);
        Assert.Equal("", q.Stderr);
    }

    [Theory]
    [InlineData("'-a'")]
    [InlineData("'--text'")]
    [InlineData("'--binary-files=text'")]
    public void Binary_TextMode_PrintsTheLinesRaw(string args)
    {
        var r = G(args + " 'foo'", BinFile());
        Assert.Equal(new[] { "foo\0bar", "foo2" }, r.Lines);
        Assert.Equal("", r.Stderr);
        Assert.Equal(0, r.Exit);
    }

    [Theory]
    [InlineData("'-I'")]
    [InlineData("'--binary-files=without-match'")]
    public void Binary_WithoutMatch_TreatsTheFileAsNonMatching(string args)
    {
        var bin = BinFile();
        var t = F("t.txt", "foo\n");
        var r = G(args + " 'foo'", bin);
        Assert.Empty(r.Lines);
        Assert.Equal(1, r.Exit);
        Assert.Equal("", r.Stderr);

        // counted as 0 (-c) / listed as a non-matching file (-L), never listed by -l
        var c = G(args + " '-c' 'foo'", bin + " " + t);
        Assert.Equal(new[] { $"{Path_("bin.dat")}:0", $"{Path_("t.txt")}:1" }, c.Lines);
        Assert.Empty(G(args + " '-l' 'foo'", bin).Lines);
        Assert.Equal(new[] { Path_("bin.dat") }, G(args + " '-L' 'foo'", bin).Lines);
    }

    [Fact]
    public void Binary_UnknownType_IsAUsageError()
    {
        var r = G("'--binary-files=bogus' 'foo'", BinFile());
        Assert.Equal(2, r.Exit);
        Assert.Contains("grep: unknown binary-files type", r.Stderr);
        Assert.Equal(2, G("'--binary=text' 'foo'", BinFile()).Exit);   // --binary takes no value
    }

    [Fact]
    public void Binary_StdinPipe_NamesStandardInput()
    {
        var r = Run("\"a`0b`nfoo\" | Invoke-BashGrep 'foo'");
        Assert.Empty(r.Lines);
        Assert.Equal(0, r.Exit);
        Assert.Contains("(standard input): binary file matches", r.Stderr);
        Assert.Equal(new[] { "foo" }, Run("\"a`0b`nfoo\" | Invoke-BashGrep '-a' 'foo'").Lines);
    }

    [Fact]
    public void EncodingErrors_AreText_IntentionalDifference()
    {
        // INTENTIONAL DIFFERENCE: GNU grep in a UTF-8 locale withholds a line holding an invalid byte and
        // reports `binary file matches`; ps-bash carries invalid bytes as escaped-byte markers through every
        // line tool (RawBytes) and treats them as text, so only a NUL makes a file binary.
        var lat2 = FB("lat2.txt", B("plain hello\nété hello\nplain hello\n"));
        var r = G("'hello'", lat2);
        Assert.Equal(new[] { "plain hello", "\uDCE9t\uDCE9 hello", "plain hello" }, r.Lines);
        Assert.Equal(0, r.Exit);
        Assert.Equal("", r.Stderr);
    }

    // ---- -z / -Z -------------------------------------------------------------------------------

    [Fact]
    public void NullData_RecordsAreNulTerminated()
    {
        var z = FB("z.dat", B("a\0b\0hello\0"));
        Assert.Equal(new[] { "hello\0" }, G("'-z' 'hello'", z).Lines);
        Assert.Equal(new[] { "1" }, G("'-zc' 'hello'", z).Lines);
        Assert.Equal(new[] { "3:hello\0" }, G("'-zn' 'hello'", z).Lines);
        Assert.Equal(new[] { "b\0" }, G("'-az' 'b'", z).Lines);
    }

    [Fact]
    public void NullData_RecordSpansLines_AnchorsAreRecordWide_DotMatchesNewline()
    {
        var z = FB("z2.dat", B("one\ntwo\0three\0"));
        Assert.Equal(new[] { "1:one\ntwo\0", "2:three\0" }, G("'-zn' 't'", z).Lines);
        Assert.Empty(G("'-z' '^two'", z).Lines);          // ^ / $ anchor the RECORD, not its lines
        Assert.Empty(G("'-z' 'one$'", z).Lines);
        Assert.Equal(new[] { "one\ntwo\0" }, G("'-z' 'one.two'", z).Lines);   // . matches the newline
        Assert.Equal(new[] { "1" }, G("'-zcv' 'three'", z).Lines);
        Assert.Equal(new[] { "two\0", "three\0" }, G("'-zo' 't[a-z]*'", z).Lines);
    }

    [Fact]
    public void NullData_LastRecordWithoutNul_GetsOne_AndOffsets()
    {
        var z = FB("z3.dat", B("a\0hello"));
        Assert.Equal(new[] { "hello\0" }, G("'-z' 'hello'", z).Lines);
        var b = FB("z4.dat", B("a\0bb\0c\0"));
        Assert.Equal(new[] { "2:bb\0", "5:c\0" }, G("'-zb' '-e' 'b' '-e' 'c'", b).Lines);
    }

    [Fact]
    public void NullData_FromPipeline_RecutsOnNul()
    {
        var r = Run("\"one`ntwo`0three`0\" | Invoke-BashGrep '-zn' 't'");
        Assert.Equal(new[] { "1:one\ntwo\0", "2:three\0" }, r.Lines);
    }

    [Fact]
    public void NullData_ContextAndSeparatorUseNewlineSeparator()
    {
        var z = FB("z5.dat", B("a\0b\0c\0d\0"));
        Assert.Equal(new[] { "a\0", "b\0" }, G("'-zA1' 'a'", z).Lines);
        var z6 = FB("z6.dat", B("a\0b\0c\0a\0"));
        Assert.Equal(new[] { "a\0", "--\n", "a\0" }, G("'-z' '-A0' 'a'", z6).Lines);
    }

    [Fact]
    public void NullName_ReplacesTheSeparatorAfterTheName()
    {
        var s = F("s.txt", "hello\nworld\nhello again\n");
        var name = Path_("s.txt");
        Assert.Equal(new[] { name + "\0", }, G("'-Z' '-l'", " 'hello' " + s).Lines);
        Assert.Equal(new[] { name + "\0" }, G("'-Z' '-L'", " 'zzz' " + s).Lines);
        Assert.Equal(new[] { name + "\0hello", name + "\0hello again" }, G("'-Z' '-H'", " 'hello' " + s).Lines);
        Assert.Equal(new[] { name + "\02" }, G("'-ZH' '-c'", " 'hello' " + s).Lines);
        Assert.Equal(new[] { "2" }, G("'--null' '-c'", " 'hello' " + s).Lines);   // no name shown: nothing to terminate
        Assert.Equal(new[] { "1:hello", "3:hello again" }, G("'-Zn'", " 'hello' " + s).Lines);
        var pipe = Run("'a' | Invoke-BashGrep '-zZ' '-H' 'a'");
        Assert.Equal(new[] { "(standard input)\0a\n\0" }, pipe.Lines);   // the record is the whole stream "a\n"
    }

    // ---- -b / -u -------------------------------------------------------------------------------

    [Fact]
    public void ByteOffset_IsTheLineStart_OrTheMatchStartWithO()
    {
        var t = F("t.txt", "hello\nworld\nhello again\n");
        Assert.Equal(new[] { "0:hello", "12:hello again" }, G("'-b' 'hello'", t).Lines);
        Assert.Equal(new[] { "0:hello", "12:hello" }, G("'-bo' 'hello'", t).Lines);
        Assert.Equal(new[] { "2:l", "3:l", "9:l", "14:l", "15:l" }, G("'-b' '-o' 'l'", t).Lines);
        Assert.Equal(new[] { "1:0:hello", "3:12:hello again" }, G("'-bn' 'hello'", t).Lines);
        Assert.Equal(new[] { "6:world" }, G("'-bv' 'hello'", t).Lines);
        Assert.Equal(new[] { "5:hello" }, G("'-b' '-o' '-B1' 'hello'", F("t2.txt", "a\nxx hello\n")).Lines);
        Assert.Equal(new[] { "3:hello", "12:hello" }, Run("\"xx hello yy hello\" | Invoke-BashGrep '-bo' 'hello'").Lines);
    }

    [Fact]
    public void ByteOffset_CountsCrAndBom_LikeGnu()
    {
        var crlf = FB("crlf.txt", B("ab\r\ncd\r\n"));
        Assert.Equal(new[] { "4:cd\r" }, G("'-b' 'cd'", crlf).Lines);   // exact bytes: the CR stays
        var bom = FB("bom.txt", 0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i', (byte)'\n');
        var r = G("'-b' 'hi'", bom);
        Assert.Equal(new[] { "0:﻿hi" }, r.Lines);
    }

    [Fact]
    public void UnixByteOffsets_IsAccepted_WithGnuWarning()
    {
        var t = F("t.txt", "hello\n");
        var r = G("'-u' '-b' 'hello'", t);
        Assert.Equal(new[] { "0:hello" }, r.Lines);
        Assert.Equal(0, r.Exit);
        Assert.Contains("grep: warning: --unix-byte-offsets (-u) is obsolete", r.Stderr);
    }

    // ---- -T ------------------------------------------------------------------------------------

    [Fact]
    public void InitialTab_AlignsContentAfterThePrefix()
    {
        var t = F("t.txt", "hello\nworld\nhello again\n");   // 25 bytes: numbers are 2 wide
        var name = Path_("t.txt");
        Assert.Equal(new[] { "hello", "hello again" }, G("'-T' 'hello'", t).Lines);            // no prefix, no tab
        Assert.Equal(new[] { " 1:\thello", " 3:\thello again" }, G("'-T' '-n' 'hello'", t).Lines);
        Assert.Equal(new[] { " 1:\thello", " 2-\tworld", " 3:\thello again" }, G("'-T' '-n' '-A1' 'hello'", t).Lines);
        Assert.Equal(new[] { " 1: 0:\thello", " 3:12:\thello again" }, G("'-T' '-n' '-b' 'hello'", t).Lines);
        Assert.Equal(new[] { "2" }, G("'-T' '-c' 'hello'", t).Lines);
        var multi = G("'-T' '-n' 'hello'", t + " " + t);
        Assert.Equal(new[] { name + ": 1:\thello", name + ": 3:\thello again", name + ": 1:\thello", name + ": 3:\thello again" }, multi.Lines);
        Assert.Equal(new[] { name + "\0 1:\thello" , name + "\0 3:\thello again" }, G("'-TZ' '-H' '-n'", " 'hello' " + t).Lines);
    }

    [Fact]
    public void InitialTab_WidthFollowsTheFileSize()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 1; i <= 12; i++) sb.Append(i).Append(" hello\n");   // 99 bytes + 1 = 3 digits
        var n12 = F("n12.txt", sb.ToString());
        var r = G("'-T' '-n' 'hello'", n12);
        Assert.Equal("  1:\t1 hello", r.Lines[0]);
        Assert.Equal(" 12:\t12 hello", r.Lines[11]);
        // stdin has no size: GNU pads to the width of the largest offset (19 digits)
        var p = Run("'a' | Invoke-BashGrep '-T' '-n' 'a'");
        Assert.Equal(new string(' ', 18) + "1:\ta", p.Lines[0]);
    }

    // ---- --label -------------------------------------------------------------------------------

    [Fact]
    public void Label_NamesStandardInput()
    {
        Assert.Equal(new[] { "LBL:hello" }, Run("'hello' | Invoke-BashGrep '-H' '--label=LBL' 'hello'").Lines);
        Assert.Equal(new[] { "hello" }, Run("'hello' | Invoke-BashGrep '--label=LBL' 'hello'").Lines);   // no name without -H
        var t = F("t.txt", "hello\nworld\nhello again\n");
        var c = Run($"'x' | Invoke-BashGrep '-c' '--label=X' 'hello' {t} '-'");
        Assert.Equal(new[] { $"{Path_("t.txt")}:2", "X:0" }, c.Lines);
        var l = Run($"'hello' | Invoke-BashGrep '-l' 'hello' '-'");
        Assert.Equal(new[] { "(standard input)" }, l.Lines);
    }

    // ---- -d / -D -------------------------------------------------------------------------------

    [Fact]
    public void Directories_ReadIsTheDefault_AnErrorForADirectory()
    {
        var t = F("t.txt", "hello\n");
        Directory.CreateDirectory(Path_("dd"));
        File.WriteAllText(Path_("dd/f.txt"), "hello\n");
        var d = "'" + Path_("dd") + "'";
        var r = G("'hello'", d);
        Assert.Equal(2, r.Exit);
        Assert.Contains("Is a directory", r.Stderr);
        var s = G("'-s' 'hello'", d);
        Assert.Equal(2, s.Exit);
        Assert.Equal("", s.Stderr);
        var both = G("'-d' 'read' 'hello'", d + " " + t);
        Assert.Equal(2, both.Exit);
        Assert.Equal(new[] { $"{Path_("t.txt")}:hello" }, both.Lines);
    }

    [Fact]
    public void Directories_SkipIgnoresThem_RecurseIsMinusR()
    {
        var t = F("t.txt", "hello\n");
        Directory.CreateDirectory(Path_("dd"));
        File.WriteAllText(Path_("dd/f.txt"), "hello\n");
        var d = "'" + Path_("dd") + "'";
        var sk = G("'-d' 'skip' 'hello'", d + " " + t);
        Assert.Equal(0, sk.Exit);
        Assert.Equal(new[] { $"{Path_("t.txt")}:hello" }, sk.Lines);
        Assert.Equal(1, G("'-d' 'skip' 'hello'", d).Exit);
        var rec = G("'-d' 'recurse' 'hello'", d);
        Assert.Equal(new[] { Path_("dd") + "/f.txt:hello" }, rec.Lines);
        Assert.Equal(new[] { Path_("dd") + "/f.txt:hello" }, G("'-d' 'skip' '-r' 'hello'", d).Lines);   // last wins
        var bad = G("'-d' 'bogus' 'hello'", d);
        Assert.Equal(2, bad.Exit);
        Assert.Contains("invalid argument 'bogus' for '--directories'", bad.Stderr);
        Assert.Contains("'read'", bad.Stderr);
    }

    [Fact]
    public void Devices_ReadSkipAndBadWord()
    {
        var t = F("t.txt", "hello\n");
        Assert.Equal(new[] { "hello" }, G("'-D' 'read' 'hello'", t).Lines);
        Assert.Equal(new[] { "hello" }, G("'-D' 'skip' 'hello'", t).Lines);   // a regular file is not a device
        Assert.Equal(1, G("'-D' 'read' 'hello'", "'/dev/null'").Exit);
        var bad = G("'-D' 'bogus' 'hello'", t);
        Assert.Equal(2, bad.Exit);
        Assert.Contains("grep: unknown devices method", bad.Stderr);
    }

    [Fact]
    public void Directories_DirectCallDecoys_ReachTheScan()
    {
        // The Pester shape: a bare -d / -D binds the declared decoy; the ACTION word still arrives.
        var t = F("t.txt", "hello\n");
        Directory.CreateDirectory(Path_("dd"));
        var d = "'" + Path_("dd") + "'";
        Assert.Equal(1, Run($"Invoke-BashGrep -d skip hello {d}").Exit);
        Assert.Equal(new[] { "hello" }, Run($"Invoke-BashGrep -D skip hello {t}").Lines);
        Assert.Equal(2, Run($"Invoke-BashGrep -D bogus hello {t}").Exit);
    }

    // ---- -r details ----------------------------------------------------------------------------

    [Fact]
    public void Recursive_SingleFileOperand_HasNoNamePrefix_UnlessH()
    {
        var t = F("t.txt", "hello\n");
        Assert.Equal(new[] { "hello" }, G("'-r' 'hello'", t).Lines);
        Assert.Equal(new[] { $"{Path_("t.txt")}:hello" }, G("'-rH' 'hello'", t).Lines);
    }

    // ---- group separator -----------------------------------------------------------------------

    [Fact]
    public void GroupSeparator_CustomEmptyAndNone()
    {
        var s = F("s.txt", "hello\nworld\nhello again\n");
        Assert.Equal(new[] { "hello", "XX", "hello again" }, G("'--group-separator=XX' '-A0' '-e' 'hello'", s).Lines);
        Assert.Equal(new[] { "hello", "", "hello again" }, G("'--group-separator=' '-A0' '-e' 'hello'", s).Lines);
        Assert.Equal(new[] { "hello", "hello again" }, G("'--no-group-separator' '-A0' '-e' 'hello'", s).Lines);
        Assert.Equal(new[] { "hello", "hello again" }, G("'--group-separator=XX' '--no-group-separator' '-A0' '-e' 'hello'", s).Lines);
        Assert.Equal(new[] { "hello", "YY", "hello again" }, G("'--no-group-separator' '--group-separator=YY' '-A0' '-e' 'hello'", s).Lines);
    }

    [Fact]
    public void Context_OnlyMatching_KeepsSeparators_AndMaxCountIsPerFile()
    {
        var t = F("t.txt", "hello\nworld\nhello again\nx\ny\nhello z\n");
        Assert.Equal(new[] { "hello", "hello", "--", "hello" }, G("'-o' '-A1' 'hello'", t).Lines);
        Assert.Equal(new[] { "world" }, G("'-o' '-C1' 'world'", t).Lines);
        var name = Path_("t.txt");
        var u = F("u.txt", "hello\nworld\nhello again\n");
        Assert.Equal(new[] { name + ":hello", Path_("u.txt") + ":hello" }, G("'-m1' 'hello'", t + " " + u).Lines);
        Assert.Equal(new[] { name + ":1", Path_("u.txt") + ":1" }, G("'-m1' '-c' 'hello'", t + " " + u).Lines);
        // after -m the trailing context prints with '-' even when a context line matches
        Assert.Equal(new[] { "1:hello", "2-world", "3:hello again", "4-x", "5-y", "6-hello z" }, G("'-n' '-m2' '-A5' 'hello'", t).Lines);
        Assert.Equal(new[] { "world", "x" }, G("'-m2' '-v' 'hello'", t).Lines);
        Assert.Empty(G("'-l' '-m0' 'hello'", t).Lines);
    }

    // ---- colour --------------------------------------------------------------------------------

    private string S() => F("s.txt", "hello\nworld\nhello again\n");

    [Fact]
    public void Color_Always_DefaultSgrSequences()
    {
        var s = S();
        var name = Path_("s.txt");
        Assert.Equal(new[] { Match("hello"), Match("hello") + " again" }, G("'--color=always' 'hello'", s).Lines);
        Assert.Equal(new[] { Match("hello"), Match("hello") + " again" }, G("'--colour=always' 'hello'", s).Lines);
        Assert.Equal(new[] { Col("35", name) + Sep(":") + Match("hello"), Col("35", name) + Sep(":") + Match("hello") + " again" },
            G("'--color=always' '-H' 'hello'", s).Lines);
        Assert.Equal(new[] { Col("32", "1") + Sep(":") + Match("hello"), Col("32", "3") + Sep(":") + Match("hello") + " again" },
            G("'--color=always' '-n' 'hello'", s).Lines);
        Assert.Equal(new[]
        {
            Col("35", name) + Sep(":") + Col("32", "1") + Sep(":") + Match("hello"),
            Col("35", name) + Sep("-") + Col("32", "2") + Sep("-") + "world",
            Col("35", name) + Sep(":") + Col("32", "3") + Sep(":") + Match("hello") + " again",
        }, G("'--color=always' '-A1' '-n' '-H' 'hello'", s).Lines);
    }

    [Fact]
    public void Color_Always_OffsetsTabsNamesAndCounts()
    {
        var s = S();
        var name = Path_("s.txt");
        Assert.Equal(new[] { Col("32", "0") + Sep(":") + Match("hello"), Col("32", "12") + Sep(":") + Match("hello") },
            G("'--color=always' '-o' '-b' 'hello'", s).Lines);
        Assert.Equal(new[] { Col("32", " 1") + Sep(":") + "\t" + Match("hello"), Col("32", " 3") + Sep(":") + "\t" + Match("hello") + " again" },
            G("'--color=always' '-T' '-n' 'hello'", s).Lines);
        Assert.Equal(new[] { Col("35", name) + Sep(":") + "2" }, G("'--color=always' '-c' '-H' 'hello'", s).Lines);
        Assert.Equal(new[] { Col("35", name) }, G("'--color=always' '-l' 'hello'", s).Lines);
        Assert.Equal(new[] { Col("35", name) }, G("'--color=always' '-L' 'zzz'", s).Lines);
        Assert.Equal(new[] { Col("35", name) + "\0" }, G("'--color=always' '-Z' '-l'", " 'hello' " + s).Lines);
        Assert.Equal(new[] { "2" }, G("'--color=always' '-c' 'hello'", s).Lines);
    }

    [Fact]
    public void Color_Always_MatchesGroupSeparatorAndInvert()
    {
        var s = S();
        Assert.Equal(new[] { Match("hello"), Sep("--"), Match("hello") + " " + Match("again") },
            G("'--color=always' '-A0' '-e' 'hello' '-e' 'again'", s).Lines);
        Assert.Equal(new[] { Match("hello"), Sep("=="), Match("hello") + " " + Match("again") },
            G("'--color=always' '--group-separator===' '-A0' '-e' 'hello' '-e' 'again'", s).Lines);
        // -v: selected lines have nothing to colour; the (matching) context line gets mc
        Assert.Equal(new[] { Col("32", "2") + Sep(":") + "world", Col("32", "3") + Sep("-") + Match("hello") + " again" },
            G("'--color=always' '-v' '-A1' '-n' 'hello'", s).Lines);
        Assert.Equal(new[] { "world", "hello again" }, G("'--color=always' '-x' '-v' 'hello'", s).Lines);
        Assert.Equal(new[] { "hello", "world", "hello again" }, G("'--color=always' ''", s).Lines);      // empty matches colour nothing
        Assert.Equal(new[] { Match("hello"), "world", Match("hello") + " again" }, G("'--color=always' '-e' '' '-e' 'hello'", s).Lines);
        Assert.Equal(new[] { "he" + Match("ll") + "o", "wor" + Match("l") + "d", "he" + Match("ll") + "o again" }, G("'--color=always' '-E' 'l*'", s).Lines);
        Assert.Equal(new[] { Match("hello"), Match("hello") + " again" }, G("'--color=always' '-i' 'HELLO'", s).Lines);
        Assert.Equal(new[] { Match("hello") }, G("'--color=always' '-x' 'hello'", s).Lines);
    }

    [Fact]
    public void Color_Always_NullDataKeepsTheRecordNewlines()
    {
        var s = S();
        var r = G("'--color=always' '-z' 'hello'", s);
        Assert.Equal(new[] { Match("hello") + "\nworld\n" + Match("hello") + " again\n\0" }, r.Lines);
    }

    [Fact]
    public void Color_AutoAndNever_StayPlainOffATerminal()
    {
        var s = S();
        Assert.Equal(new[] { "hello", "hello again" }, G("'--color=auto' 'hello'", s).Lines);
        Assert.Equal(new[] { "hello", "hello again" }, G("'--color=never' 'hello'", s).Lines);
        Assert.Equal(new[] { "hello", "hello again" }, G("'--color' 'hello'", s).Lines);
    }

    [Fact]
    public void Color_Auto_ColoursWhenTheTerminalSignalIsSet()
    {
        var s = S();
        Environment.SetEnvironmentVariable("PSBASH_GREP_TTY", "1");
        Environment.SetEnvironmentVariable("TERM", "xterm-256color");
        try
        {
            Assert.Equal(new[] { Match("hello"), Match("hello") + " again" }, G("'--color=auto' 'hello'", s).Lines);
            Environment.SetEnvironmentVariable("TERM", "dumb");
            Assert.Equal(new[] { "hello", "hello again" }, G("'--color=auto' 'hello'", s).Lines);   // TERM=dumb: GNU stays plain
            Assert.Equal(new[] { Match("hello"), Match("hello") + " again" }, G("'--color=always' 'hello'", s).Lines);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSBASH_GREP_TTY", null);
            Environment.SetEnvironmentVariable("TERM", null);
        }
    }

    [Fact]
    public void Color_GrepColors_Capabilities()
    {
        var s = S();
        var name = Path_("s.txt");
        void With(string colors, Action body)
        {
            Environment.SetEnvironmentVariable("GREP_COLORS", colors);
            try { body(); } finally { Environment.SetEnvironmentVariable("GREP_COLORS", null); }
        }

        With("mt=4", () => Assert.Equal(new[] { Col("4", "hello"), Col("4", "hello") + " again" }, G("'--color=always' 'hello'", s).Lines));
        With("bogus=1:mt=7", () => Assert.Equal(Col("7", "hello"), G("'--color=always' 'hello'", s).Lines[0]));
        With("mt=7:", () => Assert.Equal(Col("7", "hello"), G("'--color=always' 'hello'", s).Lines[0]));
        With("ne", () => Assert.Equal(new[] { $"{Esc}[32m1{Esc}[m{Esc}[36m:{Esc}[m{Esc}[01;31mhello{Esc}[m", $"{Esc}[32m3{Esc}[m{Esc}[36m:{Esc}[m{Esc}[01;31mhello{Esc}[m again" },
            G("'--color=always' '-n' 'hello'", s).Lines));
        With("fn=:ln=:se=", () => Assert.Equal(new[] { name + ":1:" + Match("hello"), name + ":3:" + Match("hello") + " again" },
            G("'--color=always' '-n' '-H' 'hello'", s).Lines));
        With("sl=1:cx=2", () => Assert.Equal(new[]
        {
            Col("32", "1") + Sep(":") + Sgr("1") + Match("hello"),
            Col("32", "2") + Sep("-") + Col("2", "world"),
            Col("32", "3") + Sep(":") + Sgr("1") + Match("hello") + Col("1", " again"),
        }, G("'--color=always' '-n' '-A1' 'hello'", s).Lines));
        With("sl=1:cx=2", () => Assert.Equal(new[]
        {
            Col("1", "world"),
            Sgr("2") + Match("hello") + Col("2", " again"),
        }, G("'--color=always' '-v' '-A1' 'hello'", s).Lines));
        With("sl=1:cx=2:rv", () => Assert.Equal(new[]
        {
            Col("2", "world"),
            Sgr("1") + Match("hello") + Col("1", " again"),
        }, G("'--color=always' '-v' '-A1' 'hello'", s).Lines));
        With("ms=4:mc=5", () => Assert.Equal(new[] { "world", Col("5", "hello") + " again" }, G("'--color=always' '-v' '-A1' 'hello'", s).Lines));
        With("fn=33:ln=34:bn=35:se=36", () => Assert.Equal(new[]
        {
            Col("33", name) + Sep(":") + Col("34", "1") + Sep(":") + Col("35", "0") + Sep(":") + Match("hello"),
            Col("33", name) + Sep("-") + Col("34", "2") + Sep("-") + Col("35", "6") + Sep("-") + "world",
            Col("33", name) + Sep(":") + Col("34", "3") + Sep(":") + Col("35", "12") + Sep(":") + Match("hello") + " again",
        }, G("'--color=always' '-n' '-b' '-H' '-A1' 'hello'", s).Lines));
    }

    [Fact]
    public void Color_DeprecatedGrepColor_WarnsAndSetsTheMatchColour()
    {
        var s = S();
        Environment.SetEnvironmentVariable("GREP_COLOR", "4;5");
        try
        {
            var r = G("'--color=always' 'hello'", s);
            Assert.Equal(Col("4;5", "hello"), r.Lines[0]);
            Assert.Contains("grep: warning: GREP_COLOR='4;5' is deprecated; use GREP_COLORS='mt=4;5'", r.Stderr);
            Environment.SetEnvironmentVariable("GREP_COLORS", "mt=7");   // GREP_COLORS overrides: no warning
            var r2 = G("'--color=always' 'hello'", s);
            Assert.Equal(Col("7", "hello"), r2.Lines[0]);
            Assert.Equal("", r2.Stderr);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GREP_COLOR", null);
            Environment.SetEnvironmentVariable("GREP_COLORS", null);
        }
    }

    // ---- -P ------------------------------------------------------------------------------------

    private R P(string pattern, string input, string flags = "")
    {
        var f = F("p.txt", input);
        return Run($"Invoke-BashGrep '-P' {flags} '{pattern.Replace("'", "''")}' {f}");
    }

    [Fact]
    public void Perl_CommonConstructs()
    {
        Assert.Equal(new[] { "hello", "hello again" }, P(@"hel+o\b", "hello\nworld\nhello again\n").Lines);
        Assert.Equal(new[] { "hello" }, P("(?i)HELLO", "hello\nx\n").Lines);
        Assert.Equal(new[] { "again" }, P(@"hello \K\w+", "hello again\nhello\n", "'-o'").Lines);
        Assert.Equal(new[] { "12" }, P(@"(?<=a)\d+", "a12\n", "'-o'").Lines);
        Assert.Equal(new[] { "wor" }, P("wor(?=ld)", "world\n", "'-o'").Lines);
        Assert.Equal(new[] { "a", "a", "a" }, P("a+?", "aaa\n", "'-o'").Lines);
        Assert.Equal(new[] { "abab" }, P(@"(ab)\1", "abab\n").Lines);
        Assert.Equal(new[] { "aB" }, P("a(?i)b", "aB\n").Lines);
        Assert.Equal(new[] { "ab" }, P(@"b\z", "ab\n").Lines);
        Assert.Equal(new[] { "ab" }, P(@"\Aab", "ab\n").Lines);
        Assert.Equal(new[] { "ab" }, P(@"(?<x>a)(?P=x)?b", "ab\n").Lines);
        Assert.Equal(new[] { "ab" }, P(@"(?P<x>a)(?P=x)?b", "ab\n").Lines);
        Assert.Equal(new[] { "1" }, P("[[:digit:]]", "a1\n", "'-o'").Lines);
        Assert.Equal(new[] { "ab" }, P(@"\p{L}+", "ab\n", "'-o'").Lines);
        Assert.Equal(new[] { "A" }, P(@"\x41", "A\n").Lines);
        Assert.Equal(new[] { "é" }, P(@"\x{e9}", "é\n").Lines);
        Assert.Equal(new[] { "12" }, P(@"\d{2}", "a12\n", "'-o'").Lines);
    }

    [Fact]
    public void Perl_WordLineAndEmptyPatterns()
    {
        Assert.Equal(new[] { "ab" }, P("ab", "ab abc\n", "'-w' '-o'").Lines);
        Assert.Equal(new[] { "ab" }, P("ab", "ab\nabc\n", "'-x'").Lines);
        Assert.Equal(new[] { "AB" }, P("ab", "AB\n", "'-i'").Lines);
        Assert.Equal(new[] { "a" }, P("", "a\n").Lines);
    }

    [Fact]
    public void Perl_BeyondTheDotNetEngine_Translated()
    {
        Assert.Equal(new[] { "aab" }, P("a++b", "aab\n").Lines);                 // possessive
        Assert.Empty(P("a++a", "aaa\n").Lines);                                    // possessive really is possessive
        Assert.Equal(new[] { "aab" }, P("(?>a+)b", "aab\n").Lines);
        Assert.Equal(new[] { "hello" }, P(@"\Qhello\E", "hello\n").Lines);
        Assert.Equal(new[] { "a.b" }, P(@"\Qa.b\E", "a.b\naxb\n").Lines);
        Assert.Equal(new[] { "h" }, P("(*UTF8)h", "h\n", "'-o'").Lines);
        Assert.Equal(new[] { "a b" }, P(@"a\hb", "a b\n").Lines);
        Assert.Equal(new[] { "ab" }, P(@"b\R?", "ab\n").Lines);
        Assert.Equal(new[] { "4:bar" }, P(@"foo=\Kbar", "foo=bar\n", "'-ob'").Lines);
        Assert.Equal(new[] { "a" }, P("a(?C1)", "a\n").Lines);
    }

    [Fact]
    public void Perl_Errors_ExitTwo()
    {
        var multi = Run($"Invoke-BashGrep '-P' '-e' 'a' '-e' 'b' {F("p.txt", "a\n")}");
        Assert.Equal(2, multi.Exit);
        Assert.Contains("grep: the -P option only supports a single pattern", multi.Stderr);

        var open = P("(", "a\n");
        Assert.Equal(2, open.Exit);
        Assert.StartsWith("grep: ", open.Stderr);
        Assert.Equal(2, P("[", "a\n").Exit);
        Assert.Equal(2, P(@"\i", "a\n").Exit);

        // constructs with no .NET equivalent are refused, not silently mis-matched
        foreach (var unsupported in new[] { "(?R)?a", "(a)(?1)", "(*FAIL)", "(?|a|b)", "(?U)a", @"(?&n)", @"\g<1>" })
        {
            var r = P(unsupported, "a\n");
            Assert.Equal(2, r.Exit);
            Assert.Contains("unsupported PCRE construct", r.Stderr);
        }
    }

    [Fact]
    public void Perl_PatternFile_WithOneLine_Works()
    {
        var pf = F("pf", "x\n");
        var xx = F("xx", "x\n");
        Assert.Equal(new[] { "x" }, Run($"Invoke-BashGrep '-P' '-f' {pf} {xx}").Lines);
    }

    // ---- pipeline / file decorations agree ------------------------------------------------------

    [Fact]
    public void Pipeline_AndFile_ShareTheDecorations()
    {
        var s = S();
        var viaFile = G("'-n' '-b' '-A1' 'hello'", s).Lines;
        var viaPipe = Run("'hello','world','hello again' | Invoke-BashGrep '-n' '-b' '-A1' 'hello'").Lines;
        Assert.Equal(new[] { "1:0:hello", "2-6-world", "3:12:hello again" }, viaFile);
        Assert.Equal(viaFile, viaPipe);
    }
}
