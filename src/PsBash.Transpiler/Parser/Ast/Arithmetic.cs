namespace PsBash.Core.Parser.Ast;

/// <summary>A parsed bash arithmetic expression together with its original text.</summary>
public sealed record ArithmeticSyntax(string Source, ArithmeticExpr Root)
{
    /// <summary>Converts typed arithmetic back to its exact original source for legacy consumers.</summary>
    public static implicit operator string(ArithmeticSyntax syntax) => syntax.Source;
}

/// <summary>Typed syntax tree for bash's integer arithmetic language.</summary>
public abstract record ArithmeticExpr : BashNode
{
    public sealed record Number(long Value) : ArithmeticExpr;
    public sealed record Identifier(string Name) : ArithmeticExpr;
    /// <summary>A parameter expansion with normalized lookup identity and preserved source spelling.</summary>
    public sealed record Parameter(string LookupKey, string Spelling, string UnbracedSuffix = "") : ArithmeticExpr
    {
        public string Name => Spelling;
        public Parameter(string spelling) : this(Normalize(spelling), spelling, "") { }
        private static string Normalize(string spelling) =>
            spelling.StartsWith("${", StringComparison.Ordinal) ? spelling[2..^1] : spelling[1..];
    }
    /// <summary>
    /// A command substitution used as an arithmetic operand — <c>$(( $(date +%s) + 60 ))</c>.
    /// Bash expands it to text and then evaluates, so it is unevaluable by the typed
    /// arithmetic evaluator alone: the node is an OPAQUE placeholder (same treatment as
    /// <c>${#arr[@]}</c>) that the emitter substitutes with the command's runtime value
    /// before the expression string ever reaches <c>Invoke-BashArith</c>.
    /// <paramref name="CommandText"/> is the bash source between the delimiters;
    /// <paramref name="Spelling"/> is the original text including them.
    /// </summary>
    public sealed record CommandSub(string CommandText, string Spelling) : ArithmeticExpr;
    public sealed record Unary(ArithmeticUnaryOp Op, ArithmeticExpr Operand) : ArithmeticExpr;
    public sealed record Binary(ArithmeticBinaryOp Op, ArithmeticExpr Left, ArithmeticExpr Right) : ArithmeticExpr;
    public sealed record Conditional(ArithmeticExpr Condition, ArithmeticExpr WhenTrue, ArithmeticExpr WhenFalse) : ArithmeticExpr;
    public sealed record Assignment(string Name, ArithmeticAssignmentOp Op, ArithmeticExpr Value) : ArithmeticExpr;
    public sealed record Increment(string Name, int Delta, bool Prefix) : ArithmeticExpr;
}

public enum ArithmeticUnaryOp { Plus, Negate, LogicalNot, BitwiseNot }

public enum ArithmeticBinaryOp
{
    Comma, LogicalOr, LogicalAnd, BitwiseOr, BitwiseXor, BitwiseAnd,
    Equal, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual,
    ShiftLeft, ShiftRight, Add, Subtract, Multiply, Divide, Modulo, Power,
}

public enum ArithmeticAssignmentOp
{
    Assign, Add, Subtract, Multiply, Divide, Modulo, ShiftLeft, ShiftRight,
    BitwiseAnd, BitwiseOr, BitwiseXor, Power,
}
