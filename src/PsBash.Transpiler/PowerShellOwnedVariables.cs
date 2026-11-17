namespace PsBash.Core;

/// <summary>
/// PowerShell's automatic and preference variables. A bash variable of such a name (<c>input=4</c>, <c>host=3</c>,
/// <c>for pid in ...</c>) is the shell's: it lives in the shell variable store, and whatever PowerShell keeps under the
/// name (the pipeline enumerator, <c>$Host</c>, the read-only <c>$PID</c>) is never its value. PowerShell names are
/// case-insensitive, so the match is too.
/// <para>The emitter never names a PowerShell variable after one (a loop iterates a private variable, a background
/// job reads the shell variable), and <c>PsBash.Cmdlets.BashVariableLookup</c> never reads one as a bash value
/// (docs/specs/environment-model.md section 8).</para>
/// </summary>
public static class PowerShellOwnedVariables
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "PID", "HOME", "Host", "true", "false", "null", "ShellId", "PSHOME", "ExecutionContext", "PSCulture",
        "PSUICulture", "PSVersionTable", "PSEdition", "IsWindows", "IsLinux", "IsMacOS", "IsCoreCLR", "Error",
        "input", "args", "this", "Matches", "foreach", "switch", "PSItem", "LASTEXITCODE", "MyInvocation",
        "PSScriptRoot", "PSCommandPath", "PSBoundParameters", "PSCmdlet", "StackTrace", "NestedPromptLevel",
        "ConsoleFileName", "PSDebugContext", "EnabledExperimentalFeatures", "Event", "EventArgs", "EventSubscriber",
        "Sender", "PWD", "OFS", "PSDefaultParameterValues",
    };

    /// <summary>True when <paramref name="name"/> is a PowerShell automatic or preference variable.</summary>
    public static bool Contains(string name) => Names.Contains(name);
}
