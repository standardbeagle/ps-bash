using System.Diagnostics;
using System.Management.Automation;
using System.Text;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// In-process throughput bench for the phase-2b streaming lane (PERF task
/// 01KXQ0KMG5C26BWXNVPZXBVA6H). Measures the INTERNAL fused throughput — the work
/// inside <c>Invoke-BashFusedPipeline</c> — without the IPC return path (that is
/// phase-2a's fixed win, already measured). Compares:
///   • phase-2a  : <c>Invoke-BashFusedPipeline { inner }</c> (delegate + batch; one
///                 PSObject per line inside the inner pipeline)
///   • phase-2b  : <c>Invoke-BashFusedPipeline -Stages @(…) -Fallback { throw }</c>
///                 (streaming line→line cores; no per-line PSObject)
/// Reports lines/sec and process-wide GC allocation for each, at 100k and 1M, plus the
/// epic's headline shape <c>cat f | grep x | sort</c> measured LITERALLY (real file
/// operand, 20 000 lines) against the S0 baseline in
/// <c>docs/specs/compiled-plan-lane.md</c> §1.4.
///
/// Opt-in only (Trait "bench") so it never runs in the normal suite; drive it with
///   dotnet test … --filter "FullyQualifiedName~FusedStreamingBench"
/// The result table is written to %TEMP%\psbash-phase2b-bench.txt.
/// </summary>
public class FusedStreamingBench : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    public FusedStreamingBench(SharedPwshFixture fixture) => _fixture = fixture;

    private (double seconds, long allocBytes, int frames) Time(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.Commands.Clear();
        // Warm once (JIT + regex compile) — discard.
        pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();

        long a0 = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        var result = pwsh.AddScript(script).Invoke();
        sw.Stop();
        long a1 = GC.GetTotalAllocatedBytes(precise: true);
        pwsh.Commands.Clear();
        return (sw.Elapsed.TotalSeconds, a1 - a0, result.Count);
    }

    [Trait("bench", "phase2b")]
    [Fact]
    public void Bench_StreamingVsDelegate()
    {
        // Opt-in only: the full sweep is ~4-5 min. In the normal suite this returns
        // immediately. Drive it with:  PSBASH_BENCH=1 dotnet test … --filter FusedStreamingBench
        if (Environment.GetEnvironmentVariable("PSBASH_BENCH") != "1") return;

        var sb = new StringBuilder();
        sb.AppendLine("PHASE-2b FUSED STREAMING BENCH (in-process, no IPC)");
        sb.AppendLine($"machine: {Environment.ProcessorCount} cores, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        sb.AppendLine("cfg: Debug build. lines/sec = producer lines / elapsed.");
        sb.AppendLine();
        sb.AppendLine($"{"chain",-16}{"N",10}{"2a l/s",14}{"2b l/s",14}{"speedup",9}{"2a MB",10}{"2b MB",10}");

        (string tag, string inner, string stages)[] chains =
        {
            ("seq|sed",  "Invoke-BashSeq 1 {0} | Invoke-BashSed 's/1/X/'",  "@(@('seq','1','{0}'),@('sed','s/1/X/'))"),
            ("seq|grep", "Invoke-BashSeq 1 {0} | Invoke-BashGrep 1",        "@(@('seq','1','{0}'),@('grep','1'))"),
            ("seq|cat",  "Invoke-BashSeq 1 {0} | Invoke-BashCat",           "@(@('seq','1','{0}'),@('cat'))"),
        };
        int[] sizes = { 100_000, 1_000_000 };

        foreach (var (tag, innerFmt, stagesFmt) in chains)
        {
            foreach (var n in sizes)
            {
                var inner = string.Format(innerFmt, n);
                var stages = string.Format(stagesFmt, n);
                var a2 = Time($"$null = (Invoke-BashFusedPipeline {{ {inner} }})");
                var b2 = Time($"$null = (Invoke-BashFusedPipeline -Stages {stages} -Fallback {{ throw 'fb' }})");
                double lps2a = n / a2.seconds;
                double lps2b = n / b2.seconds;
                sb.AppendLine(
                    $"{tag,-16}{n,10}{lps2a,14:N0}{lps2b,14:N0}{lps2b / lps2a,8:N1}x{a2.allocBytes / 1e6,10:N1}{b2.allocBytes / 1e6,10:N1}");
            }
        }

        // Output-heavy end-to-end (100k) proxy: seq|sed|wc-l.
        {
            int n = 100_000;
            var inner = $"Invoke-BashSeq 1 {n} | Invoke-BashSed 's/1/X/' | Invoke-BashWc -l";
            var stages = $"@(@('seq','1','{n}'),@('sed','s/1/X/'),@('wc','-l'))";
            var a2 = Time($"$null = (Invoke-BashFusedPipeline {{ {inner} }})");
            var b2 = Time($"$null = (Invoke-BashFusedPipeline -Stages {stages} -Fallback {{ throw 'fb' }})");
            sb.AppendLine();
            sb.AppendLine($"e2e seq|sed|wc-l 100k: 2a={a2.seconds * 1000:N0}ms  2b={b2.seconds * 1000:N0}ms  (2a alloc {a2.allocBytes / 1e6:N1}MB, 2b {b2.allocBytes / 1e6:N1}MB)");
        }

        // ── S2: the ACTUAL S0 shape, `cat f | grep x | sort` ────────────────────
        //
        // This is the chain the fan-out epic is ordered around and the one S0 measured
        // (docs/specs/compiled-plan-lane.md §1.4: 20 000-line producer, fused 35.9 MB /
        // 9 objects vs unfused 34.0 MB / 20 000 objects). S0's load-bearing finding was
        // that batching left ALLOCATION untouched — the fused lane still ran the
        // fallback PowerShell pipeline internally, allocating one PSObject per line.
        // Closing that is exactly what the S2/S3 streaming cores are for, so the number
        // to compare against 35.9 MB is the 2b ALLOCATION column below.
        //
        // The shape is measured LITERALLY — a real file operand, not a `seq` substitute.
        // Both halves of it now have cores (`sort` in S2, `cat FILE` in the 6d52b29
        // follow-up), so `-Stages` no longer declines; the `-Fallback { throw }` proves
        // the streaming lane produced the bytes rather than the scriptblock lane.
        //
        // Harness caveat, stated rather than glossed: S0's figure came from
        // scripts/profile-fanout-baseline.ps1, not from this bench. Both measure total
        // managed allocation in-process over the same 20 000-line shape, but the corpus
        // contents and surrounding harness differ, so treat the comparison as
        // order-of-magnitude evidence about the LANE, not a controlled A/B of one number.
        {
            const int n = 20_000;                       // S0's producer size, matched.
            var corpus = WriteBenchCorpus(n);
            var q = corpus.Replace("'", "''");
            var inner = $"Invoke-BashCat '{q}' | Invoke-BashGrep x | Invoke-BashSort";
            var stages = $"@(@('cat','{q}'),@('grep','x'),@('sort'))";
            try
            {
                var a2 = Time($"$null = (Invoke-BashFusedPipeline {{ {inner} }})");
                var b2 = Time($"$null = (Invoke-BashFusedPipeline -Stages {stages} -Fallback {{ throw 'fb' }})");
                sb.AppendLine();
                sb.AppendLine($"S2 cat f|grep x|sort {n:N0} (the S0 shape, S0 fused baseline 35.9 MB):");
                sb.AppendLine($"    2a delegate  {a2.seconds * 1000,8:N0}ms  alloc {a2.allocBytes / 1e6,8:N1}MB");
                sb.AppendLine($"    2b streaming {b2.seconds * 1000,8:N0}ms  alloc {b2.allocBytes / 1e6,8:N1}MB"
                            + $"   ({a2.seconds / b2.seconds:N1}x time, {(double)a2.allocBytes / b2.allocBytes:N1}x alloc)");
            }
            finally
            {
                try { File.Delete(corpus); } catch { /* best-effort */ }
            }
        }

        // Second S2 chain, kept on `seq` deliberately: it isolates the sort+uniq cores
        // from the file producer, so a regression in either can be told apart from one
        // in CatFileStage.
        {
            int n = 100_000;
            var inner = $"Invoke-BashSeq 1 {n} | Invoke-BashSort | Invoke-BashUniq";
            var stages = $"@(@('seq','1','{n}'),@('sort'),@('uniq'))";
            var a2 = Time($"$null = (Invoke-BashFusedPipeline {{ {inner} }})");
            var b2 = Time($"$null = (Invoke-BashFusedPipeline -Stages {stages} -Fallback {{ throw 'fb' }})");
            sb.AppendLine();
            sb.AppendLine($"S2 seq|sort|uniq 100k: 2a={a2.seconds * 1000:N0}ms  2b={b2.seconds * 1000:N0}ms  "
                        + $"({a2.seconds / b2.seconds:N1}x)  alloc 2a={a2.allocBytes / 1e6:N1}MB 2b={b2.allocBytes / 1e6:N1}MB");
        }

        var outPath = Path.Combine(Path.GetTempPath(), "psbash-phase2b-bench.txt");
        File.WriteAllText(outPath, sb.ToString());
        // Also surface it if the run shows output.
        Assert.True(File.Exists(outPath), sb.ToString());
    }

    /// <summary>
    /// Throughput REGRESSION guard for the phase-2b streaming lane (task
    /// 01KXQQ9QN4EVH1NA21EBE4PDZ8). Locks in the fused-pipeline wins measured on a
    /// quiet-box Release build so a future refactor that quietly reintroduces
    /// per-line PSObject/IPC framing fails CI instead of shipping.
    ///
    /// Opt-in only, behind the SAME <c>PSBASH_BENCH=1</c> gate as the full bench, so
    /// the default suite (and CI) is unaffected. Drive it with:
    ///   <code>
    ///   PSBASH_BENCH=1 dotnet test src/PsBash.Cmdlets.Tests -f net10.0 \
    ///       --filter "FullyQualifiedName~FusedStreaming"
    ///   </code>
    ///
    /// Thresholds are 25% of the user-signed-off target (>=1M l/s for grep/cat-class),
    /// i.e. a 250k l/s FLOOR — deliberately generous so ordinary box variance can't
    /// flake it. Measured internal (no-IPC) 2b l/s on the quiet-box Release run at
    /// N=100k: cat ~9.3M, grep ~340k, sed ~590k. Each chain is measured best-of-3: a
    /// floor asserts PEAK capability ("not catastrophically slow"), so a transient
    /// load spike on the timed run cannot drive it red. sed does NOT reach the 1M
    /// target (whole-input buffering + full per-line cycle engine — see the class doc
    /// and the task return), so it is floored at 25% of its own Release measurement.
    /// </summary>
    [Trait("bench", "phase2b")]
    [Fact]
    public void FusedStreaming_ThroughputRegression()
    {
        if (Environment.GetEnvironmentVariable("PSBASH_BENCH") != "1") return;

        const int n = 100_000;

        double catLps = BestStreamingLps(n, $"@(@('seq','1','{n}'),@('cat'))");
        double grepLps = BestStreamingLps(n, $"@(@('seq','1','{n}'),@('grep','1'))");
        double sedLps = BestStreamingLps(n, $"@(@('seq','1','{n}'),@('sed','s/1/X/'))");

        // grep/cat-class: >=25% of the 1M l/s target.
        Assert.True(catLps >= 250_000, $"cat streaming {catLps:N0} l/s < 250k floor (25% of 1M target)");
        Assert.True(grepLps >= 250_000, $"grep streaming {grepLps:N0} l/s < 250k floor (25% of 1M target)");
        // sed: >=25% of its Release measurement (~590k l/s @100k) = ~147k, floored at 140k.
        Assert.True(sedLps >= 140_000, $"sed streaming {sedLps:N0} l/s < 140k floor (25% of ~590k Release measurement)");
    }

    /// <summary>
    /// Write an <paramref name="n"/>-line corpus for the <c>cat f | grep x | sort</c>
    /// bench and return its path. Roughly half the lines contain an <c>x</c>, so both
    /// the filter and the sort do real work; the content is deterministic so two runs of
    /// the bench are comparable. MUST end in a newline — <c>CatFileStage</c> declines a
    /// file whose last line has no terminator, which would silently push the whole chain
    /// back onto the fallback and make the bench measure 2a twice.
    /// </summary>
    private static string WriteBenchCorpus(int n)
    {
        var path = Path.Combine(Path.GetTempPath(), $"psbash-bench-corpus-{Guid.NewGuid():N}.txt");
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            // Descending key so `sort` cannot short-circuit on already-ordered input.
            sb.Append(i % 2 == 0 ? "xray-" : "alpha-").Append(n - i).Append('\n');
        }
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    /// <summary>Best-of-3 internal (no-IPC) streaming lines/sec for a fused stage chain.</summary>
    private double BestStreamingLps(int n, string stages)
    {
        double best = 0;
        for (int i = 0; i < 3; i++)
        {
            var t = Time($"$null = (Invoke-BashFusedPipeline -Stages {stages} -Fallback {{ throw 'fb' }})");
            best = Math.Max(best, n / t.seconds);
        }
        return best;
    }
}
