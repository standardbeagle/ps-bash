using System.Globalization;

namespace PsBash.Cmdlets;

/// <summary>
/// A faithful evaluator for bash arithmetic expressions (the language inside
/// <c>$(( ))</c>, <c>(( ))</c>, and <c>for ((;;))</c>). Bash arithmetic is a
/// C-like, <b>integer-only</b> (64-bit signed) expression language whose operator
/// set and semantics differ from PowerShell's: integer (truncating) division,
/// <c>**</c> exponentiation, C bitwise/shift operators, comparisons and logicals
/// that yield <c>1</c>/<c>0</c> (not <c>True</c>/<c>False</c>), the ternary
/// operator, comma sequencing, pre/post increment, and assignment forms. The old
/// transpiler wrapped the raw expression in a PowerShell <c>$( )</c> subexpression,
/// which mistranslated all of the above (e.g. <c>$((2**10))</c> was a parse error,
/// <c>$((20/3))</c> gave <c>6.66…</c>, <c>$((5&gt;3))</c> gave <c>True</c>). This
/// evaluator computes the bash-correct result directly.
///
/// Variable handling is supplied by the host via <see cref="Read"/> /
/// <see cref="Write"/> delegates, so the same evaluator works against the live
/// runspace (environment + PowerShell variables) and against an in-memory map in
/// tests. Bash resolves a bare identifier to its value, treating unset (or
/// non-numeric) as <c>0</c>, and recursively evaluates a value that is itself an
/// arithmetic string — the <see cref="Read"/> delegate owns that policy.
/// </summary>
public static class BashArith
{
    /// <summary>
    /// Evaluate <paramref name="expression"/> and return the 64-bit integer
    /// result. <paramref name="read"/> resolves a bare identifier to a value;
    /// <paramref name="write"/> persists an assignment. Throws
    /// <see cref="BashArithException"/> on a syntax error or a runtime error
    /// (division by zero, negative exponent) — matching the conditions under
    /// which bash itself fails an arithmetic evaluation.
    /// </summary>
    public static long Evaluate(string expression, Func<string, long> read, Action<string, long> write)
    {
        var parser = new Parser(expression ?? string.Empty, read, write);
        long result = parser.ParseExpression();
        parser.ExpectEnd();
        return result;
    }

    /// <summary>Thrown on a malformed or runtime-invalid arithmetic expression.</summary>
    public sealed class BashArithException : Exception
    {
        public BashArithException(string message) : base(message) { }
    }

    // ---- token kinds -------------------------------------------------------
    private enum T
    {
        Num, Ident, End,
        Plus, Minus, Star, Slash, Percent, StarStar,
        Amp, Pipe, Caret, Tilde, Shl, Shr,
        Lt, Le, Gt, Ge, EqEq, Ne,
        AndAnd, OrOr, Bang,
        Question, Colon, Comma,
        Assign, PlusEq, MinusEq, StarEq, SlashEq, PercentEq,
        ShlEq, ShrEq, AmpEq, PipeEq, CaretEq, StarStarEq,
        Inc, Dec, LParen, RParen,
    }

    private readonly struct Tok
    {
        public readonly T Kind;
        public readonly long Num;
        public readonly string? Text;
        public Tok(T kind, long num = 0, string? text = null) { Kind = kind; Num = num; Text = text; }
    }

    // ---- recursive-descent parser/evaluator --------------------------------
    private sealed class Parser
    {
        private readonly string _s;
        private readonly Func<string, long> _read;
        private readonly Action<string, long> _write;
        private readonly List<Tok> _toks;
        private int _pos;

        public Parser(string s, Func<string, long> read, Action<string, long> write)
        {
            _s = s;
            _read = read;
            _write = write;
            _toks = Tokenize(s);
            _pos = 0;
        }

        private Tok Cur => _toks[_pos];
        private Tok Next() => _toks[_pos++];
        private bool Accept(T k) { if (Cur.Kind == k) { _pos++; return true; } return false; }

        public void ExpectEnd()
        {
            if (Cur.Kind != T.End)
                throw new BashArithException($"unexpected token in arithmetic expression near '{_s}'");
        }

        // expression := comma  (lowest precedence)
        public long ParseExpression() => ParseComma();

        private long ParseComma()
        {
            long v = ParseAssignment();
            while (Cur.Kind == T.Comma) { _pos++; v = ParseAssignment(); }
            return v;
        }

        // assignment is right-associative and requires an lvalue (identifier).
        private long ParseAssignment()
        {
            // Look ahead: IDENT followed by an assignment operator.
            if (Cur.Kind == T.Ident)
            {
                var opKind = _toks[_pos + 1].Kind;
                if (IsAssignOp(opKind))
                {
                    string name = Cur.Text!;
                    _pos += 2; // consume ident + op
                    long rhs = ParseAssignment();
                    long cur = opKind == T.Assign ? 0 : _read(name);
                    long val = opKind switch
                    {
                        T.Assign => rhs,
                        T.PlusEq => cur + rhs,
                        T.MinusEq => cur - rhs,
                        T.StarEq => cur * rhs,
                        T.SlashEq => Div(cur, rhs),
                        T.PercentEq => Mod(cur, rhs),
                        T.ShlEq => cur << (int)rhs,
                        T.ShrEq => cur >> (int)rhs,
                        T.AmpEq => cur & rhs,
                        T.PipeEq => cur | rhs,
                        T.CaretEq => cur ^ rhs,
                        T.StarStarEq => Pow(cur, rhs),
                        _ => rhs,
                    };
                    _write(name, val);
                    return val;
                }
            }
            return ParseTernary();
        }

        private long ParseTernary()
        {
            long cond = ParseLogicalOr();
            if (Accept(T.Question))
            {
                // Both branches are full assignment-expressions; only the taken
                // branch's side effects occur (bash evaluates lazily).
                if (cond != 0)
                {
                    long t = ParseAssignment();
                    Expect(T.Colon);
                    ConsumeAssignment(); // discard untaken branch (no side effects)
                    return t;
                }
                else
                {
                    ConsumeAssignment();
                    Expect(T.Colon);
                    return ParseAssignment();
                }
            }
            return cond;
        }

        private long ParseLogicalOr()
        {
            long v = ParseLogicalAnd();
            while (Cur.Kind == T.OrOr)
            {
                _pos++;
                if (v != 0) { ConsumeBitOr(); v = 1; }
                else { long r = ParseLogicalAnd(); v = r != 0 ? 1 : 0; }
            }
            return v;
        }

        private long ParseLogicalAnd()
        {
            long v = ParseBitOr();
            while (Cur.Kind == T.AndAnd)
            {
                _pos++;
                if (v == 0) { ConsumeBitOr(); v = 0; }
                else { long r = ParseBitOr(); v = r != 0 ? 1 : 0; }
            }
            return v;
        }

        private long ParseBitOr()
        {
            long v = ParseBitXor();
            while (Accept(T.Pipe)) v |= ParseBitXor();
            return v;
        }

        private long ParseBitXor()
        {
            long v = ParseBitAnd();
            while (Accept(T.Caret)) v ^= ParseBitAnd();
            return v;
        }

        private long ParseBitAnd()
        {
            long v = ParseEquality();
            while (Accept(T.Amp)) v &= ParseEquality();
            return v;
        }

        private long ParseEquality()
        {
            long v = ParseRelational();
            while (true)
            {
                if (Accept(T.EqEq)) v = v == ParseRelational() ? 1 : 0;
                else if (Accept(T.Ne)) v = v != ParseRelational() ? 1 : 0;
                else break;
            }
            return v;
        }

        private long ParseRelational()
        {
            long v = ParseShift();
            while (true)
            {
                if (Accept(T.Lt)) v = v < ParseShift() ? 1 : 0;
                else if (Accept(T.Le)) v = v <= ParseShift() ? 1 : 0;
                else if (Accept(T.Gt)) v = v > ParseShift() ? 1 : 0;
                else if (Accept(T.Ge)) v = v >= ParseShift() ? 1 : 0;
                else break;
            }
            return v;
        }

        private long ParseShift()
        {
            long v = ParseAdditive();
            while (true)
            {
                if (Accept(T.Shl)) v <<= (int)ParseAdditive();
                else if (Accept(T.Shr)) v >>= (int)ParseAdditive();
                else break;
            }
            return v;
        }

        private long ParseAdditive()
        {
            long v = ParseMultiplicative();
            while (true)
            {
                if (Accept(T.Plus)) v += ParseMultiplicative();
                else if (Accept(T.Minus)) v -= ParseMultiplicative();
                else break;
            }
            return v;
        }

        private long ParseMultiplicative()
        {
            long v = ParseExponent();
            while (true)
            {
                if (Accept(T.Star)) v *= ParseExponent();
                else if (Accept(T.Slash)) v = Div(v, ParseExponent());
                else if (Accept(T.Percent)) v = Mod(v, ParseExponent());
                else break;
            }
            return v;
        }

        // ** is right-associative and binds tighter than * but looser than unary.
        private long ParseExponent()
        {
            long b = ParseUnary();
            if (Accept(T.StarStar))
            {
                long e = ParseExponent(); // right-assoc
                return Pow(b, e);
            }
            return b;
        }

        private long ParseUnary()
        {
            switch (Cur.Kind)
            {
                case T.Plus: _pos++; return ParseUnary();
                case T.Minus: _pos++; return -ParseUnary();
                case T.Bang: _pos++; return ParseUnary() == 0 ? 1 : 0;
                case T.Tilde: _pos++; return ~ParseUnary();
                case T.Inc: _pos++; return PreIncDec(+1);
                case T.Dec: _pos++; return PreIncDec(-1);
                default: return ParsePostfix();
            }
        }

        private long PreIncDec(int delta)
        {
            if (Cur.Kind != T.Ident)
                throw new BashArithException("++/-- requires a variable");
            string name = Next().Text!;
            long v = _read(name) + delta;
            _write(name, v);
            return v;
        }

        private long ParsePostfix()
        {
            // ident++ / ident-- : value is the OLD value.
            if (Cur.Kind == T.Ident && (_toks[_pos + 1].Kind == T.Inc || _toks[_pos + 1].Kind == T.Dec))
            {
                string name = Cur.Text!;
                int delta = _toks[_pos + 1].Kind == T.Inc ? +1 : -1;
                _pos += 2;
                long old = _read(name);
                _write(name, old + delta);
                return old;
            }
            return ParsePrimary();
        }

        private long ParsePrimary()
        {
            var t = Cur;
            switch (t.Kind)
            {
                case T.Num: _pos++; return t.Num;
                case T.Ident: _pos++; return _read(t.Text!);
                case T.LParen:
                    _pos++;
                    long v = ParseExpression();
                    Expect(T.RParen);
                    return v;
                default:
                    throw new BashArithException($"unexpected token in arithmetic expression near '{_s}'");
            }
        }

        private void Expect(T k)
        {
            if (!Accept(k))
                throw new BashArithException($"expected '{k}' in arithmetic expression near '{_s}'");
        }

        // ---- skip helpers for untaken short-circuit / ternary branches ----------
        // An untaken branch must parse for STRUCTURE (to balance parens and find
        // the right delimiter) but must NOT produce side effects, so we consume
        // tokens without evaluating. The discarded value is irrelevant.
        private void ConsumeAssignment()
        {
            // Stop at the end of a ternary branch / comma element / subexpression.
            int depth = 0;
            while (true)
            {
                var k = Cur.Kind;
                if (depth == 0 && (k == T.Colon || k == T.Comma || k == T.End || k == T.RParen)) break;
                if (k == T.LParen) depth++;
                else if (k == T.RParen) depth--;
                _pos++;
            }
        }
        private void ConsumeBitOr()
        {
            // Stop at a lower-or-equal-precedence boundary (the skipped operand of
            // a short-circuited && / ||).
            int depth = 0;
            while (true)
            {
                var k = Cur.Kind;
                if (depth == 0 && (k == T.AndAnd || k == T.OrOr || k == T.Question || k == T.Colon
                                   || k == T.Comma || k == T.End || k == T.RParen)) break;
                if (k == T.LParen) depth++;
                else if (k == T.RParen) depth--;
                _pos++;
            }
        }

        // ---- integer ops with bash semantics -----------------------------------
        private static long Div(long a, long b)
        {
            if (b == 0) throw new BashArithException("division by 0");
            return a / b; // C# long division truncates toward zero, matching bash
        }
        private static long Mod(long a, long b)
        {
            if (b == 0) throw new BashArithException("division by 0");
            return a % b;
        }
        private static long Pow(long b, long e)
        {
            if (e < 0) return 0; // bash errors on negative exponent; degrade to 0
            long r = 1;
            for (long i = 0; i < e; i++) r *= b;
            return r;
        }

        // ---- lexer -------------------------------------------------------------
        private static bool IsAssignOp(T k) => k is T.Assign or T.PlusEq or T.MinusEq or T.StarEq
            or T.SlashEq or T.PercentEq or T.ShlEq or T.ShrEq or T.AmpEq or T.PipeEq or T.CaretEq or T.StarStarEq;

        private static List<Tok> Tokenize(string s)
        {
            var toks = new List<Tok>();
            int i = 0, n = s.Length;
            while (i < n)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }

                // identifiers (and bare variable names)
                if (c == '_' || char.IsLetter(c))
                {
                    int start = i;
                    while (i < n && (s[i] == '_' || char.IsLetterOrDigit(s[i]))) i++;
                    toks.Add(new Tok(T.Ident, text: s[start..i]));
                    continue;
                }

                // numbers: base#digits, 0x.., 0.. (octal), decimal
                if (char.IsDigit(c))
                {
                    int start = i;
                    while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] == '#')) i++;
                    toks.Add(new Tok(T.Num, num: ParseNumber(s[start..i])));
                    continue;
                }

                // multi-char operators (longest first)
                T k;
                if (Match3(s, i, "<<=", out k) || Match3(s, i, ">>=", out k) || Match3(s, i, "**=", out k))
                { toks.Add(new Tok(k)); i += 3; continue; }
                if (Match2(s, i, out k, out int len))
                { toks.Add(new Tok(k)); i += len; continue; }

                switch (c)
                {
                    case '+': toks.Add(new Tok(T.Plus)); break;
                    case '-': toks.Add(new Tok(T.Minus)); break;
                    case '*': toks.Add(new Tok(T.Star)); break;
                    case '/': toks.Add(new Tok(T.Slash)); break;
                    case '%': toks.Add(new Tok(T.Percent)); break;
                    case '&': toks.Add(new Tok(T.Amp)); break;
                    case '|': toks.Add(new Tok(T.Pipe)); break;
                    case '^': toks.Add(new Tok(T.Caret)); break;
                    case '~': toks.Add(new Tok(T.Tilde)); break;
                    case '<': toks.Add(new Tok(T.Lt)); break;
                    case '>': toks.Add(new Tok(T.Gt)); break;
                    case '!': toks.Add(new Tok(T.Bang)); break;
                    case '?': toks.Add(new Tok(T.Question)); break;
                    case ':': toks.Add(new Tok(T.Colon)); break;
                    case ',': toks.Add(new Tok(T.Comma)); break;
                    case '=': toks.Add(new Tok(T.Assign)); break;
                    case '(': toks.Add(new Tok(T.LParen)); break;
                    case ')': toks.Add(new Tok(T.RParen)); break;
                    case '$': i++; continue; // a stray $ before a name: ignore (var already a name)
                    default:
                        throw new BashArithException($"invalid arithmetic character '{c}'");
                }
                i++;
            }
            toks.Add(new Tok(T.End));
            return toks;
        }

        private static bool Match3(string s, int i, string op, out T k)
        {
            k = default;
            if (i + 3 > s.Length || s[i] != op[0] || s[i + 1] != op[1] || s[i + 2] != op[2]) return false;
            k = op switch { "<<=" => T.ShlEq, ">>=" => T.ShrEq, "**=" => T.StarStarEq, _ => default };
            return true;
        }

        private static bool Match2(string s, int i, out T k, out int len)
        {
            k = default; len = 2;
            if (i + 2 > s.Length) return false;
            string two = s.Substring(i, 2);
            k = two switch
            {
                "**" => T.StarStar, "<<" => T.Shl, ">>" => T.Shr,
                "<=" => T.Le, ">=" => T.Ge, "==" => T.EqEq, "!=" => T.Ne,
                "&&" => T.AndAnd, "||" => T.OrOr,
                "++" => T.Inc, "--" => T.Dec,
                "+=" => T.PlusEq, "-=" => T.MinusEq, "*=" => T.StarEq, "/=" => T.SlashEq,
                "%=" => T.PercentEq, "&=" => T.AmpEq, "|=" => T.PipeEq, "^=" => T.CaretEq,
                _ => T.End,
            };
            return k != T.End;
        }

        internal static long ParseNumber(string tok)
        {
            // base#digits (base 2..64)
            int hash = tok.IndexOf('#');
            if (hash > 0)
            {
                if (!int.TryParse(tok[..hash], NumberStyles.None, CultureInfo.InvariantCulture, out int radix)
                    || radix < 2 || radix > 64)
                    throw new BashArithException($"invalid arithmetic base in '{tok}'");
                long acc = 0;
                foreach (char d in tok[(hash + 1)..])
                {
                    int dv = DigitValue(d);
                    if (dv < 0 || dv >= radix) throw new BashArithException($"invalid digit '{d}' for base {radix}");
                    acc = acc * radix + dv;
                }
                return acc;
            }
            if (tok.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return Convert.ToInt64(tok[2..], 16);
            // leading-zero octal (but plain "0" is zero)
            if (tok.Length > 1 && tok[0] == '0')
                return Convert.ToInt64(tok, 8);
            if (!long.TryParse(tok, NumberStyles.None, CultureInfo.InvariantCulture, out long val))
                throw new BashArithException($"invalid arithmetic number '{tok}'");
            return val;
        }

        // bash base#digits digit set: 0-9, a-z (10..35), A-Z (36..61), @ (62), _ (63)
        private static int DigitValue(char d)
        {
            if (d >= '0' && d <= '9') return d - '0';
            if (d >= 'a' && d <= 'z') return d - 'a' + 10;
            if (d >= 'A' && d <= 'Z') return d - 'A' + 36;
            if (d == '@') return 62;
            if (d == '_') return 63;
            return -1;
        }
    }
}
