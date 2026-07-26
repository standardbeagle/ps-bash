using PsBash.Core.Runtime.Ipc;
using PsBash.Host.Runtime;
using PsBash.Host.Server;
using Xunit;

namespace PsBash.Host.Tests.Server;

/// <summary>
/// A command whose launcher has gone away must be CANCELLED, not run to completion.
///
/// Why it matters: <see cref="SdkWorker"/> serializes execution on a PROCESS-WIDE gate
/// (bash variables are <c>$env:</c> and the cwd is process-global, so concurrent
/// execution would corrupt shared state). An abandoned command therefore blocks every
/// other session in the daemon for its full duration. Measured before the fix: a
/// launcher killed 2 s into <c>sleep 30</c> left the next command waiting 30.9 s; after,
/// 0.2 s. The pre-existing IpcOutputQueue stall timeout did not cover this — it only
/// notices when a command PRODUCES OUTPUT and the queue fills, and a silent command
/// never writes a frame.
/// </summary>
[Collection("SdkHost")]
public sealed class ConnectionClientDisconnectTests
{
    [Fact]
    public async Task ClientDisconnectMidCommand_CancelsCommandInsteadOfRunningToCompletion()
    {
        await using var pool = new WorkerPool<SdkWorker>(warmTarget: 0, max: 1, SdkWorker.Create);

        await using var stream = new FakeTransportStream();
        await stream.QueueRequestAsync(new Mode.Command("Start-Sleep -Seconds 60"));

        var connection = new Connection(stream, pool);
        var handling = connection.HandleAsync(CancellationToken.None);

        // Let the command actually start before pulling the rug out.
        Assert.True(await stream.CommandReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(30)));
        stream.SignalClientDisconnected();

        // The whole point: this returns long before the 60 s sleep would finish.
        await handling.WaitAsync(TimeSpan.FromSeconds(30));

        // And the worker slot is returned, so the pool (max: 1) is usable again.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var worker = await pool.AcquireAsync(cts.Token);
        pool.Release(worker);
    }

    [Fact]
    public async Task ClientStaysConnected_CommandCompletesNormally()
    {
        // The no-false-positive guard: the watchdog must only fire on EOF, never
        // merely because a command is slow.
        await using var pool = new WorkerPool<SdkWorker>(warmTarget: 0, max: 1, SdkWorker.Create);

        await using var stream = new FakeTransportStream();
        await stream.QueueRequestAsync(new Mode.Command("Invoke-BashEcho 'still-here'"));

        var connection = new Connection(stream, pool);
        await connection.HandleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));

        var frames = new List<(string Line, StreamTag Tag)>();
        var exitCode = await stream.ReadResponseAsync((line, tag) => frames.Add((line, tag)));

        Assert.Equal(0, exitCode);
        Assert.Contains(frames, f => f.Tag == StreamTag.Stdout && f.Line.Contains("still-here"));
    }

    /// <summary>
    /// A live-transport-shaped stream double: NOT seekable (so it is treated as a real
    /// connection, unlike a MemoryStream), reads serve the queued request and then BLOCK
    /// the way a socket read does, and the test decides when EOF — client disconnect —
    /// happens. Writes are captured for response assertions.
    /// </summary>
    private sealed class FakeTransportStream : Stream
    {
        private readonly MemoryStream _written = new();
        private readonly TaskCompletionSource _disconnected =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[] _request = [];
        private int _requestPosition;

        /// <summary>Completes once the request has been fully consumed — i.e. the
        /// connection has moved on to executing the command.</summary>
        public TaskCompletionSource<bool> CommandReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task QueueRequestAsync(Mode mode)
        {
            using var buffer = new MemoryStream();
            await HostProtocol.WriteRequestAsync(buffer, mode);
            _request = buffer.ToArray();
        }

        public void SignalClientDisconnected() => _disconnected.TrySetResult();

        public async Task<int> ReadResponseAsync(Action<string, StreamTag> onLine)
        {
            using var replay = new MemoryStream(_written.ToArray());
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
                if (_requestPosition >= _request.Length)
                    CommandReadStarted.TrySetResult(true);
                return count;
            }

            // Request drained: behave like a socket with nothing more to deliver —
            // block until the peer closes, then report EOF.
            await _disconnected.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_written) _written.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (_written) _written.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        // The load-bearing property: a seekable stream is treated as an in-memory
        // buffer and is deliberately NOT watched for disconnect.
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
                SignalClientDisconnected();
                _written.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
