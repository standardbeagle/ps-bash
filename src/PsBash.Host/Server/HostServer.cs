using System.IO.Pipes;
using PsBash.Core.Runtime.Ipc;
using PsBash.Host.Runtime;

namespace PsBash.Host.Server;

/// <summary>
/// Accept loop that owns an <see cref="IIpcTransport"/> and dispatches each
/// incoming connection to a <see cref="Connection"/>. Exceptions at the
/// connection boundary are swallowed so the host stays alive after bad requests.
///
/// Accepts a <c>Task&lt;SdkWorker&gt;</c> so the transport can start listening
/// and write its lock file before the runspace finishes initializing. Connections
/// that arrive while the runspace is still warming up are held at the worker's
/// internal semaphore until the first <see cref="IWorker.ExecuteAsync"/> call
/// can proceed.
/// </summary>
public sealed class HostServer : IAsyncDisposable
{
    private readonly IIpcTransport _transport;
    private readonly Task<SdkWorker> _workerTask;
    private readonly IdleShutdown? _idle;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public HostServer(IIpcTransport transport, Task<SdkWorker> workerTask, IdleShutdown? idle = null)
    {
        _transport = transport;
        _workerTask = workerTask;
        _idle = idle;
    }

    /// <summary>Completes once <see cref="RunAsync"/> has called ListenAsync and is ready to accept.</summary>
    public Task WhenListening => _ready.Task;

    public async Task RunAsync(CancellationToken ct = default)
    {
        await _transport.ListenAsync(ct);
        _ready.TrySetResult();

        while (!ct.IsCancellationRequested)
        {
            Stream stream;
            try
            {
                stream = await _transport.AcceptAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"accept error: {ex.Message}");
                // Small delay prevents a tight busy-loop if AcceptAsync keeps failing
                // (e.g., transport in a persistently broken state).
                try { await Task.Delay(10, ct); } catch (OperationCanceledException) { break; }
                continue;
            }

            _ = HandleConnectionAsync(stream, ct);
        }
    }

    private async Task HandleConnectionAsync(Stream stream, CancellationToken ct)
    {
        _idle?.ConnectionStarted();
        try
        {
            await using (stream)
            {
                // Await worker init before handling — callers connected early while
                // the runspace was still loading will unblock here.
                var worker = await _workerTask.ConfigureAwait(false);
                var conn = new Connection(stream, worker);
                await conn.HandleAsync(ct);
            }
        }
        catch (Exception ex)
        {
            Log($"connection error: {ex.Message}");
        }
        finally
        {
            _idle?.ConnectionEnded();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            await _transport.DisposeAsync();
    }

    private static void Log(string message)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".psbash");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(
                Path.Combine(logDir, "host.log"),
                $"{DateTime.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch { }
    }
}
