namespace PsBash.Cmdlets;

// ── Expression AST ─────────────────────────────────────────────────────────

internal abstract class AwkExpr { }

internal sealed class NumLit : AwkExpr { public double Value; }
internal sealed class StrLit : AwkExpr { public string Value = ""; }

/// <summary>A <c>/regex/</c> literal. As a bare expression it means <c>$0 ~ /re/</c>.</summary>
internal sealed class RegexLit : AwkExpr { public string Pattern = ""; }

internal sealed class VarRef : AwkExpr { public string Name = ""; }
internal sealed class FieldRef : AwkExpr { public AwkExpr Index = null!; }
internal sealed class ArrayRef : AwkExpr { public string Name = ""; public List<AwkExpr> Subscripts = new(); }

internal sealed class Assign : AwkExpr { public AwkExpr Target = null!; public string Op = "="; public AwkExpr Value = null!; }
internal sealed class Ternary : AwkExpr { public AwkExpr Cond = null!; public AwkExpr Then = null!; public AwkExpr Else = null!; }
internal sealed class Logical : AwkExpr { public string Op = "&&"; public AwkExpr Left = null!; public AwkExpr Right = null!; }
internal sealed class MatchExpr : AwkExpr { public AwkExpr Left = null!; public AwkExpr Right = null!; public bool Negated; }
internal sealed class InExpr : AwkExpr { public List<AwkExpr> Keys = new(); public string ArrayName = ""; }
internal sealed class Compare : AwkExpr { public string Op = "=="; public AwkExpr Left = null!; public AwkExpr Right = null!; }
internal sealed class Concat : AwkExpr { public AwkExpr Left = null!; public AwkExpr Right = null!; }
internal sealed class Arith : AwkExpr { public char Op; public AwkExpr Left = null!; public AwkExpr Right = null!; }
internal sealed class Power : AwkExpr { public AwkExpr Left = null!; public AwkExpr Right = null!; }
internal sealed class Unary : AwkExpr { public char Op; public AwkExpr Operand = null!; }
internal sealed class IncDec : AwkExpr { public bool Increment; public bool Prefix; public AwkExpr Target = null!; }
internal sealed class Call : AwkExpr { public string Name = ""; public List<AwkExpr> Args = new(); }
internal sealed class Grouping : AwkExpr { public AwkExpr Inner = null!; }

// ── Statement AST ──────────────────────────────────────────────────────────

internal abstract class AwkStmt { }

internal sealed class PrintStmt : AwkStmt { public List<AwkExpr> Args = new(); }
internal sealed class PrintfStmt : AwkStmt { public List<AwkExpr> Args = new(); }
internal sealed class ExprStmt : AwkStmt { public AwkExpr Expr = null!; }
internal sealed class IfStmt : AwkStmt { public AwkExpr Cond = null!; public AwkStmt Then = null!; public AwkStmt? Else; }
internal sealed class WhileStmt : AwkStmt { public AwkExpr Cond = null!; public AwkStmt Body = null!; }
internal sealed class DoWhileStmt : AwkStmt { public AwkStmt Body = null!; public AwkExpr Cond = null!; }
internal sealed class ForStmt : AwkStmt { public AwkStmt? Init; public AwkExpr? Cond; public AwkStmt? Post; public AwkStmt Body = null!; }
internal sealed class ForInStmt : AwkStmt { public string Var = ""; public string ArrayName = ""; public AwkStmt Body = null!; }
internal sealed class BlockStmt : AwkStmt { public List<AwkStmt> Statements = new(); }
internal sealed class NextStmt : AwkStmt { }
internal sealed class NextFileStmt : AwkStmt { }
internal sealed class BreakStmt : AwkStmt { }
internal sealed class ContinueStmt : AwkStmt { }
internal sealed class ExitStmt : AwkStmt { public AwkExpr? Code; }
internal sealed class DeleteStmt : AwkStmt { public string ArrayName = ""; public List<AwkExpr>? Subscripts; }

// ── Program structure ──────────────────────────────────────────────────────

internal enum RuleKind { Begin, End, Always, Expr, Regex }

internal sealed class AwkRule
{
    public RuleKind Kind;
    public AwkExpr? Pattern;     // for Expr/Regex
    public BlockStmt? Action;    // null = default { print $0 }
}

internal sealed class AwkProgram
{
    public List<AwkRule> Begin = new();
    public List<AwkRule> Main = new();
    public List<AwkRule> End = new();
}
