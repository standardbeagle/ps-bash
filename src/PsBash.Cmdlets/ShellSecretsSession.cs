using System.Management.Automation;
using System.Management.Automation.Runspaces;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>
/// What an embedding does to give a session runspace secrets (<see cref="ShellSecrets"/>) that never enter the shared
/// process environment. Spec: <c>docs/specs/environment-model.md</c> section 13.
/// <code>
/// ShellSecretsSession.UseShellEnvironmentDrive(iss);          // before the runspace opens
/// var rs = RunspaceFactory.CreateRunspace(host, iss); rs.Open();
/// // import the module, then once in the runspace:
/// ShellSecretsSession.Install(rs.SessionStateProxy.InvokeCommand);
/// var secrets = new ShellSecrets(); secrets.Set("OPENAI_API_KEY", leased);
/// ShellSecrets.Register(rs, secrets);
/// </code>
/// </summary>
public static class ShellSecretsSession
{
    /// <summary>
    /// The runspace's <c>Env:</c> drive becomes ps-bash's shell store (<see cref="JobEnvironmentProvider"/>) in place of
    /// PowerShell's Environment provider, so <c>$env:KEY</c> in emitted code and in PowerShell-mode lines reads the
    /// session's secret, and a write to it stays in the session. Also what a background job's runspace is opened with.
    /// </summary>
    public static void UseShellEnvironmentDrive(InitialSessionState iss)
    {
        ArgumentNullException.ThrowIfNull(iss);
        InstallOwner();
        // By index: CreateDefault's entries come from the core snap-in, and Remove(name, type) leaves them in place (the
        // Env drive then stays PowerShell's, measured).
        for (int i = iss.Providers.Count - 1; i >= 0; i--)
            if (iss.Providers[i].Name == "Environment") iss.Providers.RemoveItem(i);
        iss.Providers.Add(new SessionStateProviderEntry(JobEnvironmentProvider.ProviderName, typeof(JobEnvironmentProvider), null));
    }

    /// <summary>
    /// Once per runspace, after the module is imported: a program the session starts gets its environment from ps-bash
    /// (<see cref="BashVariableStore.ApplyTo"/>, so the granted secrets) instead of from PowerShell's native-command
    /// processor, which copies the process environment. The same hook as <see cref="HostTerminalLookup.Install"/>;
    /// installing either installs both. Any lookup action the host installed before runs first.
    /// </summary>
    public static void Install(CommandInvocationIntrinsics invoke)
    {
        InstallOwner();
        HostTerminalLookup.Install(invoke);
    }

    /// <summary>A scalar store and a session's secrets are found by the runspace of the calling thread.</summary>
    internal static void InstallOwner() => ScalarOverlay.CurrentOwner ??= static () => Runspace.DefaultRunspace;
}
