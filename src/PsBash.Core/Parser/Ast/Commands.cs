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
    public sealed record ShAssignment(ImmutableArray<Assignment> Pairs) : Command;
}
