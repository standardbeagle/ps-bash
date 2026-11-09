using PsBash.Spec;

namespace PsBash.Spec.Tests;

/// <summary>Runs every <c>spec/*.spec.sh</c> case against ps-bash; an XFAIL case that passes is an error (XPASS).</summary>
public class CorpusTests
{
private static readonly object DumpLock = new();
    private static string SpecDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "spec"))) d = d.Parent;
        return d is null ? throw new DirectoryNotFoundException("spec/ not found above " + AppContext.BaseDirectory) : Path.Combine(d.FullName, "spec");
    }

    public static IEnumerable<object[]> Files()
        => Directory.GetFiles(SpecDir(), "*.spec.sh", SearchOption.AllDirectories).OrderBy(p => p)
            .Select(p => new object[] { Path.GetRelativePath(SpecDir(), p).Replace('\\', '/') });

    private static int SpecParallelism()
        => int.TryParse(Environment.GetEnvironmentVariable("PSBASH_SPEC_PAR"), out var n) && n > 0 ? n : 4;

    /// <summary>Appends <c>mode par file cases ms</c> to PSBASH_SPEC_TIMING so runs can be compared.</summary>
    private static void LogTiming(string name, int cases, long ms)
    {
        if (Environment.GetEnvironmentVariable("PSBASH_SPEC_TIMING") is not { Length: > 0 } log) return;
        lock (typeof(CorpusTests))
            File.AppendAllText(log, $"{PsBashExecutor.Mode} par={SpecParallelism()} {name} cases={cases} ms={ms}\n");
    }

    // Bounded, resumable runs (tman kills a job at its max-time and loses everything):
    //   PSBASH_SPEC_BUDGET_MIN=<n>  stop starting new files after n minutes; the rest are skipped, not failed.
    //   PSBASH_SPEC_RESULTS=<file>  one "PASS|FAIL|SKIP<TAB>name<TAB>ms" line per file, appended as each finishes.
    //   PSBASH_SPEC_RESUME=1        skip files the results file already records as PASS.
    private static readonly System.Diagnostics.Stopwatch SuiteClock = System.Diagnostics.Stopwatch.StartNew();

    private static string? ResultsFile => Environment.GetEnvironmentVariable("PSBASH_SPEC_RESULTS") is { Length: > 0 } f ? f : null;

    private static bool OverBudget()
        => double.TryParse(Environment.GetEnvironmentVariable("PSBASH_SPEC_BUDGET_MIN"), out var m) && m > 0
           && SuiteClock.Elapsed.TotalMinutes > m;

    private static bool AlreadyPassed(string name)
    {
        if (Environment.GetEnvironmentVariable("PSBASH_SPEC_RESUME") != "1" || ResultsFile is not { } f || !File.Exists(f)) return false;
        return File.ReadLines(f).Any(l => l.StartsWith("PASS\t" + name + "\t", StringComparison.Ordinal));
    }

    private static void Record(string verdict, string name, long ms)
    {
        if (ResultsFile is not { } f) return;
        lock (typeof(CorpusTests)) File.AppendAllText(f, $"{verdict}\t{name}\t{ms}\n");
    }

    [Fact]
    public void Resume_and_budget_are_inert_by_default()
    {
        // With no env set a full run must neither skip nor stop early.
        if (Environment.GetEnvironmentVariable("PSBASH_SPEC_BUDGET_MIN") is null) Assert.False(OverBudget());
        if (Environment.GetEnvironmentVariable("PSBASH_SPEC_RESUME") is null) Assert.False(AlreadyPassed("x"));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task Spec_file_conforms(string name)
    {
        if (AlreadyPassed(name)) { Record("SKIP", name, 0); return; }
        if (OverBudget()) { Record("SKIP", name, 0); return; }
        var fileClock = System.Diagnostics.Stopwatch.StartNew();
        try { await RunFileAsync(name); }
        catch { Record("FAIL", name, fileClock.ElapsedMilliseconds); throw; }
        Record("PASS", name, fileClock.ElapsedMilliseconds);
    }

    private static async Task RunFileAsync(string name)
    {
        string path = Path.Combine(SpecDir(), name);
        var file = SpecParser.Parse(name, File.ReadAllText(path));
        // Import: PSBASH_SPEC_IMPORT=<oils spec file>, run once with --filter on a (new, empty) spec/oils/NAME.spec.sh.
        if (Environment.GetEnvironmentVariable("PSBASH_SPEC_IMPORT") is { Length: > 0 } src && file.Cases.Count == 0)
        {
            file = SpecImporter.Import(path, File.ReadAllText(src), Path.GetFileName(src));
            File.WriteAllText(path, SpecParser.Format(file));
        }
        // Bulk import: PSBASH_SPEC_IMPORT_DIR=<dir of oils *.test.sh>; every empty spec/oils/NAME.spec.sh takes NAME.test.sh from it.
        if (Environment.GetEnvironmentVariable("PSBASH_SPEC_IMPORT_DIR") is { Length: > 0 } dir && file.Cases.Count == 0)
        {
            var bulkSrc = Path.Combine(dir, Path.GetFileName(path).Replace(".spec.sh", ".test.sh"));
            if (File.Exists(bulkSrc))
            {
                file = SpecImporter.Import(path, File.ReadAllText(bulkSrc), Path.GetFileName(bulkSrc));
                File.WriteAllText(path, SpecParser.Format(file));
            }
        }
        // Merge import: PSBASH_SPEC_IMPORT_MERGE=1 with PSBASH_SPEC_IMPORT_DIR adds the cases a file lacks (by name) to a non-empty spec/oils file.
        if (Environment.GetEnvironmentVariable("PSBASH_SPEC_IMPORT_MERGE") == "1"
            && Environment.GetEnvironmentVariable("PSBASH_SPEC_IMPORT_DIR") is { Length: > 0 } mergeDir && file.Cases.Count > 0
            && name.StartsWith("oils/", StringComparison.Ordinal))
        {
            var mergeSrc = Path.Combine(mergeDir, Path.GetFileName(path).Replace(".spec.sh", ".test.sh"));
            if (File.Exists(mergeSrc))
            {
                var merged = SpecImporter.Merge(file, SpecImporter.Import(path, File.ReadAllText(mergeSrc), Path.GetFileName(mergeSrc)));
                if (!ReferenceEquals(merged, file)) { file = merged; File.WriteAllText(path, SpecParser.Format(file)); }
            }
        }
        bool triage = Environment.GetEnvironmentVariable("PSBASH_SPEC_TRIAGE") == "1";
        if (Environment.GetEnvironmentVariable("PSBASH_SPEC_RECORD") == "1")
        {
            // Fill cases that have no expectation yet from real bash (WSL); existing expectations are kept.
            file = await SpecRecorder.RecordAsync(file, new WslBashExecutor(), all: Environment.GetEnvironmentVariable("PSBASH_SPEC_RECORD_ALL") == "1");
            File.WriteAllText(path, SpecParser.Format(file));
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = await SpecRunner.RunAsync(file, Executors.Get(), parallelism: SpecParallelism(), launcher: new PsBashExecutor());
        LogTiming(name, file.Cases.Count, sw.ElapsedMilliseconds);
        foreach (var slow in results.OrderByDescending(r => r.Actual?.WallMs ?? 0).Take(4))
            LogTiming($"  slowest[{slow.Actual?.WallMs}ms] {slow.Case.Name}", 0, 0);
        if (triage)
        {
            // Mark every failing case as a known gap, and unmark any that now pass, so the file records the true state.
            var marked = file.Cases.Select((c, i) => results[i].Verdict switch
            {
                Verdict.Fail => c with { Xfail = new XfailMark("triage", "differs from bash 5.2 (auto-marked; investigate)") },
                Verdict.XPass => c with { Xfail = null },
                _ => c,
            }).ToList();
            File.WriteAllText(path, SpecParser.Format(file with { Cases = marked }));
            return;
        }
        // Fidelity guard (spec-suite.md 3.3): the in-host fast path must agree with the real launcher.
        // Sampled: the first case of the file plus every 25th.
        if (PsBashExecutor.Mode == "inhost")
        {
            for (var i = 0; i < file.Cases.Count; i += 25)
            {
                var c = file.Cases[i];
                if (c.RealLauncher) continue; // already ran through the launcher; nothing to compare
                var viaLauncher = SpecRunner.Judge(c, await new PsBashExecutor().RunAsync(c.Script, c.Timeout ?? SpecRunner.DefaultTimeout, default));
                Assert.True(viaLauncher.Verdict == results[i].Verdict,
                    $"fast path and launcher disagree on '{c.Name}': in-host {results[i].Verdict}, launcher {viaLauncher.Verdict}");
            }
        }
        // Triage aid: PSBASH_SPEC_DUMP_XFAIL=<file> appends every known gap with its expected and actual output.
        if (Environment.GetEnvironmentVariable("PSBASH_SPEC_DUMP_XFAIL") is { Length: > 0 } dumpPath)
            lock (DumpLock)
                File.AppendAllText(dumpPath, string.Concat(results.Where(r => r.Verdict == Verdict.XFail).Select(r => $"##### {name}\n{r.Diff}\n=====\n")));
        var bad = results.Where(r => r.Verdict is Verdict.Fail or Verdict.XPass).ToList();
        Assert.True(bad.Count == 0, SpecRunner.Summarize(file, results) + "\n\n" +
            string.Join("\n---\n", bad.Select(b => $"[{b.Verdict}] {b.Diff}")));
    }
}
