using System.Text.RegularExpressions;
using PsBash.Spec;

namespace PsBash.Spec.Tests;

/// <summary>
/// Every XFAIL in the corpus says WHY, and no expectation holds a value only the recording run had.
/// REGRESSION: absorbing Differential.Tests recorded bash's output once and kept it verbatim (the pids of
/// <c>true &amp; echo $!</c>, the oracle's temp script path in <c>${BASH_SOURCE[0]}</c>, <c>$USER</c>, <c>/dev/fd/63</c>), so
/// the cases could never pass, not even in bash; triage then marked them <c>differs from bash 5.2; see spec-gaps.md</c>,
/// which had no entry. Eight such XFAILs hid a real cost: ten process-mode jobs outran the 30 s case limit in-host,
/// and the fidelity check saw the in-host and launcher paths "agree" because both failed.
/// Oracle note (qa-rubric Directive 1): a corpus-structure invariant; behaviour stays with the cases themselves.
/// </summary>
public class SpecCorpusHygieneTests
{
    private static string SpecDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "spec"))) d = d.Parent;
        return d is null ? throw new DirectoryNotFoundException("spec/ not found above " + AppContext.BaseDirectory) : Path.Combine(d.FullName, "spec");
    }

    private static IEnumerable<(string File, SpecCase Case)> Cases()
    {
        foreach (var path in Directory.GetFiles(SpecDir(), "*.spec.sh", SearchOption.AllDirectories).OrderBy(p => p))
        {
            var rel = Path.GetRelativePath(SpecDir(), path).Replace('\\', '/');
            foreach (var c in SpecParser.Parse(path, File.ReadAllText(path)).Cases) yield return (rel, c);
        }
    }

    // The families of a reasoned XFAIL id (intentional-differences.md, spec-gaps.md). `triage` is what
    // PSBASH_SPEC_TRIAGE writes for a failing case: a note to investigate, never a committed verdict.
    private static readonly Regex KnownId = new(@"^(platform-windows|wontfix|intentional|arch|gap)-[a-z0-9-]+$", RegexOptions.Compiled);

    [Fact]
    public void EveryXfail_HasAReasonedId_NotATriageNote()
    {
        var offenders = new List<string>();
        foreach (var (file, c) in Cases())
        {
            if (c.Xfail is not { } x) continue;
            if (!KnownId.IsMatch(x.Id))
                offenders.Add($"{file} '{c.Name}': id '{x.Id}' is not platform-windows-*/wontfix-*/intentional-*/arch-*/gap-*");
            if (x.Reason.Contains("differs from bash 5.2", StringComparison.Ordinal) || x.Reason.Contains("auto-marked", StringComparison.Ordinal))
                offenders.Add($"{file} '{c.Name}': reason is the triage placeholder ('{x.Reason}'); say what differs and why");
        }
        Assert.True(offenders.Count == 0,
            "An XFAIL names its cause (an intentional-differences.md row, a spec-gaps.md section, or a platform fact):\n"
            + string.Join("\n", offenders));
    }

    // Locations only the recording run had: the oracle's Windows-side temp dir seen from WSL, mktemp names, the
    // recorder's own script directory. A case that needs a path prints a relative one or compares it ([ "$a" = "$b" ]).
    private static readonly Regex RecordingRunValue = new(@"/mnt/[a-z]/Users/|/tmp/tmp\.\w|/spec-record/", RegexOptions.Compiled);

    [Fact]
    public void NoExpectation_HoldsAValueOnlyTheRecordingRunHad()
    {
        var offenders = new List<string>();
        foreach (var (file, c) in Cases())
        {
            foreach (var e in c.Alternatives.Prepend(c.Primary))
            {
                foreach (var text in new[] { e.Stdout, e.Stderr })
                {
                    if (text is not null && RecordingRunValue.Match(text) is { Success: true } m)
                        offenders.Add($"{file} '{c.Name}': expectation holds '{m.Value}…'");
                }
            }
        }
        Assert.True(offenders.Count == 0,
            "A recorded expectation must be reproducible by any run, bash's included:\n" + string.Join("\n", offenders));
    }

    // Under PSBASH_BG_MODE=process each `&` starts a ps-bash-host (background-jobs.md §11): ~2 s idle, 10-15 s
    // on a loaded machine. Two sequential jobs (`false & wait $!; (exit 3) & wait $!`) then outran the 30 s default
    // in-host 4 times in 18 loaded runs, a flake that read as a product failure. Such a case states its budget.
    private static readonly TimeSpan ProcessJobBudget = TimeSpan.FromSeconds(120);

    [Fact]
    public void EveryProcessModeJobCase_DeclaresTheSpawnBudget()
    {
        var offenders = new List<string>();
        foreach (var (file, c) in Cases())
        {
            if (!c.Script.Contains("PSBASH_BG_MODE=process", StringComparison.Ordinal)) continue;
            if (c.Timeout is not { } t || t < ProcessJobBudget)
                offenders.Add($"{file} '{c.Name}': timeout {(c.Timeout?.TotalSeconds.ToString() ?? "default")} s");
        }
        Assert.True(offenders.Count == 0,
            $"A case that starts process-mode jobs pays a host spawn per `&`; give it `## timeout: {ProcessJobBudget.TotalSeconds}`:\n"
            + string.Join("\n", offenders));
    }
}
