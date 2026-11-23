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
    /// <param name="resolve">
    /// Turns an assignment target into the variable it names with any subscript evaluated (<c>a[i++]</c> -> <c>a[0]</c>),
    /// once: a compound assignment and <c>++</c>/<c>--</c> read and then write the SAME element, and its subscript's side
    /// effects happen once (bash: <c>i=0; a=(1 2 3); (( a[i++]++ ))</c> leaves i=1, a=(2 2 3)). Null: names are used as given.
    /// </param>
    public static long Evaluate(string expression, Func<string, long> read, Action<string, long> write,
        Func<string, string?>? readRaw = null, Func<string, string>? resolve = null)
    {
        var tokens = NewTokenMap();
        ArithmeticSyntax syntax = ParseOrThrow(expression, tokens);
        return EvaluateCore(syntax, tokens, new Callbacks(read, write, readRaw, resolve));
    }

    /// <summary>Evaluates already-parsed syntax, allowing all arithmetic consumers to share one parse.</summary>
    public static long Evaluate(ArithmeticSyntax syntax, Func<string, long> read, Action<string, long> write,
        Func<string, string?>? readRaw = null, Func<string, string>? resolve = null)
        => EvaluateCore(syntax, null, new Callbacks(read, write, readRaw, resolve));

    /// <summary>
    /// The caller's variable access. Every call goes through here so an exception a callback raises is marked as the
    /// callback's (<see cref="MarkCallbackFault"/>): the evaluator converts only its OWN .NET failures into arithmetic
    /// errors, and a fault in the variable layer passes on as the fault it is.
    /// </summary>
    private readonly record struct Callbacks(Func<string, long> ReadFn, Action<string, long> WriteFn,
        Func<string, string?>? ReadRawFn, Func<string, string>? ResolveFn)
    {
        public long Read(string name)
        {
            try { return ReadFn(name); }
            catch (Exception ex) when (MarkCallbackFault(ex)) { throw; }
        }

        public void Write(string name, long value)
        {
            try { WriteFn(name, value); }
            catch (Exception ex) when (MarkCallbackFault(ex)) { throw; }
        }

        public string? ReadRaw(string name)
        {
            if (ReadRawFn is null) return null;
            try { return ReadRawFn(name); }
            catch (Exception ex) when (MarkCallbackFault(ex)) { throw; }
        }

        public string Resolve(string name)
        {
            if (ResolveFn is null) return name;
            try { return ResolveFn(name); }
            catch (Exception ex) when (MarkCallbackFault(ex)) { throw; }
        }
    }

    private const string CallbackFaultKey = "PsBash.Cmdlets.BashArith.CallbackFault";

    /// <summary>An exception filter that never catches: it tags a .NET fault thrown by a callback so
    /// <see cref="IsEvaluatorFault"/> lets it pass (a callback's own arithmetic error, a nested evaluation's, is not a
    /// .NET fault and is untouched).</summary>
    private static bool MarkCallbackFault(Exception ex)
    {
        if (IsFaultType(ex) && !ex.Data.IsReadOnly) ex.Data[CallbackFaultKey] = true;
        return false;
    }

    private static Dictionary<ArithmeticExpr, int> NewTokenMap() => new(ReferenceEqualityComparer.Instance);

    private static ArithmeticSyntax ParseOrThrow(string expression, Dictionary<ArithmeticExpr, int> tokens)
    {
        try
        {
            // On a thread whose stack runs out before the depth cap (1000 nested parens on a small pipeline thread), the
            // parse runs again on a large stack, as a transpile does: the limit is the same on every thread.
            return NestingGuard.WithStackFallback(() => BashArithmeticParser.Parse(expression, tokens));
        }
        catch (BashArithmeticParseException ex)
        {
            // The parser's message already ends with the error token.
            throw new BashArithException(ex.Message, ex) { Expression = ex.Expression ?? expression };
        }
        catch (Exception ex) when (IsEvaluatorFault(ex))
        {
            throw new BashArithException(ex.Message, ex) { Expression = expression };
        }
    }

    private static long EvaluateCore(ArithmeticSyntax syntax, Dictionary<ArithmeticExpr, int>? tokens, Callbacks vars)
    {
        string source = syntax.Source;
        try
        {
            string expanded = ExpandUnbracedDigitSuffixes(source, vars);
            ArithmeticExpr root = syntax.Root;
            if (expanded != source)
            {
                tokens = NewTokenMap();
                source = expanded;
                root = ParseOrThrow(expanded, tokens).Root;
            }
            return Eval(root, vars);
        }
        catch (BashArithException ex) when (ex.Expression is null)
        {
            // The innermost evaluation names the error: an operand's own expression has set it already.
            ex.Expression = source;
            if (ex.Node is not null && tokens is not null && tokens.TryGetValue(ex.Node, out int at)) ex.Token = source[at..];
            throw;
        }
        catch (Exception ex) when (IsEvaluatorFault(ex))
        {
            // No .NET failure leaves the evaluator (an uncaught OverflowException aborted the enclosing function and,
            // in a command, the host's statement): it is an arithmetic error of this expression like any other.
            throw new BashArithException(ex.Message, ex) { Expression = source };
        }
    }

    /// <summary>
    /// A failure of the evaluator's own arithmetic or bookkeeping (overflow, a bad cast, an index), as opposed to a
    /// read or write callback's signal (an unbound variable, the recursion limit, a stopped pipeline) or a .NET fault
    /// raised inside a callback (<see cref="MarkCallbackFault"/>), which pass on unchanged.
    /// </summary>
    private static bool IsEvaluatorFault(Exception ex) => IsFaultType(ex) && !ex.Data.Contains(CallbackFaultKey);

    private static bool IsFaultType(Exception ex)
        => ex is ArithmeticException or InvalidCastException or IndexOutOfRangeException or FormatException
            or ArgumentException or NullReferenceException or KeyNotFoundException;

    private static string ExpandUnbracedDigitSuffixes(string source, Callbacks vars)
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
                string raw = vars.ReadRaw(key)
                    ?? vars.Read(key).ToString(System.Globalization.CultureInfo.InvariantCulture);
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

    /// <summary>
    /// The evaluator's recursion follows the tree, whose depth the parser's nesting cap does not bound alone: a left-deep
    /// chain (<c>1+1+…</c>, up to MaxChainLength per precedence level) takes no nesting level, and nested in parens it
    /// grows to tens of thousands of nodes; and a tree parsed on the large-stack retry is evaluated HERE, on the caller's
    /// thread. So every node probes the stack: out of stack is the clean error, never a StackOverflow (uncatchable: it
    /// killed the host). On the host's pipeline thread bash's 1000-deep <c>1**1**…</c> / <c>- - …</c> / <c>?:</c> chains fit.
    /// </summary>
    private static void EnsureStack(ArithmeticExpr expression)
    {
        if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
            throw new BashArithException(OutOfStackMessage) { Node = expression };
    }

    /// <summary>The evaluator's out-of-stack error, worded as the parser's depth limit.</summary>
    internal const string OutOfStackMessage = "nesting too deep (out of stack) in arithmetic expression";

    private static long Eval(ArithmeticExpr expression, in Callbacks vars)
    {
        EnsureStack(expression);
        switch (expression)
        {
            case ArithmeticExpr.Number number: return number.Value;
            case ArithmeticExpr.Identifier identifier: return vars.Read(identifier.Name);
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
                    string raw = vars.ReadRaw(parameter.LookupKey) ?? vars.Read(parameter.LookupKey).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return Evaluate(parameter.Spelling[..(baseHash + 1)] + raw, vars.ReadFn, vars.WriteFn, vars.ReadRawFn, vars.ResolveFn);
                }
                if (parameter.UnbracedSuffix.Length == 0) return vars.Read(parameter.LookupKey);
                // Normally eliminated by whole-source expansion before parsing.
                // Retain a numeric fallback for manually constructed syntax nodes.
                string expanded = vars.Read(parameter.LookupKey).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + parameter.UnbracedSuffix;
                return Evaluate(expanded, vars.ReadFn, vars.WriteFn, vars.ReadRawFn, vars.ResolveFn);
            }
            // The emitter substitutes every `$( … )` operand with the command's
            // runtime value before the expression string reaches this evaluator, so a
            // surviving node means the string was built some other way. Report it
            // rather than crashing on an unhandled case.
            case ArithmeticExpr.CommandSub sub:
                throw new BashArithException($"command substitution is not evaluable here: {sub.Spelling}");
            case ArithmeticExpr.Unary unary:
            {
                long value = Eval(unary.Operand, vars);
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
                // The target is resolved ONCE, then read and written: `a[i++]++` evaluates `i++` once (bash).
                string target = vars.Resolve(increment.Name);
                long old = vars.Read(target);
                long value = old + increment.Delta;
                vars.Write(target, value);
                return increment.Prefix ? value : old;
            }
            case ArithmeticExpr.Assignment assignment:
            {
                // A compound assignment resolves and reads its target BEFORE the right side runs (expr.c expassign):
                // `x=1; x += (x=10)` is 11, `x += x++` is 2, `x=3; x += x *= 2` is 9, and `i=0; a[i++] += i` adds 1 to a[0].
                // A plain `=` evaluates the target's subscript AFTER the right side: `i=0; (( a[i] = i++ ))` sets a[1].
                bool compound = assignment.Op != ArithmeticAssignmentOp.Assign;
                string target = compound ? vars.Resolve(assignment.Name) : assignment.Name;
                long current = compound ? vars.Read(target) : 0;
                long rhs = Eval(assignment.Value, vars);
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
                vars.Write(target, value);
                return value;
            }
            case ArithmeticExpr.Conditional conditional:
                return Eval(conditional.Condition, vars) != 0
                    ? Eval(conditional.WhenTrue, vars)
                    : Eval(conditional.WhenFalse, vars);
            case ArithmeticExpr.Binary binary:
                return EvalBinary(binary, vars);
            default:
                throw new BashArithException("unknown arithmetic syntax node");
        }
    }

    private static long EvalBinary(ArithmeticExpr.Binary binary, in Callbacks vars)
    {
        long left = Eval(binary.Left, vars);
        if (binary.Op == ArithmeticBinaryOp.LogicalOr)
            return left != 0 ? 1 : Eval(binary.Right, vars) != 0 ? 1 : 0;
        if (binary.Op == ArithmeticBinaryOp.LogicalAnd)
            return left == 0 ? 0 : Eval(binary.Right, vars) != 0 ? 1 : 0;

        long right = Eval(binary.Right, vars);
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

    // `/` `%` `/=` `%=` all divide here. The one quotient that does not fit, -2**63 / -1, wraps as in bash (expr.c checks
    // for it): -9223372036854775808, remainder 0. .NET raised OverflowException for both.
    private static long Divide(long left, long right, ArithmeticExpr node)
    {
        if (right == 0) throw new BashArithException("division by 0") { Node = node };
        return right == -1 ? unchecked(-left) : left / right;
    }

    private static long Modulo(long left, long right, ArithmeticExpr node)
    {
        if (right == 0) throw new BashArithException("division by 0") { Node = node };
        return right == -1 ? 0 : left % right;
    }

    // Exponentiation by squaring with a wrapping multiply (bash's ipow): `(-1)**9223372036854775807` is one 63-step
    // loop, not 2**63 multiplications (the linear loop hung the host).
    private static long Power(long value, long exponent, ArithmeticExpr node)
    {
        if (exponent < 0) throw new BashArithException("exponent less than 0") { Node = node };
        long result = 1;
        while (exponent != 0)
        {
            if ((exponent & 1) != 0) result = unchecked(result * value);
            exponent >>= 1;
            value = unchecked(value * value);
        }
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
