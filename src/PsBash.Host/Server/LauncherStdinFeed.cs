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
    internal StdinCursor CreateCursor() => new(Records());

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

    private IEnumerable<object> Records()
    {
        var decoder = RawBytes.Encoding.GetDecoder();
        var pending = new StringBuilder();
        var chars = new char[HostProtocol.MaxStdinChunkBytes + 8];

        IEnumerable<byte[]> Chunks()
        {
            while (true)
            {
                byte[]? chunk = null;
                bool got;
                try { got = _chunks.TryTake(out chunk, Timeout.Infinite, _ct); }
                catch (InvalidOperationException) { got = false; }   // completed and drained
                catch (OperationCanceledException) { got = false; }  // the command was stopped
                if (!got) yield break;
                yield return chunk!;
            }
        }

        foreach (var chunk in Chunks())
        {
            var needed = RawBytes.Encoding.GetMaxCharCount(chunk.Length);
            if (chars.Length < needed) chars = new char[needed];
            int n = decoder.GetChars(chunk, 0, chunk.Length, chars, 0, flush: false);
            pending.Append(chars, 0, n);
            int start = 0;
            for (int i = 0; i < pending.Length; i++)
            {
                if (pending[i] != '\n') continue;
                yield return pending.ToString(start, i - start);
                start = i + 1;
            }
            if (start > 0) pending.Remove(0, start);
        }

        int tail = decoder.GetChars(Array.Empty<byte>(), 0, 0, chars, 0, flush: true);
        pending.Append(chars, 0, tail);
        if (pending.Length > 0)
        {
            var obj = new PSObject();
            obj.TypeNames.Insert(0, "PsBash.TextOutput");
            obj.Properties.Add(new PSNoteProperty("BashText", pending.ToString()));
            obj.Properties.Add(new PSNoteProperty("NoTrailingNewline", true));
            yield return obj;
        }
    }

    public void Dispose()
    {
        try { _chunks.CompleteAdding(); } catch (ObjectDisposedException) { }
        _chunks.Dispose();
    }
}
