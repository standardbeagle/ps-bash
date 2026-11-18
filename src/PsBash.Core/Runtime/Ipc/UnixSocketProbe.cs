namespace PsBash.Core.Runtime.Ipc;

/// <summary>
/// Whether a Unix socket file is stale. This is the only test before a socket file is removed: on POSIX, deleting the
/// file of a LIVE socket succeeds silently, so a host that took over its path left the live host running where no
/// launcher could reach it. On Windows the delete fails, and the newcomer's bind then died with an unhandled
/// "address already in use".
/// </summary>
public static class UnixSocketProbe
{
    /// <summary>
    /// True only when a connect is refused: a socket nobody listens on, or a file that is not a socket. A connect
    /// that succeeds or times out, or any other error, leaves the file alone.
    /// </summary>
    public static bool NothingListens(string path)
    {
        using var sock = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
        try
        {
            var connect = sock.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
            if (!connect.Wait(TimeSpan.FromMilliseconds(500))) return false;
            return false; // connected: a host is listening
        }
        catch (AggregateException ae) when (ae.InnerException is System.Net.Sockets.SocketException se)
        {
            return IsRefused(se.SocketErrorCode);
        }
        catch (System.Net.Sockets.SocketException se)
        {
            return IsRefused(se.SocketErrorCode);
        }
        catch { return false; }
    }

    /// <summary>
    /// The errors that prove nobody listens at the path. A path that is not a socket is refused as ECONNREFUSED on
    /// Linux and Windows, but as ENOTSOCK on macOS (XNU unp_connect: the vnode is not a socket); the socket we connect
    /// with always is one, so ENOTSOCK can only describe the target. REGRESSION: only ConnectionRefused counted, so on
    /// macOS such a file was never reaped or taken over.
    /// </summary>
    internal static bool IsRefused(System.Net.Sockets.SocketError error)
        => error is System.Net.Sockets.SocketError.ConnectionRefused or System.Net.Sockets.SocketError.NotSocket;
}

/// <summary>Another live host already serves this endpoint: the host that hit it lost a start race and exits cleanly.</summary>
public sealed class EndpointInUseException(string endpoint, Exception? inner = null)
    : IOException($"endpoint already served by another host: {endpoint}", inner)
{
    public string Endpoint { get; } = endpoint;
}
