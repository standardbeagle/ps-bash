using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// End-to-end sed behavior in the shape the transpiler emits (every dash word single-quoted, which
/// lands verbatim in Arguments) plus DIRECT calls (the Pester shape, through the psm1 literal-args
/// proxy). Oracle: GNU sed 4.9 (`wsl bash`); tests marked FIX assert output that used to be wrong.
/// Fixtures: a.txt = a1 b2 c3 (final newline), b.txt = d4 e5 f6 (NO final newline).
/// </summary>
public class SedGnuBehaviorTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;
    private readonly string _a, _b;

    public SedGnuBehaviorTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), "psbash-sedg-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        Directory.CreateDirectory(_dir);
        _a = F("a.txt", "a1\nb2\nc3\n");
        _b = F("b.txt", "d4\ne5\nf6");
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

    private string Read(string name) => File.ReadAllText(Path.Combine(_dir, name));

    /// <summary>The exact bytes the command wrote to stdout (records joined by their terminators).</summary>
    private string Bytes(string script)
    {
        var r = Run($"$o = @({script}); [PsBash.Cmdlets.BashRuntime]::RecordStreamText($o)");
        return r.Stdout;
    }

    [Fact]
    public void Files_AreOneContinuousStream_UnlessSeparate()   // FIX (line numbers and $ restarted per file)
    {
        Assert.Equal(new[] { "f6" }, Run($"Invoke-BashSed '-n' '$p' {_a} {_b}").AssertSuccess().Lines);
        Assert.Equal(new[] { "c3", "f6" }, Run($"Invoke-BashSed '-s' '-n' '$p' {_a} {_b}").AssertSuccess().Lines);
        Assert.Equal(new[] { "a1" }, Run($"Invoke-BashSed '-n' '1p' {_a} {_b}").AssertSuccess().Lines);
        Assert.Equal(new[] { "b2", "e5" }, Run($"Invoke-BashSed '-s' '-n' '2p' {_a} {_b}").AssertSuccess().Lines);
        Assert.Equal(new[] { "b2", "c3", "d4" }, Run($"Invoke-BashSed '-n' '2,4p' {_a} {_b}").AssertSuccess().Lines);
        Assert.Equal(new[] { "6" }, Run($"Invoke-BashSed '-n' '$=' {_a} {_b}").AssertSuccess().Lines);
    }

    [Fact]
    public void MissingFinalNewline_OfAnEarlierFile_IsSuppliedBetweenFiles()
    {
        Assert.Equal(new[] { "d4", "d4", "e5", "e5", "f6", "f6", "a1", "a1", "b2", "b2", "c3", "c3" },
            Run($"Invoke-BashSed 'p' {_b} {_a}").AssertSuccess().Lines);
    }

    [Fact]
    public void UnreadableOperand_IsReported_OthersStillRun_ExitTwo()   // FIX (exit was 1, message lost the typed name)
    {
        var missing = "'" + Path.Combine(_dir, "nosuch").Replace("'", "''") + "'";
        var r = Run($"Invoke-BashSed '-n' '2p' {missing} {_a}");
        Assert.Equal(new[] { "b2" }, r.Lines);
        r.AssertFailed(2, "sed: can't read", "No such file or directory");
    }

    [Fact]
    public void MultipleEAndBundles_AllReachTheCmdlet()
    {
        Assert.Equal(new[] { "b2", "c3" }, Run($"Invoke-BashSed '-n' '-e' '2p' '-e' '3p' {_a}").AssertSuccess().Lines);
        Assert.Equal(new[] { "b2", "c3" }, Run($"Invoke-BashSed '-ne2p' '-e3p' {_a}").AssertSuccess().Lines);
        Assert.Equal(new[] { "aX", "b2", "c3" }, Run($"Invoke-BashSed '-E' 's/(a)1/\\1X/' {_a}").AssertSuccess().Lines);
        Assert.Equal(new[] { "[b]2" }, Run($"Invoke-BashSed '-nr' 's/(b)/[\\1]/p' {_a}").AssertSuccess().Lines);
        Assert.Equal(new[] { "b2" }, Run($"Invoke-BashSed '--quiet' '--expression=2p' {_a}").AssertSuccess().Lines);
    }

    [Fact]
    public void ConsecutiveDashE_ChunksJoin_SoALineContinuationSpansThem()   // FIX (-e 1a\ -e foo: unknown command)
    {
        Assert.Equal(new[] { "a1", "foo", "b2", "c3" }, Run($"Invoke-BashSed '-e' '1a\\' '-e' 'foo' {_a}").AssertSuccess().Lines);
    }

    [Fact]
    public void DoubleDash_ScriptMayStartWithDash()
    {
        Run($"Invoke-BashSed '--' '-n' {_a}").AssertFailed(1, "unknown command");
        Assert.Equal(new[] { "b2" }, Run($"Invoke-BashSed '-n' '--' '2p' {_a}").AssertSuccess().Lines);
    }

    [Fact]
    public void UsageErrors_ExitOne_AndScriptErrorsToo()
    {
        Run("Invoke-BashSed").AssertFailed(1, "Usage: sed");
        Run("Invoke-BashSed '-n'").AssertFailed(1, "Usage: sed");
        Run($"Invoke-BashSed '-Q' p {_a}").AssertFailed(1, "sed: invalid option -- 'Q'");
        Run($"Invoke-BashSed '--bogus' p {_a}").AssertFailed(1, "unrecognized option '--bogus'");
        Run($"Invoke-BashSed 'foo' {_a}").AssertFailed(1, "unknown command");
        Run($"Invoke-BashSed '-n2p' {_a}").AssertFailed(1, "invalid option -- '2'");
        Run($"Invoke-BashSed '1{{p' {_a}").AssertFailed(1, "unmatched `{'");
        Run($"Invoke-BashSed 'p}}' {_a}").AssertFailed(1, "char 2: unexpected `}'");
        Run($"Invoke-BashSed 'bfoo' {_a}").AssertFailed(4, "can't find label for jump to `foo'");
        Run($"Invoke-BashSed 'v 9.0' {_a}").AssertFailed(1, "expected newer version of sed");
    }

    [Fact]
    public void MissingScriptFile_IsPanicExitFour()   // FIX (was "can't read", exit 2)
    {
        var missing = "'" + Path.Combine(_dir, "nosuch.sed").Replace("'", "''") + "'";
        Run($"Invoke-BashSed '-f' {missing} {_a}").AssertFailed(4, "couldn't open file", "No such file or directory");
    }

    [Fact]
    public void ScriptFile_AndHashN()
    {
        var sc = F("sc.sed", "2p\n");
        var sn = F("sn.sed", "#n\n2p\n");
        Assert.Equal(new[] { "b2" }, Run($"Invoke-BashSed '-n' '-f' {sc} {_a}").AssertSuccess().Lines);
        Assert.Equal(new[] { "b2" }, Run($"Invoke-BashSed '-f' {sn} {_a}").AssertSuccess().Lines);   // `#n` first line = -n
        Assert.Equal(new[] { "b2", "b2" }, Run($"Invoke-BashSed '-n' '-e' '2p' '-f' {sc} {_a}").AssertSuccess().Lines);
        Assert.Equal(new[] { "b2" }, Run($"Invoke-BashSed '-n' '--file={sc.Trim('\'')}' {_a}").AssertSuccess().Lines);
    }

    [Fact]
    public void InPlace_SuffixIsAttachedOnly_AndMakesABackup()   // FIX (no backup; -i.bak was read as flags)
    {
        Run($"Invoke-BashSed '-i.bak' 's/a/A/' {_a}").AssertSuccess();
        Assert.Equal("A1\nb2\nc3\n", Read("a.txt"));
        Assert.Equal("a1\nb2\nc3\n", Read("a.txt.bak"));

        Run($"Invoke-BashSed '-i' '-e' 's/A/a/' {_a}").AssertSuccess();   // `-i -e x`: no suffix
        Assert.Equal("a1\nb2\nc3\n", Read("a.txt"));
        Assert.Equal(new[] { "a.txt", "a.txt.bak", "b.txt" }, Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(x => x).ToArray());

        Run($"Invoke-BashSed '--in-place=.b2' 's/a/Z/' {_a}").AssertSuccess();
        Assert.Equal("Z1\nb2\nc3\n", Read("a.txt"));
        Assert.Equal("a1\nb2\nc3\n", Read("a.txt.b2"));
    }

    [Fact]
    public void InPlace_StarInSuffix_IsTheBaseName_AndDirPartIsRelativeToTheFile()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "bak"));
        Run($"Invoke-BashSed '-ibak/*.o' 's/a/A/' {_a}").AssertSuccess();
        Assert.Equal("A1\nb2\nc3\n", Read("a.txt"));
        Assert.Equal("a1\nb2\nc3\n", File.ReadAllText(Path.Combine(_dir, "bak", "a.txt.o")));
    }

    [Fact]
    public void InPlace_KeepsAMissingFinalNewline_AndEmptiesAFileWhoseLinesAllVanish()
    {
        Run($"Invoke-BashSed '-i' '2d' {_b}").AssertSuccess();
        Assert.Equal("d4\nf6", Read("b.txt"));
        var one = F("one.txt", "x\n");
        Run($"Invoke-BashSed '-i' '1d' {one}").AssertSuccess();
        Assert.Equal("", Read("one.txt"));   // GNU: an empty file, not a lone newline
    }

    [Fact]
    public void InPlace_WithoutFiles_IsAPanic()
    {
        Run("Invoke-BashSed '-n' '-i' 'p'").AssertFailed(4, "no input files");
    }

    [Fact]
    public void InPlace_TwoFiles_AreEditedSeparately()
    {
        Run($"Invoke-BashSed '-i' '1d' {_a} {_b}").AssertSuccess();
        Assert.Equal("b2\nc3\n", Read("a.txt"));
        Assert.Equal("e5\nf6", Read("b.txt"));
    }

    [Fact]
    public void NullData_TreatsNulAsTheRecordSeparator()   // FIX (-z was ignored)
    {
        // a.txt has no NUL: one record "a1\nb2\nc3\n", no terminator is added to the output.
        Assert.Equal("a1,b2,c3,", Bytes($"Invoke-BashSed '-z' 's/\\n/,/g' {_a}"));
        Assert.Equal("a1\nb2\nc3\n", Bytes($"Invoke-BashSed '-z' '-n' 'p' {_a}"));
        var n = F("n.txt", "x\0y\0");
        Assert.Equal("X\0y\0", Bytes($"Invoke-BashSed '-z' '1s/x/X/' {n}"));
        Assert.Equal("a,b,", Bytes("'a','b' | Invoke-BashSed '-z' 's/\\n/,/g'"));
    }

    [Fact]
    public void StdinOperandDash_ReadsThePipeline()
    {
        Assert.Equal(new[] { "x", "x" }, Run("'x' | Invoke-BashSed 'p' '-'").AssertSuccess().Lines);
    }

    [Fact]
    public void AcceptedNoOps_DoNotChangeTheOutput()
    {
        foreach (var flag in new[] { "-u", "-b", "--posix", "--sandbox", "--follow-symlinks", "--unbuffered", "--binary" })
            Assert.Equal(new[] { "b2" }, Run($"Invoke-BashSed '-n' '{flag}' '2p' {_a}").AssertSuccess().Lines);
        Assert.Equal(new[] { "b2" }, Run($"Invoke-BashSed '-n' '-l' '5' '2p' {_a}").AssertSuccess().Lines);
    }

    // ---- direct PowerShell calls: bare flags through the proxy, in original order ----

    [Fact]
    public void Direct_RepeatedE_CommaList_AndUpperE()
    {
        Assert.Equal(new[] { "b2", "c3" }, Run($"Invoke-BashSed -n -e 2p -e 3p {_a}").AssertSuccess().Lines);
        Assert.Equal(new[] { "b2", "c3" }, Run($"Invoke-BashSed -n -e 2p,3p {_a}").AssertSuccess().Lines);
        Assert.Equal(new[] { "aX", "b2", "c3" }, Run($"Invoke-BashSed -E 's/(a)1/\\1X/' {_a}").AssertSuccess().Lines);
        Assert.Equal(new[] { "b2" }, Run($"Invoke-BashSed -n -r 2p {_a}").AssertSuccess().Lines);
    }

    [Fact]
    public void Direct_EThenPipelineSegmentFlags_DoNotLeak()
    {
        // The old raw-line scan would have read grep's `-e` as a second sed expression.
        Assert.Equal(new[] { "B2" }, Run($"Invoke-BashSed -e 's/b/B/' {_a} | Invoke-BashGrep -e B").AssertSuccess().Lines);
    }
}
