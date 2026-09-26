using System.Diagnostics;
using PsBash.Core.Runtime.Ipc;
using PsBash.Host.Runtime;
using PsBash.Host.Server;
using Xunit;

namespace PsBash.Host.Tests.Server;

/// <summary>
/// R09 regression coverage: a live-but-slow reader must apply back-pressure, not
/// have the command aborted. The old host inferred "client dead" from a write that
/// stalled for <c>PSBASH_IPC_OUTPUT_STALL_TIMEOUT_MS</c>, which is indistinguishable
/// from a downstream reader (e.g. <c>less</c>) legitimately pausing. A bounded
/// request read and a cap on concurrent connection tasks close the two unbounded
/// waits around the same connection lifecycle.
/// </summary>
[Collection("SdkHost")]
public sealed class ConnectionBackPressureTests
{
    private const string StallTimeoutEnv = "PSBASH_IPC_OUTPUT_STALL_TIMEOUT_MS";
    private const string RequestTimeoutEnv = "PSBASH_REQUEST_READ_TIMEOUT_MS";
    private const string MaxConnectionsEnv = "PSBASH_MAX_CONCURRENT_CONNECTIONS";

    /// <summary>
    /// The headline acceptance case: a reader that makes no progress for longer
    /// than the old stall threshold, then resumes, must receive the WHOLE output
    /// and exit 0. Emits to stderr because stdout is batched into ~32 KB frames,
    /// while stderr frames are one-per-line and therefore able to overflow the
    /// output queue (capacity 4096) and trip the old stall watchdog.
    /// </summary>
    [Fact]
    public async Task SlowReader_HeldBeyondStallThreshold_CommandCompletesWithFullOutput()
    {
        var prior = Environment.GetEnvironmentVariable(StallTimeoutEnv);
        Environment.SetEnvironmentVariable(StallTimeoutEnv, "250");
        try
        {
            await using var pool = new WorkerPool<SdkWorker>(warmTarget: 0, max: 1, SdkWorker.Create);
            await using var stream = new GatedSlowReaderStream();
            await stream.QueueRequestAsync(new Mode.Command(
                "1..4200 | ForEach-Object { Write-BashHostStderr \"line $_\" }"));

            var connection = new Connection(stream, pool);
            var handling = connection.HandleAsync(CancellationToken.None);

            // The drain has dequeued a frame and is blocked writing it, so the
            // producer is now filling the queue behind it.
            Assert.True(await stream.FirstWriteBlocked.Task.WaitAsync(TimeSpan.FromSeconds(30)));

            // Hold the reader past the old 250 ms stall threshold. A liveness-based
            // implementation keeps blocking; the old one would already have thrown.
            await Task.Delay(1500);

            stream.OpenGate();

            await handling.WaitAsync(TimeSpan.FromSeconds(60));

            var frames = new List<(string Line, StreamTag Tag)>();
            var exitCode = await stream.ReadResponseAsync((line, tag) => frames.Add((line, tag)));

            Assert.Equal(0, exitCode);
            Assert.Contains(frames, f => f.Tag == StreamTag.Stderr && f.Line == "line 4200");
        }
        finally
        {
            Environment.SetEnvironmentVariable(StallTimeoutEnv, prior);
        }
    }

    /// <summary>
    /// A client that connects but never sends a request must not hold a connection
    /// task forever. The request read is bounded; on expiry the connection unwinds.
    /// </summary>
    [Fact]
    public async Task IdleClient_RequestReadTimesOut_ConnectionDoesNotHangForever()
    {
        var prior = Environment.GetEnvironmentVariable(RequestTimeoutEnv);
        Environment.SetEnvironmentVariable(RequestTimeoutEnv, "300");
        try
        {
            await using var pool = new WorkerPool<SdkWorker>(warmTarget: 0, max: 1, SdkWorker.Create);
            await using var stream = new SilentStream();

            var connection = new Connection(stream, pool);
            var sw = Stopwatch.StartNew();

            // Old code blocked inside ReadRequestAsync with no bound at all; this
            // WaitAsync is the RED assertion that it returned.
            await connection.HandleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 5000, $"connection took {sw.ElapsedMilliseconds} ms");
        }
        finally
        {
            Environment.SetEnvironmentVariable(RequestTimeoutEnv, prior);
        }
    }

    /// <summary>
    /// Concurrent connection tasks are capped: once the cap is reached the accept
    /// loop stops starting new handlers until a slot frees. Before the fix an
    /// unbounded <c>Task.Run</c> per accepted connection made this count grow with
    /// the number of idle clients.
    /// </summary>
    [Fact]
    public async Task ConcurrentConnections_AreCapped()
    {
        var prior = Environment.GetEnvironmentVariable(MaxConnectionsEnv);
        Environment.SetEnvironmentVariable(MaxConnectionsEnv, "2");
        using var cts = new CancellationTokenSource();
        try
        {
            await using var pool = new WorkerPool<SdkWorker>(warmTarget: 0, max: 1,
                () => throw new InvalidOperationException("no worker may be built for an idle client"));
            var transport = new CountingTransport();
            var streams = new ProbeStream[5];
            for (int i = 0; i < streams.Length; i++)
            {
                streams[i] = new ProbeStream(transport);
                transport.Enqueue(streams[i]);
            }

            await using var server = new HostServer(transport, pool);
            var runTask = server.RunAsync(cts.Token);
            await server.WhenListening.WaitAsync(TimeSpan.FromSeconds(10));

            // Wait until the accept loop has accepted more connections than the cap.
            await WaitForAsync(() => transport.AcceptedCount >= 3, TimeSpan.FromSeconds(10));
            await WaitForAsync(() => transport.ReadingCount >= 2, TimeSpan.FromSeconds(10));

            // Hold briefly: with a cap the third handler must NOT start reading.
            await Task.Delay(500);
            Assert.Equal(2, transport.ReadingCount);

            // Freeing one slot lets the next accepted connection start.
            streams[0].CloseStream();
            await WaitForAsync(() => transport.ReadingCount >= 3, TimeSpan.FromSeconds(10));

            cts.Cancel();
            try { await runTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
        finally
        {
            Environment.SetEnvironmentVariable(MaxConnectionsEnv, prior);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
                throw new TimeoutException("condition not met within the allotted time");
            await Task.Delay(25);
        }
    }

    /// <summary>
    /// Framed-transport double that serves one queued request, then blocks every
    /// write after the STARTED sentinel until the test opens the gate — modelling
    /// a downstream reader paused on a page. Not seekable, so the connection's
    /// client-disconnect watchdog is active.
    /// </summary>
    private sealed class GatedSlowReaderStream : Stream
    {
        private readonly MemoryStream _written = new();
        private readonly TaskCompletionSource _disconnected =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[] _request = [];
        private int _requestPosition;
        private int _writeCount;

        public TaskCompletionSource<bool> FirstWriteBlocked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task QueueRequestAsync(Mode mode)
        {
            using var buffer = new MemoryStream();
            await HostProtocol.WriteRequestAsync(buffer, mode);
            _request = buffer.ToArray();
        }

        public void OpenGate() => _gate.TrySetResult();

        public async Task<int> ReadResponseAsync(Action<string, StreamTag> onLine)
        {
            byte[] snapshot;
            lock (_written) snapshot = _written.ToArray();
            using var replay = new MemoryStream(snapshot);
            return await HostProtocol.ReadResponseAsync(replay, onLine);
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_requestPosition < _request.Length)
            {
                int count = Math.Min(buffer.Length, _request.Length - _requestPosition);
                _request.AsMemory(_requestPosition, count).CopyTo(buffer);
                _requestPosition += count;
                return count;
            }

            await _disconnected.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // Write #1 is the STARTED sentinel; the command has not run yet.
            // Everything after it is command output and is subject to back-pressure.
            bool isOutput = Interlocked.Increment(ref _writeCount) > 1;
            if (isOutput)
            {
                FirstWriteBlocked.TrySetResult(true);
                await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            lock (_written) _written.Write(buffer.Span);
        }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _gate.TrySetResult();
                _disconnected.TrySetResult();
                _written.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>A connection that never sends anything; the read blocks until cancelled or disposed.</summary>
    private sealed class SilentStream : Stream
    {
        private readonly TaskCompletionSource _closed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _closed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _closed.TrySetResult();
            base.Dispose(disposing);
        }
    }

    /// <summary>A connection whose read blocks until the test closes it.</summary>
    private sealed class ProbeStream : Stream
    {
        private readonly CountingTransport _transport;
        private readonly TaskCompletionSource _closed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _noted;

        public ProbeStream(CountingTransport transport) => _transport = transport;

        public void CloseStream() => _closed.TrySetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _noted, 1) == 0)
                _transport.NoteReading();
            await _closed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _closed.TrySetResult();
            base.Dispose(disposing);
        }
    }

    /// <summary>Transport that hands out pre-queued <see cref="ProbeStream"/>s and counts handlers.</summary>
    private sealed class CountingTransport : IIpcTransport
    {
        private readonly Queue<ProbeStream> _queue = new();
        private readonly SemaphoreSlim _available = new(0);
        private int _accepted;
        private int _reading;

        public int AcceptedCount => Volatile.Read(ref _accepted);
        public int ReadingCount => Volatile.Read(ref _reading);

        public string Endpoint => "counting";
        public string Scheme => "pipe";

        public void Enqueue(ProbeStream stream)
        {
            lock (_queue) _queue.Enqueue(stream);
            _available.Release();
        }

        internal void NoteReading() => Interlocked.Increment(ref _reading);

        public Task ListenAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async Task<Stream> AcceptAsync(CancellationToken ct = default)
        {
            await _available.WaitAsync(ct).ConfigureAwait(false);
            ProbeStream stream;
            lock (_queue) stream = _queue.Dequeue();
            Interlocked.Increment(ref _accepted);
            return stream;
        }

        public Task<Stream> ConnectAsync(CancellationToken ct = default) =>
            Task.FromException<Stream>(new NotSupportedException());

        public ValueTask DisposeAsync()
        {
            _available.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
