using System.Text;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Runtime.Ipc;

public class HostProtocolTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static string FixturePath(string name)
    {
        var baseDir = AppContext.BaseDirectory;
        return Path.Combine(baseDir, "fixtures", name);
    }

    [Fact]
    public async Task WriteRequest_Command_MatchesFixtureByteForByte()
    {
        // RED step: byte-exact match against checked-in fixture.
        var expected = await File.ReadAllBytesAsync(FixturePath("protocol-request-c.bin"));

        await using var ms = new MemoryStream();
        await HostProtocol.WriteRequestAsync(ms, new Mode.Command("echo hello"));
        var actual = ms.ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task RoundTrip_Command_PreservesBody()
    {
        await using var ms = new MemoryStream();
        await HostProtocol.WriteRequestAsync(ms, new Mode.Command("echo hello"));
        ms.Position = 0;
        var decoded = await HostProtocol.ReadRequestAsync(ms);

        var cmd = Assert.IsType<Mode.Command>(decoded);
        Assert.Equal("echo hello", cmd.Body);
    }

    [Fact]
    public async Task RoundTrip_Stdin_MultilineBody()
    {
        var body = "line1\nline2\nline3";
        await using var ms = new MemoryStream();
        await HostProtocol.WriteRequestAsync(ms, new Mode.Stdin(body));
        ms.Position = 0;
        var decoded = await HostProtocol.ReadRequestAsync(ms);

        var stdin = Assert.IsType<Mode.Stdin>(decoded);
        Assert.Equal(body, stdin.Body);
    }

    [Fact]
    public async Task RoundTrip_Script_ArgvWithNewlinesAndQuotes()
    {
        // Argv with newlines, quotes, commas — base64 envelope must keep one line per field.
        var path = "/tmp/my\nscript.sh";
        var argv = new[] { "first arg", "with \"quote\"", "and\nnewline", "with,comma" };
        var body = "#!/usr/bin/env bash\necho \"$1\" \"$2\"\n";

        await using var ms = new MemoryStream();
        await HostProtocol.WriteRequestAsync(ms, new Mode.Script(path, argv, body));
        ms.Position = 0;
        var decoded = await HostProtocol.ReadRequestAsync(ms);

        var script = Assert.IsType<Mode.Script>(decoded);
        Assert.Equal(path, script.Path);
        Assert.Equal(argv, script.Argv);
        Assert.Equal(body, script.Body);
    }

    [Fact]
    public async Task RoundTrip_Script_EmptyArgv()
    {
        await using var ms = new MemoryStream();
        await HostProtocol.WriteRequestAsync(ms, new Mode.Script("/tmp/x.sh", Array.Empty<string>(), "echo hi"));
        ms.Position = 0;
        var decoded = await HostProtocol.ReadRequestAsync(ms);

        var script = Assert.IsType<Mode.Script>(decoded);
        Assert.Empty(script.Argv);
        Assert.Equal("/tmp/x.sh", script.Path);
    }

    [Fact]
    public async Task RoundTrip_Interactive_HeaderAndEndOnly()
    {
        await using var ms = new MemoryStream();
        await HostProtocol.WriteRequestAsync(ms, new Mode.Interactive());
        var bytes = ms.ToArray();

        // Wire format: "MODE:Interactive\n<<<END>>>\n" — exactly 27 bytes, no body.
        Assert.Equal(Utf8NoBom.GetBytes("MODE:Interactive\n<<<END>>>\n"), bytes);

        ms.Position = 0;
        var decoded = await HostProtocol.ReadRequestAsync(ms);
        Assert.IsType<Mode.Interactive>(decoded);
    }

    [Fact]
    public async Task ReadRequest_TruncatedBeforeEnd_ThrowsIOException()
    {
        // Write a request, then truncate before the END sentinel.
        await using var ms = new MemoryStream();
        await HostProtocol.WriteRequestAsync(ms, new Mode.Command("echo hi"));
        var bytes = ms.ToArray();
        // Cut off the trailing "<<<END>>>\n" (10 bytes) and a couple more so EOF lands mid-body.
        var truncated = bytes[..(bytes.Length - 12)];

        await using var src = new MemoryStream(truncated);
        await Assert.ThrowsAsync<IOException>(() => HostProtocol.ReadRequestAsync(src));
    }

    [Fact]
    public async Task ReadRequest_EmptyStream_ThrowsIOException()
    {
        await using var src = new MemoryStream(Array.Empty<byte>());
        await Assert.ThrowsAsync<IOException>(() => HostProtocol.ReadRequestAsync(src));
    }

    [Fact]
    public async Task ReadRequest_UnknownMode_ThrowsIOException()
    {
        var bytes = Utf8NoBom.GetBytes("MODE:Garbage\n<<<END>>>\n");
        await using var src = new MemoryStream(bytes);
        await Assert.ThrowsAsync<IOException>(() => HostProtocol.ReadRequestAsync(src));
    }

    [Fact]
    public async Task ReadRequest_MissingModeHeader_ThrowsIOException()
    {
        var bytes = Utf8NoBom.GetBytes("just a line\n<<<END>>>\n");
        await using var src = new MemoryStream(bytes);
        await Assert.ThrowsAsync<IOException>(() => HostProtocol.ReadRequestAsync(src));
    }

    [Fact]
    public async Task WriteAndReadResponse_MultipleLinesWithExit0()
    {
        await using var ms = new MemoryStream();
        await HostProtocol.WriteResponseLineAsync(ms, "first");
        await HostProtocol.WriteResponseLineAsync(ms, "second");
        await HostProtocol.WriteResponseLineAsync(ms, "third");
        await HostProtocol.WriteExitAsync(ms, 0);

        ms.Position = 0;
        var collected = new List<string>();
        var exitCode = await HostProtocol.ReadResponseAsync(ms, collected.Add);

        Assert.Equal(0, exitCode);
        Assert.Equal(new[] { "first", "second", "third" }, collected);
    }

    [Fact]
    public async Task ReadResponse_NonZeroExit_PropagatesCode()
    {
        await using var ms = new MemoryStream();
        await HostProtocol.WriteResponseLineAsync(ms, "error: boom");
        await HostProtocol.WriteExitAsync(ms, 42);

        ms.Position = 0;
        var lines = new List<string>();
        var code = await HostProtocol.ReadResponseAsync(ms, lines.Add);

        Assert.Equal(42, code);
        Assert.Single(lines, "error: boom");
    }

    [Fact]
    public async Task ReadResponse_EmptyOutputThenExit_ReturnsCode()
    {
        await using var ms = new MemoryStream();
        await HostProtocol.WriteExitAsync(ms, 7);
        ms.Position = 0;

        var lines = new List<string>();
        var code = await HostProtocol.ReadResponseAsync(ms, lines.Add);

        Assert.Equal(7, code);
        Assert.Empty(lines);
    }

    [Fact]
    public async Task ReadResponse_StreamClosedBeforeExit_ThrowsIOException()
    {
        await using var ms = new MemoryStream();
        await HostProtocol.WriteResponseLineAsync(ms, "partial");
        ms.Position = 0;

        await Assert.ThrowsAsync<IOException>(
            () => HostProtocol.ReadResponseAsync(ms, _ => { }));
    }

    [Fact]
    public async Task WriteResponseLine_RejectsExitSentinel()
    {
        await using var ms = new MemoryStream();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => HostProtocol.WriteResponseLineAsync(ms, "<<<EXIT:0>>>"));
    }

    [Fact]
    public async Task ReadRequest_ToleratesCRLFLineEndings()
    {
        // Some platforms or stream wrappers emit CRLF. Reader must accept both.
        var bytes = Utf8NoBom.GetBytes("MODE:Command\r\necho ok\r\n<<<END>>>\r\n");
        await using var src = new MemoryStream(bytes);
        var decoded = await HostProtocol.ReadRequestAsync(src);
        var cmd = Assert.IsType<Mode.Command>(decoded);
        Assert.Equal("echo ok", cmd.Body);
    }

    [Fact]
    public async Task WriteRequest_Command_BodyAlreadyEndingInNewline_NoDoubleNewline()
    {
        await using var ms = new MemoryStream();
        await HostProtocol.WriteRequestAsync(ms, new Mode.Command("echo hello\n"));
        var text = Utf8NoBom.GetString(ms.ToArray());
        Assert.Equal("MODE:Command\necho hello\n<<<END>>>\n", text);
    }
}
