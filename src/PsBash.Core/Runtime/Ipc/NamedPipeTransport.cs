using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

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
        _pipeName = NormalizePipeNameForPlatform(pipeName);
    }

    /// <summary>
    /// On non-Windows .NET, <see cref="NamedPipeServerStream"/> is implemented
    /// over an AF_UNIX socket at <c>{TempPath}/CoreFxPipe_{pipeName}</c>. The
    /// UDS sun_path limit is 108 bytes on Linux and 104 on macOS — and macOS
    /// <c>$TMPDIR</c> alone is ~47 chars under <c>/var/folders/...</c>. A
    /// 48-char pipe name (the test pattern <c>psbash-shutdown-{Guid:N}</c>)
    /// blows the limit on macOS. Replace any name whose resulting UDS path
    /// would not fit with a stable SHA-256 prefix so both server and client,
    /// given the same input name, agree on the shortened form. Windows pipes
    /// have no path length issue and are left untouched.
    /// </summary>
    private static string NormalizePipeNameForPlatform(string pipeName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return pipeName;

        // Conservative cap: 104 (macOS UDS limit) - len(tempPath) - len("CoreFxPipe_") - 1 (trailing null).
        var tempPath = Path.GetTempPath();
        if (!tempPath.EndsWith(Path.DirectorySeparatorChar)) tempPath += Path.DirectorySeparatorChar;
        var budget = 104 - tempPath.Length - "CoreFxPipe_".Length - 1;
        if (budget < 16) budget = 16; // pathological tempdir; still emit something
        if (pipeName.Length <= budget) return pipeName;

        // Stable shortening: 12-char hex hash prefixed with a fixed tag so it's
        // recognizable in /tmp listings during debugging. Different callers
        // hashing the same input name reach the same socket file.
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(pipeName));
        var sb = new StringBuilder("psb-", budget);
        for (int i = 0; i < 12 && sb.Length < budget; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }

    public Task ListenAsync(CancellationToken ct = default)
    {
        if (_listening) throw new InvalidOperationException("Already listening");
        ThrowIfDisposed();
        DeletePipeSocketFile();
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
        if (_listening) DeletePipeSocketFile();
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
        return new NamedPipeServerStream(
            _pipeName, PipeDirection.InOut, MaxInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            inBufferSize: 65536, outBufferSize: 65536);
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

        // Explicit 64 KB buffers: CreateNamedPipe with size 0 may mean "no kernel
        // buffer" on some Windows builds, causing WriteAsync to block until the
        // peer reads every byte. Explicit sizes decouple writer from reader.
        return NamedPipeServerStreamAcl.Create(
            pipeName: _pipeName,
            direction: PipeDirection.InOut,
            maxNumberOfServerInstances: MaxInstances,
            transmissionMode: PipeTransmissionMode.Byte,
            options: PipeOptions.Asynchronous,
            inBufferSize: 65536,
            outBufferSize: 65536,
            pipeSecurity: security);
    }

    private void DeletePipeSocketFile()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        // Cross-plat dotnet supports named pipes on Linux/macOS via AF_UNIX
        // socket files at {TempPath}/CoreFxPipe_{name}. .NET does not always
        // reliably unlink that file after cancellation or process exit. Remove
        // it when a listener starts/stops, not between accepts, because deleting
        // the file for a live listener makes later clients unable to connect.
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + _pipeName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { /* let bind surface the real error */ }
        catch (UnauthorizedAccessException) { /* ditto */ }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed != 0) throw new ObjectDisposedException(nameof(NamedPipeTransport));
    }
}
