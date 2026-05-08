using System.Diagnostics.CodeAnalysis;
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
        LoadKnownPowerShellCommandAssemblies();
        RegisterSdkCmdlets(iss);

        var host = new ExitTrackingHost();
        var runspace = RunspaceFactory.CreateRunspace(host, iss);
        runspace.Open();

        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        ps.AddScript("""
            function global:Set-Location {
                param([Parameter(Position=0)][string]$Path)
                $ExecutionContext.SessionState.Path.SetLocation($Path)
                [Environment]::CurrentDirectory = $ExecutionContext.SessionState.Path.CurrentFileSystemLocation.ProviderPath
            }
            function global:Get-Location {
                $ExecutionContext.SessionState.Path.CurrentLocation
            }
            function global:cd {
                param([Parameter(Position=0)][string]$Path)
                Set-Location $Path
            }
            """).Invoke();
        ps.Commands.Clear();

        var psm1Path = Path.Combine(Path.GetDirectoryName(modulePath)!, "PsBash.psm1");
        if (File.Exists(psm1Path))
        {
            var psm1Content = File.ReadAllText(psm1Path);
            ps.AddScript(psm1Content).Invoke();
            ps.Commands.Clear();
        }

        // Mirror ps-bash-worker.ps1 startup: initialise all bash global state that
        // transpiled scripts reference before any command runs.  Without this the
        // globals are $null under Set-StrictMode -Version Latest and commands like
        // set -e, trap ERR, $#, $@, and shift silently malfunction.
        ps.AddScript("""
            $global:LASTEXITCODE = 0
            $global:__BashErrexit = $false
            $global:__BashTrapEXIT = $null
            $global:__BashTrapERR = $null
            $global:BashFlags = ''
            $global:BashExitCode = 0
            $global:BashPositional = $null
            """).Invoke();
        ps.Commands.Clear();

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

    // GetTypes() is RequiresUnreferencedCode; suppressed because RegisterSdkCmdlets
    // only runs inside the non-AOT host process, never in the AOT launcher.
    [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2026")]
    private static void LoadKnownPowerShellCommandAssemblies()
    {
        foreach (var name in new[]
        {
            "Microsoft.PowerShell.Commands.Management",
            "Microsoft.PowerShell.Commands.Utility",
        })
        {
            try { Assembly.Load(name); }
            catch { }
        }
    }

    [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2026")]
    private static void RegisterSdkCmdlets(InitialSessionState iss)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var asmName = assembly.GetName().Name ?? "";
            if (!asmName.StartsWith("Microsoft.PowerShell") && asmName != "System.Management.Automation")
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
