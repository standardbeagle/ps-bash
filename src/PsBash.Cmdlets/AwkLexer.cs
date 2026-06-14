using System.Text;

namespace PsBash.Cmdlets;

internal enum TokKind
{
    Number, String, Regex, Name, FuncName, Builtin, Keyword,
    Dollar, LParen, RParen, LBrace, RBrace, LBracket, RBracket,
    Semicolon, Newline, Comma,
    Assign, AddAssign, SubAssign, MulAssign, DivAssign, ModAssign, PowAssign,
    Plus, Minus, Star, Slash, Percent, Caret,
    Incr, Decr,
    Eq, Ne, Lt, Le, Gt, Ge,
    Not, Match, NotMatch,
    And, Or,
    Question, Colon,
    Append, Pipe,
    Eof
}

internal sealed class Tok
{
    public TokKind Kind;
    public string Text = "";
    public double Num;
    public override string ToString() => $"{Kind}:{Text}";
}

internal static class AwkLexer
{
    private static readonly HashSet<string> Keywords = new()
    {
        "BEGIN", "END", "function", "func",
        "if", "else", "while", "for", "do",
        "break", "continue", "next", "nextfile", "exit",
        "return", "delete", "in", "getline", "print", "printf",
    };

    private static readonly HashSet<string> Builtins = new()
    {
        "length", "substr", "index", "split", "sub", "gsub", "gensub", "match",
        "sprintf", "sin", "cos", "atan2", "exp", "log", "sqrt", "int",
        "rand", "srand", "tolower", "toupper", "system", "systime", "strftime",
        "mktime", "close", "fflush",
    };

    public static List<Tok> Tokenize(string src)
    {
        var toks = new List<Tok>();
        int i = 0, n = src.Length;

        // Whether a '/' at the current position begins a regex (vs division).
        bool RegexAllowed()
        {
            for (int k = toks.Count - 1; k >= 0; k--)
            {
                var t = toks[k];
                if (t.Kind == TokKind.Newline) continue;
                return t.Kind switch
                {
                    TokKind.Number or TokKind.String or TokKind.Name or TokKind.Regex
                        or TokKind.RParen or TokKind.RBracket or TokKind.Dollar
                        or TokKind.Incr or TokKind.Decr or TokKind.Builtin => false,
                    _ => true,
                };
            }
            return true;
        }

        while (i < n)
        {
            char c = src[i];

            // line continuation: backslash-newline
            if (c == '\\' && i + 1 < n && (src[i + 1] == '\n' || src[i + 1] == '\r'))
            {
                i += 2;
                if (i <= n && i - 1 < n && src[i - 1] == '\r' && i < n && src[i] == '\n') i++;
                continue;
            }

            if (c == ' ' || c == '\t') { i++; continue; }

            if (c == '#')
            {
                while (i < n && src[i] != '\n') i++;
                continue;
            }

            if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < n && src[i + 1] == '\n') i++;
                i++;
                Add(toks, TokKind.Newline, "\n");
                continue;
            }

            // numbers
            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(src[i + 1])))
            {
                int start = i;
                if (c == '0' && i + 1 < n && (src[i + 1] == 'x' || src[i + 1] == 'X'))
                {
                    i += 2;
                    // Accumulate directly into a double (awk's only numeric type).
                    // Convert.ToInt64 would throw OverflowException on a value past
                    // Int64.MaxValue (e.g. 0xFFFFFFFFFFFFFFFFFF) and FormatException
                    // on a bare `0x` with no digits — both crash the lexer.
                    double hv = 0;
                    while (i < n && Uri.IsHexDigit(src[i])) { hv = hv * 16 + HexVal(src[i]); i++; }
                    string hex = src.Substring(start, i - start);
                    toks.Add(new Tok { Kind = TokKind.Number, Text = hex, Num = hv });
                    continue;
                }
                while (i < n && char.IsDigit(src[i])) i++;
                if (i < n && src[i] == '.') { i++; while (i < n && char.IsDigit(src[i])) i++; }
                if (i < n && (src[i] == 'e' || src[i] == 'E'))
                {
                    int em = i; i++;
                    if (i < n && (src[i] == '+' || src[i] == '-')) i++;
                    int ed = i;
                    while (i < n && char.IsDigit(src[i])) i++;
                    if (i == ed) i = em;
                }
                string num = src.Substring(start, i - start);
                toks.Add(new Tok { Kind = TokKind.Number, Text = num, Num = double.Parse(num, System.Globalization.CultureInfo.InvariantCulture) });
                continue;
            }

            // identifiers / keywords / builtins / function-call names
            if (c == '_' || char.IsLetter(c))
            {
                int start = i;
                while (i < n && (src[i] == '_' || char.IsLetterOrDigit(src[i]))) i++;
                string word = src.Substring(start, i - start);
                if (Keywords.Contains(word))
                {
                    Add(toks, TokKind.Keyword, word);
                }
                else if (Builtins.Contains(word))
                {
                    Add(toks, TokKind.Builtin, word);
                }
                else
                {
                    // A name directly followed by '(' (no space) is a function call.
                    if (i < n && src[i] == '(')
                        toks.Add(new Tok { Kind = TokKind.FuncName, Text = word });
                    else
                        Add(toks, TokKind.Name, word);
                }
                continue;
            }

            // string literal
            if (c == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < n && src[i] != '"')
                {
                    if (src[i] == '\\' && i + 1 < n)
                    {
                        i++;
                        sb.Append(Unescape(src[i]));
                        i++;
                    }
                    else { sb.Append(src[i]); i++; }
                }
                if (i >= n) throw new AwkInterpreter.AwkSyntaxException("awk: syntax error: unterminated string literal");
                i++; // closing quote
                toks.Add(new Tok { Kind = TokKind.String, Text = sb.ToString() });
                continue;
            }

            // regex literal or division
            if (c == '/')
            {
                if (RegexAllowed())
                {
                    i++;
                    var sb = new StringBuilder();
                    bool inClass = false;
                    while (i < n && (src[i] != '/' || inClass))
                    {
                        if (src[i] == '\\' && i + 1 < n) { sb.Append(src[i]); sb.Append(src[i + 1]); i += 2; continue; }
                        if (src[i] == '[') inClass = true;
                        else if (src[i] == ']') inClass = false;
                        sb.Append(src[i]); i++;
                    }
                    if (i >= n) throw new AwkInterpreter.AwkSyntaxException("awk: syntax error: unterminated regex literal");
                    i++; // closing slash
                    toks.Add(new Tok { Kind = TokKind.Regex, Text = sb.ToString() });
                    continue;
                }
                if (i + 1 < n && src[i + 1] == '=') { Add(toks, TokKind.DivAssign, "/="); i += 2; continue; }
                Add(toks, TokKind.Slash, "/"); i++; continue;
            }

            // operators / punctuation
            switch (c)
            {
                case '$': Add(toks, TokKind.Dollar, "$"); i++; break;
                case '(': Add(toks, TokKind.LParen, "("); i++; break;
                case ')': Add(toks, TokKind.RParen, ")"); i++; break;
                case '{': Add(toks, TokKind.LBrace, "{"); i++; break;
                case '}': Add(toks, TokKind.RBrace, "}"); i++; break;
                case '[': Add(toks, TokKind.LBracket, "["); i++; break;
                case ']': Add(toks, TokKind.RBracket, "]"); i++; break;
                case ';': Add(toks, TokKind.Semicolon, ";"); i++; break;
                case ',': Add(toks, TokKind.Comma, ","); i++; break;
                case '?': Add(toks, TokKind.Question, "?"); i++; break;
                case ':': Add(toks, TokKind.Colon, ":"); i++; break;
                case '~': Add(toks, TokKind.Match, "~"); i++; break;
                case '+':
                    if (Next(src, i) == '+') { Add(toks, TokKind.Incr, "++"); i += 2; }
                    else if (Next(src, i) == '=') { Add(toks, TokKind.AddAssign, "+="); i += 2; }
                    else { Add(toks, TokKind.Plus, "+"); i++; }
                    break;
                case '-':
                    if (Next(src, i) == '-') { Add(toks, TokKind.Decr, "--"); i += 2; }
                    else if (Next(src, i) == '=') { Add(toks, TokKind.SubAssign, "-="); i += 2; }
                    else { Add(toks, TokKind.Minus, "-"); i++; }
                    break;
                case '*':
                    if (Next(src, i) == '*')
                    {
                        // ** and **= are gawk aliases for ^ and ^=
                        if (i + 2 < n && src[i + 2] == '=') { Add(toks, TokKind.PowAssign, "**="); i += 3; }
                        else { Add(toks, TokKind.Caret, "**"); i += 2; }
                    }
                    else if (Next(src, i) == '=') { Add(toks, TokKind.MulAssign, "*="); i += 2; }
                    else { Add(toks, TokKind.Star, "*"); i++; }
                    break;
                case '%':
                    if (Next(src, i) == '=') { Add(toks, TokKind.ModAssign, "%="); i += 2; }
                    else { Add(toks, TokKind.Percent, "%"); i++; }
                    break;
                case '^':
                    if (Next(src, i) == '=') { Add(toks, TokKind.PowAssign, "^="); i += 2; }
                    else { Add(toks, TokKind.Caret, "^"); i++; }
                    break;
                case '=':
                    if (Next(src, i) == '=') { Add(toks, TokKind.Eq, "=="); i += 2; }
                    else { Add(toks, TokKind.Assign, "="); i++; }
                    break;
                case '!':
                    if (Next(src, i) == '=') { Add(toks, TokKind.Ne, "!="); i += 2; }
                    else if (Next(src, i) == '~') { Add(toks, TokKind.NotMatch, "!~"); i += 2; }
                    else { Add(toks, TokKind.Not, "!"); i++; }
                    break;
                case '<':
                    if (Next(src, i) == '=') { Add(toks, TokKind.Le, "<="); i += 2; }
                    else { Add(toks, TokKind.Lt, "<"); i++; }
                    break;
                case '>':
                    if (Next(src, i) == '=') { Add(toks, TokKind.Ge, ">="); i += 2; }
                    else if (Next(src, i) == '>') { Add(toks, TokKind.Append, ">>"); i += 2; }
                    else { Add(toks, TokKind.Gt, ">"); i++; }
                    break;
                case '&':
                    if (Next(src, i) == '&') { Add(toks, TokKind.And, "&&"); i += 2; }
                    else { i++; } // bare & unsupported; skip
                    break;
                case '|':
                    if (Next(src, i) == '|') { Add(toks, TokKind.Or, "||"); i += 2; }
                    else { Add(toks, TokKind.Pipe, "|"); i++; }
                    break;
                default:
                    // Unknown char — skip to avoid hard failure.
                    i++;
                    break;
            }
        }

        toks.Add(new Tok { Kind = TokKind.Eof });
        return toks;
    }

    private static char Next(string s, int i) => i + 1 < s.Length ? s[i + 1] : '\0';

    private static int HexVal(char c) =>
        c >= '0' && c <= '9' ? c - '0' :
        c >= 'a' && c <= 'f' ? c - 'a' + 10 :
        c - 'A' + 10;

    private static void Add(List<Tok> toks, TokKind kind, string text) => toks.Add(new Tok { Kind = kind, Text = text });

    private static string Unescape(char c) => c switch
    {
        'n' => "\n",
        't' => "\t",
        'r' => "\r",
        'a' => "\a",
        'b' => "\b",
        'f' => "\f",
        'v' => "\v",
        '\\' => "\\",
        '"' => "\"",
        '/' => "/",
        _ => "\\" + c, // unknown escape: keep backslash (awk behavior varies; preserve)
    };
}
