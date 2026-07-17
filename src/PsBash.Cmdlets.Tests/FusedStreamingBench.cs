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
/// Reports lines/sec and process-wide GC allocation for each, at 100k and 1M.
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

        var outPath = Path.Combine(Path.GetTempPath(), "psbash-phase2b-bench.txt");
        File.WriteAllText(outPath, sb.ToString());
        // Also surface it if the run shows output.
        Assert.True(File.Exists(outPath), sb.ToString());
    }
}
