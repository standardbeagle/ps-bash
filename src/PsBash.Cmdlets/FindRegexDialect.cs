using System.Text;
using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

/// <summary>
/// Translation of the GNU regex syntaxes <c>find -regextype</c> selects into .NET regular expressions
/// (the match is always against the WHOLE printed path, so the caller anchors it).
/// <list type="bullet">
/// <item><b>emacs</b> (the default, <c>findutils-default</c>): <c>\( \) \|</c> group/alternate, <c>* + ?</c> are operators
/// (a following <c>?</c> makes them non-greedy), <c>\+ \? \{ \}</c> are literals (no intervals), no <c>[[:class:]]</c>
/// (the brackets are an ordinary set, as GNU's RE_SYNTAX_EMACS has no RE_CHAR_CLASSES), <c>\w \W \s \S \b \B \&lt; \&gt; \` \'</c>
/// and back-references <c>\1</c>..<c>\9</c>.</item>
/// <item><b>posix-basic</b> (<c>ed</c>, <c>sed</c>, <c>grep</c>, <c>posix-minimal-basic</c>): <c>\( \) \| \+ \? \{m,n\}</c> are the
/// operators, a bare <c>+ ? | ( ) { }</c> is a literal, <c>*</c> is literal at the start of an expression, classes allowed.</item>
/// <item><b>posix-extended</b> (<c>egrep</c>, <c>posix-egrep</c>, <c>awk</c>, <c>gnu-awk</c>, <c>posix-awk</c>): <c>( ) | + ? * {m,n}</c>
/// are the operators, a backslash makes any other character literal. The awk variants share this mapping (their
/// extra escape sequences are not translated: documented difference).</item>
/// </list>
/// An invalid pattern yields GNU's message text (<c>Unmatched ( or \(</c>, ...).
/// </summary>
internal static class FindRegexDialect
{
    internal enum Dialect { Emacs, Basic, Extended }

    /// <summary>GNU's list of valid names, in the order its error message prints them.</summary>
    internal static readonly string[] TypeNames =
    {
        "findutils-default", "ed", "emacs", "gnu-awk", "grep", "posix-awk", "awk", "posix-basic", "posix-egrep",
        "egrep", "posix-extended", "posix-minimal-basic", "sed",
    };

    internal static string UnknownTypeMessage(string name) =>
        $"Unknown regular expression type ‘{name}’; valid types are " + string.Join(", ", TypeNames.Select(n => $"‘{n}’")) + ".";

    internal static bool TryDialect(string name, out Dialect dialect)
    {
        switch (name)
        {
            case "findutils-default": case "emacs": dialect = Dialect.Emacs; return true;
            case "ed": case "grep": case "posix-basic": case "posix-minimal-basic": case "sed": dialect = Dialect.Basic; return true;
            case "gnu-awk": case "posix-awk": case "awk": case "posix-egrep": case "egrep": case "posix-extended":
                dialect = Dialect.Extended; return true;
            default: dialect = Dialect.Emacs; return false;
        }
    }

    /// <summary>Compile for a whole-path match; <paramref name="error"/> is GNU's wording when the pattern is invalid.</summary>
    internal static bool TryCompile(string pattern, Dialect dialect, bool ignoreCase, out Regex? regex, out string? error)
    {
        regex = null;
        if (!TryTranslate(pattern, dialect, out var net, out error)) return false;
        try
        {
            var opts = RegexOptions.Singleline | RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            regex = new Regex("^(?:" + net + ")$", opts);
            return true;
        }
        catch (ArgumentException)
        {
            error = "Invalid regular expression";
            return false;
        }
    }

    internal static bool TryTranslate(string p, Dialect d, out string net, out string? error)
    {
        var box = new string?[1];
        bool ok = TranslateCore(p, d, out net, box);
        error = box[0];
        return ok;
    }

    private static bool TranslateCore(string p, Dialect d, out string net, string?[] errBox)
    {
        var sb = new StringBuilder();
        net = "";
        int depth = 0;
        bool atStart = true;          // start of the RE / just after a group open or alternation
        bool lastWasQuantifiable = false;
        bool lastWasQuantifier = false;
        int i = 0;
        bool ext = d == Dialect.Extended, emacs = d == Dialect.Emacs;

        bool Fail(string msg) { errBox[0] = msg; return false; }
        void Atom(string s) { sb.Append(s); lastWasQuantifiable = true; lastWasQuantifier = false; atStart = false; }

        bool Quantifier(char q)
        {
            if (atStart || !lastWasQuantifiable && !lastWasQuantifier)
            {
                if (ext) return Fail("Invalid preceding regular expression");
                Atom(Regex.Escape(q.ToString()));       // basic/emacs: literal at the start
                return true;
            }
            if (lastWasQuantifier)
            {
                // emacs: a quantifier followed by ? is non-greedy; elsewhere a stacked quantifier is just applied again
                if (emacs && q == '?') { sb.Append('?'); lastWasQuantifier = true; return true; }
                // a stacked quantifier (a+*) is redundant for whole-path matching purposes: keep the first one
                return true;
            }
            sb.Append(q);
            lastWasQuantifier = true;
            return true;
        }

        while (i < p.Length)
        {
            char c = p[i];
            if (c == '\\')
            {
                if (i + 1 >= p.Length) return Fail("Trailing backslash");
                char n = p[i + 1];
                i += 2;
                if (!ext && n == '(' || false) { depth++; sb.Append('('); atStart = true; lastWasQuantifiable = false; lastWasQuantifier = false; continue; }
                if (!ext && n == ')')
                {
                    if (depth == 0) return Fail("Unmatched ) or \\)");
                    depth--; sb.Append(')'); lastWasQuantifiable = true; lastWasQuantifier = false; atStart = false; continue;
                }
                if (!ext && n == '|') { sb.Append('|'); atStart = true; lastWasQuantifiable = false; lastWasQuantifier = false; continue; }
                if (d == Dialect.Basic && (n == '+' || n == '?')) { if (!Quantifier(n)) return false; continue; }
                if (d == Dialect.Basic && n == '{')
                {
                    int close = p.IndexOf("\\}", i, StringComparison.Ordinal);
                    if (close < 0) return Fail("Unmatched \\{");
                    if (!TryInterval(p.Substring(i, close - i), out var iv)) return Fail("Invalid content of \\{\\}");
                    if (atStart) return Fail("Invalid preceding regular expression");
                    sb.Append(iv); lastWasQuantifier = true; i = close + 2; continue;
                }
                switch (n)
                {
                    case 'w': Atom(@"\w"); continue;
                    case 'W': Atom(@"\W"); continue;
                    case 's' when !emacs: Atom(@"\s"); continue;
                    case 'S' when !emacs: Atom(@"\S"); continue;
                    case 'b': sb.Append(@"\b"); lastWasQuantifiable = false; lastWasQuantifier = false; continue;
                    case 'B': sb.Append(@"\B"); lastWasQuantifiable = false; lastWasQuantifier = false; continue;
                    case '<': sb.Append(@"\b(?=\w)"); lastWasQuantifiable = false; lastWasQuantifier = false; continue;
                    case '>': sb.Append(@"\b(?<=\w)"); lastWasQuantifiable = false; lastWasQuantifier = false; continue;
                    case '`': sb.Append(@"\A"); lastWasQuantifiable = false; continue;
                    case '\'': sb.Append(@"\z"); lastWasQuantifiable = false; continue;
                    case >= '1' and <= '9': Atom("\\" + n); continue;
                }
                Atom(Regex.Escape(n.ToString()));
                continue;
            }

            i++;
            switch (c)
            {
                case '(' when ext: depth++; sb.Append('('); atStart = true; lastWasQuantifiable = false; lastWasQuantifier = false; continue;
                case ')' when ext:
                    if (depth == 0) return Fail("Unmatched ) or \\)");
                    depth--; sb.Append(')'); lastWasQuantifiable = true; lastWasQuantifier = false; atStart = false; continue;
                case '|' when ext: sb.Append('|'); atStart = true; lastWasQuantifiable = false; lastWasQuantifier = false; continue;
                case '*': if (!Quantifier('*')) return false; continue;
                case '+' when ext || emacs: if (!Quantifier('+')) return false; continue;
                case '?' when ext || emacs: if (!Quantifier('?')) return false; continue;
                case '{' when ext:
                {
                    int close = p.IndexOf('}', i);
                    if (close > 0 && TryInterval(p.Substring(i, close - i), out var iv) && !atStart)
                    { sb.Append(iv); lastWasQuantifier = true; i = close + 1; continue; }
                    Atom(@"\{");
                    continue;
                }
                case '.': Atom("."); continue;
                case '[':
                {
                    if (!TryBracket(p, ref i, d, out var set, out var berr)) return Fail(berr!);
                    Atom(set);
                    continue;
                }
                case '^':
                    if (ext || atStart) { sb.Append('^'); lastWasQuantifiable = false; lastWasQuantifier = false; }
                    else Atom(@"\^");
                    continue;
                case '$':
                {
                    bool anchor = ext || i >= p.Length || (!ext && (StartsAt(p, i, "\\)") || StartsAt(p, i, "\\|")));
                    if (anchor) { sb.Append('$'); lastWasQuantifiable = false; lastWasQuantifier = false; atStart = false; }
                    else Atom(@"\$");
                    continue;
                }
                default:
                    Atom(Regex.Escape(c.ToString()));
                    continue;
            }
        }
        if (depth > 0) return Fail("Unmatched ( or \\(");
        net = sb.ToString();
        return true;
    }

    private static bool StartsAt(string s, int i, string t) => string.CompareOrdinal(s, i, t, 0, t.Length) == 0;

    private static bool TryInterval(string body, out string net)
    {
        net = "";
        var m = Regex.Match(body, @"^(\d*)(,(\d*))?$");
        if (!m.Success || (m.Groups[1].Length == 0 && !(m.Groups[2].Success))) return false;
        string lo = m.Groups[1].Length == 0 ? "0" : m.Groups[1].Value;
        if (!m.Groups[2].Success) { net = "{" + lo + "}"; return true; }
        net = "{" + lo + "," + m.Groups[3].Value + "}";
        return true;
    }

    private static bool TryBracket(string p, ref int i, Dialect d, out string net, out string? error)
    {
        net = ""; error = null;
        int j = i;
        bool neg = false;
        if (j < p.Length && p[j] == '^') { neg = true; j++; }
        var body = new StringBuilder();
        bool first = true;
        while (true)
        {
            if (j >= p.Length) { error = "Unmatched [, [^, [:, [., or [="; return false; }
            char c = p[j];
            if (c == ']' && !first) { j++; break; }
            first = false;
            if (c == '[' && j + 1 < p.Length && d != Dialect.Emacs && (p[j + 1] == ':' || p[j + 1] == '=' || p[j + 1] == '.'))
            {
                char kind = p[j + 1];
                int end = p.IndexOf(kind + "]", j + 2, StringComparison.Ordinal);
                if (end < 0) { error = "Unmatched [, [^, [:, [., or [="; return false; }
                string name = p.Substring(j + 2, end - j - 2);
                if (kind == ':')
                {
                    var cls = ClassBody(name);
                    if (cls is null) { error = "Invalid character class name"; return false; }
                    body.Append(cls);
                }
                else body.Append(EscSet(name));
                j = end + 2;
                continue;
            }
            // range a-z (a '-' that is first/last is a literal)
            if (j + 2 < p.Length && p[j + 1] == '-' && p[j + 2] != ']')
            {
                char hi = p[j + 2];
                if (hi < c) { error = "Invalid range end"; return false; }
                body.Append(EscSet(c.ToString())).Append('-').Append(EscSet(hi.ToString()));
                j += 3;
                continue;
            }
            body.Append(EscSet(c.ToString()));
            j++;
        }
        i = j;
        net = "[" + (neg ? "^" : "") + body + "]";
        return true;
    }

    private static string EscSet(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            if (ch is '\\' or ']' or '[' or '^' or '-') sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string? ClassBody(string name) => name switch
    {
        "alpha" => "a-zA-Z",
        "digit" => "0-9",
        "alnum" => "a-zA-Z0-9",
        "upper" => "A-Z",
        "lower" => "a-z",
        "space" => " \\t\\n\\r\\f\\v",
        "blank" => " \\t",
        "punct" => "!-/:-@\\[-`{-~",
        "print" => " -~",
        "graph" => "!-~",
        "cntrl" => "\\x00-\\x1f\\x7f",
        "xdigit" => "0-9A-Fa-f",
        _ => null,
    };
}
