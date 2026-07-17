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
}
