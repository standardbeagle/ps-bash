namespace PsBash.Cmdlets;

/// <summary>
/// The <c>-OpenPlan</c> of an <c>Invoke-BashRedirect</c> stage: its targets in the order bash opens them, and where each
/// open error is reported. bash applies a command's redirects left to right, so an open that fails reports to wherever
/// fd 2 points at that moment and the later redirects are never opened: <c>echo hi 2&gt;/dev/null &gt; nodir/y</c> is
/// silent (status 1), <c>2&gt;e &gt; nodir/y</c> writes the report into e, <c>2&gt;&amp;1 &gt; nodir/y</c> to the
/// original stdout, <c>&gt; nodir/y 2&gt;e</c> never creates e. The emitter (PsEmitter.RedirectOpenPlan) writes a plan
/// only when the order or a report target differs from the stage's own (superseded <c>-Truncate</c>, <c>-Touch</c>,
/// <c>-Path</c>, <c>-ErrorPath</c>, every report on stderr).
/// <para>Text: space-separated steps <c>ROLE[:REPORT]</c>. ROLE is <c>p</c> (-Path), <c>e</c> (-ErrorPath), <c>tN</c> /
/// <c>aN</c> (-Truncate / -Touch element N). REPORT is <c>null</c> (discarded), <c>out</c> (the stage's output: the
/// original stdout) or the ROLE of an EARLIER step (that target); absent means stderr. See docs/specs/error-handling.md
/// "Redirect open errors".</para>
/// </summary>
internal static class RedirectOpenPlan
{
    /// <summary>One target to open: its role, and where its open error goes (null = stderr).</summary>
    public readonly record struct Step(string Role, string? Report);

    public const string Discard = "null";
    public const string Stdout = "out";

    /// <summary>
    /// The steps of <paramref name="plan"/>, or null when it is not a plan for a stage with these targets (every role
    /// exactly once, each report target opened before it): the stage then opens in its own order.
    /// </summary>
    public static IReadOnlyList<Step>? Parse(string? plan, int truncate, int touch, bool path, bool errorPath)
    {
        if (string.IsNullOrWhiteSpace(plan)) return null;
        var expected = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < truncate; i++) expected.Add("t" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        for (int i = 0; i < touch; i++) expected.Add("a" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (path) expected.Add("p");
        if (errorPath) expected.Add("e");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var steps = new List<Step>();
        foreach (var token in plan.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = token.IndexOf(':');
            string role = colon < 0 ? token : token[..colon];
            string? report = colon < 0 ? null : token[(colon + 1)..];
            if (!expected.Contains(role) || !seen.Add(role)) return null;
            if (report is not null && report != Discard && report != Stdout && (report == role || !seen.Contains(report)))
                return null;
            steps.Add(new Step(role, report));
        }
        return seen.Count == expected.Count ? steps : null;
    }
}
