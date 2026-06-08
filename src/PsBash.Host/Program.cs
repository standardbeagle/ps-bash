using PsBash.Core.Runtime;
using PsBash.Core.Runtime.Ipc;
using PsBash.Host.Runtime;
using PsBash.Host.Server;
using PsBash.Host.Shell;

namespace PsBash.Host;

internal sealed class Program
{
    static async Task<int> Main(string[] args)
    {
        // Raise the thread-pool floor. The pool may have runspaces warming while
        // accepted connections and IPC output writer tasks are active; a too-small
        // starting pool can make the runtime slow-grow under bursty launchers. A
        // modest floor avoids the stall without committing the threads unless
        // they are used. (Worker pool create is on dedicated LongRunning threads,
        // so this is defense-in-depth.)
        {
            ThreadPool.GetMinThreads(out var minW, out var minIo);
            ThreadPool.SetMinThreads(Math.Max(minW, 16), Math.Max(minIo, 16));
        }

        // Detach inherited launcher stdio when spawned as a daemon. See
        // InheritedFdDetach for the rationale and the RC-5 macOS fix.
        InheritedFdDetach.DetachInheritedStdioIfRequested();
        // PTY-2 probe mode: spawned by PtySpawnTests to verify the host's
        // System.Console is wired to a real terminal when invoked through
        // PtySpawner. Writes a single marker line to stdout and exits 0.
        // No runspace, no IPC — just enough to assert IsInputRedirected /
        // IsOutputRedirected and observe the PSBASH_PTY_ATTACHED hand-off
        // env var. Intentionally runs BEFORE --interactive handling so a
        // misconfigured CI environment cannot accidentally enter the REPL.
        if (args.Contains("--pty-probe"))
        {
            var inRedir = Console.IsInputRedirected;
            var outRedir = Console.IsOutputRedirected;
            var ptyEnv = Environment.GetEnvironmentVariable("PSBASH_PTY_ATTACHED") ?? "<unset>";
            Console.Out.WriteLine(
                $"PSBASH-PTY-PROBE: IsInputRedirected={inRedir} IsOutputRedirected={outRedir} PSBASH_PTY_ATTACHED={ptyEnv}");
            Console.Out.Flush();
            return 0;
        }

        // Interactive mode: host owns the tty; run the REPL directly. The
        // launcher (ps-bash with no -c) spawns us in this mode and waits.
        if (args.Contains("--interactive"))
        {
            var noProfile = args.Contains("--no-profile");
            int? iLauncherPid = GetArgInt(args, "--launcher-pid");

            var priorInteractive = Environment.GetEnvironmentVariable("PSBASH_INTERACTIVE");
            Environment.SetEnvironmentVariable("PSBASH_INTERACTIVE", "1");
            using var iCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; iCts.Cancel(); };
            await using var iDeathWatcher = ParentDeathWatcher.TryCreate(iLauncherPid, iCts);

            // Startup type-ahead: build the SDK runspace (the slow part) on a background thread so the
            // REPL can draw its prompt and accept keystrokes while it warms up. RunAsync awaits this
            // task before executing each command; we own its disposal here.
            var interactiveWorkerTask = Task.Run(() => (IWorker)SdkWorker.Create());
            try
            {
                return await InteractiveShell.RunAsync(interactiveWorkerTask, noProfile);
            }
            finally
            {
                try
                {
                    var w = await interactiveWorkerTask;
                    if (w is IAsyncDisposable d) await d.DisposeAsync();
                }
                catch { /* runspace never came up, or already torn down */ }
                Environment.SetEnvironmentVariable("PSBASH_INTERACTIVE", priorInteractive);
            }
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // Warm a pool of isolated runspaces in the background — runspace creation
        // is the slow part (~2-8 s) and the pool keeps spares hot so steady-state
        // command latency is near zero. The transport starts listening while the
        // first runspace warms so clients can connect immediately and queue work;
        // each connection checks out its own isolated worker (clean session per
        // command, concurrent across launchers). Sized from the environment
        // (PSBASH_POOL_WARM / PSBASH_POOL_MAX).
        await using var pool = WorkerPool.FromEnvironment();

        var (transport, scheme, endpoint) = CreateTransport(args);

        var idleTimeout = IdleShutdown.DefaultTimeout;
        using var idle = new IdleShutdown(cts, idleTimeout);

        int? launcherPid = GetNonInteractiveLauncherPid(args);
        await using var deathWatcher = ParentDeathWatcher.TryCreate(launcherPid, cts);

        await using var server = new HostServer(transport, pool, idle);

        var serverTask = server.RunAsync(cts.Token);
        await server.WhenListening;
        // Sidecar must be written AFTER bind succeeds — otherwise a launcher
        // racing with us would read metadata for a process that hasn't yet
        // claimed the endpoint. Per docs/specs/host-lifecycle-contract.md.
        WriteHostMetadata(scheme, endpoint);
        try { await serverTask; }
        finally { HostMetadata.Remove(scheme, endpoint); }

        return 0;
    }

    private static void WriteHostMetadata(string scheme, string endpoint)
    {
        var meta = new HostMetadata(
            Pid: Environment.ProcessId,
            ExecutablePath: Environment.ProcessPath ?? "<unknown>",
            ProtocolVersion: HostProtocol.ProtocolVersion,
            BuildIdentity: HostProtocol.BuildIdentity,
            TransportScheme: scheme,
            Endpoint: endpoint,
            StartedAt: DateTimeOffset.UtcNow,
            Owner: Environment.UserName);
        meta.Write(scheme, endpoint);
    }

    private static (IIpcTransport Transport, string Scheme, string Endpoint) CreateTransport(string[] args)
    {
        // Scheme-specific overrides (used by tests) take precedence and bypass
        // the factory entirely so they cannot be shadowed by env vars.
        var socketPath = GetArg(args, "--socket");
        if (socketPath != null) return (new UnixSocketTransport(socketPath), "unix", socketPath);

        var pipeName = GetArg(args, "--pipe");
        if (pipeName != null) return (new NamedPipeTransport(pipeName), "pipe", pipeName);

        // --ipc-endpoint <scheme:endpoint> is the public/agent-facing override:
        // tests, WSL bash drivers, or anyone who needs an isolated host can
        // pass it on both the host and the launcher (via PSBASH_IPC_ENDPOINT)
        // to bind the same address. Falls through to the canonical endpoint
        // when absent.
        var ipcEndpoint = GetArg(args, "--ipc-endpoint");
        var (scheme, endpoint) = IpcTransportFactory.ResolveEndpoint(ipcEndpoint);
        return (IpcTransportFactory.CreateDefault(ipcEndpoint), scheme, endpoint);
    }

    // Accepts both `--name value` (two-token) and `--name=value` (single-token,
    // equals form). The actual launcher (src/PsBash.Shell/Program.cs) emits the
    // equals form for --launcher-pid, so the two-token-only variant silently
    // disarmed the parent-death watcher in production. See task Zjvk1UwhQiHG.
    private static string? GetArg(string[] args, string name)
    {
        var prefix = name + "=";
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == name && i + 1 < args.Length) return args[i + 1];
            if (args[i].StartsWith(prefix, StringComparison.Ordinal))
                return args[i].Substring(prefix.Length);
        }
        return null;
    }

    private static int? GetArgInt(string[] args, string name)
    {
        var val = GetArg(args, name);
        return int.TryParse(val, out var n) ? n : null;
    }

    internal static int? GetNonInteractiveLauncherPid(string[] args)
    {
        var launcherPid = GetArgInt(args, "--launcher-pid");
        if (launcherPid is not null) return launcherPid;

        var envValue = Environment.GetEnvironmentVariable("PSBASH_HOST_PARENT_PID");
        return int.TryParse(envValue, out var envPid) ? envPid : null;
    }
}
