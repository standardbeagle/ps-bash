using System.Globalization;
using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Parser;

/// <summary>Parses bash arithmetic text without evaluating it or touching variables.</summary>
public sealed class BashArithmeticParser
{
    private enum T
    {
        Num, Ident, Parameter, End, Plus, Minus, Star, Slash, Percent, StarStar,
        Amp, Pipe, Caret, Tilde, Shl, Shr, Lt, Le, Gt, Ge, EqEq, Ne,
        AndAnd, OrOr, Bang, Question, Colon, Comma, Assign, PlusEq,
        MinusEq, StarEq, SlashEq, PercentEq, ShlEq, ShrEq, AmpEq, PipeEq,
        CaretEq, StarStarEq, Inc, Dec, LParen, RParen,
    }

    private readonly record struct Tok(T Kind, long Number = 0, string? Text = null, string? Key = null, string? Suffix = null);
    private readonly string _source;
    private readonly List<Tok> _tokens;
    private int _position;

    private BashArithmeticParser(string source)
    {
        _source = source;
        _tokens = Tokenize(source);
    }

    public static ArithmeticSyntax Parse(string? source)
    {
        var parser = new BashArithmeticParser(source ?? string.Empty);
        ArithmeticExpr root = parser.ParseComma();
        if (parser.Current.Kind != T.End) parser.Error("unexpected token");
        return new ArithmeticSyntax(parser._source, root);
    }

    private Tok Current => _tokens[_position];
    private bool Accept(T kind) { if (Current.Kind != kind) return false; _position++; return true; }
    private void Expect(T kind) { if (!Accept(kind)) Error($"expected '{kind}'"); }
    private void Error(string message) => throw new BashArithmeticParseException($"{message} in arithmetic expression near '{_source}'");

    private ArithmeticExpr ParseComma()
    {
        var value = ParseAssignment();
        while (Accept(T.Comma)) value = new ArithmeticExpr.Binary(ArithmeticBinaryOp.Comma, value, ParseAssignment());
        return value;
    }

    private ArithmeticExpr ParseAssignment()
    {
        if (Current.Kind == T.Ident && TryAssignmentOp(_tokens[_position + 1].Kind, out var op))
        {
            string name = Current.Text!;
            _position += 2;
            return new ArithmeticExpr.Assignment(name, op, ParseAssignment());
        }
        return ParseConditional();
    }

    private ArithmeticExpr ParseConditional()
    {
        var condition = ParseLogicalOr();
        if (!Accept(T.Question)) return condition;
        // Bash follows C here: the middle operand is a full expression, so a
        // comma belongs to the true branch rather than the surrounding level.
        var whenTrue = ParseComma();
        Expect(T.Colon);
        return new ArithmeticExpr.Conditional(condition, whenTrue, ParseAssignment());
    }

    private ArithmeticExpr ParseLogicalOr() => ParseLeft(ParseLogicalAnd, (T.OrOr, ArithmeticBinaryOp.LogicalOr));
    private ArithmeticExpr ParseLogicalAnd() => ParseLeft(ParseBitOr, (T.AndAnd, ArithmeticBinaryOp.LogicalAnd));
    private ArithmeticExpr ParseBitOr() => ParseLeft(ParseBitXor, (T.Pipe, ArithmeticBinaryOp.BitwiseOr));
    private ArithmeticExpr ParseBitXor() => ParseLeft(ParseBitAnd, (T.Caret, ArithmeticBinaryOp.BitwiseXor));
    private ArithmeticExpr ParseBitAnd() => ParseLeft(ParseEquality, (T.Amp, ArithmeticBinaryOp.BitwiseAnd));
    private ArithmeticExpr ParseEquality() => ParseLeft(ParseRelational, (T.EqEq, ArithmeticBinaryOp.Equal), (T.Ne, ArithmeticBinaryOp.NotEqual));
    private ArithmeticExpr ParseRelational() => ParseLeft(ParseShift, (T.Lt, ArithmeticBinaryOp.Less), (T.Le, ArithmeticBinaryOp.LessOrEqual), (T.Gt, ArithmeticBinaryOp.Greater), (T.Ge, ArithmeticBinaryOp.GreaterOrEqual));
    private ArithmeticExpr ParseShift() => ParseLeft(ParseAdditive, (T.Shl, ArithmeticBinaryOp.ShiftLeft), (T.Shr, ArithmeticBinaryOp.ShiftRight));
    private ArithmeticExpr ParseAdditive() => ParseLeft(ParseMultiplicative, (T.Plus, ArithmeticBinaryOp.Add), (T.Minus, ArithmeticBinaryOp.Subtract));
    private ArithmeticExpr ParseMultiplicative() => ParseLeft(ParsePower, (T.Star, ArithmeticBinaryOp.Multiply), (T.Slash, ArithmeticBinaryOp.Divide), (T.Percent, ArithmeticBinaryOp.Modulo));

    private ArithmeticExpr ParseLeft(Func<ArithmeticExpr> operand, params (T Token, ArithmeticBinaryOp Op)[] operators)
    {
        var value = operand();
        while (true)
        {
            int index = Array.FindIndex(operators, pair => pair.Token == Current.Kind);
            if (index < 0) return value;
            _position++;
            value = new ArithmeticExpr.Binary(operators[index].Op, value, operand());
        }
    }

    private ArithmeticExpr ParsePower()
    {
        var value = ParseUnary();
        return Accept(T.StarStar)
            ? new ArithmeticExpr.Binary(ArithmeticBinaryOp.Power, value, ParsePower())
            : value;
    }

    private ArithmeticExpr ParseUnary()
    {
        if (Accept(T.Plus)) return new ArithmeticExpr.Unary(ArithmeticUnaryOp.Plus, ParseUnary());
        if (Accept(T.Minus)) return new ArithmeticExpr.Unary(ArithmeticUnaryOp.Negate, ParseUnary());
        if (Accept(T.Bang)) return new ArithmeticExpr.Unary(ArithmeticUnaryOp.LogicalNot, ParseUnary());
        if (Accept(T.Tilde)) return new ArithmeticExpr.Unary(ArithmeticUnaryOp.BitwiseNot, ParseUnary());
        if (Accept(T.Inc)) return ParsePrefixIncrement(1);
        if (Accept(T.Dec)) return ParsePrefixIncrement(-1);
        return ParsePostfix();
    }

    private ArithmeticExpr ParsePrefixIncrement(int delta)
    {
        if (Current.Kind != T.Ident) Error("++/-- requires a variable");
        return new ArithmeticExpr.Increment(_tokens[_position++].Text!, delta, true);
    }

    private ArithmeticExpr ParsePostfix()
    {
        if (Current.Kind == T.Ident && _tokens[_position + 1].Kind is T.Inc or T.Dec)
        {
            string name = Current.Text!;
            int delta = _tokens[_position + 1].Kind == T.Inc ? 1 : -1;
            _position += 2;
            return new ArithmeticExpr.Increment(name, delta, false);
        }
        return ParsePrimary();
    }

    private ArithmeticExpr ParsePrimary()
    {
        if (Current.Kind == T.Num) return new ArithmeticExpr.Number(_tokens[_position++].Number);
        if (Current.Kind == T.Ident) return new ArithmeticExpr.Identifier(_tokens[_position++].Text!);
        if (Current.Kind == T.Parameter)
        {
            Tok parameter = _tokens[_position++];
            return new ArithmeticExpr.Parameter(parameter.Key!, parameter.Text!, parameter.Suffix ?? "");
        }
        if (Accept(T.LParen))
        {
            var value = ParseComma();
            Expect(T.RParen);
            return value;
        }
        Error("unexpected token");
        throw new InvalidOperationException();
    }

    private static bool TryAssignmentOp(T token, out ArithmeticAssignmentOp op)
    {
        op = token switch
        {
            T.Assign => ArithmeticAssignmentOp.Assign, T.PlusEq => ArithmeticAssignmentOp.Add,
            T.MinusEq => ArithmeticAssignmentOp.Subtract, T.StarEq => ArithmeticAssignmentOp.Multiply,
            T.SlashEq => ArithmeticAssignmentOp.Divide, T.PercentEq => ArithmeticAssignmentOp.Modulo,
            T.ShlEq => ArithmeticAssignmentOp.ShiftLeft, T.ShrEq => ArithmeticAssignmentOp.ShiftRight,
            T.AmpEq => ArithmeticAssignmentOp.BitwiseAnd, T.PipeEq => ArithmeticAssignmentOp.BitwiseOr,
            T.CaretEq => ArithmeticAssignmentOp.BitwiseXor, T.StarStarEq => ArithmeticAssignmentOp.Power,
            _ => default,
        };
        return token is >= T.Assign and <= T.StarStarEq;
    }

    private static List<Tok> Tokenize(string source)
    {
        var result = new List<Tok>();
        for (int i = 0; i < source.Length;)
        {
            char c = source[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '_' || char.IsLetter(c))
            {
                int start = i++;
                while (i < source.Length && (source[i] == '_' || char.IsLetterOrDigit(source[i]))) i++;
                result.Add(new Tok(T.Ident, Text: source[start..i])); continue;
            }
            if (char.IsDigit(c))
            {
                int start = i++;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] is '#' or '@' or '_')) i++;
                string text = source[start..i];
                result.Add(new Tok(T.Num, ParseNumber(text))); continue;
            }
            if (c == '$')
            {
                int start = i++;
                if (i >= source.Length) throw new BashArithmeticParseException("invalid arithmetic parameter '$'");
                if (source[i] == '{')
                {
                    int keyStart = ++i;
                    while (i < source.Length && source[i] != '}') i++;
                    if (i >= source.Length || i == keyStart)
                        throw new BashArithmeticParseException($"invalid arithmetic parameter near '{source[start..]}'");
                    string key = source[keyStart..i];
                    // Array-length expansion is valid inside arithmetic contexts
                    // (for example `${#arr[@]} + 1`). Preserve it as an opaque
                    // parameter lookup so the typed arithmetic parser does not
                    // reject an otherwise valid bash command.
                    if (!IsParameterKey(key) && !IsArrayLengthParameterKey(key))
                        throw new BashArithmeticParseException($"invalid arithmetic parameter '${{{key}}}'");
                    i++;
                    result.Add(new Tok(T.Parameter, Text: source[start..i], Key: key));
                    continue;
                }
                if (char.IsDigit(source[i]))
                {
                    string key = source[i++].ToString();
                    int suffixStart = i;
                    while (i < source.Length && char.IsDigit(source[i])) i++;
                    result.Add(new Tok(T.Parameter, Text: source[start..i], Key: key, Suffix: source[suffixStart..i]));
                    continue;
                }
                if (source[i] is '#' or '?' or '@' or '*' or '$' or '!')
                {
                    string key = source[i++].ToString();
                    result.Add(new Tok(T.Parameter, Text: source[start..i], Key: key));
                    continue;
                }
                if (source[i] == '_' || char.IsLetter(source[i]))
                {
                    i++;
                    while (i < source.Length && (source[i] == '_' || char.IsLetterOrDigit(source[i]))) i++;
                    result.Add(new Tok(T.Parameter, Text: source[start..i], Key: source[(start + 1)..i]));
                    continue;
                }
                throw new BashArithmeticParseException($"invalid arithmetic parameter near '{source[start..]}'");
            }
            if (i + 3 <= source.Length && TryOperator(source.Substring(i, 3), out var three)) { result.Add(new Tok(three)); i += 3; continue; }
            if (i + 2 <= source.Length && TryOperator(source.Substring(i, 2), out var two)) { result.Add(new Tok(two)); i += 2; continue; }
            T single = c switch
            {
                '+' => T.Plus, '-' => T.Minus, '*' => T.Star, '/' => T.Slash, '%' => T.Percent,
                '&' => T.Amp, '|' => T.Pipe, '^' => T.Caret, '~' => T.Tilde, '<' => T.Lt,
                '>' => T.Gt, '!' => T.Bang, '?' => T.Question, ':' => T.Colon, ',' => T.Comma,
                '=' => T.Assign, '(' => T.LParen, ')' => T.RParen, _ => T.End,
            };
            if (single == T.End) throw new BashArithmeticParseException($"invalid arithmetic character '{c}'");
            result.Add(new Tok(single)); i++;
        }
        result.Add(new Tok(T.End));
        return result;
    }

    private static bool IsParameterKey(string key) =>
        key.All(char.IsDigit)
        || key.Length == 1 && key[0] is '#' or '?' or '@' or '*' or '$' or '!'
        || (key[0] == '_' || char.IsLetter(key[0]))
           && key.Skip(1).All(c => c == '_' || char.IsLetterOrDigit(c));

    private static bool IsArrayLengthParameterKey(string key)
    {
        if (key.Length < 5 || key[0] != '#' || !key.EndsWith("]", StringComparison.Ordinal)) return false;
        int bracket = key.IndexOf('[', 1);
        if (bracket <= 1 || (key[1] != '_' && !char.IsLetter(key[1]))) return false;
        return key[1..bracket].All(c => c == '_' || char.IsLetterOrDigit(c))
            && key[(bracket + 1)..^1] is "@" or "*";
    }

    private static bool TryOperator(string text, out T token)
    {
        token = text switch
        {
            "<<=" => T.ShlEq, ">>=" => T.ShrEq, "**=" => T.StarStarEq,
            "**" => T.StarStar, "<<" => T.Shl, ">>" => T.Shr, "<=" => T.Le, ">=" => T.Ge,
            "==" => T.EqEq, "!=" => T.Ne, "&&" => T.AndAnd, "||" => T.OrOr,
            "++" => T.Inc, "--" => T.Dec, "+=" => T.PlusEq, "-=" => T.MinusEq,
            "*=" => T.StarEq, "/=" => T.SlashEq, "%=" => T.PercentEq, "&=" => T.AmpEq,
            "|=" => T.PipeEq, "^=" => T.CaretEq, _ => T.End,
        };
        return token != T.End;
    }

    internal static long ParseNumber(string token)
    {
        int hash = token.IndexOf('#');
        if (hash > 0)
        {
            if (!int.TryParse(token[..hash], NumberStyles.None, CultureInfo.InvariantCulture, out int radix) || radix is < 2 or > 64)
                throw new BashArithmeticParseException($"invalid arithmetic base in '{token}'");
            return ParseDigits(token[(hash + 1)..], radix, token, true);
        }
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return ParseDigits(token[2..], 16, token, false);
        if (token.Length > 1 && token[0] == '0') return ParseDigits(token, 8, token, false);
        if (!long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out long value))
            throw new BashArithmeticParseException($"invalid arithmetic number '{token}'");
        return value;
    }

    private static long ParseDigits(string digits, int radix, string token, bool bashDigits)
    {
        if (digits.Length == 0) throw new BashArithmeticParseException($"invalid arithmetic number '{token}'");
        long value = 0;
        foreach (char digit in digits)
        {
            int d = bashDigits ? BashDigit(digit) : HexDigit(digit);
            if (d < 0 || d >= radix) throw new BashArithmeticParseException($"invalid digit '{digit}' in '{token}'");
            value = unchecked(value * radix + d);
        }
        return value;
    }

    private static int HexDigit(char c) => c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;
    private static int BashDigit(char c) => c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'z' ? c - 'a' + 10 : c is >= 'A' and <= 'Z' ? c - 'A' + 36 : c == '@' ? 62 : c == '_' ? 63 : -1;
}

public sealed class BashArithmeticParseException : Exception
{
    public BashArithmeticParseException(string message) : base(message) { }
}
