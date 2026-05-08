using PsBash.Core.Runtime.Ipc;
using PsBash.Host.Runtime;
using PsBash.Host.Server;
using PsBash.Host.Shell;

namespace PsBash.Host;

internal sealed class Program
{
    static async Task<int> Main(string[] args)
    {
        RegisterSideBySideAssemblyResolver();

        // Interactive mode: host owns the tty; run the REPL and a server so -c clients
        // spawned from within the session can connect to the same runspace.
        if (args.Contains("--interactive"))
            return await RunInteractiveAsync(args);

        var sessionId = GetArg(args, "--session-id") ?? Environment.GetEnvironmentVariable("PSBASH_SESSION_ID") ?? "default";

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // Start runspace init on a background thread — this is the slow part (~2-8 s).
        // The transport starts listening and writes the lock file while the runspace
        // warms up, so clients can connect immediately and queue work.
        var workerTask = Task.Run(SdkWorker.Create);

        IIpcTransport transport = CreateTransport(sessionId, args);

        var idleTimeout = IdleShutdown.DefaultTimeout;
        using var idle = new IdleShutdown(cts, idleTimeout);

        int? launcherPid = GetArgInt(args, "--launcher-pid");
        using var deathWatcher = ParentDeathWatcher.TryCreate(launcherPid, cts);

        var lockFile = HostLockFile.ForSession(sessionId);
        await using var server = new HostServer(transport, workerTask, idle);

        try
        {
            var serverTask = server.RunAsync(cts.Token);
            // Lock file written as soon as transport is listening — well before the
            // runspace is ready. Clients that connect early are held at the worker
            // semaphore inside HandleConnectionAsync.
            await server.WhenListening;
            lockFile.Write(transport, Environment.ProcessId);
            await serverTask;
        }
        finally
        {
            lockFile.Delete();
            // Ensure background runspace init is awaited so exceptions surface cleanly.
            try { var w = await workerTask; await w.DisposeAsync(); } catch { }
        }

        return 0;
    }

    private static async Task<int> RunInteractiveAsync(string[] args)
    {
        var noProfile = args.Contains("--no-profile");
        int? launcherPid = GetArgInt(args, "--launcher-pid");
        var sessionId = GetArg(args, "--session-id")
            ?? throw new InvalidOperationException("--interactive requires --session-id");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        using var deathWatcher = ParentDeathWatcher.TryCreate(launcherPid, cts);

        var workerTask = Task.Run(SdkWorker.Create);
        IIpcTransport transport = CreateTransport(sessionId, args);
        // No IdleShutdown: server lifetime is tied to the REPL, not client activity.
        await using var server = new HostServer(transport, workerTask);
        var lockFile = HostLockFile.ForSession(sessionId);

        var serverTask = server.RunAsync(cts.Token);
        await server.WhenListening;
        lockFile.Write(transport, Environment.ProcessId);

        SdkWorker? worker = null;
        try
        {
            worker = await workerTask;
            return await InteractiveShell.RunAsync(worker, noProfile);
        }
        finally
        {
            lockFile.Delete();
            cts.Cancel();
            try { await serverTask; } catch (OperationCanceledException) { }
            if (worker is not null) await worker.DisposeAsync();
        }
    }

    private static IIpcTransport CreateTransport(string sessionId, string[] args)
    {
        var socketPath = GetArg(args, "--socket");
        if (socketPath != null) return new UnixSocketTransport(socketPath);

        var pipeName = GetArg(args, "--pipe");
        if (pipeName != null) return new NamedPipeTransport(pipeName);

        return IpcTransportFactory.CreateDefault(sessionId);
    }

    private static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    private static int? GetArgInt(string[] args, string name)
    {
        var val = GetArg(args, name);
        return int.TryParse(val, out var n) ? n : null;
    }

    private static void RegisterSideBySideAssemblyResolver()
    {
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
        {
            var candidate = Path.Combine(AppContext.BaseDirectory, assemblyName.Name + ".dll");
            return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
        };
    }
}
