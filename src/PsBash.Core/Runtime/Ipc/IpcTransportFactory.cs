using System.Text;

namespace PsBash.Core.Runtime.Ipc;

/// <summary>
/// Resolves the canonical ps-bash-host endpoint. One socket per OS user —
/// interactive REPL and one-shot <c>-c</c> invocations connect to the same
/// listener. No session ids, no lock files: presence is detected by attempting
/// to connect.
/// </summary>
public static class IpcTransportFactory
{
    // Test seam: override platform detection without P/Invoke or env hacks.
    internal static Func<bool>? UnixSocketSupportedOverride { get; set; }

    public static bool IsUnixSocketSupported()
    {
        if (UnixSocketSupportedOverride is { } fn) return fn();
        return !OperatingSystem.IsWindows() || Environment.OSVersion.Version.Build >= 17063;
    }

    /// <summary>
    /// The canonical endpoint identifier (filesystem path on POSIX, named-pipe
    /// name on pre-1803 Windows). One per user, no per-process suffix.
    /// </summary>
    public static (string Scheme, string Endpoint) ResolveEndpoint()
    {
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
    /// Build a fresh transport instance bound to the canonical endpoint.
    /// Each call returns a new object — transports are typically single-use.
    /// </summary>
    public static IIpcTransport CreateDefault()
    {
        var (scheme, endpoint) = ResolveEndpoint();
        return scheme == "unix"
            ? new UnixSocketTransport(endpoint)
            : new NamedPipeTransport(endpoint);
    }

    /// <summary>
    /// Retire the current endpoint so a replacement host can bind the canonical
    /// address. On AF_UNIX this unlinks the socket path; named pipes have no
    /// filesystem endpoint to remove.
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
