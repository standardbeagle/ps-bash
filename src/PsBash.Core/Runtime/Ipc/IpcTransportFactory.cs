using System.Text;

namespace PsBash.Core.Runtime.Ipc;

/// <summary>
/// Resolves the canonical ps-bash-host endpoint. One socket per OS user —
/// interactive REPL and one-shot <c>-c</c> invocations connect to the same
/// listener. Session ids do not participate in endpoint naming; lifecycle
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

    // Test seam: override platform detection without P/Invoke or env hacks.
    internal static Func<bool>? UnixSocketSupportedOverride { get; set; }


    public static bool IsUnixSocketSupported()
    {
        if (UnixSocketSupportedOverride is { } fn) return fn();
        return !OperatingSystem.IsWindows() || Environment.OSVersion.Version.Build >= 17063;
    }

    /// <summary>
    /// Resolve the endpoint a host should bind / a client should connect to.
    /// Precedence: <paramref name="cliOverride"/> &gt; <c>PSBASH_IPC_ENDPOINT</c>
    /// env var &gt; canonical per-user endpoint. The canonical endpoint is a
    /// filesystem path on POSIX, named-pipe name on pre-1803 Windows. One per
    /// user, no per-process suffix.
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
        if (IsUnixSocketSupported())
        {
            var sockDir = Path.Combine(Path.GetTempPath(), "ps-bash");
            Directory.CreateDirectory(sockDir);
            return ("unix", Path.Combine(sockDir, $"host-{user}.sock"));
        }
        return ("pipe", $"psbash-host-{user}");
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
