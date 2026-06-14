using System.Globalization;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// C-style <c>printf</c> formatting for awk's <c>printf</c> / <c>sprintf</c> and
/// for number→string conversion (CONVFMT / OFMT, default <c>%.6g</c>).
/// Supports conversions d i o u x X e E f F g G s c %, the flags
/// <c>- + space # 0</c>, field width, precision, and <c>*</c> width/precision.
/// </summary>
internal static class AwkPrintf
{
    public static string Format(string format, IReadOnlyList<AwkValue> args, string convfmt)
    {
        var sb = new StringBuilder();
        int argi = 0;
        int i = 0, n = format.Length;

        AwkValue NextArg() => argi < args.Count ? args[argi++] : AwkValue.Uninitialized;

        while (i < n)
        {
            char c = format[i];
            if (c != '%') { sb.Append(c); i++; continue; }

            i++;
            if (i >= n) { sb.Append('%'); break; }
            if (format[i] == '%') { sb.Append('%'); i++; continue; }

            // flags
            bool left = false, plus = false, space = false, alt = false, zero = false;
            while (i < n)
            {
                char f = format[i];
                if (f == '-') left = true;
                else if (f == '+') plus = true;
                else if (f == ' ') space = true;
                else if (f == '#') alt = true;
                else if (f == '0') zero = true;
                else break;
                i++;
            }

            // width
            int width = -1;
            if (i < n && format[i] == '*') { width = ClampSpec(NextArg().ToNumber()); i++; if (width < 0) { left = true; width = -width; } }
            else { width = ParseSpecDigits(format, ref i, out bool anyW); if (!anyW) width = -1; }

            // precision
            int prec = -1;
            if (i < n && format[i] == '.')
            {
                i++;
                if (i < n && format[i] == '*') { prec = ClampSpec(NextArg().ToNumber()); i++; if (prec < 0) prec = -1; }
                else { prec = ParseSpecDigits(format, ref i, out _); }
            }

            if (i >= n) { sb.Append('%'); break; }
            char conv = format[i]; i++;

            string body;
            switch (conv)
            {
                case 'd':
                case 'i':
                {
                    long v = (long)NextArg().ToNumber();
                    string digits = Math.Abs(v).ToString(CultureInfo.InvariantCulture);
                    if (prec >= 0) { zero = false; if (digits.Length < prec) digits = new string('0', prec - digits.Length) + digits; if (prec == 0 && v == 0) digits = ""; }
                    string sign = v < 0 ? "-" : plus ? "+" : space ? " " : "";
                    body = ApplyIntPad(sign, digits, width, left, zero);
                    sb.Append(body);
                    continue;
                }
                case 'o':
                case 'x':
                case 'X':
                case 'u':
                {
                    long sv = (long)NextArg().ToNumber();
                    ulong uv = unchecked((ulong)sv);
                    string digits = conv switch
                    {
                        'o' => Convert.ToString((long)uv, 8),
                        'x' => uv.ToString("x", CultureInfo.InvariantCulture),
                        'X' => uv.ToString("X", CultureInfo.InvariantCulture),
                        _ => uv.ToString(CultureInfo.InvariantCulture),
                    };
                    if (prec >= 0) { zero = false; if (digits.Length < prec) digits = new string('0', prec - digits.Length) + digits; }
                    string prefix = "";
                    if (alt && uv != 0)
                    {
                        if (conv == 'x') prefix = "0x";
                        else if (conv == 'X') prefix = "0X";
                        else if (conv == 'o' && !digits.StartsWith("0")) prefix = "0";
                    }
                    body = ApplyIntPad(prefix, digits, width, left, zero);
                    sb.Append(body);
                    continue;
                }
                case 'c':
                {
                    AwkValue a = NextArg();
                    string ch;
                    if (a.Kind == AwkValue.K.Number)
                    {
                        int code = (int)a.ToNumber();
                        ch = code == 0 ? "" : ((char)(code & 0xFF)).ToString();
                    }
                    else
                    {
                        string s = a.ToStr(convfmt);
                        ch = s.Length > 0 ? s[0].ToString() : "";
                    }
                    body = Pad(ch, width, left, false);
                    sb.Append(body);
                    continue;
                }
                case 's':
                {
                    string s = NextArg().ToStr(convfmt);
                    if (prec >= 0 && s.Length > prec) s = s.Substring(0, prec);
                    body = Pad(s, width, left, false);
                    sb.Append(body);
                    continue;
                }
                case 'e':
                case 'E':
                {
                    double d = NextArg().ToNumber();
                    body = FormatE(d, prec < 0 ? 6 : prec, conv == 'E', plus, space, alt);
                    sb.Append(ApplyFloatPad(body, d, width, left, zero));
                    continue;
                }
                case 'f':
                case 'F':
                {
                    double d = NextArg().ToNumber();
                    int p = prec < 0 ? 6 : prec;
                    string mag = Math.Abs(d).ToString("F" + p, CultureInfo.InvariantCulture);
                    if (alt && p == 0 && !mag.Contains('.')) mag += ".";
                    string sign = d < 0 || (1 / d) < 0 ? "-" : plus ? "+" : space ? " " : "";
                    body = sign + mag;
                    sb.Append(ApplyFloatPad(body, d, width, left, zero));
                    continue;
                }
                case 'g':
                case 'G':
                {
                    double d = NextArg().ToNumber();
                    body = FormatG(d, prec < 0 ? 6 : prec, conv == 'G', plus, space, alt);
                    sb.Append(ApplyFloatPad(body, d, width, left, zero));
                    continue;
                }
                default:
                    // Unknown conversion — emit literally.
                    sb.Append('%');
                    sb.Append(conv);
                    continue;
            }
        }

        return sb.ToString();
    }

    // Upper bound on a field width or precision. A width/precision is either
    // accumulated from format digits (`%999999999d`, which overflows a raw
    // `w*10+d` int and wraps to a bogus huge positive) or taken from a `*`
    // argument (`printf "%*s", 1e20, x`, whose `(int)` cast saturates to
    // int.MaxValue). Either way the padding code would then allocate a multi-
    // gigabyte string. 1,000,000 is far past any legitimate use and caps a single
    // directive's allocation at ~1 MB.
    private const int MaxSpec = 1_000_000;

    /// <summary>Clamp a <c>*</c> width/precision argument to [-MaxSpec, MaxSpec], preserving sign.</summary>
    private static int ClampSpec(double v)
    {
        if (double.IsNaN(v)) return 0;
        if (v >= MaxSpec) return MaxSpec;
        if (v <= -MaxSpec) return -MaxSpec;
        return (int)v;
    }

    /// <summary>
    /// Parse a run of format digits into a width/precision, saturating at MaxSpec
    /// instead of overflowing the accumulator. <paramref name="any"/> reports
    /// whether at least one digit was consumed (distinguishes width 0 from absent).
    /// </summary>
    private static int ParseSpecDigits(string format, ref int i, out bool any)
    {
        int v = 0;
        any = false;
        while (i < format.Length && char.IsDigit(format[i]))
        {
            any = true;
            if (v < MaxSpec) v = v * 10 + (format[i] - '0');
            if (v > MaxSpec) v = MaxSpec;
            i++;
        }
        return v;
    }

    private static string Pad(string s, int width, bool left, bool zero)
    {
        if (width < 0 || s.Length >= width) return s;
        string pad = new string(zero ? '0' : ' ', width - s.Length);
        return left ? s + pad : pad + s;
    }

    private static string ApplyIntPad(string sign, string digits, int width, bool left, bool zero)
    {
        string s = sign + digits;
        if (width < 0 || s.Length >= width) return s;
        if (zero && !left)
        {
            string pad = new string('0', width - s.Length);
            return sign + pad + digits;
        }
        string sp = new string(' ', width - s.Length);
        return left ? s + sp : sp + s;
    }

    private static string ApplyFloatPad(string body, double d, int width, bool left, bool zero)
    {
        if (width < 0 || body.Length >= width) return body;
        if (zero && !left && !double.IsNaN(d) && !double.IsInfinity(d))
        {
            // zero-pad after the sign
            string sign = "";
            string rest = body;
            if (body.Length > 0 && (body[0] == '-' || body[0] == '+' || body[0] == ' '))
            {
                sign = body[0].ToString();
                rest = body.Substring(1);
            }
            string pad = new string('0', width - body.Length);
            return sign + pad + rest;
        }
        string sp = new string(' ', width - body.Length);
        return left ? body + sp : sp + body;
    }

    private static string FormatE(double d, int prec, bool upper, bool plus, bool space, bool alt)
    {
        string sign = d < 0 || (1 / d) < 0 ? "-" : plus ? "+" : space ? " " : "";
        double mag = Math.Abs(d);
        string s = mag.ToString((upper ? "E" : "e") + prec, CultureInfo.InvariantCulture);
        // .NET gives e+003; C wants e+03 (at least two exponent digits).
        s = NormalizeExponent(s, upper);
        if (alt && prec == 0 && !s.Contains('.'))
        {
            int eIdx = s.IndexOfAny(new[] { 'e', 'E' });
            s = s.Substring(0, eIdx) + "." + s.Substring(eIdx);
        }
        return sign + s;
    }

    private static string FormatG(double d, int prec, bool upper, bool plus, bool space, bool alt)
    {
        if (prec == 0) prec = 1;
        string sign = d < 0 || (1 / d) < 0 ? "-" : plus ? "+" : space ? " " : "";
        double mag = Math.Abs(d);
        if (mag == 0)
        {
            string z = "0";
            if (alt && prec > 1) z = "0." + new string('0', prec - 1);
            return sign + z;
        }
        int exp = (int)Math.Floor(Math.Log10(mag));
        string outStr;
        if (exp < -4 || exp >= prec)
        {
            // use %e with prec-1
            outStr = mag.ToString((upper ? "E" : "e") + (prec - 1), CultureInfo.InvariantCulture);
            outStr = NormalizeExponent(outStr, upper);
            if (!alt) outStr = StripGTrailingZeros(outStr, hasExp: true);
        }
        else
        {
            int fdigits = prec - 1 - exp;
            if (fdigits < 0) fdigits = 0;
            outStr = mag.ToString("F" + fdigits, CultureInfo.InvariantCulture);
            if (!alt) outStr = StripGTrailingZeros(outStr, hasExp: false);
        }
        return sign + outStr;
    }

    private static string NormalizeExponent(string s, bool upper)
    {
        char e = upper ? 'E' : 'e';
        int idx = s.IndexOfAny(new[] { 'e', 'E' });
        if (idx < 0) return s;
        string mant = s.Substring(0, idx);
        string exp = s.Substring(idx + 1);
        char esign = '+';
        if (exp.Length > 0 && (exp[0] == '+' || exp[0] == '-')) { esign = exp[0]; exp = exp.Substring(1); }
        exp = exp.TrimStart('0');
        if (exp.Length < 2) exp = exp.PadLeft(2, '0');
        return mant + e + esign + exp;
    }

    private static string StripGTrailingZeros(string s, bool hasExp)
    {
        if (hasExp)
        {
            int idx = s.IndexOfAny(new[] { 'e', 'E' });
            string mant = s.Substring(0, idx);
            string rest = s.Substring(idx);
            if (mant.Contains('.'))
            {
                mant = mant.TrimEnd('0');
                if (mant.EndsWith(".")) mant = mant.Substring(0, mant.Length - 1);
            }
            return mant + rest;
        }
        if (s.Contains('.'))
        {
            s = s.TrimEnd('0');
            if (s.EndsWith(".")) s = s.Substring(0, s.Length - 1);
        }
        return s;
    }
}
