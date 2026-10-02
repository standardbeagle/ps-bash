using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Maps a PCRE2 (<c>-P</c>) or Rust-regex (<c>rg</c> default) pattern onto the .NET regex dialect, and reports the
/// constructs .NET cannot express instead of silently mis-matching.
///
/// <para><b>Converted</b> (both dialects): <c>(?P&lt;n&gt;…)</c> to <c>(?&lt;n&gt;…)</c>, <c>(?P=n)</c> to
/// <c>\k&lt;n&gt;</c>, <c>\x{HHHH}</c>, the brace-less <c>\pL</c> / <c>\PL</c> property forms.
/// <b>Converted</b> (PCRE only): <c>\Q…\E</c> literals, <c>\h</c> / <c>\H</c>, <c>\R</c>, <c>\N</c>, possessive
/// quantifiers (<c>a++</c>, <c>a*+</c>, <c>a?+</c>, <c>a{n,m}+</c> become atomic groups), and the no-op
/// start-of-pattern verbs <c>(*UTF8)</c> / <c>(*UTF)</c> / <c>(*UCP)</c>.
/// <b>Rejected</b> with a message (PCRE only): <c>\K</c>, <c>\X</c>, recursion / subroutine calls
/// (<c>(?R)</c>, <c>(?1)</c>, <c>(?&amp;n)</c>, <c>(?P&gt;n)</c>), branch reset <c>(?|</c>, backtracking verbs
/// <c>(*…)</c>, callouts <c>(?C)</c>, and the ungreedy flag <c>(?U)</c>. Script properties such as
/// <c>\p{Greek}</c> are left to the .NET constructor, which rejects the names it does not know.</para>
/// </summary>
internal static class PcreToDotNet
{
    /// <summary>Translate <paramref name="pattern"/>; false (with <paramref name="error"/>) for an unsupported construct.</summary>
    internal static bool TryTranslate(string pattern, bool pcre, out string result, out string? error)
    {
        error = null;
        var sb = new StringBuilder(pattern.Length + 8);
        var groupStarts = new Stack<int>();
        int lastAtom = -1;
        bool inClass = false;
        int i = 0;
        int n = pattern.Length;

        // PCRE start-of-pattern option verbs that only select Unicode mode (always on here).
        if (pcre)
        {
            while (pattern.AsSpan(i).StartsWith("(*UTF8)") || pattern.AsSpan(i).StartsWith("(*UTF)") || pattern.AsSpan(i).StartsWith("(*UCP)"))
                i = pattern.IndexOf(')', i) + 1;
        }

        while (i < n)
        {
            char c = pattern[i];

            if (c == '\\' && i + 1 < n)
            {
                char d = pattern[i + 1];
                int atomStart = sb.Length;
                switch (d)
                {
                    case 'Q' when pcre:
                    {
                        int end = pattern.IndexOf("\\E", i + 2, StringComparison.Ordinal);
                        string lit = end < 0 ? pattern.Substring(i + 2) : pattern.Substring(i + 2, end - (i + 2));
                        i = end < 0 ? n : end + 2;
                        foreach (char ch in lit)
                        {
                            atomStart = sb.Length;
                            sb.Append(System.Text.RegularExpressions.Regex.Escape(ch.ToString()));
                            if (!inClass) lastAtom = atomStart;
                        }
                        continue;
                    }
                    case 'K' when pcre: return Fail("\\K (reset match start) has no .NET equivalent", out result, out error);
                    case 'X' when pcre: return Fail("\\X (extended grapheme cluster) has no .NET equivalent", out result, out error);
                    case 'h' when pcre: sb.Append(inClass ? " \\t" : "[ \\t]"); i += 2; break;
                    case 'H' when pcre:
                        if (inClass) return Fail("\\H inside a character class is not supported", out result, out error);
                        sb.Append("[^ \\t]"); i += 2; break;
                    case 'R' when pcre:
                        if (inClass) return Fail("\\R inside a character class is not allowed", out result, out error);
                        sb.Append("(?:\\r\\n|\\n|\\r)"); i += 2; break;
                    case 'N' when pcre && !inClass: sb.Append("[^\\n]"); i += 2; break;
                    case 'x' when i + 2 < n && pattern[i + 2] == '{':
                    {
                        int close = pattern.IndexOf('}', i + 3);
                        if (close < 0 || !int.TryParse(pattern.AsSpan(i + 3, close - (i + 3)), System.Globalization.NumberStyles.HexNumber, null, out int cp)
                            || cp > 0x10FFFF)
                            return Fail("invalid \\x{...} escape", out result, out error);
                        if (cp <= 0xFFFF) sb.Append("\\u").Append(cp.ToString("X4"));
                        else
                        {
                            string s = char.ConvertFromUtf32(cp);
                            sb.Append("(?:\\u").Append(((int)s[0]).ToString("X4")).Append("\\u").Append(((int)s[1]).ToString("X4")).Append(')');
                        }
                        i = close + 1;
                        break;
                    }
                    case 'p' or 'P' when i + 2 < n && pattern[i + 2] != '{' && char.IsLetter(pattern[i + 2]):
                        sb.Append('\\').Append(d).Append('{').Append(pattern[i + 2]).Append('}');
                        i += 3;
                        break;
                    default:
                        sb.Append(c).Append(d);
                        i += 2;
                        break;
                }
                if (!inClass) lastAtom = atomStart;
                continue;
            }

            if (inClass)
            {
                sb.Append(c);
                if (c == '[' && i + 1 < n && pattern[i + 1] == ':')
                {
                    // POSIX class inside a bracket expression: copy through ":]".
                    int end = pattern.IndexOf(":]", i + 2, StringComparison.Ordinal);
                    if (end > 0) { sb.Append(pattern, i + 1, end + 2 - (i + 1)); i = end + 2; continue; }
                }
                if (c == ']') inClass = false;
                i++;
                continue;
            }

            switch (c)
            {
                case '[':
                {
                    lastAtom = sb.Length;
                    sb.Append(c);
                    i++;
                    inClass = true;
                    // A leading ^ and a literal ] directly after the opener belong to the class.
                    if (i < n && pattern[i] == '^') { sb.Append('^'); i++; }
                    if (i < n && pattern[i] == ']') { sb.Append("\\]"); i++; }
                    continue;
                }
                case '(':
                {
                    if (pcre && i + 1 < n && pattern[i + 1] == '*')
                        return Fail("backtracking control verbs (*VERB) are not supported", out result, out error);
                    if (i + 1 < n && pattern[i + 1] == '?')
                    {
                        string rest = pattern.Substring(i + 2, Math.Min(4, n - (i + 2)));
                        if (rest.StartsWith("P<", StringComparison.Ordinal))
                        {
                            groupStarts.Push(sb.Length); sb.Append("(?<"); i += 4; continue;
                        }
                        if (rest.StartsWith("P=", StringComparison.Ordinal))
                        {
                            int close = pattern.IndexOf(')', i);
                            if (close < 0) return Fail("missing closing parenthesis", out result, out error);
                            lastAtom = sb.Length;
                            sb.Append("\\k<").Append(pattern, i + 4, close - (i + 4)).Append('>');
                            i = close + 1;
                            continue;
                        }
                        if (pcre)
                        {
                            if (rest.StartsWith("P>", StringComparison.Ordinal) || rest.StartsWith("R)", StringComparison.Ordinal)
                                || rest.StartsWith('&') || IsSubroutineCall(pattern, i + 2))
                                return Fail("recursion and subroutine calls are not supported", out result, out error);
                            if (rest.StartsWith("|")) return Fail("branch reset groups (?|...) are not supported", out result, out error);
                            if (rest.StartsWith("C")) return Fail("callouts are not supported", out result, out error);
                            if (rest.Length > 0 && IsFlagGroupWithU(pattern, i + 2))
                                return Fail("the ungreedy flag (?U) is not supported", out result, out error);
                        }
                    }
                    groupStarts.Push(sb.Length);
                    sb.Append(c);
                    i++;
                    continue;
                }
                case ')':
                {
                    lastAtom = groupStarts.Count > 0 ? groupStarts.Pop() : sb.Length;
                    sb.Append(c);
                    i++;
                    continue;
                }
                case '*' or '+' or '?':
                case '{' when IsQuantifierBrace(pattern, i):
                {
                    int qStart = sb.Length;
                    int j = i;
                    if (c == '{') { j = pattern.IndexOf('}', i); sb.Append(pattern, i, j - i + 1); }
                    else sb.Append(c);
                    j++;
                    // lazy marker
                    if (j < n && pattern[j] == '?') { sb.Append('?'); j++; }
                    else if (pcre && j < n && pattern[j] == '+' && lastAtom >= 0)
                    {
                        // possessive: (?>atom quantifier)
                        sb.Insert(lastAtom, "(?>");
                        sb.Append(')');
                        j++;
                    }
                    _ = qStart;
                    i = j;
                    continue;
                }
                default:
                    lastAtom = sb.Length;
                    sb.Append(c);
                    i++;
                    continue;
            }
        }

        result = sb.ToString();
        return true;
    }

    private static bool IsSubroutineCall(string p, int from)
    {
        int j = from;
        if (j < p.Length && (p[j] == '+' || p[j] == '-')) j++;
        int digitsStart = j;
        while (j < p.Length && char.IsDigit(p[j])) j++;
        return j > digitsStart && j < p.Length && p[j] == ')';
    }

    private static bool IsFlagGroupWithU(string p, int from)
    {
        int j = from;
        while (j < p.Length && (char.IsLetter(p[j]) || p[j] == '-' || p[j] == '^'))
        {
            if (p[j] == 'U') return true;
            j++;
        }
        return false;
    }

    private static bool IsQuantifierBrace(string p, int i)
    {
        int close = p.IndexOf('}', i);
        if (close < 0) return false;
        for (int j = i + 1; j < close; j++)
            if (!char.IsDigit(p[j]) && p[j] != ',') return false;
        return close > i + 1;
    }

    private static bool Fail(string msg, out string result, out string? error)
    {
        result = string.Empty;
        error = msg;
        return false;
    }
}
