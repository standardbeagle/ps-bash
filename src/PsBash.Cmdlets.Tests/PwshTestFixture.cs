using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Central fixture for creating a PowerShell instance with the PsBash module loaded.
/// Handles cross-platform differences (e.g. ExecutionPolicy is Windows-only).
/// </summary>
public static class PwshTestFixture
{
    public static PowerShell Create()
    {
        var iss = InitialSessionState.CreateDefault2();

        // ExecutionPolicy is a Windows-only concept; setting it on Linux/macOS
        // throws PlatformNotSupportedException during runspace.Open().
        if (OperatingSystem.IsWindows())
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;

        var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();

        var pwsh = PowerShell.Create();
        pwsh.Runspace = runspace;

        // Import the binary module manifest from the test output directory.
        // The manifest nests PsBash.psd1 and re-exports its functions globally
        // (fallback for ScriptBlock.Create not binding to private nested scope).
        var cmdletPsd1 = Path.Combine(AppContext.BaseDirectory, "PsBash.Cmdlets.psd1");
        pwsh.AddCommand("Import-Module").AddParameter("Name", cmdletPsd1).Invoke();
        pwsh.Commands.Clear();

        return pwsh;
    }
}
