using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace PsBash.Core.Parser;

/// <summary>How a <see cref="BashPattern"/> program matches: the context's options, not the pattern's text.</summary>
[Flags]
public enum BashPatternOptions
{
    /// <summary>Case-sensitive, characters, <c>/</c> an ordinary character, extglob recognized.</summary>
    None = 0,
    /// <summary><c>nocasematch</c> / <c>nocaseglob</c>: characters, members and ranges fold; a POSIX class tests the
    /// character as written (bash 5.2: <c>[[:lower:]]b</c> does not match <c>Ab</c> under nocaseglob).</summary>
    NoCase = 1,
    /// <summary>FNM_PATHNAME (a <c>GLOBIGNORE</c> pattern): <c>?</c>, a bracket and a <c>*</c> do not match <c>/</c>,
    /// except a <c>*</c> that ends the pattern.</summary>
    Pathname = 2,
    /// <summary>The C / POSIX locale: the subject is bytes (one char per byte) and the POSIX classes are ASCII.</summary>
    Bytes = 4,
    /// <summary>No extglob operators (GNU <c>fnmatch</c> without FNM_EXTMATCH: <c>ls -I</c>, and a pattern context
    /// whose run-time value is matched while <c>shopt extglob</c> is off).</summary>
    NoExtGlob = 8,
    /// <summary>FNM_PERIOD for a subject that STARTS WITH A PERIOD (a dot-file name; set it only for such a subject):
    /// the period must be matched by a literal period. A <c>*</c>, <c>?</c>, bracket or <c>!(…)</c> at the start of the
    /// pattern fails; an extglob at the start holds each arm's first character to the same rule, and when it matches
    /// nothing, what follows it too (bash's <c>sm_loop</c> / <c>extmatch</c>).</summary>
    LeadingPeriod = 16,
}

/// <summary>
/// THE bash pattern compiler (bash 5.2 manual §3.5.8.1 "Pattern Matching"), the one used by every context: pathname
/// components, <c>case</c>, <c>[[ == != ]]</c>, <c>${v#p} ${v##p} ${v%p} ${v%%p}</c>, <c>${v/p/r}</c> and its
/// <c>// /# /%</c> forms, <c>${v^p} ${v^^p} ${v,p} ${v,,p}</c>, <c>GLOBIGNORE</c>, <c>compgen -X</c> and <c>ls -I</c>.
/// No other code turns a glob into a regex (guard <c>PatternCompilerGuardTests</c>).
/// <para>The input is the pattern in its CANONICAL form: quote removal done, every character that was quoted or
/// escaped written as <c>\c</c> (<see cref="EscapeLiteral"/>), so <c>\c</c> is always the literal c and every other
/// character keeps its pattern meaning. The emitter builds that text from a word (quoted parts escaped, an unquoted
/// expansion's value spliced in active), the same rule for every context.</para>
/// <list type="bullet">
/// <item><c>*</c> any string, <c>?</c> any character (a newline too), <c>\c</c> the literal c.</item>
/// <item><c>[…]</c>: <c>!</c> or <c>^</c> first negates; a <c>]</c> first (after the negation) is a member; ranges
/// <c>a-z</c> (a reversed one is empty, never an error); <c>[:class:]</c> by the locale (Unicode categories, ASCII in
/// the C locale; an unknown name matches nothing); <c>[=c=]</c> and <c>[.c.]</c> / <c>[.name.]</c> (the POSIX names)
/// are the character c, as in the C.UTF-8 locale. An unclosed <c>[</c> is a literal.</item>
/// <item>extglob <c>?(L) *(L) +(L) @(L) !(L)</c> over a <c>|</c>-separated list, nesting allowed. <c>!(L)</c> is exact:
/// the segment is captured and a lookahead nested in a lookbehind checks that L cannot match exactly that text.</item>
/// </list>
/// <para>Matching (<see cref="BashPatternProgram"/>) is always a WHOLE match of a candidate string. The removal and
/// substitution operators choose candidates exactly as bash does (shortest/longest prefix or suffix; the leftmost
/// start, then the longest match there). A plain glob gets the answer from one regex search (its greedy match is the
/// longest, its lazy one the shortest, the leftmost start comes first); an extglob, whose alternation regex would take
/// leftmost-first, tries the candidate lengths.</para>
/// </summary>
public static class BashPattern
{
    /// <summary>True when <paramref name="raw"/> holds an UNQUOTED extglob operator: one of
    /// <c>? * + @ !</c> followed by <c>(</c>.</summary>
    public static bool HasExtGlob(string raw, bool honorQuotes = true)
    {
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '\\') { i++; continue; }
            if (honorQuotes && c is '\'' or '"')
            {
                int close = raw.IndexOf(c, i + 1);
                if (close < 0) return false;
                i = close;
                continue;
            }
            if (c is '?' or '*' or '+' or '@' or '!' && i + 1 < raw.Length && raw[i + 1] == '(')
                return true;
        }
        return false;
    }

    /// <summary>The characters a literal must escape to stay inert in a canonical pattern: the glob characters, the
    /// escape itself, the extglob list syntax (an escaped <c>(</c> cannot open <c>@(</c>, an escaped <c>|</c> or
    /// <c>)</c> cannot end an arm) and the bracket syntax (a quoted <c>!</c> / <c>^</c> first in a bracket does not
    /// negate it, a quoted <c>-</c> is no range: <c>["$x"a]</c> with x=! is the set {!, a}).</summary>
    private const string Specials = "*?[]\\()|!^-";

    /// <summary>What unquoted literal text of a pattern word escapes: the extglob list syntax and the escape. Its
    /// <c>[ ] ! ^ -</c> stay active, so a bracket assembled around an expansion (<c>[$c]</c>, <c>["$x"a]</c>) is a
    /// bracket, as in bash (<c>*</c> and <c>?</c> never reach a literal part: the parser makes them glob parts).</summary>
    private const string WordSpecials = "*?\\()|";

    /// <summary>
    /// <paramref name="literal"/> as canonical pattern text that matches exactly itself (a quoted part of a pattern
    /// word, a quoted expansion's value): each of <c>* ? [ ] \ ( ) | ! ^ -</c> gets a backslash.
    /// </summary>
    public static string EscapeLiteral(string? literal) => Escape(literal, Specials);

    /// <summary>Whether a QUOTED <paramref name="c"/> needs a backslash in canonical pattern text (what
    /// <see cref="EscapeLiteral"/> escapes), for a builder that assembles a pattern one tagged character at a time.</summary>
    public static bool QuotedNeedsEscape(char c) => Specials.IndexOf(c) >= 0;

    /// <summary>
    /// UNQUOTED literal text of a pattern word as canonical pattern text: unlike <see cref="EscapeLiteral"/> its
    /// bracket characters stay pattern syntax (<c>[$c]</c> is a bracket expression once c's value is spliced in).
    /// </summary>
    public static string EscapeWordText(string? text) => Escape(text, WordSpecials);

    private static string Escape(string? literal, string specials)
    {
        if (string.IsNullOrEmpty(literal)) return literal ?? string.Empty;
        if (literal.AsSpan().IndexOfAny(specials) < 0) return literal;
        var sb = new StringBuilder(literal.Length + 8);
        foreach (char c in literal)
        {
            if (specials.IndexOf(c) >= 0) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// An unquoted expansion's value as pattern text while <c>shopt extglob</c> is OFF: its extglob list syntax
    /// (an unescaped <c>( ) |</c>) is escaped, so <c>v='+(a)'</c> matches the text +(a) and <c>*(a)</c> is a star
    /// followed by the text (a), as bash's matcher without FNM_EXTMATCH reads them. Its other glob characters stay
    /// active. (With extglob on, and always in <c>[[ ]]</c>, the value is used as it is.)
    /// </summary>
    public static string ValueWithoutExtGlob(string value)
    {
        if (value.AsSpan().IndexOfAny("()|") < 0) return value;
        var sb = new StringBuilder(value.Length + 8);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\\' && i + 1 < value.Length) { sb.Append(c).Append(value[++i]); continue; }
            if (c is '(' or ')' or '|') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// <paramref name="literal"/> as replacement-template text (<see cref="Substitute"/>) that inserts exactly itself:
    /// <c>\</c> and <c>&amp;</c> get a backslash (a quoted <c>&amp;</c> is not the match).
    /// </summary>
    public static string EscapeReplacement(string? literal)
    {
        if (string.IsNullOrEmpty(literal)) return literal ?? string.Empty;
        if (literal.AsSpan().IndexOfAny('\\', '&') < 0) return literal;
        var sb = new StringBuilder(literal.Length + 4);
        foreach (char c in literal)
        {
            if (c is '\\' or '&') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    // One program per (text, options): a script line in a loop evaluates the same pattern every iteration. The host is
    // long-lived and a run-time pattern can differ every iteration, so the cache is bounded by entries AND by the
    // pattern text it holds (a program's regexes grow with its pattern); a pattern too long to be worth keeping is
    // compiled for the one use.
    private static readonly ConcurrentDictionary<(string, BashPatternOptions), BashPatternProgram> Programs = new();
    private const int MaxCached = 512;
    private const int MaxCachedChars = 256 * 1024;
    internal const int MaxCacheablePattern = 4096;
    private static int s_cachedChars;

    /// <summary>The compiled program for the canonical <paramref name="pattern"/> (cached).</summary>
    public static BashPatternProgram Compile(string pattern, BashPatternOptions options = BashPatternOptions.None)
    {
        if (Programs.TryGetValue((pattern, options), out var hit)) return hit;
        var program = new BashPatternProgram(pattern, options);
        if (pattern.Length > MaxCacheablePattern) return program;
        if (Programs.Count >= MaxCached || Volatile.Read(ref s_cachedChars) + pattern.Length > MaxCachedChars)
        {
            Programs.Clear();
            Interlocked.Exchange(ref s_cachedChars, 0);
        }
        if (Programs.TryAdd((pattern, options), program)) Interlocked.Add(ref s_cachedChars, pattern.Length);
        return program;
    }

    /// <summary>The number of cached programs and the pattern characters they hold (tests).</summary>
    internal static (int Count, int Chars) CacheSize => (Programs.Count, Volatile.Read(ref s_cachedChars));

    /// <summary>Where a match that ran out of time is reported (the host sets it to its host log). A match never hangs
    /// the host: a pattern the backtracking engine cannot finish within <see cref="BashPatternProgram.MatchBudget"/>
    /// fails to match and is reported here.</summary>
    public static Action<string>? Diagnostic { get; set; }

    internal static void ReportTimeout(string pattern, int subjectLength)
    {
        try
        {
            Diagnostic?.Invoke($"bash pattern match gave up after {BashPatternProgram.MatchBudget.TotalMilliseconds:0} ms "
                + $"(no match): pattern of {pattern.Length} chars "
                + $"'{(pattern.Length > 80 ? pattern[..80] + "..." : pattern)}' against {subjectLength} chars");
        }
        catch
        {
            // a diagnostic never fails the match
        }
    }

    /// <summary>Which operator <see cref="Substitute"/> applies.</summary>
    public enum SubstituteMode
    {
        /// <summary><c>${v/p/r}</c>: the first match (leftmost start, longest there).</summary>
        First,
        /// <summary><c>${v//p/r}</c>: every match, left to right.</summary>
        All,
        /// <summary><c>${v/#p/r}</c>: the longest match at the start.</summary>
        Start,
        /// <summary><c>${v/%p/r}</c>: the longest match at the end.</summary>
        End,
    }

    /// <summary>
    /// bash's <c>pat_subst</c>. <paramref name="template"/> is the expanded replacement: <c>\\</c> a backslash,
    /// <c>\&amp;</c> an ampersand, a bare <c>&amp;</c> the matched text when <paramref name="matchAmpersand"/>
    /// (<c>patsub_replacement</c>, on by default; off, it is an ampersand), any other backslash itself. An empty
    /// pattern changes nothing, except that <c>/#</c> and <c>/%</c> then insert at the start / end. An empty value
    /// is replaced when the pattern matches the empty string. With <c>//</c> a match is tried at each position up to
    /// the last character, never at the end itself: after an empty match one character is copied
    /// (<c>${v//*([0-9])/X}</c> on 123x is XXx).
    /// </summary>
    public static string Substitute(string value, BashPatternProgram pattern, bool patternEmpty, string template,
        bool matchAmpersand, SubstituteMode mode)
    {
        var rep = new Replacement(template, matchAmpersand);
        if (patternEmpty)
            return mode switch
            {
                SubstituteMode.Start => rep.For("") + value,
                SubstituteMode.End => value + rep.For(""),
                _ => value,
            };
        if (value.Length == 0)
            return pattern.IsMatch(ReadOnlySpan<char>.Empty) ? rep.For("") : value;
        switch (mode)
        {
            case SubstituteMode.Start:
            {
                int n = pattern.MatchPrefix(value, longest: true);
                return n < 0 ? value : rep.For(value[..n]) + value[n..];
            }
            case SubstituteMode.End:
            {
                int st = pattern.MatchSuffix(value, longest: true);
                return st < 0 ? value : value[..st] + rep.For(value[st..]);
            }
        }
        StringBuilder? sb = null;
        int pos = 0;
        while (pos < value.Length && pattern.Find(value, pos, out int start, out int end))
        {
            sb ??= new StringBuilder(value.Length + 16);
            sb.Append(value, pos, start - pos);
            rep.AppendTo(sb, value, start, end - start);
            pos = end;
            if (mode != SubstituteMode.All) break;
            if (start == end && pos < value.Length)
            {
                int w = char.IsHighSurrogate(value[pos]) && pos + 1 < value.Length && char.IsLowSurrogate(value[pos + 1]) ? 2 : 1;
                sb.Append(value, pos, w);
                pos += w;
            }
        }
        if (sb is null) return value;
        sb.Append(value, pos, value.Length - pos);
        return sb.ToString();
    }

    /// <summary>
    /// <c>${v^p}</c> <c>${v^^p}</c> <c>${v,p}</c> <c>${v,,p}</c>: each character (only the first for a single
    /// <c>^</c> / <c>,</c>) that matches the WHOLE <paramref name="pattern"/> is converted; a null pattern is
    /// <c>?</c>. <paramref name="asciiOnly"/>: the C locale changes only ASCII letters.
    /// </summary>
    public static string ModifyCase(string value, BashPatternProgram? pattern, bool upper, bool all, bool asciiOnly)
    {
        if (value.Length == 0) return value;
        if (pattern is null && all && !asciiOnly) return upper ? value.ToUpperInvariant() : value.ToLowerInvariant();
        char[]? buf = null;
        for (int i = 0; i < value.Length;)
        {
            int w = char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]) ? 2 : 1;
            if (w == 1 && (pattern is null || pattern.IsMatch(value.AsSpan(i, 1))))
            {
                char c = value[i];
                char m = asciiOnly
                    ? (upper ? (c is >= 'a' and <= 'z' ? (char)(c - 32) : c) : (c is >= 'A' and <= 'Z' ? (char)(c + 32) : c))
                    : upper ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c);
                if (m != c) { buf ??= value.ToCharArray(); buf[i] = m; }
            }
            i += w;
            if (!all) break;
        }
        return buf is null ? value : new string(buf);
    }

    /// <summary>A parsed replacement template: literal runs and the places the match goes.</summary>
    private readonly struct Replacement
    {
        private readonly string? _constant;        // no `&` to fill in: the whole replacement
        private readonly List<string?>? _pieces;   // literal runs; null = the match

        public Replacement(string template, bool matchAmpersand)
        {
            _constant = null;
            _pieces = null;
            if (template.AsSpan().IndexOfAny('\\', '&') < 0) { _constant = template; return; }
            var lit = new StringBuilder(template.Length);
            List<string?>? pieces = null;
            for (int i = 0; i < template.Length; i++)
            {
                char c = template[i];
                if (c == '\\' && i + 1 < template.Length && template[i + 1] is '\\' or '&') { lit.Append(template[++i]); continue; }
                if (c == '&' && matchAmpersand)
                {
                    pieces ??= new List<string?>();
                    pieces.Add(lit.ToString());
                    pieces.Add(null);
                    lit.Clear();
                    continue;
                }
                lit.Append(c);
            }
            if (pieces is null) { _constant = lit.ToString(); return; }
            pieces.Add(lit.ToString());
            _pieces = pieces;
        }

        public string For(string match)
        {
            if (_constant is not null) return _constant;
            var sb = new StringBuilder();
            AppendTo(sb, match, 0, match.Length);
            return sb.ToString();
        }

        public void AppendTo(StringBuilder sb, string source, int start, int length)
        {
            if (_constant is not null) { sb.Append(_constant); return; }
            foreach (var p in _pieces!)
            {
                if (p is null) sb.Append(source, start, length);
                else sb.Append(p);
            }
        }
    }

    /// <summary>Canonical pattern → .NET regex body. Internal to the compiler: nothing else builds a glob regex.</summary>
    internal sealed class Converter(string raw, bool lazy, BashPatternOptions options)
    {
        /// <summary>A set no character is in: what an element that can never match compiles to.</summary>
        private const string Never = "[^\\s\\S]";
        // How many times a nullable extglob at the start may fork the rest into its two readings (LeadingPeriod only):
        // each fork compiles the rest twice, so a run of them is capped (past it the group keeps one reading).
        private const int MaxStartForks = 8;

        private int _groups;
        private int _startForks;
        private readonly bool _pathname = (options & BashPatternOptions.Pathname) != 0;
        private readonly bool _ascii = (options & BashPatternOptions.Bytes) != 0;
        private readonly bool _extglobOn = (options & BashPatternOptions.NoExtGlob) == 0;
        private readonly bool _noCase = (options & BashPatternOptions.NoCase) != 0;
        private readonly bool _period = (options & BashPatternOptions.LeadingPeriod) != 0;
        private readonly int _lastClose = raw.LastIndexOf(']');

        /// <summary>True once an extglob operator was compiled (the program then tries candidate lengths).</summary>
        public bool UsedExtGlob { get; private set; }

        /// <summary>True when the pattern is plain text: no operator, bracket or <c>*</c>/<c>?</c> took effect.</summary>
        public bool IsLiteral { get; private set; } = true;

        /// <summary>True once a construct only the backtracking engine runs was compiled: <c>!(…)</c> (a lookbehind and
        /// backreferences) or a POSIX class beside folded members under nocase (an inline option group). Everything
        /// else runs on the linear-time NonBacktracking engine.</summary>
        public bool NeedsBacktracking { get; private set; }

        public string Whole()
        {
            int pos = 0;
            return Sequence(ref pos, raw.Length, atStart: _period, out _);
        }

        // The pattern from pos up to end: the whole pattern, or one item of an extglob list (whose bounds Scan found, so
        // a `|` or `)` met here is an ordinary character, as in bash). atStart (LeadingPeriod only): nothing before this
        // point can have consumed a character, so the next element faces the subject's leading period. nullable: the
        // sequence can match "".
        private string Sequence(ref int pos, int end, bool atStart, out bool nullable)
        {
            var sb = new StringBuilder((end - pos) * 2);
            nullable = true;
            while (pos < end)
            {
                char c = raw[pos];
                if (_extglobOn && c is '?' or '*' or '+' or '@' or '!' && pos + 1 < end && raw[pos + 1] == '(')
                {
                    var (group, groupNullable) = ExtGlob(ref pos, end, atStart);
                    if (group is null)
                    {
                        // A list that never closes: bash compares the rest of the pattern, operator included, with the
                        // rest of the subject as plain strings (sm_loop.c EXTMATCH → STRCOMPARE, backslashes and all).
                        // Its first character is the operator, never a period, so FNM_PERIOD holds as well.
                        IsLiteral = false;
                        for (; pos < end; pos++) AppendLiteral(sb, raw[pos]);
                        nullable = false;
                        break;
                    }
                    if (atStart && groupNullable)
                    {
                        if (_startForks < MaxStartForks)
                        {
                            // The group either consumed the period (and the rest is past the start) or matched nothing
                            // (and the rest still faces the period): bash's extmatch passes FNM_PERIOD on in that case.
                            _startForks++;
                            int restPos = pos;
                            string rest = Sequence(ref pos, end, atStart: false, out bool restNullable);
                            string restAtStart = Sequence(ref restPos, end, atStart: true, out _);
                            nullable = restNullable;
                            return sb.Append("(?:").Append(group).Append(rest).Append('|').Append(restAtStart).Append(')').ToString();
                        }
                        sb.Append("(?:").Append(group).Append(")?");
                    }
                    else
                    {
                        sb.Append(group);
                        nullable &= groupNullable;
                    }
                    atStart = false;
                    continue;
                }
                if (c == '*')
                {
                    while (pos + 1 < end && raw[pos + 1] == '*'
                           && !(_extglobOn && pos + 2 < end && raw[pos + 2] == '(')) pos++;   // `**` is `*`
                    IsLiteral = false;
                    sb.Append(atStart ? Never : _pathname && pos + 1 < raw.Length ? "[^/]*" : lazy ? ".*?" : ".*");
                    pos++;
                    atStart = false;
                    continue;
                }
                if (c == '?')
                {
                    IsLiteral = false;
                    sb.Append(atStart ? Never : _pathname ? "[^/]" : ".");
                    pos++;
                    nullable = atStart = false;
                    continue;
                }
                if (c == '[' && pos < _lastClose)
                {
                    var bracket = new StringBuilder();
                    if (TryBracket(ref pos, end, bracket))
                    {
                        IsLiteral = false;
                        sb.Append(atStart ? Never : bracket.ToString());
                        nullable = atStart = false;
                        continue;
                    }
                }
                if (c == '\\' && pos + 1 < end) c = raw[++pos];
                AppendLiteral(sb, c);
                pos++;
                nullable = atStart = false;
            }
            return sb.ToString();
        }

        private static void AppendLiteral(StringBuilder sb, char c)
        {
            if (c is '\\' or '*' or '+' or '?' or '|' or '{' or '}' or '[' or ']' or '(' or ')' or '^' or '$' or '.' or '#' or ' ')
                sb.Append('\\');
            if (c == '\t') { sb.Append("\\t"); return; }
            if (c == '\n') { sb.Append("\\n"); return; }
            sb.Append(c);
        }

        // `op( item | item … )` at pos (pos on the operator), within [pos, end): the group's regex and whether it can
        // match "". A null regex — nothing consumed — when the list has no closing paren (Sequence then compares the
        // rest as a plain string). atStart (LeadingPeriod): the regex is the group's reading that CONSUMES the leading
        // period (each arm's first character held to the rule; a repetition's later rounds are past it); Sequence adds
        // the empty reading.
        private (string? Regex, bool Nullable) ExtGlob(ref int pos, int end, bool atStart)
        {
            int close = Scan(pos + 2, end, bar: false);
            if (close < 0) return (null, false);
            // Sequence ↔ ExtGlob recurse once per nested group: bounded like every other descent
            // (a pattern with 20,000 nested groups overflowed the stack and killed the host).
            using var nesting = NestingGuard.Enter();
            char op = raw[pos];
            string first = List(pos + 2, close, atStart, out bool armNullable);
            UsedExtGlob = true;
            IsLiteral = false;
            string l = atStart && op is '*' or '+' ? List(pos + 2, close, atStart: false, out _) : first;
            pos = close + 1;
            return op switch
            {
                '?' => (atStart ? "(?:" + first + ")" : "(?:" + l + ")?", true),
                '*' => (atStart ? "(?:" + first + ")(?:" + l + ")*" : "(?:" + l + ")*", true),
                '+' => (atStart ? "(?:" + first + ")(?:" + l + ")*" : "(?:" + l + ")+", armNullable),
                '@' => ("(?:" + l + ")", armNullable),
                // bash: a `!(…)` at the start never matches a leading period (no arm matching a split is a failure).
                _ => atStart ? (Never, false) : (Negation(l), true),
            };
        }

        // The items of the list in [p, close), joined with `|` (an item may be empty: `@(|a)`).
        private string List(int p, int close, bool atStart, out bool anyNullable)
        {
            var list = new StringBuilder();
            anyNullable = false;
            while (true)
            {
                int bar = Scan(p, close, bar: true);
                int itemEnd = bar < 0 ? close : bar;
                list.Append(Sequence(ref p, itemEnd, atStart, out bool nullable));
                anyNullable |= nullable;
                if (bar < 0) return list.ToString();
                list.Append('|');
                p = bar + 1;
            }
        }

        // bash's PATSCAN (lib/glob/sm_loop.c) from `from` up to `end`: the index of the `)` that closes the list (any
        // `(` nests, so `@(a(x|y))` is one item `a(x|y)`), or with bar the `|` that ends the item, outside a bracket
        // expression and nested parens; -1 when there is none. An escaped character is skipped, and a `[` opens a
        // bracket that only its `]` closes (an unclosed one takes the rest: `@(a[b)` never closes). One pass per call:
        // an unterminated list used to be found by parsing it, which every enclosing attempt and each later operator
        // did again (2^n for `@(@(@(…`: the host hung on 40 of them).
        private int Scan(int from, int end, bool bar)
        {
            int pnest = 0, bnest = 0, bfirst = -1;
            char cchar = '\0';
            for (int s = from; s < end; s++)
            {
                switch (raw[s])
                {
                    case '\\':
                        s++;
                        break;
                    case '[':
                        if (bnest == 0)
                        {
                            bfirst = s + 1;
                            if (bfirst < end && raw[bfirst] is '!' or '^') bfirst++;
                            bnest++;
                        }
                        else if (s + 1 < end && raw[s + 1] is ':' or '.' or '=') cchar = raw[s + 1];
                        break;
                    case ']':
                        if (bnest > 0)
                        {
                            if (cchar != '\0' && raw[s - 1] == cchar) cchar = '\0';
                            else if (s != bfirst) { bnest--; bfirst = -1; }
                        }
                        break;
                    case '(':
                        if (bnest == 0) pnest++;
                        break;
                    case ')':
                        if (bnest == 0 && pnest-- <= 0) return s;
                        break;
                    case '|':
                        if (bar && bnest == 0 && pnest == 0) return s;
                        break;
                }
            }
            return -1;
        }

        // !(list): capture the segment x and the tail t after it; the lookbehind walks back over x
        // (right to left) to x's start, where the lookahead asks whether the list matches EXACTLY x
        // (list, then precisely the captured tail, then end). If it can, this split is rejected.
        private string Negation(string list)
        {
            NeedsBacktracking = true;
            int n = _groups++;
            return "(?<psbx" + n + ">.*)(?=(?<psbt" + n + ">.*))(?<!(?=(?:" + list + ")\\k<psbt" + n + ">\\z)\\k<psbx" + n + ">)";
        }

        // [ … ] at pos, closed before end. False (nothing consumed) when unclosed: the `[` is then a literal.
        private bool TryBracket(ref int pos, int end, StringBuilder into)
        {
            int p = pos + 1;
            bool negated = false;
            if (p < end && raw[p] is '!' or '^') { negated = true; p++; }
            var members = new StringBuilder();
            StringBuilder? classes = null;
            bool first = true;
            while (p < end)
            {
                char c = raw[p];
                if (c == ']' && !first)
                {
                    pos = p + 1;
                    EmitBracket(into, negated, members.ToString(), classes?.ToString());
                    return true;
                }
                first = false;
                if (c == '[' && p + 1 < end && raw[p + 1] == ':'
                    && raw.IndexOf(":]", p + 2, end - (p + 2), StringComparison.Ordinal) is var close and > 0)
                {
                    // An unknown name matches nothing; the other members still count (`[[:foo:]x]` matches x).
                    if (PosixClassBody(raw[(p + 2)..close], _ascii) is { } body) (classes ??= new StringBuilder()).Append(body);
                    p = close + 2;
                    continue;
                }
                if (!TryElement(ref p, end, out char lo)) continue;
                // A range: `-` not last. Its end may be escaped or a collating symbol; a reversed range is empty.
                if (p + 1 < end && raw[p] == '-' && raw[p + 1] != ']')
                {
                    int save = p;
                    p++;
                    if (TryElement(ref p, end, out char hi))
                    {
                        if (lo <= hi) { AppendClassChar(members, lo); members.Append('-'); AppendClassChar(members, hi); }
                        continue;
                    }
                    p = save;
                }
                AppendClassChar(members, lo);
            }
            return false;
        }

        // One bracket member at p: an escaped character, `[=c=]`, `[.c.]` / `[.name.]`, or a plain character.
        // False when it is a `[.x.]` / `[=x=]` whose content names no character (it matches nothing).
        private bool TryElement(ref int p, int end, out char ch)
        {
            char c = raw[p];
            if (c == '\\' && p + 1 < end) { ch = raw[p + 1]; p += 2; return true; }
            if (c == '[' && p + 1 < end && raw[p + 1] is '=' or '.')
            {
                char kind = raw[p + 1];
                int close = raw.IndexOf(kind == '=' ? "=]" : ".]", p + 2, end - (p + 2), StringComparison.Ordinal);
                if (close > 0)
                {
                    string name = Unescape(raw[(p + 2)..close]);
                    p = close + 2;
                    if (name.Length == 1) { ch = name[0]; return true; }
                    if (kind == '.' && CollatingName(name) is char named) { ch = named; return true; }
                    ch = '\0';
                    return false;
                }
            }
            ch = c;
            p++;
            return true;
        }

        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length) i++;
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        // One character class (no lookaround, so the NonBacktracking engine runs it). FNM_PATHNAME: a bracket never
        // matches `/` (subtracted, or added to a negated set).
        private void EmitBracket(StringBuilder into, bool negated, string members, string? classes)
        {
            if (classes is not null && _noCase)
            {
                // A class tests the character as written while members and ranges fold: (?-i:) keeps the class out
                // of the folding. Only the backtracking engine has inline options and lookarounds.
                NeedsBacktracking = true;
                if (_pathname) into.Append("(?!/)");
                if (negated)
                    into.Append("(?!(?-i:[").Append(classes).Append("]))").Append(members.Length == 0 ? "." : "[^" + members + "]");
                else if (members.Length == 0) into.Append("(?-i:[").Append(classes).Append("])");
                else into.Append("(?:(?-i:[").Append(classes).Append("])|[").Append(members).Append("])");
                return;
            }
            string set = members + classes;
            if (negated)
            {
                if (set.Length == 0 && !_pathname) into.Append('.');
                else into.Append("[^").Append(set).Append(_pathname ? "/" : "").Append(']');
                return;
            }
            if (set.Length == 0) { into.Append(Never); return; }
            into.Append('[').Append(set).Append(_pathname ? "-[/]" : "").Append(']');
        }

        private static void AppendClassChar(StringBuilder sb, char c)
        {
            if (c is '\\' or ']' or '[' or '^' or '-') sb.Append('\\');
            if (c == '\n') { sb.Append("\\n"); return; }
            sb.Append(c);
        }
    }

    /// <summary>The .NET class body of a POSIX bracket class in a REGULAR EXPRESSION (ERE / BRE / tr), ASCII ranges;
    /// null for an unknown name. Shared with the runtime's <c>BashRuntime.TranslatePosixClasses</c>. Glob patterns
    /// classify by the locale instead (<see cref="PosixClassBody"/>).</summary>
    public static string? PosixClass(string name) => name switch
    {
        "alnum" => "a-zA-Z0-9",
        "alpha" => "a-zA-Z",
        "blank" => @" \t",
        "cntrl" => @"\x00-\x1f\x7f",
        "digit" => "0-9",
        "graph" => @"\x21-\x7e",
        "lower" => "a-z",
        "print" => @"\x20-\x7e",
        "punct" => @"\p{P}\p{S}",
        "space" => @"\s",
        "upper" => "A-Z",
        "word" => @"\w",
        "xdigit" => "A-Fa-f0-9",
        _ => null,
    };

    /// <summary>The .NET class body of a POSIX class: Unicode categories as the C.UTF-8 locale classifies, ASCII in the
    /// C locale. Null for an unknown name.</summary>
    private static string? PosixClassBody(string name, bool ascii) => name switch
    {
        "alpha" => ascii ? "a-zA-Z" : @"\p{L}",
        "upper" => ascii ? "A-Z" : @"\p{Lu}",
        "lower" => ascii ? "a-z" : @"\p{Ll}",
        "digit" => "0-9",
        "alnum" => ascii ? "a-zA-Z0-9" : @"\p{L}\p{Nd}",
        "xdigit" => "0-9A-Fa-f",
        "space" => ascii ? @" \t\n\r\f\v" : @"\s",
        "blank" => ascii ? @" \t" : @" \t\p{Zs}",
        "punct" => ascii ? @"!-/:-@\[-`{-~" : @"\p{P}\p{S}",
        "cntrl" => ascii ? @"\x00-\x1f\x7f" : @"\p{Cc}",
        "print" => ascii ? @"\x20-\x7e" : @"\p{L}\p{M}\p{N}\p{P}\p{S}\p{Zs}",
        "graph" => ascii ? @"\x21-\x7e" : @"\p{L}\p{M}\p{N}\p{P}\p{S}",
        "word" => ascii ? "a-zA-Z0-9_" : @"\w",
        _ => null,
    };

    /// <summary>The POSIX portable character names a collating symbol <c>[.name.]</c> may use (bash's collsyms).</summary>
    private static char? CollatingName(string name) => name switch
    {
        "NUL" => '\0', "alert" => '\a', "backspace" => '\b', "tab" => '\t', "newline" => '\n',
        "vertical-tab" => '\v', "form-feed" => '\f', "carriage-return" => '\r', "space" => ' ',
        "exclamation-mark" => '!', "quotation-mark" => '"', "number-sign" => '#', "dollar-sign" => '$',
        "percent-sign" => '%', "ampersand" => '&', "apostrophe" => '\'', "left-parenthesis" => '(',
        "right-parenthesis" => ')', "asterisk" => '*', "plus-sign" => '+', "comma" => ',', "hyphen" => '-',
        "hyphen-minus" => '-', "period" => '.', "full-stop" => '.', "slash" => '/', "solidus" => '/',
        "zero" => '0', "one" => '1', "two" => '2', "three" => '3', "four" => '4', "five" => '5', "six" => '6',
        "seven" => '7', "eight" => '8', "nine" => '9', "colon" => ':', "semicolon" => ';',
        "less-than-sign" => '<', "equals-sign" => '=', "greater-than-sign" => '>', "question-mark" => '?',
        "commercial-at" => '@', "left-square-bracket" => '[', "backslash" => '\\', "reverse-solidus" => '\\',
        "right-square-bracket" => ']', "circumflex" => '^', "circumflex-accent" => '^', "underscore" => '_',
        "low-line" => '_', "grave-accent" => '`', "left-brace" => '{', "left-curly-bracket" => '{',
        "vertical-line" => '|', "right-brace" => '}', "right-curly-bracket" => '}', "tilde" => '~', "DEL" => '\x7f',
        _ => null,
    };
}

/// <summary>
/// One compiled bash pattern (<see cref="BashPattern.Compile"/>): whole-string matching plus the candidate choices of
/// the removal and substitution operators. Allocation-free on the hot path (span matching, cached regexes).
/// </summary>
public sealed class BashPatternProgram
{
    private readonly string _pattern;
    private readonly BashPatternOptions _options;
    private readonly string? _literal;          // the pattern is this plain text: ordinal string operations
    private readonly bool _extGlob;             // alternation / repetition: candidate lengths are tried
    private readonly string _body;
    private readonly StringComparison _cmp;
    private readonly bool _backtracking;        // `!(…)` or a nocase POSIX class: the time-boxed backtracking engine
    private Regex? _full, _prefixLong, _prefixShort, _suffixLatest, _suffixEarliest, _search;

    /// <summary>How long one backtracking match may run before it counts as no match (and is reported through
    /// <see cref="BashPattern.Diagnostic"/>). Only <c>!(…)</c> and nocase POSIX classes use that engine; every other
    /// pattern runs on the NonBacktracking engine, linear in the subject (<c>*(a|aa)</c> against 45 a's and a b, or
    /// <c>*a*a*a*a*a*a*a*a*a*b</c> against 300 a's, which bash itself takes minutes over).</summary>
    public static readonly TimeSpan MatchBudget = TimeSpan.FromSeconds(1);

    internal BashPatternProgram(string pattern, BashPatternOptions options)
    {
        _pattern = pattern;
        _options = options;
        var conv = new BashPattern.Converter(pattern, lazy: false, options);
        _body = conv.Whole();
        _extGlob = conv.UsedExtGlob;
        _backtracking = conv.NeedsBacktracking;
        _cmp = (options & BashPatternOptions.NoCase) != 0 ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (conv.IsLiteral) _literal = Unescape(pattern);
    }

    /// <summary>The canonical pattern text this program was compiled from.</summary>
    public string Pattern => _pattern;

    /// <summary>The anchored .NET regex this program matches whole strings with (diagnostics and tests).</summary>
    public string AnchoredRegex => "^(?:" + _body + ")\\z";

    /// <summary>True when the program runs on the linear-time NonBacktracking engine (tests).</summary>
    internal bool IsLinear => !_backtracking;

    private Regex Build(string text)
    {
        var opts = RegexOptions.Singleline | RegexOptions.CultureInvariant
                   | ((_options & BashPatternOptions.NoCase) != 0 ? RegexOptions.IgnoreCase : RegexOptions.None);
        if (!_backtracking)
        {
            try
            {
                return new Regex(text, opts | RegexOptions.NonBacktracking);
            }
            catch (NotSupportedException)
            {
                // a construct the converter did not flag: the time-boxed engine below
            }
        }
        return new Regex(text, opts, MatchBudget);
    }

    private Regex Full => _full ??= Build("^(?:" + _body + ")\\z");
    private Regex PrefixLong => _prefixLong ??= Build("^(?:" + _body + ")");
    private Regex PrefixShort => _prefixShort ??= Build("^(?:" + new BashPattern.Converter(_pattern, lazy: true, _options).Whole() + ")");
    private Regex SuffixLatest => _suffixLatest ??= Build("^.*(?<psbm>(?:" + _body + "))\\z");
    private Regex SuffixEarliest => _suffixEarliest ??= Build("(?:" + _body + ")\\z");
    private Regex Search => _search ??= Build("(?:" + _body + ")");

    /// <summary>Does the whole of <paramref name="text"/> match?</summary>
    public bool IsMatch(ReadOnlySpan<char> text)
    {
        if (_literal is not null) return text.Equals(_literal, _cmp);
        try
        {
            return Full.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            BashPattern.ReportTimeout(_pattern, text.Length);
            return false;
        }
    }

    /// <summary>The length of the shortest (<c>#</c>) or longest (<c>##</c>) prefix of <paramref name="s"/> the
    /// pattern matches whole; -1 when none does.</summary>
    public int MatchPrefix(string s, bool longest)
    {
        try
        {
            return MatchPrefixCore(s, longest);
        }
        catch (RegexMatchTimeoutException)
        {
            BashPattern.ReportTimeout(_pattern, s.Length);
            return -1;
        }
    }

    /// <summary>The start of the shortest (<c>%</c>) or longest (<c>%%</c>) suffix of <paramref name="s"/> the
    /// pattern matches whole; -1 when none does.</summary>
    public int MatchSuffix(string s, bool longest)
    {
        try
        {
            return MatchSuffixCore(s, longest);
        }
        catch (RegexMatchTimeoutException)
        {
            BashPattern.ReportTimeout(_pattern, s.Length);
            return -1;
        }
    }

    /// <summary>The match <c>${v/p/r}</c> replaces, searching from <paramref name="from"/>: the leftmost start at which
    /// the pattern matches some substring, and the longest substring there.</summary>
    public bool Find(string s, int from, out int start, out int end)
    {
        try
        {
            return FindCore(s, from, out start, out end);
        }
        catch (RegexMatchTimeoutException)
        {
            BashPattern.ReportTimeout(_pattern, s.Length);
            start = end = -1;
            return false;
        }
    }

    private int MatchPrefixCore(string s, bool longest)
    {
        if (_literal is not null) return s.StartsWith(_literal, _cmp) ? _literal.Length : -1;
        if (!_extGlob)
        {
            var m = (longest ? PrefixLong : PrefixShort).Match(s);
            return m.Success ? m.Length : -1;
        }
        if (!PrefixLong.IsMatch(s)) return -1;
        if (longest)
        {
            for (int n = s.Length; n >= 0; n--)
                if (Full.IsMatch(s.AsSpan(0, n))) return n;
        }
        else
        {
            for (int n = 0; n <= s.Length; n++)
                if (Full.IsMatch(s.AsSpan(0, n))) return n;
        }
        return -1;
    }

    private int MatchSuffixCore(string s, bool longest)
    {
        if (_literal is not null) return s.EndsWith(_literal, _cmp) ? s.Length - _literal.Length : -1;
        if (!_extGlob)
        {
            if (longest)
            {
                var m = SuffixEarliest.Match(s);
                return m.Success ? m.Index : -1;
            }
            var g = SuffixLatest.Match(s);
            return g.Success ? g.Groups["psbm"].Index : -1;
        }
        if (!SuffixEarliest.IsMatch(s)) return -1;
        if (longest)
        {
            for (int st = 0; st <= s.Length; st++)
                if (Full.IsMatch(s.AsSpan(st))) return st;
        }
        else
        {
            for (int st = s.Length; st >= 0; st--)
                if (Full.IsMatch(s.AsSpan(st))) return st;
        }
        return -1;
    }

    private bool FindCore(string s, int from, out int start, out int end)
    {
        start = end = -1;
        if (_literal is not null)
        {
            int i = _literal.Length == 0 ? from : s.IndexOf(_literal, from, _cmp);
            if (i < 0) return false;
            start = i;
            end = i + _literal.Length;
            return true;
        }
        var m = Search.Match(s, from);
        if (!m.Success) return false;
        start = m.Index;
        end = m.Index + m.Length;
        if (!_extGlob) return true;
        for (int e = s.Length; e > end; e--)
            if (Full.IsMatch(s.AsSpan(start, e - start))) { end = e; break; }
        return true;
    }

    private static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length) i++;
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
