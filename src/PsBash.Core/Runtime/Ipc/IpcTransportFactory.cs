using System.Text;

namespace PsBash.Core.Runtime.Ipc;

/// <summary>
/// Resolves the canonical ps-bash-host endpoint. One daemon per
/// <c>(user, session)</c>: the session token is an explicit <see cref="SessionEnvVar"/>
/// or, when unset, the launcher's stable session anchor
/// (<see cref="ProcessAncestry.FindSessionAnchor"/> — a shell/agent ancestor plus
/// its start time). So repeated <c>-c</c> invocations from one shell / agent reuse
/// the same warm daemon, while independent shells / agents get distinct daemons —
/// spreading load instead of contending on a single per-user host. When no session
/// token is available the historical per-user endpoint (<c>host-{user}</c>) is used.
/// Lifecycle metadata and ownership rules are specified in
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
    /// from the launcher's stable session anchor (see
    /// <see cref="ProcessAncestry.FindSessionAnchor"/>).
    /// </summary>
    public const string SessionEnvVar = "PSBASH_SESSION";

    /// <summary>
    /// Marker the host sets in its process environment (value = host PID) for the duration of
    /// every launcher-framed command. A ps-bash started by that command inherits it and must
    /// not reuse the host that is executing its parent (the host serializes execution behind a
    /// process-wide gate the parent holds, so the child would deadlock behind it, or — on an
    /// obsolete-build mismatch — retire the host out from under the parent).
    /// <see cref="IpcWorker.StartAsync"/> therefore gives such a launcher a private host.
    /// </summary>
    public const string InsideHostEnvVar = "PSBASH_INSIDE_HOST";

    /// <summary>True when this process was started (transitively) by a command a host is running.</summary>
    public static bool IsInsideHostCommand()
        => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(InsideHostEnvVar));

    // Test seam: override platform detection without P/Invoke or env hacks.
    internal static Func<bool>? UnixSocketSupportedOverride { get; set; }

    // Test seam: override the automatic (ancestor-identity) session anchor without
    // P/Invoke. Null = use the real ProcessAncestry-derived anchor. A provider
    // returning null selects the per-user canonical endpoint (the pre-per-session
    // behavior).
    internal static Func<ProcessAncestry.ProcessIdentity?>? SessionAnchorOverride { get; set; }

    // Test seam: override the launcher process id used in the per-invocation
    // endpoint name, so the sun_path budget can be exercised with a 6-digit pid.
    internal static Func<int>? ProcessIdOverride { get; set; }


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
    /// <c>(user, session)</c> (session = <c>PSBASH_SESSION</c> or session anchor; see
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
            var sockDir = SocketDirectory();
            var candidate = Path.Combine(sockDir, $"host-{user}{suffix}.sock");
            // A long temp root / user / session token can overflow sun_path.
            // Fall back to the named-pipe scheme, which has no path-length cap
            // (NamedPipeTransport applies its own macOS budget on POSIX). The
            // scheme is deterministic given the same inputs, so launcher and
            // host agree.
            if (candidate.Length <= UnixSocketPathBudget())
            {
                // Create AND validate the per-user 0700 socket directory before
                // the host binds: a shared, attacker-writable socket directory
                // would let another user pre-create or replace the socket file.
                PsBashRuntimeDirectory.EnsureDirectory();
                return ("unix", candidate);
            }
        }
        return ("pipe", $"psbash-host-{user}{suffix}");
    }

    /// <summary>
    /// The per-session grouping token folded into the canonical endpoint name.
    /// Precedence: explicit <see cref="SessionEnvVar"/> &gt; the launcher's stable
    /// session anchor (<see cref="ProcessAncestry.FindSessionAnchor"/>). Returns
    /// <c>""</c> when neither is available, selecting the historical per-user
    /// endpoint. The token is sanitized to the filesystem/pipe-safe charset (it
    /// lands in a socket path / pipe name).
    /// </summary>
    private static string ResolveSessionToken()
    {
        var explicitSession = Environment.GetEnvironmentVariable(SessionEnvVar);
        if (!string.IsNullOrWhiteSpace(explicitSession))
            return SanitizeUser(explicitSession);

        var anchor = SessionAnchorOverride is { } seam
            ? seam()
            : ProcessAncestry.FindSessionAnchor();
        if (anchor is not { } id) return "";
        // "<pid>-<startTicks>": the start time defeats PID reuse — a recycled PID
        // with a new start time is a distinct session, so it can never attach to a
        // previous session's stale daemon.
        return SanitizeUser(
            $"{id.Pid}-{id.StartTimeUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
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
    /// On POSIX the endpoint is a socket file inside the per-user
    /// <see cref="PsBashRuntimeDirectory"/> (0700); on pre-1803 Windows, or when
    /// the socket path would not fit the sun_path budget with at least
    /// <see cref="MinInvocationSuffixHexChars"/> random hex chars, it is a
    /// named pipe. Either way the launcher owns the host process and unlinks
    /// the socket artifact when it disposes.
    /// </remarks>
    public static (string Scheme, string Endpoint) ResolvePerInvocationEndpoint()
    {
        var pid = ProcessIdOverride is { } pidSeam ? pidSeam() : Environment.ProcessId;
        var guid = Guid.NewGuid().ToString("N");
        if (IsUnixSocketSupported())
        {
            var sockDir = SocketDirectory();
            var prefix = Path.Combine(sockDir, $"host-pi-{pid}-");
            const string ext = ".sock";
            // sun_path caps the WHOLE path, so the random suffix gets whatever
            // room the temp root + pid leave. 12 hex chars (48 bits) is ample
            // to keep two concurrent launchers of the same pid distinct; below
            // that the path is pathological, so use the pipe scheme instead.
            var room = UnixSocketPathBudget() - prefix.Length - ext.Length;
            if (room >= MinInvocationSuffixHexChars)
            {
                PsBashRuntimeDirectory.EnsureDirectory();
                var unique = guid[..Math.Min(guid.Length, room)];
                return ("unix", prefix + unique + ext);
            }
        }
        return ("pipe", $"psbash-host-pi-{pid}-{guid}");
    }

    /// <summary>
    /// Longest AF_UNIX path .NET accepts. <c>sun_path</c> is a 108-byte buffer
    /// including the terminating NUL on Linux/Windows, so the path itself is
    /// capped at 107 chars; macOS uses a 104-byte buffer (103 chars).
    /// </summary>
    internal const int UnixSocketPathMaxChars = 107;

    /// <summary>Minimum random hex chars kept for per-invocation uniqueness.</summary>
    internal const int MinInvocationSuffixHexChars = 12;

    private static int UnixSocketPathBudget()
        => OperatingSystem.IsMacOS() ? 103 : UnixSocketPathMaxChars;

    private static string SocketDirectory() => PsBashRuntimeDirectory.GetPath();

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
