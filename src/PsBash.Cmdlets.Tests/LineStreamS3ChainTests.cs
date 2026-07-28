using System.Text;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// S3's DEFINITION OF DONE: chains, not commands. The fused streaming lane is
/// ALL-OR-NOTHING — a chain streams only if EVERY stage certifies — so "five cores added"
/// proves nothing on its own. S2 learned this the hard way: it added <c>sort</c> + <c>uniq</c>,
/// hit its numbers, and still missed its own criterion because <c>cat FILE</c> had been
/// quietly declining all along. The blocker had merely MOVED.
///
/// <para>Each test here runs the real <c>Invoke-BashFusedPipeline</c> with a <b>THROWING
/// <c>-Fallback</c></b> (the technique from
/// <c>LineStreamCatFileParityTests.Streamed_CatGrepSort_TheNinetyPercentShape_ByteIdenticalToUnfused</c>):
/// a silent fallback FAILS the test instead of passing quietly. The equality against the
/// unfused pipeline is the byte-parity half.</para>
/// </summary>
public class LineStreamS3ChainTests : LineStreamParityHarness, IDisposable
{
    private readonly string _dir;

    public LineStreamS3ChainTests(SharedPwshFixture fixture) : base(fixture)
    {
        _dir = Path.Combine(Path.GetTempPath(), "psbash-s3chain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string WriteUtf8(string name, string content)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, new UTF8Encoding(false).GetBytes(content));
        return p.Replace("'", "''");
    }

    private static string Corpus()
    {
        var sb = new StringBuilder();
        string[] words = { "xray", "alpha", "xenon", "beta", "xylem", "café", "x🚀", "xray", "delta", "echo",
                           "foxtrot", "golf", "hotel", "india", "juliet", "kilo" };
        foreach (var w in words) sb.Append(w).Append('\n');
        return sb.ToString();
    }

    private static string Tabbed()
    {
        var sb = new StringBuilder();
        for (int i = 1; i <= 12; i++) sb.Append("f").Append(i).Append('\t').Append("v").Append(i).Append('\t')
                                        .Append("w").Append(i).Append('\n');
        return sb.ToString();
    }

    // ── the five S3 chains: each STREAMS (throwing fallback) and is byte-identical ──

    [Fact]
    public void Streamed_CatTr_ByteIdenticalToUnfused()
    {
        var f = WriteUtf8("tr.txt", Corpus());
        AssertStreamedMatchesUnfused(
            $"Invoke-BashCat '{f}' | Invoke-BashTr 'a-z' 'A-Z'",
            $"@(@('cat','{f}'),@('tr','a-z','A-Z'))");
    }

    [Fact]
    public void Streamed_CatCut_ByteIdenticalToUnfused()
    {
        var f = WriteUtf8("cut.txt", Tabbed());
        AssertStreamedMatchesUnfused(
            $"Invoke-BashCat '{f}' | Invoke-BashCut '-f1'",
            $"@(@('cat','{f}'),@('cut','-f1'))");
    }

    [Fact]
    public void Streamed_CatTail_ByteIdenticalToUnfused()
    {
        var f = WriteUtf8("tail.txt", Corpus());
        AssertStreamedMatchesUnfused(
            $"Invoke-BashCat '{f}' | Invoke-BashTail -n 5",
            $"@(@('cat','{f}'),@('tail','-n','5'))");
    }

    [Fact]
    public void Streamed_CatTac_ByteIdenticalToUnfused()
    {
        var f = WriteUtf8("tac.txt", Corpus());
        AssertStreamedMatchesUnfused(
            $"Invoke-BashCat '{f}' | Invoke-BashTac",
            $"@(@('cat','{f}'),@('tac'))");
    }

    [Fact]
    public void Streamed_CatNl_ByteIdenticalToUnfused()
    {
        var f = WriteUtf8("nl.txt", Corpus());
        AssertStreamedMatchesUnfused(
            $"Invoke-BashCat '{f}' | Invoke-BashNl",
            $"@(@('cat','{f}'),@('nl'))");
    }

    // ── longer chains: the S3 cores composed with the S1/S2 ones ─────────────

    [Fact]
    public void Streamed_CatGrepCutSortUniq_ByteIdenticalToUnfused()
    {
        var f = WriteUtf8("mix.txt", Tabbed());
        AssertStreamedMatchesUnfused(
            $"Invoke-BashCat '{f}' | Invoke-BashGrep v | Invoke-BashCut '-f2' | Invoke-BashSort | Invoke-BashUniq",
            $"@(@('cat','{f}'),@('grep','v'),@('cut','-f2'),@('sort'),@('uniq'))");
    }

    [Fact]
    public void Streamed_CatSortTacNl_ByteIdenticalToUnfused()
    {
        var f = WriteUtf8("stn.txt", Corpus());
        AssertStreamedMatchesUnfused(
            $"Invoke-BashCat '{f}' | Invoke-BashSort | Invoke-BashTac | Invoke-BashNl",
            $"@(@('cat','{f}'),@('sort'),@('tac'),@('nl'))");
    }

    [Fact]
    public void Streamed_SeqTrHeadWc_ByteIdenticalToUnfused()
        => AssertStreamedMatchesUnfused(
            "Invoke-BashSeq 1 50 | Invoke-BashTr '0-9' 'x' | Invoke-BashHead -n 5 | Invoke-BashWc -l",
            "@(@('seq','1','50'),@('tr','0-9','x'),@('head','-n','5'),@('wc','-l'))");

    [Fact]
    public void Streamed_SeqTailNl_ByteIdenticalToUnfused()
        => AssertStreamedMatchesUnfused(
            "Invoke-BashSeq 1 40 | Invoke-BashTail -n 7 | Invoke-BashNl '-ba'",
            "@(@('seq','1','40'),@('tail','-n','7'),@('nl','-ba'))");

    // ── the decline path still lands on the SAME bytes ───────────────────────

    [Fact]
    public void Streamed_TailFollow_FallsBackToScriptblock()
    {
        // A followed stage must NEVER take the streaming lane: the fused executor only
        // returns once the inner chain completes, so it would buffer forever. Here the
        // fallback does the real work — and `tail -f` on PIPELINE input (no file operand)
        // terminates, so the case is safe to run.
        var inner = "Invoke-BashSeq 1 20 | Invoke-BashTail -f -n 3";
        Assert.Equal(
            RenderScript(inner),
            RenderScript($"Invoke-BashFusedPipeline -Stages @(@('seq','1','20'),@('tail','-f','-n','3')) "
                       + $"-Fallback {{ {inner} }}"));
    }

    [Fact]
    public void Streamed_CutUncertifiedFlag_FallsBackToScriptblock()
    {
        var f = WriteUtf8("fb.txt", Tabbed());
        var inner = $"Invoke-BashCat '{f}' | Invoke-BashCut -c 1-3";
        Assert.Equal(
            RenderScript(inner),
            RenderScript($"Invoke-BashFusedPipeline -Stages @(@('cat','{f}'),@('cut','-c','1-3')) "
                       + $"-Fallback {{ {inner} }}"));
    }
}
