using System.Collections.Immutable;

namespace PsBash.Core.Parser.Ast;

/// <summary>
/// Base type for all command nodes.
/// Modeled after oils syntax.asdl command variants.
/// </summary>
public abstract record Command : BashNode
{
    /// <summary>
    /// A simple command: words with optional environment pairs and redirects.
    /// Modeled after oils command.Simple.
    /// </summary>
    public sealed record Simple(
        ImmutableArray<CompoundWord> Words,
        ImmutableArray<EnvPair> EnvPairs,
        ImmutableArray<Redirect> Redirects) : Command;

    /// <summary>
    /// A pipeline of commands connected by <c>|</c> or <c>|&amp;</c>.
    /// Modeled after oils command.Pipeline.
    /// </summary>
    public sealed record Pipeline(
        ImmutableArray<Command> Commands,
        ImmutableArray<string> Ops,
        bool Negated) : Command;

    /// <summary>
    /// Commands joined by <c>&amp;&amp;</c> or <c>||</c>.
    /// Modeled after oils command.AndOr.
    /// </summary>
    public sealed record AndOrList(
        ImmutableArray<Command> Commands,
        ImmutableArray<string> Ops) : Command;

    /// <summary>
    /// A list of commands separated by <c>;</c> or newline.
    /// Modeled after oils command.CommandList.
    /// </summary>
    public sealed record CommandList(ImmutableArray<Command> Commands) : Command;

    /// <summary>
    /// A bare assignment command, e.g. <c>x=1 y=2</c>.
    /// Modeled after oils command.ShAssignment.
    /// </summary>
    public sealed record ShAssignment(
        ImmutableArray<Assignment> Pairs,
        bool IsLocal = false) : Command;

    /// <summary>
    /// An if/elif/else statement.
    /// Modeled after oils command.If.
    /// </summary>
    public sealed record If(
        ImmutableArray<IfArm> Arms,
        Command? ElseBody) : Command;

    /// <summary>
    /// A test expression: <c>[ ... ]</c> or <c>[[ ... ]]</c>.
    /// The inner words are stored without the surrounding brackets.
    /// </summary>
    public sealed record BoolExpr(
        ImmutableArray<CompoundWord> Inner,
        bool Extended) : Command;

    /// <summary>
    /// A for-in loop: <c>for x in a b c; do body; done</c>.
    /// An empty list means implicit <c>$@</c> (<c>for x; do ...</c>).
    /// </summary>
    public sealed record ForIn(
        string Var,
        ImmutableArray<CompoundWord> List,
        Command Body) : Command;

    /// <summary>
    /// A C-style arithmetic for loop: <c>for ((init; cond; step)); do body; done</c>.
    /// The three clauses are stored as raw strings.
    /// </summary>
    public sealed record ForArith(
        string Init,
        string Cond,
        string Step,
        Command Body) : Command;

    /// <summary>
    /// A while or until loop: <c>while cmd; do body; done</c> / <c>until cmd; do body; done</c>.
    /// When <paramref name="IsUntil"/> is true, the condition is logically negated.
    /// </summary>
    public sealed record While(
        bool IsUntil,
        Command Cond,
        Command Body) : Command;

    /// <summary>
    /// A case/esac statement: <c>case expr in pattern) body;; esac</c>.
    /// Maps to PowerShell <c>switch</c>.
    /// </summary>
    public sealed record Case(
        CompoundWord Expr,
        ImmutableArray<CaseArm> Arms) : Command;

    /// <summary>
    /// A standalone arithmetic command: <c>(( expr ))</c>.
    /// Maps to PowerShell arithmetic expression or comparison.
    /// </summary>
    public sealed record ArithCommand(string Expr) : Command;

    /// <summary>
    /// A function definition: <c>function name { body }</c> or <c>name() { body }</c>.
    /// Maps to PowerShell <c>function name { body }</c>.
    /// </summary>
    public sealed record ShFunction(
        string Name,
        Command Body) : Command;

    /// <summary>
    /// A subshell: <c>(cmd1; cmd2)</c> runs commands in a child process.
    /// Maps to PowerShell <c>&amp; { cmd1; cmd2 }</c>.
    /// </summary>
    public sealed record Subshell(
        Command Body,
        ImmutableArray<Redirect> Redirects) : Command;

    /// <summary>
    /// A brace group: <c>{ cmd1; cmd2; }</c> runs commands in the current shell.
    /// Emitted inline (passthrough, same scope).
    /// </summary>
    public sealed record BraceGroup(Command Body) : Command;
}

/// <summary>
/// A single arm of an if/elif chain: condition plus body.
/// </summary>
public sealed record IfArm(Command Cond, Command Body) : BashNode;

/// <summary>
/// A single arm of a case/esac statement: one or more patterns and a body.
/// </summary>
public sealed record CaseArm(
    ImmutableArray<string> Patterns,
    Command Body) : BashNode;
