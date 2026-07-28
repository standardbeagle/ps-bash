using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

/// <summary>
/// <c>sort</c> pipeline mode for the fused streaming lane (S2 of the fan-out epic).
/// <c>cat f | grep x | sort</c> is the dominant real-world pipeline shape and it
/// could not stream at all before this stage existed: the lane is ALL-OR-NOTHING,
/// so one missing core dropped the whole chain back to the per-line-PSObject
/// scriptblock.
///
/// <para><b>THIS STAGE IS BLOCKING — it is NOT lazy.</b> Every other core in
/// <c>LineStreamStages.cs</c> is a streaming transform; a comparison sort cannot be.
/// <see cref="Run"/> drains the whole upstream enumerable into a
/// <c>List&lt;string&gt;</c> (the buffer — one entry per input line, bounded by the
/// input, held for the duration of the stage) BEFORE it yields its first line.
/// Two consequences, both deliberate:</para>
/// <list type="bullet">
/// <item>A DOWNSTREAM stage cannot early-exit past a sort. <c>… | sort | head -n 3</c>
/// still reads every upstream line, because the third smallest line is not known until
/// the last line has been read. That is what <c>sort</c> means; the unfused lane has
/// exactly the same property (the cmdlet buffers its whole pipeline input too).</item>
/// <item>Stages UPSTREAM of the sort are unaffected — they still stream into the
/// buffer one line at a time, and a <c>head</c> BEFORE the sort still stops its own
/// producer.</item>
/// </list>
///
/// <para><b>The real cmdlet is the parity oracle — not bash, not POSIX.</b> The
/// certified argv subset below is re-implemented to match
/// <see cref="InvokeBashSortCommand"/> byte-for-byte, INCLUDING its two known
/// pre-existing divergences from GNU sort (<c>-k</c> field model, <c>-V</c> version
/// ordering; see <c>scripts/sort-parity-check.ps1</c> and the sort row of
/// <c>docs/specs/runtime-command-reference.md</c>). <c>-V</c> is not in the certified
/// subset at all, and the <c>-k</c> key extraction here is a line-for-line port of the
/// cmdlet's — divergences must NOT be "fixed" here, because a fused/unfused split is
/// worse than a documented divergence. The logic is duplicated rather than shared
/// because this slice's file scope did not include <c>InvokeBashSortCommand.cs</c>;
/// the byte-parity tests (<c>LineStreamSortUniqParityTests</c>) run every certified
/// argv through BOTH paths and diff, so drift fails the build rather than shipping.</para>
///
/// <para><b>Certified argv subset</b> (anything else returns <c>null</c> from
/// <see cref="TryCreate"/> and the whole chain falls back — a core that guesses is a
/// correctness bug, a core that declines is merely slower):
/// <c>-r</c> reverse, <c>-n</c> numeric, <c>-u</c> unique, <c>-f</c> fold-case,
/// <c>-t SEP</c> / <c>-tSEP</c> field separator, <c>-k SPEC</c> / <c>-kSPEC</c> key
/// (with the cmdlet's per-key <c>n/r/R/b/B</c> modifiers), and bundles of
/// <c>r/n/u/f</c>. DECLINED: file operands (file mode), <c>--</c>, every long form,
/// <c>-o</c>/<c>--output</c>, and the flags whose comparators are not ported —
/// <c>-h -g -M -V -c -b -d -s -i</c> and any unknown flag.</para>
/// </summary>
internal sealed class SortStage : ILineStreamStage
{
    /// <summary>Port of <c>InvokeBashSortCommand.KeySpec</c>.</summary>
    private sealed class KeySpec
    {
        public int StartField;
        public int StartChar;
        public int EndField;
        public int EndChar;
        public bool Numeric;
        public bool Reverse;
        public bool BlankIgnore;
    }

    // Ported verbatim from InvokeBashSortCommand (same patterns, same options).
    private static readonly Regex s_numericPrefix = new(@"^\s*[+-]?\d+(?:\.\d+)?", RegexOptions.Compiled);
    private static readonly Regex s_keySpecPos = new(@"^(\d+)(?:\.(\d+))?([nrRbB]*)?$", RegexOptions.Compiled);

    private readonly bool _reverse;
    private readonly bool _numeric;
    private readonly bool _unique;
    private readonly bool _fold;
    private readonly string? _delimiter;
    private readonly List<KeySpec> _keys;

    private SortStage(bool reverse, bool numeric, bool unique, bool fold, string? delimiter, List<KeySpec> keys)
    {
        _reverse = reverse; _numeric = numeric; _unique = unique; _fold = fold;
        _delimiter = delimiter; _keys = keys;
    }

    /// <summary>sort never sets a non-zero code on the paths in the certified subset
    /// (<c>-c</c> check mode, the only exit-1 path, is declined). Valid after full
    /// enumeration, like every stage.</summary>
    public int ExitCode => 0;

    /// <summary>
    /// Argv gate. The scan order mirrors <see cref="InvokeBashSortCommand"/>'s exactly
    /// — notably <c>-t</c>/<c>-k</c> JOINED forms are matched BEFORE the short-flag
    /// bundle loop, so <c>-tr</c> is the separator <c>r</c> and not the bundle
    /// <c>-t -r</c>. Anything the cmdlet would treat as an operand, an error, or a
    /// non-ported flag declines.
    /// </summary>
    internal static ILineStreamStage? TryCreate(string[] argv)
    {
        bool reverse = false, numeric = false, unique = false, fold = false;
        string? delimiter = null;
        var keys = new List<KeySpec>();

        int i = 0;
        while (i < argv.Length)
        {
            var arg = argv[i];

            // `--` starts operands (file mode) — decline.
            if (arg == "--") return null;

            // -o FILE / -oFILE / --output… : writes a file instead of the pipeline.
            if (arg == "-o" || arg.StartsWith("--output", StringComparison.Ordinal)
                || (arg.Length > 2 && arg.StartsWith("-o", StringComparison.Ordinal)))
                return null;

            // -tSEP (joined) — before the bundle loop, exactly as the cmdlet.
            if (arg.Length > 2 && arg.StartsWith("-t", StringComparison.Ordinal))
            {
                delimiter = arg.Substring(2);
                i++;
                continue;
            }

            // -kSPEC (joined, digit-led) — the cmdlet's `^-k\d…` shape.
            if (arg.Length > 2 && arg.StartsWith("-k", StringComparison.Ordinal) && char.IsDigit(arg[2]))
            {
                keys.Add(ParseKeySpec(arg.Substring(2)));
                i++;
                continue;
            }

            if (arg == "-t")
            {
                i++;
                if (i >= argv.Length) return null; // degenerate trailing -t → let the cmdlet own it
                delimiter = argv[i];
                i++;
                continue;
            }

            if (arg == "-k")
            {
                i++;
                if (i >= argv.Length) return null; // degenerate trailing -k → cmdlet
                keys.Add(ParseKeySpec(argv[i]));
                i++;
                continue;
            }

            // Every long form (including the aliases of supported short flags) is
            // outside the certified subset.
            if (arg.StartsWith("--", StringComparison.Ordinal)) return null;

            if (arg.Length > 1 && arg[0] == '-')
            {
                foreach (char ch in arg.Substring(1))
                {
                    switch (ch)
                    {
                        case 'r': reverse = true; break;
                        case 'n': numeric = true; break;
                        case 'u': unique = true; break;
                        case 'f': fold = true; break;
                        // -h -g -M -V -c -b -d -s -i and anything unknown: the
                        // comparator/erroring paths are not ported. Decline.
                        default: return null;
                    }
                }
                i++;
                continue;
            }

            // A bare operand is a file (or the `-` stdin marker) → file mode. Decline.
            return null;
        }

        // `sort -t ''` is an exit-2 error in the cmdlet — an error path, not a sort.
        if (delimiter is { Length: 0 }) return null;

        return new SortStage(reverse, numeric, unique, fold, delimiter, keys);
    }

    /// <summary>
    /// Drain, sort, emit. <b>Blocking:</b> the <c>items</c> list below IS the buffer —
    /// the entire upstream stream is materialized before the first line is yielded
    /// (see the class remarks). Decorate-sort-undecorate mirrors the cmdlet: every
    /// sort key is precomputed once into parallel arrays, so the comparator does no
    /// key derivation and the result is identical to the cmdlet's by construction.
    /// </summary>
    public IEnumerable<string> Run(IEnumerable<string> input)
    {
        // ---- the buffer ----------------------------------------------------
        // `emit` holds the text each item will be written as; `raw` its sort text.
        // The cmdlet's pipeline path splits an item whose BashText contains an
        // embedded newline into one item per sub-line and emits the SUB-LINE, but
        // emits the ORIGINAL object (trailing newline intact) otherwise. Fused
        // stages yield one bare line at a time so the split never fires in practice,
        // but reproducing it keeps the two paths identical for any producer that
        // ever hands over a multi-line record.
        var emit = new List<string>();
        foreach (var item in input)
        {
            string trimmed = item.TrimEnd('\n');
            if (trimmed.Contains('\n'))
            {
                foreach (var subLine in trimmed.Split('\n')) emit.Add(subLine);
            }
            else
            {
                emit.Add(item);
            }
        }

        int n = emit.Count;
        var raw = new string[n];                                 // full compare text
        bool hasKeys = _keys.Count > 0;
        var globalText = hasKeys ? null : new string[n];
        var globalNum = (!hasKeys && _numeric) ? new double[n] : null;
        var keyText = hasKeys ? new string[n][] : null;
        var keyNum = hasKeys ? new double[n][] : null;

        for (int d = 0; d < n; d++)
        {
            string text = emit[d].TrimEnd('\n');                 // GetFullText (no -b in subset)
            raw[d] = text;
            if (hasKeys)
            {
                var kt = new string[_keys.Count];
                var kn = new double[_keys.Count];
                for (int s = 0; s < _keys.Count; s++)
                {
                    var spec = _keys[s];
                    string key = ExtractKeyText(text, spec, _delimiter);
                    kt[s] = key;
                    if (spec.Numeric || _numeric) kn[s] = ParseNumericPrefix(key);
                }
                keyText![d] = kt;
                keyNum![d] = kn;
            }
            else
            {
                globalText![d] = text;
                if (_numeric)
                {
                    // NOTE: the cmdlet parses the WHOLE global text (not a numeric
                    // prefix) for a keyless -n. Ported as-is — matching the cmdlet is
                    // the contract, even where it differs from GNU.
                    double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v);
                    globalNum![d] = v;
                }
            }
        }

        int Compare(int ai, int bi)
        {
            if (hasKeys)
            {
                for (int s = 0; s < _keys.Count; s++)
                {
                    var spec = _keys[s];
                    bool useNumeric = spec.Numeric || _numeric;
                    int cmp = CompareByMode(
                        useNumeric, _fold,
                        keyNum![ai][s], keyNum![bi][s],
                        keyText![ai][s], keyText![bi][s]);
                    if (spec.Reverse || _reverse) cmp = -cmp;
                    if (cmp != 0) return Math.Sign(cmp);
                }
                return 0;
            }

            int c = CompareByMode(
                _numeric, _fold,
                _numeric ? globalNum![ai] : 0.0, _numeric ? globalNum![bi] : 0.0,
                globalText![ai], globalText![bi]);
            if (_reverse) c = -c;
            return Math.Sign(c);
        }

        // GNU last-resort tie-break: equal keys compare the whole original line
        // (reversed by a global -r). -s (stable, which suppresses it) is declined.
        int LastResort(int ai, int bi)
        {
            int c = string.CompareOrdinal(raw[ai], raw[bi]);
            if (_reverse) c = -c;
            return Math.Sign(c);
        }

        var indexed = new List<int>(n);
        for (int idx = 0; idx < n; idx++) indexed.Add(idx);
        indexed.Sort((a, b) =>
        {
            int c = Compare(a, b);
            if (c != 0) return c;
            c = LastResort(a, b);
            if (c != 0) return c;
            return a - b;                                        // total order → deterministic
        });

        if (_unique)
        {
            // `sort -u` drops lines that COMPARE EQUAL under the active key, not
            // lines that are byte-equal (the whole-line LastResort is excluded from
            // the equality test) — the cmdlet's rule, ported.
            bool havePrev = false;
            int prev = -1;
            foreach (int idx in indexed)
            {
                if (havePrev && Compare(prev, idx) == 0) continue;
                prev = idx;
                havePrev = true;
                yield return emit[idx];
            }
            yield break;
        }

        foreach (int idx in indexed) yield return emit[idx];
    }

    /// <summary>Port of <c>InvokeBashSortCommand.CompareByMode</c> restricted to the
    /// certified subset's modes (numeric → fold → ordinal; no month). Reverse is
    /// applied by the caller.</summary>
    private static int CompareByMode(bool useNumeric, bool useFold, double numA, double numB, string textA, string textB)
    {
        if (useNumeric) return numA < numB ? -1 : (numA > numB ? 1 : 0);
        if (useFold) return string.Compare(textA, textB, StringComparison.OrdinalIgnoreCase);
        return string.CompareOrdinal(textA, textB);
    }

    // ----- key extraction (port of the cmdlet's, including its GNU field model) -----

    private static string ExtractKeyText(string text, KeySpec spec, string? delimiter)
    {
        string[] parts;
        string joinSep;
        if (delimiter != null)
        {
            parts = Regex.Split(text, Regex.Escape(delimiter));
            joinSep = delimiter;
        }
        else
        {
            parts = SplitWhitespaceFields(text);
            joinSep = "";
        }

        int startIdx = spec.StartField - 1;
        if (startIdx < 0) startIdx = 0;
        if (startIdx >= parts.Length) return "";
        int endIdx = spec.EndField > 0 ? spec.EndField - 1 : parts.Length - 1;
        if (endIdx >= parts.Length) endIdx = parts.Length - 1;
        if (endIdx < startIdx) endIdx = startIdx;

        string key;
        if (startIdx == endIdx)
        {
            key = ApplyCharOffsets(parts[startIdx], spec.StartChar, spec.EndChar);
        }
        else
        {
            var sb = new StringBuilder();
            for (int fi = startIdx; fi <= endIdx; fi++)
            {
                int startChar = fi == startIdx ? spec.StartChar : 0;
                int endChar = fi == endIdx ? spec.EndChar : 0;
                if (fi > startIdx) sb.Append(joinSep);
                sb.Append(ApplyCharOffsets(parts[fi], startChar, endChar));
            }
            key = sb.ToString();
        }

        // Global -b is declined, so only the per-key b/B modifier can strip blanks.
        if (spec.BlankIgnore) key = StripLeadingBlanks(key);
        return key;
    }

    private static string ApplyCharOffsets(string fieldText, int startChar, int endChar)
    {
        if (startChar > 0)
        {
            int skip = startChar - 1;
            fieldText = skip < fieldText.Length ? fieldText.Substring(skip) : "";
        }
        if (endChar > 0 && endChar < fieldText.Length)
        {
            fieldText = fieldText.Substring(0, endChar);
        }
        return fieldText;
    }

    /// <summary>GNU default field split (no <c>-t</c>): a field is its leading blanks
    /// plus a maximal run of non-blanks. Port of the cmdlet's single-pass scan — a
    /// naive <c>\s+</c> split misaligns <c>-k</c> on a leading-blank line.</summary>
    private static string[] SplitWhitespaceFields(string text)
    {
        if (text.Length == 0) return new[] { string.Empty };
        var fields = new List<string>();
        int start = 0;
        for (int i = 1; i < text.Length; i++)
        {
            bool cBlank = text[i] == ' ' || text[i] == '\t';
            bool pBlank = text[i - 1] == ' ' || text[i - 1] == '\t';
            if (cBlank && !pBlank)
            {
                fields.Add(text.Substring(start, i - start));
                start = i;
            }
        }
        fields.Add(text.Substring(start));
        return fields.ToArray();
    }

    private static string StripLeadingBlanks(string s)
    {
        int i = 0;
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
        return i == 0 ? s : s.Substring(i);
    }

    private static double ParseNumericPrefix(string s)
    {
        var m = s_numericPrefix.Match(s);
        string numStr = m.Success ? m.Value : "0";
        double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var v);
        return v;
    }

    /// <summary>Port of the cmdlet's key-spec parser. A spec the regex does not match
    /// falls through to all-zero fields (field 0 → clamped to field 1) exactly as the
    /// cmdlet does — no exception, no decline, same output.</summary>
    private static KeySpec ParseKeySpec(string spec)
    {
        var result = new KeySpec();
        var parts = spec.Split(new[] { ',' }, 2);
        var start = ParseKeySpecPos(parts[0]);
        result.StartField = start.field;
        result.StartChar = start.charOffset;
        result.Numeric = start.numeric;
        result.Reverse = start.reverse;
        result.BlankIgnore = start.blankIgnore;
        if (parts.Length >= 2)
        {
            var end = ParseKeySpecPos(parts[1]);
            result.EndField = end.field;
            result.EndChar = end.charOffset;
            if (end.numeric) result.Numeric = true;
            if (end.reverse) result.Reverse = true;
            if (end.blankIgnore) result.BlankIgnore = true;
        }
        return result;
    }

    private static (int field, int charOffset, bool numeric, bool reverse, bool blankIgnore)
        ParseKeySpecPos(string s)
    {
        var m = s_keySpecPos.Match(s);
        int field = 0, charOffset = 0;
        bool numeric = false, reverse = false, blankIgnore = false;
        if (m.Success)
        {
            int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out field);
            if (m.Groups[2].Success && m.Groups[2].Value.Length > 0)
            {
                int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out charOffset);
            }
            if (m.Groups[3].Success)
            {
                foreach (char c in m.Groups[3].Value)
                {
                    switch (c)
                    {
                        case 'n': numeric = true; break;
                        case 'r': case 'R': reverse = true; break;
                        case 'b': case 'B': blankIgnore = true; break;
                    }
                }
            }
        }
        return (field, charOffset, numeric, reverse, blankIgnore);
    }
}
