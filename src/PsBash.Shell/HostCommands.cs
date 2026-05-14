using System.Net.Sockets;
using PsBash.Core.Runtime;
using PsBash.Core.Runtime.Ipc;

namespace PsBash.Shell;

internal static class HostCommands
{
    public static bool IsHostCommand(string[] args)
        => args.Length >= 1 && args[0] == "host";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length < 2)
        {
            PrintUsage();
            return 2;
        }

        return args[1] switch
        {
            "status" => await StatusAsync(ct).ConfigureAwait(false),
            "shutdown" => await ShutdownAsync(ParseDeadline(args.AsSpan(2)), waitForExit: true, ct).ConfigureAwait(false),
            "restart" => await RestartAsync(ParseDeadline(args.AsSpan(2)), ct).ConfigureAwait(false),
            "-h" or "--help" or "help" => Help(),
            _ => Unknown(args[1]),
        };
    }

    private static async Task<int> StatusAsync(CancellationToken ct)
    {
        var (scheme, endpoint) = IpcTransportFactory.ResolveEndpoint();
        var meta = HostMetadata.TryRead(scheme, endpoint);

        Console.WriteLine($"endpoint: {scheme}:{endpoint}");
        if (meta is null)
        {
            Console.WriteLine("metadata: absent");
        }
        else
        {
            Console.WriteLine("metadata: present");
            Console.WriteLine($"pid: {meta.Pid}");
            Console.WriteLine($"executable: {meta.ExecutablePath}");
            Console.WriteLine($"protocol: {meta.ProtocolVersion}");
            Console.WriteLine($"build: {meta.BuildIdentity}");
            Console.WriteLine($"owner: {meta.Owner}");
            Console.WriteLine($"startedAt: {meta.StartedAt:O}");
        }

        var (exitCode, lines) = await SendControlRequestAsync(new Mode.Health(), ct).ConfigureAwait(false);
        if (exitCode is null)
        {
            Console.WriteLine("status: stopped");
            return 1;
        }

        foreach (var line in lines) Console.WriteLine(line);
        Console.WriteLine(exitCode == 0 ? "status: running" : $"status: starting-or-unhealthy exit={exitCode}");
        return exitCode == 0 ? 0 : 1;
    }

    private static async Task<int> ShutdownAsync(int deadlineMs, bool waitForExit, CancellationToken ct)
    {
        var (scheme, endpoint) = IpcTransportFactory.ResolveEndpoint();
        var meta = HostMetadata.TryRead(scheme, endpoint);
        var (exitCode, lines) = await SendControlRequestAsync(new Mode.Shutdown(deadlineMs), ct).ConfigureAwait(false);

        if (exitCode is null)
        {
            HostMetadata.Remove(scheme, endpoint);
            IpcTransportFactory.RetireEndpoint(scheme, endpoint);
            Console.WriteLine("ps-bash-host already stopped");
            return 0;
        }

        foreach (var line in lines) Console.WriteLine(line);
        if (exitCode != 0) return exitCode.Value;

        if (waitForExit)
            await WaitUntilStoppedAsync(meta, deadlineMs, ct).ConfigureAwait(false);

        HostMetadata.Remove(scheme, endpoint);
        IpcTransportFactory.RetireEndpoint(scheme, endpoint);
        return 0;
    }

    private static async Task<int> RestartAsync(int deadlineMs, CancellationToken ct)
    {
        var shutdownExit = await ShutdownAsync(deadlineMs, waitForExit: true, ct).ConfigureAwait(false);
        if (shutdownExit != 0) return shutdownExit;

        var hostBinary = ResolveHostBinary()
            ?? throw new HostUnavailableException(
                "ps-bash-host binary not found. Set PSBASH_HOST=<path> or install alongside ps-bash.");

        // `ps-bash host restart` explicitly manages the shared per-user daemon —
        // start it on the canonical endpoint and leave it running for subsequent
        // launchers. REFACTOR-7: must NOT use the PerInvocation default, which
        // would spawn a private host on a process-local socket and kill it on
        // dispose, leaving "restart" with nothing running.
        await using var worker = await IpcWorker.StartAsync(
            hostBinary, lifetime: Lifetime.Daemon, ct: ct).ConfigureAwait(false);
        Console.WriteLine("ps-bash-host restarted");
        return 0;
    }

    internal static async Task<(int? ExitCode, List<string> Lines)> SendControlRequestAsync(
        Mode mode,
        CancellationToken ct,
        string? endpointOverride = null)
    {
        var transport = IpcTransportFactory.CreateDefault(endpointOverride);
        await using (transport.ConfigureAwait(false))
        {
            Stream stream;
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(TimeSpan.FromMilliseconds(750));
                stream = await transport.ConnectAsync(connectCts.Token).ConfigureAwait(false);
            }
            catch (SocketException) { return (null, []); }
            catch (IOException) { return (null, []); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (null, []); }

            await using (stream.ConfigureAwait(false))
            {
                var lines = new List<string>();
                try
                {
                    await HostProtocol.WriteRequestAsync(stream, mode, ct).ConfigureAwait(false);
                    var exitCode = await HostProtocol.ReadResponseAsync(stream, line => lines.Add(line), ct).ConfigureAwait(false);
                    return (exitCode, lines);
                }
                catch (SocketException) { return (null, lines); }
                catch (IOException) { return (null, lines); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (null, lines); }
            }
        }
    }

    private static async Task WaitUntilStoppedAsync(HostMetadata? metadata, int deadlineMs, CancellationToken ct)
    {
        var waitMs = Math.Max(deadlineMs, 500);
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(waitMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (metadata is not null)
            {
                var (alive, _) = HostOwnership.ProbeProcess(metadata.Pid);
                if (!alive) return;
            }

            var (exitCode, _) = await SendControlRequestAsync(new Mode.Health(), ct).ConfigureAwait(false);
            if (exitCode is null) return;
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }

    private static int ParseDeadline(ReadOnlySpan<string> args)
    {
        var deadlineMs = HostProtocol.DefaultShutdownDeadlineMs;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--deadline-ms" && i + 1 < args.Length && int.TryParse(args[i + 1], out var n))
            {
                deadlineMs = n;
                i++;
            }
            else if (args[i].StartsWith("--deadline-ms=", StringComparison.Ordinal)
                && int.TryParse(args[i]["--deadline-ms=".Length..], out var eq))
            {
                deadlineMs = eq;
            }
        }
        return deadlineMs;
    }

    private static int Help()
    {
        PrintUsage();
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"ps-bash: unknown host command '{command}'");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("usage: ps-bash host status");
        Console.Error.WriteLine("       ps-bash host shutdown [--deadline-ms N]");
        Console.Error.WriteLine("       ps-bash host restart [--deadline-ms N]");
    }

    private static string? ResolveHostBinary()
    {
        var overridePath = Environment.GetEnvironmentVariable("PSBASH_HOST");
        if (!string.IsNullOrEmpty(overridePath)) return overridePath;

        var sxs = Path.Combine(AppContext.BaseDirectory, IpcWorker.GetHostBinaryName());
        return File.Exists(sxs) ? sxs : null;
    }
}
