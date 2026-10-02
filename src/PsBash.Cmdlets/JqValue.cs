using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// A jq number that remembers the text it was written as. jq 1.7 prints a number it has not computed with exactly as
/// decNumber would canonicalise the literal (<c>1.0</c> stays <c>1.0</c>, <c>1e2</c> is <c>1E+2</c>, <c>100000000000000000000</c> keeps all
/// digits); any arithmetic turns it into a plain double, which is printed with 17 significant digits like jq's
/// <c>jvp_dtoa_fmt</c>. <see cref="Value"/> is the double jq compares and computes with.
/// </summary>
internal sealed class JqNumber
{
    public JqNumber(double value, string canonical) { Value = value; Canonical = canonical; }
    public double Value { get; }
    public string Canonical { get; }
    public override string ToString() => Canonical;
}

/// <summary>A jq error: a runtime failure carrying the value <c>error(v)</c> was called with (a string for built-in errors).</summary>
internal sealed class JqError : Exception
{
    public JqError(object? value) : base(value as string ?? "jq error") { Value = value; }
    public object? Value { get; }
}

/// <summary>
/// The jq value model on .NET: <c>null</c>, <c>bool</c>, numbers (<see cref="JqNumber"/>, <c>double</c>, <c>int</c>, <c>long</c>), <c>string</c>,
/// <c>object?[]</c> arrays and <see cref="OrderedDictionary"/> objects (insertion ordered, like jq's output). Ordering, equality,
/// type names and the two JSON writers live here.
/// </summary>
internal static class JqValue
{
    // ───────────── types ─────────────

    public static bool IsNumber(object? v) => v is JqNumber or double or int or long or float or decimal;

    public static double ToDouble(object? v) => v switch
    {
        JqNumber n => n.Value,
        double d => d,
        int i => i,
        long l => l,
        float f => f,
        decimal m => (double)m,
        _ => throw new InvalidCastException(),
    };

    public static string TypeName(object? v) => v switch
    {
        null => "null",
        bool => "boolean",
        string => "string",
        _ when IsNumber(v) => "number",
        IDictionary => "object",
        IList => "array",
        _ => "null",
    };

    public static bool IsTruthy(object? v) => !(v is null || v is false);

    /// <summary>Wraps a computed double (jq arithmetic result).</summary>
    public static object Num(double d) => d;

    public static object?[] ToArray(object? v) => v switch
    {
        object?[] a => a,
        IList l => l.Cast<object?>().ToArray(),
        _ => throw new InvalidCastException(),
    };

    public static OrderedDictionary NewObject() => new();

    public static OrderedDictionary CopyObject(IDictionary d)
    {
        var copy = new OrderedDictionary();
        foreach (DictionaryEntry e in d) copy[e.Key] = e.Value;
        return copy;
    }

    public static IEnumerable<string> Keys(IDictionary d) => d.Keys.Cast<object>().Select(k => (string)k);

    // ───────────── ordering / equality ─────────────

    private static int TypeRank(object? v) => v switch
    {
        null => 0,
        false => 1,
        true => 2,
        _ when IsNumber(v) => 3,
        string => 4,
        IDictionary => 6,
        IList => 5,
        _ => 0,
    };

    /// <summary>jq's total order: null &lt; false &lt; true &lt; numbers &lt; strings &lt; arrays &lt; objects.</summary>
    public static int Compare(object? a, object? b)
    {
        int ra = TypeRank(a), rb = TypeRank(b);
        if (ra != rb) return ra < rb ? -1 : 1;
        switch (ra)
        {
            case 3:
                {
                    double x = ToDouble(a), y = ToDouble(b);
                    if (double.IsNaN(x)) return double.IsNaN(y) ? -1 : -1;   // nan sorts below every number (jq: nan < nan)
                    if (double.IsNaN(y)) return 1;
                    return x < y ? -1 : x > y ? 1 : 0;
                }
            case 4: return CompareStrings((string)a!, (string)b!);
            case 5:
                {
                    var x = ToArray(a); var y = ToArray(b);
                    for (int i = 0; i < x.Length && i < y.Length; i++)
                    {
                        int c = Compare(x[i], y[i]);
                        if (c != 0) return c;
                    }
                    return x.Length.CompareTo(y.Length);
                }
            case 6:
                {
                    var x = (IDictionary)a!; var y = (IDictionary)b!;
                    var kx = Keys(x).OrderBy(k => k, Comparer<string>.Create(CompareStrings)).ToList();
                    var ky = Keys(y).OrderBy(k => k, Comparer<string>.Create(CompareStrings)).ToList();
                    for (int i = 0; i < kx.Count && i < ky.Count; i++)
                    {
                        int c = CompareStrings(kx[i], ky[i]);
                        if (c != 0) return c;
                    }
                    if (kx.Count != ky.Count) return kx.Count.CompareTo(ky.Count);
                    foreach (var k in kx)
                    {
                        int c = Compare(x[k], y[k]);
                        if (c != 0) return c;
                    }
                    return 0;
                }
            default: return 0;
        }
    }

    /// <summary>Strings order by code point (jq compares UTF-8 bytes).</summary>
    public static int CompareStrings(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            int ca = NextCodePoint(a, ref i), cb = NextCodePoint(b, ref j);
            if (ca != cb) return ca < cb ? -1 : 1;
        }
        return i < a.Length ? 1 : j < b.Length ? -1 : 0;
    }

    private static int NextCodePoint(string s, ref int i)
    {
        char c = s[i++];
        if (char.IsHighSurrogate(c) && i < s.Length && char.IsLowSurrogate(s[i])) return char.ConvertToUtf32(c, s[i++]);
        return c;
    }
    public static bool Equal(object? a, object? b)
    {
        int ra = TypeRank(a), rb = TypeRank(b);
        if (ra != rb) return false;
        return ra switch
        {
            3 => ToDouble(a) == ToDouble(b),
            4 => string.Equals((string)a!, (string)b!, StringComparison.Ordinal),
            5 => ArraysEqual(ToArray(a), ToArray(b)),
            6 => ObjectsEqual((IDictionary)a!, (IDictionary)b!),
            _ => true,
        };
    }

    private static bool ArraysEqual(object?[] x, object?[] y)
    {
        if (x.Length != y.Length) return false;
        for (int i = 0; i < x.Length; i++) if (!Equal(x[i], y[i])) return false;
        return true;
    }

    private static bool ObjectsEqual(IDictionary x, IDictionary y)
    {
        if (x.Count != y.Count) return false;
        foreach (DictionaryEntry e in x)
        {
            if (!y.Contains(e.Key) || !Equal(e.Value, y[e.Key])) return false;
        }
        return true;
    }

    // ───────────── numbers ─────────────

    /// <summary>
    /// A double as jq 1.7 prints a computed number: the shortest round-trip digits (at most 17), positional notation unless the decimal
    /// point is more than 15 places beyond the digits or below 1e-4, exponents with a sign and at least two digits
    /// (<c>1e+16</c>, <c>1e-05</c>); nan prints <c>null</c>, infinities the largest double.
    /// </summary>
    public static string FormatDouble(double d)
    {
        if (double.IsNaN(d)) return "null";
        if (double.IsPositiveInfinity(d)) return "1.7976931348623157e+308";
        if (double.IsNegativeInfinity(d)) return "-1.7976931348623157e+308";
        if (d == 0) return (1 / d) < 0 ? "-0" : "0";

        string r = d.ToString("R", CultureInfo.InvariantCulture);
        bool negative = r[0] == '-';
        if (negative) r = r.Substring(1);
        int exp = 0;
        int e = r.IndexOfAny(new[] { 'E', 'e' });
        if (e >= 0)
        {
            exp = int.Parse(r.Substring(e + 1), CultureInfo.InvariantCulture);
            r = r.Substring(0, e);
        }
        int dot = r.IndexOf('.');
        string digits;
        int decpt;
        if (dot >= 0) { digits = r.Remove(dot, 1); decpt = dot + exp; }
        else { digits = r; decpt = r.Length + exp; }
        // strip leading zeros (0.0001 -> digits "00001")
        int lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') { lead++; decpt--; }
        digits = digits.Substring(lead).TrimEnd('0');
        if (digits.Length == 0) digits = "0";

        var sb = new StringBuilder();
        if (negative) sb.Append('-');
        if (decpt <= -4 || decpt > digits.Length + 15)
        {
            sb.Append(digits[0]);
            if (digits.Length > 1) { sb.Append('.'); sb.Append(digits, 1, digits.Length - 1); }
            int x = decpt - 1;
            sb.Append('e');
            sb.Append(x < 0 ? '-' : '+');
            sb.Append(Math.Abs(x).ToString("00", CultureInfo.InvariantCulture));
        }
        else if (decpt <= 0)
        {
            sb.Append("0.");
            sb.Append('0', -decpt);
            sb.Append(digits);
        }
        else if (decpt >= digits.Length)
        {
            sb.Append(digits);
            sb.Append('0', decpt - digits.Length);
        }
        else
        {
            sb.Append(digits, 0, decpt);
            sb.Append('.');
            sb.Append(digits, decpt, digits.Length - decpt);
        }
        return sb.ToString();
    }

    public static string FormatNumber(object? v) => v switch
    {
        JqNumber n => n.Canonical,
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => FormatDouble(d),
        float f => FormatDouble(f),
        decimal m => FormatDouble((double)m),
        _ => "null",
    };

    /// <summary>decNumber's to-scientific-string of a JSON number literal (what jq 1.7 prints back for an untouched literal).</summary>
    public static string CanonicalLiteral(string literal)
    {
        string s = literal;
        bool negative = false;
        if (s.StartsWith('-')) { negative = true; s = s.Substring(1); }
        else if (s.StartsWith('+')) s = s.Substring(1);

        int exp = 0;
        int e = s.IndexOfAny(new[] { 'e', 'E' });
        if (e >= 0)
        {
            exp = int.Parse(s.Substring(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            s = s.Substring(0, e);
        }
        int dot = s.IndexOf('.');
        string digits = dot >= 0 ? s.Remove(dot, 1) : s;
        if (dot >= 0) exp -= s.Length - dot - 1;
        digits = digits.TrimStart('0');
        if (digits.Length == 0) digits = "0";
        if (digits == "0") exp = Math.Max(exp, int.MinValue);

        int adjusted = exp + digits.Length - 1;
        var sb = new StringBuilder();
        if (negative) sb.Append('-');
        if (exp <= 0 && adjusted >= -6)
        {
            if (exp == 0) sb.Append(digits);
            else
            {
                int pointPos = digits.Length + exp;      // digits left of the point
                if (pointPos > 0) { sb.Append(digits, 0, pointPos); sb.Append('.'); sb.Append(digits, pointPos, digits.Length - pointPos); }
                else { sb.Append("0."); sb.Append('0', -pointPos); sb.Append(digits); }
            }
        }
        else
        {
            sb.Append(digits[0]);
            if (digits.Length > 1) { sb.Append('.'); sb.Append(digits, 1, digits.Length - 1); }
            sb.Append('E');
            sb.Append(adjusted < 0 ? '-' : '+');
            sb.Append(Math.Abs(adjusted).ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    // ───────────── JSON writers ─────────────

    /// <summary>Output layout: <see cref="Indent"/> spaces (or a tab with <see cref="Tab"/>); 0 is compact.</summary>
    public readonly record struct WriteOptions(int Indent, bool Tab, bool SortKeys, bool Ascii)
    {
        public static WriteOptions Compact => new(0, false, false, false);
        public bool IsCompact => !Tab && Indent == 0;
    }

    public static string ToJson(object? value, WriteOptions options)
    {
        var sb = new StringBuilder();
        Write(sb, value, options, 0);
        return sb.ToString();
    }

    public static string ToCompactJson(object? value) => ToJson(value, WriteOptions.Compact);

    private static void NewLine(StringBuilder sb, WriteOptions o, int depth)
    {
        sb.Append('\n');
        if (o.Tab) sb.Append('\t', depth);
        else sb.Append(' ', depth * o.Indent);
    }

    private static void Write(StringBuilder sb, object? v, WriteOptions o, int depth)
    {
        switch (v)
        {
            case null: sb.Append("null"); return;
            case bool b: sb.Append(b ? "true" : "false"); return;
            case string s: WriteString(sb, s, o.Ascii); return;
        }
        if (IsNumber(v)) { sb.Append(FormatNumber(v)); return; }
        if (v is IDictionary d)
        {
            if (d.Count == 0) { sb.Append("{}"); return; }
            var keys = Keys(d).ToList();
            if (o.SortKeys) keys.Sort(CompareStrings);
            sb.Append('{');
            bool first = true;
            foreach (var k in keys)
            {
                if (!first) sb.Append(',');
                first = false;
                if (!o.IsCompact) NewLine(sb, o, depth + 1);
                WriteString(sb, k, o.Ascii);
                sb.Append(o.IsCompact ? ":" : ": ");
                Write(sb, d[k], o, depth + 1);
            }
            if (!o.IsCompact) NewLine(sb, o, depth);
            sb.Append('}');
            return;
        }
        if (v is IList l)
        {
            if (l.Count == 0) { sb.Append("[]"); return; }
            sb.Append('[');
            bool first = true;
            foreach (var item in l)
            {
                if (!first) sb.Append(',');
                first = false;
                if (!o.IsCompact) NewLine(sb, o, depth + 1);
                Write(sb, item, o, depth + 1);
            }
            if (!o.IsCompact) NewLine(sb, o, depth);
            sb.Append(']');
            return;
        }
        sb.Append("null");
    }

    public static void WriteString(StringBuilder sb, string s, bool ascii)
    {
        sb.Append('"');
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                case '\r': sb.Append("\\r"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || c == 0x7f)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    {
                        if (ascii)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            sb.Append("\\u").Append(((int)s[i + 1]).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else sb.Append(c).Append(s[i + 1]);
                        i++;
                    }
                    else if (char.IsSurrogate(c))
                    {
                        // A lone surrogate is not text: jq prints the replacement character.
                        sb.Append(ascii ? "\\ufffd" : "�");
                    }
                    else if (ascii && c > 0x7e)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>The text of a value as <c>tostring</c> gives it: a string unchanged, anything else as compact JSON.</summary>
    public static string ToText(object? v) => v is string s ? s : ToCompactJson(v);

    // ───────────── code points ─────────────

    public static int[] CodePoints(string s)
    {
        var list = new List<int>(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                list.Add(char.ConvertToUtf32(s[i], s[i + 1]));
                i++;
            }
            else list.Add(char.IsSurrogate(s[i]) ? 0xFFFD : s[i]);
        }
        return list.ToArray();
    }

    public static string FromCodePoints(IEnumerable<int> cps)
    {
        var sb = new StringBuilder();
        foreach (int cp in cps)
        {
            if (cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF) || cp < 0) sb.Append('�');
            else sb.Append(char.ConvertFromUtf32(cp));
        }
        return sb.ToString();
    }
}
