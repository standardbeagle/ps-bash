using System.Management.Automation.Runspaces;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>
/// What every session state a runspace runs bash code in needs, for the builders in this assembly (in-process jobs,
/// <c>BashJobRunspaces.InitialState</c>; an unbounded pipe producer, <c>New-BashLazyStdin</c>). The shell's own runspace
/// is built in the host (<c>SdkRunspace.CreateBaseSessionState</c>), which applies the same rule; guard
/// <c>Core.Tests/PowerShellAliasGuardTests</c>.
/// </summary>
public static class BashSessionState
{
    /// <summary>
    /// Drop every alias that is not the bash runtime's own (<see cref="PowerShellAliases"/>): PowerShell resolves an alias
    /// before a function or a program, so a built-in one turned a bash command word into a cmdlet (<c>write</c>,
    /// <c>r</c>, <c>start</c>). By index: entries that come from the core snap-in ignore Remove(name, type).
    /// </summary>
    public static void RemoveForeignAliases(InitialSessionState iss)
    {
        ArgumentNullException.ThrowIfNull(iss);
        for (int i = iss.Commands.Count - 1; i >= 0; i--)
            if (iss.Commands[i] is SessionStateAliasEntry alias && !PowerShellAliases.IsBashRuntimeAlias(alias.Definition))
                iss.Commands.RemoveItem(i);
    }
}
