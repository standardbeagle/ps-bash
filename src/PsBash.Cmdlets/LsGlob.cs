namespace PsBash.Cmdlets;

/// <summary>
/// <c>fnmatch(pattern, name, FNM_PERIOD)</c> for <c>ls -I / --hide</c>: <c>*</c>, <c>?</c>, <c>[set]</c> (with <c>!</c>/<c>^</c>
/// negation, ranges and <c>[:class:]</c> for the common classes) and backslash escapes; a leading period in the name
/// is matched only by a literal period in the pattern. Case-sensitive, as GNU.
/// </summary>
internal static class LsGlob
{
    public static bool Match(string pattern, string name)
        => MatchAt(pattern, 0, name, 0, name.Length > 0 && name[0] == '.');

    /// <summary>
    /// <see cref="Match(string, string)"/> with bash's <c>dotglob</c> switch: when <paramref name="allowLeadingPeriod"/>
    /// a leading period needs no literal period in the pattern.
    /// </summary>
    public static bool Match(string pattern, string name, bool allowLeadingPeriod)
        => MatchAt(pattern, 0, name, 0, !allowLeadingPeriod && name.Length > 0 && name[0] == '.');

    private static bool MatchAt(string p, int pi, string s, int si, bool periodGuard)
    {
        while (pi < p.Length)
        {
            char pc = p[pi];
            if (pc == '*')
            {
                if (periodGuard && si == 0) return false;
                while (pi < p.Length && p[pi] == '*') pi++;
                if (pi == p.Length) return true;
                for (int k = si; k <= s.Length; k++)
                    if (MatchAt(p, pi, s, k, false)) return true;
                return false;
            }
            if (si >= s.Length) return false;
            char sc = s[si];
            if (pc == '?')
            {
                if (periodGuard && si == 0) return false;
                pi++; si++;
                continue;
            }
            if (pc == '[')
            {
                if (periodGuard && si == 0) return false;
                if (!TryBracket(p, ref pi, sc, out bool hit)) { if (sc != '[') return false; pi++; si++; continue; }
                if (!hit) return false;
                si++;
                continue;
            }
            if (pc == '\\' && pi + 1 < p.Length) { pi++; pc = p[pi]; }
            if (pc != sc) return false;
            pi++; si++;
        }
        return si == s.Length;
    }

    /// <summary>Parses a bracket expression starting at <paramref name="pi"/>; false (pi unchanged) when unterminated.</summary>
    private static bool TryBracket(string p, ref int pi, char c, out bool hit)
    {
        hit = false;
        int i = pi + 1;
        bool negate = false;
        if (i < p.Length && (p[i] == '!' || p[i] == '^')) { negate = true; i++; }
        bool first = true, matched = false;
        while (i < p.Length)
        {
            char x = p[i];
            if (x == ']' && !first) { pi = i + 1; hit = matched != negate; return true; }
            first = false;
            if (x == '[' && i + 1 < p.Length && p[i + 1] == ':')
            {
                int end = p.IndexOf(":]", i + 2, StringComparison.Ordinal);
                if (end > 0)
                {
                    matched |= InClass(p.Substring(i + 2, end - i - 2), c);
                    i = end + 2;
                    continue;
                }
            }
            if (x == '\\' && i + 1 < p.Length) { i++; x = p[i]; }
            if (i + 2 < p.Length && p[i + 1] == '-' && p[i + 2] != ']')
            {
                char hi = p[i + 2];
                if (hi == '\\' && i + 3 < p.Length) { hi = p[i + 3]; i++; }
                if (c >= x && c <= hi) matched = true;
                i += 3;
                continue;
            }
            if (c == x) matched = true;
            i++;
        }
        return false;
    }

    private static bool InClass(string name, char c) => name switch
    {
        "alpha" => char.IsLetter(c), "digit" => c is >= '0' and <= '9', "alnum" => char.IsLetterOrDigit(c),
        "upper" => char.IsUpper(c), "lower" => char.IsLower(c), "space" => char.IsWhiteSpace(c),
        "punct" => char.IsPunctuation(c) || char.IsSymbol(c), "xdigit" => Uri.IsHexDigit(c),
        "blank" => c is ' ' or '\t', "cntrl" => char.IsControl(c), "print" => !char.IsControl(c),
        "graph" => !char.IsControl(c) && c != ' ',
        _ => false,
    };
}
