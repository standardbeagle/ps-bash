using PsBash.Core.Parser;
using PsBash.Core.Parser.Ast;

namespace PsBash.Cmdlets;

/// <summary>Evaluates the shared typed bash arithmetic syntax tree.</summary>
public static class BashArith
{
    private const int MaxExpandedSourceLength = 64 * 1024;
    private static readonly System.Text.RegularExpressions.Regex UnbracedDigitSuffix =
        new(@"\$(?<key>[0-9])(?<suffix>[0-9]+)");

    /// <summary>
    /// Evaluates <paramref name="expression"/>. A failure is a <see cref="BashArithException"/> that names the expression
    /// it belongs to and bash's error token (<see cref="BashArithException.Describe"/>): an operand variable whose value
    /// is itself an expression (<c>y='1/0'; $(( y + 1 ))</c>) reports THAT expression, as bash does.
    /// </summary>
    public static long Evaluate(string expression, Func<string, long> read, Action<string, long> write,
        Func<string, string?>? readRaw = null)
    {
        var (syntax, tokens) = Parsed(expression);
        return EvaluateCore(syntax, tokens, read, write, readRaw);
    }

    // A loop evaluates the same few expressions (`i<N`, `i++`, `s+i`) on every pass; parsing each one once saves the
    // parse and its allocations per pass. The tree and its token map are never written after the parse, so threads
    // (background jobs) share them. Bounded: a script that builds new expression text per pass (`$(( $x * 2 ))` with
    // the value spliced in) cannot grow it without limit; past the bound it is emptied and refills with what is hot.
    private const int ParseCacheMax = 2048;
    private const int ParseCacheMaxLength = 512;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (ArithmeticSyntax, Dictionary<ArithmeticExpr, int>)> ParseCache
        = new(StringComparer.Ordinal);

    private static (ArithmeticSyntax Syntax, Dictionary<ArithmeticExpr, int> Tokens) Parsed(string expression)
    {
        if (ParseCache.TryGetValue(expression, out var hit)) return hit;
        var tokens = NewTokenMap();
        var parsed = (ParseOrThrow(expression, tokens), tokens);   // a parse error is thrown, never cached
        if (expression.Length <= ParseCacheMaxLength)
        {
            if (ParseCache.Count >= ParseCacheMax) ParseCache.Clear();
            ParseCache[expression] = parsed;
        }
        return parsed;
    }

    /// <summary>Evaluates already-parsed syntax, allowing all arithmetic consumers to share one parse.</summary>
    public static long Evaluate(ArithmeticSyntax syntax, Func<string, long> read, Action<string, long> write,
        Func<string, string?>? readRaw = null)
        => EvaluateCore(syntax, null, read, write, readRaw);

    private static Dictionary<ArithmeticExpr, int> NewTokenMap() => new(ReferenceEqualityComparer.Instance);

    private static ArithmeticSyntax ParseOrThrow(string expression, Dictionary<ArithmeticExpr, int> tokens)
    {
        try
        {
            return BashArithmeticParser.Parse(expression, tokens);
        }
        catch (BashArithmeticParseException ex)
        {
            // The parser's message already ends with the error token.
            throw new BashArithException(ex.Message, ex) { Expression = ex.Expression ?? expression };
        }
    }

    private static long EvaluateCore(ArithmeticSyntax syntax, Dictionary<ArithmeticExpr, int>? tokens,
        Func<string, long> read, Action<string, long> write, Func<string, string?>? readRaw)
    {
        string source = syntax.Source;
        try
        {
            string expanded = ExpandUnbracedDigitSuffixes(source, read, readRaw);
            ArithmeticExpr root = syntax.Root;
            if (expanded != source)
            {
                tokens = NewTokenMap();
                source = expanded;
                root = ParseOrThrow(expanded, tokens).Root;
            }
            return Eval(root, read, write, readRaw);
        }
        catch (BashArithException ex) when (ex.Expression is null)
        {
            // The innermost evaluation names the error: an operand's own expression has set it already.
            ex.Expression = source;
            if (ex.Node is not null && tokens is not null && tokens.TryGetValue(ex.Node, out int at)) ex.Token = source[at..];
            throw;
        }
    }

    private static string ExpandUnbracedDigitSuffixes(string source, Func<string, long> read,
        Func<string, string?>? readRaw)
    {
        if (source.Length > MaxExpandedSourceLength)
            throw new BashArithException("expanded arithmetic source exceeded maximum length");
        // Only `$1x`-shaped text expands; most expressions have no `$` at all, and need no regex or set.
        if (source.IndexOf('$') < 0 || !UnbracedDigitSuffix.IsMatch(source)) return source;
        string current = source;
        var seen = new HashSet<string>(StringComparer.Ordinal) { current };
        for (int depth = 0; depth < 32; depth++)
        {
            if (!UnbracedDigitSuffix.IsMatch(current)) return current;
            var builder = new System.Text.StringBuilder(Math.Min(current.Length, MaxExpandedSourceLength));
            int copiedThrough = 0;
            foreach (System.Text.RegularExpressions.Match match in UnbracedDigitSuffix.Matches(current))
            {
                string key = match.Groups["key"].Value;
                string raw = readRaw?.Invoke(key)
                    ?? read(key).ToString(System.Globalization.CultureInfo.InvariantCulture);
                string suffix = match.Groups["suffix"].Value;
                int literalLength = match.Index - copiedThrough;
                long projected = (long)builder.Length + literalLength + raw.Length + suffix.Length;
                if (projected > MaxExpandedSourceLength)
                    throw new BashArithException("expanded arithmetic source exceeded maximum length");
                builder.Append(current, copiedThrough, literalLength);
                builder.Append(raw);
                builder.Append(suffix);
                copiedThrough = match.Index + match.Length;
            }
            int tailLength = current.Length - copiedThrough;
            if ((long)builder.Length + tailLength > MaxExpandedSourceLength)
                throw new BashArithException("expanded arithmetic source exceeded maximum length");
            builder.Append(current, copiedThrough, tailLength);
            string next = builder.ToString();
            if (!seen.Add(next))
                throw new BashArithException("cyclic arithmetic parameter expansion");
            current = next;
        }
        throw new BashArithException("arithmetic parameter expansion exceeded maximum depth");
    }

    private static long Eval(ArithmeticExpr expression, Func<string, long> read, Action<string, long> write,
        Func<string, string?>? readRaw)
    {
        switch (expression)
        {
            case ArithmeticExpr.Number number: return number.Value;
            case ArithmeticExpr.Identifier identifier: return read(identifier.Name);
            // The runtime variable contract historically receives bare names. Keep
            // `$name` identical to `name`; positional/special parameters use the
            // same stripped key and therefore naturally read as zero when unset.
            case ArithmeticExpr.Parameter parameter:
            {
                // `16#$h` / `10#${x}`: the base prefix is followed by the variable's raw text (digits like "ff"), not its numeric value.
                int baseHash = parameter.Spelling.IndexOf('#');
                if (baseHash > 0 && baseHash + 1 < parameter.Spelling.Length && parameter.Spelling[baseHash + 1] == '$'
                    && parameter.Spelling[..baseHash].All(char.IsDigit))
                {
                    string raw = readRaw?.Invoke(parameter.LookupKey) ?? read(parameter.LookupKey).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return Evaluate(parameter.Spelling[..(baseHash + 1)] + raw, read, write, readRaw);
                }
                if (parameter.UnbracedSuffix.Length == 0) return read(parameter.LookupKey);
                // Normally eliminated by whole-source expansion before parsing.
                // Retain a numeric fallback for manually constructed syntax nodes.
                string expanded = read(parameter.LookupKey).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + parameter.UnbracedSuffix;
                return Evaluate(expanded, read, write, readRaw);
            }
            // The emitter substitutes every `$( … )` operand with the command's
            // runtime value before the expression string reaches this evaluator, so a
            // surviving node means the string was built some other way. Report it
            // rather than crashing on an unhandled case.
            case ArithmeticExpr.CommandSub sub:
                throw new BashArithException($"command substitution is not evaluable here: {sub.Spelling}");
            case ArithmeticExpr.Unary unary:
            {
                long value = Eval(unary.Operand, read, write, readRaw);
                return unary.Op switch
                {
                    ArithmeticUnaryOp.Plus => value,
                    ArithmeticUnaryOp.Negate => -value,
                    ArithmeticUnaryOp.LogicalNot => value == 0 ? 1 : 0,
                    ArithmeticUnaryOp.BitwiseNot => ~value,
                    _ => value,
                };
            }
            case ArithmeticExpr.Increment increment:
            {
                long old = read(increment.Name);
                long value = old + increment.Delta;
                write(increment.Name, value);
                return increment.Prefix ? value : old;
            }
            case ArithmeticExpr.Assignment assignment:
            {
                long rhs = Eval(assignment.Value, read, write, readRaw);
                long current = assignment.Op == ArithmeticAssignmentOp.Assign ? 0 : read(assignment.Name);
                long value = assignment.Op switch
                {
                    ArithmeticAssignmentOp.Assign => rhs,
                    ArithmeticAssignmentOp.Add => current + rhs,
                    ArithmeticAssignmentOp.Subtract => current - rhs,
                    ArithmeticAssignmentOp.Multiply => current * rhs,
                    ArithmeticAssignmentOp.Divide => Divide(current, rhs, assignment),
                    ArithmeticAssignmentOp.Modulo => Modulo(current, rhs, assignment),
                    ArithmeticAssignmentOp.ShiftLeft => current << (int)rhs,
                    ArithmeticAssignmentOp.ShiftRight => current >> (int)rhs,
                    ArithmeticAssignmentOp.BitwiseAnd => current & rhs,
                    ArithmeticAssignmentOp.BitwiseOr => current | rhs,
                    ArithmeticAssignmentOp.BitwiseXor => current ^ rhs,
                    _ => rhs,
                };
                write(assignment.Name, value);
                return value;
            }
            case ArithmeticExpr.Conditional conditional:
                return Eval(conditional.Condition, read, write, readRaw) != 0
                    ? Eval(conditional.WhenTrue, read, write, readRaw)
                    : Eval(conditional.WhenFalse, read, write, readRaw);
            case ArithmeticExpr.Binary binary:
                return EvalBinary(binary, read, write, readRaw);
            default:
                throw new BashArithException("unknown arithmetic syntax node");
        }
    }

    private static long EvalBinary(ArithmeticExpr.Binary binary, Func<string, long> read, Action<string, long> write,
        Func<string, string?>? readRaw)
    {
        long left = Eval(binary.Left, read, write, readRaw);
        if (binary.Op == ArithmeticBinaryOp.LogicalOr)
            return left != 0 ? 1 : Eval(binary.Right, read, write, readRaw) != 0 ? 1 : 0;
        if (binary.Op == ArithmeticBinaryOp.LogicalAnd)
            return left == 0 ? 0 : Eval(binary.Right, read, write, readRaw) != 0 ? 1 : 0;

        long right = Eval(binary.Right, read, write, readRaw);
        return binary.Op switch
        {
            ArithmeticBinaryOp.Comma => right,
            ArithmeticBinaryOp.BitwiseOr => left | right,
            ArithmeticBinaryOp.BitwiseXor => left ^ right,
            ArithmeticBinaryOp.BitwiseAnd => left & right,
            ArithmeticBinaryOp.Equal => left == right ? 1 : 0,
            ArithmeticBinaryOp.NotEqual => left != right ? 1 : 0,
            ArithmeticBinaryOp.Less => left < right ? 1 : 0,
            ArithmeticBinaryOp.LessOrEqual => left <= right ? 1 : 0,
            ArithmeticBinaryOp.Greater => left > right ? 1 : 0,
            ArithmeticBinaryOp.GreaterOrEqual => left >= right ? 1 : 0,
            ArithmeticBinaryOp.ShiftLeft => left << (int)right,
            ArithmeticBinaryOp.ShiftRight => left >> (int)right,
            ArithmeticBinaryOp.Add => left + right,
            ArithmeticBinaryOp.Subtract => left - right,
            ArithmeticBinaryOp.Multiply => left * right,
            ArithmeticBinaryOp.Divide => Divide(left, right, binary),
            ArithmeticBinaryOp.Modulo => Modulo(left, right, binary),
            ArithmeticBinaryOp.Power => Power(left, right, binary),
            _ => right,
        };
    }

    private static long Divide(long left, long right, ArithmeticExpr node)
    {
        if (right == 0) throw new BashArithException("division by 0") { Node = node };
        return left / right;
    }

    private static long Modulo(long left, long right, ArithmeticExpr node)
    {
        if (right == 0) throw new BashArithException("division by 0") { Node = node };
        return left % right;
    }

    private static long Power(long value, long exponent, ArithmeticExpr node)
    {
        if (exponent < 0) throw new BashArithException("exponent less than 0") { Node = node };
        long result = 1;
        for (long i = 0; i < exponent; i++) result *= value;
        return result;
    }

    public sealed class BashArithException : Exception
    {
        public BashArithException(string message) : base(message) { }
        public BashArithException(string message, Exception innerException) : base(message, innerException) { }

        /// <summary>The node whose evaluation failed (division by 0, a negative exponent): it locates the error token.</summary>
        public ArithmeticExpr? Node { get; init; }

        /// <summary>The failure was in an array subscript (<c>$(( a[1/0] ))</c>): under <c>bash -c</c> it ends the shell.</summary>
        public bool InSubscript { get; set; }

        /// <summary>The expression the error belongs to (the innermost one evaluated); null until an evaluation names it.</summary>
        public string? Expression { get; set; }

        /// <summary>bash's error token: the source text from the failing operand on (<c>1 / 0 + 2</c>: "0 + 2"); null when
        /// unknown or already part of <see cref="Exception.Message"/> (a syntax error).</summary>
        public string? Token { get; set; }

        /// <summary>
        /// bash's report after the shell's prefix and any label (expr.c <c>evalerror</c>):
        /// <c>EXPR: MESSAGE (error token is "TOKEN")</c>. <paramref name="expression"/> stands in for an unnamed expression.
        /// </summary>
        public string Describe(string expression)
            => (Expression ?? expression) + ": " + Message + (Token is null ? "" : " (error token is \"" + Token + "\")");
    }
}
