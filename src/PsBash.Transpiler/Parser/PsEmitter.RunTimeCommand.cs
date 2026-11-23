using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Parser;

public static partial class PsEmitter
{
    /// <summary>
    /// What a command name the emitter maps under ANOTHER command's name runs as when the name is known only at run time
    /// (<c>c=egrep; $c x</c>, <c>"$@"</c>): <c>egrep</c> is <c>Invoke-BashGrep '-E'</c>, <c>gawk</c> <c>Invoke-BashAwk</c>,
    /// <c>sh</c> <c>Invoke-BashBash</c>. Resolve-BashCommand asks this after its own <c>Invoke-Bash&lt;Name&gt;</c> lookup, so a
    /// dynamic name reaches the command of the same table the static emitter uses (<see cref="TryEmitMappedCommand"/>).
    /// The lookup by <c>Invoke-Bash&lt;Name&gt;</c> alone missed these: <c>c=gawk; $c ...</c> ran a <c>gawk</c> program or was
    /// "command not found" where the literal <c>gawk</c> ran Invoke-BashAwk.
    /// <para>
    /// Null when the static form IS <c>Invoke-Bash&lt;Name&gt;</c> (the run-time lookup finds it by name), when the name is a
    /// builtin with a run-time form of its own (<see cref="BashBuiltins.RunAsSource"/>,
    /// <see cref="BashBuiltins.CallStackBuiltins"/>) or when the emitter maps nothing. Otherwise PowerShell script text,
    /// <see cref="PsBuild.MappedCommandStandIn"/>. Guard: Host.Tests DynamicCommandTableGuardTests.
    /// </para>
    /// </summary>
    public static string? RunTimeMappedCommand(string? name)
    {
        if (string.IsNullOrEmpty(name) || BashBuiltins.RunAsSource.Contains(name) || BashBuiltins.CallStackBuiltins.Contains(name))
            return null;
        // The bare name, as IsMappedCommandName asks it: no function of a script shadows the table here (the run-time
        // lookup checked for a bash function first).
        var prior = _definedFunctions;
        _definedFunctions = null;
        string head;
        try
        {
            if (!TryEmitMappedCommand(new Command.Simple([new CompoundWord([new WordPart.Literal(name)])], [], []), out head))
                return null;
        }
        finally { _definedFunctions = prior; }
        if (!head.StartsWith("Invoke-Bash", StringComparison.Ordinal) || PsBuild.IsStatementList(head)
            || string.Equals(head, "Invoke-Bash" + name, StringComparison.OrdinalIgnoreCase))
            return null;
        return PsBuild.MappedCommandStandIn(head);
    }
}
