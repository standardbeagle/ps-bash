using System.Text;
using System.Reflection;

namespace PsBash.Core.Runtime.Ipc;

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
                sb.Append(cmd.Body);
                if (!cmd.Body.EndsWith('\n')) sb.Append('\n');
                break;
            case Mode.Stdin stdin:
                sb.Append(ModeHeaderPrefix).Append("Stdin").Append('\n');
                sb.Append(stdin.Body);
                if (!stdin.Body.EndsWith('\n')) sb.Append('\n');
                break;
            case Mode.Script script:
                sb.Append(ModeHeaderPrefix).Append("Script").Append('\n');
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
                return new Mode.Command(await ReadBodyUntilEndAsync(reader, ct).ConfigureAwait(false));
            case "Stdin":
                return new Mode.Stdin(await ReadBodyUntilEndAsync(reader, ct).ConfigureAwait(false));
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
        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false)
                ?? throw new IOException("Request stream closed before END sentinel (Script)");
            if (line == EndSentinel) break;
            if (line.StartsWith(PathHeaderPrefix, StringComparison.Ordinal))
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
        return new Mode.Script(path, DecodeArgv(argvLine), body);
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
    public static async Task WriteResponseLineAsync(Stream stream, string line, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(line);
        var encoded = Convert.ToBase64String(Utf8NoBom.GetBytes(line));
        var bytes = Utf8NoBom.GetBytes(encoded + "\n");
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
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(onLine);
        var reader = new StreamLineReader(stream);
        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false)
                ?? throw new IOException("Response stream closed before EXIT sentinel");
            if (line.StartsWith(ExitPrefix, StringComparison.Ordinal)
                && line.EndsWith(ExitSuffix, StringComparison.Ordinal))
            {
                var code = line[ExitPrefix.Length..^ExitSuffix.Length];
                return int.TryParse(code, out var n) ? n : 1;
            }
            // Data lines are base64-encoded by WriteResponseLineAsync; decode
            // before delivering to the caller. Tolerate undecodable lines
            // (e.g. legacy/raw senders) by passing through verbatim — keeps
            // the reader robust against partial-rollouts.
            string decoded;
            try
            {
                decoded = Utf8NoBom.GetString(Convert.FromBase64String(line));
            }
            catch (FormatException)
            {
                decoded = line;
            }
            onLine(decoded);
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
