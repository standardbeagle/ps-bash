namespace PsBash.Spec;

/// <summary>Runs one script and returns what it printed. The Phase 1 implementation spawns the launcher; the in-host executor replaces it.</summary>
public interface IScriptExecutor
{
    Task<ExecResult> RunAsync(string script, TimeSpan timeout, CancellationToken cancel);
}

/// <summary>Runs the cases of a spec file and judges each against its expectation (docs/specs/spec-suite.md sections 3, 5, 6).</summary>
public static class SpecRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A known gap is only checked for "still fails / now passes"; a hang must not cost its whole timeout every run.</summary>
    public static readonly TimeSpan KnownGapTimeout = TimeSpan.FromSeconds(3);

    /// <summary>A real-launcher case may have to cold-start a host (about 2-3 s), so a known-gap cap below that only measures start-up.</summary>
    public static readonly TimeSpan RealLauncherGapTimeout = TimeSpan.FromSeconds(12);

    public static TimeSpan TimeoutFor(SpecCase c)
    {
        var limit = c.Timeout ?? DefaultTimeout;
        var cap = c.RealLauncher ? RealLauncherGapTimeout : KnownGapTimeout;
        return c.Xfail is not null && limit > cap ? cap : limit;
    }

    public static async Task<IReadOnlyList<CaseResult>> RunAsync(
        SpecFile file, IScriptExecutor executor, int parallelism = 4, CancellationToken cancel = default,
        IScriptExecutor? launcher = null)
    {
        var results = new CaseResult[file.Cases.Count];
        using var gate = new SemaphoreSlim(Math.Max(1, parallelism));
        // Launcher cases share one daemon endpoint and each nests another launcher: run concurrently they
        // fan out across executor slots, each a cold host, and the cold starts eat the case timeouts.
        using var launcherGate = new SemaphoreSlim(1);
        var tasks = file.Cases.Select(async (c, index) =>
        {
            var viaLauncher = c.RealLauncher && launcher is not null;
            if (viaLauncher) await launcherGate.WaitAsync(cancel);
            try
            {
            await gate.WaitAsync(cancel);
            try
            {
                ExecResult actual;
                // `## real-launcher`: the case needs a whole shell process (interactive shell, own stdin), not the shared in-host runspace.
                var runner = c.RealLauncher && launcher is not null ? launcher : executor;
                try { actual = await runner.RunAsync(c.Script, TimeoutFor(c), cancel); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    actual = new ExecResult(-1, "", "runner error: " + ex.Message);
                }
                results[index] = Judge(c, actual);
            }
            finally { gate.Release(); }
            }
            finally { if (viaLauncher) launcherGate.Release(); }
        });
        await Task.WhenAll(tasks);
        return results;
    }

    /// <summary>The verdict for one case: an XFAIL that passes is XPass (an error, so the gap list only shrinks).</summary>
    public static CaseResult Judge(SpecCase c, ExecResult actual)
    {
        bool ok = Matches(c.Primary, actual) || c.Alternatives.Any(a => Matches(a, actual));
        var verdict = (ok, c.Xfail is not null) switch
        {
            (true, false) => Verdict.Pass,
            (false, false) => Verdict.Fail,
            (false, true) => Verdict.XFail,
            (true, true) => Verdict.XPass,
        };
        return new CaseResult(c, verdict, actual, verdict is Verdict.Fail or Verdict.XPass ? Describe(c, actual) : null);
    }

    public static bool Matches(Expectation e, ExecResult actual)
        => (e.Stdout is null || Normalize(actual.Stdout) == e.Stdout)
           && (e.Status is null || actual.ExitCode == e.Status)
           && (e.Stderr is null || NormalizeStderr(actual.Stderr) == e.Stderr);

    /// <summary>Line endings are not part of a case; the launcher's stdout may carry CR LF on Windows.</summary>
    public static string Normalize(string s) => s.Replace("\r\n", "\n");

    /// <summary>Messages carry the shell name and script line, which differ by shell and are not behaviour.</summary>
    public static string NormalizeStderr(string s)
        => System.Text.RegularExpressions.Regex.Replace(Normalize(s), @"^(?:\S*/)?(?:ps-)?bash: (?:line \d+: )?", "", System.Text.RegularExpressions.RegexOptions.Multiline);

    private static string Describe(SpecCase c, ExecResult actual)
    {
        var e = c.Primary;
        var sb = new System.Text.StringBuilder();
        sb.Append("case: ").Append(c.Name).Append('\n');
        if (e.Stdout is not null) sb.Append("expected stdout: ").Append(Show(e.Stdout)).Append("\n  actual stdout: ").Append(Show(Normalize(actual.Stdout))).Append('\n');
        if (e.Status is not null) sb.Append("expected status: ").Append(e.Status).Append(", actual: ").Append(actual.ExitCode).Append('\n');
        if (e.Stderr is not null) sb.Append("expected stderr: ").Append(Show(e.Stderr)).Append("\n  actual stderr: ").Append(Show(NormalizeStderr(actual.Stderr))).Append('\n');
        else if (actual.Stderr.Length > 0) sb.Append("stderr: ").Append(Show(actual.Stderr)).Append('\n');
        sb.Append("script:\n").Append(c.Script);
        return sb.ToString();
    }

    private static string Show(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";

    /// <summary>One summary line per file: <c>name pass=N xfail=N xpass=N fail=N</c>.</summary>
    public static string Summarize(SpecFile file, IReadOnlyList<CaseResult> results)
    {
        int Count(Verdict v) => results.Count(r => r.Verdict == v);
        return $"{Path.GetFileName(file.Path)} pass={Count(Verdict.Pass)} xfail={Count(Verdict.XFail)} xpass={Count(Verdict.XPass)} fail={Count(Verdict.Fail)}";
    }
}
