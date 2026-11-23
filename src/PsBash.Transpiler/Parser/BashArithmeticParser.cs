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
        CaretEq, Inc, Dec, LParen, RParen, CommandSub,
    }

    private readonly record struct Tok(T Kind, long Number = 0, string? Text = null, string? Key = null, string? Suffix = null);
    private readonly string _source;
    private readonly List<Tok> _tokens;
    // Where each token starts in _source: a syntax error names the text from there on, as bash does.
    private readonly List<int> _starts;
    private int _position;
    // Where a node's run-time error token starts in _source (null: not wanted). See RuntimeErrorTokens.
    private readonly IDictionary<ArithmeticExpr, int>? _errorTokens;

    private BashArithmeticParser(string source, IDictionary<ArithmeticExpr, int>? errorTokens)
    {
        _source = source;
        _errorTokens = errorTokens;
        _starts = new List<int>();
        _tokens = Tokenize(source, _starts);
    }

    public static ArithmeticSyntax Parse(string? source) => Parse(source, null);

    /// <summary>
    /// <see cref="Parse(string?)"/>, also filling <paramref name="errorTokens"/> (give it a reference-equality comparer:
    /// equal records are different nodes) with where each node that can fail when EVALUATED starts its error token in
    /// <see cref="ArithmeticSyntax.Source"/>. bash (expr.c) names the text from there to the end: <c>1 / 0 + 2</c> reports
    /// <c>division by 0 (error token is "0 + 2 ")</c>.
    /// <list type="bullet">
    /// <item><c>/</c> and <c>%</c> (division by 0): the right operand's first token (<c>10/(1-1)</c> -> "(1-1)").</item>
    /// <item><c>**</c> (exponent less than 0) and <c>/=</c>, <c>%=</c>: the token after the right operand, or
    /// at the end the last token read (<c>2 ** -1 + 3</c> -> "+ 3", <c>2**-1</c> -> "1").</item>
    /// </list>
    /// </summary>
    public static ArithmeticSyntax Parse(string? source, IDictionary<ArithmeticExpr, int>? errorTokens)
    {
        var parser = new BashArithmeticParser(StripDoubleQuotes(source), errorTokens);
        ArithmeticExpr root = parser.ParseComma();
        if (parser.Current.Kind != T.End) parser.Error("syntax error in expression");
        return new ArithmeticSyntax(parser._source, root);
    }

    /// <summary>Like <see cref="Parse(string?)"/>, but an expression the static grammar rejects becomes a <see cref="ArithmeticExpr.Deferred"/>
    /// node (bash expands its text and reports any syntax error only when it runs).</summary>
    public static ArithmeticSyntax ParseOrDefer(string? source)
    {
        try { return Parse(source); }
        // The resource limits are not syntax errors: they must reach the caller (and its large-stack retry).
        catch (BashArithmeticParseException ex) when (!ex.Message.Contains(NestingGuard.TooDeepMessage, StringComparison.Ordinal)
                                                      && !ex.Message.Contains(NestingGuard.TooLongMessage, StringComparison.Ordinal))
        {
            string text = StripDoubleQuotes(source);
            return new ArithmeticSyntax(text, new ArithmeticExpr.Deferred(text));
        }
    }

    /// <summary>bash removes double quotes (and the <c>$</c> of <c>$"…"</c>) from an arithmetic expression: <c>(( "1" + "2" ))</c> is 3.</summary>
    private static string StripDoubleQuotes(string? source)
    {
        string text = source ?? string.Empty;
        if (!text.Contains('"')) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        int sub = 0;            // depth inside $( … ): copied verbatim, its quotes belong to the command
        char inner = '\0';      // quote char open inside such a substitution
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (sub > 0)
            {
                sb.Append(c);
                if (inner != '\0') { if (c == inner) inner = '\0'; }
                else if (c is '"' or '\'') inner = c;
                else if (c == '(') sub++;
                else if (c == ')') sub--;
                continue;
            }
            if (c == '$' && i + 1 < text.Length && text[i + 1] == '(') { sb.Append("$("); i++; sub = 1; continue; }
            if (c == '$' && i + 1 < text.Length && text[i + 1] == '"') continue; // $"…" → "…"
            if (c == '"') continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private Tok Current => _tokens[_position];
    private bool Accept(T kind) { if (Current.Kind != kind) return false; _position++; return true; }
    private void Expect(T kind)
    {
        if (!Accept(kind))
            Error(kind == T.RParen ? "missing `)'" : kind == T.Colon ? "`:' expected for conditional expression" : $"expected '{kind}'");
    }

    /// <summary>
    /// A syntax error in bash's words (expr.c <c>evalerror</c>): <c>syntax error: operand expected (error token is "+")</c>.
    /// The error token is the text from the token being read on -- or, at the end of the expression, from the last
    /// token read (bash leaves its last-token pointer there): <c>1+</c> -> "+", <c>a b</c> -> "b", <c>1+*2</c> -> "*2".
    /// The caller puts <c>EXPR: </c> (and the shell's prefix) in front.
    /// </summary>
    private void Error(string message)
        => throw new BashArithmeticParseException($"{message} (error token is \"{_source[LastTokenStart()..]}\")");

    /// <summary>Where bash's last-token pointer is: the token being read, or at the end the last token read.</summary>
    private int LastTokenStart()
    {
        int at = Current.Kind == T.End && _position > 0 ? _position - 1 : _position;
        return at < _starts.Count ? Math.Min(_starts[at], _source.Length) : _source.Length;
    }

    /// <summary>The start of the token at <see cref="_position"/> (the operand after an operator just read).</summary>
    private int CurrentTokenStart() => _position < _starts.Count ? Math.Min(_starts[_position], _source.Length) : _source.Length;

    private TNode NoteErrorToken<TNode>(TNode node, int start) where TNode : ArithmeticExpr
    {
        if (_errorTokens is not null) _errorTokens[node] = start;
        return node;
    }

    // Every recursive EDGE of this grammar holds one NestingGuard level while it is taken: a paren group and a ?: middle
    // operand (-> ParseComma), the ?: false branch (-> ParseConditional), a=b=… (-> ParseAssignment), 2**2**… (->
    // ParsePower) and - - … x (-> ParseUnary). Every cycle of the call graph passes one of them, so no expression can
    // overflow the stack while it is PARSED; and a paren costs ONE level, not one per precedence function it passes
    // through (bash takes 1000 nested parens). The tree's depth is not bounded by this alone (a left-deep `1+1+…` run
    // takes no level), so the runtime evaluator, which recurses over the tree, probes the stack per node (BashArith.Eval).
    // REGRESSIONS: the ?: false branch held no level and `0?0:0?0:…1` 30000 deep overflowed the host; then a level per
    // FUNCTION cut nested parens to ~200. Guards: NestingGuardTests.ArithmeticParse_EveryRecursionEdge*, *_NestedParens*.
    private ArithmeticExpr ParseComma()
    {
        var value = ParseAssignment();
        int count = 0;
        while (Accept(T.Comma))
        {
            NestingGuard.CheckArithmeticBreadth(++count, NestingGuard.MaxChainLength, "comma list");
            value = new ArithmeticExpr.Binary(ArithmeticBinaryOp.Comma, value, ParseAssignment());
        }
        return value;
    }

    private ArithmeticExpr ParseAssignment()
    {
        if (Current.Kind == T.Ident && TryAssignmentOp(_tokens[_position + 1].Kind, out var op))
        {
            string name = Current.Text!;
            _position += 2;
            ArithmeticExpr value;
            using (NestingGuard.EnterArithmetic()) value = ParseAssignment();
            var assignment = new ArithmeticExpr.Assignment(name, op, value);
            return op is ArithmeticAssignmentOp.Divide or ArithmeticAssignmentOp.Modulo
                ? NoteErrorToken(assignment, LastTokenStart())
                : assignment;
        }
        var condition = ParseConditional();
        // bash (expr.c expassign): an assignment operator after anything but a bare name -- `1=2`, `0 ? 1 : x = 5`, whose
        // left side is the whole conditional -- is "attempted assignment to non-variable (error token is "= 5")".
        if (TryAssignmentOp(Current.Kind, out _)) Error("attempted assignment to non-variable");
        return condition;
    }

    private ArithmeticExpr ParseConditional()
    {
        var condition = ParseLogicalOr();
        if (!Accept(T.Question)) return condition;
        // bash (expr.c expcond): nothing, or a `:`, after the `?` and nothing after the `:` is "expression expected".
        if (Current.Kind is T.End or T.Colon) Error("expression expected");
        // Bash follows C here: the middle operand is a full expression, so a
        // comma belongs to the true branch rather than the surrounding level.
        ArithmeticExpr whenTrue, whenFalse;
        using (NestingGuard.EnterArithmetic()) whenTrue = ParseComma();
        Expect(T.Colon);
        if (Current.Kind == T.End) Error("expression expected");
        // The false branch is a conditional, not an assignment: `0 ? 1 : x = 5` assigns to the conditional (an error).
        using (NestingGuard.EnterArithmetic()) whenFalse = ParseConditional();
        return new ArithmeticExpr.Conditional(condition, whenTrue, whenFalse);
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
        int count = 0;
        while (true)
        {
            int index = Array.FindIndex(operators, pair => pair.Token == Current.Kind);
            if (index < 0) return value;
            // A flat run (1+1+…) becomes a left-deep tree, both here and in the PowerShell it emits.
            NestingGuard.CheckArithmeticBreadth(++count, NestingGuard.MaxChainLength, "operator chain");
            _position++;
            var op = operators[index].Op;
            int rightStart = CurrentTokenStart();
            value = new ArithmeticExpr.Binary(op, value, operand());
            if (op is ArithmeticBinaryOp.Divide or ArithmeticBinaryOp.Modulo) NoteErrorToken(value, rightStart);
        }
    }

    private ArithmeticExpr ParsePower()
    {
        var value = ParseUnary();
        if (!Accept(T.StarStar)) return value;
        ArithmeticExpr exponent;
        using (NestingGuard.EnterArithmetic()) exponent = ParsePower();
        return NoteErrorToken(new ArithmeticExpr.Binary(ArithmeticBinaryOp.Power, value, exponent), LastTokenStart());
    }

    private ArithmeticExpr ParseUnary()
    {
        if (Accept(T.Plus)) return new ArithmeticExpr.Unary(ArithmeticUnaryOp.Plus, ParseUnaryOperand());
        if (Accept(T.Minus)) return new ArithmeticExpr.Unary(ArithmeticUnaryOp.Negate, ParseUnaryOperand());
        if (Accept(T.Bang)) return new ArithmeticExpr.Unary(ArithmeticUnaryOp.LogicalNot, ParseUnaryOperand());
        if (Accept(T.Tilde)) return new ArithmeticExpr.Unary(ArithmeticUnaryOp.BitwiseNot, ParseUnaryOperand());
        if (Accept(T.Inc)) return ParsePrefixIncrement(1);
        if (Accept(T.Dec)) return ParsePrefixIncrement(-1);
        return ParsePostfix();
    }

    /// <summary>A unary sign's operand: the recursive edge ParseUnary -> ParseUnary, one nesting level.</summary>
    private ArithmeticExpr ParseUnaryOperand()
    {
        using var nesting = NestingGuard.EnterArithmetic();
        return ParseUnary();
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
        if (Current.Kind == T.CommandSub)
        {
            Tok sub = _tokens[_position++];
            return new ArithmeticExpr.CommandSub(sub.Key!, sub.Text!);
        }
        if (Accept(T.LParen))
        {
            ArithmeticExpr value;
            using (NestingGuard.EnterArithmetic()) value = ParseComma();
            Expect(T.RParen);
            return value;
        }
        Error("syntax error: operand expected");
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
            T.CaretEq => ArithmeticAssignmentOp.BitwiseXor,
            _ => default,
        };
        return token is >= T.Assign and <= T.CaretEq;
    }

    private static List<Tok> Tokenize(string source, List<int> starts)
    {
        var result = new List<Tok>();
        int tokenStart = 0;
        for (int i = 0; i < source.Length;)
        {
            // The token(s) the previous step added start where that step began.
            while (starts.Count < result.Count) starts.Add(tokenStart);
            char c = source[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            tokenStart = i;
            if (c == '_' || char.IsLetter(c))
            {
                int start = i++;
                while (i < source.Length && (source[i] == '_' || char.IsLetterOrDigit(source[i]))) i++;
                // `a[i]` / `m[$k]` / `c[x+1]++`: an array element is one operand; the evaluator's read / write callbacks resolve it.
                if (i < source.Length && source[i] == '[')
                {
                    int j = BashLexer.SubscriptClose(source, i);
                    if (j < 0) throw new BashArithmeticParseException($"unterminated array subscript near '{source[start..]}'");
                    i = j + 1;
                }
                result.Add(new Tok(T.Ident, Text: source[start..i])); continue;
            }
            if (char.IsDigit(c))
            {
                int start = i++;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] is '#' or '@' or '_')) i++;
                string text = source[start..i];
                // `10#$x` / `16#${h}`: the digits are a run-time expansion. The value cannot be known here, so it stays an
                // opaque parameter (never folded to a constant); the evaluator expands `$x` in the source before parsing.
                if (text.EndsWith('#') && i < source.Length && source[i] == '$')
                {
                    int pStart = i++;
                    if (i < source.Length && source[i] == '{') i = BashLexer.DollarBraceClose(source, pStart) is var close and >= 0 ? close + 1 : source.Length;
                    else while (i < source.Length && (source[i] == '_' || char.IsLetterOrDigit(source[i]))) i++;
                    result.Add(new Tok(T.Parameter, Text: text + source[pStart..i], Key: source[pStart..i].Trim('$', '{', '}')));
                    continue;
                }
                long number;
                // bash: `1 + 08` -> `1 + 08: value too great for base (error token is "08")`: the expression it names ends
                // with the bad constant, which is the error token.
                try { number = ParseNumber(text); }
                catch (BashArithmeticParseException ex)
                {
                    throw new BashArithmeticParseException($"{ex.Message} (error token is \"{text}\")") { Expression = source[..i] };
                }
                result.Add(new Tok(T.Num, number)); continue;
            }
            if (c == '`')
            {
                int close = source.IndexOf('`', i + 1);
                if (close < 0) throw new BashArithmeticParseException($"unterminated command substitution near '{source[i..]}'");
                result.Add(new Tok(T.CommandSub, Text: source[i..(close + 1)], Key: source[(i + 1)..close]));
                i = close + 1; continue;
            }
            if (c == '$')
            {
                int start = i++;
                if (i >= source.Length) throw new BashArithmeticParseException("invalid arithmetic parameter '$'");
                // `$(( … ))` is a NESTED arithmetic expansion, not a subshell command
                // substitution. Dropping the `$` leaves `(( … ))`, which the existing
                // paren grouping already parses correctly.
                if (source[i] == '(' && i + 1 < source.Length && source[i + 1] == '(') continue;
                if (source[i] == '(')
                {
                    int close = FindMatchingParen(source, i);
                    if (close < 0) throw new BashArithmeticParseException($"unterminated command substitution near '{source[start..]}'");
                    result.Add(new Tok(T.CommandSub, Text: source[start..(close + 1)], Key: source[(i + 1)..close]));
                    i = close + 1; continue;
                }
                if (source[i] == '{')
                {
                    int keyStart = ++i;
                    // A full parameter expansion (`${a[i]}`, `${m[$k]:-0}`, `${u:-${v}}`): the lexer's extent.
                    int braceClose = BashLexer.DollarBraceClose(source, start);
                    i = braceClose < 0 ? source.Length : braceClose;
                    if (i >= source.Length || i == keyStart)
                        throw new BashArithmeticParseException($"invalid arithmetic parameter near '{source[start..]}'");
                    string key = source[keyStart..i];
                    if (key is "#@" or "#*") key = "#"; // ${#@} is $#
                    // Array-length expansion is valid inside arithmetic contexts
                    // (for example `${#arr[@]} + 1`). Preserve it as an opaque
                    // parameter lookup so the typed arithmetic parser does not
                    // reject an otherwise valid bash command.
                    // Any other expansion is the shell's to expand before evaluation (the emitter splices it in).
                    i++;
                    result.Add(new Tok(T.Parameter, Text: key == "#" ? "$#" : source[start..i], Key: key));
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
            // bash (expr.c readtok): `++` / `--` is an increment only after a name (`x++`) or before one (`++x`, `-- x`);
            // anywhere else it is two signs: `++5` and `--5` are 5, `1--1` is 2, `5++` is `5 + +` (operand expected).
            if (c is '+' or '-' && i + 1 < source.Length && source[i + 1] == c
                && !(result.Count > 0 && result[^1].Kind == T.Ident) && !NameFollows(source, i + 2))
            {
                result.Add(new Tok(c == '+' ? T.Plus : T.Minus)); i++; continue;
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
            if (single == T.End)
                throw new BashArithmeticParseException($"syntax error: invalid arithmetic operator (error token is \"{source[i..]}\")");
            result.Add(new Tok(single)); i++;
        }
        while (starts.Count < result.Count) starts.Add(tokenStart);
        result.Add(new Tok(T.End));
        starts.Add(source.Length);
        return result;
    }

    /// <summary>Whether a name starts at <paramref name="at"/> after optional blanks (bash's <c>legal_variable_starter</c>).</summary>
    private static bool NameFollows(string source, int at)
    {
        while (at < source.Length && char.IsWhiteSpace(source[at])) at++;
        return at < source.Length && (source[at] == '_' || char.IsLetter(source[at]));
    }

    /// <summary>
    /// Index of the <c>)</c> closing the <c>(</c> at <paramref name="open"/>, honoring
    /// nesting and skipping parens inside single/double quotes (a `)` in
    /// <c>$(grep "a)b" f)</c> must not close the substitution); -1 if unbalanced.
    /// </summary>
    internal static int FindMatchingParen(string source, int open)
    {
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '\\') { i++; continue; }
            if (c is '\'' or '"')
            {
                char quote = c;
                for (i++; i < source.Length && source[i] != quote; i++)
                    if (quote == '"' && source[i] == '\\') i++;
                if (i >= source.Length) return -1;
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return i;
        }
        return -1;
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
            // No `**=`: bash has none (expr.c); `x **= 2` is `x ** = 2`, "operand expected" at `= 2`.
            "<<=" => T.ShlEq, ">>=" => T.ShrEq,
            "**" => T.StarStar, "<<" => T.Shl, ">>" => T.Shr, "<=" => T.Le, ">=" => T.Ge,
            "==" => T.EqEq, "!=" => T.Ne, "&&" => T.AndAnd, "||" => T.OrOr,
            "++" => T.Inc, "--" => T.Dec, "+=" => T.PlusEq, "-=" => T.MinusEq,
            "*=" => T.StarEq, "/=" => T.SlashEq, "%=" => T.PercentEq, "&=" => T.AmpEq,
            "|=" => T.PipeEq, "^=" => T.CaretEq, _ => T.End,
        };
        return token != T.End;
    }

    /// <summary>
    /// An integer constant's value. A bad one is bash's message (expr.c <c>strlong</c>), without the error token:
    /// <c>1#1</c>, <c>65#1</c> "invalid arithmetic base"; <c>02#011</c> "invalid number"; <c>2#</c> "invalid integer
    /// constant"; <c>08</c>, <c>2#12</c>, <c>0xg</c>, <c>9a</c> "value too great for base". A decimal past 64 bits wraps.
    /// </summary>
    internal static long ParseNumber(string token)
    {
        int hash = token.IndexOf('#');
        if (hash > 0)
        {
            // A base with a leading 0 (`02#0110`) is not a decimal base in bash: "invalid number".
            if (token[0] == '0') throw new BashArithmeticParseException("invalid number");
            if (!int.TryParse(token[..hash], NumberStyles.None, CultureInfo.InvariantCulture, out int radix) || radix is < 2 or > 64)
                throw new BashArithmeticParseException("invalid arithmetic base");
            return ParseDigits(token[(hash + 1)..], radix, true);
        }
        // A bare `0x` (no hex digits) is 0 in bash, not an error: `q=0x; echo $((q+0))` prints 0.
        if (token.Length == 2 && token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return 0;
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return ParseDigits(token[2..], 16, false);
        if (token.Length > 1 && token[0] == '0') return ParseDigits(token, 8, false);
        return ParseDigits(token, 10, false);
    }

    private static long ParseDigits(string digits, int radix, bool bashDigits)
    {
        if (digits.Length == 0) throw new BashArithmeticParseException("invalid integer constant");
        long value = 0;
        foreach (char digit in digits)
        {
            int d = bashDigits ? BashDigit(digit, radix) : HexDigit(digit);
            if (d < 0 || d >= radix) throw new BashArithmeticParseException("value too great for base");
            value = unchecked(value * radix + d);
        }
        return value;
    }

    private static int HexDigit(char c) => c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;

    /// <summary>
    /// A digit of <c>BASE#DIGITS</c> (expr.c <c>strlong</c>): up to base 36 a letter of either case is 10-35
    /// (<c>16#FF</c> = <c>16#ff</c> = 255, <c>36#Z</c> = 35); above it a-z are 10-35, A-Z 36-61, <c>@</c> 62, <c>_</c> 63.
    /// </summary>
    private static int BashDigit(char c, int radix) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'z' => c - 'a' + 10,
        >= 'A' and <= 'Z' => c - 'A' + (radix <= 36 ? 10 : 36),
        '@' => 62,
        '_' => 63,
        _ => -1,
    };

    /// <summary>
    /// The ONE place an arithmetic operand may skip the evaluator: text that is a canonical decimal literal
    /// (<c>0</c>, <c>7</c>, <c>-12</c>; 19 digits at most and within 64 bits), which every evaluator reads as exactly
    /// that number. Anything else -- <c>010</c> (octal 8), <c>08</c> (an error), <c>-010</c>, <c>+5</c>, <c>-0</c>,
    /// blanks, <c>1+1</c>, a name -- is the evaluator's (<c>Invoke-BashArith</c> / <c>BashArith.Evaluate</c>).
    /// <para>
    /// The bug class: each context (a slice bound, an indexed subscript, <c>unset 'a[i]'</c>, <c>declare -i</c>) decided
    /// for itself with <c>int.TryParse</c> / <c>[long]</c> / <c>-match '^-?[0-9]+$'</c>, which read <c>010</c> as 10 and
    /// <c>08</c> as 8, and a variable bound (<c>${a[@]:$j}</c>, j='1+1') as a PowerShell cast that threw. Guard:
    /// <c>Core.Tests/ArithmeticOperandGuardTests</c>; spec <c>docs/specs/emitter-strategy.md</c> "One arithmetic evaluator".
    /// </para>
    /// </summary>
    public static bool TryPlainDecimal(ReadOnlySpan<char> text, out long value)
    {
        value = 0;
        int sign = text.Length > 0 && text[0] == '-' ? 1 : 0;
        int digits = text.Length - sign;
        if (digits is < 1 or > 19) return false;
        // A leading 0 is octal (`010` is 8, `08` an error), and `-0` is not canonical.
        if (text[sign] == '0' && (digits > 1 || sign == 1)) return false;
        foreach (char c in text[sign..])
            if (c is < '0' or > '9') return false;
        return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    /// <summary><see cref="TryPlainDecimal"/> for emitted PowerShell: the number, or null for the evaluator to decide.</summary>
    public static long? PlainDecimal(string? text) => text is not null && TryPlainDecimal(text, out long value) ? value : null;
}

public sealed class BashArithmeticParseException : Exception
{
    public BashArithmeticParseException(string message) : base(message) { }

    /// <summary>
    /// The expression bash names before the message when it is not the whole text: a bad integer constant ends it
    /// (<c>1 + 08 + 2</c> reports <c>1 + 08: value too great for base</c>). Null: the whole expression.
    /// </summary>
    public string? Expression { get; init; }
}
