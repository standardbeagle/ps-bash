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

    /// <summary>
    /// Pin the PROCESS-scope execution policy to Bypass before any runspace is
    /// opened. SMA resolves the effective policy from a process-global cache that
    /// is populated lazily on the first runspace open; the warm pool opens several
    /// runspaces CONCURRENTLY (WorkerPool.TopUpWarm on dedicated threads) and that
    /// concurrent first init is racy. When it loses, a runspace resolves to
    /// Restricted, a dot-sourced <c>.ps1</c> (script-file mode) fails to load as a
    /// NON-terminating error, and the host reports exit 0 for a script that calls
    /// <c>exit 42</c>. <c>iss.ExecutionPolicy</c> is per-ISS and does not seed the
    /// process-global cache, so it cannot prevent the race.
    /// <c>PSExecutionPolicyPreference</c> is the documented process-scope source
    /// SMA reads; seeding it once here is deterministic. The launcher also exports
    /// it (PsBash.Shell/Program.cs) so a spawned host inherits it from process
    /// start — SMA snapshots the policy then, so a host that only sets it in its
    /// own Main can still lose the race.
    /// See docs/bugs/script-exit-code-lost-execution-policy-race.md.
    /// </summary>
    internal static void PinProcessExecutionPolicy()
        => Environment.SetEnvironmentVariable("PSExecutionPolicyPreference", "Bypass");

    public static SdkRunspace Create()
    {
        PinProcessExecutionPolicy();

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

        // The session state every runspace starts from is built once per process (RunspaceTemplate): the SDK and
        // PsBash cmdlets plus the psm1's functions and aliases. Only the psm1's residual statements run per runspace.
        var psm1Path = Path.Combine(Path.GetDirectoryName(modulePath)!, "PsBash.psm1");
        var template = RunspaceTemplate.For(
            File.Exists(psm1Path) ? psm1Path : null, ModuleExtractor.GetCmdletsDllPath(), CreateBaseSessionState);
        bool issPreRegistered = template.CmdletsPreRegistered;
        Trace("template-ready");

        var host = new ExitTrackingHost();
        var runspace = RunspaceFactory.CreateRunspace(host, template.NewSessionState());
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

        // $OutputEncoding is how PowerShell encodes the text it PIPES INTO a native command's stdin
        // (`printf '\351' | native`). The default is plain UTF-8, which turns an escaped-byte marker
        // (RawBytes) into U+FFFD; the byte-faithful codec writes the single original byte.
        try { runspace.SessionStateProxy.SetVariable("OutputEncoding", PsBash.Core.RawBytes.Encoding); }
        catch { /* the default UTF-8 is still correct for every valid-UTF-8 payload */ }

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
        // This runspace runs transpiled bash: the shell has already globbed every unquoted operand, so a cmdlet must
        // take what it receives literally (`head 'two-*'` names the file two-*). FileSystemHelpers.OperandsShellExpanded.
        runspace.SessionStateProxy.SetVariable("__BashOperandsExpanded", true);

        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        ps.AddScript(setupScriptContent).Invoke();
        ps.Commands.Clear();
        // After the setup script: the guard hands the retry to the command-not-found handler that script installed.
        GetVerbFallbackGuard.Install(runspace.SessionStateProxy.InvokeCommand);
        Trace("setup-script-invoked");

        if (File.Exists(psm1Path))
        {
            // The psm1 runs as a script here, so it cannot discover its own path;
            // pooled background runspaces need it to import the module.
            runspace.SessionStateProxy.SetVariable("PsBashModulePsm1Path", psm1Path);
            // Its definitions are already in the session state (RunspaceTemplate); run the rest.
            ps.AddScript(template.ResidualScript).Invoke();
            ps.Commands.Clear();
            Trace("psm1-invoked");
        }

        if (File.Exists(psm1Path) && !(template.IsSplit && issPreRegistered))
        {
            // The psm1's Invoke-BashGrep / Invoke-BashSed are literal-argument PROXIES for DIRECT
            // PowerShell calls (they keep the binder out of a repeated `-e`). Transpiled bash never needs
            // them: the emitter single-quotes every flag, so the whole argv reaches the cmdlet verbatim.
            // They are also a steppable-pipeline hop, which makes the real cmdlet's early stop
            // (`grep -q`, `grep -m N`, `sed q`) unable to reach the caller's upstream. Drop them here so a
            // transpiled `producer | grep -q x` resolves straight to the cmdlet and stops the producer.
            // (A split template leaves them out of the session state already: RunspaceTemplate.LiteralArgumentProxies.)
            // Run inside `& { }` so the loop variable is local: a global `$n` would shadow the bash variable `n`
            // (the arithmetic evaluator and `$env`-less lookups read PS variables first).
            ps.AddScript(
                "& { foreach ($proxy in 'Invoke-BashGrep','Invoke-BashSed') { " +
                "if (Microsoft.PowerShell.Core\\Get-Command $proxy -CommandType Cmdlet -ErrorAction SilentlyContinue) { " +
                "Microsoft.PowerShell.Management\\Remove-Item -LiteralPath \"Function:\\$proxy\" -ErrorAction SilentlyContinue } } }").Invoke();
            ps.Commands.Clear();
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

    /// <summary>
    /// The session state before PsBash's own entries: CreateDefault2 with the SDK cmdlets registered. Match the
    /// pattern used by PwshTestFixture / CanaryPwshFixture: no ImportPSModule on the ISS (it causes NestedModules /
    /// FormatsToProcess resolution errors that corrupt the runspace output pipeline even under Bypass).
    /// </summary>
    internal static InitialSessionState CreateBaseSessionState()
    {
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
        RemoveForeignAliases(iss);
        return iss;
    }

    /// <summary>
    /// Drop every alias that is not the bash runtime's own (<see cref="PsBash.Core.PowerShellAliases"/>): PowerShell resolves an alias
    /// before a function or a program, so a built-in one turned a bash command word into a cmdlet (<c>write</c>,
    /// <c>r</c>, <c>start</c>). By index: entries that come from the core snap-in ignore Remove(name, type).
    /// </summary>
    internal static void RemoveForeignAliases(InitialSessionState iss)
    {
        for (int i = iss.Commands.Count - 1; i >= 0; i--)
            if (iss.Commands[i] is SessionStateAliasEntry alias && !PsBash.Core.PowerShellAliases.IsBashRuntimeAlias(alias.Definition))
                iss.Commands.RemoveItem(i);
    }

    /// <summary>
    /// Open the runspaces background jobs will run in (<c>PsBash.Cmdlets.BashJobRunspaces</c>, docs/specs/background-jobs.md
    /// section 12.5) in the background, called when a command that uses <c>&amp;</c> is about to run: its first jobs then
    /// find them ready or opening. Not at host start: opening them beside other commands slowed those (a command-not-found
    /// lookup took 11.7 s instead of 0.45 s), so a host that never runs a job never opens one. Once the spares are open a
    /// call does nothing. The type lives in the cmdlets assembly, which this project loads at run time, not at build time.
    /// Best effort: a failure leaves the jobs to open their own.
    /// </summary>
    internal static void WarmJobRunspaces()
    {
        try { WarmJobRunspacesCall.Value?.Invoke(); }
        catch (Exception) { /* the first job opens a runspace itself */ }
    }

    private static readonly Lazy<Action?> WarmJobRunspacesCall = new(static () =>
    {
        try
        {
            var psm1 = Path.Combine(Path.GetDirectoryName(ModuleExtractor.ExtractEmbedded())!, "PsBash.psm1");
            var cmdlets = Assembly.LoadFrom(ModuleExtractor.GetCmdletsDllPath());
            var warm = cmdlets.GetType("PsBash.Cmdlets.BashJobRunspaces")?.GetMethod("WarmAhead");
            return warm is null ? null : () => warm.Invoke(null, [psm1]);
        }
        catch (Exception)
        {
            return null;
        }
    });

    internal static string ReadStartupModule(string path)
    {
        return BoundedTextFile.Read(
            path,
            MaxStartupModuleBytes,
            "Extracted PsBash module exceeds the maximum supported startup size.");
    }

    public Runspace Runspace => _runspace;

    // Test seam: how many runspaces had background-job state to tear down on release (a runspace that never
    // used `&` has none, so it does not count).
    internal static int BackgroundJobStatesReleased;

    /// <summary>How long releasing a runspace waits for the job teardown (stop + dispose of the jobs).</summary>
    private static readonly TimeSpan BackgroundTeardownTimeout = TimeSpan.FromSeconds(5);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            ReleaseBackgroundJobs();
            _runspace.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The job table of `cmd &amp;` lives in the module's script scope. Disposing the runspace does NOT stop a job
    /// its variables point at, so a job of a discarded runspace (the daemon discards one per <c>-c</c> command) ran
    /// on, holding its job runspace (and, before BashJobRunspaces, a whole private RunspacePool) for the life of the
    /// host. Ask the module to tear its job state down first (<c>Remove-BashBgState</c>: stop + dispose the jobs; a
    /// stopped job's runspace is closed). Best effort and bounded: a runspace that is still busy (an abandoned
    /// command) cannot take another pipeline and is disposed as before.
    /// </summary>
    private void ReleaseBackgroundJobs()
    {
        try
        {
            if (_runspace.RunspaceStateInfo.State != RunspaceState.Opened) return;
            if (_runspace.RunspaceAvailability != RunspaceAvailability.Available) return;
            // `$!` is set the first time anything is backgrounded: a runspace that never used `&`
            // (almost every command) skips the extra pipeline entirely.
            if (_runspace.SessionStateProxy.GetVariable("BashBgLastPid") is null) return;

            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddScript("if (Get-Command Remove-BashBgState -ErrorAction SilentlyContinue) { Remove-BashBgState }");
            var async = ps.BeginInvoke();
            if (!async.AsyncWaitHandle.WaitOne(BackgroundTeardownTimeout))
            {
                try { ps.BeginStop(null, null); } catch { }
                return;
            }
            var output = ps.EndInvoke(async);
            if (output.Count > 0 && output[0]?.BaseObject is true)
                Interlocked.Increment(ref BackgroundJobStatesReleased);
        }
        catch
        {
            // Teardown is advisory: never let it mask the runspace disposal that follows.
        }
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
