using System.Text;

namespace PsBash.Core.Parser;

/// <summary>
/// The regex of <c>[[ s =~ re ]]</c> is a POSIX ERE, read by glibc's regcomp; the match runs on .NET. This converts the
/// constructs the two read differently. Used at transpile time for a literal regex and at run time
/// (<c>BashPatternMatch.Ere</c>) for one assembled from expansions.
/// <list type="bullet">
/// <item><c>[:name:]</c> inside a bracket expression becomes its .NET range (<c>[[:digit:]]</c> -> <c>[0-9]</c>);
/// outside a bracket it is left alone.</item>
/// <item><c>{,n}</c> is <c>{0,n}</c> (.NET reads <c>{,n}</c> as literal text).</item>
/// <item>A <c>{</c> that does not start a valid interval (<c>{m}</c>, <c>{m,}</c>, <c>{m,n}</c>, <c>{,n}</c>) right
/// after an atom (<c>{</c>, <c>a{</c>, <c>x{1</c>, <c>(){</c>, <c>x|{</c>) is a regcomp error in bash (status 2) where
/// .NET reads a literal brace: <see cref="ArgumentException"/>, the same exception a pattern .NET rejects raises.</item>
/// </list>
/// Everything else passes through (a <c>\x</c> pair stays a pair, as .NET reads it).
/// </summary>
public static class BashEre
{
    public static string ToDotNet(string pattern)
    {
        if (pattern.IndexOf('{') < 0 && !pattern.Contains("[:", StringComparison.Ordinal)) return pattern;
        var sb = new StringBuilder(pattern.Length + 8);
        bool atom = false;  // something a repetition can apply to precedes this position
        bool quantified = false;    // the last atom already carries a repetition
        int atomStart = -1;         // where the last atom starts in sb
        var groups = new Stack<int>();
        // ERE stacks repetitions (`a{1}{2}` is (a{1}){2}, `a**`); .NET rejects a nested quantifier (or reads `{2}?` as
        // lazy), so a repeated atom is grouped first.
        void Requantify() { if (quantified && atomStart >= 0) { sb.Insert(atomStart, "(?:"); sb.Append(')'); } }
        string p = pattern;
        for (int i = 0; i < p.Length;)
        {
            char c = p[i];
            if (c == '\\' && i + 1 < p.Length)
            {
                atomStart = sb.Length;
                sb.Append(c).Append(p[i + 1]);
                i += 2;
                atom = true;
                quantified = false;
                continue;
            }
            int before = sb.Length;
            if (c == '[' && TryBracket(p, i, sb, out int end))
            {
                atomStart = before;
                i = end;
                atom = true;
                quantified = false;
                continue;
            }
            if (c == '{')
            {
                int j = i + 1;
                int m0 = j;
                while (j < p.Length && char.IsAsciiDigit(p[j])) j++;
                bool hasMin = j > m0;
                bool comma = j < p.Length && p[j] == ',';
                if (comma) j++;
                int n0 = j;
                while (j < p.Length && char.IsAsciiDigit(p[j])) j++;
                bool hasMax = j > n0;
                bool valid = j < p.Length && p[j] == '}' && (hasMin || (comma && hasMax));
                if (!valid || !atom)
                    throw new ArgumentException("syntax error in regular expression");
                Requantify();
                sb.Append('{');
                if (!hasMin) sb.Append('0');
                sb.Append(p, i + 1, j - i);     // through the closing `}`
                i = j + 1;
                atom = true;                    // `a{1}{2}` is valid
                quantified = true;
                continue;
            }
            if (c is '*' or '+' or '?' && atom)
            {
                Requantify();
                sb.Append(c);
                i++;
                quantified = true;
                continue;
            }
            if (c == '(') groups.Push(sb.Length);
            atomStart = c == ')' ? (groups.Count > 0 ? groups.Pop() : -1) : sb.Length;
            sb.Append(c);
            i++;
            atom = c is not ('(' or '|' or '^');
            quantified = false;
        }
        return sb.ToString();
    }

    // The bracket expression opening at i, appended with its POSIX classes translated; false (nothing appended) when it
    // is unclosed. `]` first (after an optional `^`) is a member; `\x` is a pair, as .NET reads it.
    private static bool TryBracket(string p, int i, StringBuilder sb, out int end)
    {
        end = i;
        var cls = new StringBuilder();
        cls.Append('[');
        int j = i + 1;
        if (j < p.Length && p[j] == '^') cls.Append(p[j++]);
        if (j < p.Length && p[j] == ']') cls.Append(p[j++]);
        while (j < p.Length)
        {
            char c = p[j];
            if (c == '\\' && j + 1 < p.Length) { cls.Append(c).Append(p[j + 1]); j += 2; continue; }
            if (c == '[' && j + 1 < p.Length && p[j + 1] == ':')
            {
                int close = p.IndexOf(":]", j + 2, StringComparison.Ordinal);
                if (close > 0 && BashPattern.PosixClass(p[(j + 2)..close]) is { } range)
                {
                    cls.Append(range);
                    j = close + 2;
                    continue;
                }
            }
            if (c == ']')
            {
                cls.Append(']');
                sb.Append(cls);
                end = j + 1;
                return true;
            }
            cls.Append(c);
            j++;
        }
        return false;
    }
}
