namespace PsBash.Core.Parser.Ast;

/// <summary>
/// A redirect operation, e.g. <c>&gt;file</c>, <c>2&gt;&amp;1</c>.
/// Modeled after oils syntax.asdl Redir.
/// </summary>
public sealed record Redirect(string Op, int Fd, CompoundWord Target) : BashNode;

/// <summary>
/// Assignment operator: <c>=</c> or <c>+=</c>.
/// </summary>
public enum AssignOp
{
    Equal,
    PlusEqual,
}

/// <summary>
/// A variable assignment, e.g. <c>foo=bar</c> or <c>foo+=baz</c>.
/// Modeled after oils syntax.asdl AssignPair.
/// </summary>
public sealed record Assignment(string Name, AssignOp Op, CompoundWord? Value) : BashNode;

/// <summary>
/// An environment pair for command prefix, e.g. <c>FOO=bar cmd</c>.
/// Modeled after oils syntax.asdl EnvPair.
/// </summary>
public sealed record EnvPair(string Name, CompoundWord? Value) : BashNode;
