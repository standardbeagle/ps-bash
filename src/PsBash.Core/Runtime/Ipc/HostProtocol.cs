using System.Text;
using System.Reflection;

namespace PsBash.Core.Runtime.Ipc;

/// <summary>
/// Stream discriminator for host → launcher response data lines. REFACTOR-4:
/// every host output stream flows through the IPC channel — the launcher routes
/// each frame to the matching launcher stream by this tag. <see cref="Stdout"/>
/// frames are wire-identical to the pre-REFACTOR-4 untagged data line so older
/// readers and existing fixtures round-trip byte-for-byte; <see cref="Stderr"/>
/// frames carry an explicit <c>STDERR:</c> prefix.
/// </summary>
public enum StreamTag
{
    /// <summary>Default. Wire form: bare base64 data line (back-compat with pre-REFACTOR-4).</summary>
    Stdout = 0,

    /// <summary>Wire form: <c>STDERR:</c> + base64 data line. Routed to the launcher's Console.Error.</summary>
    Stderr = 1,
}

/// <summary>
/// Wire-protocol framing for the ps-bash host/launcher IPC channel. Reuses
/// the worker sentinel format byte-for-byte: <c>&lt;&lt;&lt;END&gt;&gt;&gt;</c>
/// terminates a request body and <c>&lt;&lt;&lt;EXIT:N&gt;&gt;&gt;</c> terminates a
/// response stream. A single new <c>MODE:&lt;kind&gt;</c> header line precedes
/// each request so the dispatcher (T05a) can route Command/Stdin/Script/Interactive
/// without reading the body.
/// </summary>
/// <remarks>
/// Encoding: UTF-8, no BOM. Line terminator on the wire is LF (<c>\n</c>);
/// readers tolerate CRLF for cross-platform robustness. Fields that may contain
/// newlines (script path, argv elements, script body) are base64-encoded so
/// every protocol line is a single physical line.
/// </remarks>
public static class HostProtocol
{
    public const int ProtocolVersion = 2;
    public const int HealthStartingExitCode = 75;
    public const string EndSentinel = "<<<END>>>";
    public const string ExitPrefix = "<<<EXIT:";
    public const string ExitSuffix = ">>>";
    public const string ModeHeaderPrefix = "MODE:";
    public const string PathHeaderPrefix = "PATH:";
    public const string ArgvHeaderPrefix = "ARGV:";
    public const string BodyHeaderPrefix = "BODY:";
    public const string DeadlineHeaderPrefix = "DEADLINE:";
    /// <summary>
    /// PTY-4: optional <c>SESSION:Framed</c> / <c>SESSION:Interactive</c> header
    /// emitted between <see cref="ModeHeaderPrefix"/> and the body for
    /// Command/Stdin/Script frames. Absent header decodes to
    /// <see cref="SessionMode.Framed"/> so pre-PTY-4 launchers stay wire-compatible.
    /// </summary>
    public const string SessionHeaderPrefix = "SESSION:";
    /// <summary>
    /// PTY-4 lifecycle sentinel emitted by the host after
    /// <see cref="ExitPrefix"/> when running in <see cref="SessionMode.Interactive"/>.
    /// Signals the launcher that the host runspace is idle and the launcher may
    /// re-take terminal control (restore line-editor cursor, repaint prompt).
    /// In <see cref="SessionMode.Framed"/> the sentinel is NOT emitted (back-compat).
    /// </summary>
    public const string PromptReadySentinel = "<<<PROMPT-READY>>>";

    /// <summary>
    /// REFACTOR-4: prefix marking a response data line as a <see cref="StreamTag.Stderr"/>
    /// frame. The colon is outside the base64 alphabet (<c>[A-Za-z0-9+/=]</c>), so a
    /// <see cref="StreamTag.Stdout"/> frame — a bare base64 line — can never begin with
    /// this prefix. A line without the prefix decodes to <see cref="StreamTag.Stdout"/>,
    /// keeping the wire format back-compatible with pre-REFACTOR-4 launchers.
    /// </summary>
    public const string StderrPrefix = "STDERR:";

    /// <summary>
    /// Default drain deadline for graceful shutdown when the launcher does not
    /// supply an explicit value. Five seconds is long enough for short-running
    /// commands to finish and short enough that an obsolete host does not
    /// block a replacement for an unbounded time.
    /// </summary>
    public const int DefaultShutdownDeadlineMs = 5_000;

    /// <summary>
    /// Response body line written before exit when a host accepts a graceful
    /// shutdown request. Allows the launcher to confirm the request reached a
    /// shutdown-aware host (older hosts respond with a protocol error).
    /// </summary>
    public const string ShutdownAcceptedPayload = "ps-bash-host shutdown=accepted";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string HealthPayload { get; } =
        $"ps-bash-host protocol={ProtocolVersion} build={GetBuildIdentity()}";
    public static string HealthStartingPayload { get; } =
        $"ps-bash-host protocol={ProtocolVersion} status=starting build={GetBuildIdentity()}";

    /// <summary>
    /// Serialize a <see cref="Mode"/> request to <paramref name="stream"/>. The
    /// frame ends with a final <see cref="EndSentinel"/> line. Caller is
    /// responsible for flushing if needed; this method writes and flushes the
    /// underlying writer once.
    /// </summary>
    public static async Task WriteRequestAsync(Stream stream, Mode mode, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(mode);

        var sb = new StringBuilder();
        switch (mode)
        {
            case Mode.Command cmd:
                sb.Append(ModeHeaderPrefix).Append("Command").Append('\n');
                AppendSessionHeader(sb, cmd.Session);
                sb.Append(cmd.Body);
                if (!cmd.Body.EndsWith('\n')) sb.Append('\n');
                break;
            case Mode.Stdin stdin:
                sb.Append(ModeHeaderPrefix).Append("Stdin").Append('\n');
                AppendSessionHeader(sb, stdin.Session);
                sb.Append(stdin.Body);
                if (!stdin.Body.EndsWith('\n')) sb.Append('\n');
                break;
            case Mode.Script script:
                sb.Append(ModeHeaderPrefix).Append("Script").Append('\n');
                AppendSessionHeader(sb, script.Session);
                sb.Append(PathHeaderPrefix).Append(EncodeBase64(script.Path)).Append('\n');
                sb.Append(ArgvHeaderPrefix).Append(EncodeArgv(script.Argv)).Append('\n');
                sb.Append(BodyHeaderPrefix).Append(EncodeBase64(script.Body)).Append('\n');
                break;
            case Mode.Interactive:
                sb.Append(ModeHeaderPrefix).Append("Interactive").Append('\n');
                break;
            case Mode.Health:
                sb.Append(ModeHeaderPrefix).Append("Health").Append('\n');
                break;
            case Mode.Shutdown sd:
                sb.Append(ModeHeaderPrefix).Append("Shutdown").Append('\n');
                sb.Append(DeadlineHeaderPrefix).Append(sd.DeadlineMs.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                break;
            default:
                throw new InvalidOperationException($"Unknown mode: {mode.GetType().Name}");
        }
        sb.Append(EndSentinel).Append('\n');

        var bytes = Utf8NoBom.GetBytes(sb.ToString());
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Read and parse a request frame from <paramref name="stream"/>. Throws
    /// <see cref="IOException"/> if the stream closes before the
    /// <see cref="EndSentinel"/> is observed.
    /// </summary>
    public static async Task<Mode> ReadRequestAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var reader = new StreamLineReader(stream);

        var header = await reader.ReadLineAsync(ct).ConfigureAwait(false)
            ?? throw new IOException("Request stream closed before MODE header");
        if (!header.StartsWith(ModeHeaderPrefix, StringComparison.Ordinal))
            throw new IOException($"Expected '{ModeHeaderPrefix}' header but got: {header}");

        var kind = header[ModeHeaderPrefix.Length..];
        switch (kind)
        {
            case "Command":
                {
                    var (body, session) = await ReadBodyAndSessionAsync(reader, ct).ConfigureAwait(false);
                    return new Mode.Command(body, session);
                }
            case "Stdin":
                {
                    var (body, session) = await ReadBodyAndSessionAsync(reader, ct).ConfigureAwait(false);
                    return new Mode.Stdin(body, session);
                }
            case "Script":
                return await ReadScriptAsync(reader, ct).ConfigureAwait(false);
            case "Interactive":
                {
                    var next = await reader.ReadLineAsync(ct).ConfigureAwait(false)
                        ?? throw new IOException("Request stream closed before END sentinel (Interactive)");
                    if (next != EndSentinel)
                        throw new IOException($"Expected END sentinel after Interactive header, got: {next}");
                    return new Mode.Interactive();
                }
            case "Health":
                {
                    var next = await reader.ReadLineAsync(ct).ConfigureAwait(false)
                        ?? throw new IOException("Request stream closed before END sentinel (Health)");
                    if (next != EndSentinel)
                        throw new IOException($"Expected END sentinel after Health header, got: {next}");
                    return new Mode.Health();
                }
            case "Shutdown":
                return await ReadShutdownAsync(reader, ct).ConfigureAwait(false);
            default:
                throw new IOException($"Unknown MODE kind: {kind}");
        }
    }

    /// <summary>
    /// Emit the PTY-4 <see cref="SessionHeaderPrefix"/> header. Suppressed when
    /// the value is <see cref="SessionMode.Framed"/> so existing wire fixtures
    /// (and pre-PTY-4 launchers) round-trip byte-for-byte. Hosts MUST treat
    /// absent SESSION as <see cref="SessionMode.Framed"/>; only an explicit
    /// <c>SESSION:Interactive</c> opts into the PTY-4 codepath.
    /// </summary>
    private static void AppendSessionHeader(StringBuilder sb, SessionMode session)
    {
        if (session == SessionMode.Framed) return;
        sb.Append(SessionHeaderPrefix).Append(session.ToString()).Append('\n');
    }

    /// <summary>
    /// Read the optional <see cref="SessionHeaderPrefix"/> header. Returns
    /// <see cref="SessionMode.Framed"/> if the first line is not a SESSION
    /// header and pushes the line back into <paramref name="firstBodyLine"/>
    /// for the caller's body loop. Returns the parsed mode if the line WAS a
    /// SESSION header and sets <paramref name="firstBodyLine"/> to null.
    /// </summary>
    private static SessionMode ParseOptionalSessionHeader(string line, out string? firstBodyLine)
    {
        if (line.StartsWith(SessionHeaderPrefix, StringComparison.Ordinal))
        {
            var raw = line[SessionHeaderPrefix.Length..];
            firstBodyLine = null;
            return raw switch
            {
                "Framed" => SessionMode.Framed,
                "Interactive" => SessionMode.Interactive,
                _ => throw new IOException($"Invalid {SessionHeaderPrefix} value: {raw}"),
            };
        }
        firstBodyLine = line;
        return SessionMode.Framed;
    }

    private static async Task<(string Body, SessionMode Session)> ReadBodyAndSessionAsync(StreamLineReader reader, CancellationToken ct)
    {
        var first = await reader.ReadLineAsync(ct).ConfigureAwait(false)
            ?? throw new IOException("Request stream closed before END sentinel");
        var session = ParseOptionalSessionHeader(first, out var carryover);
        var lines = new List<string>();
        if (carryover is not null)
        {
            if (carryover == EndSentinel) return (string.Empty, session);
            lines.Add(carryover);
        }
        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false)
                ?? throw new IOException("Request stream closed before END sentinel");
            if (line == EndSentinel) break;
            lines.Add(line);
        }
        return (string.Join('\n', lines), session);
    }

    private static async Task<string> ReadBodyUntilEndAsync(StreamLineReader reader, CancellationToken ct)
    {
        var lines = new List<string>();
        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false)
                ?? throw new IOException("Request stream closed before END sentinel");
            if (line == EndSentinel) break;
            lines.Add(line);
        }
        return string.Join('\n', lines);
    }

    private static async Task<Mode.Shutdown> ReadShutdownAsync(StreamLineReader reader, CancellationToken ct)
    {
        int? deadlineMs = null;
        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false)
                ?? throw new IOException("Request stream closed before END sentinel (Shutdown)");
            if (line == EndSentinel) break;
            if (line.StartsWith(DeadlineHeaderPrefix, StringComparison.Ordinal))
            {
                var raw = line[DeadlineHeaderPrefix.Length..];
                if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n))
                    throw new IOException($"Invalid {DeadlineHeaderPrefix} value: {raw}");
                deadlineMs = n;
            }
            else
            {
                throw new IOException($"Unexpected line in Shutdown frame: {line}");
            }
        }
        if (deadlineMs is null)
            throw new IOException("Shutdown frame missing DEADLINE field");
        return new Mode.Shutdown(deadlineMs.Value);
    }

    private static async Task<Mode.Script> ReadScriptAsync(StreamLineReader reader, CancellationToken ct)
    {
        string? path = null, argvLine = null, body = null;
        var session = SessionMode.Framed;
        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false)
                ?? throw new IOException("Request stream closed before END sentinel (Script)");
            if (line == EndSentinel) break;
            if (line.StartsWith(SessionHeaderPrefix, StringComparison.Ordinal))
                session = ParseOptionalSessionHeader(line, out _);
            else if (line.StartsWith(PathHeaderPrefix, StringComparison.Ordinal))
                path = DecodeBase64(line[PathHeaderPrefix.Length..]);
            else if (line.StartsWith(ArgvHeaderPrefix, StringComparison.Ordinal))
                argvLine = line[ArgvHeaderPrefix.Length..];
            else if (line.StartsWith(BodyHeaderPrefix, StringComparison.Ordinal))
                body = DecodeBase64(line[BodyHeaderPrefix.Length..]);
            else
                throw new IOException($"Unexpected line in Script frame: {line}");
        }

        if (path is null || argvLine is null || body is null)
            throw new IOException("Script frame missing PATH, ARGV, or BODY field");
        return new Mode.Script(path, DecodeArgv(argvLine), body, session);
    }

    /// <summary>
    /// Serialize a single response line. Caller is responsible for emitting one
    /// call per output line, then a final <see cref="WriteExitAsync"/>.
    /// </summary>
    /// <remarks>
    /// Response data lines are base64-encoded so arbitrary command output
    /// (including bytes that look like the EXIT sentinel, embedded newlines,
    /// or control characters) is safe on the wire. Base64's alphabet
    /// (<c>[A-Za-z0-9+/=]</c>) cannot collide with the <c>&lt;&lt;&lt;EXIT:</c>
    /// prefix, so the response reader can unambiguously distinguish data
    /// lines from the exit sentinel.
    /// </remarks>
    public static Task WriteResponseLineAsync(Stream stream, string line, CancellationToken ct = default)
        => WriteResponseLineAsync(stream, line, StreamTag.Stdout, ct);

    /// <summary>
    /// REFACTOR-4: serialize a single response data line tagged with the host
    /// stream it came from. <see cref="StreamTag.Stdout"/> emits the bare
    /// base64 line (wire-identical to the pre-REFACTOR-4 format);
    /// <see cref="StreamTag.Stderr"/> emits <see cref="StderrPrefix"/> + the
    /// base64 line. The launcher's response reader routes each frame to the
    /// matching launcher stream by inspecting the prefix.
    /// </summary>
    public static async Task WriteResponseLineAsync(Stream stream, string line, StreamTag tag, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(line);
        var encoded = Convert.ToBase64String(Utf8NoBom.GetBytes(line));
        var framed = tag == StreamTag.Stderr ? StderrPrefix + encoded : encoded;
        var bytes = Utf8NoBom.GetBytes(framed + "\n");
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Emit the terminating <c>&lt;&lt;&lt;EXIT:N&gt;&gt;&gt;</c> sentinel and flush.
    /// </summary>
    public static async Task WriteExitAsync(Stream stream, int exitCode, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var bytes = Utf8NoBom.GetBytes($"{ExitPrefix}{exitCode}{ExitSuffix}\n");
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// PTY-4 lifecycle: emit the <see cref="PromptReadySentinel"/> after
    /// <see cref="WriteExitAsync"/> when the host completes a command that ran
    /// in <see cref="SessionMode.Interactive"/>. The launcher consumes this
    /// signal to restore line-editor state and repaint the prompt knowing
    /// command output (which went straight to the PTY slave) has fully landed.
    /// In <see cref="SessionMode.Framed"/> callers MUST NOT emit this sentinel:
    /// pre-PTY-4 launchers' <see cref="ReadResponseAsync"/> would interpret it
    /// as a stray data line.
    /// </summary>
    public static async Task WritePromptReadyAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var bytes = Utf8NoBom.GetBytes(PromptReadySentinel + "\n");
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Read a response from <paramref name="stream"/>, invoking
    /// <paramref name="onLine"/> for each output line, and returning the parsed
    /// exit code from the trailing sentinel. Throws <see cref="IOException"/>
    /// if the stream closes before the EXIT sentinel.
    /// </summary>
    public static async Task<int> ReadResponseAsync(
        Stream stream,
        Action<string> onLine,
        CancellationToken ct = default)
    {
        var (exit, _) = await ReadResponseWithLifecycleAsync(stream, onLine, ct).ConfigureAwait(false);
        return exit;
    }

    /// <summary>
    /// REFACTOR-4: tag-aware read. Like <see cref="ReadResponseAsync(Stream, Action{string}, CancellationToken)"/>
    /// but <paramref name="onLine"/> receives each data line together with the
    /// <see cref="StreamTag"/> it was framed with, so the launcher can route
    /// <see cref="StreamTag.Stdout"/> to Console.Out and
    /// <see cref="StreamTag.Stderr"/> to Console.Error.
    /// </summary>
    public static async Task<int> ReadResponseAsync(
        Stream stream,
        Action<string, StreamTag> onLine,
        CancellationToken ct = default)
    {
        var (exit, _) = await ReadResponseWithLifecycleAsync(stream, onLine, ct).ConfigureAwait(false);
        return exit;
    }

    /// <summary>
    /// PTY-4 lifecycle-aware read. Like <see cref="ReadResponseAsync"/> but also
    /// observes a trailing <see cref="PromptReadySentinel"/> if the host
    /// emits one (interactive sessions). Returns <c>(exitCode, promptReady)</c>
    /// where <c>promptReady</c> is true iff the sentinel was observed. Callers
    /// in framed mode can safely ignore the flag; pre-PTY-4 hosts never emit
    /// the sentinel and this method returns <c>(exitCode, false)</c>.
    /// </summary>
    /// <remarks>
    /// The prompt-ready scan is bounded: after reading <see cref="ExitPrefix"/>
    /// the method does ONE additional non-blocking-ish read for the sentinel
    /// using a short deadline. If the underlying stream blocks past the deadline
    /// the method returns with <c>promptReady = false</c> rather than hanging —
    /// the launcher decides whether the absent sentinel is meaningful. Today
    /// every host (PTY-4 framed mode AND legacy hosts) closes the connection
    /// immediately after EXIT, so the read either returns EOF (legacy) or the
    /// sentinel line (PTY-4 interactive). Both paths exit the loop deterministically.
    /// </remarks>
    public static Task<(int ExitCode, bool PromptReady)> ReadResponseWithLifecycleAsync(
        Stream stream,
        Action<string> onLine,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(onLine);
        // Back-compat callers ignore the stream tag — collapse both streams onto
        // the single delegate. Stdout and stderr both still surface; only the
        // routing distinction is dropped.
        return ReadResponseWithLifecycleAsync(stream, (line, _) => onLine(line), ct);
    }

    /// <summary>
    /// REFACTOR-4: tag-aware lifecycle read. As
    /// <see cref="ReadResponseWithLifecycleAsync(Stream, Action{string}, CancellationToken)"/>
    /// but each data line is delivered with the <see cref="StreamTag"/> it was
    /// framed with so the caller can route stdout and stderr independently.
    /// </summary>
    public static async Task<(int ExitCode, bool PromptReady)> ReadResponseWithLifecycleAsync(
        Stream stream,
        Action<string, StreamTag> onLine,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(onLine);
        var reader = new StreamLineReader(stream);
        int? exitCode = null;
        bool promptReady = false;
        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                if (exitCode is null)
                    throw new IOException("Response stream closed before EXIT sentinel");
                return (exitCode.Value, promptReady);
            }
            if (exitCode is null
                && line.StartsWith(ExitPrefix, StringComparison.Ordinal)
                && line.EndsWith(ExitSuffix, StringComparison.Ordinal))
            {
                var code = line[ExitPrefix.Length..^ExitSuffix.Length];
                exitCode = int.TryParse(code, out var n) ? n : 1;
                continue;
            }
            if (line == PromptReadySentinel)
            {
                promptReady = true;
                // Sentinel arrives after EXIT; once observed we can stop reading.
                // If EXIT hasn't been seen yet, this is a protocol bug — treat as
                // garbled stream and continue scanning for EXIT defensively.
                if (exitCode is not null) return (exitCode.Value, promptReady);
                continue;
            }
            if (exitCode is not null)
            {
                // Unexpected line after EXIT in framed mode would only happen
                // with a malformed host; close out gracefully rather than
                // hanging. Discard the line.
                continue;
            }
            // REFACTOR-4: a data line carrying the StderrPrefix is a stderr
            // frame; the colon in the prefix is outside the base64 alphabet so
            // this can never collide with a bare base64 stdout line.
            var tag = StreamTag.Stdout;
            var payload = line;
            if (line.StartsWith(StderrPrefix, StringComparison.Ordinal))
            {
                tag = StreamTag.Stderr;
                payload = line[StderrPrefix.Length..];
            }
            // Data lines are base64-encoded by WriteResponseLineAsync; decode
            // before delivering to the caller. Tolerate undecodable lines
            // (e.g. legacy/raw senders) by passing through verbatim — keeps
            // the reader robust against partial-rollouts.
            string decoded;
            try
            {
                decoded = Utf8NoBom.GetString(Convert.FromBase64String(payload));
            }
            catch (FormatException)
            {
                decoded = payload;
            }
            onLine(decoded, tag);
        }
    }

    private static string EncodeBase64(string s)
        => Convert.ToBase64String(Utf8NoBom.GetBytes(s));

    private static string DecodeBase64(string s)
        => Utf8NoBom.GetString(Convert.FromBase64String(s));

    /// <summary>
    /// Build-identity string the host advertises in its health payload and
    /// writes into its metadata sidecar. Stable for the lifetime of the
    /// assembly; lets clients detect protocol-compatible-but-build-drifted
    /// hosts as Obsolete.
    /// </summary>
    public static string BuildIdentity { get; } = ResolveBuildIdentity();

    private static string GetBuildIdentity() => BuildIdentity;

    private static string ResolveBuildIdentity()
    {
        var asm = typeof(HostProtocol).Assembly;
        return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString()
            ?? "unknown";
    }

    private static string EncodeArgv(IReadOnlyList<string> argv)
    {
        if (argv.Count == 0) return "";
        var parts = new string[argv.Count];
        for (int i = 0; i < argv.Count; i++) parts[i] = EncodeBase64(argv[i]);
        return string.Join(',', parts);
    }

    private static IReadOnlyList<string> DecodeArgv(string argvLine)
    {
        if (argvLine.Length == 0) return Array.Empty<string>();
        var parts = argvLine.Split(',');
        var result = new string[parts.Length];
        for (int i = 0; i < parts.Length; i++) result[i] = DecodeBase64(parts[i]);
        return result;
    }

    /// <summary>
    /// UTF-8 line reader that tolerates CRLF or LF, reads byte-by-byte (so the
    /// underlying stream isn't over-buffered past a frame boundary), and
    /// returns null at EOF.
    /// </summary>
    private sealed class StreamLineReader
    {
        private const int MaxLineBytes = 1 * 1024 * 1024; // 1 MB — guards against malformed/malicious frames
        private readonly Stream _stream;
        private readonly byte[] _one = new byte[1];

        public StreamLineReader(Stream stream) { _stream = stream; }

        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            var buf = new List<byte>(64);
            while (true)
            {
                int n = await _stream.ReadAsync(_one.AsMemory(), ct).ConfigureAwait(false);
                if (n == 0)
                    return buf.Count == 0 ? null : Utf8NoBom.GetString(buf.ToArray());
                byte b = _one[0];
                if (b == (byte)'\n')
                {
                    if (buf.Count > 0 && buf[^1] == (byte)'\r') buf.RemoveAt(buf.Count - 1);
                    return Utf8NoBom.GetString(buf.ToArray());
                }
                buf.Add(b);
                if (buf.Count > MaxLineBytes)
                    throw new IOException($"IPC line exceeded {MaxLineBytes / 1024} KB limit — possible malformed frame");
            }
        }
    }
}
