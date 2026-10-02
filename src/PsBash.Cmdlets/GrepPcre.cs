using System.Text;
using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

/// <summary>
/// <c>grep -P</c>: maps a PCRE pattern onto the .NET regex engine. .NET already speaks most of PCRE
/// (lookaround, atomic groups, lazy quantifiers, named groups, backreferences, <c>\b \A \z \Z \G</c>,
/// <c>(?i)</c> flags, conditionals); the translator rewrites the constructs it spells differently:
/// <c>\Q..\E</c>, <c>\K</c> (top level, as a lookbehind), <c>\h \H \v \V \R \N \X \C</c>, <c>\x{HHHH}</c>,
/// possessive quantifiers (<c>a++</c> becomes <c>(?&gt;a+)</c>), <c>(?P&lt;n&gt;..)</c> / <c>(?P=n)</c>,
/// <c>\g{N}</c> / <c>\k{n}</c>, leading <c>(*UTF8)</c>-style option verbs and <c>(?C)</c> callouts (dropped).
/// Constructs with no .NET equivalent are refused (the caller exits 2): recursion and subroutine calls
/// (<c>(?R)</c> <c>(?1)</c> <c>(?&amp;n)</c> <c>\g&lt;n&gt;</c>), branch reset <c>(?|</c>, backtracking verbs
/// <c>(*FAIL)</c> / <c>(*SKIP)</c> ..., inline <c>(?U)</c> / <c>(?J)</c>, <c>\K</c> inside a group or next to a
/// top-level alternation, <c>\o{}</c>.
/// </summary>
internal static class GrepPcre
{
    private static readonly HashSet<string> OptionVerbs = new(StringComparer.Ordinal)
    {
        "UTF", "UTF8", "UCP", "CR", "LF", "CRLF", "ANYCRLF", "ANY", "NUL", "BSR_ANYCRLF", "BSR_UNICODE",
        "NO_START_OPT", "NO_AUTO_POSSESS", "NO_JIT", "NO_DOTSTAR_ANCHOR", "NOTEMPTY", "NOTEMPTY_ATSTART",
    };

    private const string HSpace = @" \t\u00A0\u1680\u180E\u2000-\u200A\u202F\u205F\u3000";
    private const string VSpace = @"\n\x0B\f\r\u0085\u2028\u2029";

    public static bool TryTranslate(string p, out string net, out string? error)
    {
        net = "";
        error = null;
        var o = new StringBuilder(p.Length + 16);
        var groupStarts = new Stack<int>();
        int lastAtom = -1, groups = 0, kPos = -1;
        bool topAlt = false;
        int i = 0;

        // Leading (*VERB) option settings carry no meaning for the .NET engine: drop them.
        while (i + 2 < p.Length && p[i] == '(' && p[i + 1] == '*')
        {
            int close = p.IndexOf(')', i);
            if (close < 0) break;
            string verb = p[(i + 2)..close];
            if (!OptionVerbs.Contains(verb) && !verb.StartsWith("LIMIT_", StringComparison.Ordinal)) break;
            i = close + 1;
        }

        for (; i < p.Length; i++)
        {
            char c = p[i];
            switch (c)
            {
                case '\\':
                    if (i + 1 >= p.Length) { error = "\\ at end of pattern"; return false; }
                    if (!Escape(p, ref i, o, ref lastAtom, ref kPos, groupStarts.Count, groups, inClass: false, ref error))
                        return false;
                    break;

                case '[':
                    lastAtom = o.Length;
                    if (!CharClass(p, ref i, o, ref error)) return false;
                    break;

                case '(':
                    if (!GroupOpen(p, ref i, o, groupStarts, ref groups, ref error)) return false;
                    break;

                case ')':
                    if (groupStarts.Count == 0) { error = "unmatched closing parenthesis"; return false; }
                    lastAtom = groupStarts.Pop();
                    o.Append(')');
                    break;

                case '|':
                    if (groupStarts.Count == 0) topAlt = true;
                    o.Append('|');
                    lastAtom = -1;
                    break;

                case '*':
                case '+':
                case '?':
                    o.Append(c);
                    QuantifierSuffix(p, ref i, o, lastAtom);
                    break;

                case '{':
                {
                    int end = QuantifierEnd(p, i);
                    if (end < 0) { lastAtom = o.Length; o.Append("\\{"); break; }
                    o.Append(p, i, end - i + 1);
                    i = end;
                    QuantifierSuffix(p, ref i, o, lastAtom);
                    break;
                }

                case '^':
                case '$':
                    o.Append(c);
                    lastAtom = -1;
                    break;

                case '.':
                    lastAtom = o.Length;
                    o.Append(c);
                    break;

                default:
                    lastAtom = o.Length;
                    o.Append(c);
                    break;
            }
        }

        if (groupStarts.Count > 0) { error = "missing closing parenthesis"; return false; }

        string result = o.ToString();
        if (kPos >= 0)
        {
            if (topAlt) { error = "unsupported PCRE construct: \\K next to a top-level alternation"; return false; }
            result = "(?<=" + result[..kPos] + ")" + result[kPos..];
        }
        net = result;
        return true;
    }

    /// <summary>After a quantifier: a trailing '+' makes it possessive, a trailing '?' lazy.</summary>
    private static void QuantifierSuffix(string p, ref int i, StringBuilder o, int lastAtom)
    {
        if (i + 1 >= p.Length) return;
        if (p[i + 1] == '?') { o.Append('?'); i++; return; }
        if (p[i + 1] == '+' && lastAtom >= 0)
        {
            o.Insert(lastAtom, "(?>");
            o.Append(')');
            i++;
        }
    }

    /// <summary>Index of the closing brace when <c>{n}</c> / <c>{n,}</c> / <c>{n,m}</c> starts at <paramref name="i"/>, else -1.</summary>
    private static int QuantifierEnd(string p, int i)
    {
        int j = i + 1;
        int digits = 0;
        while (j < p.Length && char.IsAsciiDigit(p[j])) { j++; digits++; }
        if (digits == 0) return -1;
        if (j < p.Length && p[j] == ',')
        {
            j++;
            while (j < p.Length && char.IsAsciiDigit(p[j])) j++;
        }
        return j < p.Length && p[j] == '}' ? j : -1;
    }

    private static bool GroupOpen(string p, ref int i, StringBuilder o, Stack<int> groupStarts, ref int groups, ref string? error)
    {
        int start = o.Length;
        if (i + 1 < p.Length && p[i + 1] == '*')
        {
            int verbEnd = p.IndexOf(')', i);
            error = "unsupported PCRE construct: backtracking verb " + (verbEnd < 0 ? p[i..] : p[i..(verbEnd + 1)]);
            return false;
        }
        if (i + 1 < p.Length && p[i + 1] == '?')
        {
            string rest = p[(i + 2)..];
            if (rest.StartsWith('#'))
            {
                int close = p.IndexOf(')', i);
                if (close < 0) { error = "missing ) after comment"; return false; }
                i = close;
                return true;
            }
            if (rest.StartsWith('C'))
            {
                int close = p.IndexOf(')', i);
                if (close < 0) { error = "missing closing parenthesis for callout"; return false; }
                i = close; // (?C n) callouts do nothing for grep
                return true;
            }
            if (rest.StartsWith("P<", StringComparison.Ordinal))
            {
                o.Append("(?<");
                i += 3;
                groups++;
                groupStarts.Push(start);
                return true;
            }
            if (rest.StartsWith("P=", StringComparison.Ordinal))
            {
                int close = p.IndexOf(')', i);
                if (close < 0) { error = "missing closing parenthesis"; return false; }
                o.Append("\\k<").Append(p, i + 4, close - (i + 4)).Append('>');
                i = close;
                return true;
            }
            if (rest.StartsWith("P>", StringComparison.Ordinal) || rest.StartsWith('&')
                || rest.StartsWith('R') || rest.StartsWith('|')
                || (rest.Length > 0 && (char.IsAsciiDigit(rest[0]) || ((rest[0] is '+' or '-') && rest.Length > 1 && char.IsAsciiDigit(rest[1])))))
            {
                error = rest.StartsWith('|')
                    ? "unsupported PCRE construct: branch reset group (?|"
                    : "unsupported PCRE construct: recursion / subroutine call";
                return false;
            }
            // inline flag groups: (?i) (?i:..) (?-i) ...; U (ungreedy) and J (duplicate names) have no .NET form
            int k = 0;
            while (k < rest.Length && (char.IsAsciiLetter(rest[k]) || rest[k] == '-' || rest[k] == '^'))
            {
                if (rest[k] is 'U' or 'J') { error = $"unsupported PCRE construct: inline option ({rest[k]})"; return false; }
                k++;
            }
            if (k > 0 && k < rest.Length && (rest[k] == ')' || rest[k] == ':'))
            {
                o.Append("(?");
                o.Append(rest[..k].Replace("^", ""));
                if (rest[k] == ')') { o.Append(')'); i += 2 + k; return true; }
                o.Append(':');
                i += 2 + k;
                groupStarts.Push(start);
                return true;
            }
            if (rest.StartsWith('<') && rest.Length > 1 && rest[1] != '=' && rest[1] != '!') groups++;
            else if (rest.StartsWith('\'')) groups++;
            o.Append("(?");
            i++;
            groupStarts.Push(start);
            return true;
        }
        groups++;
        o.Append('(');
        groupStarts.Push(start);
        return true;
    }

    private static bool CharClass(string p, ref int i, StringBuilder o, ref string? error)
    {
        int j = i + 1;
        o.Append('[');
        if (j < p.Length && p[j] == '^') { o.Append('^'); j++; }
        if (j < p.Length && p[j] == ']') { o.Append("\\]"); j++; }
        for (; j < p.Length; j++)
        {
            char c = p[j];
            if (c == ']') { o.Append(']'); i = j; return true; }
            if (c == '[' && j + 1 < p.Length && p[j + 1] == ':')
            {
                int close = p.IndexOf(":]", j + 2, StringComparison.Ordinal);
                if (close > 0) { o.Append(p, j, close + 2 - j); j = close + 1; continue; }
            }
            if (c == '[') { o.Append("\\["); continue; }
            if (c == '\\' && j + 1 < p.Length)
            {
                int dummyAtom = 0, dummyK = -1;
                int jj = j;
                if (!Escape(p, ref jj, o, ref dummyAtom, ref dummyK, 0, 0, inClass: true, ref error)) return false;
                j = jj;
                continue;
            }
            o.Append(c);
        }
        error = "missing terminating ] for character class";
        return false;
    }

    /// <summary>Translate the escape starting at <c>p[i] == '\\'</c>; leaves <paramref name="i"/> on its last character.</summary>
    private static bool Escape(string p, ref int i, StringBuilder o, ref int lastAtom, ref int kPos,
        int depth, int groups, bool inClass, ref string? error)
    {
        char n = p[i + 1];
        int atom = o.Length;
        switch (n)
        {
            case 'Q':
            {
                int end = p.IndexOf("\\E", i + 2, StringComparison.Ordinal);
                string lit = end < 0 ? p[(i + 2)..] : p[(i + 2)..end];
                foreach (char ch in lit)
                {
                    lastAtom = o.Length;
                    o.Append(Regex.Escape(ch.ToString()));
                }
                i = end < 0 ? p.Length - 1 : end + 1;
                return true;
            }
            case 'E':
                i++;
                return true;
            case 'K':
                if (inClass || depth > 0) { error = "unsupported PCRE construct: \\K inside a group"; return false; }
                kPos = o.Length;
                i++;
                return true;
            case 'h':
                o.Append(inClass ? HSpace : "[" + HSpace + "]");
                break;
            case 'H':
                o.Append(inClass ? "" : "[^" + HSpace + "]");
                if (inClass) { error = "unsupported PCRE construct: \\H inside a character class"; return false; }
                break;
            case 'v':
                o.Append(inClass ? VSpace : "[" + VSpace + "]");
                break;
            case 'V':
                if (inClass) { error = "unsupported PCRE construct: \\V inside a character class"; return false; }
                o.Append("[^" + VSpace + "]");
                break;
            case 'R':
                o.Append("(?:\\r\\n|[" + VSpace + "])");
                break;
            case 'N':
                o.Append("[^\\n]");
                break;
            case 'X':
                o.Append("(?:\\P{M}\\p{M}*)");
                break;
            case 'C':
                o.Append('.');
                break;
            case 'o':
                error = "unsupported PCRE construct: \\o{...}";
                return false;
            case 'x':
                if (i + 2 < p.Length && p[i + 2] == '{')
                {
                    int close = p.IndexOf('}', i + 3);
                    if (close < 0 || !int.TryParse(p.AsSpan(i + 3, close - i - 3), System.Globalization.NumberStyles.HexNumber, null, out int cp)
                        || cp > 0x10FFFF)
                    { error = "non-hex character in \\x{} (closing brace missing?)"; return false; }
                    o.Append(cp <= 0xFFFF ? $"\\u{cp:X4}" : Regex.Escape(char.ConvertFromUtf32(cp)));
                    i = close;
                    if (!inClass) lastAtom = atom;
                    return true;
                }
                o.Append("\\x");
                break;
            case 'g':
            {
                // \gN \g{N} \g{-N} \g{name}; \g<..> / \g'..' are subroutine calls
                int j = i + 2;
                if (j < p.Length && (p[j] == '<' || p[j] == '\''))
                { error = "unsupported PCRE construct: subroutine call"; return false; }
                string refText;
                int endIdx;
                if (j < p.Length && p[j] == '{')
                {
                    int close = p.IndexOf('}', j);
                    if (close < 0) { error = "\\g is not followed by a braced, angle-bracketed, or quoted name/number"; return false; }
                    refText = p[(j + 1)..close];
                    endIdx = close;
                }
                else
                {
                    int k = j;
                    if (k < p.Length && p[k] == '-') k++;
                    while (k < p.Length && char.IsAsciiDigit(p[k])) k++;
                    refText = p[j..k];
                    endIdx = k - 1;
                }
                if (int.TryParse(refText, out int num))
                {
                    if (num < 0) num = groups + 1 + num;
                    o.Append('\\').Append(num);
                }
                else o.Append("\\k<").Append(refText).Append('>');
                i = endIdx;
                if (!inClass) lastAtom = atom;
                return true;
            }
            case 'k':
            {
                int j = i + 2;
                if (j < p.Length && (p[j] is '<' or '\'' or '{'))
                {
                    char closeCh = p[j] switch { '<' => '>', '{' => '}', _ => '\'' };
                    int close = p.IndexOf(closeCh, j + 1);
                    if (close < 0) { error = "\\k is not followed by a braced, angle-bracketed, or quoted name"; return false; }
                    o.Append("\\k<").Append(p, j + 1, close - j - 1).Append('>');
                    i = close;
                    if (!inClass) lastAtom = atom;
                    return true;
                }
                o.Append("\\k");
                break;
            }
            case 'e':
                o.Append("\\x1B");
                break;
            case 'p':
            case 'P':
                if (i + 2 < p.Length && p[i + 2] == '{')
                {
                    int close = p.IndexOf('}', i + 3);
                    if (close < 0) { error = "malformed \\P or \\p sequence"; return false; }
                    o.Append(p, i, close - i + 1);
                    i = close;
                    if (!inClass) lastAtom = atom;
                    return true;
                }
                o.Append('\\').Append(n);
                break;
            default:
                o.Append('\\').Append(n);
                break;
        }
        if (!inClass) lastAtom = atom;
        i++;
        return true;
    }
}
