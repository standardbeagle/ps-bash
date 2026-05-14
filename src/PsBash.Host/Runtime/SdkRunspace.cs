using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using Microsoft.PowerShell;
using PsBash.Core.Runtime;

namespace PsBash.Host.Runtime;

/// <summary>
/// Creates and owns a single shared PowerShell runspace with PsBash module loaded.
/// PsBash.psm1 is loaded exactly once per SdkRunspace instance.
/// </summary>
internal sealed class SdkRunspace : IAsyncDisposable
{
    private readonly Runspace _runspace;
    private int _disposed;

    // Test seam: tracks how many times the module was initialized.
    internal static int ModuleLoadCount;

    private SdkRunspace(Runspace runspace, ExitTrackingHost host)
    {
        _runspace = runspace;
        Host = host;
    }

    public ExitTrackingHost Host { get; }

    public static SdkRunspace Create()
    {
        var modulePath = ModuleExtractor.ExtractEmbedded();

        // Match the pattern used by PwshTestFixture / CanaryPwshFixture:
        // open with a bare CreateDefault2() (no ImportPSModule on ISS), then
        // load the psm1 by running its content as a script on the first PowerShell
        // instance.  Using iss.ImportPSModule causes NestedModules / FormatsToProcess
        // resolution errors that corrupt the runspace output pipeline even when
        // ExecutionPolicy is Bypass.
        var iss = InitialSessionState.CreateDefault2();
        if (OperatingSystem.IsWindows())
            iss.ExecutionPolicy = ExecutionPolicy.Bypass;

        // On Windows, CreateDefault2() configures many cmdlets for lazy auto-loading
        // from module manifests. Auto-loading finds the Windows PowerShell v5 manifests
        // whose NestedModules reference v5 binary DLLs that use PSSnapIn — a type removed
        // in SMA 7.x — causing TypeLoadException on first use of Write-Output, Get-Location, etc.
        // Fix: enumerate cmdlets from the in-process SMA 7.x assemblies and pre-register
        // them in the ISS so they're immediately available without hitting the module loader.
        RegisterSdkCmdlets(iss);

        var host = new ExitTrackingHost();
        var runspace = RunspaceFactory.CreateRunspace(host, iss);
        runspace.Open();
        try
        {
            runspace.SessionStateProxy.Path.SetLocation(Environment.CurrentDirectory);
        }
        catch
        {
            // Some constrained SDK hosts may not have a FileSystem provider yet.
            // In that case, module commands fall back to .NET's current directory.
        }

        // Extract SdkRunspaceSetup.ps1 next to PsBash.psm1 so the Cmdlets.dll
        // probe runs from a real .ps1 file rather than a C# string. The
        // previous embedded-string pattern caused multiple escaping-bug
        // regressions (see REFACTOR-1 task description) — keep PowerShell
        // logic in .ps1 files where it can be parsed at build time.
        var moduleDir = Path.GetDirectoryName(modulePath)!;
        var setupScriptPath = RunspaceSetupExtractor.Extract(moduleDir);

        // Pass parameters via session-state variables (no string interpolation
        // through C# / quoted PowerShell). $PsBashCmdletsDllPath is currently
        // optional ($null in the SDK host); the script falls back to its
        // Get-Module probe. $PsBashRunspaceSetupPath is consumed by the
        // AddScript dot-source below.
        runspace.SessionStateProxy.SetVariable(
            "PsBashRunspaceSetupPath", setupScriptPath);
        runspace.SessionStateProxy.SetVariable(
            "PsBashCmdletsDllPath", null);

        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        // Dot-source the setup script. No string interpolation; the path lives
        // in a session-state variable, so the original "C# verbatim string ->
        // PowerShell single-quote -> Path.Replace single-quote-to-double-quote"
        // escaping chain is gone.
        ps.AddScript(". $PsBashRunspaceSetupPath").Invoke();
        ps.Commands.Clear();

        var psm1Path = Path.Combine(moduleDir, "PsBash.psm1");
        if (File.Exists(psm1Path))
        {
            var psm1Content = File.ReadAllText(psm1Path);
            ps.AddScript(psm1Content).Invoke();
            ps.Commands.Clear();
        }

        Interlocked.Increment(ref ModuleLoadCount);
        return new SdkRunspace(runspace, host);
    }

    public Runspace Runspace => _runspace;

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _runspace.Dispose();
        return ValueTask.CompletedTask;
    }

    private static void RegisterSdkCmdlets(InitialSessionState iss)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var asmName = assembly.GetName().Name ?? "";
            if (!asmName.StartsWith("Microsoft.PowerShell") &&
                asmName != "System.Management.Automation")
                continue;

            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray()!;
            }

            foreach (var type in types)
            {
                if (type?.IsAbstract != false) continue;
                try
                {
                    if (!typeof(Cmdlet).IsAssignableFrom(type)) continue;
                    var attr = type.GetCustomAttribute<CmdletAttribute>();
                    if (attr == null) continue;
                    iss.Commands.Add(new SessionStateCmdletEntry(
                        $"{attr.VerbName}-{attr.NounName}", type, null));
                }
                catch { }
            }
        }
    }
}
