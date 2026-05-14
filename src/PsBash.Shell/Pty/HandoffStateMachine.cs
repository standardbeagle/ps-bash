using PsBash.Core.Runtime.Ipc;

namespace PsBash.Shell.Pty;

/// <summary>
/// PTY-6: launcher-side foreground/background handoff state machine.
///
/// <para>The launcher (ps-bash) owns the user's real terminal. When it spawns
/// the host under a pseudo-terminal and pumps bytes (PTY-2/PTY-3's
/// <c>RunHostUnderPtyAsync</c>), it must know <i>deterministically</i> when the
/// host runspace is mid-command (so the host's TUI output owns the screen) and
/// when it has returned to a prompt (so the launcher's line editor may redraw).
/// Without an explicit signal the launcher would have to poll or guess, which
/// races the line editor against TUI output.</para>
///
/// <para>This type consumes the PTY-6 lifecycle events from
/// <see cref="HostProtocol"/> — <see cref="HostProtocol.BusySentinel"/>,
/// <see cref="HostProtocol.PromptReadySentinel"/> (PTY-4), and
/// <see cref="HostProtocol.HostExitingSentinel"/> — and drives a small
/// three-state machine. It is deliberately pure (no I/O, no threads): the
/// launcher feeds it event lines, it returns the resulting
/// <see cref="HandoffState"/> and a <see cref="HandoffAction"/> describing the
/// terminal-mode transition the launcher should perform. This keeps the state
/// logic unit-testable and the launcher's pump loop thin.</para>
///
/// <para><b>Level-triggered, not edge-triggered.</b> The task spec requires
/// duplicate <c>prompt-ready</c> events to be idempotent: a host that emits
/// <c>prompt-ready</c> twice (e.g. an empty command, or a defensive
/// re-emission) must not cause the launcher to redraw its prompt twice or
/// toggle raw mode spuriously. Every transition here is therefore a no-op when
/// the machine is already in the target state — <see cref="HandoffAction.None"/>
/// is returned and the state is unchanged.</para>
///
/// <para><b>Reconciliation with the task text.</b> PTY-6's description says
/// "InteractiveShell consumes the event and transitions modes". PTY-3 actually
/// placed the terminal-mode toggle (<see cref="TerminalMode"/>) in the
/// <i>launcher</i> around the PTY pump, not in <c>InteractiveShell</c> (which
/// runs inside the host process and has no access to the launcher's tty).
/// <c>prompt-ready</c> exists precisely so the <i>launcher</i> can leave raw
/// mode; consuming it in the host would be meaningless. This state machine is
/// therefore launcher-side, matching PTY-3's landed shape. See the PTY-6
/// completion notes for the recorded divergence.</para>
/// </summary>
public sealed class HandoffStateMachine
{
    private HandoffState _state;

    /// <summary>
    /// Create a handoff state machine in its initial state. A launcher that has
    /// just sent an <c>execute</c> request but not yet seen any lifecycle event
    /// starts in <see cref="HandoffState.HostRunning"/> — the host owns the
    /// terminal until it says otherwise.
    /// </summary>
    public HandoffStateMachine()
    {
        _state = HandoffState.HostRunning;
    }

    /// <summary>The current handoff state.</summary>
    public HandoffState State => _state;

    /// <summary>
    /// Feed one raw IPC response line to the state machine. Non-event lines
    /// (command output, base64 data frames, the <c>&lt;&lt;&lt;EXIT:N&gt;&gt;&gt;</c>
    /// sentinel) are ignored and return <see cref="HandoffAction.None"/> with no
    /// state change — the caller routes those elsewhere. Recognised PTY-6 / PTY-4
    /// lifecycle sentinels drive the transition and return the
    /// <see cref="HandoffAction"/> the launcher should perform.
    /// </summary>
    /// <param name="line">
    /// A single physical line read from the host's interactive response stream,
    /// with its trailing newline already stripped.
    /// </param>
    /// <returns>
    /// The terminal-mode action the launcher should take in response. Repeated
    /// events that do not change the state return <see cref="HandoffAction.None"/>
    /// (idempotency guarantee).
    /// </returns>
    public HandoffAction Consume(string? line)
    {
        if (line is null)
            return HandoffAction.None;

        return line switch
        {
            HostProtocol.BusySentinel => TransitionTo(HandoffState.HostRunning, HandoffAction.EnterRawMode),
            HostProtocol.PromptReadySentinel => TransitionTo(HandoffState.PromptReady, HandoffAction.LeaveRawMode),
            HostProtocol.HostExitingSentinel => TransitionTo(HandoffState.HostExiting, HandoffAction.RestoreTerminal),
            _ => HandoffAction.None,
        };
    }

    /// <summary>
    /// True once the host has signalled <see cref="HostProtocol.HostExitingSentinel"/>.
    /// After this point the launcher should stop feeding lines and let the host
    /// process exit; the terminal has already been restored.
    /// </summary>
    public bool HostExiting => _state == HandoffState.HostExiting;

    private HandoffAction TransitionTo(HandoffState target, HandoffAction action)
    {
        // host-exiting is terminal: once the host has announced it is leaving,
        // a late or duplicate busy/prompt-ready event must not move the machine
        // back — the launcher has already restored the terminal and a re-entry
        // into raw mode would strand the user's parent shell.
        if (_state == HandoffState.HostExiting)
            return HandoffAction.None;

        // Level-triggered: a transition into the state we are already in is a
        // no-op. This is the idempotency guarantee — duplicate prompt-ready (or
        // duplicate busy) events neither redraw the prompt nor toggle raw mode
        // a second time.
        if (_state == target)
            return HandoffAction.None;

        _state = target;
        return action;
    }
}

/// <summary>
/// PTY-6 handoff states. The launcher's terminal ownership is fully described
/// by which of these the <see cref="HandoffStateMachine"/> is in.
/// </summary>
public enum HandoffState
{
    /// <summary>
    /// The host runspace is executing a command. Its output (including raw TUI
    /// escape sequences) owns the terminal; the launcher keeps stdin in raw
    /// mode and does not draw its line editor. This is also the initial state:
    /// immediately after an <c>execute</c> request the host is presumed busy.
    /// </summary>
    HostRunning = 0,

    /// <summary>
    /// The host runspace finished a command and is ready for new input. The
    /// launcher may leave raw mode and redraw its line editor.
    /// </summary>
    PromptReady = 1,

    /// <summary>
    /// The host announced it is about to exit. The launcher has restored
    /// terminal modes and detached signal forwarding; no further input should
    /// be sent. Terminal state.
    /// </summary>
    HostExiting = 2,
}

/// <summary>
/// The terminal-mode action a launcher should perform in response to a handoff
/// transition. The launcher maps each to its <see cref="TerminalMode"/> /
/// <see cref="SignalForwarder"/> scope operations.
/// </summary>
public enum HandoffAction
{
    /// <summary>
    /// No transition occurred (non-event line, or a duplicate event that left
    /// the state unchanged). The launcher does nothing.
    /// </summary>
    None = 0,

    /// <summary>
    /// The host became busy. The launcher should ensure stdin is in raw mode so
    /// keystrokes flow straight to the host's TUI.
    /// </summary>
    EnterRawMode = 1,

    /// <summary>
    /// The host returned to a prompt. The launcher should leave raw mode and
    /// redraw its line editor.
    /// </summary>
    LeaveRawMode = 2,

    /// <summary>
    /// The host is exiting. The launcher should restore the user's original
    /// terminal modes and detach signal forwarding before the host process
    /// exits.
    /// </summary>
    RestoreTerminal = 3,
}
