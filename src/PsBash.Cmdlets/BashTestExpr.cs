using System.Globalization;

namespace PsBash.Cmdlets;

/// <summary>A <c>test</c> / <c>[</c> usage error: the message is bash's wording, the exit status is 2.</summary>
internal sealed class TestSyntaxException : Exception
{
    public TestSyntaxException(string message) : base(message) { }
}

/// <summary>
/// The evaluator of bash's <c>test</c> / <c>[</c> builtin (a port of bash 5.2 <c>test.c</c>, which is
/// argument-count driven, NOT a getopt scan): there are NO options — every word, dash-leading or not,
/// is part of the expression. 0 words = false, 1 word = non-empty, 2/3/4 words have fixed shapes
/// (<c>! x</c>, <c>-e x</c>, <c>a = b</c>, <c>( x )</c>, <c>! a = b</c>), and five or more fall to the
/// recursive-descent grammar <c>-o</c> &gt; <c>-a</c> &gt; <c>!</c> &gt; primary with <c>( )</c>.
/// Pure: file and variable predicates arrive as delegates so every branch is unit-testable.
/// </summary>
internal sealed class BashTestExpr
{
    private readonly string[] _a;
    private readonly Func<string, string, bool> _unary;
    private readonly Func<string, string, string, bool> _fileBinary;
    private int _pos;

    private BashTestExpr(string[] args, Func<string, string, bool> unary, Func<string, string, string, bool> fileBinary)
    {
        _a = args;
        _unary = unary;
        _fileBinary = fileBinary;
    }

    /// <summary>
    /// Evaluates <paramref name="args"/> (the words between <c>test</c> / <c>[</c> and the closing
    /// <c>]</c>). <paramref name="unary"/>(op, operand) answers every <c>-X</c> predicate except
    /// <c>-n</c> / <c>-z</c> (handled here); <paramref name="fileBinary"/>(op, l, r) answers
    /// <c>-nt -ot -ef</c>. Throws <see cref="TestSyntaxException"/> for a malformed expression.
    /// </summary>
    public static bool Eval(
        string[] args,
        Func<string, string, bool> unary,
        Func<string, string, string, bool> fileBinary)
    {
        var e = new BashTestExpr(args, unary, fileBinary);
        bool value;
        switch (args.Length)
        {
            case 0: return false;
            case 1: return args[0].Length > 0;
            case 2: value = e.TwoArguments(); break;
            case 3: value = e.ThreeArguments(); break;
            case 4: value = e.FourArguments(); break;
            default: value = e.Expr(); break;
        }
        if (e._pos != args.Length)
        {
            // bash names the leftover word when it is option-shaped (`-n`, `-eq`, `--`, `-foo`);
            // a plain word, `=`, `(` or `!` is just "too many arguments".
            string left = args[e._pos];
            bool op = left.Length > 1 && left[0] == '-';
            throw new TestSyntaxException(op ? $"syntax error: `{left}' unexpected" : "too many arguments");
        }
        return value;
    }

    // ---- operator tables -------------------------------------------------------------------

    internal static bool IsBinaryOp(string s) => s switch
    {
        "=" or "==" or "!=" or "<" or ">" or "-nt" or "-ot" or "-ef"
            or "-eq" or "-ne" or "-lt" or "-le" or "-gt" or "-ge" => true,
        _ => false,
    };

    /// <summary>bash's <c>test_unop</c>: a dash and exactly one of these letters.</summary>
    internal static bool IsUnaryOp(string s) =>
        s.Length == 2 && s[0] == '-' && "abcdefghknoprstuvwxzGLNORS".IndexOf(s[1]) >= 0;

    private static bool IsWord(string s, string word) => string.Equals(s, word, StringComparison.Ordinal);

    // ---- fixed-shape forms -----------------------------------------------------------------

    private void Advance(bool needMore)
    {
        _pos++;
        if (_pos > _a.Length || (needMore && _pos >= _a.Length)) throw new TestSyntaxException("argument expected");
    }

    private static bool OneArg(string s) => s.Length > 0;

    private bool TwoArguments()
    {
        bool value;
        if (IsWord(_a[_pos], "!")) value = !OneArg(_a[_pos + 1]);
        else if (IsUnaryOp(_a[_pos])) value = UnaryOperator();
        else throw new TestSyntaxException($"{_a[_pos]}: unary operator expected");
        _pos = _a.Length;
        return value;
    }

    private bool ThreeArguments()
    {
        bool value;
        if (IsBinaryOp(_a[_pos + 1]))
        {
            value = BinaryOperator();
            _pos = _a.Length;
        }
        else if (IsWord(_a[_pos + 1], "-a"))
        {
            value = OneArg(_a[_pos]) && OneArg(_a[_pos + 2]);
            _pos = _a.Length;
        }
        else if (IsWord(_a[_pos + 1], "-o"))
        {
            value = OneArg(_a[_pos]) || OneArg(_a[_pos + 2]);
            _pos = _a.Length;
        }
        else if (IsWord(_a[_pos], "!"))
        {
            Advance(true);
            value = !TwoArguments();
            _pos = _a.Length;
        }
        else if (IsWord(_a[_pos], "(") && IsWord(_a[_pos + 2], ")"))
        {
            value = OneArg(_a[_pos + 1]);
            _pos = _a.Length;
        }
        else throw new TestSyntaxException($"{_a[_pos + 1]}: binary operator expected");
        _pos = _a.Length;
        return value;
    }

    private bool FourArguments()
    {
        if (IsWord(_a[_pos], "!"))
        {
            Advance(true);
            var v = !ThreeArguments();
            _pos = _a.Length;
            return v;
        }
        if (IsWord(_a[_pos], "(") && IsWord(_a[_pos + 3], ")"))
        {
            Advance(true);
            var v = TwoArguments();
            _pos = _a.Length;
            return v;
        }
        return Expr();
    }

    // ---- the general grammar ---------------------------------------------------------------

    private bool Expr()
    {
        if (_pos >= _a.Length) throw new TestSyntaxException("argument expected");
        return Or();
    }

    /// <summary>
    /// One recursion level of the evaluator (parenthesised groups, `!`). This runs at RUN time on
    /// the host's pipeline thread, so a deep `[ ( ( … ) ) ]` or a long `-o` chain overflowed the stack and
    /// killed the shared host; NestingGuard turns it into the test command's own syntax error.
    /// </summary>
    private static PsBash.Core.Parser.NestingGuard.Scope Nest()
    {
        try { return PsBash.Core.Parser.NestingGuard.Enter(); }
        catch (PsBash.Core.Parser.ParseException) { throw new TestSyntaxException("expression too deeply nested"); }
    }

    // -o / -a chains are LOOPS, not recursion (they recursed once per operator, so a long chain
    // overflowed the host's stack). || and && are associative and nothing short-circuits — every
    // operand is still parsed — so the value is unchanged.
    private bool Or()
    {
        bool value = And();
        while (_pos < _a.Length && IsWord(_a[_pos], "-o"))
        {
            Advance(false);
            bool v2 = And(); // no short-circuit: the rest must still parse
            value = value || v2;
        }
        return value;
    }

    private bool And()
    {
        bool value = Term();
        while (_pos < _a.Length && IsWord(_a[_pos], "-a"))
        {
            Advance(false);
            bool v2 = Term();
            value = value && v2;
        }
        return value;
    }

    private bool Term()
    {
        using var nesting = Nest();
        if (_pos >= _a.Length) throw new TestSyntaxException("argument expected");

        if (IsWord(_a[_pos], "!"))
        {
            bool negate = false;
            while (_pos < _a.Length && IsWord(_a[_pos], "!"))
            {
                Advance(true);
                negate = !negate;
            }
            bool t = Term();
            return negate ? !t : t;
        }

        bool value;
        if (IsWord(_a[_pos], "("))
        {
            Advance(true);
            value = Or();
            if (_pos >= _a.Length || !IsWord(_a[_pos], ")"))
                throw new TestSyntaxException(_pos >= _a.Length
                    ? "`)' expected"
                    : $"`)' expected, found {_a[_pos]}");
            Advance(false);
        }
        else if (_pos + 3 <= _a.Length && IsBinaryOp(_a[_pos + 1]))
        {
            value = BinaryOperator();
        }
        else if (_pos + 1 < _a.Length && IsUnaryOp(_a[_pos]))   // a trailing `-n` has no operand: it is a plain word
        {
            value = UnaryOperator();
        }
        else
        {
            value = _a[_pos].Length > 0;
            Advance(false);
        }
        return value;
    }

    // ---- operators -------------------------------------------------------------------------

    private bool UnaryOperator()
    {
        string op = _a[_pos];
        if (op[1] == 't')
        {
            // -t takes an optional fd argument.
            Advance(false);
            if (_pos < _a.Length)
            {
                if (TryParseInteger(_a[_pos], out _))
                {
                    Advance(false);
                    return _unary(op, _a[_pos - 1]);
                }
                return false;
            }
            return _unary(op, "1");
        }
        Advance(true);                 // operand must exist ("argument expected")
        string operand = _a[_pos];
        Advance(false);
        return op[1] switch
        {
            'n' => operand.Length > 0,
            'z' => operand.Length == 0,
            _ => _unary(op, operand),
        };
    }
    private bool BinaryOperator()
    {
        string l = _a[_pos];
        string op = _a[_pos + 1];
        string r = _a[_pos + 2];
        _pos += 3;
        switch (op)
        {
            case "=":
            case "==": return string.Equals(l, r, StringComparison.Ordinal);
            case "!=": return !string.Equals(l, r, StringComparison.Ordinal);
            case "<": return string.CompareOrdinal(l, r) < 0;
            case ">": return string.CompareOrdinal(l, r) > 0;
            case "-nt":
            case "-ot":
            case "-ef": return _fileBinary(op, l, r);
        }

        if (!TryParseInteger(l, out long a)) throw new TestSyntaxException($"{l}: integer expression expected");
        if (!TryParseInteger(r, out long b)) throw new TestSyntaxException($"{r}: integer expression expected");
        return op switch
        {
            "-eq" => a == b,
            "-ne" => a != b,
            "-lt" => a < b,
            "-le" => a <= b,
            "-gt" => a > b,
            _ => a >= b, // -ge
        };
    }

    /// <summary>bash's <c>legal_number</c>: optional surrounding blanks, optional sign, decimal digits.</summary>
    internal static bool TryParseInteger(string s, out long value)
    {
        value = 0;
        var t = s.Trim(' ', '\t', '\n');
        if (t.Length == 0) return false;
        int i = 0;
        if (t[0] == '+' || t[0] == '-') i = 1;
        if (i >= t.Length) return false;
        for (int k = i; k < t.Length; k++)
        {
            if (t[k] < '0' || t[k] > '9') return false;
        }
        return long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }
}
