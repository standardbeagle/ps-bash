using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Parser;

public static partial class PsEmitter
{
    /// <summary>
    /// True when <paramref name="command"/> sets an ERR trap of its own (<c>trap 'x' ERR</c>, not <c>trap - ERR</c>) at
    /// its own level: in a list, a pipeline, an and-or list, a group or a branch, not in a nested subshell or function.
    /// A command substitution with one runs its statements' trap checks (<c>x=$(trap 'echo E' ERR; false)</c> captures
    /// E, as bash does); without one its body is exempt, since the caller's trap is put aside there
    /// (<c>ErrTrapInheritance</c>).
    /// </summary>
    internal static bool BodySetsErrTrap(BashNode? command) => command switch
    {
        Command.Simple { Words.Length: > 2 } s => GetLiteralValue(s.Words[0]) == "trap"
            && GetLiteralValue(s.Words[^1]) == "ERR"
            && GetLiteralValue(s.Words[1]) != "-",
        Command.Pipeline p => p.Commands.Any(BodySetsErrTrap),
        Command.AndOrList a => a.Commands.Any(BodySetsErrTrap),
        Command.CommandList l => l.Commands.Any(BodySetsErrTrap),
        Command.BraceGroup b => BodySetsErrTrap(b.Body),
        Command.If i => i.Arms.Any(arm => BodySetsErrTrap(arm.Cond) || BodySetsErrTrap(arm.Body)) || BodySetsErrTrap(i.ElseBody),
        _ => false,
    };
}
