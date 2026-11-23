using System.Collections.Concurrent;
using System.Text;

namespace PsBash.Core.Parser;

/// <summary>The outcome of <c>[[ s =~ re ]]</c>: the test's status and what BASH_REMATCH becomes.</summary>
public sealed class BashEreResult
{
    internal static readonly string[] NoGroups = [];

    internal BashEreResult(int status, string[] groups, string? error = null)
    {
        Status = status;
        Groups = groups;
        Error = error;
    }

    /// <summary>0 = matched, 1 = no match, 2 = regcomp rejected the regex (bash leaves BASH_REMATCH alone).</summary>
    public int Status { get; }

    /// <summary>BASH_REMATCH: the whole match, then every group ("" for one that took no part); empty on no match.</summary>
    public string[] Groups { get; }

    /// <summary>The regcomp error (REG_BADRPT, ...) of status 2; bash prints nothing for it.</summary>
    public string? Error { get; }
}

/// <summary>
/// THE POSIX ERE primitive of <c>[[ s =~ re ]]</c>: bash 5.2 hands the word to glibc's <c>regcomp</c>
/// (REG_EXTENDED, plus REG_ICASE under <c>shopt -s nocasematch</c>) and <c>regexec</c>. This is a port of that, not a
/// translation to .NET regex (which differs everywhere: leftmost-first alternation, lazy <c>*?</c>, <c>\d</c>,
/// lookaround, <c>$</c> before a final newline). Guard: <c>EreNoDotNetRegexGuardTests</c>. Spec:
/// <c>docs/specs/regex-ere.md</c>.
/// <list type="bullet">
/// <item><see cref="WordToRegex"/> is bash's <c>quote_string_for_globbing(QGLOB_REGEXP|QGLOB_CTLESC)</c>: a QUOTED
/// character arrives marked with <see cref="Ctlesc"/> and becomes <c>\c</c> when it is an ERE special, the bare
/// character otherwise (so the word <c>\d</c> is the regex <c>d</c>).</item>
/// <item>The parse mirrors glibc's tree shapes (repetition expansion, optional-subexpression marks, the GNU escapes
/// <c>\w \W \s \S \b \B \&lt; \&gt; \` \'</c>, back-references, every REG_* error), so the captures follow glibc's
/// rules, including its quirks.</item>
/// <item>The match is leftmost-longest (one O(n·m) pass, no backtracking); the groups come from glibc's own walk
/// over the states that can still reach the match end. Only a regex with a back-reference searches, under a step
/// budget.</item>
/// </list>
/// Every cost is bounded (tree size, nesting via <see cref="NestingGuard"/>, work, memory): a regex past a bound is
/// status 2, never a hang (host-lifecycle-contract.md §No input may take the host down).
/// </summary>
public static class BashEre
{
    /// <summary>bash's CTLESC: the marker in front of a quoted character of the <c>=~</c> word.</summary>
    public const char Ctlesc = '\u0001';

    private static readonly ConcurrentDictionary<(string, bool), BashEreProgram> Cache = new();
    // The host is long-lived and a run-time regex can differ every iteration: bound the cache.
    private const int MaxCached = 512;

    /// <summary>Match <paramref name="subject"/> against the regcomp text <paramref name="regex"/>.</summary>
    public static BashEreResult Match(string regex, bool icase, string subject) =>
        Compile(regex, icase).Run(subject);

    /// <summary>The compiled regex (or its regcomp error), cached.</summary>
    internal static BashEreProgram Compile(string regex, bool icase)
    {
        if (Cache.TryGetValue((regex, icase), out var hit)) return hit;
        var program = BashEreCompiler.Compile(regex, icase);
        if (Cache.Count > MaxCached) Cache.Clear();
        Cache[(regex, icase)] = program;
        return program;
    }

    /// <summary>
    /// The regcomp text of an <c>=~</c> word whose quoted characters are each preceded by <see cref="Ctlesc"/> (and
    /// whose unquoted text carries a literal CTLESC doubled): bash's <c>quote_string_for_globbing</c> with
    /// QGLOB_REGEXP|QGLOB_CTLESC, quirks included (an unclosed <c>[</c> is rescanned as plain text, a bracket left
    /// open at the end truncates, the class/equivalence/collating state carries across brackets).
    /// </summary>
    public static string WordToRegex(string p)
    {
        int n = p.Length;
        char At(int k) => k < n ? p[k] : '\0';
        var sb = new StringBuilder(n * 2);
        bool cclass = false, equiv = false, collsym = false;
        for (int i = 0; i < n; i++)
        {
            char c = p[i];
            if (c == Ctlesc && i + 1 == n)
            {
                sb.Append(c);
                break;
            }
            if (c == Ctlesc && p[i + 1] == Ctlesc)
            {
                i++;
                sb.Append(p[i]);
                continue;
            }
            if (c == Ctlesc)
            {
                if (!IsEreSpecial(p[i + 1])) continue;
                sb.Append('\\');
                i++;
                sb.Append(p[i]);
                continue;
            }
            if (c == '[' && (i == 0 || p[i - 1] != Ctlesc))
            {
                sb.Append('[');
                i++;
                int savej = sb.Length, savei = i;
                char ch = At(i++);
                if (ch == '^') { sb.Append(ch); ch = At(i++); }
                if (ch == ']') { sb.Append(ch); ch = At(i++); }
                do
                {
                    if (ch == '\0') return sb.ToString();
                    if (ch == Ctlesc)
                    {
                        if (At(i) == '\0') return sb.ToString();
                        sb.Append(p[i++]);
                    }
                    else if (ch == '[' && At(i) == ':') { sb.Append(ch).Append(p[i++]); cclass = true; }
                    else if (cclass && ch == ':' && At(i) == ']') { sb.Append(ch).Append(p[i++]); cclass = false; }
                    else if (ch == '[' && At(i) == '=')
                    {
                        sb.Append(ch).Append(p[i++]);
                        if (At(i) == ']') sb.Append(p[i++]);
                        equiv = true;
                    }
                    else if (equiv && ch == '=' && At(i) == ']') { sb.Append(ch).Append(p[i++]); equiv = false; }
                    else if (ch == '[' && At(i) == '.')
                    {
                        sb.Append(ch).Append(p[i++]);
                        if (At(i) == ']') sb.Append(p[i++]);
                        collsym = true;
                    }
                    else if (collsym && ch == '.' && At(i) == ']') { sb.Append(ch).Append(p[i++]); collsym = false; }
                    else sb.Append(ch);
                }
                while ((ch = At(i++)) != ']' && ch != '\0');
                if (ch == '\0')
                {
                    // No closing bracket: rescan from just after the `[` as ordinary text.
                    i = savei - 1;
                    sb.Length = savej;
                    continue;
                }
                sb.Append(ch);
                i--;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // bash's ere_char: what a quoted character must be backslash-escaped as.
    private static bool IsEreSpecial(char c) =>
        c is '.' or '[' or '\\' or '(' or ')' or '*' or '+' or '?' or '{' or '|' or '^' or '$';

    /// <summary>The marked form of a QUOTED part's value: every character preceded by <see cref="Ctlesc"/>.</summary>
    public static string MarkQuoted(string value)
    {
        if (value.Length == 0) return value;
        var sb = new StringBuilder(value.Length * 2);
        foreach (char c in value) sb.Append(Ctlesc).Append(c);
        return sb.ToString();
    }

    /// <summary>The marked form of an UNQUOTED part's value (regex text): a literal CTLESC doubled.</summary>
    public static string MarkUnquoted(string value) =>
        value.IndexOf(Ctlesc) < 0 ? value : value.Replace("\u0001", "\u0001\u0001", StringComparison.Ordinal);
}
