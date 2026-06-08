using PsBash.Core.Runtime.Ipc;
using PsBash.Host.Runtime;
using System.Collections.Concurrent;

namespace PsBash.Host.Server;

/// <summary>
/// Handles a single accepted connection: reads one request, checks out an isolated
/// <see cref="SdkWorker"/> from the <see cref="WorkerPool"/>, executes the command,
/// streams the response, and releases the worker (whose runspace is discarded so
/// the next command runs in a clean session).
/// </summary>
internal sealed class Connection
{
    // Belt-and-suspenders reset of globals that a transpiled command may set
    // ('set -e' → __BashErrexit; positional params via BuildPositionalPreamble).
    // With per-connection pooled isolation each worker is already a fresh runspace,
    // so this is redundant for the daemon path — but it is cheap and keeps any
    // single-worker / warm-spare edge correct.
    private const string PerInvocationReset =
        "$global:__BashErrexit = $false; " +
        "$ErrorActionPreference = 'Continue'; " +
        "$global:LASTEXITCODE = 0; " +
        "$global:BashPositional = $null; " +
        "$global:BashPositional0 = $null; ";

    private readonly Stream _stream;
    private readonly WorkerPool<SdkWorker> _pool;
    private readonly HostServer? _server;

    internal Connection(Stream stream, WorkerPool<SdkWorker> pool, HostServer? server = null)
    {
        _stream = stream;
        _pool = pool;
        _server = server;
    }

    internal async Task HandleAsync(CancellationToken ct)
    {
        Mode mode;
        try
        {
            mode = await HostProtocol.ReadRequestAsync(_stream, ct);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException)
        {
            WorkerPool<SdkWorker>.DiagLog($"Connection: malformed request: {ex.Message}");
            return;
        }

        string? command = mode switch
        {
            Mode.Health => null,
            Mode.Command c => c.Body,
            Mode.Stdin s => s.Body,
            Mode.Script sc => sc.Body,
            Mode.Interactive => null,
            _ => null
        };

        // PTY-4: pull the session-mode discriminant off whichever variant we're
        // handling. Only Command/Stdin/Script carry a SessionMode; Health and
        // Interactive (the legacy "begin REPL" handshake) always run framed.
        var sessionMode = mode switch
        {
            Mode.Command c => c.Session,
            Mode.Stdin s => s.Session,
            Mode.Script sc => sc.Session,
            _ => SessionMode.Framed,
        };

        if (mode is Mode.Shutdown sd)
        {
            await HostProtocol.WriteResponseLineAsync(_stream, HostProtocol.ShutdownAcceptedPayload, ct);
            await HostProtocol.WriteExitAsync(_stream, 0, ct);
            // Fire-and-forget the server-side drain so the response is delivered
            // even if the host's accept loop tears down immediately. The
            // server's RequestShutdownAsync excludes this connection because
            // ConnectionEnded fires when HandleAsync returns.
            if (_server is not null)
                _ = Task.Run(() => _server.RequestShutdownAsync(sd.DeadlineMs));
            return;
        }

        if (mode is Mode.Health)
        {
            // Health must not consume a pool slot — it only inspects warm state.
            if (_pool.IsReady)
            {
                await HostProtocol.WriteResponseLineAsync(_stream, HostProtocol.HealthPayload, ct);
                await HostProtocol.WriteExitAsync(_stream, 0, ct);
            }
            else if (_pool.FirstWarmError is not null && !_pool.IsWarming)
            {
                await HostProtocol.WriteResponseLineAsync(_stream, "ps-bash-host worker failed", ct);
                await HostProtocol.WriteExitAsync(_stream, 1, ct);
            }
            else
            {
                await HostProtocol.WriteResponseLineAsync(_stream, HostProtocol.HealthStartingPayload, ct);
                await HostProtocol.WriteExitAsync(_stream, HostProtocol.HealthStartingExitCode, ct);
            }
            return;
        }

        if (command == null)
        {
            await HostProtocol.WriteExitAsync(_stream, 0, ct);
            return;
        }

        // Reset per-invocation state so globals set by one -c or script run
        // (e.g. set -e → $global:__BashErrexit, positional params) do not
        // leak into the next invocation on the shared SdkWorker runspace.
        command = PerInvocationReset + command;

        IpcOutputQueue? frameWriter = sessionMode == SessionMode.Interactive
            ? null
            : new IpcOutputQueue(_stream, ct);

        // PTY-4: in interactive mode the host runspace's Console.Out is the PTY
        // slave (PtySpawner wired stdio inheritance). Bypass the IPC writer so
        // command output bytes flow straight from Out-Default / Write-Host /
        // BashText emitters to the terminal — preserving raw TUI byte fidelity
        // (escape sequences, cursor moves) and removing the IPC line-framing
        // throughput bottleneck. The IPC channel then carries only protocol
        // events: the EXIT sentinel and the trailing PROMPT-READY lifecycle
        // signal.
        //
        // Output sink contract per mode (see SdkWorker.RunCommand for the
        // forwarder wiring):
        //   Framed       → output != null → IPC WriteOutput, no console writes
        //   Interactive  → output == null → SdkWorker falls back to Console.Write
        //                                    (and ExitTrackingHostUI's forwarder
        //                                    is also wired to Console.Write so
        //                                    Out-Default formatter rows land in
        //                                    the same byte stream)
        Action<string>? outputSink = sessionMode == SessionMode.Interactive
            ? null
            : line => frameWriter!.Write(line, StreamTag.Stdout);

        // REFACTOR-4: in framed mode the stderr sink is a STDERR-tagged IPC
        // frame writer; in interactive mode (PTY) stderr — like stdout — goes
        // straight to the host's Console (the PTY slave), so pass null and let
        // SdkWorker fall back to Console.Error.
        Action<string>? errorSink = sessionMode == SessionMode.Interactive
            ? null
            : line => frameWriter!.Write(line, StreamTag.Stderr);

        // Check out an isolated worker for this one command; discard it on the way
        // out so the next -c never sees this command's session state. Distinct
        // connections get distinct workers, so concurrent launchers run in parallel.
        WorkerPool<SdkWorker>.DiagLog("Connection: acquiring worker");
        var worker = await _pool.AcquireAsync(ct).ConfigureAwait(false);
        WorkerPool<SdkWorker>.DiagLog("Connection: acquired; executing command");
        int exitCode;
        try
        {
            exitCode = await worker.ExecuteWithOutputAsync(command, outputSink, errorSink, ct);
            WorkerPool<SdkWorker>.DiagLog($"Connection: executed, exit={exitCode}");
        }
        finally
        {
            _pool.Release(worker);
            WorkerPool<SdkWorker>.DiagLog("Connection: released worker");
            if (frameWriter is not null)
            {
                await frameWriter.CompleteAsync().ConfigureAwait(false);
            }
        }
        await HostProtocol.WriteExitAsync(_stream, exitCode, ct);
        WorkerPool<SdkWorker>.DiagLog("Connection: wrote exit");

        if (sessionMode == SessionMode.Interactive)
        {
            // Lifecycle cue: the host is idle and the launcher may re-take
            // terminal control (restore line-editor cursor, repaint prompt).
            // Skipped in framed mode for back-compat — pre-PTY-4 launchers
            // would treat the unexpected line as a malformed response frame.
            await HostProtocol.WritePromptReadyAsync(_stream, ct);
        }
    }

    private readonly record struct IpcOutputFrame(StreamTag Tag, string Line);

    private sealed class IpcOutputQueue : IAsyncDisposable
    {
        private const int DefaultCapacity = 4096;

        private readonly BlockingCollection<IpcOutputFrame> _queue;
        private readonly CancellationToken _ct;
        private readonly Task _drainTask;
        private Exception? _failure;

        public IpcOutputQueue(Stream stream, CancellationToken ct)
        {
            _ct = ct;
            _queue = new BlockingCollection<IpcOutputFrame>(QueueCapacity());
            _drainTask = Task.Run(async () =>
            {
                try
                {
                    foreach (var frame in _queue.GetConsumingEnumerable(ct))
                    {
                        await HostProtocol.WriteResponseLineAsync(stream, frame.Line, frame.Tag, ct)
                            .ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Connection is shutting down.
                }
                catch (Exception ex)
                {
                    _failure = ex;
                    try { _queue.CompleteAdding(); } catch { }
                }
            }, CancellationToken.None);
        }

        public void Write(string line, StreamTag tag)
        {
            if (_failure is not null)
                throw new IOException("IPC output writer failed.", _failure);

            try
            {
                _queue.Add(new IpcOutputFrame(tag, line), _ct);
            }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
            {
                throw new IOException("IPC output writer is closed.", ex);
            }
        }

        public async Task CompleteAsync()
        {
            try { _queue.CompleteAdding(); } catch { }
            await _drainTask.ConfigureAwait(false);
            if (_failure is not null)
                throw new IOException("IPC output writer failed.", _failure);
        }

        public async ValueTask DisposeAsync()
        {
            try { await CompleteAsync().ConfigureAwait(false); }
            catch { /* connection teardown best-effort */ }
            _queue.Dispose();
        }

        private static int QueueCapacity()
        {
            var raw = Environment.GetEnvironmentVariable("PSBASH_IPC_OUTPUT_QUEUE_CAPACITY");
            return int.TryParse(raw, out var value) && value > 0
                ? Math.Max(value, 16)
                : DefaultCapacity;
        }
    }
}
