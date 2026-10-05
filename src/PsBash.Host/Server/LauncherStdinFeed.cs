using System.Collections.Concurrent;
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
/// The queue is bounded: when the command is not reading, the pump stops pulling frames off the socket
/// (back-pressure reaches the launcher, which stops reading its own stdin) instead of buffering an
/// unbounded pipe. A command that never reads stdin therefore never consumes any of it.
/// </para>
/// </summary>
internal sealed class LauncherStdinFeed : IDisposable
{
    private const int ChunkCapacity = 64;

    private readonly BlockingCollection<byte[]> _chunks = new(ChunkCapacity);
    private readonly CancellationToken _ct;

    internal LauncherStdinFeed(CancellationToken ct) => _ct = ct;

    /// <summary>The cursor the command reads (<c>$global:__BashStdIn</c>).</summary>
    internal StdinCursor CreateCursor() => new(new RecordSource(this));

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
                if (bytes is { Length: > 0 })
                    _chunks.Add(bytes, stop);
            }
        }
        finally
        {
            try { _chunks.CompleteAdding(); } catch (ObjectDisposedException) { }
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
                if (!_feed._chunks.TryTake(out var taken, Timeout.Infinite, token)) return false;
                chunk = taken!;
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct); // the caller gave up waiting; nothing was taken
            }
            catch (OperationCanceledException) { return false; }  // the command was stopped
            catch (InvalidOperationException) { return false; }   // completed and drained, or the feed is disposed
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
        try { _chunks.CompleteAdding(); } catch (ObjectDisposedException) { }
        _chunks.Dispose();
    }
}
