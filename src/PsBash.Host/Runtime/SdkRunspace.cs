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
        // Per-phase instrumentation. Enabled with PSBASH_TRACE_STARTUP=1.
        // Writes "[ps-bash-host trace] <phase> +<ms>ms cum=<ms>ms pid=<pid>"
        // to stderr. Used to diagnose host-startup time under parallel test
        // load (task #9). Stderr is unredirected by the launcher's spawn
        // path, so these lines reach the testhost-attached console without
        // colliding with the IPC stream.
        var trace = Environment.GetEnvironmentVariable("PSBASH_TRACE_STARTUP") == "1";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long last = 0;
        int pid = Environment.ProcessId;
        void Trace(string phase)
        {
            if (!trace) return;
            var now = sw.ElapsedMilliseconds;
            var delta = now - last;
            last = now;
            Console.Error.WriteLine(
                $"[ps-bash-host trace] {phase} +{delta}ms cum={now}ms pid={pid}");
        }
        Trace("startup-begin");

        var modulePath = ModuleExtractor.ExtractEmbedded();
        Trace("module-extracted");

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
        Trace("iss-cmdlets-registered");

        // Pre-register PsBash.Cmdlets via ISS to skip the Import-Module path
        // entirely. Import-Module of a binary DLL ran 1.8-5.7 s under parallel
        // load (host startup #9); ISS pre-registration is essentially free
        // because the assembly is already in the AppDomain (or LoadFrom is
        // a single file open). The setup script remains responsible for the
        // CommandNotFoundAction handler, but no longer does Import-Module.
        var cmdletsDll = ModuleExtractor.GetCmdletsDllPath();
        bool issPreRegistered = false;
        if (File.Exists(cmdletsDll))
        {
            try
            {
                var asm = System.Reflection.Assembly.LoadFrom(cmdletsDll);
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray()!;
                }
                int registered = 0;
                foreach (var type in types)
                {
                    if (type?.IsAbstract != false) continue;
                    if (!typeof(Cmdlet).IsAssignableFrom(type)) continue;
                    var attr = type.GetCustomAttribute<CmdletAttribute>();
                    if (attr == null) continue;
                    iss.Commands.Add(new SessionStateCmdletEntry(
                        $"{attr.VerbName}-{attr.NounName}", type, null));
                    registered++;
                }
                issPreRegistered = registered > 0;
            }
            catch
            {
                // Fall back to the script-side Import-Module probe in psm1.
            }
        }
        Trace("iss-psbash-cmdlets-registered");

        var host = new ExitTrackingHost();
        var runspace = RunspaceFactory.CreateRunspace(host, iss);
        runspace.Open();
        Trace("runspace-opened");
        try
        {
            runspace.SessionStateProxy.Path.SetLocation(Environment.CurrentDirectory);
        }
        catch
        {
            // Some constrained SDK hosts may not have a FileSystem provider yet.
            // In that case, module commands fall back to .NET's current directory.
        }

        // Pull the setup script content directly from the embedded resource
        // instead of extract-to-disk + dot-source-from-disk. The previous
        // path paid ~600 ms on every cold start for file I/O + path
        // resolution + dot-source overhead even after the Import-Module body
        // became a gated no-op (host startup #9). The setup script content is
        // small (~30 effective lines: CommandNotFoundAction handler + gated
        // Import-Module fallback) and embedding it as an AddScript string
        // skips both filesystem operations and dot-source semantics. The
        // disk-extracted copy still serves the syntax-check unit test path.
        var setupScriptContent = RunspaceSetupExtractor.ReadEmbedded();

        // Canonical module-load path (REFACTOR-5): PsBash.Cmdlets.dll is
        // embedded in PsBash.Core and extracted by ModuleExtractor alongside
        // the psm1. The cmdlets are pre-registered in ISS above; the setup
        // script's Import-Module is gated on Get-Command not seeing them, so
        // this variable is only consulted on the fallback path.
        var cmdletsDllPath = ModuleExtractor.GetCmdletsDllPath();

        runspace.SessionStateProxy.SetVariable(
            "PsBashCmdletsDllPath", cmdletsDllPath);
        // Tell the setup script whether ISS pre-reg has already brought in
        // the binary cmdlets. When true, the script can skip both the
        // Get-Command probe (~300 ms cold-runspace JIT cost) and the
        // Import-Module fallback. False keeps the existing probe-and-import
        // behaviour for SDK callers who skipped ISS pre-reg.
        runspace.SessionStateProxy.SetVariable(
            "PsBashCmdletsAlreadyLoaded", issPreRegistered);

        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        ps.AddScript(setupScriptContent).Invoke();
        ps.Commands.Clear();
        Trace("setup-script-invoked");

        var psm1Path = Path.Combine(Path.GetDirectoryName(modulePath)!, "PsBash.psm1");
        if (File.Exists(psm1Path))
        {
            var psm1Content = File.ReadAllText(psm1Path);
            Trace("psm1-read");
            ps.AddScript(psm1Content).Invoke();
            ps.Commands.Clear();
            Trace("psm1-invoked");
        }

        Interlocked.Increment(ref ModuleLoadCount);
        Trace("startup-complete");
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
