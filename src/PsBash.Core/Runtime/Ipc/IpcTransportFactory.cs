using System.Text;

namespace PsBash.Core.Runtime.Ipc;

/// <summary>
/// Resolves the canonical ps-bash-host endpoint. One daemon per
/// <c>(user, session)</c>: the session token is an explicit <see cref="SessionEnvVar"/>
/// or, when unset, the launcher's parent process id (<see cref="ProcessAncestry"/>).
/// So repeated <c>-c</c> invocations from one shell / agent reuse the same warm
/// daemon, while independent shells / agents get distinct daemons — spreading load
/// instead of contending on a single per-user host. When no session token is
/// available the historical per-user endpoint (<c>host-{user}</c>) is used. Lifecycle
/// metadata and ownership rules are specified in
/// <c>docs/specs/host-lifecycle-contract.md</c>.
/// </summary>
public static class IpcTransportFactory
{
    /// <summary>
    /// Environment variable that overrides the canonical endpoint. Format is
    /// <c>scheme:endpoint</c> with scheme = <c>unix</c> or <c>pipe</c>. The
    /// endpoint is an absolute filesystem path for <c>unix</c> or a pipe name
    /// for <c>pipe</c>. Set this to point a launcher and a host at the same
    /// isolated address so test runs (or WSL bash drivers) do not collide
    /// with the user's canonical daemon.
    /// </summary>
    public const string EndpointEnvVar = "PSBASH_IPC_ENDPOINT";

    /// <summary>
    /// Explicit per-session grouping token. When set (and <see cref="EndpointEnvVar"/>
    /// is not), the canonical endpoint becomes one daemon per <c>(user, session)</c>
    /// instead of one per user — so independent shells / agents do not pile onto a
    /// single shared host. Repeated invocations that share a <c>PSBASH_SESSION</c>
    /// value reuse the same warm daemon. A multi-agent runner should set this to a
    /// stable per-agent id. When unset, the session token is derived automatically
    /// from the launcher's parent process id (see <see cref="ProcessAncestry"/>).
    /// </summary>
    public const string SessionEnvVar = "PSBASH_SESSION";

    // Test seam: override platform detection without P/Invoke or env hacks.
    internal static Func<bool>? UnixSocketSupportedOverride { get; set; }

    // Test seam: override the automatic (parent-pid) session token without P/Invoke.
    // Null = use the real ProcessAncestry-derived token. A provider returning null/blank
    // selects the per-user canonical endpoint (the pre-per-session behavior).
    internal static Func<string?>? SessionTokenOverride { get; set; }


    public static bool IsUnixSocketSupported()
    {
        if (UnixSocketSupportedOverride is { } fn) return fn();
        return !OperatingSystem.IsWindows() || Environment.OSVersion.Version.Build >= 17063;
    }

    /// <summary>
    /// Resolve the endpoint a host should bind / a client should connect to.
    /// Precedence: <paramref name="cliOverride"/> &gt; <c>PSBASH_IPC_ENDPOINT</c>
    /// env var &gt; canonical per-session endpoint. The canonical endpoint is a
    /// filesystem path on POSIX, named-pipe name on pre-1803 Windows. One daemon per
    /// <c>(user, session)</c> (session = <c>PSBASH_SESSION</c> or parent pid; see
    /// <see cref="ResolveSessionToken"/>), per-session not per-process so warm reuse
    /// within a session is preserved.
    /// </summary>
    /// <param name="cliOverride">
    /// Optional explicit override in <c>scheme:endpoint</c> form. The host
    /// passes its <c>--ipc-endpoint</c> flag value here.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The override (CLI or env var) is malformed or names an unknown scheme.
    /// </exception>
    public static (string Scheme, string Endpoint) ResolveEndpoint(string? cliOverride = null)
    {
        if (TryParseEndpointSpec(cliOverride, "--ipc-endpoint", out var cli)) return cli;

        var envValue = Environment.GetEnvironmentVariable(EndpointEnvVar);
        if (TryParseEndpointSpec(envValue, EndpointEnvVar, out var env)) return env;

        var user = SanitizeUser(Environment.UserName);
        // Per-session suffix: one daemon per (user, session) so independent shells /
        // agents don't contend on a single shared host (the contention that
        // serializes N callers behind one warm pool and starves it under load).
        // Empty token → the historical per-user endpoint (back-compat).
        var session = ResolveSessionToken();
        var suffix = session.Length > 0 ? $"-s{session}" : "";
        if (IsUnixSocketSupported())
        {
            var sockDir = Path.Combine(Path.GetTempPath(), "ps-bash");
            Directory.CreateDirectory(sockDir);
            return ("unix", Path.Combine(sockDir, $"host-{user}{suffix}.sock"));
        }
        return ("pipe", $"psbash-host-{user}{suffix}");
    }

    /// <summary>
    /// The per-session grouping token folded into the canonical endpoint name.
    /// Precedence: explicit <see cref="SessionEnvVar"/> &gt; the launcher's parent
    /// process id (<see cref="ProcessAncestry"/>). Returns <c>""</c> when neither is
    /// available, selecting the historical per-user endpoint. The token is sanitized
    /// to the filesystem/pipe-safe charset (it lands in a socket path / pipe name).
    /// </summary>
    private static string ResolveSessionToken()
    {
        var explicitSession = Environment.GetEnvironmentVariable(SessionEnvVar);
        if (!string.IsNullOrWhiteSpace(explicitSession))
            return SanitizeUser(explicitSession);

        var ppid = SessionTokenOverride is { } seam
            ? seam()
            : ProcessAncestry.GetParentProcessId()?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(ppid) ? "" : SanitizeUser(ppid);
    }

    /// <summary>
    /// Resolve a process-local endpoint unique to a single launcher invocation.
    /// REFACTOR-7: a <see cref="IpcWorker"/> running with
    /// <c>Lifetime.PerInvocation</c> spawns a private host on this endpoint
    /// instead of the shared per-session daemon socket
    /// (<see cref="ResolveEndpoint(string)"/>). Because the endpoint name carries the
    /// launcher PID plus a random suffix, two concurrent launchers never collide
    /// and there is no obsolete-host / ownership classification to perform — the
    /// socket either does not exist (spawn fresh) or is the one this launcher
    /// just spawned.
    /// </summary>
    /// <remarks>
    /// On POSIX the endpoint is a socket file under <c>{TEMP}/ps-bash/</c>;
    /// on pre-1803 Windows it is a named pipe. Either way the launcher owns the
    /// host process and unlinks the socket artifact when it disposes.
    /// </remarks>
    public static (string Scheme, string Endpoint) ResolvePerInvocationEndpoint()
    {
        var unique = $"{Environment.ProcessId}-{Guid.NewGuid():N}";
        if (IsUnixSocketSupported())
        {
            var sockDir = Path.Combine(Path.GetTempPath(), "ps-bash");
            Directory.CreateDirectory(sockDir);
            return ("unix", Path.Combine(sockDir, $"host-pi-{unique}.sock"));
        }
        return ("pipe", $"psbash-host-pi-{unique}");
    }

    /// <summary>
    /// Build a fresh transport instance bound to the resolved endpoint. Each
    /// call returns a new object — transports are typically single-use.
    /// </summary>
    /// <param name="cliOverride">
    /// Optional explicit override forwarded to <see cref="ResolveEndpoint"/>.
    /// </param>
    public static IIpcTransport CreateDefault(string? cliOverride = null)
    {
        var (scheme, endpoint) = ResolveEndpoint(cliOverride);
        return scheme == "unix"
            ? new UnixSocketTransport(endpoint)
            : new NamedPipeTransport(endpoint);
    }

    /// <summary>
    /// Parse a <c>scheme:endpoint</c> string. Returns false (no parse, no
    /// throw) for null/empty input so callers can treat absence as "fall
    /// through to the next precedence layer". Throws <see cref="ArgumentException"/>
    /// for non-empty input that is malformed — the source name (CLI flag or
    /// env var) is included in the message so the user sees which input was
    /// wrong.
    /// </summary>
    internal static bool TryParseEndpointSpec(
        string? spec,
        string sourceName,
        out (string Scheme, string Endpoint) result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(spec)) return false;

        var idx = spec.IndexOf(':');
        if (idx <= 0 || idx == spec.Length - 1)
            throw new ArgumentException(
                $"{sourceName} value '{spec}' must be 'scheme:endpoint' (scheme = unix|pipe).",
                nameof(spec));

        var scheme = spec[..idx];
        var endpoint = spec[(idx + 1)..];
        if (scheme is not ("unix" or "pipe"))
            throw new ArgumentException(
                $"{sourceName} value '{spec}' has unknown scheme '{scheme}'. Expected unix or pipe.",
                nameof(spec));

        result = (scheme, endpoint);
        return true;
    }

    /// <summary>
    /// Retire the current endpoint so a replacement host can bind the canonical
    /// address. This is endpoint cleanup only, not process cleanup. On AF_UNIX
    /// this unlinks the socket path; Windows named pipes are kernel namespace
    /// objects with no filesystem endpoint to remove.
    /// </summary>
    public static void RetireEndpoint(string scheme, string endpoint)
    {
        if (scheme != "unix") return;
        try { if (File.Exists(endpoint)) File.Delete(endpoint); }
        catch { /* best effort: bind will surface any remaining problem */ }
    }

    private static string SanitizeUser(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
            else sb.Append('_');
        }
        return sb.Length == 0 ? "unknown" : sb.ToString();
    }
}
