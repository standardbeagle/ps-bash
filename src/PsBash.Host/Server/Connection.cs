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

    // Bound on how long a connection may sit half-open before its request arrives.
    // Without it an idle client that connects and sends nothing holds a connection
    // task (and, once the cap below is reached, an accept slot) forever. Generous
    // relative to a launcher that dials and immediately writes its request.
    private const int DefaultRequestReadTimeoutMs = 30_000;

    internal async Task HandleAsync(CancellationToken ct)
    {
        Mode mode;
        try
        {
            using var requestRead = CancellationTokenSource.CreateLinkedTokenSource(ct);
            requestRead.CancelAfter(RequestReadTimeoutMs());
            mode = await HostProtocol.ReadRequestAsync(_stream, requestRead.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The read deadline elapsed with no complete request: the client is
            // idle or gone. Unwind quietly; caller cancellation still propagates.
            WorkerPool<SdkWorker>.DiagLog("Connection: request read timed out; closing idle connection");
            return;
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

        // Client-disconnect watchdog. Back-pressure must BLOCK on a live-but-slow
        // reader (a downstream `less` paused on a page), so write latency can no
        // longer be the liveness signal. This pending 1-byte read is: it only ever
        // completes on EOF/reset, never on a slow consumer. On disconnect it trips
        // execCts, which unblocks both the command and any blocked output write.
        // A silent command (`sleep 30`) never writes a frame, so the output path
        // alone could not notice a dead launcher — this watchdog covers both.
        //
        // Installed only for a live duplex transport in framed mode. A SEEKABLE stream
        // is an in-memory buffer (test doubles), where a read returning 0 means "end of
        // buffer", not "peer gone" — watching one would abandon every command.
        // Interactive sessions are excluded because their stream carries additional
        // protocol traffic that this detector must not consume.
        using var clientGone = new CancellationTokenSource();
        using var watchStop = new CancellationTokenSource();
        using var execCts = CancellationTokenSource.CreateLinkedTokenSource(ct, clientGone.Token);
        if (sessionMode == SessionMode.Framed && !_stream.CanSeek)
            _ = WatchForClientDisconnectAsync(_stream, clientGone, watchStop.Token);

        // `await using` binds disposal to the whole remaining scope, so ANY exit
        // after construction — including a throw from _pool.AcquireAsync below
        // (cancellation / pool disposed / worker-spawn failure) — disposes the
        // queue and unpins its drain task. Disposing an unwritten/never-faulted
        // queue is safe: DisposeAsync → CompleteAsync → CompleteAdding on an empty
        // queue lets the drain foreach exit immediately (no hang). In interactive
        // mode frameWriter is null and `await using` on null is a no-op. The queue
        // is built on execCts so a client disconnect still unblocks a full-queue
        // write (back-pressure) — without aborting on mere slowness.
        await using IpcOutputQueue? frameWriter = sessionMode == SessionMode.Interactive
            ? null
            : new IpcOutputQueue(_stream, execCts.Token);

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
        SdkWorker? worker = null;
        int exitCode;

        try
        {
            worker = await _pool.AcquireAsync(execCts.Token).ConfigureAwait(false);
            WorkerPool<SdkWorker>.DiagLog("Connection: acquired; executing command");

            // R05: acknowledge execution-start on the framed channel BEFORE the
            // command runs and before any output frame. The launcher retries a
            // pre-output transport reset ONLY when it never saw this frame, so a
            // reset after a silent command completed (`echo x >> f`, `rm`,
            // `mkdir`) can no longer re-run it. Emitted directly on the stream
            // (not via the output queue) so it cannot interleave with output; the
            // queue is empty here because execution has not started. Interactive
            // sessions are excluded — their launcher uses the PTY, not this
            // framed-retry reader.
            if (sessionMode == SessionMode.Framed)
                await HostProtocol.WriteStartedAsync(_stream, execCts.Token).ConfigureAwait(false);

            exitCode = await worker.ExecuteWithOutputAsync(command, outputSink, errorSink, execCts.Token);
            WorkerPool<SdkWorker>.DiagLog($"Connection: executed, exit={exitCode}");
        }
        catch (OperationCanceledException) when (clientGone.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The launcher is gone: there is no one to receive a response, and the
            // command has been stopped. Abandon quietly — the point of the cancel is
            // to release the exec gate promptly for everyone else.
            // The finally below cancels the watchdog and releases the worker.
            WorkerPool<SdkWorker>.DiagLog("Connection: client disconnected; command abandoned");
            return;
        }
        catch (Exception ex) when (ex is not IpcOutputException
                                   && !(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            // Worker construction (including assembly/module load) and failures
            // before command execution used to escape to HostServer, which could
            // only log and close the stream. Give the launcher a complete,
            // actionable protocol response instead. Transport failures and caller
            // cancellation still escape unchanged: attempting another write to a
            // disconnected/cancelled client would only obscure the original event.
            if (frameWriter is not null)
                await frameWriter.CompleteAsync().ConfigureAwait(false);

            var detail = string.IsNullOrWhiteSpace(ex.Message)
                ? ex.GetType().Name
                : ex.Message;
            await HostProtocol.WriteResponseLineAsync(
                _stream,
                $"ps-bash-host worker failure: {detail}",
                StreamTag.Stderr,
                ct).ConfigureAwait(false);
            await HostProtocol.WriteExitAsync(_stream, 1, ct).ConfigureAwait(false);
            WorkerPool<SdkWorker>.DiagLog($"Connection: worker failure: {detail}");
            return;
        }
        finally
        {
            watchStop.Cancel();
            if (worker is not null)
            {
                _pool.Release(worker);
                WorkerPool<SdkWorker>.DiagLog("Connection: released worker");
            }
            if (frameWriter is not null)
            {
                // CompleteAsync drains the queue and rethrows a writer failure so the
                // caller sees it — the `await using` DisposeAsync at scope end would
                // swallow that failure ("best-effort" teardown), so CompleteAsync stays
                // here on the normal exit path. DisposeAsync re-runs CompleteAdding
                // (idempotent) on the already-drained queue and disposes it.
                //
                // Exception: when the CLIENT is gone, a failed drain is the expected
                // outcome, not news. Swallow it so an abandoned connection unwinds
                // quietly instead of logging a transport error for something we
                // deliberately cancelled.
                if (clientGone.IsCancellationRequested)
                {
                    try { await frameWriter.CompleteAsync().ConfigureAwait(false); }
                    catch { /* client already gone */ }
                }
                else
                {
                    await frameWriter.CompleteAsync().ConfigureAwait(false);
                }
            }
        }
        // Belt-and-suspenders for the abandoned case: if the worker returned normally
        // after its pipeline was stopped (rather than surfacing the cancellation), there
        // is still nobody to write to. Skip the exit frame instead of throwing an
        // IOException that would surface as a spurious "connection error" in the log.
        if (clientGone.IsCancellationRequested)
        {
            WorkerPool<SdkWorker>.DiagLog("Connection: client disconnected; skipping exit frame");
            return;
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

    /// <summary>
    /// Trips <paramref name="clientGone"/> when the launcher closes or resets its end
    /// of the connection while a command is running.
    ///
    /// A single pending 1-byte read is the transport-agnostic detector: in framed mode
    /// the whole request (including any stdin/script body) is consumed by
    /// <c>ReadRequestAsync</c> before this point and the client sends nothing further,
    /// so this read only ever completes with EOF (clean close) or throws (reset). It
    /// deliberately does NOT fire on a read returning data — that would mean an
    /// unexpected protocol byte, which is not evidence of a disconnect.
    ///
    /// Never throws: the watchdog is advisory, and on some transports a pending read
    /// is not truly cancellable, so it may instead be completed later by the stream's
    /// disposal. Every outcome is swallowed.
    /// </summary>
    private static async Task WatchForClientDisconnectAsync(
        Stream stream, CancellationTokenSource clientGone, CancellationToken stop)
    {
        try
        {
            var buffer = new byte[1];
            if (await stream.ReadAsync(buffer.AsMemory(0, 1), stop).ConfigureAwait(false) == 0)
                Trip(clientGone);
        }
        catch (OperationCanceledException) { /* command finished first */ }
        catch (ObjectDisposedException) { /* connection already torn down */ }
        catch (IOException) { Trip(clientGone); /* reset by peer */ }
        catch (Exception) { /* advisory only — never fail the connection */ }

        static void Trip(CancellationTokenSource cts)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private readonly record struct IpcOutputFrame(StreamTag Tag, string Line);

    /// <summary>
    /// Marks failures of this connection's framed response transport. Worker
    /// code may legitimately throw <see cref="IOException"/> (including
    /// assembly/module loading failures), so IOException itself is not a safe
    /// proxy for a disconnected client.
    /// </summary>
    private sealed class IpcOutputException : IOException
    {
        public IpcOutputException(string message, Exception? inner = null)
            : base(message, inner) { }
    }

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
                throw new IpcOutputException("IPC output writer failed.", _failure);

            try
            {
                // Back-pressure, not a liveness verdict. A full queue means the
                // consumer is slow (a `less` paused on a page), not dead, so block
                // until a slot frees. Death is signalled by the client-disconnect
                // watchdog cancelling _ct (execCts), which unblocks this Add with an
                // OperationCanceledException; a failed drain also completes the queue
                // and makes Add throw InvalidOperationException.
                _queue.Add(new IpcOutputFrame(tag, line), _ct);
            }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested)
            {
                // Preserve caller cancellation all the way through HandleAsync;
                // it is lifecycle control, not a disconnected output transport.
                throw;
            }
            catch (InvalidOperationException ex)
            {
                throw new IpcOutputException("IPC output writer is closed.", ex);
            }
        }

        public async Task CompleteAsync()
        {
            try { _queue.CompleteAdding(); } catch { }
            await _drainTask.ConfigureAwait(false);
            if (_failure is not null)
                throw new IpcOutputException("IPC output writer failed.", _failure);
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

    private static int RequestReadTimeoutMs()
    {
        var raw = Environment.GetEnvironmentVariable("PSBASH_REQUEST_READ_TIMEOUT_MS");
        return int.TryParse(raw, out var value) && value > 0 ? value : DefaultRequestReadTimeoutMs;
    }
}
