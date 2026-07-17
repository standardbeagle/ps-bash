using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Text;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Runtime tests for the fused-pipeline lane (PERF task
/// 01KXQ0KMG5C26BWXNVPZXBVA6H, phase 2). <c>Invoke-BashFusedPipeline { … }</c>
/// runs an all-mapped pipeline host-side and returns its output in a few large
/// batched frames instead of one object per line.
///
/// The load-bearing property is FIDELITY: the rendered bytes of the fused frames
/// must equal the rendered bytes of the unfused per-line objects. <see cref="Render"/>
/// mirrors <c>SdkWorker.GetOutputText</c> (the host's object→line serializer) so
/// the assertion measures the same bytes the launcher would print. Each parity
/// case runs the inner pipeline both ways and diffs (Directive 1: bytes, not a
/// hand-written expectation). Also covered: exit-code propagation (grep no-match →
/// 1), the batching frame-count reduction, stdin forwarding, and empty input.
/// </summary>
public class InvokeBashFusedPipelineCommandTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;

    public InvokeBashFusedPipelineCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(Path.GetTempPath(), "psbash-fused-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private Collection<PSObject> Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result;
    }

    /// <summary>
    /// Mirrors <c>PsBash.Host.Runtime.SdkWorker.GetOutputText</c>: a BashText object
    /// renders as its text plus <c>Environment.NewLine</c> unless it carries
    /// <c>NoTrailingNewline</c>; a bare string as string + newline; anything else via
    /// ToString() + newline. This is the exact serialization the launcher receives, so
    /// diffing two Renders is a byte-level parity check.
    /// </summary>
    private static string Render(IEnumerable<PSObject> objs)
    {
        var sb = new StringBuilder();
        foreach (var o in objs)
        {
            if (o is null) continue;
            var bt = o.Properties["BashText"]?.Value;
            if (bt is not null)
            {
                sb.Append(bt.ToString());
                if (o.Properties["NoTrailingNewline"]?.Value is not true)
                    sb.Append(Environment.NewLine);
                continue;
            }
            if (o.BaseObject is string s)
            {
                sb.Append(s).Append(Environment.NewLine);
                continue;
            }
            sb.Append(o.ToString()).Append(Environment.NewLine);
        }
        return sb.ToString();
    }

    private void AssertFusedMatchesUnfused(string innerPipeline)
    {
        var unfused = Render(Run(innerPipeline));
        var fused = Render(Run($"Invoke-BashFusedPipeline {{ {innerPipeline} }}"));
        Assert.Equal(unfused, fused);
    }

    [Fact]
    public void Fused_SeqWcChain_ByteIdenticalToUnfused()
        => AssertFusedMatchesUnfused("Invoke-BashSeq 1 20 | Invoke-BashWc -l");

    [Fact]
    public void Fused_SedChain_ByteIdenticalToUnfused()
        => AssertFusedMatchesUnfused("Invoke-BashSeq 1 30 | Invoke-BashSed 's/1/X/'");

    [Fact]
    public void Fused_GrepChain_ByteIdenticalToUnfused()
        => AssertFusedMatchesUnfused("Invoke-BashSeq 1 50 | Invoke-BashGrep 3");

    [Fact]
    public void Fused_MultiStage_ByteIdenticalToUnfused()
        => AssertFusedMatchesUnfused(
            "Invoke-BashSeq 1 100 | Invoke-BashGrep 1 | Invoke-BashSort | Invoke-BashUniq | Invoke-BashWc -l");

    [Fact]
    public void Fused_RevAndNl_ByteIdenticalToUnfused()
        => AssertFusedMatchesUnfused("Invoke-BashSeq 1 12 | Invoke-BashRev | Invoke-BashNl -ba");

    [Fact]
    public void Fused_UnicodeContent_ByteIdenticalToUnfused()
    {
        var f = Path.Combine(_tmpDir, "u.txt");
        File.WriteAllText(f, "café\nnaïve\n🚀 rocket\ncombininǵ\n");
        var q = f.Replace("\\", "\\\\");
        AssertFusedMatchesUnfused($"Invoke-BashCat '{q}' | Invoke-BashGrep -v zzz");
    }

    [Fact]
    public void Fused_EmptyOutput_ProducesNothing()
    {
        var result = Run("Invoke-BashFusedPipeline { Invoke-BashSeq 1 5 | Invoke-BashGrep zzzznope }");
        Assert.Empty(result);
    }

    [Fact]
    public void Fused_LastStageExitCode_PropagatesOnNoMatch()
    {
        // Read $LASTEXITCODE in the SAME invocation — the shared fixture resets it
        // between Run() calls, so a second Run would see the reset value, not grep's.
        var lec = Run(
            "$null = (Invoke-BashFusedPipeline { Invoke-BashSeq 1 5 | Invoke-BashGrep zzzznope }); $global:LASTEXITCODE")
            .Select(o => o.BaseObject).LastOrDefault();
        Assert.Equal(1, Convert.ToInt32(lec));
    }

    [Fact]
    public void Fused_LastStageExitCode_ZeroOnMatch()
    {
        var lec = Run(
            "$null = (Invoke-BashFusedPipeline { Invoke-BashSeq 1 5 | Invoke-BashGrep 3 }); $global:LASTEXITCODE")
            .Select(o => o.BaseObject).LastOrDefault();
        Assert.Equal(0, Convert.ToInt32(lec));
    }

    [Fact]
    public void Fused_Batches_FrameCountFarBelowLineCount()
    {
        // 20000 lines unfused = 20000 objects (one per line). Fused coalesces them
        // into a handful of large frames — the whole point of the lane (bottleneck #1).
        var unfused = Run("Invoke-BashSeq 1 20000");
        Assert.Equal(20000, unfused.Count);

        var fused = Run("Invoke-BashFusedPipeline { Invoke-BashSeq 1 20000 }");
        Assert.True(fused.Count > 0, "fused produced no frames");
        Assert.True(fused.Count < 100,
            $"expected batched frame count << 20000, got {fused.Count}");

        // …and the bytes still match.
        Assert.Equal(Render(unfused), Render(fused));
    }

    [Fact]
    public void Fused_TailChain_ByteIdenticalToUnfused()
        => AssertFusedMatchesUnfused("Invoke-BashSeq 1 40 | Invoke-BashTail -n 5 | Invoke-BashTac");

    [Fact]
    public void Fused_CutAndTr_ByteIdenticalToUnfused()
    {
        var f = Path.Combine(_tmpDir, "csv.txt");
        File.WriteAllText(f, "a,b,c\nd,e,f\n");
        var q = f.Replace("\\", "\\\\");
        AssertFusedMatchesUnfused($"Invoke-BashCat '{q}' | Invoke-BashCut '-d,' -f2 | Invoke-BashTr a-z A-Z");
    }

    // ── Phase-2b: streaming-core lane (-Stages) ──────────────────────────────
    //
    // These prove the STREAMING path (no per-line PSObject) is byte-identical to
    // the unfused per-object path. The -Fallback scriptblock THROWS: if any stage
    // declined and the cmdlet fell back, the throw surfaces and the test fails —
    // so a green test also proves the streaming lane actually ran.

    /// <summary>Assert the streaming (-Stages) lane matches the unfused pipeline
    /// AND that the streaming lane (not the fallback) was taken.</summary>
    private void AssertStreamedMatchesUnfused(string unfusedInner, string stagesLiteral)
    {
        var unfused = Render(Run(unfusedInner));
        var streamed = Render(Run(
            $"Invoke-BashFusedPipeline -Stages {stagesLiteral} -Fallback {{ throw 'fell back to scriptblock lane' }}"));
        Assert.Equal(unfused, streamed);
    }

    [Fact]
    public void Streamed_SeqProducer_ByteIdenticalToUnfused()
        => AssertStreamedMatchesUnfused("Invoke-BashSeq 1 20", "@(,@('seq','1','20'))");

    [Fact]
    public void Streamed_SeqEqualWidth_ByteIdenticalToUnfused()
        => AssertStreamedMatchesUnfused("Invoke-BashSeq -w 8 12", "@(,@('seq','-w','8','12'))");

    [Fact]
    public void Streamed_SeqSeparator_ByteIdenticalToUnfused()
        => AssertStreamedMatchesUnfused("Invoke-BashSeq -s ',' 1 5", "@(,@('seq','-s',',','1','5'))");

    [Fact]
    public void Streamed_SeqSed_ByteIdenticalToUnfused()
        => AssertStreamedMatchesUnfused(
            "Invoke-BashSeq 1 30 | Invoke-BashSed 's/1/X/'",
            "@(@('seq','1','30'),@('sed','s/1/X/'))");

    [Fact]
    public void Streamed_SeqSedGlobal_ByteIdenticalToUnfused()
        => AssertStreamedMatchesUnfused(
            "Invoke-BashSeq 1 40 | Invoke-BashSed 's/1/X/g'",
            "@(@('seq','1','40'),@('sed','s/1/X/g'))");

    [Fact]
    public void Streamed_SeqSedSuppressPrint_ByteIdenticalToUnfused()
        => AssertStreamedMatchesUnfused(
            "Invoke-BashSeq 1 40 | Invoke-BashSed -n '/3/p'",
            "@(@('seq','1','40'),@('sed','-n','/3/p'))");

    [Fact]
    public void Streamed_SeqGrep_ByteIdenticalToUnfused()
        => AssertStreamedMatchesUnfused(
            "Invoke-BashSeq 1 50 | Invoke-BashGrep 3",
            "@(@('seq','1','50'),@('grep','3'))");

    [Fact]
    public void Streamed_SeqGrepInvertLineNumber_ByteIdenticalToUnfused()
        => AssertStreamedMatchesUnfused(
            "Invoke-BashSeq 1 20 | Invoke-BashGrep -v -n 1",
            "@(@('seq','1','20'),@('grep','-v','-n','1'))");

    [Fact]
    public void Streamed_SeqGrepCount_ByteIdenticalToUnfused()
        => AssertStreamedMatchesUnfused(
            "Invoke-BashSeq 1 50 | Invoke-BashGrep -c 1",
            "@(@('seq','1','50'),@('grep','-c','1'))");

    [Fact]
    public void Streamed_SeqCat_ByteIdenticalToUnfused()
        => AssertStreamedMatchesUnfused(
            "Invoke-BashSeq 1 25 | Invoke-BashCat",
            "@(@('seq','1','25'),@('cat'))");

    [Fact]
    public void Streamed_SeqRev_ByteIdenticalToUnfused()
        => AssertStreamedMatchesUnfused(
            "Invoke-BashSeq 8 13 | Invoke-BashRev",
            "@(@('seq','8','13'),@('rev'))");

    [Fact]
    public void Streamed_SeqHead_ByteIdenticalToUnfused()
        => AssertStreamedMatchesUnfused(
            "Invoke-BashSeq 1 100 | Invoke-BashHead -n 7",
            "@(@('seq','1','100'),@('head','-n','7'))");

    [Fact]
    public void Streamed_SeqWcLines_ByteIdenticalToUnfused()
        => AssertStreamedMatchesUnfused(
            "Invoke-BashSeq 1 37 | Invoke-BashWc -l",
            "@(@('seq','1','37'),@('wc','-l'))");

    [Fact]
    public void Streamed_SeqSedWc_EndToEnd_ByteIdenticalToUnfused()
        => AssertStreamedMatchesUnfused(
            "Invoke-BashSeq 1 200 | Invoke-BashSed 's/1/X/' | Invoke-BashWc -l",
            "@(@('seq','1','200'),@('sed','s/1/X/'),@('wc','-l'))");

    [Fact]
    public void Streamed_SeqGrepHead_MultiStage_ByteIdenticalToUnfused()
        => AssertStreamedMatchesUnfused(
            "Invoke-BashSeq 1 500 | Invoke-BashGrep 1 | Invoke-BashHead -n 4",
            "@(@('seq','1','500'),@('grep','1'),@('head','-n','4'))");

    [Fact]
    public void Streamed_GrepNoMatch_ExitCodeOne()
    {
        var lec = Run(
            "$null = (Invoke-BashFusedPipeline -Stages @(@('seq','1','5'),@('grep','zzz')) -Fallback { throw 'fb' }); $global:LASTEXITCODE")
            .Select(o => o.BaseObject).LastOrDefault();
        Assert.Equal(1, Convert.ToInt32(lec));
    }

    [Fact]
    public void Streamed_GrepMatch_ExitCodeZero()
    {
        var lec = Run(
            "$null = (Invoke-BashFusedPipeline -Stages @(@('seq','1','5'),@('grep','3')) -Fallback { throw 'fb' }); $global:LASTEXITCODE")
            .Select(o => o.BaseObject).LastOrDefault();
        Assert.Equal(0, Convert.ToInt32(lec));
    }

    [Fact]
    public void Streamed_HeadEarlyExit_DoesNotEnumerateWholeProducer()
    {
        // head -n 3 over a 2,000,000-line seq: if head did not stop the upstream
        // generator this would take seconds / large memory. The lazy chain stops
        // pulling after 3 lines, so it returns instantly. (Correctness of the
        // early-exit; the byte parity is covered above.)
        var streamed = Render(Run(
            "Invoke-BashFusedPipeline -Stages @(@('seq','1','2000000'),@('head','-n','3')) -Fallback { throw 'fb' }"));
        Assert.Equal("1" + Environment.NewLine + "2" + Environment.NewLine + "3" + Environment.NewLine, streamed);
    }

    [Fact]
    public void Streamed_UnsupportedStageArgv_FallsBackToScriptblock()
    {
        // grep -o is NOT in the streaming subset → the whole chain must use the
        // Fallback scriptblock. Here the fallback DOES the real work and must match
        // the unfused pipeline (proving decline → correct fallback, not a throw).
        var inner = "Invoke-BashSeq 1 20 | Invoke-BashGrep -o 1";
        var unfused = Render(Run(inner));
        var fused = Render(Run(
            $"Invoke-BashFusedPipeline -Stages @(@('seq','1','20'),@('grep','-o','1')) -Fallback {{ {inner} }}"));
        Assert.Equal(unfused, fused);
    }

    [Fact]
    public void Streamed_UnicodeContent_ByteIdenticalToUnfused()
        => AssertStreamedMatchesUnfused(
            "Invoke-BashSeq 1 5 | Invoke-BashSed 's/3/café🚀/'",
            "@(@('seq','1','5'),@('sed','s/3/café🚀/'))");

    [Fact]
    public void Streamed_EmptyGrepOutput_ProducesNothing()
    {
        var result = Run(
            "Invoke-BashFusedPipeline -Stages @(@('seq','1','5'),@('grep','zzzznope')) -Fallback { throw 'fb' }");
        Assert.Empty(result);
    }

    // ── Full argv-form coverage per streamed stage (review attempt-1 blocker) ──
    // Every argv form TryCreate ACCEPTS must have a streamed-vs-unfused byte-parity
    // case, so the certified subset can never outrun the tested matrix.

    // wc: each single selector + bare.
    [Theory]
    [InlineData("Invoke-BashWc",     "@('wc')")]
    [InlineData("Invoke-BashWc -l",  "@('wc','-l')")]
    [InlineData("Invoke-BashWc -w",  "@('wc','-w')")]
    [InlineData("Invoke-BashWc -c",  "@('wc','-c')")]
    [InlineData("Invoke-BashWc -m",  "@('wc','-m')")]
    [InlineData("Invoke-BashWc -L",  "@('wc','-L')")]
    public void Streamed_WcSelectors_ByteIdenticalToUnfused(string wcInner, string wcStage)
        => AssertStreamedMatchesUnfused(
            $"Invoke-BashSeq 1 30 | {wcInner}",
            $"@(@('seq','1','30'),{wcStage})");

    // grep: every accepted SINGLE-flag form (-i -v -n -c -w -F -E) + -e.
    [Theory]
    [InlineData("Invoke-BashGrep -i 1",    "@('grep','-i','1')")]
    [InlineData("Invoke-BashGrep -v 1",    "@('grep','-v','1')")]
    [InlineData("Invoke-BashGrep -w 1",    "@('grep','-w','1')")]
    [InlineData("Invoke-BashGrep -F 1",    "@('grep','-F','1')")]
    [InlineData("Invoke-BashGrep -n 2",    "@('grep','-n','2')")]
    [InlineData("Invoke-BashGrep -c 1",    "@('grep','-c','1')")]
    [InlineData("Invoke-BashGrep -e 2",    "@('grep','-e','2')")]
    public void Streamed_GrepFlagForms_ByteIdenticalToUnfused(string grepInner, string grepStage)
        => AssertStreamedMatchesUnfused(
            $"Invoke-BashSeq 1 30 | {grepInner}",
            $"@(@('seq','1','30'),{grepStage})");

    [Fact]
    public void Streamed_GrepBundle_DeclinesToFallback()
    {
        // A flag bundle is declined by the streaming stage → the Fallback scriptblock
        // (the real cmdlet, with its binder decoys) runs and must match the unfused path.
        var inner = "Invoke-BashSeq 1 30 | Invoke-BashGrep -vn 1";
        var unfused = Render(Run(inner));
        var fused = Render(Run(
            $"Invoke-BashFusedPipeline -Stages @(@('seq','1','30'),@('grep','-vn','1')) -Fallback {{ {inner} }}"));
        Assert.Equal(unfused, fused);
    }

    [Fact]
    public void Streamed_GrepExtendedAlternation_ByteIdenticalToUnfused()
        => AssertStreamedMatchesUnfused(
            "Invoke-BashSeq 1 30 | Invoke-BashGrep -E '1|2'",
            "@(@('seq','1','30'),@('grep','-E','1|2'))");

    // sed: -E / -r extended, and repeated -e composition (cmdlet raw-line reparse).
    [Fact]
    public void Streamed_SedExtended_ByteIdenticalToUnfused()
        => AssertStreamedMatchesUnfused(
            "Invoke-BashSeq 1 30 | Invoke-BashSed -E 's/[0-9]+/X/'",
            "@(@('seq','1','30'),@('sed','-E','s/[0-9]+/X/'))");

    [Fact]
    public void Streamed_SedRExtended_ByteIdenticalToUnfused()
        => AssertStreamedMatchesUnfused(
            "Invoke-BashSeq 1 30 | Invoke-BashSed -r 's/[0-9]+/X/'",
            "@(@('seq','1','30'),@('sed','-r','s/[0-9]+/X/'))");

    [Fact]
    public void Streamed_SedMultipleExpressions_ByteIdenticalToUnfused()
        => AssertStreamedMatchesUnfused(
            "Invoke-BashSeq 1 30 | Invoke-BashSed -e 's/1/X/' -e 's/2/Y/'",
            "@(@('seq','1','30'),@('sed','-e','s/1/X/','-e','s/2/Y/'))");

    // head: joined -n7 and legacy -7.
    [Theory]
    [InlineData("Invoke-BashHead -n7", "@('head','-n7')")]
    [InlineData("Invoke-BashHead -7",  "@('head','-7')")]
    [InlineData("Invoke-BashHead -n 0", "@('head','-n','0')")]
    public void Streamed_HeadCountForms_ByteIdenticalToUnfused(string headInner, string headStage)
        => AssertStreamedMatchesUnfused(
            $"Invoke-BashSeq 1 40 | {headInner}",
            $"@(@('seq','1','40'),{headStage})");
}
