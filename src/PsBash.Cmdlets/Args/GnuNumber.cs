namespace PsBash.Cmdlets.Args;

/// <summary>
/// Pure parser for the GNU coreutils <c>NUM</c> argument of head/tail (<c>-n</c>, <c>-c</c>):
/// an optional sign, decimal digits, and an optional multiplier suffix
/// (<c>b</c> 512, <c>kB</c> 1000, <c>K</c>/<c>k</c>/<c>KiB</c> 1024, <c>MB</c> 1000², <c>M</c>/<c>MiB</c> 1024²,
/// <c>GB</c>/<c>G</c>/<c>GiB</c>, <c>TB</c>/<c>T</c>/<c>TiB</c>, ...). Anything else is invalid, which
/// the old per-cmdlet scans silently ignored (<c>head -n abc</c> printed 10 lines, exit 0).
/// The result saturates at <see cref="int.MaxValue"/> because every caller stores counts as
/// <c>int</c>; a count that large is "everything" for any real input.
/// </summary>
public static class GnuNumber
{
    /// <summary>
    /// Parse <paramref name="s"/>. <paramref name="sign"/> is <c>'+'</c>, <c>'-'</c> or <c>'\0'</c>
    /// (none); <paramref name="magnitude"/> is the non-negative count after the multiplier.
    /// </summary>
    public static bool TryParse(string s, out int magnitude, out char sign)
    {
        magnitude = 0;
        sign = '\0';
        int i = 0;
        if (i < s.Length && (s[i] == '+' || s[i] == '-'))
        {
            sign = s[i];
            i++;
        }

        int digitsStart = i;
        long value = 0;
        bool saturated = false;
        while (i < s.Length && s[i] >= '0' && s[i] <= '9')
        {
            if (!saturated)
            {
                value = value * 10 + (s[i] - '0');
                if (value > int.MaxValue) saturated = true;
            }
            i++;
        }
        if (i == digitsStart) return false;

        long mult = 1;
        if (i < s.Length)
        {
            if (!TryMultiplier(s.AsSpan(i), out mult)) return false;
        }

        if (saturated) { magnitude = int.MaxValue; return true; }
        // Overflow-safe: value <= int.MaxValue but mult can be 2^40 (P/E/Z/...).
        magnitude = value == 0 ? 0
            : value > int.MaxValue / mult ? int.MaxValue
            : (int)(value * mult);
        return true;
    }

    /// <summary>
    /// GNU <c>xstrtoumax</c> with valid suffixes <c>bkKmMGTPEZYRQ</c> (lowercase ONLY b, k, m —
    /// oracle: <c>head -c 1t</c> is "invalid number of bytes"). The letter is followed by nothing
    /// (base 1024), <c>B</c> (base 1000) or <c>iB</c> (base 1024); <c>b</c> stands alone (512).
    /// </summary>
    private static bool TryMultiplier(ReadOnlySpan<char> suffix, out long mult)
    {
        mult = 1;
        char letter = suffix[0];
        int exp = letter switch
        {
            'k' or 'K' => 1,
            'm' or 'M' => 2,
            'G' => 3,
            'T' => 4,
            'P' => 5,
            'E' => 6,
            'Z' => 7,
            'Y' => 8,
            'R' => 9,
            'Q' => 10,
            _ => 0,
        };
        var rest = suffix.Slice(1);
        if (letter == 'b')
        {
            if (rest.Length != 0) return false;
            mult = 512;
            return true;
        }
        if (exp == 0) return false;

        long bas;
        if (rest.Length == 0 || rest.SequenceEqual("iB")) bas = 1024;
        else if (rest.SequenceEqual("B")) bas = 1000;
        else return false;

        // Exponents past 4 exceed any int count; clamp the multiplier so the caller saturates.
        for (int i = 0; i < exp && mult < (1L << 40); i++) mult *= bas;
        return true;
    }
}