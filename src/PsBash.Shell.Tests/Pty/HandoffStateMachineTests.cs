using PsBash.Core.Runtime.Ipc;
using PsBash.Shell.Pty;
using Xunit;

namespace PsBash.Shell.Tests.Pty;

/// <summary>
/// PTY-6 acceptance tests: the launcher-side foreground/background handoff
/// state machine (<see cref="HandoffStateMachine"/>).
///
/// <para>The state machine is pure (no I/O, no threads), so these are
/// deterministic in-process tests — no PTY, no timing, zero flake surface.
/// They pin the three properties the task spec calls for:</para>
/// <list type="number">
///   <item><description>a round-tripped command's <c>prompt-ready</c> event
///     moves the launcher out of raw mode;</description></item>
///   <item><description>duplicate events are idempotent (no spurious second
///     transition);</description></item>
///   <item><description>the <c>host-exiting</c> event is terminal and triggers
///     a terminal restore.</description></item>
/// </list>
/// </summary>
public class HandoffStateMachineTests
{
    [Fact]
    public void InitialState_IsHostRunning()
    {
        // After an execute request but before any lifecycle event, the host
        // owns the terminal — the launcher presumes it busy.
        var sm = new HandoffStateMachine();
        Assert.Equal(HandoffState.HostRunning, sm.State);
        Assert.False(sm.HostExiting);
    }

    [Fact]
    public void PromptReady_FromHostRunning_LeavesRawMode()
    {
        // The core round-trip: host finished a command, launcher leaves raw
        // mode and may redraw its line editor.
        var sm = new HandoffStateMachine();
        var action = sm.Consume(HostProtocol.PromptReadySentinel);
        Assert.Equal(HandoffAction.LeaveRawMode, action);
        Assert.Equal(HandoffState.PromptReady, sm.State);
    }

    [Fact]
    public void DuplicatePromptReady_IsIdempotent_NoSecondTransition()
    {
        // Task spec: "assert duplicate events are idempotent." A host that
        // emits prompt-ready twice (empty command, defensive re-emission) must
        // not cause a second LeaveRawMode — the launcher would redraw its
        // prompt twice.
        var sm = new HandoffStateMachine();

        var first = sm.Consume(HostProtocol.PromptReadySentinel);
        Assert.Equal(HandoffAction.LeaveRawMode, first);

        var second = sm.Consume(HostProtocol.PromptReadySentinel);
        Assert.Equal(HandoffAction.None, second);

        var third = sm.Consume(HostProtocol.PromptReadySentinel);
        Assert.Equal(HandoffAction.None, third);

        Assert.Equal(HandoffState.PromptReady, sm.State);
    }

    [Fact]
    public void Busy_FromPromptReady_ReEntersRawMode()
    {
        // Next command starts: host goes busy again, launcher re-enters raw
        // mode so keystrokes reach the host's TUI.
        var sm = new HandoffStateMachine();
        sm.Consume(HostProtocol.PromptReadySentinel);

        var action = sm.Consume(HostProtocol.BusySentinel);
        Assert.Equal(HandoffAction.EnterRawMode, action);
        Assert.Equal(HandoffState.HostRunning, sm.State);
    }

    [Fact]
    public void DuplicateBusy_IsIdempotent()
    {
        // Busy is also level-triggered. The initial state is already
        // HostRunning, so the very first busy event is a no-op, as is a
        // repeat after a real prompt-ready→busy cycle.
        var sm = new HandoffStateMachine();
        Assert.Equal(HandoffAction.None, sm.Consume(HostProtocol.BusySentinel));

        sm.Consume(HostProtocol.PromptReadySentinel);
        Assert.Equal(HandoffAction.EnterRawMode, sm.Consume(HostProtocol.BusySentinel));
        Assert.Equal(HandoffAction.None, sm.Consume(HostProtocol.BusySentinel));
        Assert.Equal(HandoffState.HostRunning, sm.State);
    }

    [Fact]
    public void FullCycle_BusyPromptReadyBusyPromptReady()
    {
        // A realistic two-command interactive session.
        var sm = new HandoffStateMachine();

        // command 1 finishes
        Assert.Equal(HandoffAction.LeaveRawMode, sm.Consume(HostProtocol.PromptReadySentinel));
        // command 2 starts
        Assert.Equal(HandoffAction.EnterRawMode, sm.Consume(HostProtocol.BusySentinel));
        // command 2 finishes
        Assert.Equal(HandoffAction.LeaveRawMode, sm.Consume(HostProtocol.PromptReadySentinel));
    }

    [Fact]
    public void HostExiting_FromPromptReady_RestoresTerminal()
    {
        var sm = new HandoffStateMachine();
        sm.Consume(HostProtocol.PromptReadySentinel);

        var action = sm.Consume(HostProtocol.HostExitingSentinel);
        Assert.Equal(HandoffAction.RestoreTerminal, action);
        Assert.Equal(HandoffState.HostExiting, sm.State);
        Assert.True(sm.HostExiting);
    }

    [Fact]
    public void HostExiting_FromHostRunning_RestoresTerminal()
    {
        // The host can exit mid-command (e.g. the user typed `exit` and the
        // REPL tore down). Still a clean RestoreTerminal.
        var sm = new HandoffStateMachine();
        var action = sm.Consume(HostProtocol.HostExitingSentinel);
        Assert.Equal(HandoffAction.RestoreTerminal, action);
        Assert.True(sm.HostExiting);
    }

    [Fact]
    public void HostExiting_IsTerminal_LateEventsAreNoOps()
    {
        // Once host-exiting fires the launcher has restored the terminal. A
        // late or duplicate busy/prompt-ready (race against process exit) must
        // NOT re-enter raw mode and strand the user's parent shell.
        var sm = new HandoffStateMachine();
        sm.Consume(HostProtocol.HostExitingSentinel);

        Assert.Equal(HandoffAction.None, sm.Consume(HostProtocol.BusySentinel));
        Assert.Equal(HandoffAction.None, sm.Consume(HostProtocol.PromptReadySentinel));
        Assert.Equal(HandoffAction.None, sm.Consume(HostProtocol.HostExitingSentinel));
        Assert.Equal(HandoffState.HostExiting, sm.State);
    }

    [Theory]
    [InlineData("")]
    [InlineData("regular command output")]
    [InlineData("<<<EXIT:0>>>")]
    [InlineData("<<<SIGNAL-DELIVERED:SIGINT>>>")]
    [InlineData("c29tZSBiYXNlNjQ=")]
    public void NonEventLines_AreIgnored_NoStateChange(string line)
    {
        // Command output, the EXIT sentinel, PTY-5's signal-delivered token,
        // and base64 data frames all flow past the handoff machine untouched —
        // the caller routes those elsewhere.
        var sm = new HandoffStateMachine();
        var action = sm.Consume(line);
        Assert.Equal(HandoffAction.None, action);
        Assert.Equal(HandoffState.HostRunning, sm.State);
    }

    [Fact]
    public void NullLine_IsIgnored()
    {
        // EOF on the response stream surfaces as a null line; must not throw.
        var sm = new HandoffStateMachine();
        var action = sm.Consume(null);
        Assert.Equal(HandoffAction.None, action);
        Assert.Equal(HandoffState.HostRunning, sm.State);
    }

    [Fact]
    public void NonEventLinesInterleavedWithEvents_DoNotDisturbTransitions()
    {
        // Realistic stream: output lines interleaved with lifecycle events.
        var sm = new HandoffStateMachine();

        Assert.Equal(HandoffAction.None, sm.Consume("building..."));
        Assert.Equal(HandoffAction.None, sm.Consume("<<<EXIT:0>>>"));
        Assert.Equal(HandoffAction.LeaveRawMode, sm.Consume(HostProtocol.PromptReadySentinel));
        Assert.Equal(HandoffAction.None, sm.Consume("more output"));
        Assert.Equal(HandoffAction.EnterRawMode, sm.Consume(HostProtocol.BusySentinel));
        Assert.Equal(HandoffState.HostRunning, sm.State);
    }
}
