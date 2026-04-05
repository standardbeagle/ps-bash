namespace PsBash.Core.Parser.Ast;

/// <summary>
/// A redirect operation, e.g. <c>&gt;file</c>, <c>2&gt;&amp;1</c>.
/// Modeled after oils syntax.asdl Redir.
/// </summary>
public sealed record Redirect(string Op, int Fd, CompoundWord Target) : BashNode;

/// <summary>
/// A here-document redirect, e.g. <c>&lt;&lt;EOF\ntext\nEOF</c>.
/// <paramref name="Body"/> is the collected text between delimiters.
/// <paramref name="Expand"/> is true when variable expansion should occur (unquoted delimiter).
/// <paramref name="StripTabs"/> is true for <c>&lt;&lt;-</c> (leading tabs stripped from body).
/// </summary>
public sealed record HereDoc(string Body, bool Expand, bool StripTabs) : BashNode;

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
/// For array assignments like <c>arr=(a b c)</c>, <see cref="ArrayValue"/> is set
/// and <see cref="Value"/> is null.
/// Modeled after oils syntax.asdl AssignPair.
/// </summary>
public sealed record Assignment(
    string Name,
    AssignOp Op,
    CompoundWord? Value,
    ArrayWord? ArrayValue = null) : BashNode;

/// <summary>
/// An environment pair for command prefix, e.g. <c>FOO=bar cmd</c>.
/// Modeled after oils syntax.asdl EnvPair.
/// </summary>
public sealed record EnvPair(string Name, CompoundWord? Value) : BashNode;
