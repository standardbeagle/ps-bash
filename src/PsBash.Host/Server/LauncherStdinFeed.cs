using System.Management.Automation;
using System.Text;
using PsBash.Core;
using PsBash.Core.Runtime.Ipc;

namespace PsBash.Host.Server;

/// <summary>
/// The launcher's forwarded stdin as seen by the host: raw byte chunks arrive on the connection
/// (<see cref="HostProtocol.StdinFramePrefix"/> frames, pushed by <see cref="PumpAsync"/>) and are turned
/// into the LINE RECORDS of a lazy <see cref="StdinCursor"/> — a normal record is a line without its
/// terminator, a final piece with no trailing newline is an exact-bytes (<c>NoTrailingNewline</c>)
/// record, exactly like <c>printf 'a'</c> output, so bytes survive end to end. Decoding goes through the
/// stateful <see cref="RawBytes"/> decoder: a chunk may end mid-character and invalid UTF-8 travels as
/// escaped-byte markers.
/// <para>
/// The queue is bounded by bytes (<see cref="MaxQueuedBytes"/>): past that the pump stops pulling frames
/// off the socket (back-pressure reaches the launcher) instead of buffering an unbounded pipe. The bound
/// is large on purpose — a parked pump cannot observe the peer's disconnect.
/// </para>
/// </summary>
internal sealed class LauncherStdinFeed : IDisposable
{
    /// <summary>
    /// Unread bytes held in memory; the rest of a backlog spills to a temp file (<see cref="ChunkQueue"/>),
    /// so the pump keeps draining the socket and always sees the peer's EOF. A pump parked on a full
    /// queue could not, and a disconnected launcher then kept the shared execution gate.
    /// </summary>
    internal const long MaxMemoryBytes = 4L * 1024 * 1024;

    /// <summary>Hard ceiling on unread bytes (memory + spill) before the pump finally applies back-pressure.</summary>
    internal const long MaxQueuedBytes = 4L * 1024 * 1024 * 1024;

    private readonly ChunkQueue _chunks = new(MaxMemoryBytes);
    private readonly CancellationToken _ct;

    internal LauncherStdinFeed(CancellationToken ct) => _ct = ct;

    /// <summary>The cursor the command reads (<c>$global:__BashStdIn</c>).</summary>
    internal StdinCursor CreateCursor() => new(new RecordSource(this)) { IsShellStdin = true };

    /// <summary>
    /// Read stdin frames from <paramref name="stream"/> until the end-of-input marker or the stream closes.
    /// Returns true when the stream closed WITHOUT an end-of-input marker (the launcher is gone).
    /// </summary>
    internal async Task<bool> PumpAsync(Stream stream, CancellationToken stop)
    {
        var reader = new HostProtocol.FrameLineReader(stream);
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(stop).ConfigureAwait(false);
                if (line is null) return true; // peer closed: no more input AND the launcher is gone
                if (!HostProtocol.TryParseStdinFrame(line, out var bytes, out var eof)) continue;
                if (eof) return false;
                if (bytes is not { Length: > 0 }) continue;
                while (_chunks.QueuedBytes >= MaxQueuedBytes)
                    await Task.Delay(10, stop).ConfigureAwait(false);
                _chunks.Add(bytes);
            }
        }
        finally
        {
            _chunks.CompleteAdding();
        }
    }

    /// <summary>
    /// The chunks decoded into line records, one at a time. A wait for the next chunk honours the caller's
    /// token as well as the command's (<see cref="IStdinRecordSource"/>): the pump that feeds a native
    /// program's stdin abandons a silent stdin without taking anything.
    /// </summary>
    private sealed class RecordSource : IStdinRecordSource
    {
        private readonly LauncherStdinFeed _feed;
        private readonly System.Text.Decoder _decoder = RawBytes.Encoding.GetDecoder();
        private readonly StringBuilder _pending = new();
        private readonly Queue<object> _ready = new();
        private char[] _chars = new char[HostProtocol.MaxStdinChunkBytes + 8];
        private bool _ended;

        internal RecordSource(LauncherStdinFeed feed) => _feed = feed;

        public bool TryReadNext(CancellationToken ct, out object? record)
        {
            while (true)
            {
                if (_ready.TryDequeue(out record)) return true;
                if (_ended) return false;

                if (TryTakeChunk(ct, out var chunk)) Decode(chunk);
                else Finish();
            }
        }

        private bool TryTakeChunk(CancellationToken ct, out byte[] chunk)
        {
            chunk = Array.Empty<byte>();
            CancellationTokenSource? linked = null;
            try
            {
                var token = _feed._ct;
                if (ct.CanBeCanceled)
                {
                    linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _feed._ct);
                    token = linked.Token;
                }
                if (!_feed._chunks.TryTake(out var taken, token)) return false;
                chunk = taken!;
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct); // the caller gave up waiting; nothing was taken
            }
            catch (OperationCanceledException) { return false; }  // the command was stopped
            catch (ObjectDisposedException) { return false; }     // the feed is disposed
            finally
            {
                linked?.Dispose();
            }
        }

        private void Decode(byte[] chunk)
        {
            var needed = RawBytes.Encoding.GetMaxCharCount(chunk.Length);
            if (_chars.Length < needed) _chars = new char[needed];
            int n = _decoder.GetChars(chunk, 0, chunk.Length, _chars, 0, flush: false);
            _pending.Append(_chars, 0, n);
            int start = 0;
            for (int i = 0; i < _pending.Length; i++)
            {
                if (_pending[i] != '\n') continue;
                _ready.Enqueue(_pending.ToString(start, i - start));
                start = i + 1;
            }
            if (start > 0) _pending.Remove(0, start);
        }

        private void Finish()
        {
            _ended = true;
            int tail = _decoder.GetChars(Array.Empty<byte>(), 0, 0, _chars, 0, flush: true);
            _pending.Append(_chars, 0, tail);
            if (_pending.Length == 0) return;
            var obj = new PSObject();
            obj.TypeNames.Insert(0, "PsBash.TextOutput");
            obj.Properties.Add(new PSNoteProperty("BashText", _pending.ToString()));
            obj.Properties.Add(new PSNoteProperty("NoTrailingNewline", true));
            _ready.Enqueue(obj);
            _pending.Clear();
        }

        public void Dispose() { }
    }

    public void Dispose()
    {
        _chunks.Dispose();
    }

    /// <summary>
    /// FIFO of byte chunks: up to <c>memoryLimit</c> bytes in memory, further chunks appended to a
    /// delete-on-close temp file while a backlog exists. Order is kept because once anything is spilled
    /// every later chunk spills too, and memory is always drained before the file.
    /// </summary>
    private sealed class ChunkQueue(long memoryLimit) : IDisposable
    {
        private readonly object _gate = new();
        private readonly Queue<byte[]> _mem = new();
        private readonly SemaphoreSlim _items = new(0);
        private FileStream? _spill;
        private long _memBytes, _writePos, _readPos;
        private int _spilled;
        private bool _completed, _disposed;

        internal long QueuedBytes { get { lock (_gate) return _memBytes + (_writePos - _readPos); } }

        internal void Add(byte[] bytes)
        {
            lock (_gate)
            {
                if (_completed || _disposed) return;
                if (_spilled == 0 && _memBytes + bytes.Length <= memoryLimit)
                {
                    _mem.Enqueue(bytes);
                    _memBytes += bytes.Length;
                }
                else
                {
                    _spill ??= new FileStream(Path.Combine(PsBash.Core.Runtime.PsBashRuntimeDirectory.EnsureDirectory(), "stdin-spill-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.ReadWrite,
                        FileShare.None, 1, FileOptions.DeleteOnClose | FileOptions.RandomAccess);
                    var header = BitConverter.GetBytes(bytes.Length);
                    RandomAccess.Write(_spill.SafeFileHandle, header, _writePos);
                    RandomAccess.Write(_spill.SafeFileHandle, bytes, _writePos + header.Length);
                    _writePos += header.Length + bytes.Length;
                    _spilled++;
                }
            }
            _items.Release();
        }

        internal void CompleteAdding()
        {
            lock (_gate)
            {
                if (_completed) return;
                _completed = true;
            }
            _items.Release(); // wakes a waiting taker that finds nothing left: end of input
        }

        /// <summary>False at the end of input; throws <see cref="OperationCanceledException"/> when cancelled.</summary>
        internal bool TryTake(out byte[]? chunk, CancellationToken ct)
        {
            chunk = null;
            while (true)
            {
                _items.Wait(ct);
                lock (_gate)
                {
                    if (_disposed) throw new ObjectDisposedException(nameof(ChunkQueue));
                    if (_mem.Count > 0)
                    {
                        chunk = _mem.Dequeue();
                        _memBytes -= chunk.Length;
                        return true;
                    }
                    if (_spilled > 0)
                    {
                        Span<byte> header = stackalloc byte[4];
                        RandomAccess.Read(_spill!.SafeFileHandle, header, _readPos);
                        chunk = new byte[BitConverter.ToInt32(header)];
                        RandomAccess.Read(_spill.SafeFileHandle, chunk, _readPos + 4);
                        _readPos += 4 + chunk.Length;
                        if (--_spilled == 0) { _spill.SetLength(0); _readPos = _writePos = 0; }
                        return true;
                    }
                }
                _items.Release(); // the completion token: leave it for any other taker
                return false;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _completed = true;
                _mem.Clear();
                _spill?.Dispose();
            }
            _items.Release();
        }
    }
}
