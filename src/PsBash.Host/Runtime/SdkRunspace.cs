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
    private const long MaxStartupModuleBytes = 4 * 1024 * 1024;

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
        // From here on every step (SetLocation, setup script, psm1 load) can throw.
        // The runspace is already OPEN, so a throw must dispose it — otherwise a
        // wedged daemon leaks one opened runspace per pool top-up attempt.
        try
        {
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
            var psm1Content = ReadStartupModule(psm1Path);
            Trace("psm1-read");
            ps.AddScript(psm1Content).Invoke();
            ps.Commands.Clear();
            Trace("psm1-invoked");
        }

        Interlocked.Increment(ref ModuleLoadCount);
        Trace("startup-complete");
        return new SdkRunspace(runspace, host);
        }
        catch
        {
            try { runspace.Dispose(); } catch { }
            throw;
        }
    }

    private static string ReadStartupModule(string path)
    {
        return BoundedTextFile.Read(
            path,
            MaxStartupModuleBytes,
            "Extracted PsBash module exceeds the maximum supported startup size.");
    }

    public Runspace Runspace => _runspace;

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _runspace.Dispose();
        return ValueTask.CompletedTask;
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "SingleFile", "IL3000",
        Justification = "ps-bash-host is published self-contained, NOT single-file, so " +
            "Assembly.Location returns the real SMA path. We need that exact path (not " +
            "AppContext.BaseDirectory) to locate the sibling Microsoft.PowerShell.Commands.* " +
            "DLLs, which differs from the app base dir under framework-dependent deployment.")]
    private static void RegisterSdkCmdlets(InitialSessionState iss)
    {
        // PowerShell SDK cmdlet assemblies (Microsoft.PowerShell.Commands.*)
        // are NOT deployed to the host bin folder — Microsoft.PowerShell.SDK
        // has PrivateAssets="all" in PsBash.Host.csproj, which keeps them out
        // of the deps closure. The first user command (e.g. Write-Output) in
        // our in-process SDK runspace would then trigger SMA's module loader
        // walking $PSModulePath, finding the Windows PowerShell 5 manifest,
        // and throwing TypeLoadException on PSSnapIn (a type removed in SMA
        // 7.x). To avoid that, force the SDK to load Microsoft.PowerShell.
        // Commands.* into the AppDomain by spinning up a throwaway default
        // PowerShell instance and asking it to resolve Write-Output — that
        // routes through SMA's first-time init which knows where to find
        // the Utility assembly in the SDK runtime store. Once loaded, the
        // AppDomain.GetAssemblies() walk below picks up every cmdlet from
        // Utility/Management/Security and registers them in our custom ISS,
        // so the user-facing runspace never has to invoke the broken module
        // auto-loader path. Discovered when SdkWorkerTests.QueryAsync_*
        // started seeing "command was found in module 'Microsoft.PowerShell.
        // Utility' but the module could not be loaded".
        // The PowerShell SDK cmdlet implementation assemblies
        // (Microsoft.PowerShell.Commands.Utility, .Management, .Security)
        // ship under runtimes/{rid}/lib/{tfm}/ in the package layout but
        // `Assembly.Load(name)` doesn't probe that path — only the top-level
        // bin folder. The in-process SDK runspace then falls back to SMA's
        // module loader, which walks $PSModulePath, finds the Windows
        // PowerShell 5 manifest, and throws TypeLoadException on PSSnapIn
        // (removed in SMA 7.x). Discovered when SdkWorkerTests.QueryAsync_*
        // started seeing "command was found in module 'Microsoft.PowerShell.
        // Utility' but the module could not be loaded".
        //
        // Find the SMA assembly's location (always loaded), use it as a
        // hop-off point to the sibling Microsoft.PowerShell.Commands.* dlls
        // in the same runtime store, and Assembly.LoadFrom them explicitly.
        // After that the AppDomain.GetAssemblies() walk below picks up every
        // cmdlet in those assemblies and adds it to ISS, so the runspace
        // never has to invoke the broken module auto-loader.
        try
        {
            var smaPath = typeof(System.Management.Automation.PSObject).Assembly.Location;
            if (!string.IsNullOrEmpty(smaPath))
            {
                var sdkRuntimeDir = Path.GetDirectoryName(smaPath);
                if (sdkRuntimeDir != null)
                {
                    foreach (var fileName in new[]
                    {
                        "Microsoft.PowerShell.Commands.Utility.dll",
                        "Microsoft.PowerShell.Commands.Management.dll",
                        "Microsoft.PowerShell.Security.dll",
                    })
                    {
                        var asmPath = Path.Combine(sdkRuntimeDir, fileName);
                        if (File.Exists(asmPath))
                        {
                            try { System.Reflection.Assembly.LoadFrom(asmPath); }
                            catch { /* assembly load failure — fall through to ISS-only path */ }
                        }
                    }
                }
            }
        }
        catch { /* best-effort: SMA location unavailable in some hosts */ }

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
