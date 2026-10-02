using System.Globalization;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>A jq program that does not compile (syntax error, undefined function/variable); exit status 3.</summary>
internal sealed class JqCompileException : Exception
{
    public JqCompileException(string message) : base(message) { }
}

// ───────────────────────────── AST ─────────────────────────────

internal abstract class JNode { public int Line = 1; }

internal sealed class JIdentity : JNode { }
internal sealed class JRecurseDefault : JNode { }
internal sealed class JLiteral : JNode { public JLiteral(object? v) { Value = v; } public object? Value { get; } }

/// <summary>A string literal; Parts are <c>string</c> pieces and <see cref="JNode"/> interpolations; Format is <c>@name</c> or null.</summary>
internal sealed class JString : JNode { public JString(string? format, List<object> parts) { Format = format; Parts = parts; } public string? Format { get; } public List<object> Parts { get; } }
internal sealed class JFormat : JNode { public JFormat(string name) { Name = name; } public string Name { get; } }
internal sealed class JIndex : JNode { public JIndex(JNode t, JNode k) { Target = t; Key = k; } public JNode Target { get; } public JNode Key { get; } }
internal sealed class JSlice : JNode { public JSlice(JNode t, JNode? f, JNode? to) { Target = t; From = f; To = to; } public JNode Target { get; } public JNode? From { get; } public JNode? To { get; } }
internal sealed class JIterate : JNode { public JIterate(JNode t) { Target = t; } public JNode Target { get; } }
internal sealed class JTry : JNode { public JTry(JNode body, JNode? @catch) { Body = body; Catch = @catch; } public JNode Body { get; } public JNode? Catch { get; } }
internal sealed class JNeg : JNode { public JNeg(JNode o) { Operand = o; } public JNode Operand { get; } }
internal sealed class JPipe : JNode { public JPipe(JNode l, JNode r) { L = l; R = r; } public JNode L { get; } public JNode R { get; } }
internal sealed class JComma : JNode { public JComma(JNode l, JNode r) { L = l; R = r; } public JNode L { get; } public JNode R { get; } }
internal sealed class JBinary : JNode { public JBinary(string op, JNode l, JNode r) { Op = op; L = l; R = r; } public string Op { get; } public JNode L { get; } public JNode R { get; } }
internal sealed class JAnd : JNode { public JAnd(JNode l, JNode r) { L = l; R = r; } public JNode L { get; } public JNode R { get; } }
internal sealed class JOr : JNode { public JOr(JNode l, JNode r) { L = l; R = r; } public JNode L { get; } public JNode R { get; } }
internal sealed class JAlt : JNode { public JAlt(JNode l, JNode r) { L = l; R = r; } public JNode L { get; } public JNode R { get; } }
internal sealed class JAssign : JNode { public JAssign(string op, JNode l, JNode r) { Op = op; L = l; R = r; } public string Op { get; } public JNode L { get; } public JNode R { get; } }
internal sealed class JIf : JNode { public JIf(JNode c, JNode t, JNode? e) { Cond = c; Then = t; Else = e; } public JNode Cond { get; } public JNode Then { get; } public JNode? Else { get; } }
internal sealed class JReduce : JNode { public JReduce(JNode s, JPattern p, JNode i, JNode u) { Source = s; Pattern = p; Init = i; Update = u; } public JNode Source { get; } public JPattern Pattern { get; } public JNode Init { get; } public JNode Update { get; } }
internal sealed class JForeach : JNode { public JForeach(JNode s, JPattern p, JNode i, JNode u, JNode? x) { Source = s; Pattern = p; Init = i; Update = u; Extract = x; } public JNode Source { get; } public JPattern Pattern { get; } public JNode Init { get; } public JNode Update { get; } public JNode? Extract { get; } }
internal sealed class JFuncDef : JNode { public JFuncDef(string name, string[] ps, JNode body, JNode rest) { Name = name; Params = ps; Body = body; Rest = rest; } public string Name { get; } public string[] Params { get; } public JNode Body { get; } public JNode Rest { get; } }
internal sealed class JCall : JNode { public JCall(string name, JNode[] args) { Name = name; Args = args; } public string Name { get; } public JNode[] Args { get; } }
internal sealed class JVar : JNode { public JVar(string n) { Name = n; } public string Name { get; } }
internal sealed class JAs : JNode { public JAs(JNode s, List<JPattern> ps, JNode b) { Source = s; Patterns = ps; Body = b; } public JNode Source { get; } public List<JPattern> Patterns { get; } public JNode Body { get; } }
internal sealed class JLabel : JNode { public JLabel(string n, JNode b) { Name = n; Body = b; } public string Name { get; } public JNode Body { get; } }
internal sealed class JBreak : JNode { public JBreak(string n) { Name = n; } public string Name { get; } }
internal sealed class JArrayCons : JNode { public JArrayCons(JNode? b) { Body = b; } public JNode? Body { get; } }
internal sealed class JObjectCons : JNode { public JObjectCons(List<(JNode Key, JNode Value)> e) { Entries = e; } public List<(JNode Key, JNode Value)> Entries { get; } }

internal abstract class JPattern { }
internal sealed class JPVar : JPattern { public JPVar(string n) { Name = n; } public string Name { get; } }
internal sealed class JPArray : JPattern { public JPArray(List<JPattern> e) { Elements = e; } public List<JPattern> Elements { get; } }
internal sealed class JPObject : JPattern
{
    public JPObject(List<JPObjectEntry> e) { Entries = e; }
    public List<JPObjectEntry> Entries { get; }
}
/// <summary>One <c>{...}</c> pattern entry: the key expression, the variable it also binds (<c>$name</c>), and the sub-pattern.</summary>
internal sealed class JPObjectEntry { public JNode Key = null!; public string? Var; public JPattern? Sub; }

// ───────────────────────────── lexer ─────────────────────────────

internal enum JTok { Number, String, Ident, Field, Var, Format, Keyword, Op, End }

internal sealed class JToken
{
    public JTok Kind;
    public string Text = "";
    public int Line = 1;
    public int Position;
    /// <summary>For strings: the parts (literal text or interpolation source).</summary>
    public List<object>? StringParts;
}

internal static class JqLexer
{
    private static readonly HashSet<string> Keywords = new()
    {
        "def", "if", "then", "elif", "else", "end", "as", "reduce", "foreach", "try", "catch", "label", "import", "include", "and", "or", "__loc__",
    };

    private static readonly string[] Operators =
    {
        "?//", "//=", "|=", "+=", "-=", "*=", "/=", "%=", "==", "!=", "<=", ">=", "//", "..",
        ".", "[", "]", "{", "}", "(", ")", "|", ",", ":", ";", "=", "<", ">", "+", "-", "*", "/", "%", "?",
    };

    public static List<JToken> Lex(string src)
    {
        var tokens = new List<JToken>();
        int i = 0, line = 1;
        while (i < src.Length)
        {
            char c = src[i];
            if (c == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '#')
            {
                while (i < src.Length && src[i] != '\n') i++;
                continue;
            }
            int start = i;
            if (c == '"')
            {
                var parts = LexString(src, ref i, line);
                tokens.Add(new JToken { Kind = JTok.String, StringParts = parts, Line = line, Position = start });
                continue;
            }
            if (c == '.' && i + 1 < src.Length && (char.IsLetter(src[i + 1]) || src[i + 1] == '_'))
            {
                int j = i + 1;
                while (j < src.Length && (char.IsLetterOrDigit(src[j]) || src[j] == '_')) j++;
                tokens.Add(new JToken { Kind = JTok.Field, Text = src.Substring(i + 1, j - i - 1), Line = line, Position = start });
                i = j;
                continue;
            }
            if (char.IsDigit(c) || (c == '.' && i + 1 < src.Length && char.IsDigit(src[i + 1])))
            {
                int j = i;
                while (j < src.Length && char.IsDigit(src[j])) j++;
                if (j < src.Length && src[j] == '.') { j++; while (j < src.Length && char.IsDigit(src[j])) j++; }
                if (j < src.Length && (src[j] == 'e' || src[j] == 'E'))
                {
                    int k = j + 1;
                    if (k < src.Length && (src[k] == '+' || src[k] == '-')) k++;
                    if (k < src.Length && char.IsDigit(src[k]))
                    {
                        while (k < src.Length && char.IsDigit(src[k])) k++;
                        j = k;
                    }
                }
                tokens.Add(new JToken { Kind = JTok.Number, Text = src.Substring(i, j - i), Line = line, Position = start });
                i = j;
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int j = i;
                while (j < src.Length)
                {
                    if (char.IsLetterOrDigit(src[j]) || src[j] == '_') { j++; continue; }
                    if (src[j] == ':' && j + 2 < src.Length && src[j + 1] == ':' && (char.IsLetter(src[j + 2]) || src[j + 2] == '_')) { j += 2; continue; }
                    break;
                }
                string word = src.Substring(i, j - i);
                tokens.Add(new JToken { Kind = Keywords.Contains(word) ? JTok.Keyword : JTok.Ident, Text = word, Line = line, Position = start });
                i = j;
                continue;
            }
            if (c == '$' && i + 1 < src.Length && (char.IsLetter(src[i + 1]) || src[i + 1] == '_'))
            {
                int j = i + 1;
                while (j < src.Length && (char.IsLetterOrDigit(src[j]) || src[j] == '_')) j++;
                tokens.Add(new JToken { Kind = JTok.Var, Text = src.Substring(i + 1, j - i - 1), Line = line, Position = start });
                i = j;
                continue;
            }
            if (c == '@' && i + 1 < src.Length && (char.IsLetter(src[i + 1]) || src[i + 1] == '_'))
            {
                int j = i + 1;
                while (j < src.Length && (char.IsLetterOrDigit(src[j]) || src[j] == '_')) j++;
                tokens.Add(new JToken { Kind = JTok.Format, Text = src.Substring(i, j - i), Line = line, Position = start });
                i = j;
                continue;
            }
            string? op = null;
            foreach (var candidate in Operators)
            {
                if (string.CompareOrdinal(src, i, candidate, 0, candidate.Length) == 0) { op = candidate; break; }
            }
            if (op is null) throw new JqCompileException($"syntax error, unexpected INVALID_CHARACTER (Unix shell quoting issues?) at <top-level>, line {line}:");
            tokens.Add(new JToken { Kind = JTok.Op, Text = op, Line = line, Position = start });
            i += op.Length;
        }
        tokens.Add(new JToken { Kind = JTok.End, Line = line, Position = src.Length });
        return tokens;
    }

    /// <summary>Lexes a string literal starting at the opening quote; <c>\(...)</c> interpolations become <see cref="JNode"/>-source parts.</summary>
    private static List<object> LexString(string src, ref int i, int line)
    {
        var parts = new List<object>();
        var sb = new StringBuilder();
        i++;   // opening quote
        while (true)
        {
            if (i >= src.Length) throw new JqCompileException($"syntax error, unexpected end of file (Unix shell quoting issues?) at <top-level>, line {line}:");
            char c = src[i++];
            if (c == '"') break;
            if (c != '\\') { sb.Append(c); continue; }
            if (i >= src.Length) throw new JqCompileException("syntax error, unterminated string at <top-level>, line " + line + ":");
            char e = src[i++];
            switch (e)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    {
                        int cp = ReadHex(src, ref i);
                        if (cp >= 0xD800 && cp <= 0xDBFF && i + 1 < src.Length && src[i] == '\\' && src[i + 1] == 'u')
                        {
                            int save = i;
                            i += 2;
                            int lo = ReadHex(src, ref i);
                            if (lo >= 0xDC00 && lo <= 0xDFFF) { sb.Append((char)cp).Append((char)lo); break; }
                            i = save;
                        }
                        sb.Append(cp >= 0xD800 && cp <= 0xDFFF ? '�' : (char)cp);
                        break;
                    }
                case '(':
                    {
                        if (sb.Length > 0) { parts.Add(sb.ToString()); sb.Clear(); }
                        int depth = 1, j = i;
                        while (j < src.Length && depth > 0)
                        {
                            char d = src[j];
                            if (d == '"') { int k = j; LexString(src, ref k, line); j = k; continue; }
                            if (d == '(') depth++;
                            else if (d == ')') depth--;
                            j++;
                        }
                        if (depth != 0) throw new JqCompileException("syntax error, unterminated string interpolation at <top-level>, line " + line + ":");
                        parts.Add(new JInterpolationSource(src.Substring(i, j - 1 - i), line));
                        i = j;
                        break;
                    }
                default:
                    throw new JqCompileException($"syntax error, invalid escape '\\{e}' at <top-level>, line {line}:");
            }
        }
        if (sb.Length > 0 || parts.Count == 0) parts.Add(sb.ToString());
        return parts;
    }

    private static int ReadHex(string src, ref int i)
    {
        if (i + 4 > src.Length || !int.TryParse(src.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
            throw new JqCompileException("syntax error, invalid \\u escape at <top-level>");
        i += 4;
        return v;
    }
}

internal sealed record JInterpolationSource(string Source, int Line);

// ───────────────────────────── parser ─────────────────────────────

/// <summary>
/// Recursive-descent parser for jq programs, following jq's grammar and precedence (<c>|</c> &lt; <c>,</c> &lt; <c>//</c> &lt; assignment &lt;
/// <c>or</c> &lt; <c>and</c> &lt; comparison &lt; <c>+ -</c> &lt; <c>* / %</c> &lt; unary minus &lt; postfix). <c>Term as $x | body</c> takes the whole
/// rest of the pipeline as its body; <c>try</c>/<c>reduce</c>/<c>foreach</c>/<c>if</c>/<c>label</c>/<c>def</c> are supported as terms.
/// </summary>
internal sealed class JqParser
{
    private readonly List<JToken> _t;
    private int _p;
    private readonly string _src;

    private JqParser(string src, List<JToken> tokens) { _src = src; _t = tokens; }

    /// <summary>Parses a sequence of <c>def ...;</c> definitions (the built-in prelude).</summary>
    public static List<JFuncDef> ParseDefinitions(string source)
    {
        var parser = new JqParser(source, JqLexer.Lex(source));
        var defs = new List<JFuncDef>();
        while (parser.IsKw("def"))
        {
            parser.ParseFuncDefHeader(out string name, out string[] ps, out JNode body);
            defs.Add(new JFuncDef(name, ps, body, new JIdentity()));
        }
        if (parser.Peek.Kind != JTok.End) throw parser.Unexpected(parser.Peek);
        return defs;
    }

    public static JNode Parse(string program)
    {
        var parser = new JqParser(program, JqLexer.Lex(program));
        // A program may be only definitions (a library); treat the empty remainder as identity.
        JNode node = parser.ParseProgram();
        return node;
    }

    private JToken Peek => _t[_p];
    private JToken PeekAt(int n) => _t[Math.Min(_p + n, _t.Count - 1)];
    private JToken Next() => _t[_p++];

    private bool IsOp(string op) => Peek.Kind == JTok.Op && Peek.Text == op;
    private bool IsKw(string kw) => Peek.Kind == JTok.Keyword && Peek.Text == kw;

    private JqCompileException Unexpected(JToken tok)
    {
        string what = tok.Kind switch
        {
            JTok.End => "end of file",
            JTok.Number => "LITERAL",
            JTok.String => "QQSTRING_START",
            JTok.Ident => "IDENT",
            JTok.Field => "FIELD",
            JTok.Var => "'$'",
            JTok.Format => "FORMAT",
            JTok.Keyword => tok.Text,
            _ => "'" + tok.Text + "'",
        };
        return new JqCompileException($"syntax error, unexpected {what} (Unix shell quoting issues?) at <top-level>, line {tok.Line}:");
    }

    private void ExpectOp(string op)
    {
        if (!IsOp(op)) throw Unexpected(Peek);
        _p++;
    }

    private void ExpectKw(string kw)
    {
        if (!IsKw(kw)) throw Unexpected(Peek);
        _p++;
    }

    private JNode ParseProgram()
    {
        // Leading `import`/`include` directives are not supported.
        if (IsKw("import") || IsKw("include")) throw new JqCompileException("syntax error: modules are not supported by ps-bash at <top-level>, line 1:");
        if (Peek.Kind == JTok.End) return new JIdentity();
        var node = ParsePipe();
        if (Peek.Kind != JTok.End) throw Unexpected(Peek);
        return node;
    }

    // ───────────── pipe and below ─────────────

    private JNode ParsePipe()
    {
        int line = Peek.Line;
        if (IsKw("def"))
        {
            ParseFuncDefHeader(out string name, out string[] ps, out JNode body);
            if (Peek.Kind == JTok.End) throw new JqCompileException("Top-level program not given (try \".\")");
            JNode rest = ParsePipe();
            return new JFuncDef(name, ps, body, rest) { Line = line };
        }
        var lhs = ParseComma();
        if (IsOp("|"))
        {
            _p++;
            var rhs = ParsePipe();
            return new JPipe(lhs, rhs) { Line = line };
        }
        return lhs;
    }

    private bool ParseFuncDefHeader(out string name, out string[] ps, out JNode body)
    {
        ExpectKw("def");
        var nameTok = Next();
        if (nameTok.Kind != JTok.Ident && nameTok.Kind != JTok.Keyword) throw Unexpected(nameTok);
        name = nameTok.Text;
        var parameters = new List<string>();
        if (IsOp("("))
        {
            _p++;
            while (true)
            {
                var pt = Next();
                if (pt.Kind == JTok.Var) parameters.Add("$" + pt.Text);
                else if (pt.Kind == JTok.Ident || pt.Kind == JTok.Keyword) parameters.Add(pt.Text);
                else throw Unexpected(pt);
                if (IsOp(";")) { _p++; continue; }
                ExpectOp(")");
                break;
            }
        }
        ExpectOp(":");
        body = ParsePipe();
        ExpectOp(";");
        ps = parameters.ToArray();
        return true;
    }

    private JNode ParseComma()
    {
        var lhs = ParseAlt();
        while (IsOp(","))
        {
            int line = Peek.Line;
            _p++;
            lhs = new JComma(lhs, ParseAlt()) { Line = line };
        }
        return lhs;
    }

    private JNode ParseAlt()
    {
        var lhs = ParseAssign();
        if (IsOp("//"))
        {
            int line = Peek.Line;
            _p++;
            return new JAlt(lhs, ParseAlt()) { Line = line };
        }
        return lhs;
    }

    private static readonly HashSet<string> AssignOps = new() { "=", "|=", "+=", "-=", "*=", "/=", "%=", "//=" };

    private JNode ParseAssign()
    {
        var lhs = ParseOr();
        if (Peek.Kind == JTok.Op && AssignOps.Contains(Peek.Text))
        {
            var op = Next();
            // The right side of `//=`/`=` is an alternative-level expression in jq's yacc grammar
            // (`.a = 1 // 2` is `(.a = 1) // 2`), so it stops before `//`.
            var rhs = ParseOr();
            return new JAssign(op.Text, lhs, rhs) { Line = op.Line };
        }
        return lhs;
    }

    private JNode ParseOr()
    {
        var lhs = ParseAnd();
        while (IsKw("or"))
        {
            int line = Peek.Line;
            _p++;
            lhs = new JOr(lhs, ParseAnd()) { Line = line };
        }
        return lhs;
    }

    private JNode ParseAnd()
    {
        var lhs = ParseCompare();
        while (IsKw("and"))
        {
            int line = Peek.Line;
            _p++;
            lhs = new JAnd(lhs, ParseCompare()) { Line = line };
        }
        return lhs;
    }

    private static readonly HashSet<string> CompareOps = new() { "==", "!=", "<", "<=", ">", ">=" };

    private JNode ParseCompare()
    {
        var lhs = ParseAdditive();
        if (Peek.Kind == JTok.Op && CompareOps.Contains(Peek.Text))
        {
            var op = Next();
            var rhs = ParseAdditive();
            lhs = new JBinary(op.Text, lhs, rhs) { Line = op.Line };
            if (Peek.Kind == JTok.Op && CompareOps.Contains(Peek.Text)) throw Unexpected(Peek);   // non-associative
        }
        return lhs;
    }

    private JNode ParseAdditive()
    {
        var lhs = ParseMultiplicative();
        while (IsOp("+") || IsOp("-"))
        {
            var op = Next();
            lhs = new JBinary(op.Text, lhs, ParseMultiplicative()) { Line = op.Line };
        }
        return lhs;
    }

    private JNode ParseMultiplicative()
    {
        var lhs = ParseUnary();
        while (IsOp("*") || IsOp("/") || IsOp("%"))
        {
            var op = Next();
            lhs = new JBinary(op.Text, lhs, ParseUnary()) { Line = op.Line };
        }
        return lhs;
    }

    private JNode ParseUnary()
    {
        if (IsOp("-"))
        {
            int line = Peek.Line;
            _p++;
            return new JNeg(ParseUnary()) { Line = line };
        }
        return ParsePostfix();
    }

    // ───────────── postfix terms ─────────────

    private JNode ParsePostfix()
    {
        var term = ParsePostfixNoAs();
        if (IsKw("as"))
        {
            int line = Peek.Line;
            _p++;
            var patterns = new List<JPattern> { ParsePattern() };
            while (IsOp("?//")) { _p++; patterns.Add(ParsePattern()); }
            ExpectOp("|");
            var body = ParsePipe();
            return new JAs(term, patterns, body) { Line = line };
        }
        return term;
    }

    private JNode ParsePostfixNoAs()
    {
        var term = ParsePrimary();
        while (true)
        {
            int line = Peek.Line;
            if (Peek.Kind == JTok.Field)
            {
                var f = Next();
                term = new JIndex(term, new JLiteral(f.Text)) { Line = line };
            }
            else if (IsOp(".") && PeekAt(1).Kind == JTok.String)
            {
                _p++;
                var key = ParseStringToken(Next(), null);
                term = new JIndex(term, key) { Line = line };
            }
            else if (IsOp(".") && PeekAt(1).Kind == JTok.Op && PeekAt(1).Text == "[")
            {
                _p++;   // `.a.[0]` — the dot before a bracket is optional
            }
            else if (IsOp("["))
            {
                term = ParseBracketSuffix(term);
            }
            else if (IsOp("?"))
            {
                _p++;
                term = new JTry(term, null) { Line = line };
            }
            else break;
        }
        return term;
    }

    private JNode ParseBracketSuffix(JNode target)
    {
        int line = Peek.Line;
        ExpectOp("[");
        if (IsOp("]")) { _p++; return new JIterate(target) { Line = line }; }
        if (IsOp(":"))
        {
            _p++;
            var to = ParsePipe();
            ExpectOp("]");
            return new JSlice(target, null, to) { Line = line };
        }
        var first = ParsePipe();
        if (IsOp(":"))
        {
            _p++;
            if (IsOp("]")) { _p++; return new JSlice(target, first, null) { Line = line }; }
            var to = ParsePipe();
            ExpectOp("]");
            return new JSlice(target, first, to) { Line = line };
        }
        ExpectOp("]");
        return new JIndex(target, first) { Line = line };
    }

    private JNode ParsePrimary()
    {
        var tok = Peek;
        int line = tok.Line;
        switch (tok.Kind)
        {
            case JTok.Number:
                {
                    _p++;
                    double d = double.Parse(tok.Text, NumberStyles.Float, CultureInfo.InvariantCulture);
                    return new JLiteral(new JqNumber(d, JqValue.CanonicalLiteral(tok.Text))) { Line = line };
                }
            case JTok.String:
                _p++;
                return ParseStringToken(tok, null);
            case JTok.Format:
                {
                    _p++;
                    if (Peek.Kind == JTok.String) { var s = Next(); return ParseStringToken(s, tok.Text); }
                    return new JFormat(tok.Text) { Line = line };
                }
            case JTok.Field:
                _p++;
                return new JIndex(new JIdentity(), new JLiteral(tok.Text)) { Line = line };
            case JTok.Var:
                _p++;
                return new JVar(tok.Text) { Line = line };
            case JTok.Keyword:
                return ParseKeywordTerm(tok);
            case JTok.Ident:
                return ParseCall();
            case JTok.Op:
                break;
            default:
                throw Unexpected(tok);
        }

        switch (tok.Text)
        {
            case ".":
                _p++;
                if (Peek.Kind == JTok.String)
                {
                    var s = Next();
                    return new JIndex(new JIdentity(), ParseStringToken(s, null)) { Line = line };
                }
                return new JIdentity { Line = line };
            case "..":
                _p++;
                return new JRecurseDefault { Line = line };
            case "(":
                {
                    _p++;
                    var inner = ParsePipe();
                    ExpectOp(")");
                    return inner;
                }
            case "[":
                {
                    _p++;
                    if (IsOp("]")) { _p++; return new JArrayCons(null) { Line = line }; }
                    var body = ParsePipe();
                    ExpectOp("]");
                    return new JArrayCons(body) { Line = line };
                }
            case "{":
                return ParseObject();
            case "-":
                _p++;
                return new JNeg(ParsePostfix()) { Line = line };
        }
        throw Unexpected(tok);
    }

    private JNode ParseKeywordTerm(JToken tok)
    {
        int line = tok.Line;
        switch (tok.Text)
        {
            case "if":
                {
                    _p++;
                    var cond = ParsePipe();
                    ExpectKw("then");
                    var then = ParsePipe();
                    return new JIf(cond, then, ParseElse()) { Line = line };
                }
            case "try":
                {
                    _p++;
                    var body = ParsePostfixNoAs();
                    JNode? handler = null;
                    if (IsKw("catch")) { _p++; handler = ParsePostfixNoAs(); }
                    return new JTry(body, handler) { Line = line };
                }
            case "reduce":
                {
                    _p++;
                    var source = ParsePostfixNoAs();
                    ExpectKw("as");
                    var pattern = ParsePattern();
                    ExpectOp("(");
                    var init = ParsePipe();
                    ExpectOp(";");
                    var update = ParsePipe();
                    ExpectOp(")");
                    return new JReduce(source, pattern, init, update) { Line = line };
                }
            case "foreach":
                {
                    _p++;
                    var source = ParsePostfixNoAs();
                    ExpectKw("as");
                    var pattern = ParsePattern();
                    ExpectOp("(");
                    var init = ParsePipe();
                    ExpectOp(";");
                    var update = ParsePipe();
                    JNode? extract = null;
                    if (IsOp(";")) { _p++; extract = ParsePipe(); }
                    ExpectOp(")");
                    return new JForeach(source, pattern, init, update, extract) { Line = line };
                }
            case "label":
                {
                    _p++;
                    var name = Next();
                    if (name.Kind != JTok.Var) throw Unexpected(name);
                    ExpectOp("|");
                    var body = ParsePipe();
                    return new JLabel(name.Text, body) { Line = line };
                }
            case "def":
                {
                    ParseFuncDefHeader(out string name, out string[] ps, out JNode body);
                    var rest = ParsePipe();
                    return new JFuncDef(name, ps, body, rest) { Line = line };
                }
            case "__loc__":
                throw Unexpected(tok);
        }
        throw Unexpected(tok);
    }

    private JNode? ParseElse()
    {
        if (IsKw("elif"))
        {
            int line = Peek.Line;
            _p++;
            var cond = ParsePipe();
            ExpectKw("then");
            var then = ParsePipe();
            return new JIf(cond, then, ParseElse()) { Line = line };
        }
        if (IsKw("else"))
        {
            _p++;
            var e = ParsePipe();
            ExpectKw("end");
            return e;
        }
        ExpectKw("end");
        return null;
    }

    private JNode ParseCall()
    {
        var tok = Next();
        if (tok.Text == "break" && Peek.Kind == JTok.Var)
        {
            var label = Next();
            return new JBreak(label.Text) { Line = tok.Line };
        }
        if (!IsOp("(") && tok.Text is "null" or "true" or "false")
            return new JLiteral(tok.Text == "null" ? null : (object)(tok.Text == "true")) { Line = tok.Line };
        var args = new List<JNode>();
        if (IsOp("("))
        {
            _p++;
            while (true)
            {
                args.Add(ParsePipe());
                if (IsOp(";")) { _p++; continue; }
                ExpectOp(")");
                break;
            }
        }
        return new JCall(tok.Text, args.ToArray()) { Line = tok.Line };
    }

    // ───────────── strings, objects, patterns ─────────────

    private JNode ParseStringToken(JToken tok, string? format)
    {
        var parts = new List<object>();
        foreach (var part in tok.StringParts!)
        {
            if (part is string s) parts.Add(s);
            else if (part is JInterpolationSource src)
            {
                var inner = new JqParser(src.Source, JqLexer.Lex(src.Source));
                var node = inner.ParsePipe();
                if (inner.Peek.Kind != JTok.End) throw inner.Unexpected(inner.Peek);
                parts.Add(node);
            }
        }
        // A plain literal string folds to a constant.
        if (format is null && parts.Count == 1 && parts[0] is string only) return new JLiteral(only) { Line = tok.Line };
        return new JString(format, parts) { Line = tok.Line };
    }

    private JNode ParseObject()
    {
        int line = Peek.Line;
        ExpectOp("{");
        var entries = new List<(JNode, JNode)>();
        if (IsOp("}")) { _p++; return new JObjectCons(entries) { Line = line }; }
        while (true)
        {
            JNode key;
            JNode? value = null;
            var tok = Peek;
            switch (tok.Kind)
            {
                case JTok.Var:
                    _p++;
                    if (tok.Text == "__loc__") { key = new JLiteral("__loc__"); value = new JVar("__loc__"); }
                    else { key = new JLiteral(tok.Text); value = new JVar(tok.Text); }
                    break;
                case JTok.Ident:
                case JTok.Keyword:
                    _p++;
                    key = new JLiteral(tok.Text);
                    break;
                case JTok.Number:
                    _p++;
                    key = new JLiteral(tok.Text);
                    break;
                case JTok.String:
                    _p++;
                    key = ParseStringToken(tok, null);
                    break;
                case JTok.Format:
                    {
                        _p++;
                        if (Peek.Kind != JTok.String) throw Unexpected(Peek);
                        key = ParseStringToken(Next(), tok.Text);
                        break;
                    }
                case JTok.Op when tok.Text == "(":
                    {
                        _p++;
                        key = ParsePipe();
                        ExpectOp(")");
                        if (key is JLiteral { Value: not string } constant)
                            throw new JqCompileException($"Cannot use {JqInterp.Describe(constant.Value is JqNumber n ? (object)n.Value : constant.Value)} as object key at <top-level>, line {tok.Line}:");
                        break;
                    }
                default:
                    throw Unexpected(tok);
            }

            if (IsOp(":"))
            {
                _p++;
                value = ParseObjectValue();
            }
            else if (value is null)
            {
                // {a} / {"a b"} shorthand: the value is `.[key]`.
                value = new JIndex(new JIdentity(), key);
            }
            entries.Add((key, value));
            if (IsOp(",")) { _p++; continue; }
            ExpectOp("}");
            break;
        }
        return new JObjectCons(entries) { Line = line };
    }

    /// <summary>ExpD: alternative-level expressions joined by <c>|</c> (a <c>,</c> ends the entry).</summary>
    private JNode ParseObjectValue()
    {
        var lhs = ParseAlt();
        while (IsOp("|"))
        {
            int line = Peek.Line;
            _p++;
            lhs = new JPipe(lhs, ParseAlt()) { Line = line };
        }
        return lhs;
    }

    private JPattern ParsePattern()
    {
        var tok = Peek;
        if (tok.Kind == JTok.Var) { _p++; return new JPVar(tok.Text); }
        if (IsOp("["))
        {
            _p++;
            var elements = new List<JPattern>();
            while (true)
            {
                elements.Add(ParsePattern());
                if (IsOp(",")) { _p++; continue; }
                ExpectOp("]");
                break;
            }
            return new JPArray(elements);
        }
        if (IsOp("{"))
        {
            _p++;
            var entries = new List<JPObjectEntry>();
            while (true)
            {
                var entry = new JPObjectEntry();
                var k = Peek;
                if (k.Kind == JTok.Var)
                {
                    _p++;
                    entry.Var = k.Text;
                    entry.Key = new JLiteral(k.Text);
                    if (IsOp(":")) { _p++; entry.Sub = ParsePattern(); }
                }
                else
                {
                    if (k.Kind == JTok.Ident || k.Kind == JTok.Keyword) { _p++; entry.Key = new JLiteral(k.Text); }
                    else if (k.Kind == JTok.String) { _p++; entry.Key = ParseStringToken(k, null); }
                    else if (IsOp("(")) { _p++; entry.Key = ParsePipe(); ExpectOp(")"); }
                    else throw Unexpected(k);
                    ExpectOp(":");
                    entry.Sub = ParsePattern();
                }
                entries.Add(entry);
                if (IsOp(",")) { _p++; continue; }
                ExpectOp("}");
                break;
            }
            return new JPObject(entries);
        }
        throw Unexpected(tok);
    }
}
