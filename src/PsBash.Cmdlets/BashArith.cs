using PsBash.Core.Parser;
using PsBash.Core.Parser.Ast;

namespace PsBash.Cmdlets;

/// <summary>Evaluates the shared typed bash arithmetic syntax tree.</summary>
public static class BashArith
{
    private const int MaxExpandedSourceLength = 64 * 1024;
    private static readonly System.Text.RegularExpressions.Regex UnbracedDigitSuffix =
        new(@"\$(?<key>[0-9])(?<suffix>[0-9]+)");

    public static long Evaluate(string expression, Func<string, long> read, Action<string, long> write,
        Func<string, string?>? readRaw = null)
    {
        try
        {
            return Evaluate(BashArithmeticParser.Parse(expression), read, write, readRaw);
        }
        catch (BashArithmeticParseException ex)
        {
            throw new BashArithException(ex.Message, ex);
        }
    }

    /// <summary>Evaluates already-parsed syntax, allowing all arithmetic consumers to share one parse.</summary>
    public static long Evaluate(ArithmeticSyntax syntax, Func<string, long> read, Action<string, long> write,
        Func<string, string?>? readRaw = null)
    {
        string expanded = ExpandUnbracedDigitSuffixes(syntax.Source, read, readRaw);
        ArithmeticExpr root = expanded == syntax.Source
            ? syntax.Root
            : BashArithmeticParser.Parse(expanded).Root;
        return Eval(root, read, write, readRaw);
    }

    private static string ExpandUnbracedDigitSuffixes(string source, Func<string, long> read,
        Func<string, string?>? readRaw)
    {
        if (source.Length > MaxExpandedSourceLength)
            throw new BashArithException("expanded arithmetic source exceeded maximum length");
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
                    ArithmeticAssignmentOp.Divide => Divide(current, rhs),
                    ArithmeticAssignmentOp.Modulo => Modulo(current, rhs),
                    ArithmeticAssignmentOp.ShiftLeft => current << (int)rhs,
                    ArithmeticAssignmentOp.ShiftRight => current >> (int)rhs,
                    ArithmeticAssignmentOp.BitwiseAnd => current & rhs,
                    ArithmeticAssignmentOp.BitwiseOr => current | rhs,
                    ArithmeticAssignmentOp.BitwiseXor => current ^ rhs,
                    ArithmeticAssignmentOp.Power => Power(current, rhs),
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
            ArithmeticBinaryOp.Divide => Divide(left, right),
            ArithmeticBinaryOp.Modulo => Modulo(left, right),
            ArithmeticBinaryOp.Power => Power(left, right),
            _ => right,
        };
    }

    private static long Divide(long left, long right)
    {
        if (right == 0) throw new BashArithException("division by 0");
        return left / right;
    }

    private static long Modulo(long left, long right)
    {
        if (right == 0) throw new BashArithException("division by 0");
        return left % right;
    }

    private static long Power(long value, long exponent)
    {
        if (exponent < 0) return 0;
        long result = 1;
        for (long i = 0; i < exponent; i++) result *= value;
        return result;
    }

    public sealed class BashArithException : Exception
    {
        public BashArithException(string message) : base(message) { }
        public BashArithException(string message, Exception innerException) : base(message, innerException) { }
    }
}
