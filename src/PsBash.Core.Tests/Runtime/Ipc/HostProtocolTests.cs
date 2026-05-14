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
    public async Task RoundTrip_Health_HeaderAndEndOnly()
    {
        await using var ms = new MemoryStream();
        await HostProtocol.WriteRequestAsync(ms, new Mode.Health());
        var bytes = ms.ToArray();

        Assert.Equal(Utf8NoBom.GetBytes("MODE:Health\n<<<END>>>\n"), bytes);

        ms.Position = 0;
        var decoded = await HostProtocol.ReadRequestAsync(ms);
        Assert.IsType<Mode.Health>(decoded);
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
    public async Task WriteResponseLine_AcceptsExitSentinelLiteral_AndRoundTrips()
    {
        // Regression: response framing must NOT reject output that happens to
        // look like the EXIT sentinel. Real shell commands (e.g.
        // `echo '<<<EXIT:0>>>'`) emit that text verbatim; the prior raw-line
        // framing aborted the connection. Base64 framing makes data and exit
        // sentinels unambiguous on the wire.
        var payload = "<<<EXIT:0>>>";
        await using var ms = new MemoryStream();
        await HostProtocol.WriteResponseLineAsync(ms, payload);
        await HostProtocol.WriteExitAsync(ms, 0);

        ms.Position = 0;
        var lines = new List<string>();
        var code = await HostProtocol.ReadResponseAsync(ms, lines.Add);

        Assert.Equal(0, code);
        Assert.Single(lines);
        Assert.Equal(payload, lines[0]);
    }

    [Fact]
    public async Task WriteResponseLine_AcceptsArbitraryBytes_RoundTrips()
    {
        // Adversarial payloads: embedded newlines, CR, NUL, unicode, EXIT-shaped
        // content interleaved. All must survive the wire intact and exit sentinel
        // must still be recognised at the boundary.
        var payloads = new[]
        {
            "",                                        // empty line
            "plain ASCII",
            "<<<EXIT:42>>>",                           // looks like sentinel
            "<<<EXIT:not-a-number>>>",
            "embedded\nnewline",                       // newline inside one logical line
            "carriage\rreturn",
            "tab\there",
            "unicode: 日本語 🦀",
            "nul-\0-byte",
            "<<<EXIT:0>>>\nfollowed by junk\n<<<EXIT:1>>>",
        };

        await using var ms = new MemoryStream();
        foreach (var p in payloads)
            await HostProtocol.WriteResponseLineAsync(ms, p);
        await HostProtocol.WriteExitAsync(ms, 7);

        ms.Position = 0;
        var collected = new List<string>();
        var code = await HostProtocol.ReadResponseAsync(ms, collected.Add);

        Assert.Equal(7, code);
        Assert.Equal(payloads, collected);
    }

    [Fact]
    public async Task WriteResponseLine_EncodesAsBase64_DoesNotEmitRawPayload()
    {
        // Wire-format check: the raw EXIT sentinel string must NOT appear
        // anywhere in the data-frame bytes, even when the payload contains it.
        // This is the framing invariant that protects ReadResponseAsync.
        var payload = "<<<EXIT:0>>>";
        await using var ms = new MemoryStream();
        await HostProtocol.WriteResponseLineAsync(ms, payload);
        var wire = Utf8NoBom.GetString(ms.ToArray());

        Assert.DoesNotContain(HostProtocol.ExitPrefix, wire);
        // And the line must be a valid base64 payload (no <, >, : in the alphabet).
        var trimmed = wire.TrimEnd('\n');
        Assert.Matches("^[A-Za-z0-9+/=]+$", trimmed);
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

    // -- PTY-4 ----------------------------------------------------------------

    /// <summary>
    /// PTY-4 default: Command without an explicit SessionMode must decode to
    /// <see cref="SessionMode.Framed"/>. Back-compat: pre-PTY-4 launcher fixtures
    /// (which never emit a SESSION header) keep working.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Command_DefaultSessionMode_IsFramed()
    {
        await using var ms = new MemoryStream();
        await HostProtocol.WriteRequestAsync(ms, new Mode.Command("echo hi"));
        ms.Position = 0;
        var decoded = await HostProtocol.ReadRequestAsync(ms);

        var cmd = Assert.IsType<Mode.Command>(decoded);
        Assert.Equal(SessionMode.Framed, cmd.Session);
    }

    /// <summary>
    /// PTY-4 back-compat: when SessionMode is Framed, no SESSION header is
    /// emitted on the wire. Existing wire fixture (protocol-request-c.bin) must
    /// continue to byte-match; the unrelated wire-format check above already
    /// guards that. Here we double-check the absence of "SESSION:" in the bytes.
    /// </summary>
    [Fact]
    public async Task WriteRequest_FramedCommand_DoesNotEmitSessionHeader()
    {
        await using var ms = new MemoryStream();
        await HostProtocol.WriteRequestAsync(ms, new Mode.Command("echo hi", SessionMode.Framed));
        var wire = Utf8NoBom.GetString(ms.ToArray());

        Assert.DoesNotContain(HostProtocol.SessionHeaderPrefix, wire);
        Assert.Equal("MODE:Command\necho hi\n<<<END>>>\n", wire);
    }

    /// <summary>
    /// PTY-4 explicit interactive: when SessionMode=Interactive the wire frame
    /// MUST carry <c>SESSION:Interactive\n</c> immediately after the MODE line.
    /// </summary>
    [Fact]
    public async Task WriteRequest_InteractiveCommand_EmitsSessionHeader()
    {
        await using var ms = new MemoryStream();
        await HostProtocol.WriteRequestAsync(ms, new Mode.Command("vim", SessionMode.Interactive));
        var wire = Utf8NoBom.GetString(ms.ToArray());

        Assert.Equal("MODE:Command\nSESSION:Interactive\nvim\n<<<END>>>\n", wire);
    }

    [Fact]
    public async Task RoundTrip_Command_InteractiveSessionMode_Preserved()
    {
        await using var ms = new MemoryStream();
        await HostProtocol.WriteRequestAsync(ms, new Mode.Command("htop", SessionMode.Interactive));
        ms.Position = 0;
        var decoded = await HostProtocol.ReadRequestAsync(ms);

        var cmd = Assert.IsType<Mode.Command>(decoded);
        Assert.Equal("htop", cmd.Body);
        Assert.Equal(SessionMode.Interactive, cmd.Session);
    }

    [Fact]
    public async Task RoundTrip_Stdin_InteractiveSessionMode_Preserved()
    {
        await using var ms = new MemoryStream();
        await HostProtocol.WriteRequestAsync(ms, new Mode.Stdin("read x\necho $x", SessionMode.Interactive));
        ms.Position = 0;
        var decoded = await HostProtocol.ReadRequestAsync(ms);

        var stdin = Assert.IsType<Mode.Stdin>(decoded);
        Assert.Equal("read x\necho $x", stdin.Body);
        Assert.Equal(SessionMode.Interactive, stdin.Session);
    }

    [Fact]
    public async Task RoundTrip_Script_InteractiveSessionMode_Preserved()
    {
        var path = "/tmp/tui.sh";
        var argv = new[] { "arg1", "arg2" };
        var body = "#!/usr/bin/env bash\nvim\n";
        await using var ms = new MemoryStream();
        await HostProtocol.WriteRequestAsync(ms, new Mode.Script(path, argv, body, SessionMode.Interactive));
        ms.Position = 0;
        var decoded = await HostProtocol.ReadRequestAsync(ms);

        var script = Assert.IsType<Mode.Script>(decoded);
        Assert.Equal(SessionMode.Interactive, script.Session);
        Assert.Equal(path, script.Path);
        Assert.Equal(argv, script.Argv);
        Assert.Equal(body, script.Body);
    }

    /// <summary>
    /// PTY-4 wire forward-compat: a legacy host that did not understand SESSION
    /// would have rejected the unknown line. A PTY-4 host MUST accept any valid
    /// header and ignore unknown SESSION values would be an error path — but
    /// known values "Framed" and "Interactive" round-trip cleanly.
    /// </summary>
    [Fact]
    public async Task ReadRequest_RejectsUnknownSessionValue()
    {
        var bytes = Utf8NoBom.GetBytes("MODE:Command\nSESSION:Garbage\necho hi\n<<<END>>>\n");
        await using var src = new MemoryStream(bytes);
        await Assert.ThrowsAsync<IOException>(() => HostProtocol.ReadRequestAsync(src));
    }

    /// <summary>
    /// QA rubric Directive 7 (negative): a pre-PTY-4 host reading a SESSION
    /// header is impossible (pre-PTY-4 hosts threw on unknown lines), but the
    /// inverse — a PTY-4 host reading a frame without SESSION — must default
    /// to Framed and continue normally. This is the wire-compat test.
    /// </summary>
    [Fact]
    public async Task ReadRequest_LegacyWireFormat_NoSessionHeader_DefaultsToFramed()
    {
        // Exact pre-PTY-4 wire format: MODE then body then END, no SESSION line.
        var legacyWire = "MODE:Command\necho legacy\n<<<END>>>\n";
        var bytes = Utf8NoBom.GetBytes(legacyWire);
        await using var src = new MemoryStream(bytes);
        var decoded = await HostProtocol.ReadRequestAsync(src);

        var cmd = Assert.IsType<Mode.Command>(decoded);
        Assert.Equal("echo legacy", cmd.Body);
        Assert.Equal(SessionMode.Framed, cmd.Session);
    }

    /// <summary>
    /// PTY-4 prompt-ready sentinel: in framed mode the host MUST NOT emit one
    /// (pre-PTY-4 launchers would treat it as data). Reader's lifecycle-aware
    /// API reports promptReady=false when only EXIT is present.
    /// </summary>
    [Fact]
    public async Task ReadResponseWithLifecycle_FramedResponse_PromptReadyFalse()
    {
        await using var ms = new MemoryStream();
        await HostProtocol.WriteResponseLineAsync(ms, "output");
        await HostProtocol.WriteExitAsync(ms, 0);
        ms.Position = 0;

        var lines = new List<string>();
        var (exit, ready) = await HostProtocol.ReadResponseWithLifecycleAsync(ms, lines.Add);

        Assert.Equal(0, exit);
        Assert.False(ready);
        Assert.Equal(new[] { "output" }, lines);
    }

    /// <summary>
    /// PTY-4 prompt-ready sentinel: the lifecycle frame is emitted AFTER EXIT,
    /// and the launcher's lifecycle-aware reader returns promptReady=true.
    /// </summary>
    [Fact]
    public async Task ReadResponseWithLifecycle_InteractiveResponse_PromptReadyTrue()
    {
        await using var ms = new MemoryStream();
        // Interactive mode: no data lines on the IPC (they went to PTY slave).
        await HostProtocol.WriteExitAsync(ms, 0);
        await HostProtocol.WritePromptReadyAsync(ms);
        ms.Position = 0;

        var lines = new List<string>();
        var (exit, ready) = await HostProtocol.ReadResponseWithLifecycleAsync(ms, lines.Add);

        Assert.Equal(0, exit);
        Assert.True(ready);
        Assert.Empty(lines);
    }

    /// <summary>
    /// PTY-4 prompt-ready exact wire format: <c>&lt;&lt;&lt;PROMPT-READY&gt;&gt;&gt;\n</c>.
    /// Pinning the bytes so a future refactor that changes the sentinel breaks
    /// loudly rather than silently (launchers would then ignore the lifecycle cue).
    /// </summary>
    [Fact]
    public async Task WritePromptReady_EmitsExpectedWireBytes()
    {
        await using var ms = new MemoryStream();
        await HostProtocol.WritePromptReadyAsync(ms);
        var wire = Utf8NoBom.GetString(ms.ToArray());
        Assert.Equal("<<<PROMPT-READY>>>\n", wire);
        Assert.Equal("<<<PROMPT-READY>>>", HostProtocol.PromptReadySentinel);
    }

    /// <summary>
    /// PTY-4 back-compat: the existing <see cref="HostProtocol.ReadResponseAsync"/>
    /// (used by every pre-PTY-4 caller) must remain wire-compatible with a
    /// host that emits PROMPT-READY. It SHOULD ignore the sentinel rather
    /// than throw or surface it as a data line.
    /// </summary>
    [Fact]
    public async Task ReadResponseAsync_TolerantOfPromptReadySentinel()
    {
        await using var ms = new MemoryStream();
        await HostProtocol.WriteResponseLineAsync(ms, "hello");
        await HostProtocol.WriteExitAsync(ms, 0);
        await HostProtocol.WritePromptReadyAsync(ms);
        ms.Position = 0;

        var lines = new List<string>();
        var exit = await HostProtocol.ReadResponseAsync(ms, lines.Add);

        Assert.Equal(0, exit);
        // The lifecycle sentinel must NOT show up as a fake "data" line —
        // base64-decoding "<<<PROMPT-READY>>>" yields garbage; the legacy
        // reader's FormatException catch-block would otherwise surface it.
        Assert.Equal(new[] { "hello" }, lines);
    }

    // -----------------------------------------------------------------------
    // REFACTOR-4: stream-tagged response frames. The RC-1 regression — host
    // stderr silently lost because it travelled the host's detached fd 2
    // instead of the IPC channel — is closed by tagging every response data
    // line with its source stream and routing by tag on the launcher side.
    // -----------------------------------------------------------------------

    /// <summary>
    /// A STDERR-tagged frame is emitted with the <c>STDERR:</c> prefix; a
    /// STDOUT frame stays a bare base64 line (wire-identical to pre-REFACTOR-4).
    /// </summary>
    [Fact]
    public async Task WriteResponseLine_StderrTag_EmitsStderrPrefix()
    {
        await using var ms = new MemoryStream();
        await HostProtocol.WriteResponseLineAsync(ms, "boom", StreamTag.Stderr);
        await HostProtocol.WriteResponseLineAsync(ms, "fine", StreamTag.Stdout);

        var wire = Utf8NoBom.GetString(ms.ToArray());
        var wireLines = wire.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, wireLines.Length);
        Assert.StartsWith("STDERR:", wireLines[0]);
        Assert.DoesNotContain(":", wireLines[1]); // bare base64, no prefix
        Assert.Equal("STDERR:", HostProtocol.StderrPrefix);
    }

    /// <summary>
    /// RC-1 regression: a stderr line survives a full WriteResponseLine →
    /// ReadResponseAsync round-trip and arrives tagged <see cref="StreamTag.Stderr"/>,
    /// while a stdout line on the same stream arrives tagged
    /// <see cref="StreamTag.Stdout"/>. Routing by this tag is what lets the
    /// launcher deliver stderr to its Console.Error.
    /// </summary>
    [Fact]
    public async Task ReadResponse_StreamTagged_RoutesStderrAndStdoutSeparately()
    {
        await using var ms = new MemoryStream();
        await HostProtocol.WriteResponseLineAsync(ms, "out-1", StreamTag.Stdout);
        await HostProtocol.WriteResponseLineAsync(ms, "err-1", StreamTag.Stderr);
        await HostProtocol.WriteResponseLineAsync(ms, "out-2", StreamTag.Stdout);
        await HostProtocol.WriteExitAsync(ms, 0);
        ms.Position = 0;

        var stdout = new List<string>();
        var stderr = new List<string>();
        var exit = await HostProtocol.ReadResponseAsync(
            ms,
            (line, tag) =>
            {
                if (tag == StreamTag.Stderr) stderr.Add(line);
                else stdout.Add(line);
            });

        Assert.Equal(0, exit);
        Assert.Equal(new[] { "out-1", "out-2" }, stdout);
        Assert.Equal(new[] { "err-1" }, stderr);
    }

    /// <summary>
    /// A stderr payload containing characters that look like protocol
    /// sentinels (<c>&lt;&lt;&lt;EXIT:0&gt;&gt;&gt;</c>, colons, newlines) still
    /// round-trips intact — the base64 body insulates the wire from the
    /// payload, the STDERR prefix only wraps it.
    /// </summary>
    [Fact]
    public async Task ReadResponse_StderrPayloadLooksLikeSentinel_RoundTripsIntact()
    {
        await using var ms = new MemoryStream();
        var nasty = "<<<EXIT:0>>>\nSTDERR:not-a-prefix: still data";
        await HostProtocol.WriteResponseLineAsync(ms, nasty, StreamTag.Stderr);
        await HostProtocol.WriteExitAsync(ms, 3);
        ms.Position = 0;

        var stdout = new List<string>();
        var stderr = new List<string>();
        var exit = await HostProtocol.ReadResponseAsync(
            ms,
            (line, tag) =>
            {
                if (tag == StreamTag.Stderr) stderr.Add(line);
                else stdout.Add(line);
            });

        Assert.Equal(3, exit);
        Assert.Empty(stdout);
        Assert.Equal(new[] { nasty }, stderr);
    }

    /// <summary>
    /// Back-compat: the legacy <see cref="HostProtocol.ReadResponseAsync(System.IO.Stream, System.Action{string}, System.Threading.CancellationToken)"/>
    /// overload (Action&lt;string&gt;, no tag) still surfaces BOTH streams — it
    /// collapses stdout and stderr onto the one delegate rather than dropping
    /// stderr. Pre-REFACTOR-4 callers (health/shutdown probes) keep working.
    /// </summary>
    [Fact]
    public async Task ReadResponse_LegacyOverload_StillSurfacesStderrLines()
    {
        await using var ms = new MemoryStream();
        await HostProtocol.WriteResponseLineAsync(ms, "out", StreamTag.Stdout);
        await HostProtocol.WriteResponseLineAsync(ms, "err", StreamTag.Stderr);
        await HostProtocol.WriteExitAsync(ms, 0);
        ms.Position = 0;

        var lines = new List<string>();
        var exit = await HostProtocol.ReadResponseAsync(ms, lines.Add);

        Assert.Equal(0, exit);
        Assert.Equal(new[] { "out", "err" }, lines);
    }

    /// <summary>
    /// Back-compat: a pre-REFACTOR-4 host emits only bare (untagged) base64
    /// data lines. The tag-aware reader must decode every one of them as
    /// <see cref="StreamTag.Stdout"/> — never mis-classify a legacy line.
    /// </summary>
    [Fact]
    public async Task ReadResponse_LegacyUntaggedFrames_AllDecodeAsStdout()
    {
        await using var ms = new MemoryStream();
        // Pre-REFACTOR-4 wire form: the default overload, bare base64 lines.
        await HostProtocol.WriteResponseLineAsync(ms, "legacy-1");
        await HostProtocol.WriteResponseLineAsync(ms, "legacy-2");
        await HostProtocol.WriteExitAsync(ms, 0);
        ms.Position = 0;

        var stdout = new List<string>();
        var stderr = new List<string>();
        var exit = await HostProtocol.ReadResponseAsync(
            ms,
            (line, tag) =>
            {
                if (tag == StreamTag.Stderr) stderr.Add(line);
                else stdout.Add(line);
            });

        Assert.Equal(0, exit);
        Assert.Equal(new[] { "legacy-1", "legacy-2" }, stdout);
        Assert.Empty(stderr);
    }
}
