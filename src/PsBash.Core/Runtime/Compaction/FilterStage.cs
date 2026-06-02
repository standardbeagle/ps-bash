using System.Text;
using System.Text.RegularExpressions;
using PsBash.Core.Runtime.Ipc;

namespace PsBash.Core.Runtime.Compaction;

/// <summary>
/// Runs a <see cref="FilterSpec"/>'s reduction pipeline against captured output.
/// Pure (no I/O). Stage order is fixed and mirrors tokf so its published fixtures are
/// a valid oracle: strip/trim → matchOutput (short-circuit) → replace → skip/keep →
/// dedup → success/failure template.
/// </summary>
internal static class FilterStage
{
    // Per-rule regex evaluation is bounded so a pathological pattern in a (user-authored)
    // spec cannot hang the pipeline (qa-rubric Directive 12, ReDoS).
    private static readonly TimeSpan RegexBudget = TimeSpan.FromMilliseconds(100);

    public static string Run(
        FilterSpec spec,
        string command,
        int exitCode,
        bool timedOut,
        IReadOnlyList<OutputFrame> frames,
        int maxLines)
    {
        var lines = CompactionText.SplitFrames(frames);
        // Header counts report the pre-reduction totals, matching OutputCompactor.
        var stdoutCount = lines.Count(l => l.Stream == StreamTag.Stdout);
        var stderrCount = lines.Count(l => l.Stream == StreamTag.Stderr);

        var sb = new StringBuilder();
        CompactionText.AppendHeader(sb, command, exitCode, timedOut, stdoutCount, stderrCount);

        // strip/trim — cleanup before any pattern matching.
        if (spec.StripAnsi)
            lines = lines.Select(l => l with { Text = CompactionText.StripAnsi(l.Text) }).ToList();
        if (spec.TrimLines)
            lines = lines.Select(l => l with { Text = l.Text.Trim() }).ToList();

        // matchOutput — whole-output substring check; first hit emits its template and stops.
        if (spec.MatchOutput.Count > 0)
        {
            var whole = string.Join("\n", lines.Select(l => l.Text));
            foreach (var rule in spec.MatchOutput)
            {
                if (whole.Contains(rule.Contains, StringComparison.OrdinalIgnoreCase))
                {
                    AppendBody(sb, rule.Emit);
                    return sb.ToString();
                }
            }
        }

        // replace — per-line regex, in order. A timed-out/invalid pattern leaves the line untouched.
        if (spec.Replace.Count > 0)
        {
            lines = lines.Select(l =>
            {
                var text = l.Text;
                foreach (var rule in spec.Replace)
                {
                    try { text = Regex.Replace(text, rule.Pattern, rule.With, RegexOptions.None, RegexBudget); }
                    catch (RegexMatchTimeoutException) { /* keep current text */ }
                    catch (ArgumentException) { /* invalid pattern — skip rule */ }
                }
                return l with { Text = text };
            }).ToList();
        }

        // keep — allow-list (when present); skip — drop-list.
        if (spec.Keep.Count > 0)
            lines = lines.Where(l => AnyMatch(spec.Keep, l.Text)).ToList();
        if (spec.Skip.Count > 0)
            lines = lines.Where(l => !AnyMatch(spec.Skip, l.Text)).ToList();

        // dedup — collapse consecutive duplicates.
        if (spec.Dedup)
            lines = CompactionText.CollapseRuns(lines);

        // Hard cap so a filter that keeps everything still can't blow the budget.
        if (lines.Count > maxLines)
            lines = lines.Take(maxLines).ToList();

        var body = string.Join(
            "\n",
            lines.Select(l => (l.Stream == StreamTag.Stderr ? "[err] " : "[out] ") + l.Text));

        var template = exitCode == 0 ? spec.OnSuccess : spec.OnFailure;
        // {{body}} is substituted exactly once; the inserted text is never re-scanned for
        // further placeholders (template-injection guard, Directive 12).
        var rendered = template is null ? body : template.Replace("{{body}}", body);

        AppendBody(sb, rendered);
        return sb.ToString();
    }

    private static void AppendBody(StringBuilder sb, string body)
    {
        sb.Append(body);
        if (body.Length == 0 || body[^1] != '\n') sb.Append('\n');
    }

    private static bool AnyMatch(IReadOnlyList<string> patterns, string text)
    {
        foreach (var pattern in patterns)
        {
            try { if (Regex.IsMatch(text, pattern, RegexOptions.None, RegexBudget)) return true; }
            catch (RegexMatchTimeoutException) { /* treat as no-match */ }
            catch (ArgumentException) { /* invalid pattern — no-match */ }
        }
        return false;
    }
}
