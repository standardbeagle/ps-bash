using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PsBash.Core.Runtime.Ipc;

/// <summary>
/// Windows named-pipe transport. Used as the AF_UNIX fallback on Windows
/// platforms where socket paths collide with the legacy 108-byte sun_path
/// limit or when the host wants ACL-based security rather than POSIX modes.
/// </summary>
/// <remarks>
/// The first server instance applies a user-only ACL to the pipe. We cap
/// <c>maxNumberOfServerInstances = 16</c> matching the Unix backlog so a
/// fork-bombing client can't exhaust pipe instances on the host.
/// </remarks>
public sealed class NamedPipeTransport : IIpcTransport
{
    private const int MaxInstances = 16;
    private readonly string _pipeName;
    private bool _listening;
    private int _disposed;

    public string Endpoint => _pipeName;
    public string Scheme => "pipe";

    public NamedPipeTransport(string pipeName)
    {
        if (string.IsNullOrEmpty(pipeName)) throw new ArgumentException("pipeName required", nameof(pipeName));
        // Reject backslash-prefixed forms so callers don't confuse the kernel
        // namespace path (\\.\pipe\name) with the bare name we want.
        if (pipeName.StartsWith(@"\\")) throw new ArgumentException("pipeName must be the bare name, not the kernel path", nameof(pipeName));
        _pipeName = pipeName;
    }

    public Task ListenAsync(CancellationToken ct = default)
    {
        if (_listening) throw new InvalidOperationException("Already listening");
        ThrowIfDisposed();
        // Listening is per-connection on Windows pipes — there is no
        // long-lived listener socket. We just record state; AcceptAsync
        // creates the server stream on demand.
        _listening = true;
        return Task.CompletedTask;
    }

    public async Task<Stream> AcceptAsync(CancellationToken ct = default)
    {
        if (!_listening) throw new InvalidOperationException("Call ListenAsync first");
        ThrowIfDisposed();

        var server = CreatePipeServer();
        try
        {
            await server.WaitForConnectionAsync(ct);
            return server;
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    public async Task<Stream> ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var client = new NamedPipeClientStream(
            serverName: ".",
            pipeName: _pipeName,
            direction: PipeDirection.InOut,
            options: PipeOptions.Asynchronous,
            impersonationLevel: TokenImpersonationLevel.None,
            inheritability: HandleInheritability.None);
        try
        {
            await client.ConnectAsync(ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        // Outstanding server streams are owned by the caller of AcceptAsync.
        // Nothing global to release here.
        _listening = false;
        return ValueTask.CompletedTask;
    }

    private NamedPipeServerStream CreatePipeServer()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return CreateWindowsPipeServer();
        }
        // Cross-plat dotnet supports named pipes on Linux/macOS via files in
        // /tmp; ACL is N/A there. We still create a basic server so the
        // transport runs in tests on non-Windows hosts.
        return new NamedPipeServerStream(
            _pipeName, PipeDirection.InOut, MaxInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    [SupportedOSPlatform("windows")]
    private NamedPipeServerStream CreateWindowsPipeServer()
    {
        // Owner-only DACL: deny everyone except the current user.
        var security = new PipeSecurity();
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Unable to resolve current Windows user SID");
        security.AddAccessRule(new PipeAccessRule(
            currentUser,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName: _pipeName,
            direction: PipeDirection.InOut,
            maxNumberOfServerInstances: MaxInstances,
            transmissionMode: PipeTransmissionMode.Byte,
            options: PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: security);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed != 0) throw new ObjectDisposedException(nameof(NamedPipeTransport));
    }
}
