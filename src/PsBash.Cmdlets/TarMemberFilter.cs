namespace PsBash.Cmdlets;

/// <summary>
/// fnmatch(3) as GNU tar uses it for member names and <c>--exclude</c> patterns: <c>*</c> and <c>?</c> match any
/// character INCLUDING <c>/</c> (tar's default <c>--wildcards-match-slash</c>), <c>[...]</c> sets with <c>!</c>/<c>^</c>
/// negation and ranges, <c>\</c> escapes the next character. Case-sensitive. Pure.
/// </summary>
internal static class TarGlob
{
    public static bool HasWildcard(string s) => s.AsSpan().IndexOfAny('*', '?', '[') >= 0;

    public static bool IsMatch(string pattern, string text)
    {
        int pi = 0, ti = 0, star = -1, mark = 0;
        while (ti < text.Length)
        {
            if (pi < pattern.Length && pattern[pi] == '*') { star = pi++; mark = ti; continue; }
            if (pi < pattern.Length && TokenMatches(pattern, pi, text[ti], out int next)) { pi = next; ti++; continue; }
            if (star >= 0) { pi = star + 1; ti = ++mark; continue; }
            return false;
        }
        while (pi < pattern.Length && pattern[pi] == '*') pi++;
        return pi == pattern.Length;
    }

    /// <summary>One non-star pattern token against one character; <paramref name="next"/> is the token's end.</summary>
    private static bool TokenMatches(string p, int pi, char ch, out int next)
    {
        char c = p[pi];
        if (c == '?') { next = pi + 1; return true; }
        if (c == '\\' && pi + 1 < p.Length) { next = pi + 2; return p[pi + 1] == ch; }
        if (c == '[')
        {
            int i = pi + 1;
            bool negate = false;
            if (i < p.Length && (p[i] == '!' || p[i] == '^')) { negate = true; i++; }
            bool matched = false, first = true;
            while (i < p.Length && (p[i] != ']' || first))
            {
                first = false;
                char lo = p[i];
                if (lo == '\\' && i + 1 < p.Length) { i++; lo = p[i]; }
                if (i + 2 < p.Length && p[i + 1] == '-' && p[i + 2] != ']')
                {
                    char hi = p[i + 2];
                    int used = 3;
                    if (hi == '\\' && i + 3 < p.Length) { hi = p[i + 3]; used = 4; }
                    if (ch >= lo && ch <= hi) matched = true;
                    i += used;
                }
                else
                {
                    if (ch == lo) matched = true;
                    i++;
                }
            }
            if (i < p.Length) { next = i + 1; return matched != negate; }
            next = pi + 1;                 // no closing ']': the '[' is an ordinary character
            return ch == '[';
        }
        next = pi + 1;
        return c == ch;
    }
}

/// <summary>
/// Which archive members <c>tar -t</c> / <c>-x</c> act on (GNU tar 1.35, oracle-checked): with no member names
/// every member is selected; otherwise a member is selected by the FIRST name, in command-line order, that
/// matches it — a plain name matches the member of that name and everything below it (<c>d</c> selects
/// <c>d/</c>, <c>d/a</c>; a trailing slash on the name or the member is ignored; <c>d/su</c> selects nothing), a
/// <c>--wildcards</c> name is an fnmatch pattern that matches the member or any leading directory of it. A name that
/// selected nothing is reported afterwards (<see cref="Unmatched"/>). <c>--exclude</c> / <c>-X</c> patterns are always
/// wildcards and unanchored: a pattern excludes a member when it matches any run of whole path components
/// (so <c>sub</c> excludes <c>d/sub</c> and everything below it, <c>d/sub</c> excludes <c>d/sub/b.txt</c>).
/// </summary>
internal sealed class TarMemberFilter
{
    private sealed class Name
    {
        public Name(string text, bool wildcard) { Text = text; Wildcard = wildcard; Trimmed = text.TrimEnd('/'); }
        public string Text { get; }
        public bool Wildcard { get; }
        public string Trimmed { get; }
        public bool Found { get; set; }

        public bool Matches(string member)
        {
            if (!Wildcard) return member == Trimmed || member.StartsWith(Trimmed + "/", StringComparison.Ordinal);
            if (TarGlob.IsMatch(Trimmed, member)) return true;
            for (int i = 0; i < member.Length; i++)
                if (member[i] == '/' && TarGlob.IsMatch(Trimmed, member.Substring(0, i))) return true;
            return false;
        }
    }

    private readonly List<Name> _names = new();
    private readonly List<string> _excludes = new();

    public TarMemberFilter(IEnumerable<(string Text, bool Wildcard)> names, IEnumerable<string> excludePatterns)
    {
        foreach (var (text, wildcard) in names) _names.Add(new Name(text, wildcard));
        foreach (var pattern in excludePatterns)
        {
            var trimmed = pattern.TrimEnd('/');
            if (trimmed.Length > 0) _excludes.Add(trimmed);
        }
    }

    /// <summary>True when member names were given (so only matching members are selected).</summary>
    public bool HasNames => _names.Count > 0;

    public bool HasExcludes => _excludes.Count > 0;

    /// <summary>Whether <paramref name="memberName"/> is acted on; records which name selected it.</summary>
    public bool Selects(string memberName)
    {
        var member = memberName.TrimEnd('/');
        if (IsExcluded(member)) return false;
        if (_names.Count == 0) return true;
        foreach (var name in _names)
        {
            if (!name.Matches(member)) continue;
            name.Found = true;
            return true;
        }
        return false;
    }

    /// <summary>The names that selected no member, as typed, in command-line order.</summary>
    public IEnumerable<string> Unmatched() => _names.Where(n => !n.Found).Select(n => n.Text);

    /// <summary>True when an <c>--exclude</c> pattern matches a run of whole components of <paramref name="path"/>.</summary>
    public bool IsExcluded(string path)
    {
        if (_excludes.Count == 0) return false;
        var comps = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < comps.Length; i++)
        {
            for (int j = i; j < comps.Length; j++)
            {
                var run = string.Join('/', comps, i, j - i + 1);
                foreach (var pattern in _excludes)
                    if (TarGlob.IsMatch(pattern, run)) return true;
            }
        }
        return false;
    }

    /// <summary>Lines of a <c>-T</c> / <c>-X</c> list file: split on <c>\n</c> only (a <c>\r</c> or trailing blank is part of the
    /// name, as in GNU tar); the empty segment after a final newline is dropped, other empty lines are kept (callers skip them) so
    /// line numbers stay right.</summary>
    public static string[] ReadListLines(string text)
    {
        var lines = text.Split('\n');
        return lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }
}
