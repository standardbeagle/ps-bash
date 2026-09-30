using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

/// <summary>
/// <c>uniq</c> pipeline mode for the fused streaming lane (S2 of the fan-out epic).
///
/// <para><b>This stage IS lazy</b>, including <c>-c</c>. The slice brief assumed
/// <c>uniq -c</c> is blocking like <c>sort</c>; it is not, and neither is the real
/// cmdlet — <c>uniq</c> only ever needs the CURRENT RUN of adjacent equal lines
/// (previous line, previous key, run count), so the buffer is three fields, not the
/// input. A run's representative is emitted as soon as the run ends, and the final
/// run is flushed at end of input. <c>… | uniq | head -n 3</c> therefore still
/// early-exits its producer, exactly like the other streaming cores. (The blocking
/// caveat in this wave belongs to <see cref="SortStage"/> alone.)</para>
///
/// <para><b>Parity oracle is <see cref="InvokeBashUniqCommand"/></b>, not bash: the
/// key derivation (<c>-f</c>/<c>-s</c>/<c>-w</c>), the run-flush ordering, and the
/// <c>-c</c> count format (<c>"{0,7} {1}"</c>) are ports of the cmdlet's, and the
/// byte-parity tests diff both paths over the certified argv. The logic is duplicated
/// rather than shared because this slice's file scope did not include the cmdlet
/// file.</para>
///
/// <para><b>Certified argv subset:</b> every argv the cmdlet accepts with no file operands and
/// no <c>-D</c>/<c>--all-repeated</c> — <c>-c -d -u -i -f N -s N -w N</c> (bundled, joined, long,
/// abbreviated, obsolete <c>-N</c>). The cmdlet's own <see cref="InvokeBashUniqCommand.Plan"/>
/// resolves and validates the argv for both lanes. DECLINED: file operands, <c>-D</c> /
/// <c>--all-repeated</c> (group-separator methods live in the cmdlet), a bad number, an
/// unsupported option (<c>-z</c>, <c>--group</c>), <c>--help</c> / <c>--version</c>, unknown flags.</para>
/// </summary>
internal sealed class UniqStage : ILineStreamStage
{
    private readonly bool _countMode, _duplicatesOnly, _uniqueOnly, _ignoreCase;
    private readonly int _skipFields, _skipChars, _checkChars;

    private UniqStage(bool countMode, bool duplicatesOnly, bool uniqueOnly, bool ignoreCase,
                      int skipFields, int skipChars, int checkChars)
    {
        _countMode = countMode; _duplicatesOnly = duplicatesOnly; _uniqueOnly = uniqueOnly;
        _ignoreCase = ignoreCase; _skipFields = skipFields; _skipChars = skipChars; _checkChars = checkChars;
    }

    /// <summary>uniq's certified subset has no non-zero exit path (a file-read failure
    /// is the cmdlet's only exit-1, and file mode is declined).</summary>
    public int ExitCode => 0;

    /// <summary>Argv gate: the cmdlet's own resolver decides, so this core can NEVER accept an argv
    /// the cmdlet would reject or read differently. Declined: file operands (file mode), scan or
    /// value errors, --help/--version, and -D/--all-repeated (its group-separator methods are the
    /// cmdlet's).</summary>
    internal static ILineStreamStage? TryCreate(string[] argv)
    {
        var plan = InvokeBashUniqCommand.Plan(argv);
        if (plan.Declined || plan.Operands.Count > 0 || plan.AllRepeated) return null;
        return new UniqStage(plan.Count, plan.Repeated, plan.Unique, plan.IgnoreCase,
                             plan.SkipFields, plan.SkipChars, plan.CheckChars);
    }
    /// <summary>
    /// Lazy adjacent dedup. State is the current run only (<c>prevLine</c>,
    /// <c>prevKey</c>, <c>runCount</c>) — no buffering of the input, so a downstream
    /// early-exit still propagates upstream.
    /// </summary>
    public IEnumerable<string> Run(IEnumerable<string> input)
    {
        string? prevLine = null;
        string? prevKey = null;
        int runCount = 0;

        foreach (var item in input)
        {
            // Same record expansion as the cmdlet's ProcessRecord: trailing newline
            // trimmed, an embedded-newline record split into lines. Fused producers
            // yield one bare line, so this is a no-op in the normal case.
            string trimmed = item.TrimEnd('\n');
            if (trimmed.Contains('\n'))
            {
                foreach (var subLine in trimmed.Split('\n'))
                {
                    string key = GetUniqKey(subLine, _skipFields, _skipChars, _checkChars);
                    if (prevKey != null && SameKey(key, prevKey)) { runCount++; continue; }
                    if (TryFlush(prevLine, runCount, out var flushed)) yield return flushed!;
                    prevLine = subLine; prevKey = key; runCount = 1;
                }
                continue;
            }

            string k = GetUniqKey(trimmed, _skipFields, _skipChars, _checkChars);
            if (prevKey != null && SameKey(k, prevKey)) { runCount++; continue; }
            if (TryFlush(prevLine, runCount, out var f)) yield return f!;
            prevLine = trimmed; prevKey = k; runCount = 1;
        }

        if (TryFlush(prevLine, runCount, out var last)) yield return last!;
    }

    private bool SameKey(string key, string prevKey)
        => _ignoreCase
            ? string.Equals(key, prevKey, StringComparison.OrdinalIgnoreCase)
            : string.Equals(key, prevKey, StringComparison.Ordinal);

    /// <summary>Port of the cmdlet's <c>FlushRun</c> (minus the declined <c>-D</c>
    /// branch): emit the run's representative unless <c>-d</c>/<c>-u</c> filter it
    /// out, prefixed by the GNU-width count under <c>-c</c>.</summary>
    private bool TryFlush(string? prevLine, int runCount, out string? text)
    {
        text = null;
        if (prevLine == null) return false;
        if (_duplicatesOnly && runCount < 2) return false;
        if (_uniqueOnly && runCount > 1) return false;
        text = _countMode ? string.Format("{0,7} {1}", runCount, prevLine) : prevLine;
        return true;
    }

    // ----- key derivation (port of InvokeBashUniqCommand's) -----

    private static readonly Regex s_wsRun = new(@"\s+", RegexOptions.Compiled);

    private static string GetUniqKey(string line, int skipFields, int skipChars, int checkChars)
    {
        string key = line;

        if (skipFields > 0)
        {
            int unboundedFieldCount = CountWhitespaceFields(key);
            key = unboundedFieldCount > skipFields ? ConsumeSkipFields(line, skipFields) : "";
        }

        if (skipChars > 0 && key.Length > skipChars) key = key.Substring(skipChars);
        else if (skipChars > 0) key = "";

        // -w N compares at most N chars; N = 0 compares NOTHING (GNU), -1 = unlimited.
        if (checkChars >= 0 && key.Length > checkChars) key = key.Substring(0, checkChars);

        return key;
    }

    private static int CountWhitespaceFields(string s)
    {
        int runs = 0;
        foreach (var _ in s_wsRun.EnumerateMatches(s)) runs++;
        return runs + 1;
    }

    private static string ConsumeSkipFields(string line, int skipFields)
    {
        int idx = 0;
        int len = line.Length;
        for (int n = 0; n < skipFields; n++)
        {
            while (idx < len && !char.IsWhiteSpace(line[idx])) idx++;
            while (idx < len && char.IsWhiteSpace(line[idx])) idx++;
        }
        if (idx >= len) return "";
        return line.Substring(idx);
    }
}
