namespace PsBash.Cmdlets;

/// <summary>
/// The <c>-OpenPlan</c> of an <c>Invoke-BashRedirect</c> stage: its targets in the order bash opens them, the table
/// descriptors its dups copy, and where each error is reported. bash applies a command's redirects left to right, so a
/// redirect that fails reports to wherever fd 2 points at that moment and the later redirects are never applied:
/// <c>echo hi 2&gt;/dev/null &gt; nodir/y</c> is silent (status 1), <c>2&gt;e &gt; nodir/y</c> writes the report into e,
/// <c>2&gt;&amp;1 &gt; nodir/y</c> to the original stdout, <c>&gt; nodir/y 2&gt;e</c> never creates e; the same for a dup
/// of a descriptor that is not open (<c>echo y 2&gt;/dev/null &gt;&amp;5</c> is silent, <c>&gt;&amp;5 2&gt;/dev/null</c>
/// shows "5: Bad file descriptor"). The emitter (PsEmitter.RedirectOpenPlan) writes a plan whenever the stage checks a
/// descriptor, or the order or a report target differs from the stage's own (superseded <c>-Truncate</c>, <c>-Touch</c>,
/// <c>-Path</c>, <c>-ErrorPath</c>, every report on stderr).
/// <para>Text: space-separated steps <c>ROLE[:REPORT]</c>. ROLE is <c>p</c> (-Path), <c>e</c> (-ErrorPath), <c>tN</c> /
/// <c>aN</c> (-Truncate / -Touch element N), or <c>dN</c>: a check that table descriptor N is open (a dup
/// <c>&gt;&amp;N</c> / <c>2&gt;&amp;N</c>; any number of them). REPORT is <c>null</c> (discarded), <c>out</c> (the stage's
/// output: the original stdout), <c>fdM</c> (table descriptor M, after <c>2&gt;&amp;M</c>) or the ROLE of an EARLIER
/// target; absent means stderr. See docs/specs/error-handling.md "Redirect open errors".</para>
/// </summary>
internal static class RedirectOpenPlan
{
    /// <summary>One step: a target to open or a descriptor to check (<see cref="IsCheck"/>), and where its error goes
    /// (null = stderr).</summary>
    public readonly record struct Step(string Role, string? Report);

    public const string Discard = "null";
    public const string Stdout = "out";

    /// <summary>True when <paramref name="role"/> is a check of table descriptor <paramref name="fd"/> (<c>dN</c>).</summary>
    public static bool IsCheck(string role, out int fd) => TryNumber(role, 'd', 1, out fd);

    /// <summary>True when <paramref name="report"/> names table descriptor <paramref name="fd"/> (<c>fdM</c>).</summary>
    public static bool IsDescriptorReport(string? report, out int fd)
    {
        fd = -1;
        return report is { Length: > 2 } && report[1] == 'd' && TryNumber(report, 'f', 2, out fd);
    }

    private static bool TryNumber(string text, char lead, int start, out int n)
    {
        n = -1;
        if (text.Length <= start || text[0] != lead) return false;
        for (int i = start; i < text.Length; i++)
            if (text[i] is < '0' or > '9') return false;
        return int.TryParse(text.AsSpan(start), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out n);
    }

    /// <summary>
    /// The steps of <paramref name="plan"/>, or null when it is not a plan for a stage with these targets (every target
    /// role exactly once, each report target opened before it): the stage then opens in its own order.
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
            bool check = IsCheck(role, out _);
            if (!check && (!expected.Contains(role) || !seen.Add(role))) return null;
            if (report is not null && report != Discard && report != Stdout && !IsDescriptorReport(report, out _)
                && (report == role || !seen.Contains(report)))
                return null;
            steps.Add(new Step(role, report));
        }
        return seen.Count == expected.Count ? steps : null;
    }
}
