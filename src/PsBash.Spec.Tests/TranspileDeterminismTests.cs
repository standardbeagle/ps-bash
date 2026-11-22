using PsBash.Core.Parser;
using PsBash.Spec;

namespace PsBash.Spec.Tests;

/// <summary>
/// The same bash text gives the same PowerShell, whatever was transpiled before it on the thread: every case of the spec
/// corpus and every eval fixture is transpiled after the case before it, then again after a unit that leaves the emitter's
/// counters raised (<c>v=$(foo || exit 1)</c>, a pipe stage's exit, a function's return, an errexit and-or), and the texts
/// must match. REGRESSION: <c>_leavingReturns</c> was thread state that no transpile reset, so a script emitted an errexit
/// and-or's final operand in <c>$( )</c> (buffering its output) or not, depending on the units before it. The field
/// inventory is <c>PsBash.Core.Tests.EmitterStateGuardTests</c>.
/// <para>Oracle note (qa-rubric Directive 1): a property of the emitter (the text is a function of the input); no bash
/// behavior is asserted here.</para>
/// </summary>
public class TranspileDeterminismTests
{
    private static string RepoDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "spec"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("spec/ not found above " + AppContext.BaseDirectory);
    }

    private static IEnumerable<(string Name, string Script)> Corpus()
    {
        string repo = RepoDir(), spec = Path.Combine(repo, "spec");
        foreach (var path in Directory.GetFiles(spec, "*.spec.sh", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(spec, path).Replace('\\', '/');
            foreach (var c in SpecParser.Parse(path, File.ReadAllText(path)).Cases) yield return (rel + ": " + c.Name, c.Script);
        }
        var fixtures = Path.Combine(repo, "tests", "fixtures", "eval");
        if (Directory.Exists(fixtures))
            foreach (var path in Directory.GetFiles(fixtures, "*.sh").OrderBy(p => p, StringComparer.Ordinal))
                yield return ("fixtures/eval/" + Path.GetFileName(path), File.ReadAllText(path));
    }

    /// <summary>Units that leave the emitter's counters and modes raised if a transpile does not reset them.</summary>
    private static readonly string[] Before =
    [
        "set -e\nv=$(foo || exit 1)",
        "set -e\nx | while :; do exit 1; done",
        "set -e\nf() { x || return 1; { return 6; } 2>/dev/null; }",
        "trap 'echo E' ERR\nset -o pipefail\nf() { [[ 1 ]] && { return 6; }; eval \"$c\" <<< x; seq 3 > >(tac); }",
    ];

    private static string TranspileText(string script)
    {
        try { return PsEmitter.Transpile(script) ?? "<null>"; }
        catch (Exception ex) { return "<" + ex.GetType().Name + ": " + ex.Message + ">"; }
    }

    [Fact]
    public void Transpile_SameText_GivesTheSamePowerShellWhateverCameBefore()
    {
        var corpus = Corpus().ToList();
        Assert.True(corpus.Count > 500, "expected the spec corpus, found " + corpus.Count + " cases");
        var differ = new List<string>();
        // One thread with a deep stack (the emitter recurses), as the host's pipeline thread has.
        var worker = new Thread(() =>
        {
            var first = new string[corpus.Count];
            for (int i = 0; i < corpus.Count; i++) first[i] = TranspileText(corpus[i].Script);
            for (int i = 0; i < corpus.Count; i++)
            {
                TranspileText(Before[i % Before.Length]);
                string again = TranspileText(corpus[i].Script);
                if (again != first[i]) differ.Add(corpus[i].Name + "\n    first: " + Around(first[i], again) + "\n    again: " + Around(again, first[i]));
            }
        }, 64 * 1024 * 1024);
        worker.Start();
        worker.Join();
        Assert.True(differ.Count == 0,
            differ.Count + " case(s) transpiled to different PowerShell after another unit (emitter thread state that no "
            + "transpile resets: PsEmitter.ResetUnitState / EmitterScope):\n  " + string.Join("\n  ", differ.Take(20)));
    }

    /// <summary>The text of <paramref name="a"/> around its first difference from <paramref name="b"/>.</summary>
    private static string Around(string a, string b)
    {
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        int from = Math.Max(0, i - 60);
        return "@" + i + " ..." + a.Substring(from, Math.Min(160, a.Length - from)) + "...";
    }
}
