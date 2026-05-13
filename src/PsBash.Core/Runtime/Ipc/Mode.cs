namespace PsBash.Core.Runtime.Ipc;

/// <summary>
/// Session-mode discriminator carried on every request that runs a command
/// (<see cref="Mode.Command"/>, <see cref="Mode.Stdin"/>, <see cref="Mode.Script"/>).
/// PTY-4 boundary: in <see cref="Framed"/>, the host serializes command output
/// through the IPC channel one base64-encoded line at a time and the launcher
/// re-emits the bytes; in <see cref="Interactive"/>, command output bytes go
/// straight from the host runspace to <c>System.Console.Out</c> — which the
/// PTY-2 spawn wired to the PTY slave — and the IPC channel carries only
/// protocol/lifecycle events (exit code, <c>prompt-ready</c>, future tab-completion
/// queries). This preserves raw TUI byte fidelity (escape sequences, cursor
/// movement) and lifts the IPC line-framing throughput bottleneck.
/// </summary>
/// <remarks>
/// <para>
/// Wire format: <c>SESSION:Framed</c> or <c>SESSION:Interactive</c>, immediately
/// after the <c>MODE:</c> header line. The header is OPTIONAL; missing means
/// <see cref="Framed"/> (back-compat with pre-PTY-4 launchers).
/// </para>
/// <para>
/// <see cref="Interactive"/> mode is meaningful only when the host runspace has
/// a real terminal on its stdio — i.e. when spawned via <c>PtySpawner</c> with
/// <c>PSBASH_PTY_ATTACHED=1</c>. If a launcher requests <see cref="Interactive"/>
/// on a host whose stdio is redirected (the legacy non-PTY path), command
/// output will be silently swallowed by the redirected pipe; the launcher is
/// responsible for not requesting <see cref="Interactive"/> in that case.
/// </para>
/// </remarks>
public enum SessionMode
{
    /// <summary>
    /// Default. Host streams command output to the launcher via base64-encoded
    /// IPC response lines, terminated by <c>&lt;&lt;&lt;EXIT:N&gt;&gt;&gt;</c>.
    /// Used by <c>-c</c>, stdin pipe, and script-file invocations.
    /// </summary>
    Framed = 0,

    /// <summary>
    /// Command output bypasses IPC framing and writes directly to the host's
    /// <c>System.Console.Out</c> (the PTY slave). The IPC response stream
    /// carries only the <c>&lt;&lt;&lt;EXIT:N&gt;&gt;&gt;</c> sentinel and a
    /// trailing <c>&lt;&lt;&lt;PROMPT-READY&gt;&gt;&gt;</c> lifecycle frame
    /// signalling the launcher may re-take terminal control (restore line-editor
    /// state, repaint prompt).
    /// </summary>
    Interactive = 1,
}

/// <summary>
/// Discriminated union of host-protocol request modes.
/// </summary>
/// <remarks>
/// Phase-1 protocol: the launcher opens one connection per request and sends a
/// single <see cref="Mode"/> as a <c>MODE:&lt;kind&gt;</c> header line followed
/// by mode-specific body lines and a final <c>&lt;&lt;&lt;END&gt;&gt;&gt;</c>
/// terminator. The host writes zero or more output lines then a single
/// <c>&lt;&lt;&lt;EXIT:N&gt;&gt;&gt;</c> sentinel. Sentinels are reused byte-for-byte
/// from the worker contract so in-process and cross-process workers
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
    /// <param name="Body">Bash command string.</param>
    /// <param name="Session">Session mode (<see cref="SessionMode.Framed"/> by default).</param>
    public sealed record Command(string Body, SessionMode Session = SessionMode.Framed) : Mode;

    /// <summary>
    /// Bash script body read from launcher's stdin, evaluated as a sequence of
    /// commands. Equivalent to <c>echo "..." | ps-bash</c>.
    /// </summary>
    /// <param name="Body">Bash script body.</param>
    /// <param name="Session">Session mode (<see cref="SessionMode.Framed"/> by default).</param>
    public sealed record Stdin(string Body, SessionMode Session = SessionMode.Framed) : Mode;

    /// <summary>
    /// Script-file invocation. <paramref name="Path"/> is the absolute script
    /// path on the launcher's filesystem (informational; the body has already
    /// been read by the launcher), <paramref name="Argv"/> is the positional
    /// argument vector ($1..$N), and <paramref name="Body"/> is the full script
    /// contents. Path and argv elements may contain newlines and quote
    /// characters — they are encoded base64 on the wire.
    /// </summary>
    /// <param name="Path">Absolute script path.</param>
    /// <param name="Argv">Positional argument vector.</param>
    /// <param name="Body">Full script contents.</param>
    /// <param name="Session">Session mode (<see cref="SessionMode.Framed"/> by default).</param>
    public sealed record Script(string Path, IReadOnlyList<string> Argv, string Body, SessionMode Session = SessionMode.Framed) : Mode;

    /// <summary>
    /// Begin an interactive REPL session. Phase-1 sends header + END only with
    /// no body; the host's REPL takes over once dispatched (T05a).
    /// </summary>
    public sealed record Interactive() : Mode;

    /// <summary>
    /// Lightweight launcher-to-host health probe. A healthy host responds before
    /// touching the PowerShell worker so stale or wedged workers are not reused.
    /// </summary>
    public sealed record Health() : Mode;

    /// <summary>
    /// Request graceful shutdown. The host stops accepting new connections,
    /// waits up to <paramref name="DeadlineMs"/> milliseconds for in-flight
    /// requests to drain, then exits. A compatible launcher uses this when
    /// health reveals a protocol/build mismatch or obsolete host metadata so
    /// the running host retires before a replacement is spawned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>DeadlineMs</c> bounds the drain wait. Zero or negative means "no
    /// drain wait" — in-flight requests are abandoned at the deadline, but the
    /// shutdown is still acknowledged. The default deadline is
    /// <see cref="HostProtocol.DefaultShutdownDeadlineMs"/>.
    /// </para>
    /// <para>
    /// The host responds to the shutdown request itself with
    /// <see cref="HostProtocol.ShutdownAcceptedPayload"/> and exit 0 before
    /// closing the accept loop, so the requesting client always sees an
    /// acknowledgement.
    /// </para>
    /// </remarks>
    public sealed record Shutdown(int DeadlineMs) : Mode;
}
