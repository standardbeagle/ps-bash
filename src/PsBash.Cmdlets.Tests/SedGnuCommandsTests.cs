using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The GNU sed 4.9 commands the engine used to refuse as "unsupported command" (blocks <c>{ }</c>, labels and
/// branches <c>: b t T</c>, hold space <c>h H g G x</c>, <c>n</c>, <c>z</c>, <c>F</c>, <c>l</c>, <c>r R w W</c>,
/// <c>e</c>, <c>v</c>, <c>s///w</c>, a <c>q</c>/<c>Q</c> exit status) and <c>--debug</c>. Every expectation is the
/// byte output of GNU sed 4.9 (`wsl bash`) on the same fixture: a.txt = a1 b2 c3.
/// </summary>
public class SedGnuCommandsTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;
    private readonly string _a;

    public SedGnuCommandsTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), "psbash-sedc-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        Directory.CreateDirectory(_dir);
        _a = F("a.txt", "a1\nb2\nc3\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private CmdResult Run(string script) => CmdResult.Run(_fixture.AcquireFresh(), script);

    private string F(string name, string content)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        return "'" + p.Replace("'", "''") + "'";
    }

    private static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>The exact bytes the command wrote to stdout, plus its exit status.</summary>
    private (string Out, int Exit) Bytes(string script)
    {
        var r = Run($"$o = @({script}); $e = $global:LASTEXITCODE; [PsBash.Cmdlets.BashRuntime]::RecordStreamText($o); \"<rc=$e>\"");
        string s = r.Stdout.Replace("\r\n", "\n");
        int i = s.LastIndexOf("<rc=", StringComparison.Ordinal);
        int rc = int.Parse(s.Substring(i + 4, s.IndexOf('>', i) - i - 4));
        string body = s.Substring(0, i);
        if (body.EndsWith('\n')) body = body.Substring(0, body.Length - 1); // the separator before <rc=
        return (body, rc);
    }

    private string Sed(params string[] argv)
    {
        var (o, rc) = Bytes("Invoke-BashSed " + string.Join(" ", argv.Select(Q)) + " " + _a);
        Assert.Equal(0, rc);
        return o;
    }

    [Theory]
    [InlineData("b2\nc3\n", "-n", "1{b end};p;:end")]
    [InlineData("b2\nc3\n", "-n", "1{b end\n};p;:end")]
    [InlineData("a1,b2,c3\n", "-n", ":a;N;$!ba;s/\\n/,/g;p")]
    [InlineData("b2\nb2\n", "-n", "/b/{p;p}")]
    [InlineData("a1\nb2\nb2\nb2\nc3\n", "-n", "/b/{p;p};p")]
    [InlineData("c3\nb2\na1\n", "-n", "1!G;h;$p")]
    [InlineData("X1\nb2 no\nc3 no\n", "s/a/X/;ta;s/$/ no/;:a")]
    [InlineData("X1 no\nb2\nc3\n", "s/a/X/;Ta;s/$/ no/;:a")]
    [InlineData("b2\n", "-n", "$!{n;p}")]
    [InlineData("a1\nc3\n", "n;d")]
    [InlineData("\na1\na1\nb2\nb2\nc3\n", "x;G")]
    [InlineData(",a1,b2,c3\n", "-n", "H;${x;s/\\n/,/g;p}")]
    [InlineData("\n\n\n", "y/abc/xyz/;g")]
    [InlineData("a1\nb2\na1\na1\nc3\n", "1h;2{G;G}")]
    [InlineData("\n", "-n", "2{z;p}")]
    [InlineData("a1\nb2\n", "1!{2!d}")]
    [InlineData("a1 z\nX2\nc3 z\n", "s/b/X/;t\ns/$/ z/")]
    [InlineData("a1\nb2\nc3\n", "1~2{N;N}")]
    [InlineData("a1\nfoo\nbar\nc3\n", "1a foo\\\nbar\n2d")]
    [InlineData("a1\nfoo\nc3\n", "1a foo\n2d")]
    [InlineData("b2\nc3\n", "-n", "2{p;n;p}")]
    [InlineData("a1\nc3\n", "# comment\n2d # trailing")]
    [InlineData("a1\nb2\nc3\n", "v 4.2")]
    [InlineData("a1\nb2\nc3\n", ":a;:a")]
    public void Commands_MatchGnu(string expected, params string[] argv) => Assert.Equal(expected, Sed(argv));

    [Fact]
    public void BlocksAcrossExpressions_JoinLikeGnu()
    {
        Assert.Equal("a1\na1\nb2\nc3\n", Sed("-e", "1{", "-e", "p", "-e", "}"));
    }

    [Fact]
    public void Quit_CarriesItsExitStatus()   // FIX (the status was parsed and dropped)
    {
        Assert.Equal(("a1\nb2\n", 5), Bytes($"Invoke-BashSed '2q5' {_a}"));
        Assert.Equal(("a1\n", 7), Bytes($"Invoke-BashSed '2Q7' {_a}"));
    }

    [Fact]
    public void F_PrintsTheInputName()
    {
        var (o, _) = Bytes("'x' | Invoke-BashSed '-n' 'F'");
        Assert.Equal("-\n", o);
        string name = Path.Combine(_dir, "a.txt");
        Assert.Equal(new[] { name, name, name }, Run($"Invoke-BashSed '-n' 'F' {_a}").AssertSuccess().Lines);
    }

    [Fact]
    public void L_ListsUnambiguously_AndWraps()
    {
        var b = F("b.txt", "aXa.b\tc\\d\u0001é\n");
        Assert.Equal("aXa.b\\tc\\\\d\\001\\303\\251$\n", Bytes($"Invoke-BashSed '-n' 'l' {b}").Out);
        Assert.Equal("aXa.\\\nb\\tc\\\n\\\\d\\\n\\001\\\n\\303\\\n\\251$\n", Bytes($"Invoke-BashSed '-n' 'l 5' {b}").Out);
        Assert.Equal("aXa.b\\tc\\\\d\\001\\303\\251$\n", Bytes($"Invoke-BashSed '-n' 'l 0' {b}").Out);
        var lng = F("long.txt", new string('0', 100) + "\n");
        Assert.Equal(new string('0', 69) + "\\\n" + new string('0', 31) + "$\n", Bytes($"Invoke-BashSed '-n' 'l' {lng}").Out);
        Assert.Equal(string.Concat(Enumerable.Repeat(new string('0', 9) + "\\\n", 11)) + "0$\n",
            Bytes($"Invoke-BashSed '-l' '10' '-n' 'l' {lng}").Out);
    }

    [Fact]
    public void ReadAndWriteFiles()
    {
        string a = Path.Combine(_dir, "a.txt");
        Assert.Equal("a1\na1\nb2\nb2\nc3\nc3\n", Sed($"R {a}"));
        Assert.Equal("a1\nb2\na1\nb2\nc3\nc3\n", Sed($"2r {a}"));
        Assert.Equal("a1\nb2\nc3\n", Sed("r " + Path.Combine(_dir, "nofile")));   // a missing r file is ignored
        Assert.Equal("b2\n", Sed("-n", "2w /dev/stdout"));
        Assert.Equal("a1\nX2\nX2\nc3\n", Sed("s/b/X/w /dev/stdout"));

        string outF = Path.Combine(_dir, "out.txt");
        string outW = Path.Combine(_dir, "outW.txt");
        File.WriteAllText(outF, "OLD");
        Assert.Equal("", Sed("-n", $"/[ab]/w {outF}"));
        Assert.Equal("a1\nb2\n", File.ReadAllText(outF));
        Assert.Equal("", Sed("-n", $"$!N;W {outW}"));
        Assert.Equal("a1\nc3\n", File.ReadAllText(outW));
        // a w file named but never written is still created (truncated) at program start
        string never = Path.Combine(_dir, "never.txt");
        File.WriteAllText(never, "OLD");
        Assert.Equal("a1\nb2\nc3\n", Sed($"/zzz/w {never}"));
        Assert.Equal("", File.ReadAllText(never));
    }

    [Fact]
    public void E_RunsCommands()
    {
        Assert.Equal("hi\na1\nhi\nb2\nhi\nc3\n", Sed("e echo hi"));
        var script = F("cmds.txt", "echo foo\necho bar\n");
        Assert.Equal("foo\nbar\n", Bytes($"Invoke-BashSed 'e' {script}").Out);
        Assert.Equal("foo\necho bar\n", Bytes($"Invoke-BashSed '1e' {script}").Out);
    }

    [Fact]
    public void Debug_PrintsProgramAndAnnotations()
    {
        Assert.Equal(
            "SED PROGRAM:\n  2 p\n" +
            "INPUT:   'X' line 1\nPATTERN: a1\nCOMMAND: 2 p\nEND-OF-CYCLE:\n" +
            "INPUT:   'X' line 2\nPATTERN: b2\nCOMMAND: 2 p\nb2\nEND-OF-CYCLE:\n" +
            "INPUT:   'X' line 3\nPATTERN: c3\nCOMMAND: 2 p\nEND-OF-CYCLE:\n",
            Bytes($"Invoke-BashSed '--debug' '-n' '2p' {_a}").Out.Replace(Path.Combine(_dir, "a.txt"), "X"));

        Assert.Equal(
            "SED PROGRAM:\n  s/a/X/g\n  2 d\n" +
            "INPUT:   'X' line 1\nPATTERN: a1\nCOMMAND: s/a/X/g\nMATCHED REGEX REGISTERS\n  regex[0] = 0-1 'a'\nPATTERN: X1\nCOMMAND: 2 d\nEND-OF-CYCLE:\nX1\n" +
            "INPUT:   'X' line 2\nPATTERN: b2\nCOMMAND: s/a/X/g\nPATTERN: b2\nCOMMAND: 2 d\nEND-OF-CYCLE:\n" +
            "INPUT:   'X' line 3\nPATTERN: c3\nCOMMAND: s/a/X/g\nPATTERN: c3\nCOMMAND: 2 d\nEND-OF-CYCLE:\nc3\n",
            Bytes($"Invoke-BashSed '--debug' 's/a/X/g;2d' {_a}").Out.Replace(Path.Combine(_dir, "a.txt"), "X"));

        Assert.Equal(
            "SED PROGRAM:\n  /b/ {\n    p\n    d\n  }\n  $! N\n" +
            "INPUT:   'X' line 1\nPATTERN: a1\nCOMMAND: /b/ {\nCOMMAND: }\nCOMMAND: $! N\nPATTERN: a1\\nb2\nEND-OF-CYCLE:\na1\nb2\n" +
            "INPUT:   'X' line 3\nPATTERN: c3\nCOMMAND: /b/ {\nCOMMAND: }\nCOMMAND: $! N\nEND-OF-CYCLE:\nc3\n",
            Bytes($"Invoke-BashSed '--debug' '-e' '/b/{{p;d}}' '-e' '$!N' {_a}").Out.Replace(Path.Combine(_dir, "a.txt"), "X"));

        Assert.Equal(
            "SED PROGRAM:\n  y/xy/ab/\n  1! G\n  h\n  $! d\n" +
            "INPUT:   'STDIN' line 1\nPATTERN: x\nCOMMAND: y/xy/ab/\nPATTERN: a\nCOMMAND: 1! G\nCOMMAND: h\nHOLD:    a\nCOMMAND: $! d\nEND-OF-CYCLE:\n" +
            "INPUT:   'STDIN' line 2\nPATTERN: y\nCOMMAND: y/xy/ab/\nPATTERN: b\nCOMMAND: 1! G\nPATTERN: b\\na\nCOMMAND: h\nHOLD:    b\\na\nCOMMAND: $! d\nEND-OF-CYCLE:\nb\na\n",
            Bytes("'x','y' | Invoke-BashSed '--debug' 'y/xy/ab/;1!G;h;$!d'").Out);

        // N;P;D: a D with a newline restarts without END-OF-CYCLE; N at the end ends the run.
        Assert.Equal(
            "SED PROGRAM:\n  N\n  P\n  D\n" +
            "INPUT:   'STDIN' line 1\nPATTERN: x\nCOMMAND: N\nPATTERN: x\\ny\nCOMMAND: P\nx\nCOMMAND: D\nPATTERN: y\n" +
            "COMMAND: N\nPATTERN: y\\nz\nCOMMAND: P\ny\nCOMMAND: D\nPATTERN: z\nCOMMAND: N\nEND-OF-CYCLE:\nz\n",
            Bytes("'x','y','z' | Invoke-BashSed '--debug' 'N;P;D'").Out);

        // A `d` inside a block leaves the block level raised for the next cycle (GNU's global block_level).
        Assert.Equal(
            "SED PROGRAM:\n  1 {\n    d\n  }\n" +
            "INPUT:   'STDIN' line 1\nPATTERN: x\nCOMMAND: 1 {\nCOMMAND:   d\nEND-OF-CYCLE:\n" +
            "INPUT:   'STDIN' line 2\nPATTERN: y\nCOMMAND:   1 {\nCOMMAND:   }\nEND-OF-CYCLE:\ny\n",
            Bytes("'x','y' | Invoke-BashSed '--debug' '1{d\n}'").Out);
    }

    [Fact]
    public void SplitSedCommands_SeparatesBracesAndLabels()
    {
        Assert.Equal(new[] { "/x/{", "p", "d", "}" }, InvokeBashSedCommand.SplitSedCommands("/x/{p;d}"));
        Assert.Equal(new[] { ":a", "N", "$!ba", "s/\\n/,/g" }, InvokeBashSedCommand.SplitSedCommands(":a;N;$!ba;s/\\n/,/g"));
        Assert.Equal(new[] { "1a foo", "2d" }, InvokeBashSedCommand.SplitSedCommands("1a foo\n2d"));
    }

    [Fact]
    public void FusedStage_DeclinesTheNewCommands()
    {
        Assert.False(LineStreamRegistry.TryCreate("sed", new[] { "1h;G" }, out _));
        Assert.False(LineStreamRegistry.TryCreate("sed", new[] { "--debug", "p" }, out _));
        Assert.False(LineStreamRegistry.TryCreate("sed", new[] { "2q5" }, out _));
        Assert.True(LineStreamRegistry.TryCreate("sed", new[] { "s/a/b/" }, out _));
    }
}
