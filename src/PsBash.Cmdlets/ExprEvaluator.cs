using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

/// <summary>
/// GNU <c>expr</c>'s grammar (coreutils 9.4 <c>expr.c</c>), one recursive-descent level per precedence, every binary
/// level LEFT-associative and looping over its operator (<c>1 + 2 + 3</c> is 6, <c>20 / 2 / 5</c> is 2):
/// <code>
/// or      := and   { '|' and }                         ARG1 unless it is null or 0, else ARG2 (0 when both are)
/// and     := cmp   { '&amp;' cmp }                         ARG1 unless either is null or 0, else 0
/// cmp     := add   { ('&lt;' | '&lt;=' | '=' | '==' | '!=' | '&gt;=' | '&gt;') add }   numeric when both are integers, else bytewise
/// add     := mul   { ('+' | '-') mul }                 integers only ("non-integer argument")
/// mul     := colon { ('*' | '/' | '%') colon }         integers only; "division by zero"
/// colon   := unary { ':' unary }                       anchored BRE: group 1, or the match length
/// unary   := '+' TOKEN | 'length' unary | 'match' unary unary | 'index' unary unary
///          | 'substr' unary unary unary | primary
/// primary := '(' or ')' | TOKEN
/// </code>
/// The right side of <c>|</c> / <c>&amp;</c> is parsed but not evaluated when the left side decides
/// (<c>expr 2 \| 1 / 0</c> is 2). A value is text; an integer result prints in canonical form while a literal keeps
/// its spelling (<c>expr 007</c> is 007, <c>expr 007 + 0</c> is 7). Pure: errors are an <see cref="ExprException"/>
/// carrying GNU's message and status (2 = syntax / invalid argument).
/// </summary>
internal sealed class ExprEvaluator
{
    internal sealed class ExprException(string message, int status) : Exception(message)
    {
        public int Status { get; } = status;
    }

    private readonly struct Value
    {
        public readonly string Text;
        public readonly BigInteger? Int;
        private Value(string text, BigInteger? i) { Text = text; Int = i; }
        public static Value Str(string s) => new(s, null);
        public static Value Num(BigInteger n) => new(n.ToString(CultureInfo.InvariantCulture), n);
    }

    private readonly string[] _args;
    private int _pos;

    private ExprEvaluator(string[] args) { _args = args; }

    /// <summary>The result text of <paramref name="args"/> (a leading <c>--</c> already removed); throws <see cref="ExprException"/>.</summary>
    public static string Evaluate(string[] args)
    {
        var e = new ExprEvaluator(args);
        var v = e.Or(true);
        if (e._pos < args.Length) throw Syntax($"unexpected argument {GnuQuote.Locale(args[e._pos])}");
        return v.Text;
    }

    /// <summary>GNU <c>null()</c>: the empty string, or an integer spelling of zero (<c>0</c>, <c>00</c>, <c>-0</c>).</summary>
    public static bool IsNull(string text)
    {
        if (text.Length == 0) return true;
        int i = text[0] == '-' ? 1 : 0;
        if (i >= text.Length) return false;
        for (; i < text.Length; i++) if (text[i] != '0') return false;
        return true;
    }

    private static ExprException Syntax(string what) => new("expr: syntax error: " + what, 2);

    private bool AtEnd => _pos >= _args.Length;

    private bool Next(string token)
    {
        if (AtEnd || !string.Equals(_args[_pos], token, StringComparison.Ordinal)) return false;
        _pos++;
        return true;
    }

    private bool NextAny(string[] tokens, out string op)
    {
        op = "";
        if (AtEnd) return false;
        foreach (var t in tokens)
            if (string.Equals(_args[_pos], t, StringComparison.Ordinal)) { op = t; _pos++; return true; }
        return false;
    }

    private static bool IsNull(Value v) => v.Int is { } n ? n.IsZero : IsNull(v.Text);

    /// <summary>GNU <c>looks_like_integer</c>: an optional <c>-</c> and at least one ASCII digit, nothing else.</summary>
    private static bool TryInt(Value v, out BigInteger n)
    {
        if (v.Int is { } i) { n = i; return true; }
        n = default;
        var s = v.Text;
        int k = s.Length > 0 && s[0] == '-' ? 1 : 0;
        if (k >= s.Length) return false;
        for (int j = k; j < s.Length; j++) if (s[j] is < '0' or > '9') return false;
        n = BigInteger.Parse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        return true;
    }

    private Value Or(bool evaluate)
    {
        var l = And(evaluate);
        while (Next("|"))
        {
            var r = And(evaluate && IsNull(l));
            if (IsNull(l)) l = IsNull(r) ? Value.Num(0) : r;
        }
        return l;
    }

    private Value And(bool evaluate)
    {
        var l = Compare(evaluate);
        while (Next("&"))
        {
            var r = Compare(evaluate && !IsNull(l));
            if (IsNull(l) || IsNull(r)) l = Value.Num(0);
        }
        return l;
    }

    private static readonly string[] CompareOps = ["<", "<=", "=", "==", "!=", ">=", ">"];
    private static readonly string[] AddOps = ["+", "-"];
    private static readonly string[] MulOps = ["*", "/", "%"];

    private Value Compare(bool evaluate)
    {
        var l = Add(evaluate);
        while (NextAny(CompareOps, out var op))
        {
            var r = Add(evaluate);
            if (!evaluate) continue;
            int c = TryInt(l, out var a) && TryInt(r, out var b) ? a.CompareTo(b) : string.CompareOrdinal(l.Text, r.Text);
            bool truth = op switch
            {
                "<" => c < 0, "<=" => c <= 0, "=" or "==" => c == 0, "!=" => c != 0, ">=" => c >= 0, _ => c > 0,
            };
            l = Value.Num(truth ? 1 : 0);
        }
        return l;
    }

    private Value Add(bool evaluate)
    {
        var l = Mul(evaluate);
        while (NextAny(AddOps, out var op))
        {
            var r = Mul(evaluate);
            if (!evaluate) continue;
            var (a, b) = Integers(l, r);
            l = Value.Num(op == "+" ? a + b : a - b);
        }
        return l;
    }

    private Value Mul(bool evaluate)
    {
        var l = Colon(evaluate);
        while (NextAny(MulOps, out var op))
        {
            var r = Colon(evaluate);
            if (!evaluate) continue;
            var (a, b) = Integers(l, r);
            if (op != "*" && b.IsZero) throw new ExprException("expr: division by zero", 2);
            // BigInteger division truncates toward zero and the remainder takes the dividend's sign, as C's.
            l = Value.Num(op == "*" ? a * b : op == "/" ? a / b : a % b);
        }
        return l;
    }

    private static (BigInteger, BigInteger) Integers(Value l, Value r)
    {
        if (!TryInt(l, out var a) || !TryInt(r, out var b)) throw new ExprException("expr: non-integer argument", 2);
        return (a, b);
    }

    private Value Colon(bool evaluate)
    {
        var l = Unary(evaluate);
        while (Next(":"))
        {
            var r = Unary(evaluate);
            if (evaluate) l = Match(l.Text, r.Text);
        }
        return l;
    }

    private Value Unary(bool evaluate)
    {
        if (Next("+"))
        {
            if (AtEnd) throw Syntax($"missing argument after {GnuQuote.Locale(_args[_pos - 1])}");
            return Value.Str(_args[_pos++]);
        }
        if (Next("length"))
        {
            var r = Unary(evaluate);
            return Value.Num(CharCount(r.Text));
        }
        if (Next("match"))
        {
            var l = Unary(evaluate);
            var r = Unary(evaluate);
            return evaluate ? Match(l.Text, r.Text) : Value.Num(0);
        }
        if (Next("index"))
        {
            var l = Unary(evaluate);
            var r = Unary(evaluate);
            return Value.Num(Index(l.Text, r.Text));
        }
        if (Next("substr"))
        {
            var s = Unary(evaluate);
            var p = Unary(evaluate);
            var n = Unary(evaluate);
            return Value.Str(Substr(s.Text, p, n));
        }
        return Primary(evaluate);
    }

    private Value Primary(bool evaluate)
    {
        if (AtEnd) throw Syntax($"missing argument after {GnuQuote.Locale(_args[_pos - 1])}");
        if (Next("("))
        {
            var v = Or(evaluate);
            if (AtEnd) throw Syntax($"expecting ')' after {GnuQuote.Locale(_args[_pos - 1])}");
            if (!Next(")")) throw Syntax($"expecting ')' instead of {GnuQuote.Locale(_args[_pos])}");
            return v;
        }
        if (Next(")")) throw Syntax("unexpected ')'");
        return Value.Str(_args[_pos++]);
    }

    /// <summary>Characters (code points; a raw-byte marker is one), as GNU's <c>mbslen</c> counts them.</summary>
    private static int CharCount(string s)
    {
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
            n++;
        }
        return n;
    }

    /// <summary>1-based position of the first character of <paramref name="s"/> that occurs in <paramref name="chars"/>, else 0.</summary>
    private static int Index(string s, string chars)
    {
        for (int i = 0; i < s.Length; i++)
            if (chars.IndexOf(s[i]) >= 0) return i + 1;
        return 0;
    }

    /// <summary><c>substr STRING POS LENGTH</c>: empty unless POS and LENGTH are integers with 1 &lt;= POS &lt;= length and LENGTH &gt; 0.</summary>
    private static string Substr(string s, Value pos, Value len)
    {
        if (!TryInt(pos, out var p) || !TryInt(len, out var n)) return "";
        if (p < 1 || p > s.Length || n < 1) return "";
        int start = (int)p - 1;
        int count = (int)BigInteger.Min(n, s.Length - start);
        return s.Substring(start, count);
    }

    /// <summary>
    /// <c>STRING : REGEX</c>: a POSIX BRE anchored at the start. With a <c>\(…\)</c> group the result is group 1 (empty
    /// when there is no match), else the number of characters matched (0 when none).
    /// </summary>
    private static Value Match(string str, string pattern)
    {
        var netPattern = BashRuntime.TranslateBre(BashRuntime.TranslatePosixClasses(pattern));
        if (!netPattern.StartsWith('^')) netPattern = "^" + netPattern;
        Regex rx;
        try { rx = new Regex(netPattern, RegexOptions.CultureInvariant); }
        catch (ArgumentException) { throw new ExprException("expr: invalid regular expression", 2); }
        var m = rx.Match(str);
        if (rx.GetGroupNumbers().Length > 1) return Value.Str(m.Success && m.Groups[1].Success ? m.Groups[1].Value : "");
        return Value.Num(m.Success ? CharCount(m.Value) : 0);
    }
}
