using System.Globalization;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// An AWK scalar value. AWK values are dual-natured: a value can be a number, a
/// string constant, or a "numeric string" (strnum) that came from input (fields,
/// FS-split, <c>-v</c>, ENVIRON, getline). The distinction drives comparison:
/// two values compare numerically only when BOTH are numeric (a number, or a
/// strnum whose text looks like a number); otherwise they compare as strings.
/// An uninitialized scalar reads as both <c>0</c> and <c>""</c>.
/// </summary>
internal readonly struct AwkValue
{
    public enum K { Number, String, StrNum, Uninit }

    public readonly K Kind;
    private readonly double _num;
    private readonly string _str;

    private AwkValue(K kind, double num, string str)
    {
        Kind = kind;
        _num = num;
        _str = str;
    }

    public static readonly AwkValue Uninitialized = new(K.Uninit, 0, "");

    public static AwkValue Number(double d) => new(K.Number, d, "");
    public static AwkValue Str(string s) => new(K.String, 0, s ?? "");

    /// <summary>A value originating from input — number if it looks like one.</summary>
    public static AwkValue StrNum(string s) => new(K.StrNum, 0, s ?? "");

    public static AwkValue Bool(bool b) => Number(b ? 1 : 0);

    /// <summary>Numeric in a comparison context?</summary>
    public bool IsNumericContext =>
        Kind == K.Number || Kind == K.Uninit ||
        (Kind == K.StrNum && LooksNumeric(_str));

    public double ToNumber()
    {
        if (Kind == K.Number) return _num;
        if (Kind == K.Uninit) return 0;
        return ParseLeadingNumber(_str);
    }

    public string ToStr(string convfmt)
    {
        switch (Kind)
        {
            case K.Number: return NumberToString(_num, convfmt);
            case K.Uninit: return "";
            default: return _str;
        }
    }

    /// <summary>Truthiness: numbers are true when non-zero; strings when non-empty.</summary>
    public bool ToBool()
    {
        if (Kind == K.Number) return _num != 0;
        if (Kind == K.Uninit) return false;
        // A strnum that looks numeric is true iff its numeric value is non-zero.
        if (Kind == K.StrNum && LooksNumeric(_str)) return ParseLeadingNumber(_str) != 0;
        return _str.Length > 0;
    }

    // ── number → string (CONVFMT / OFMT, %.6g default) ─────────────────────

    public static string NumberToString(double d, string fmt)
    {
        if (double.IsNaN(d)) return "nan";
        if (double.IsPositiveInfinity(d)) return "inf";
        if (double.IsNegativeInfinity(d)) return "-inf";
        // Integral values print without a fractional part (awk special-cases this
        // regardless of CONVFMT/OFMT).
        if (d == Math.Floor(d) && Math.Abs(d) < 1e16)
        {
            return ((long)d).ToString(CultureInfo.InvariantCulture);
        }
        // Non-integral: honor the format string (default %.6g).
        return AwkPrintf.Format(fmt, new[] { AwkValue.Number(d) }, fmt);
    }

    // ── numeric-string detection / leading-number parse ────────────────────

    public static bool LooksNumeric(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        int i = 0, n = s.Length;
        while (i < n && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n')) i++;
        int end = n;
        while (end > i && (s[end - 1] == ' ' || s[end - 1] == '\t' || s[end - 1] == '\n')) end--;
        if (i >= end) return false;
        // Span slice avoids a Substring allocation on every comparison.
        return double.TryParse(s.AsSpan(i, end - i), NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>
    /// Parse the longest leading numeric prefix (awk's string→number coercion):
    /// "3.5abc" → 3.5, "  10" → 10, "abc" → 0.
    /// </summary>
    public static double ParseLeadingNumber(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int i = 0, n = s.Length;
        while (i < n && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n')) i++;
        int start = i;
        if (i < n && (s[i] == '+' || s[i] == '-')) i++;
        int digitsBefore = i;
        while (i < n && char.IsDigit(s[i])) i++;
        bool sawDigits = i > digitsBefore;
        if (i < n && s[i] == '.')
        {
            i++;
            int fracStart = i;
            while (i < n && char.IsDigit(s[i])) i++;
            if (i > fracStart) sawDigits = true;
        }
        if (!sawDigits) return 0;
        // optional exponent
        if (i < n && (s[i] == 'e' || s[i] == 'E'))
        {
            int expMark = i;
            i++;
            if (i < n && (s[i] == '+' || s[i] == '-')) i++;
            int expDigits = i;
            while (i < n && char.IsDigit(s[i])) i++;
            if (i == expDigits) i = expMark; // no exponent digits — back off
        }
        // Span slice avoids a Substring allocation on every numeric coercion.
        return double.TryParse(s.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
