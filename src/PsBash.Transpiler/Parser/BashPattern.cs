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
    /// <summary>No extglob operators (GNU <c>fnmatch</c> without FNM_EXTMATCH: <c>ls -I</c>).</summary>
    NoExtGlob = 8,
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
    /// escape itself, and the extglob list syntax (an escaped <c>(</c> cannot open <c>@(</c>, an escaped <c>|</c> or
    /// <c>)</c> cannot end an arm).</summary>
    private const string Specials = "*?[]\\()|";

    /// <summary>
    /// <paramref name="literal"/> as canonical pattern text that matches exactly itself (a quoted part of a pattern
    /// word, a quoted expansion's value): each of <c>* ? [ ] \ ( ) |</c> gets a backslash.
    /// </summary>
    public static string EscapeLiteral(string? literal)
    {
        if (string.IsNullOrEmpty(literal)) return literal ?? string.Empty;
        if (literal.AsSpan().IndexOfAny(Specials) < 0) return literal;
        var sb = new StringBuilder(literal.Length + 8);
        foreach (char c in literal)
        {
            if (Specials.IndexOf(c) >= 0) sb.Append('\\');
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
    // long-lived and a run-time pattern can differ every iteration, so the cache is bounded.
    private static readonly ConcurrentDictionary<(string, BashPatternOptions), BashPatternProgram> Programs = new();
    private const int MaxCached = 512;

    /// <summary>The compiled program for the canonical <paramref name="pattern"/> (cached).</summary>
    public static BashPatternProgram Compile(string pattern, BashPatternOptions options = BashPatternOptions.None)
    {
        if (Programs.TryGetValue((pattern, options), out var hit)) return hit;
        if (Programs.Count > MaxCached) Programs.Clear();
        var program = new BashPatternProgram(pattern, options);
        Programs[(pattern, options)] = program;
        return program;
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
        private int _groups;
        private readonly bool _pathname = (options & BashPatternOptions.Pathname) != 0;
        private readonly bool _ascii = (options & BashPatternOptions.Bytes) != 0;
        private readonly bool _extglobOn = (options & BashPatternOptions.NoExtGlob) == 0;

        /// <summary>True once an extglob operator was compiled (the program then tries candidate lengths).</summary>
        public bool UsedExtGlob { get; private set; }

        /// <summary>True when the pattern is plain text: no operator, bracket or <c>*</c>/<c>?</c> took effect.</summary>
        public bool IsLiteral { get; private set; } = true;

        public string Whole()
        {
            int pos = 0;
            return Sequence(ref pos, inList: false);
        }

        // A pattern sequence up to end of input, or — inside an extglob list — up to the `|` or
        // `)` that ends this item (left unconsumed for the caller).
        private string Sequence(ref int pos, bool inList)
        {
            var sb = new StringBuilder(raw.Length * 2);
            while (pos < raw.Length)
            {
                char c = raw[pos];
                if (inList && c is '|' or ')') break;
                if (_extglobOn && c is '?' or '*' or '+' or '@' or '!' && pos + 1 < raw.Length && raw[pos + 1] == '('
                    && TryExtGlob(ref pos, sb))
                    continue;
                if (c == '*')
                {
                    while (pos + 1 < raw.Length && raw[pos + 1] == '*'
                           && !(_extglobOn && pos + 2 < raw.Length && raw[pos + 2] == '(')) pos++;   // `**` is `*`
                    IsLiteral = false;
                    sb.Append(_pathname && pos + 1 < raw.Length ? "[^/]*" : lazy ? ".*?" : ".*");
                    pos++;
                    continue;
                }
                if (c == '?') { IsLiteral = false; sb.Append(_pathname ? "[^/]" : "."); pos++; continue; }
                if (c == '[' && TryBracket(ref pos, sb)) { IsLiteral = false; continue; }
                if (c == '\\' && pos + 1 < raw.Length) c = raw[++pos];
                AppendLiteral(sb, c);
                pos++;
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

        // `op( item | item … )` at pos (pos on the operator). False — nothing consumed — when the
        // list has no closing paren; the operator is then an ordinary glob char.
        private bool TryExtGlob(ref int pos, StringBuilder into)
        {
            // Sequence ↔ TryExtGlob recurse once per nested group: bounded like every other descent
            // (a pattern with 20,000 nested groups overflowed the stack and killed the host).
            using var nesting = NestingGuard.Enter();
            char op = raw[pos];
            int p = pos + 2;
            var list = new StringBuilder();
            bool literal = IsLiteral;
            while (true)
            {
                list.Append(Sequence(ref p, inList: true));                  // an item may be empty: `@(|a)`
                if (p >= raw.Length) { IsLiteral = literal; return false; }   // unterminated list
                if (raw[p] == ')') { p++; break; }
                list.Append('|');
                p++;                                                         // the `|`
            }
            UsedExtGlob = true;
            IsLiteral = false;
            string l = list.ToString();
            switch (op)
            {
                case '?': into.Append("(?:").Append(l).Append(")?"); break;
                case '*': into.Append("(?:").Append(l).Append(")*"); break;
                case '+': into.Append("(?:").Append(l).Append(")+"); break;
                case '@': into.Append("(?:").Append(l).Append(')'); break;
                default: AppendNegation(into, l); break;
            }
            pos = p;
            return true;
        }

        // !(list): capture the segment x and the tail t after it; the lookbehind walks back over x
        // (right to left) to x's start, where the lookahead asks whether the list matches EXACTLY x
        // (list, then precisely the captured tail, then end). If it can, this split is rejected.
        private void AppendNegation(StringBuilder into, string list)
        {
            int n = _groups++;
            into.Append("(?<psbx").Append(n).Append(">.*)(?=(?<psbt").Append(n).Append(">.*))(?<!(?=(?:")
                .Append(list).Append(")\\k<psbt").Append(n).Append(">\\z)\\k<psbx").Append(n).Append(">)");
        }

        // [ … ] at pos. False (nothing consumed) when unclosed: the `[` is then a literal.
        private bool TryBracket(ref int pos, StringBuilder into)
        {
            int p = pos + 1;
            bool negated = false;
            if (p < raw.Length && raw[p] is '!' or '^') { negated = true; p++; }
            var members = new StringBuilder();
            StringBuilder? classes = null;
            bool first = true;
            while (p < raw.Length)
            {
                char c = raw[p];
                if (c == ']' && !first)
                {
                    pos = p + 1;
                    EmitBracket(into, negated, members.ToString(), classes?.ToString());
                    return true;
                }
                first = false;
                if (c == '[' && p + 1 < raw.Length && raw[p + 1] == ':'
                    && raw.IndexOf(":]", p + 2, StringComparison.Ordinal) is var close and > 0)
                {
                    // An unknown name matches nothing; the other members still count (`[[:foo:]x]` matches x).
                    if (PosixClassBody(raw[(p + 2)..close], _ascii) is { } body) (classes ??= new StringBuilder()).Append(body);
                    p = close + 2;
                    continue;
                }
                if (!TryElement(ref p, out char lo)) continue;
                // A range: `-` not last. Its end may be escaped or a collating symbol; a reversed range is empty.
                if (p + 1 < raw.Length && raw[p] == '-' && raw[p + 1] != ']')
                {
                    int save = p;
                    p++;
                    if (TryElement(ref p, out char hi))
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
        private bool TryElement(ref int p, out char ch)
        {
            char c = raw[p];
            if (c == '\\' && p + 1 < raw.Length) { ch = raw[p + 1]; p += 2; return true; }
            if (c == '[' && p + 1 < raw.Length && raw[p + 1] is '=' or '.')
            {
                char kind = raw[p + 1];
                int close = raw.IndexOf(kind == '=' ? "=]" : ".]", p + 2, StringComparison.Ordinal);
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

        private void EmitBracket(StringBuilder into, bool negated, string members, string? classes)
        {
            // FNM_PATHNAME: a bracket never matches `/`.
            if (_pathname) into.Append("(?!/)");
            if (classes is null)
            {
                if (members.Length == 0) into.Append(negated ? "(?s:.)" : "(?!)");
                else into.Append(negated ? "[^" : "[").Append(members).Append(']');
                return;
            }
            // A class tests the character as written, members and ranges fold under nocase: (?-i:) keeps
            // the class out of the folding.
            if (negated)
            {
                into.Append("(?!(?-i:[").Append(classes).Append("]))");
                if (members.Length == 0) into.Append("(?s:.)");
                else into.Append("[^").Append(members).Append(']');
                return;
            }
            if (members.Length == 0) into.Append("(?-i:[").Append(classes).Append("])");
            else into.Append("(?:(?-i:[").Append(classes).Append("])|[").Append(members).Append("])");
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
    private Regex? _full, _prefixLong, _prefixShort, _suffixLatest, _suffixEarliest, _search;

    internal BashPatternProgram(string pattern, BashPatternOptions options)
    {
        _pattern = pattern;
        _options = options;
        var conv = new BashPattern.Converter(pattern, lazy: false, options);
        _body = conv.Whole();
        _extGlob = conv.UsedExtGlob;
        _cmp = (options & BashPatternOptions.NoCase) != 0 ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (conv.IsLiteral) _literal = Unescape(pattern);
    }

    /// <summary>The canonical pattern text this program was compiled from.</summary>
    public string Pattern => _pattern;

    /// <summary>The anchored .NET regex this program matches whole strings with (diagnostics and tests).</summary>
    public string AnchoredRegex => "^(?:" + _body + ")\\z";

    private Regex Build(string text) => new(text,
        RegexOptions.Singleline | RegexOptions.CultureInvariant
        | ((_options & BashPatternOptions.NoCase) != 0 ? RegexOptions.IgnoreCase : RegexOptions.None));

    private Regex Full => _full ??= Build("^(?:" + _body + ")\\z");
    private Regex PrefixLong => _prefixLong ??= Build("^(?:" + _body + ")");
    private Regex PrefixShort => _prefixShort ??= Build("^(?:" + new BashPattern.Converter(_pattern, lazy: true, _options).Whole() + ")");
    private Regex SuffixLatest => _suffixLatest ??= Build("^.*(?<psbm>(?:" + _body + "))\\z");
    private Regex SuffixEarliest => _suffixEarliest ??= Build("(?:" + _body + ")\\z");
    private Regex Search => _search ??= Build("(?:" + _body + ")");

    /// <summary>Does the whole of <paramref name="text"/> match?</summary>
    public bool IsMatch(ReadOnlySpan<char> text)
        => _literal is not null ? text.Equals(_literal, _cmp) : Full.IsMatch(text);

    /// <summary>The length of the shortest (<c>#</c>) or longest (<c>##</c>) prefix of <paramref name="s"/> the
    /// pattern matches whole; -1 when none does.</summary>
    public int MatchPrefix(string s, bool longest)
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

    /// <summary>The start of the shortest (<c>%</c>) or longest (<c>%%</c>) suffix of <paramref name="s"/> the
    /// pattern matches whole; -1 when none does.</summary>
    public int MatchSuffix(string s, bool longest)
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

    /// <summary>The match <c>${v/p/r}</c> replaces, searching from <paramref name="from"/>: the leftmost start at which
    /// the pattern matches some substring, and the longest substring there.</summary>
    public bool Find(string s, int from, out int start, out int end)
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
