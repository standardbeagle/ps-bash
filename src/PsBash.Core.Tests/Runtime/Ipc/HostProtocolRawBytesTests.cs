using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Runtime.Ipc;

/// <summary>
/// The IPC frames carry text as escaped-byte-marker strings (<see cref="RawBytes"/>): an invalid UTF-8 byte
/// travels as that single byte (base64 of the original bytes), not as U+FFFD (EF BF BD).
/// </summary>
public class HostProtocolRawBytesTests
{
    private static string Marked(params byte[] bytes) => RawBytes.GetString(bytes);

    private static byte[] FirstFramePayload(byte[] wire)
    {
        var line = Encoding.ASCII.GetString(wire).Split('\n')[0];
        if (line.StartsWith(HostProtocol.StderrPrefix, StringComparison.Ordinal))
            line = line[HostProtocol.StderrPrefix.Length..];
        return Convert.FromBase64String(line);
    }

    [Fact]
    public async Task ResponseLine_WithInvalidByte_PutsTheSingleByteOnTheWire()
    {
        await using var ms = new MemoryStream();
        await HostProtocol.WriteResponseLineAsync(ms, Marked(0x61, 0xE9, 0x62));
        Assert.Equal(new byte[] { 0x61, 0xE9, 0x62 }, FirstFramePayload(ms.ToArray()));
    }

    [Fact]
    public async Task ResponseLine_RoundTripsAnyBytes_StdoutAndStderr()
    {
        var all = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var text = Marked(all);
        await using var ms = new MemoryStream();
        await HostProtocol.WriteResponseLineAsync(ms, text, StreamTag.Stdout);
        await HostProtocol.WriteResponseLineAsync(ms, text, StreamTag.Stderr);
        await HostProtocol.WriteExitAsync(ms, 0);

        ms.Position = 0;
        var got = new List<(string Text, StreamTag Tag)>();
        await HostProtocol.ReadResponseAsync(ms, (s, tag) => got.Add((s, tag)));

        Assert.Equal(2, got.Count);
        Assert.All(got, g => Assert.Equal(all, RawBytes.GetBytes(g.Text)));
        Assert.Equal(StreamTag.Stdout, got[0].Tag);
        Assert.Equal(StreamTag.Stderr, got[1].Tag);
    }

    [Fact]
    public async Task ResponseLine_LargerThanOneFrame_KeepsEveryByteAcrossChunks()
    {
        // Past MaxResponseChunkChars the payload is split into several frames; each chunk is encoded on its own,
        // so a marker run and valid multi-byte characters straddling the cut must still concatenate to the
        // original bytes.
        var rng = new Random(5);
        var bytes = new byte[HostProtocol.MaxResponseChunkChars * 2 + 777];
        rng.NextBytes(bytes);
        var text = Marked(bytes);

        await using var ms = new MemoryStream();
        await HostProtocol.WriteResponseLineAsync(ms, text);
        await HostProtocol.WriteExitAsync(ms, 0);
        ms.Position = 0;
        var sb = new StringBuilder();
        await HostProtocol.ReadResponseAsync(ms, s => sb.Append(s));

        Assert.Equal(bytes, RawBytes.GetBytes(sb.ToString()));
    }

    [Fact]
    public async Task CommandBody_WithInvalidByte_RoundTripsThroughTheRequestFrame()
    {
        var body = "printf '%s' '" + Marked(0xE9, 0xFF) + "'";
        await using var ms = new MemoryStream();
        await HostProtocol.WriteRequestAsync(ms, new Mode.Command(body));
        // The single bytes E9 FF are on the wire (not EF BF BD).
        var raw = ms.ToArray();
        Assert.True(raw.AsSpan().IndexOf(new byte[] { 0xE9, 0xFF }) >= 0);
        Assert.False(raw.AsSpan().IndexOf(new byte[] { 0xEF, 0xBF, 0xBD }) >= 0);

        ms.Position = 0;
        var decoded = Assert.IsType<Mode.Command>(await HostProtocol.ReadRequestAsync(ms));
        Assert.Equal(body, decoded.Body);
    }

    [Fact]
    public async Task ScriptFrame_WithInvalidByteInBody_RoundTrips()
    {
        var body = "echo " + Marked(0x80, 0xC3, 0x28);
        await using var ms = new MemoryStream();
        await HostProtocol.WriteRequestAsync(ms, new Mode.Script("s.sh", Array.Empty<string>(), body));
        ms.Position = 0;
        var decoded = Assert.IsType<Mode.Script>(await HostProtocol.ReadRequestAsync(ms));
        Assert.Equal(body, decoded.Body);
    }
}
