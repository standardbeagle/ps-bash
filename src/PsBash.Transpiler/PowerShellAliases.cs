namespace PsBash.Core;

/// <summary>
/// Which PowerShell aliases a bash runspace keeps. A command word the emitter does not map is looked up by PowerShell,
/// and PowerShell resolves an alias before a function or a program, so every built-in alias was a bash command:
/// <c>write -- $x</c> ran <c>Write-Output</c> and printed <c>--</c> (oils <c>hay.spec.sh</c>, Windows fleet s22e), <c>r</c>
/// re-ran the previous command (<c>Invoke-History</c>), <c>start</c>, <c>del</c>, <c>iex</c>, <c>mount</c> ran cmdlets, and
/// <c>where</c> hid <c>where.exe</c>. In bash such a word is a function, a program on PATH, or "command not found".
/// <para>
/// Rule: a bash runspace's session state holds only the runtime's own aliases, the ones that name an
/// <c>Invoke-Bash*</c> command. Both builders drop the rest before the module's entries go in
/// (<c>SdkRunspace.CreateBaseSessionState</c>, <c>BashJobRunspaces.InitialState</c>). The module itself removes only the
/// aliases it replaces: imported into a user's own pwsh, it must leave <c>gci</c> and <c>%</c> alone. ps-bash's own
/// PowerShell uses no alias (Core.Tests/PowerShellAliasGuardTests). Spec: emitter-strategy.md section 2.3.
/// </para>
/// </summary>
public static class PowerShellAliases
{
    /// <summary>True when an alias defined as <paramref name="definition"/> is the bash runtime's own and stays.</summary>
    public static bool IsBashRuntimeAlias(string? definition)
        => definition is not null && definition.StartsWith("Invoke-Bash", StringComparison.OrdinalIgnoreCase);
}
