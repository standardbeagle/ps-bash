namespace PsBash.Core.Runtime.Ipc;

/// <summary>
/// Bidirectional duplex stream abstraction over an OS IPC primitive.
/// The host listens with <see cref="ListenAsync"/>; the launcher dials with
/// <see cref="ConnectAsync"/>. Both return a <see cref="Stream"/> that supports
/// concurrent read+write for the wire protocol layer (T04).
/// </summary>
/// <remarks>
/// Implementations: <see cref="UnixSocketTransport"/> (POSIX),
/// <see cref="NamedPipeTransport"/> (Windows fallback).
/// </remarks>
public interface IIpcTransport : IAsyncDisposable
{
    /// <summary>
    /// Endpoint identifier used to advertise this transport in the host lock
    /// file: an absolute filesystem path for <see cref="UnixSocketTransport"/>
    /// or a pipe name (no <c>\\.\pipe\</c> prefix) for <see cref="NamedPipeTransport"/>.
    /// </summary>
    string Endpoint { get; }

    /// <summary>
    /// Lock-file scheme prefix: <c>"unix"</c> or <c>"pipe"</c>. Combined with
    /// <see cref="Endpoint"/> as <c>{Scheme}:{Endpoint}</c> for advertisement.
    /// </summary>
    string Scheme { get; }

    /// <summary>
    /// Begin listening (host side). After this returns, callers may
    /// <see cref="AcceptAsync"/> to receive a connection. Idempotent — calling
    /// twice on the same instance throws <see cref="InvalidOperationException"/>.
    /// </summary>
    Task ListenAsync(CancellationToken ct = default);

    /// <summary>
    /// Wait for the next inbound connection (host side). Returns a duplex
    /// stream ready for read+write. Caller owns the returned stream and must
    /// dispose it.
    /// </summary>
    Task<Stream> AcceptAsync(CancellationToken ct = default);

    /// <summary>
    /// Dial the host (launcher side). Returns a duplex stream ready for
    /// read+write. Caller owns the returned stream and must dispose it.
    /// </summary>
    Task<Stream> ConnectAsync(CancellationToken ct = default);
}
