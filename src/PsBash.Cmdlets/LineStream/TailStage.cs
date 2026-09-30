using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// <c>tail</c> pipeline mode for the fused streaming lane (S3 of the fan-out epic).
///
/// <para><b>FOLLOW MUST DECLINE, and this core is the second, independent barrier.</b> The
/// fused executor runs the inner chain to COMPLETION before it returns anything, so a
/// followed stage would buffer forever — a silent hang, with no error message to debug, in a
/// shape where the unfused lane streams live. <c>PsEmitter.StageIsUnbounded</c> is the
/// emitter-side name check; until S3 it was the ONLY barrier, and it held only by ACCIDENT —
/// <c>tail</c> had no core for a follow argv to reach. Adding a core removes that accident, so
/// <see cref="TryCreate"/> refuses every follow spelling itself: <c>-f</c>, <c>-F</c>,
/// <c>--follow</c>, <c>--follow=…</c>, <c>--retry</c>, and an <c>f</c>/<c>F</c> hidden inside a
/// short-flag bundle. The guarantee must not rest on one string comparison in a different
/// project.</para>
///
/// <para><b>Blocking or lazy, depending on the form.</b> <c>-n +N</c> ("from line N onward") is
/// a skip-then-stream transform and stays lazy. Plain <c>-n N</c> cannot be: the last N lines
/// are unknown until the input ends, so <see cref="Run"/> drains its producer into a ring
/// buffer of N entries (bounded by the COUNT, not the input — unlike <see cref="SortStage"/>,
/// whose buffer is the whole stream) before yielding. A downstream <c>head</c> therefore
/// cannot early-exit past a plain <c>tail</c>; the unfused cmdlet has exactly the same
/// property.</para>
///
/// <para><b>Certified argv subset:</b> <c>-n N</c> / <c>-nN</c> / <c>-n +N</c> / <c>-n+N</c>,
/// the legacy <c>-N</c> shorthand, a bare positional number, <c>--lines=N</c> /
/// <c>--lines N</c> (including the <c>+N</c> forms), and <c>-q</c> / <c>--quiet</c> /
/// <c>--silent</c> as accepted no-ops. DECLINED: every follow spelling (above), <c>-c</c> byte
/// mode (the cmdlet's pipeline branch runs BEFORE its byte branch, so <c>-c</c> is silently
/// ignored on a pipe — an oddity worth declining rather than reproducing), <c>-s</c> /
/// <c>--sleep-interval</c> (meaningful only with follow), <c>-v</c>, <c>--</c>, <c>--help</c> /
/// <c>--version</c>, file operands (file mode emits typed <c>PsBash.CatLine</c> objects, not
/// bare lines), and any non-numeric or negative count.</para>
///
/// <para><b>Was a documented quirk, now fixed in BOTH lanes:</b> <c>tail -n 0</c> used to emit ONE
/// line (the ring buffer is sized <c>Math.Max(count, 1)</c>); GNU emits nothing, and so do the
/// cmdlet and this core now.</para>
/// </summary>
internal sealed class TailStage : ILineStreamStage
{
    private readonly int _count;
    private readonly bool _fromLine;

    private TailStage(int count, bool fromLine) { _count = count; _fromLine = fromLine; }

    /// <summary>The certified subset has no non-zero exit path (file-read failure is file
    /// mode, which is declined).</summary>
    public int ExitCode => 0;

    internal static ILineStreamStage? TryCreate(string[] argv)
    {
        // ---- the follow guard, first and unconditional ----
        foreach (var a in argv)
            if (IsFollowToken(a)) return null;

        // The cmdlet's own resolver (shared ordered parser + NUM rules) decides next, so this core
        // can NEVER accept an argv the cmdlet would reject or interpret differently.
        var plan = InvokeBashTailCommand.Plan(argv);
        if (plan.Declined || plan.BytesMode || plan.Follow || plan.Operands.Count > 0) return null;

        // Certified subset within that: -n N / -nN / -n +N / -NUM / --lines / a bare positional
        // number (resolved by Plan) and -q/--quiet/--silent no-ops. No -c, -s, `--`.
        foreach (var tok in plan.Parsed.Tokens)
        {
            if (tok.Kind == ArgTokKind.DoubleDash) return null;
            if (tok.Kind == ArgTokKind.Option
                && tok.OptId != "lines" && tok.OptId != "num" && tok.OptId != "quiet") return null;
        }
        return new TailStage(plan.Count, plan.FromLine);
    }

    /// <summary>
    /// Every spelling that puts <c>tail</c> into never-terminating follow mode. Mirrors (and
    /// deliberately duplicates) <c>PsEmitter.StageIsUnbounded</c>'s rule so the two barriers
    /// are independent: a hang has no error message, so one check is not enough.
    /// </summary>
    private static bool IsFollowToken(string a)
    {
        // --follow, --follow=…, and any getopt_long abbreviation (--f, --fo, --foll, …).
        if (a.Length >= 3 && a.StartsWith("--", StringComparison.Ordinal))
        {
            int eq = a.IndexOf('=');
            var name = eq >= 0 ? a.Substring(0, eq) : a;
            if (name.Length >= 3 && "--follow".StartsWith(name, StringComparison.Ordinal)) return true;
        }
        if (a == "--retry" || a == "--follow-retry") return true;
        // Any short-flag group carrying f/F: -f, -F, -qf, -fn, …
        if (a.Length >= 2 && a[0] == '-' && a[1] != '-'
            && (a.IndexOf('f') >= 0 || a.IndexOf('F') >= 0)) return true;
        // Obsolete `+NUM[bcl]f`.
        return a.Length >= 3 && a[0] == '+' && char.IsAsciiDigit(a[1]) && a[^1] == 'f';
    }

    /// <summary>
    /// Port of the cmdlet's pipeline branch. Note it emits the ORIGINAL record for a
    /// single-line item and the SUB-LINE for a multi-line one — which is why this stage does
    /// its own expansion instead of using <see cref="LineStreamRecord.SubLines"/> (that helper
    /// returns the TRIMMED text, and trimming here would drop a byte the cmdlet keeps).
    /// </summary>
    public IEnumerable<string> Run(IEnumerable<string> input)
    {
        if (_fromLine)
        {
            int skip = _count - 1;
            int idx = 0;
            foreach (var item in input)
            {
                string trimmed = item.TrimEnd('\n');
                if (trimmed.Contains('\n'))
                {
                    foreach (var subLine in trimmed.Split('\n'))
                    {
                        if (idx >= skip) yield return subLine;
                        idx++;
                    }
                }
                else
                {
                    if (idx >= skip) yield return item;
                    idx++;
                }
            }
            yield break;
        }

        // ---- the buffer: N entries, not the whole stream (see the class remarks) ----
        if (_count == 0) yield break; // GNU: 	ail -n 0 prints nothing
        int cap = Math.Max(_count, 1);
        var buf = new string[cap];
        int bufLen = 0, pos = 0;
        foreach (var item in input)
        {
            string trimmed = item.TrimEnd('\n');
            if (trimmed.Contains('\n'))
            {
                foreach (var subLine in trimmed.Split('\n'))
                {
                    buf[pos] = subLine;
                    pos = (pos + 1) % cap;
                    if (bufLen < cap) bufLen++;
                }
            }
            else
            {
                buf[pos] = item;
                pos = (pos + 1) % cap;
                if (bufLen < cap) bufLen++;
            }
        }

        int start = bufLen < cap ? 0 : pos;
        for (int k = 0; k < bufLen; k++) yield return buf[(start + k) % cap];
    }
}
