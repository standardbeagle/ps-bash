using System.Collections.Immutable;
using PsBash.Core.Parser;

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
        ImmutableArray<Redirect> Redirects,
        ImmutableArray<HereDoc> HereDocs = default,
        int Line = 0) : Command;

    /// <summary>
    /// A pipeline of commands connected by <c>|</c> or <c>|&amp;</c>.
    /// <see cref="Time"/> is the <c>time</c> / <c>time -p</c> keyword in front of it.
    /// Modeled after oils command.Pipeline.
    /// </summary>
    public sealed record Pipeline(
        ImmutableArray<Command> Commands,
        ImmutableArray<string> Ops,
        bool Negated,
        PipelineTime Time = PipelineTime.None) : Command;

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
    /// A bare assignment command, e.g. <c>x=1 y=2</c>, or <c>local</c> / <c>export</c> NAME=VAL.
    /// <see cref="IsExport"/> matters for errexit: <c>export X=$(false)</c> has the status of
    /// <c>export</c> (0), unlike a bare <c>X=$(false)</c>.
    /// Modeled after oils command.ShAssignment.
    /// </summary>
    public sealed record ShAssignment(
        ImmutableArray<Assignment> Pairs,
        bool IsLocal = false,
        bool IsExport = false,
        bool IsReadonly = false,
        bool IsNameref = false,
        bool IsAssoc = false,
        bool IsDeclare = false,
        int Line = 0,
        // `declare -a`: an INDEXED array is declared (refused on an associative one).
        bool IsIndexed = false,
        // `declare -A m=(…) 2>/dev/null`: the builtin's own redirects (its error goes there).
        ImmutableArray<Redirect> Redirects = default) : Command;

    /// <summary>
    /// An if/elif/else statement.
    /// Modeled after oils command.If.
    /// </summary>
    public sealed record If(
        ImmutableArray<IfArm> Arms,
        Command? ElseBody,
        ImmutableArray<Redirect> Redirects = default) : Command;

    /// <summary>
    /// A test expression: <c>[ ... ]</c> or <c>[[ ... ]]</c>.
    /// The inner words are stored without the surrounding brackets.
    /// <see cref="Line"/> (here and on <see cref="ForIn"/>, <see cref="ForArith"/>, <see cref="Case"/>,
    /// <see cref="ArithCommand"/>) is the source line the command starts on, for <c>trap DEBUG</c>; 0 = unknown.
    /// </summary>
    public sealed record BoolExpr(
        ImmutableArray<CompoundWord> Inner,
        bool Extended,
        int Line = 0) : Command;

    /// <summary>
    /// A for-in loop: <c>for x in a b c; do body; done</c>.
    /// An empty list means implicit <c>$@</c> (<c>for x; do ...</c>).
    /// </summary>
    public sealed record ForIn(
        string Var,
        ImmutableArray<CompoundWord> List,
        Command Body,
        ImmutableArray<Redirect> Redirects = default,
        bool ExplicitIn = false,
        int Line = 0) : Command;

    /// <summary>
    /// A <c>select</c> menu loop: <c>select x in a b c; do body; done</c> —
    /// identical grammar to <see cref="ForIn"/>. ps-bash does not implement the
    /// interactive menu/REPL behavior; the emitter degrades it to a documented
    /// comment (rather than aborting the whole transpile) so the surrounding
    /// script still transpiles.
    /// </summary>
    public sealed record Select(
        string Var,
        ImmutableArray<CompoundWord> List,
        Command Body) : Command;

    /// <summary>
    /// A C-style arithmetic for loop: <c>for ((init; cond; step)); do body; done</c>.
    /// Empty clauses are represented by <see langword="null"/>; non-empty clauses retain
    /// both their typed arithmetic tree and exact source text.
    /// </summary>
    public sealed record ForArith(
        ArithmeticSyntax? Init,
        ArithmeticSyntax? Cond,
        ArithmeticSyntax? Step,
        Command Body,
        ImmutableArray<Redirect> Redirects = default,
        int Line = 0) : Command
    {
        /// <summary>
        /// Legacy source-compatible constructor; empty clauses remain explicitly absent.
        /// Migration note: the primary properties are intentionally typed; use <c>Init?.Source</c>,
        /// <c>Cond?.Source</c>, and <c>Step?.Source</c> when source text is required.
        /// </summary>
        public ForArith(string init, string cond, string step, Command body, ImmutableArray<Redirect> redirects = default)
            : this(ParseOptional(init), ParseOptional(cond), ParseOptional(step), body, redirects) { }

        private static ArithmeticSyntax? ParseOptional(string source) =>
            string.IsNullOrWhiteSpace(source) ? null : BashArithmeticParser.Parse(source);
    }

    /// <summary>
    /// A while or until loop: <c>while cmd; do body; done</c> / <c>until cmd; do body; done</c>.
    /// When <paramref name="IsUntil"/> is true, the condition is logically negated.
    /// </summary>
    public sealed record While(
        bool IsUntil,
        Command Cond,
        Command Body,
        ImmutableArray<Redirect> Redirects = default) : Command;

    /// <summary>
    /// A case/esac statement: <c>case expr in pattern) body;; esac</c>.
    /// Maps to PowerShell <c>switch</c>.
    /// </summary>
    public sealed record Case(
        CompoundWord Expr,
        ImmutableArray<CaseArm> Arms,
        ImmutableArray<Redirect> Redirects = default,
        int Line = 0) : Command;

    /// <summary>
    /// A standalone arithmetic command: <c>(( expr ))</c>.
    /// Maps to PowerShell arithmetic expression or comparison.
    /// </summary>
    public sealed record ArithCommand(ArithmeticSyntax Expr, int Line = 0) : Command
    {
        /// <summary>
        /// Legacy source-compatible constructor. Migration note: the typed <see cref="Expr"/>
        /// property is intentional; use <c>Expr.Source</c> when source text is required.
        /// </summary>
        public ArithCommand(string expr) : this(BashArithmeticParser.Parse(expr)) { }
    }

    /// <summary>
    /// A function definition: <c>function name { body }</c> or <c>name() { body }</c>.
    /// Maps to PowerShell <c>function name { body }</c>.
    /// </summary>
    public sealed record ShFunction(
        string Name,
        Command Body,
        // The body exactly as written (`{ echo hi; }`, `( cd x && ls )`), kept so `declare -f` can print bash, not the
        // emitted PowerShell. Null when it cannot be sliced whole (a here-document body that lies after the function).
        string? Source = null) : Command
    {
        // The source text is presentation only: two definitions with the same name and body are the same definition.
        public bool Equals(ShFunction? other) => other is not null && Name == other.Name && Body.Equals(other.Body);
        public override int GetHashCode() => HashCode.Combine(Name, Body);
    }

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
    public sealed record BraceGroup(
        Command Body,
        ImmutableArray<Redirect> Redirects = default) : Command;

    /// <summary>
    /// A background command: <c>cmd &amp;</c> runs the inner command asynchronously.
    /// The <c>&amp;</c> operator is consumed in <c>ParseList()</c> and wraps the
    /// preceding command in this node.
    /// </summary>
    public sealed record Background(Command Inner) : Command;
}

/// <summary>
/// A single arm of an if/elif chain: condition plus body.
/// </summary>
public sealed record IfArm(Command Cond, Command Body) : BashNode;

/// <summary>
/// How a case arm terminates, which controls fall-through:
/// <list type="bullet">
/// <item><see cref="Break"/> — <c>;;</c>: run this arm only, then leave.</item>
/// <item><see cref="FallThrough"/> — <c>;&amp;</c>: run this arm's body then the
/// NEXT arm's body unconditionally (bash 4 fall-through).</item>
/// <item><see cref="ContinueTest"/> — <c>;;&amp;</c>: run this arm, then keep
/// testing the remaining patterns.</item>
/// </list>
/// </summary>
public enum CaseTerminator { Break, FallThrough, ContinueTest }

/// <summary>
/// A single arm of a case/esac statement: one or more patterns, a body, and the
/// terminator controlling fall-through. Defaults to <see cref="CaseTerminator.Break"/>
/// (<c>;;</c>) so existing constructions are unaffected.
/// </summary>
public sealed record CaseArm(
    ImmutableArray<string> Patterns,
    Command Body,
    CaseTerminator Terminator = CaseTerminator.Break) : BashNode;

/// <summary>The <c>time</c> keyword before a pipeline: none, <c>time</c> (TIMEFORMAT / bash's default report), or <c>time -p</c> (POSIX report).</summary>
public enum PipelineTime { None, Default, Posix }
