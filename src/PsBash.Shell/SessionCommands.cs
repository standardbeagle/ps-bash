using System.Text.RegularExpressions;
using PsBash.Core.Runtime;
using PsBash.Core.Runtime.Ipc;

namespace PsBash.Shell;

/// <summary>
/// <c>ps-bash session</c>: named, durable sessions. A named session is the per-session
/// daemon (<see cref="IpcTransportFactory.SessionEnvVar"/>) given a stable name instead of
/// the launcher-derived anchor, so it
/// <list type="bullet">
///   <item>outlives every launcher that attaches to it (no anchor watcher ends it),</item>
///   <item>keeps its shell state (cwd, variables) between attachments, and</item>
///   <item>runs concurrently with every other session — each is its own host process, so the
///   per-host execution lease never serializes two sessions against each other.</item>
/// </list>
/// Attach with <c>PSBASH_SESSION=NAME ps-bash -c '…'</c>. This command only manages lifetime;
/// it never changes how commands run.
/// </summary>
internal static partial class SessionCommands
{
    /// <summary>A managed session is kept for a day of silence unless the caller says otherwise.</summary>
    internal const int DefaultIdleSecs = 24 * 60 * 60;

    public static bool IsSessionCommand(string[] args)
        => args.Length >= 1 && args[0] == "session";

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"^\d+-\d+$")]
    private static partial Regex AnchorTokenRegex();

    internal static bool IsValidName(string name) => NameRegex().IsMatch(name);

    /// <summary>A token of the form <c>pid-startTicks</c> is a launcher-anchored (ephemeral) session.</summary>
    internal static bool IsAnchorToken(string token) => AnchorTokenRegex().IsMatch(token);

    /// <summary>
    /// The session token inside a metadata sidecar file name (<c>host-USER-sTOKEN.sock.host.json</c>),
    /// or null if the file belongs to another user or is not a per-session endpoint.
    /// </summary>
    internal static string? TokenFromSidecarName(string fileName, string sanitizedUser)
    {
        const string head = "host-", tail = ".sock.host.json";
        if (!fileName.StartsWith(head, StringComparison.Ordinal)
            || !fileName.EndsWith(tail, StringComparison.Ordinal)
            || fileName.Length < head.Length + tail.Length)
            return null;

        var stem = fileName[head.Length..^tail.Length];
        var prefix = sanitizedUser + "-s";
        return stem.StartsWith(prefix, StringComparison.Ordinal) && stem.Length > prefix.Length
            ? stem[prefix.Length..]
            : null;
    }

    /// <summary>
    /// The session token of a terminal broker socket (<c>host-USER-sTOKEN-t.sock</c>), or null for any other
    /// file. A host session literally named <c>X-t</c> owns <c>host-USER-sX-t.sock</c> too; the caller tells
    /// the two apart by the host sidecar that only a host socket has.
    /// </summary>
    internal static string? TokenFromTerminalSocketName(string fileName, string sanitizedUser)
    {
        const string head = "host-", tail = "-t.sock";
        if (!fileName.StartsWith(head, StringComparison.Ordinal)
            || !fileName.EndsWith(tail, StringComparison.Ordinal)
            || fileName.Length < head.Length + tail.Length)
            return null;

        var stem = fileName[head.Length..^tail.Length];
        var prefix = sanitizedUser + "-s";
        return stem.StartsWith(prefix, StringComparison.Ordinal) && stem.Length > prefix.Length
            ? stem[prefix.Length..]
            : null;
    }

    /// <summary>One line of <c>session list</c>: the command host and the terminal broker under one name.</summary>
    internal static string FormatListLine(string token, HostMetadata? host, bool hostAlive, bool sidecarPresent,
        Sessions.TerminalClient.BrokerState terminal)
    {
        var term = terminal switch
        {
            Sessions.TerminalClient.BrokerState.Running => "terminal=running",
            Sessions.TerminalClient.BrokerState.Stale => "terminal=stale",
            _ => "terminal=none",
        };
        if (!sidecarPresent) return $"{token}\tno-host\t{term}";
        if (host is null) return $"{token}\tunknown\t{term}";
        return $"{token}\t{(hostAlive ? "running" : "stale")}\tpid={host.Pid}\tstarted={host.StartedAt:O}\t{(host.Stateful ? "stateful" : "stateless")}\t{term}";
    }

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length < 2 || args[1] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length < 2 ? 2 : 0;
        }

        if (args[1] == "list") return await ListAsync(ct).ConfigureAwait(false);

        if (args[1] is not ("start" or "status" or "stop" or "attach" or "serve"))
        {
            Console.Error.WriteLine($"ps-bash: unknown session command '{args[1]}'");
            PrintUsage();
            return 2;
        }

        if (args.Length < 3 || !IsValidName(args[2]))
        {
            Console.Error.WriteLine("ps-bash session: NAME is required (1-32 of A-Z a-z 0-9 _ -)");
            return 2;
        }

        Environment.SetEnvironmentVariable(IpcTransportFactory.SessionEnvVar, args[2]);

        switch (args[1])
        {
            case "start":
                if (!TryParseIdle(args.AsSpan(3), out var idle))
                {
                    Console.Error.WriteLine("ps-bash session start: --idle-secs needs a positive integer");
                    return 2;
                }
                // The host reads its idle window from the environment it is spawned with.
                if (idle > 0 || Environment.GetEnvironmentVariable("PSBASH_HOST_IDLE_SECS") is null)
                    Environment.SetEnvironmentVariable(
                        "PSBASH_HOST_IDLE_SECS", (idle > 0 ? idle : DefaultIdleSecs).ToString());
                var stateless = args.AsSpan(3).Contains("--stateless");
                var (sch, ep) = IpcTransportFactory.ResolveEndpoint();
                var running = HostMetadata.TryRead(sch, ep);
                if (running is not null && HostOwnership.ProbeProcess(running.Pid).Alive && running.Stateful == stateless)
                {
                    Console.Error.WriteLine(
                        $"ps-bash session start: '{args[2]}' is already running {(running.Stateful ? "stateful" : "stateless")}; " +
                        "run `ps-bash session stop " + args[2] + "` first to change that");
                    return 1;
                }
                // The host consumes this switch at startup; it is only read when the host is spawned.
                Environment.SetEnvironmentVariable(IpcTransportFactory.StatefulEnvVar, stateless ? null : "1");
                return await HostCommands.RunAsync(["host", "start"], ct).ConfigureAwait(false);
            case "status":
                // `attach` starts a broker without a command host; status must see either half.
                var hostCode = await HostCommands.RunAsync(["host", "status"], ct).ConfigureAwait(false);
                var terminal = await Sessions.TerminalClient.ProbeCurrentAsync(ct).ConfigureAwait(false);
                Console.WriteLine($"terminal: {terminal.ToString().ToLowerInvariant()}");
                return hostCode == 0 || terminal == Sessions.TerminalClient.BrokerState.Running ? 0 : 1;
            case "attach":
                return await Sessions.TerminalClient.AttachAsync(args[2], args.AsSpan(3).Contains("--read-only"), ct).ConfigureAwait(false);
            case "serve":
                return await Sessions.TerminalClient.ServeAsync(ct).ConfigureAwait(false);
            default:
                await Sessions.TerminalClient.QuitAsync(ct).ConfigureAwait(false);
                return await HostCommands.RunAsync(["host", "shutdown"], ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// True when this launcher is attached to an explicitly named session whose host is stateful, so it
    /// must not re-impose its own cwd (the host keeps the session's). Reads the host's sidecar.
    /// </summary>
    internal static bool IsStatefulSessionActive()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(IpcTransportFactory.SessionEnvVar))) return false;
        try
        {
            var (scheme, endpoint) = IpcTransportFactory.ResolveEndpoint();
            return HostMetadata.TryRead(scheme, endpoint)?.Stateful == true;
        }
        catch (IOException) { return false; }
    }

    private static bool TryParseIdle(ReadOnlySpan<string> args, out int idle)
    {
        idle = 0;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] != "--idle-secs") continue;
            return i + 1 < args.Length && int.TryParse(args[i + 1], out idle) && idle > 0;
        }
        return true;
    }

    private static async Task<int> ListAsync(CancellationToken ct)
    {
        var dir = PsBashRuntimeDirectory.GetPath();
        if (!Directory.Exists(dir)) return 0;

        var user = new string(Environment.UserName
            .Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());

        // token -> (host sidecar path or null, terminal socket path or null)
        var sessions = new SortedDictionary<string, (string? Sidecar, string? Socket)>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(dir, "*.host.json"))
        {
            var token = TokenFromSidecarName(Path.GetFileName(file), user);
            if (token is null || IsAnchorToken(token)) continue;
            sessions[token] = (file, sessions.TryGetValue(token, out var prior) ? prior.Socket : null);
        }
        foreach (var file in Directory.EnumerateFiles(dir, "*-t.sock"))
        {
            var token = TokenFromTerminalSocketName(Path.GetFileName(file), user);
            if (token is null || IsAnchorToken(token)) continue;
            if (File.Exists(file + ".host.json")) continue; // a host session literally named "<x>-t", not a broker
            sessions[token] = (sessions.TryGetValue(token, out var prior) ? prior.Sidecar : null, file);
        }

        foreach (var (token, (sidecar, socket)) in sessions)
        {
            var meta = sidecar is null ? null : TryReadSidecar(sidecar);
            var alive = meta is not null && HostOwnership.ProbeProcess(meta.Pid).Alive;
            var terminal = socket is null
                ? Sessions.TerminalClient.BrokerState.None
                : await Sessions.TerminalClient.ProbeAsync(socket, ct).ConfigureAwait(false);
            Console.WriteLine(FormatListLine(token, meta, alive, sidecar is not null, terminal));
        }
        return 0;
    }

    private static HostMetadata? TryReadSidecar(string file)
    {
        return HostMetadata.TryRead("unix", file[..^".host.json".Length]);
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("usage: ps-bash session start NAME [--idle-secs N] [--stateless]   # durable session (default idle: 24h); stateful keeps vars/cwd/exports between calls");
        Console.Error.WriteLine("       ps-bash session status NAME");
        Console.Error.WriteLine("       ps-bash session stop NAME");
        Console.Error.WriteLine("       ps-bash session attach NAME [--read-only]   # reattachable terminal; detach with Ctrl-] d");
        Console.Error.WriteLine("remote: ssh -t HOST ps-bash session attach NAME");
        Console.Error.WriteLine("       ps-bash session list                         # named sessions only; host and terminal state per session");
        Console.Error.WriteLine("attach: PSBASH_SESSION=NAME ps-bash -c '...'");
    }
}
