using System.Text;
using System.Text.RegularExpressions;

namespace PsBash.Core.Parser;

/// <summary>
/// Bash pattern-matching text (a case pattern, the RHS of <c>[[ x == pat ]]</c>, the pattern in
/// <c>${v%pat}</c>) → a .NET regex body, including bash's extended globs. The one converter for
/// the extglob forms; a caller anchors the result (<see cref="ToAnchoredRegex"/>).
/// <list type="bullet">
/// <item><c>?(P)</c> zero or one, <c>*(P)</c> zero or more, <c>+(P)</c> one or more, <c>@(P)</c>
/// exactly one, of the <c>|</c>-separated pattern list P (each item a full pattern, nesting allowed).</item>
/// <item><c>!(P)</c> any string that does NOT match the list. Exact, not the common lookahead
/// approximation: the segment is captured, then a lookahead NESTED IN A LOOKBEHIND checks that
/// P cannot match exactly that captured text (oracle: <c>[[ a == !(a)* ]]</c> is true,
/// <c>[[ ac == @(a)!(c) ]]</c> false — the approximation gets the first wrong).</item>
/// <item><c>*</c>, <c>?</c>, <c>[…]</c> (<c>[!…]</c>/<c>[^…]</c> negation, POSIX classes), <c>\x</c>
/// literal x; with <c>honorQuotes</c>, <c>'…'</c> / <c>"…"</c> text is literal and the quotes drop.</item>
/// </list>
/// Bash only parses the extglob forms after <c>shopt -s extglob</c> outside <c>[[ ]]</c> (inside it
/// they are always on); the transpiler recognizes them unconditionally — a superset that differs
/// only for scripts bash rejects with a syntax error.
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

    /// <summary><c>(?s)^(?:body)\z</c>: a whole-string match, <c>.</c> spanning newlines as bash's
    /// <c>*</c> / <c>?</c> do.</summary>
    public static string ToAnchoredRegex(string raw, bool honorQuotes = true)
        => "(?s)^(?:" + ToRegex(raw, lazy: false, honorQuotes) + ")\\z";

    /// <summary>The unanchored regex body for <paramref name="raw"/>. <paramref name="lazy"/> makes
    /// <c>*</c> shortest-first (the <c>${v#pat}</c> / <c>${v%pat}</c> shortest-match forms).</summary>
    public static string ToRegex(string raw, bool lazy = false, bool honorQuotes = true)
    {
        var conv = new Converter(raw, lazy, honorQuotes);
        int pos = 0;
        string body = conv.Sequence(ref pos, inList: false);
        return body;
    }

    private sealed class Converter(string raw, bool lazy, bool honorQuotes)
    {
        private int _groups;

        // A pattern sequence up to end of input, or — inside an extglob list — up to the `|` or
        // `)` that ends this item (left unconsumed for the caller).
        public string Sequence(ref int pos, bool inList)
        {
            var sb = new StringBuilder();
            while (pos < raw.Length)
            {
                char c = raw[pos];
                if (inList && c is '|' or ')') break;
                if (c is '?' or '*' or '+' or '@' or '!' && pos + 1 < raw.Length && raw[pos + 1] == '('
                    && TryExtGlob(ref pos, out var eg))
                {
                    sb.Append(eg);
                    continue;
                }
                if (c == '*') { sb.Append(lazy ? ".*?" : ".*"); pos++; continue; }
                if (c == '?') { sb.Append('.'); pos++; continue; }
                if (c == '[' && TryBracket(ref pos, out var cls)) { sb.Append(cls); continue; }
                if (c == '\\')
                {
                    if (pos + 1 < raw.Length) { sb.Append(Regex.Escape(raw[pos + 1].ToString())); pos += 2; }
                    else { sb.Append(@"\\"); pos++; }
                    continue;
                }
                if (honorQuotes && c == '\'')
                {
                    int close = raw.IndexOf('\'', pos + 1);
                    if (close > pos)
                    {
                        sb.Append(Regex.Escape(raw[(pos + 1)..close]));
                        pos = close + 1;
                        continue;
                    }
                }
                if (honorQuotes && c == '"' && TryDoubleQuoted(ref pos, out var dq)) { sb.Append(dq); continue; }
                sb.Append(Regex.Escape(c.ToString()));
                pos++;
            }
            return sb.ToString();
        }

        // `op( item | item … )` at pos (pos on the operator). False — nothing consumed — when the
        // list has no closing paren; the operator is then an ordinary glob char.
        private bool TryExtGlob(ref int pos, out string regex)
        {
            regex = "";
            char op = raw[pos];
            int p = pos + 2;
            var items = new List<string>();
            while (true)
            {
                items.Add(Sequence(ref p, inList: true));
                if (p >= raw.Length) return false;              // unterminated list
                if (raw[p] == ')') { p++; break; }
                p++;                                            // the `|`
            }
            string list = string.Join("|", items);
            // Shortest-match callers (`${v#pat}`, `${v%pat}`) need every quantifier lazy, not
            // just `*`: `${v#+(a)}` on `aaa` strips ONE a (oracle).
            string q = lazy ? "?" : "";
            regex = op switch
            {
                '?' => "(?:" + list + ")?" + q,
                '*' => "(?:" + list + ")*" + q,
                '+' => "(?:" + list + ")+" + q,
                '@' => "(?:" + list + ")",
                _ => Negation(list),
            };
            pos = p;
            return true;
        }

        // !(list): capture the segment x and the tail t after it; the lookbehind walks back over x
        // (right to left) to x's start, where the lookahead asks whether the list matches EXACTLY x
        // (list, then precisely the captured tail, then end). If it can, this split is rejected.
        private string Negation(string list)
        {
            int n = _groups++;
            string x = "psbx" + n, t = "psbt" + n;
            string any = lazy ? ".*?" : ".*";
            return $"(?<{x}>{any})(?=(?<{t}>.*))(?<!(?=(?:{list})\\k<{t}>\\z)\\k<{x}>)";
        }

        // [ … ] at pos. `!` or `^` first negates; a `]` first is literal; [:name:] classes map to
        // their .NET ranges. False (nothing consumed) when unclosed: the `[` is literal.
        private bool TryBracket(ref int pos, out string cls)
        {
            cls = "";
            int p = pos + 1;
            var sb = new StringBuilder("[");
            if (p < raw.Length && raw[p] is '!' or '^') { sb.Append('^'); p++; }
            bool first = true;
            while (p < raw.Length)
            {
                char c = raw[p];
                if (c == ']' && !first) { sb.Append(']'); pos = p + 1; cls = sb.ToString(); return true; }
                first = false;
                if (c == '[' && p + 1 < raw.Length && raw[p + 1] == ':')
                {
                    int close = raw.IndexOf(":]", p + 2, StringComparison.Ordinal);
                    if (close > 0 && PosixClass(raw[(p + 2)..close]) is { } range)
                    {
                        sb.Append(range);
                        p = close + 2;
                        continue;
                    }
                }
                if (c == '\\' && p + 1 < raw.Length) { sb.Append(EscapeInClass(raw[p + 1])); p += 2; continue; }
                sb.Append(c == '-' ? "-" : EscapeInClass(c));
                p++;
            }
            return false;
        }

        // "…" in a pattern: literal text; `\` escapes only $ ` " \ (bash's double-quote rule).
        private bool TryDoubleQuoted(ref int pos, out string lit)
        {
            lit = "";
            var sb = new StringBuilder();
            int p = pos + 1;
            while (p < raw.Length && raw[p] != '"')
            {
                if (raw[p] == '\\' && p + 1 < raw.Length && raw[p + 1] is '$' or '`' or '"' or '\\') p++;
                sb.Append(raw[p]);
                p++;
            }
            if (p >= raw.Length) return false;
            lit = Regex.Escape(sb.ToString());
            pos = p + 1;
            return true;
        }

        private static string EscapeInClass(char c) => c is '\\' or ']' or '[' or '^' ? "\\" + c : c.ToString();
    }

    /// <summary>The .NET class body for a POSIX bracket class name (<c>digit</c> → <c>0-9</c>), or
    /// null for an unknown name. Shared with the runtime's <c>BashRuntime.TranslatePosixClasses</c>.</summary>
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
}
