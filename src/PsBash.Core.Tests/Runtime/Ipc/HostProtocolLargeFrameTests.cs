using System.Text;
using PsBash.Core.Runtime;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Runtime.Ipc;

/// <summary>
/// R04 (task 01M37WN0F4QPXGZMBH51CVHYVE): a single output line larger than the
/// reader's <c>MaxLineBytes</c> cap used to be written as ONE unbounded frame.
/// Base64 grows the payload by 4/3, so a &gt;768 KB line crossed the 1 MB cap, the
/// reader threw an <see cref="IOException"/>, and <see cref="IpcWorker"/> retried
/// it as a transport reset — re-running the command (side effect twice) and
/// exiting 125 with no output.
///
/// <para>The fix splits an oversized payload into multiple frames below the cap,
/// the reader reassembles them byte-for-byte, and a genuine oversize protocol
/// error is a distinct, non-retryable exception type.</para>
/// </summary>
public class HostProtocolLargeFrameTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The reader's hard per-line cap (HostProtocol.StreamLineReader.MaxLineBytes).</summary>
    private const int MaxLineBytes = 1 * 1024 * 1024;

    /// <summary>Split a raw wire stream into physical LF-terminated frames.</summary>
    private static List<byte[]> SplitFrames(byte[] wire)
    {
        var frames = new List<byte[]>();
        int start = 0;
        for (int i = 0; i < wire.Length; i++)
        {
            if (wire[i] == (byte)'\n')
            {
                frames.Add(wire[start..i]);
                start = i + 1;
            }
        }
        if (start < wire.Length) frames.Add(wire[start..]);
        return frames;
    }

    [Fact]
    public async Task WriteResponseLine_10MbSingleLine_RoundTripsByteExact()
    {
        // Exactly the confirmed repro shape: one ~10 MB output line (no interior
        // newline until the trailing one). Must survive the wire byte-for-byte.
        var payload = new string('0', 10 * 1024 * 1024) + "\n";

        await using var ms = new MemoryStream();
        await HostProtocol.WriteResponseLineAsync(ms, payload);
        await HostProtocol.WriteExitAsync(ms, 0);

        ms.Position = 0;
        var sb = new StringBuilder();
        var exit = await HostProtocol.ReadResponseAsync(ms, s => sb.Append(s));

        Assert.Equal(0, exit);
        Assert.Equal(payload.Length, sb.Length);
        Assert.Equal(payload, sb.ToString());
    }

    [Fact]
    public async Task WriteResponseLine_10MbSingleLine_NoFrameExceedsTheCap()
    {
        // The fix's mechanism: no physical frame may reach the reader's hard cap,
        // or a large line reproduces the original IOException/retry failure.
        var payload = new string('0', 10 * 1024 * 1024) + "\n";

        await using var ms = new MemoryStream();
        await HostProtocol.WriteResponseLineAsync(ms, payload);
        await HostProtocol.WriteExitAsync(ms, 0);

        foreach (var frame in SplitFrames(ms.ToArray()))
        {
            Assert.True(frame.Length < MaxLineBytes,
                $"a frame of {frame.Length} bytes reached the {MaxLineBytes}-byte reader cap");
        }
    }

    [Fact]
    public async Task WriteResponseLine_StderrTag_LargePayload_RoundTripsByteExact()
    {
        // stderr is unbatched, so a large single stderr line takes the same wire
        // path; every chunk must keep the STDERR: prefix and reassemble intact.
        var payload = new string('e', 3 * 1024 * 1024);

        await using var ms = new MemoryStream();
        await HostProtocol.WriteResponseLineAsync(ms, payload, StreamTag.Stderr);
        await HostProtocol.WriteExitAsync(ms, 1);

        ms.Position = 0;
        var stderr = new StringBuilder();
        var stdout = new StringBuilder();
        var exit = await HostProtocol.ReadResponseAsync(
            ms,
            (line, tag) => (tag == StreamTag.Stderr ? stderr : stdout).Append(line));

        Assert.Equal(1, exit);
        Assert.Equal(payload, stderr.ToString());
        Assert.Empty(stdout.ToString());
    }

    [Fact]
    public async Task ReadResponse_OversizedRawFrame_IsNonRetryableProtocolError()
    {
        // A malformed/foreign host that still emits an oversized frame must be a
        // distinct protocol error, NEVER a retryable transport reset (which would
        // double-execute a no-output side-effecting command).
        var huge = new string('A', MaxLineBytes + 64);
        var bytes = Utf8NoBom.GetBytes(huge + "\n" + HostProtocol.ExitPrefix + "0" + HostProtocol.ExitSuffix + "\n");

        await using var ms = new MemoryStream(bytes);
        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => HostProtocol.ReadResponseAsync(ms, _ => { }));

        Assert.False(IpcWorker.IsTransportReset(ex),
            $"an oversized frame must not be a retryable transport reset; got {ex.GetType().FullName}");
    }

    [Fact]
    public async Task ReadResponse_LargeLine_ReadsInBufferedChunksNotPerByte()
    {
        // The reader used to issue one async socket read per byte (and allocate a
        // list per line). A half-megabyte line must now take a handful of reads.
        var payload = new string('x', 512 * 1024) + "\n";

        await using var ms = new MemoryStream();
        await HostProtocol.WriteResponseLineAsync(ms, payload);
        await HostProtocol.WriteExitAsync(ms, 0);

        await using var counting = new CountingReadStream(ms.ToArray());
        var sb = new StringBuilder();
        var exit = await HostProtocol.ReadResponseAsync(counting, s => sb.Append(s));

        Assert.Equal(0, exit);
        Assert.Equal(payload, sb.ToString());
        Assert.True(counting.ReadCalls < 4096,
            $"reader issued {counting.ReadCalls} reads for a {payload.Length}-char line; expected buffered reads");
    }

    /// <summary>A stream that counts how many times the reader pulls bytes from it.</summary>
    private sealed class CountingReadStream : MemoryStream
    {
        public int ReadCalls;

        public CountingReadStream(byte[] data) : base(data) { }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}

