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

        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        // Pre-load PsBash.Cmdlets.dll from the installed PsBash module so
        // Invoke-BashSource and hook cmdlets are available without triggering the slow
        // $PSModulePath auto-loading scan. Get-Module -ListAvailable by name is a fast
        // directory lookup. Silent no-op when PsBash is not installed (test environment).
        //
        // Known gap: PsBash.Cmdlets ships as its own PSGallery module, but the
        // workflow change in publish.yml now bundles `PsBash.Cmdlets.dll` inside the
        // staged `PsBash.Cmdlets` PSGallery package alongside its psd1. The host
        // probe below currently looks in the `PsBash` module dir only — the legacy
        // bundled layout — so users on the new PSGallery layout still need the dll
        // copied beside `PsBash.psd1` for the cmdlet path to work. Static-eval
        // inlining in PsEmitter.EmitEval covers the cases where PowerShell expanded
        // `$()` before ps-bash saw the input (the user-reported scenario).
        //
        // Attempting to add a second probe for `PsBash.Cmdlets -ListAvailable` here
        // surfaced a host-startup deadlock on Windows pwsh 7.x SDK that wedged even
        // simple `echo hi` invocations. Left for a future change with deeper
        // investigation; documented here so the gap is not silently rediscovered.
        // First try the binary shipped beside ps-bash-host (post RC-3:
        // Host.csproj copies PsBash.Cmdlets.dll into its OutDir). This works
        // in CI and dev without `Install-Module PsBash` ever running. Fall
        // back to the installed-PSGallery path so end users on the published
        // module continue to load the cmdlets from their installed location.
        var hostDir = Path.GetDirectoryName(typeof(SdkRunspace).Assembly.Location)
            ?? AppContext.BaseDirectory;
        var localCmdlets = Path.Combine(hostDir, "PsBash.Cmdlets.dll");
        var localCmdletsLiteral = localCmdlets.Replace("'", "''");
        ps.AddScript($@"
if (Test-Path '{localCmdletsLiteral}') {{
    Import-Module '{localCmdletsLiteral}' -Force -ErrorAction SilentlyContinue -DisableNameChecking
}} else {{
    $__m = Get-Module PsBash -ListAvailable -ErrorAction SilentlyContinue |
        Sort-Object Version -Descending | Select-Object -First 1
    if ($__m) {{
        $__dll = Join-Path (Split-Path $__m.Path) 'PsBash.Cmdlets.dll'
        if (Test-Path $__dll) {{
            Import-Module $__dll -Force -ErrorAction SilentlyContinue -DisableNameChecking
        }}
    }}
    Remove-Variable -Name __m, __dll -ErrorAction SilentlyContinue
}}
").Invoke();
        ps.Commands.Clear();

        var psm1Path = Path.Combine(Path.GetDirectoryName(modulePath)!, "PsBash.psm1");
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
