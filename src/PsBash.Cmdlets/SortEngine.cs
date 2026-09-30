using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

/// <summary>
/// The ordering options of one sort key — or, for a keyless sort, of the whole line. Mirrors GNU's
/// <c>struct keyfield</c>: per-key options REPLACE the global ones (they do not add to them); a key
/// with no ordering option of its own inherits every global one, including <c>-r</c>
/// (<see cref="Inherit"/>).
/// </summary>
internal sealed class SortKeyOpts
{
    public bool Numeric, General, Human, Month, Version, Fold, Dict, NonPrint, BlankStart, BlankEnd, Reverse;

    /// <summary>GNU <c>default_key_compare</c>: no ordering option (b d f g h i M n R V) on the key.
    /// <c>-r</c> alone does not count as an ordering option but still stops inheritance.</summary>
    public bool HasOrdering =>
        Numeric || General || Human || Month || Version || Fold || Dict || NonPrint || BlankStart || BlankEnd;

    public SortKeyOpts Clone() => (SortKeyOpts)MemberwiseClone();

    public bool IsNumericLike => Numeric || General || Human;

    /// <summary>GNU <c>check_ordering_compatibility</c>: numeric, general, human and month are
    /// mutually exclusive, and each excludes version/dictionary/nonprinting (one shared bucket).
    /// Returns the option letters (GNU order d f g h i M n V) when incompatible, else null.</summary>
    public string? IncompatibleLetters()
    {
        int n = (Numeric ? 1 : 0) + (General ? 1 : 0) + (Human ? 1 : 0) + (Month ? 1 : 0)
            + ((Version || Dict || NonPrint) ? 1 : 0);
        if (n <= 1) return null;
        var sb = new StringBuilder();
        if (Dict) sb.Append('d');
        if (Fold) sb.Append('f');
        if (General) sb.Append('g');
        if (Human) sb.Append('h');
        if (NonPrint) sb.Append('i');
        if (Month) sb.Append('M');
        if (Numeric) sb.Append('n');
        if (Version) sb.Append('V');
        return sb.ToString();
    }
}

/// <summary>One <c>-k F[.C][OPTS][,F[.C][OPTS]]</c> key. Fields and offsets are 1-based; 0 = absent
/// (<see cref="EndField"/> 0 = to the end of the line, <see cref="EndChar"/> 0 = to the end of the
/// end field). <see cref="Whole"/> is the implicit whole-line key of a keyless sort.</summary>
internal sealed class SortKey
{
    public int StartField, StartChar, EndField, EndChar;
    public bool Whole;
    public SortKeyOpts Opts = new();
}

/// <summary>The fully resolved, validated argv of <c>sort</c> (see <c>InvokeBashSortCommand.Plan</c>).</summary>
internal sealed class SortPlan
{
    public List<SortKey> Keys = new();           // effective keys (a keyless sort gets one Whole key)
    public bool HasExplicitKeys;
    public bool Reverse;                          // GLOBAL -r: only the last-resort comparison uses it directly
    public bool Unique, Stable, Merge;
    public char? Delimiter;                       // -t
}

/// <summary>
/// The sort engine shared by <c>Invoke-BashSort</c> and the fused <c>SortStage</c> core (one engine,
/// so the two lanes cannot drift): key extraction, decorate-sort-undecorate comparison, GNU
/// last-resort tie-break, <c>-u</c> run collapsing, <c>-c</c> disorder detection and the
/// <c>-m</c> k-way merge.
///
/// <para>GNU semantics (coreutils 9.4, oracle-checked): per-key options replace the globals
/// (<see cref="SortKeyOpts"/>); the last-resort whole-line comparison (reversed by a GLOBAL
/// <c>-r</c>) applies unless <c>-s</c> or <c>-u</c> (so <c>-u</c> keeps the FIRST line of each run
/// of equal keys, in input order); <c>-c</c> uses the same full comparison, so equal keys with
/// out-of-order tails are disorder unless <c>-s</c>; with <c>-u</c> equal keys are disorder;
/// <c>-n</c> reads a leading <c>-?digits[.digits]</c> or <c>.digits</c> after blanks (no
/// <c>+</c>, else 0) for a whole-line key too; <c>-k F.C,F.C</c> character offsets count from
/// the start of their own field.</para>
///
/// <para>Known divergences kept (scripts/sort-parity-check.ps1): <c>-V</c> uses a dot/dash split
/// not GNU's filevercmp; ordering is ordinal (== <c>LC_ALL=C</c>).</para>
/// </summary>
internal sealed class SortEngine
{
    private static readonly Regex s_numericPrefix = new(@"^[ \t]*-?(?:\d+(?:\.\d*)?|\.\d+)", RegexOptions.Compiled);

    private readonly SortPlan _plan;
    private string[] _raw = Array.Empty<string>();
    private string[][] _text = Array.Empty<string[]>();      // [key][item] compare text
    private double[][] _num = Array.Empty<double[]>();       // [key][item] numeric / month value

    public SortEngine(SortPlan plan) => _plan = plan;

    /// <summary>
    /// Decorate every item ONCE (keys are pure functions of the line). <paramref name="texts"/> are the
    /// lines without their terminator; <paramref name="sizeOf"/> optionally supplies a typed byte size
    /// for a whole-line <c>-h</c> key (an <c>ls -lh</c> object's <c>SizeBytes</c>).
    /// </summary>
    public void Prepare(IReadOnlyList<string> texts, Func<int, double?>? sizeOf = null)
    {
        int n = texts.Count;
        int nk = _plan.Keys.Count;
        _raw = new string[n];
        for (int i = 0; i < n; i++) _raw[i] = texts[i];
        _text = new string[nk][];
        _num = new double[nk][];
        for (int k = 0; k < nk; k++)
        {
            var key = _plan.Keys[k];
            var o = key.Opts;
            var kt = new string[n];
            double[]? kn = (o.IsNumericLike || o.Month) ? new double[n] : null;
            for (int i = 0; i < n; i++)
            {
                string text = key.Whole
                    ? (o.BlankStart ? StripLeadingBlanks(_raw[i]) : _raw[i])
                    : ExtractKeyText(_raw[i], key, _plan.Delimiter);
                if (o.Dict) text = StripDictionary(text);
                if (o.NonPrint) text = StripNonPrinting(text);
                kt[i] = text;
                if (o.Human) kn![i] = (key.Whole ? sizeOf?.Invoke(i) : null) ?? ConvertFromHumanNumeric(text);
                else if (o.General) kn![i] = ParseGeneralNumeric(text);
                else if (o.Numeric) kn![i] = ParseNumericPrefix(text);
                else if (o.Month) kn![i] = ConvertFromMonthName(text);
            }
            _text[k] = kt;
            _num[k] = kn ?? Array.Empty<double>();
        }
    }

    /// <summary>Key comparison only (no last resort): 0 means "equal keys".</summary>
    public int CompareKeys(int a, int b)
    {
        for (int k = 0; k < _plan.Keys.Count; k++)
        {
            var o = _plan.Keys[k].Opts;
            int c;
            if (o.IsNumericLike || o.Month)
            {
                double x = _num[k][a], y = _num[k][b];
                c = x < y ? -1 : (x > y ? 1 : 0);
            }
            else if (o.Version) c = VersionCompare(_text[k][a], _text[k][b]);
            else if (o.Fold) c = string.Compare(_text[k][a], _text[k][b], StringComparison.OrdinalIgnoreCase);
            else c = string.CompareOrdinal(_text[k][a], _text[k][b]);
            if (o.Reverse) c = -c;
            if (c != 0) return Math.Sign(c);
        }
        return 0;
    }

    /// <summary>GNU <c>compare</c>: keys, then (unless <c>-s</c>/<c>-u</c>) the whole line bytewise,
    /// reversed by a global <c>-r</c>.</summary>
    public int Compare(int a, int b)
    {
        int c = CompareKeys(a, b);
        if (c != 0 || _plan.Unique || _plan.Stable) return c;
        c = string.CompareOrdinal(_raw[a], _raw[b]);
        if (_plan.Reverse) c = -c;
        return Math.Sign(c);
    }

    /// <summary>First index that is out of order for <c>-c</c> (GNU: the line that sorts before its
    /// predecessor; with <c>-u</c> also a line EQUAL to its predecessor), or -1.</summary>
    public int FirstDisorder()
    {
        for (int i = 1; i < _raw.Length; i++)
        {
            int c = Compare(i - 1, i);
            if (c > 0 || (_plan.Unique && c == 0)) return i;
        }
        return -1;
    }

    /// <summary>Indices in output order: a total order (input index breaks every tie), or — for
    /// <c>-m</c> — a k-way merge of the already-sorted <paramref name="sourceStarts"/> runs; then
    /// <c>-u</c> keeps the first line of each run of equal keys.</summary>
    public List<int> Order(IReadOnlyList<int>? sourceStarts = null)
    {
        int n = _raw.Length;
        var order = new List<int>(n);
        if (_plan.Merge)
        {
            var starts = sourceStarts is { Count: > 0 } ? sourceStarts : new[] { 0 };
            int src = starts.Count;
            var head = new int[src];
            var end = new int[src];
            for (int s = 0; s < src; s++)
            {
                head[s] = starts[s];
                end[s] = s + 1 < src ? starts[s + 1] : n;
            }
            while (true)
            {
                int best = -1;
                for (int s = 0; s < src; s++)
                {
                    if (head[s] >= end[s]) continue;
                    if (best < 0 || Compare(head[s], head[best]) < 0) best = s;   // ties: earlier source
                }
                if (best < 0) break;
                order.Add(head[best]++);
            }
        }
        else
        {
            var idx = new int[n];
            for (int i = 0; i < n; i++) idx[i] = i;
            Array.Sort(idx, (a, b) =>
            {
                int c = Compare(a, b);
                return c != 0 ? c : a - b;
            });
            order.AddRange(idx);
        }

        if (!_plan.Unique) return order;
        var kept = new List<int>(order.Count);
        int prev = -1;
        foreach (int i in order)
        {
            if (prev >= 0 && CompareKeys(prev, i) == 0) continue;
            kept.Add(i);
            prev = i;
        }
        return kept;
    }

    // ----- key extraction -----

    // GNU keys are POSITIONAL: [begfield, limfield) over the line. A key is NOT clipped to its field —
    // `-k1.2,1.3` is characters 2-3 of the LINE (the start field begins at the line start), even when they
    // run past the first blank. The start field includes its leading blanks under the default model.
    private static string ExtractKeyText(string line, SortKey key, char? tab)
    {
        int beg = BeginField(line, key.StartField - 1, key.StartChar, key.Opts.BlankStart, tab);
        int end = LimitField(line, key.EndField, key.EndChar, key.Opts.BlankEnd, tab);
        return end <= beg ? "" : line.Substring(beg, end - beg);
    }

    private static bool IsBlank(char c) => c == ' ' || c == '\t';

    // GNU begfield: skip `sword0` fields, then (-b) the blanks, then schar-1 characters (clamped to the line).
    private static int BeginField(string line, int sword0, int schar, bool skipBlanks, char? tab)
    {
        int lim = line.Length, ptr = 0;
        if (sword0 < 0) sword0 = 0;
        if (tab is { } d)
        {
            while (ptr < lim && sword0-- > 0)
            {
                while (ptr < lim && line[ptr] != d) ptr++;
                if (ptr < lim) ptr++;
            }
        }
        else
        {
            while (ptr < lim && sword0-- > 0)
            {
                while (ptr < lim && IsBlank(line[ptr])) ptr++;
                while (ptr < lim && !IsBlank(line[ptr])) ptr++;
            }
        }
        if (skipBlanks) while (ptr < lim && IsBlank(line[ptr])) ptr++;
        return Math.Min(lim, ptr + (schar > 1 ? schar - 1 : 0));
    }

    // GNU limfield: the end of the end field (echar == 0) or echar characters into it; no end field = line end.
    private static int LimitField(string line, int eword, int echar, bool skipBlanks, char? tab)
    {
        int lim = line.Length, ptr = 0;
        if (eword <= 0) return lim;
        int ew = eword - 1;
        if (echar == 0) ew++;
        if (tab is { } d)
        {
            while (ptr < lim && ew-- > 0)
            {
                while (ptr < lim && line[ptr] != d) ptr++;
                if (ptr < lim && (ew > 0 || echar > 0)) ptr++;
            }
        }
        else
        {
            while (ptr < lim && ew-- > 0)
            {
                while (ptr < lim && IsBlank(line[ptr])) ptr++;
                while (ptr < lim && !IsBlank(line[ptr])) ptr++;
            }
        }
        if (echar != 0)
        {
            if (skipBlanks) while (ptr < lim && IsBlank(line[ptr])) ptr++;
            ptr = (int)Math.Min((long)lim, (long)ptr + echar);
        }
        return ptr;
    }
    private static string StripLeadingBlanks(string s)
    {
        int i = 0;
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
        return i == 0 ? s : s.Substring(i);
    }

    // -d: only blanks and alphanumerics count.
    private static string StripDictionary(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || char.IsWhiteSpace(c))
                sb.Append(c);
        }
        return sb.ToString();
    }

    // -i: only printable characters count.
    private static string StripNonPrinting(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (!char.IsControl(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    internal static double ParseNumericPrefix(string s)
    {
        var m = s_numericPrefix.Match(s);
        if (!m.Success) return 0.0;
        double.TryParse(m.Value.TrimStart(' ', '\t'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v);
        return v;
    }

    private static readonly Regex s_generalPrefix = new(
        @"^[ \t]*[+-]?(?:(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?|inf(?:inity)?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary><c>-g</c>: the longest general-float PREFIX (<c>1e3</c>, <c>+5</c>, <c>.5</c>, <c>inf</c>), like
    /// strtold; text with no number sorts below every number.</summary>
    internal static double ParseGeneralNumeric(string s)
    {
        var m = s_generalPrefix.Match(s);
        if (!m.Success) return double.NegativeInfinity;
        string num = m.Value.Trim(new[] { ' ', '\t' });
        return double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NegativeInfinity;
    }

    private static readonly Regex s_humanPrefix = new(
        @"^[ \t]*(-?(?:\d+(?:\.\d*)?|\.\d+))([kKMGTPEZYRQ])?", RegexOptions.Compiled);

    /// <summary><c>-h</c>: a numeric PREFIX optionally followed directly by a 1024-based unit letter
    /// (<c>10K</c>, <c>1.5M</c>); anything else is 0 (GNU does not accept <c>+5</c>).</summary>
    internal static double ConvertFromHumanNumeric(string value)
    {
        var m = s_humanPrefix.Match(value);
        if (!m.Success) return 0.0;
        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double num)) return 0.0;
        double mult = 1.0;
        if (m.Groups[2].Success)
        {
            int idx = "kKMGTPEZYRQ".IndexOf(m.Groups[2].Value[0]);   // k K = 1, M = 2, G = 3 ... (powers of 1024)
            mult = Math.Pow(1024.0, idx <= 1 ? 1 : idx);
        }
        return num * mult;
    }
    internal static int ConvertFromMonthName(string value)
    {
        var trimmed = value.Trim().ToLowerInvariant();
        if (trimmed.Length < 3) return 0;
        return trimmed.Substring(0, 3) switch
        {
            "jan" => 1, "feb" => 2, "mar" => 3, "apr" => 4,
            "may" => 5, "jun" => 6, "jul" => 7, "aug" => 8,
            "sep" => 9, "oct" => 10, "nov" => 11, "dec" => 12,
            _ => 0,
        };
    }

    /// <summary>
    /// <c>-V</c>: the dpkg/gnulib <c>verrevcmp</c> ordering — alternating non-digit runs (compared
    /// character by character: letters, then everything else after them, <c>~</c> before even the end)
    /// and digit runs (compared numerically, leading zeros ignored), an empty string first. Not ported
    /// from gnulib's <c>filevercmp</c>: the file-suffix stripping (<c>.tar.gz</c>) and the special
    /// <c>.</c>/<c>..</c> cases, so those differ from GNU.
    /// </summary>
    internal static int VersionCompare(string a, string b)
    {
        if (a.Length == 0) return b.Length == 0 ? 0 : -1;
        if (b.Length == 0) return 1;
        // gnulib: dot names first ("." < ".." < ".x" < everything else).
        bool da = a[0] == '.', db = b[0] == '.';
        if (da != db) return da ? -1 : 1;
        if (da)
        {
            int ra = a == "." ? 0 : a == ".." ? 1 : 2, rb = b == "." ? 0 : b == ".." ? 1 : 2;
            if (ra != rb) return ra < rb ? -1 : 1;
            if (ra < 2) return 0;
        }
        int i = 0, j = 0;
        while (i < a.Length || j < b.Length)
        {
            while ((i < a.Length && !char.IsAsciiDigit(a[i])) || (j < b.Length && !char.IsAsciiDigit(b[j])))
            {
                int ac = i < a.Length ? VersionOrder(a[i]) : 0;
                int bc = j < b.Length ? VersionOrder(b[j]) : 0;
                if (ac != bc) return ac < bc ? -1 : 1;
                i++;
                j++;
            }
            while (i < a.Length && a[i] == '0') i++;
            while (j < b.Length && b[j] == '0') j++;
            int firstDiff = 0;
            while (i < a.Length && char.IsAsciiDigit(a[i]) && j < b.Length && char.IsAsciiDigit(b[j]))
            {
                if (firstDiff == 0) firstDiff = a[i] - b[j];
                i++;
                j++;
            }
            if (i < a.Length && char.IsAsciiDigit(a[i])) return 1;
            if (j < b.Length && char.IsAsciiDigit(b[j])) return -1;
            if (firstDiff != 0) return firstDiff < 0 ? -1 : 1;
        }
        return 0;
    }

    private static int VersionOrder(char c)
    {
        if (char.IsAsciiDigit(c)) return 0;
        if (char.IsAsciiLetter(c)) return c;
        if (c == '~') return -1;
        return c + 256;
    }}
