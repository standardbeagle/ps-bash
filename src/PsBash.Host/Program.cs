using PsBash.Core.Runtime.Ipc;
using PsBash.Host.Runtime;
using PsBash.Host.Server;
using PsBash.Host.Shell;

namespace PsBash.Host;

internal sealed class Program
{
    static async Task<int> Main(string[] args)
    {
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
            using var iDeathWatcher = ParentDeathWatcher.TryCreate(iLauncherPid, iCts);

            try
            {
                await using var interactiveWorker = SdkWorker.Create();
                return await InteractiveShell.RunAsync(interactiveWorker, noProfile);
            }
            finally
            {
                Environment.SetEnvironmentVariable("PSBASH_INTERACTIVE", priorInteractive);
            }
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // Start runspace init on a background thread — this is the slow part (~2-8 s).
        // The transport starts listening while the runspace warms up so clients
        // can connect immediately and queue work.
        var workerTask = Task.Run(SdkWorker.Create);

        IIpcTransport transport = CreateTransport(args);

        var idleTimeout = IdleShutdown.DefaultTimeout;
        using var idle = new IdleShutdown(cts, idleTimeout);

        int? launcherPid = GetArgInt(args, "--launcher-pid");
        using var deathWatcher = ParentDeathWatcher.TryCreate(launcherPid, cts);

        await using var server = new HostServer(transport, workerTask, idle);

        try
        {
            var serverTask = server.RunAsync(cts.Token);
            await server.WhenListening;
            await serverTask;
        }
        finally
        {
            try { var w = await workerTask; await w.DisposeAsync(); } catch { }
        }

        return 0;
    }

    private static IIpcTransport CreateTransport(string[] args)
    {
        // Explicit overrides (used by tests) take precedence.
        var socketPath = GetArg(args, "--socket");
        if (socketPath != null) return new UnixSocketTransport(socketPath);

        var pipeName = GetArg(args, "--pipe");
        if (pipeName != null) return new NamedPipeTransport(pipeName);

        // Default: the canonical per-user endpoint shared with launcher clients.
        return IpcTransportFactory.CreateDefault();
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
}
