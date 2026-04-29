namespace PsBash.Core.Runtime.Ipc;

/// <summary>
/// Discriminated union of host-protocol request modes.
/// </summary>
/// <remarks>
/// Phase-1 protocol: the launcher opens one connection per request and sends a
/// single <see cref="Mode"/> as a <c>MODE:&lt;kind&gt;</c> header line followed
/// by mode-specific body lines and a final <c>&lt;&lt;&lt;END&gt;&gt;&gt;</c>
/// terminator. The host writes zero or more output lines then a single
/// <c>&lt;&lt;&lt;EXIT:N&gt;&gt;&gt;</c> sentinel. Sentinels are reused byte-for-byte
/// from <see cref="PwshWorker"/> so the in-process and cross-process workers
/// share one framing contract.
/// Cancellation in phase-1 is signalled by closing the connection (no message).
/// </remarks>
public abstract record Mode
{
    private Mode() { }

    /// <summary>
    /// One-shot bash command string evaluated against the host's shared session.
    /// Equivalent to <c>ps-bash -c "..."</c>.
    /// </summary>
    public sealed record Command(string Body) : Mode;

    /// <summary>
    /// Bash script body read from launcher's stdin, evaluated as a sequence of
    /// commands. Equivalent to <c>echo "..." | ps-bash</c>.
    /// </summary>
    public sealed record Stdin(string Body) : Mode;

    /// <summary>
    /// Script-file invocation. <paramref name="Path"/> is the absolute script
    /// path on the launcher's filesystem (informational; the body has already
    /// been read by the launcher), <paramref name="Argv"/> is the positional
    /// argument vector ($1..$N), and <paramref name="Body"/> is the full script
    /// contents. Path and argv elements may contain newlines and quote
    /// characters — they are encoded base64 on the wire.
    /// </summary>
    public sealed record Script(string Path, IReadOnlyList<string> Argv, string Body) : Mode;

    /// <summary>
    /// Begin an interactive REPL session. Phase-1 sends header + END only with
    /// no body; the host's REPL takes over once dispatched (T05a).
    /// </summary>
    public sealed record Interactive() : Mode;
}
