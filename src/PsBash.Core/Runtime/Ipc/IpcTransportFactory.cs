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
    /// <c>unix:/absolute/socket/path</c>. Set this to point a launcher and a host at the same
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
    /// Set to <c>1</c> by <c>ps-bash session start</c> in the launcher that spawns a named session's
    /// host; the host consumes and clears it. A stateful host runs every command in one pinned runspace
    /// (see <c>HostServer.Stateful</c>) and advertises that in its metadata sidecar.
    /// </summary>
    public const string StatefulEnvVar = "PSBASH_SESSION_STATEFUL";

    /// <summary>Executor slots per endpoint: slot 0 is the endpoint itself, slots 1..N spill (docs/specs/executor-slots.md).</summary>
    public const int MaxExecSlots = 6;

    /// <summary>
    /// The slot an overflow ticket (<see cref="ExecOverflowTicket.Index"/>) runs its command on: the slots past the last
    /// executor slot, one per ticket (docs/specs/executor-slots.md §Overflow). Launchers reach them only through a ticket.
    /// </summary>
    public static int OverflowSlot(int ticketIndex) => MaxExecSlots + 1 + ticketIndex;

    private const string SlotMarker = "-x";

    /// <summary>
    /// The endpoint of executor slot <paramref name="slot"/> (&gt;= 1) of <paramref name="baseEndpoint"/>:
    /// the base with a <c>-x{slot}</c> suffix. Slot 0 is the base itself. Falls back to a hashed name when a
    /// unix path would not fit.
    /// </summary>
    public static (string Scheme, string Endpoint) ResolveSlotEndpoint(string scheme, string baseEndpoint, int slot)
    {
        if (slot <= 0) return (scheme, baseEndpoint);
        var slotText = slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var endpoint = baseEndpoint + SlotMarker + slotText;
        if (endpoint.Length > UnixSocketPathBudget())
        {
            var dir = Path.GetDirectoryName(baseEndpoint) ?? SocketDirectory();
            endpoint = Path.Combine(dir, $"psbash-slot-{ShortHash(endpoint)}{SlotMarker}{slotText}");
        }
        return (scheme, RequireFits(endpoint));
    }
    /// <summary>True when <paramref name="endpoint"/> names an executor slot &gt;= 1 (the <c>-x{k}</c> suffix of <see cref="ResolveSlotEndpoint"/>).</summary>
    public static bool IsSlotEndpoint(string endpoint)
    {
        int digits = endpoint.Length;
        while (digits > 0 && char.IsAsciiDigit(endpoint[digits - 1])) digits--;
        int count = endpoint.Length - digits;
        return count is >= 1 and <= 2
            && endpoint.AsSpan(0, digits).EndsWith(SlotMarker, StringComparison.Ordinal);
    }

    private static string ShortHash(string s, int chars = 16)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..chars];

    /// <summary>
    /// Returns <paramref name="path"/> when AF_UNIX can bind it. A path past sun_path is a
    /// configuration fault (an over-long runtime directory), reported with the way out, because
    /// there is no other transport to fall back to.
    /// </summary>
    private static string RequireFits(string path)
    {
        if (path.Length <= UnixSocketPathBudget()) return path;
        throw new IOException(
            $"ps-bash cannot place its control socket: '{path}' is {path.Length} characters and " +
            $"AF_UNIX allows {UnixSocketPathBudget()}. Point TMPDIR (or XDG_RUNTIME_DIR) at a shorter " +
            $"writable directory, or set {EndpointEnvVar}=unix:/short/path.sock.");
    }

    /// <summary>
    /// Creates and validates the per-user runtime directory. A sandbox that cannot write the
    /// filesystem (e.g. a read-only restricted-token sandbox) cannot host the control socket at
    /// all, so the failure names the cause instead of surfacing a bare access-denied.
    /// </summary>
    private static void EnsureRuntimeDirectory()
    {
        try { PsBashRuntimeDirectory.EnsureDirectory(); }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException(
                "ps-bash needs a writable per-user runtime directory for its control socket and this " +
                $"process cannot create it ({ex.Message}). Run outside a read-only sandbox, or point TMPDIR " +
                "at a writable directory.", ex);
        }
    }

    // Test seam: override the automatic (ancestor-identity) session anchor without
    // P/Invoke. Null = use the real ProcessAncestry-derived anchor. A provider
    // returning null selects the per-user canonical endpoint (the pre-per-session
    // behavior).
    internal static Func<ProcessIdentity?>? SessionAnchorOverride { get; set; }

    // Test seam: override the launcher process id used in the per-invocation
    // endpoint name, so the sun_path budget can be exercised with a 6-digit pid.
    internal static Func<int>? ProcessIdOverride { get; set; }


    /// <summary>
    /// Resolve the endpoint a host should bind / a client should connect to.
    /// Precedence: <paramref name="cliOverride"/> &gt; <c>PSBASH_IPC_ENDPOINT</c>
    /// env var &gt; canonical per-session endpoint. The canonical endpoint is an
    /// AF_UNIX socket path (Windows 10 1803+ included). One daemon per
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
        => ResolveEndpoint(cliOverride, out _);

    /// <summary>
    /// <see cref="ResolveEndpoint(string)"/>, also reporting the automatic session anchor
    /// folded into the endpoint name. <paramref name="sessionAnchor"/> is null when the
    /// endpoint came from an override, from <c>PSBASH_SESSION</c>, or from the per-user
    /// fallback — no process then owns the session, so the daemon keeps only its idle timeout.
    /// </summary>
    internal static (string Scheme, string Endpoint) ResolveEndpoint(string? cliOverride, out ProcessIdentity? sessionAnchor)
        => ResolveEndpoint(cliOverride, out sessionAnchor, out _);

    /// <summary>
    /// <see cref="ResolveEndpoint(string, out ProcessIdentity?)"/>, also reporting the endpoint of the enclosing host
    /// (its own sidecar endpoint, a slot or the base) when the launcher joined that host's session. Null otherwise.
    /// </summary>
    internal static (string Scheme, string Endpoint) ResolveEndpoint(
        string? cliOverride, out ProcessIdentity? sessionAnchor, out string? enclosingHostEndpoint)
    {
        sessionAnchor = null;
        enclosingHostEndpoint = null;
        if (TryParseEndpointSpec(cliOverride, "--ipc-endpoint", out var cli)) return cli;

        var envValue = Environment.GetEnvironmentVariable(EndpointEnvVar);
        if (TryParseEndpointSpec(envValue, EndpointEnvVar, out var env)) return env;

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SessionEnvVar))
            && TryResolveEnclosingHostEndpoint(out var hosted, out sessionAnchor, out enclosingHostEndpoint))
            return hosted;

        var user = SanitizeUser(Environment.UserName);
        // Per-session suffix: one daemon per (user, session) so independent shells /
        // agents don't contend on a single shared host (the contention that
        // serializes N callers behind one warm pool and starves it under load).
        // Empty token → the historical per-user endpoint (back-compat).
        var session = ResolveSessionToken(out sessionAnchor);
        var suffix = session.Length > 0 ? $"-s{session}" : "";
        // Create AND validate the per-user 0700 socket directory before the host binds:
        // a shared, attacker-writable socket directory would let another user pre-create
        // or replace the socket file.
        EnsureRuntimeDirectory();
        var sockDir = SocketDirectory();
        var candidate = Path.Combine(sockDir, $"host-{user}{suffix}.sock");
        // A long user name can overflow sun_path; hash it (the session token stays
        // readable so `ps-bash session list` still names the session). Deterministic
        // for the same inputs, so launcher and host agree.
        if (candidate.Length > UnixSocketPathBudget())
            candidate = Path.Combine(sockDir, $"host-{ShortHash(user, 8)}{suffix}.sock");
        return ("unix", RequireFits(candidate));
    }

    /// <summary>Test seam for <see cref="ProcessAncestry.FindEnclosingHost"/>. Null = the real ancestry walk.</summary>
    internal static Func<ProcessIdentity?>? EnclosingHostOverride { get; set; }

    /// <summary>
    /// A launcher started by a command that a registered host is running (nested <c>bash -c</c>, awk getline) joins
    /// that host's session: it resolves the host's base endpoint, so it reuses an idle session host or spills to the
    /// session's executor slots (docs/specs/executor-slots.md), instead of anchoring a new session on the host and
    /// starting another full daemon there. A host spawned from here is anchored on the enclosing host's session
    /// (<see cref="EnclosingHostAnchor"/>). False when no registered host encloses this launcher.
    /// </summary>
    private static bool TryResolveEnclosingHostEndpoint(
        out (string Scheme, string Endpoint) endpoint, out ProcessIdentity? anchor, out string? hostEndpoint)
    {
        endpoint = default;
        anchor = null;
        hostEndpoint = null;
        var host = EnclosingHostOverride is { } seam ? seam() : ProcessAncestry.FindEnclosingHost();
        if (host is not { } id) return false;
        var sidecars = ReachableHostSidecars();
        if (EnclosingHostSidecar(id, sidecars) is not { } own) return false;
        endpoint = ("unix", BaseOfSlotEndpoint(own.Endpoint));
        anchor = EnclosingHostAnchor(id, [own]);
        hostEndpoint = own.Endpoint;
        return true;
    }

    /// <summary>
    /// What a host spawned by a launcher nested in <paramref name="host"/>'s command is anchored on: the session that
    /// host lives for, as its sidecar records it, since the spawned slot serves that whole session's commands. A host
    /// that records none (a private host, an explicit endpoint, an older build) bounds it itself.
    /// REGRESSION: the slot was always anchored on the enclosing host, a sibling slot or the base; when that one was
    /// retired or idled out, the slot hung up the session command it was running (exit 125).
    /// </summary>
    internal static ProcessIdentity EnclosingHostAnchor(ProcessIdentity host, IEnumerable<HostMetadata> sidecars)
        => EnclosingHostSidecar(host, sidecars) is { } own && ProcessIdentity.TryParse(own.SessionAnchor, out var session)
            ? session
            : host;

    /// <summary>
    /// The sidecars of every host this user's launchers reach: the runtime directory's, plus, when
    /// <c>PSBASH_IPC_ENDPOINT</c> names a socket elsewhere (a test harness's private endpoint), the sidecars of that
    /// endpoint and its executor slots (<c>…-x{k}</c>), which every host writes beside its socket. Only files named after
    /// the configured endpoint are read there: that directory may be shared (<c>/tmp</c>), the runtime directory is
    /// owner-only. The one source for "which host is pid N" (a nested launcher's enclosing host, <c>kill</c>'s target).
    /// </summary>
    internal static List<HostMetadata> ReachableHostSidecars()
        => ReachableHostSidecars(SocketDirectory(), Environment.GetEnvironmentVariable(EndpointEnvVar));

    /// <summary><see cref="ReachableHostSidecars()"/> for a given runtime directory and <c>PSBASH_IPC_ENDPOINT</c> value.</summary>
    internal static List<HostMetadata> ReachableHostSidecars(string dir, string? endpointSpec)
    {
        var all = HostMetadata.ReadAll(dir);
        (string Scheme, string Endpoint) env;
        try
        {
            if (!TryParseEndpointSpec(endpointSpec, EndpointEnvVar, out env)) return all;
        }
        catch (ArgumentException) { return all; }
        if (env.Scheme != "unix" || Path.GetDirectoryName(env.Endpoint) is not { Length: > 0 } envDir
            || string.Equals(Path.GetFullPath(envDir), Path.GetFullPath(dir), OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return all;
        all.AddRange(HostMetadata.ReadAll(envDir, Path.GetFileName(BaseOfSlotEndpoint(env.Endpoint)) + "*"));
        // A slot whose name would not fit a socket path is hashed (psbash-slot-{hash}-x{k}, ResolveSlotEndpoint), so the
        // pattern above misses it. REGRESSION (macOS, long $TMPDIR): every slot host there was hashed, and `kill` of a
        // script on a slot said "a ps-bash shell host that could not be reached".
        foreach (var name in HashedSlotNames(env.Endpoint))
            all.AddRange(HostMetadata.ReadAll(envDir, name + "*"));
        return all;
    }

    /// <summary>
    /// The file names of <paramref name="endpoint"/>'s executor and overflow slots that <see cref="ResolveSlotEndpoint"/>
    /// hashes (every overflow slot a session could have, whatever <c>PSBASH_EXEC_OVERFLOW_MAX</c> says now).
    /// </summary>
    internal static List<string> HashedSlotNames(string endpoint)
    {
        var names = new List<string>();
        var baseEndpoint = BaseOfSlotEndpoint(endpoint);
        for (int slot = 1; slot <= OverflowSlot(ExecOverflowTicket.Ceiling - 1); slot++)
        {
            string resolved;
            try { resolved = ResolveSlotEndpoint("unix", baseEndpoint, slot).Endpoint; }
            catch (IOException) { continue; }   // does not fit even hashed (RequireFits): no host can serve it
            var name = Path.GetFileName(resolved);
            if (name.StartsWith("psbash-slot-", StringComparison.Ordinal)) names.Add(name);
        }
        return names;
    }

    /// <summary>
    /// The base endpoint of <paramref name="host"/>'s sidecar, or null when none is its own: the sidecar must name the
    /// host's PID and have been written after that process started (a stale sidecar of an earlier host whose PID was
    /// reused is not it). The host writes its sidecar after it binds, so its start time always precedes the record.
    /// </summary>
    internal static string? EnclosingHostBase(ProcessIdentity host, IEnumerable<HostMetadata> sidecars)
        => EnclosingHostOwnEndpoint(host, sidecars) is { } own ? BaseOfSlotEndpoint(own) : null;

    /// <summary>The endpoint <paramref name="host"/> itself serves (slot or base), per <see cref="EnclosingHostBase"/>'s sidecar rules.</summary>
    internal static string? EnclosingHostOwnEndpoint(ProcessIdentity host, IEnumerable<HostMetadata> sidecars)
        => EnclosingHostSidecar(host, sidecars)?.Endpoint;

    private static HostMetadata? EnclosingHostSidecar(ProcessIdentity host, IEnumerable<HostMetadata> sidecars)
    {
        foreach (var meta in sidecars)
        {
            if (meta.Pid != host.Pid || meta.TransportScheme != "unix") continue;
            if (meta.StartedAt.UtcTicks < host.StartTimeUtcTicks - TimeSpan.TicksPerSecond) continue;
            return meta;
        }
        return null;
    }

    /// <summary>
    /// The executor slot <paramref name="endpoint"/> serves relative to <see cref="BaseOfSlotEndpoint"/>: k for
    /// <c>…-x{k}</c>, 0 for a base. A hashed slot name is its own base (spills chain from it), so it is slot 0 of that chain.
    /// </summary>
    public static int SlotIndexOf(string endpoint)
    {
        if (BaseOfSlotEndpoint(endpoint) == endpoint) return 0;
        int cut = endpoint.LastIndexOf(SlotMarker, StringComparison.Ordinal);
        return int.Parse(endpoint.AsSpan(cut + SlotMarker.Length), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The base endpoint of a slot endpoint (<c>…-x3</c> → <c>…</c>); any other endpoint is its own base. A slot name
    /// hashed for length cannot be mapped back and is returned unchanged (its spills chain from it instead).
    /// </summary>
    public static string BaseOfSlotEndpoint(string endpoint)
    {
        if (!IsSlotEndpoint(endpoint)) return endpoint;
        int cut = endpoint.LastIndexOf(SlotMarker, StringComparison.Ordinal);
        return Path.GetFileName(endpoint).StartsWith("psbash-slot-", StringComparison.Ordinal) ? endpoint : endpoint[..cut];
    }

    /// <summary>
    /// The per-session grouping token folded into the canonical endpoint name.
    /// Precedence: explicit <see cref="SessionEnvVar"/> &gt; the launcher's stable
    /// session anchor (<see cref="ProcessAncestry.FindSessionAnchor"/>). Returns
    /// <c>""</c> when neither is available, selecting the historical per-user
    /// endpoint. The token is sanitized to the filesystem/pipe-safe charset (it
    /// lands in a socket path / pipe name).
    /// </summary>
    private static string ResolveSessionToken(out ProcessIdentity? anchor)
    {
        anchor = null;
        var explicitSession = Environment.GetEnvironmentVariable(SessionEnvVar);
        if (!string.IsNullOrWhiteSpace(explicitSession))
            return SanitizeUser(explicitSession);

        anchor = SessionAnchorOverride is { } seam
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
    /// The endpoint is a socket file inside the per-user <see cref="PsBashRuntimeDirectory"/>
    /// (0700). The launcher owns the host process and unlinks the socket when it disposes.
    /// </remarks>
    /// <exception cref="IOException">
    /// The runtime directory leaves fewer than <see cref="MinInvocationSuffixHexChars"/> random
    /// hex chars under the sun_path budget, or cannot be created.
    /// </exception>
    public static (string Scheme, string Endpoint) ResolvePerInvocationEndpoint()
    {
        var pid = ProcessIdOverride is { } pidSeam ? pidSeam() : Environment.ProcessId;
        var guid = Guid.NewGuid().ToString("N");
        EnsureRuntimeDirectory();
        var prefix = Path.Combine(SocketDirectory(), $"host-pi-{pid}-");
        const string ext = ".sock";
        // sun_path caps the WHOLE path, so the random suffix gets whatever room the
        // runtime dir + pid leave. 12 hex chars (48 bits) is ample to keep two concurrent
        // launchers of the same pid distinct; below that the path is pathological.
        var room = UnixSocketPathBudget() - prefix.Length - ext.Length;
        if (room < MinInvocationSuffixHexChars) RequireFits(prefix + new string('0', MinInvocationSuffixHexChars) + ext);
        var unique = guid[..Math.Min(guid.Length, room)];
        return ("unix", prefix + unique + ext);
    }



    /// <summary>Minimum random hex chars kept for per-invocation uniqueness.</summary>
    internal const int MinInvocationSuffixHexChars = 12;

    /// <summary>Longest AF_UNIX path on this OS (<see cref="PsBash.Core.Platform.UnixSocketPath"/>).</summary>
    internal static int UnixSocketPathBudget() => PsBash.Core.Platform.UnixSocketPath.MaxChars;

    private static string SocketDirectory() => PsBashRuntimeDirectory.GetPath();

    /// <summary>
    /// Build a fresh transport instance bound to the resolved endpoint. Each
    /// call returns a new object — transports are typically single-use.
    /// </summary>
    /// <param name="cliOverride">
    /// Optional explicit override forwarded to <see cref="ResolveEndpoint(string)"/>.
    /// </param>
    public static IIpcTransport CreateDefault(string? cliOverride = null)
        => new UnixSocketTransport(ResolveEndpoint(cliOverride).Endpoint);

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
            throw new InvalidEndpointException(
                $"{sourceName} value '{spec}' must be 'scheme:endpoint' (scheme = unix).");

        var scheme = spec[..idx];
        var endpoint = spec[(idx + 1)..];
        if (scheme != "unix")
            throw new InvalidEndpointException(
                scheme == "pipe"
                    ? $"{sourceName} value '{spec}': the named-pipe transport was removed; use 'unix:<socket path>'."
                    : $"{sourceName} value '{spec}' has unknown scheme '{scheme}'. Expected unix.");

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
        // The one deliberate removal of a possibly live socket (UnixSocketGuardTests allow-list): the caller replaces
        // that host on purpose.
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
