using System.Collections.Specialized;
using System.Text;
using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

/// <summary>
/// jq's regex builtins on .NET regular expressions: flags <c>g i x n s p l</c>, captures reported in order of appearance (named ones
/// carry their name), offsets in code points, the key order and the empty-capture / zero-width-match quirks of jq 1.7 (a
/// zero-width match reports no captures; an empty or unmatched capture is <c>offset, string, length, name</c>).
/// </summary>
internal sealed class JqRegex
{
    private sealed record GroupRef(int Number, string? Name);

    private readonly Regex _regex;
    private readonly List<GroupRef> _groups;

    public bool Global { get; }
    private readonly bool _skipEmpty;

    private JqRegex(Regex regex, List<GroupRef> groups, bool global, bool skipEmpty)
    {
        _regex = regex;
        _groups = groups;
        Global = global;
        _skipEmpty = skipEmpty;
    }

    private static readonly Dictionary<(string, string), JqRegex> Cache = new();

    public static JqRegex Get(object? pattern, object? flags)
    {
        if (pattern is not string re) throw new JqError($"{JqInterp.Describe(pattern)} cannot be matched, as it is not a string");
        if (flags != null && flags is not string) throw new JqError($"{JqInterp.Describe(flags)} is not a string");
        var key = (re, (string?)flags ?? "");
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            var compiled = Compile(re, (string?)flags);
            if (Cache.Count < 512) Cache[key] = compiled;
            return compiled;
        }
    }

    private static JqRegex Compile(string pattern, string? flags)
    {
        var options = RegexOptions.None;
        bool global = false, skipEmpty = false;
        foreach (char f in flags ?? "")
        {
            switch (f)
            {
                case 'g': global = true; break;
                case 'i': options |= RegexOptions.IgnoreCase; break;
                case 'x': options |= RegexOptions.IgnorePatternWhitespace; break;
                case 'n': skipEmpty = true; break;
                case 's': break;                                   // single-line anchors: the .NET default
                case 'p': options |= RegexOptions.Singleline; break; // dot matches newline
                case 'l': break;                                   // longest match: not modelled
                default: throw new JqError($"{flags} is not a valid modifier string");
            }
        }

        string translated = Translate(pattern, out var groups);
        try
        {
            return new JqRegex(new Regex(translated, options, TimeSpan.FromSeconds(30)), groups, global, skipEmpty);
        }
        catch (ArgumentException ex)
        {
            // Oniguruma's wording for the common mistakes
            string message =
                ex.Message.Contains("Not enough )", StringComparison.OrdinalIgnoreCase) ? "end pattern with unmatched parenthesis"
                : ex.Message.Contains("Too many )", StringComparison.OrdinalIgnoreCase) ? "unmatched close parenthesis"
                : ex.Message.Contains("Unterminated [] set", StringComparison.OrdinalIgnoreCase) ? "premature end of char-class"
                : ex.Message.Contains("following nothing", StringComparison.OrdinalIgnoreCase) ? "target of repeat operator is not specified"
                : ex.Message;
            throw new JqError($"Regex failure: {message}");
        }
    }

    /// <summary>Oniguruma → .NET: POSIX classes, <c>\h</c>, <c>(?P&lt;n&gt;</c>; also records the capture groups in order of appearance.</summary>
    private static string Translate(string pattern, out List<GroupRef> groups)
    {
        string p = BashRuntime.TranslatePosixClasses(pattern);
        var sb = new StringBuilder(p.Length);
        groups = new List<GroupRef>();
        int unnamed = 0;
        bool inClass = false;
        for (int i = 0; i < p.Length; i++)
        {
            char c = p[i];
            if (c == '\\' && i + 1 < p.Length)
            {
                char n = p[i + 1];
                if (!inClass && n == 'h') { sb.Append("[0-9a-fA-F]"); i++; continue; }
                if (!inClass && n == 'H') { sb.Append("[^0-9a-fA-F]"); i++; continue; }
                sb.Append(c).Append(n);
                i++;
                continue;
            }
            if (inClass)
            {
                if (c == ']') inClass = false;
                sb.Append(c);
                continue;
            }
            if (c == '[')
            {
                inClass = true;
                sb.Append(c);
                // a leading ^ and a literal ] right after it belong to the class
                if (i + 1 < p.Length && p[i + 1] == '^') { sb.Append('^'); i++; }
                if (i + 1 < p.Length && p[i + 1] == ']') { sb.Append("\\]"); i++; }
                continue;
            }
            if (c == '.')
            {
                // Oniguruma's dot is one code point; .NET's is one UTF-16 unit, so a surrogate pair gets its own branch first.
                sb.Append(@"(?:[\uD800-\uDBFF][\uDC00-\uDFFF]|.)");
                continue;
            }
            if (c == '(')
            {
                if (i + 1 < p.Length && p[i + 1] == '?')
                {
                    // (?P<name>...) -> (?<name>...)
                    if (i + 3 < p.Length && p[i + 2] == 'P' && p[i + 3] == '<')
                    {
                        int end = p.IndexOf('>', i + 4);
                        if (end > 0) { string name = p.Substring(i + 4, end - i - 4); groups.Add(new GroupRef(0, name)); sb.Append("(?<").Append(name).Append('>'); i = end; continue; }
                    }
                    // (?<name>...) / (?'name'...) are captures; (?<= (?<! (?: (?= (?! (?> (?i) are not
                    if (i + 2 < p.Length && (p[i + 2] == '<' || p[i + 2] == '\'') && i + 3 < p.Length && p[i + 3] != '=' && p[i + 3] != '!')
                    {
                        char close = p[i + 2] == '<' ? '>' : '\'';
                        int end = p.IndexOf(close, i + 3);
                        if (end > 0) groups.Add(new GroupRef(0, p.Substring(i + 3, end - i - 3)));
                    }
                }
                else
                {
                    groups.Add(new GroupRef(++unnamed, null));
                }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static int CodePointIndex(string s, int utf16Index)
    {
        int count = 0;
        for (int i = 0; i < utf16Index && i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
            count++;
        }
        return count;
    }

    private static int CodePointCount(string s) => CodePointIndex(s, s.Length);

    public bool IsMatch(string input) => _regex.IsMatch(input);

    public IEnumerable<Match> RawMatches(string input)
    {
        var m = _regex.Match(input);
        while (m.Success)
        {
            if (!(_skipEmpty && m.Length == 0)) yield return m;
            if (!Global) yield break;
            m = m.NextMatch();
        }
    }

    /// <summary>The jq match objects of <paramref name="input"/> (all of them with <c>g</c>, else at most one).</summary>
    public IEnumerable<OrderedDictionary> MatchObjects(string input)
    {
        foreach (var m in RawMatches(input)) yield return ToObject(input, m);
    }

    public OrderedDictionary ToObject(string input, Match m)
    {
        var obj = new OrderedDictionary
        {
            ["offset"] = Num(CodePointIndex(input, m.Index)),
            ["length"] = Num(CodePointCount(m.Value)),
            ["string"] = m.Value,
        };
        if (m.Length == 0)
        {
            // jq 1.7: a zero-width match carries no captures.
            obj["captures"] = Array.Empty<object?>();
            return obj;
        }
        var captures = new List<object?>();
        foreach (var g in _groups)
        {
            Group grp = g.Name != null ? m.Groups[g.Name] : m.Groups[g.Number];
            var cap = new OrderedDictionary();
            if (!grp.Success)
            {
                cap["offset"] = Num(-1);
                cap["string"] = null;
                cap["length"] = Num(0);
            }
            else if (grp.Length == 0)
            {
                cap["offset"] = Num(CodePointIndex(input, grp.Index));
                cap["string"] = "";
                cap["length"] = Num(0);
            }
            else
            {
                cap["offset"] = Num(CodePointIndex(input, grp.Index));
                cap["length"] = Num(CodePointCount(grp.Value));
                cap["string"] = grp.Value;
            }
            cap["name"] = g.Name;
            captures.Add(cap);
        }
        obj["captures"] = captures.ToArray();
        return obj;
    }

    private static object Num(int i) => new JqNumber(i, i.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The object <c>capture</c> builds: named captures only, name → string (null when the group did not take part).</summary>
    public OrderedDictionary CaptureObject(OrderedDictionary matchObject)
    {
        var result = new OrderedDictionary();
        foreach (var c in (object?[])matchObject["captures"]!)
        {
            var cap = (OrderedDictionary)c!;
            if (cap["name"] is string name) result[name] = cap["string"];
        }
        return result;
    }
}
