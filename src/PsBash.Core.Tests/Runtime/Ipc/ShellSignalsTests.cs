using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Runtime.Ipc;

/// <summary>
/// The receiving half of a signal sent to a ps-bash shell (docs/specs/background-jobs.md §13): what
/// <see cref="ShellSignals.Deliver"/> does with it, by the command's state. ps-bash-specific surface (there is no bash
/// equivalent of the queue), so asserted directly; the end-to-end behavior is compared with bash in
/// <c>Differential.Tests/ShellSignalDifferentialTests</c>. ShellSignals is process-wide, so every test of it is in this
/// one class (xunit runs a class's tests one at a time) and no other Core test touches it.
/// </summary>
public class ShellSignalsTests : IDisposable
{
    private readonly Guid _runspace = Guid.NewGuid();
    private readonly List<int> _terminated = new();

    private void Begin() => ShellSignals.BeginCommand(_runspace, status => _terminated.Add(status));

    public void Dispose() => ShellSignals.EndCommand(_runspace);

    [Fact]
    public void Deliver_NoCommandRunning_DoesNothing()
    {
        Assert.Equal(ShellSignals.Outcome.NoCommand, ShellSignals.Deliver(10));
        Assert.False(ShellSignals.Pending);
    }

    [Fact]
    public void Deliver_NoTrapEverSet_TerminatesWith128PlusN()
    {
        Begin();
        Assert.Equal(ShellSignals.Outcome.Terminated, ShellSignals.Deliver(10));
        Assert.Equal(new[] { 138 }, _terminated);
        Assert.False(ShellSignals.Pending);
    }

    [Theory]
    [InlineData(17)] // CHLD
    [InlineData(23)] // URG
    [InlineData(28)] // WINCH
    [InlineData(19)] // STOP: a host command cannot be suspended
    [InlineData(20)] // TSTP
    public void Deliver_IgnoredByDefault_NeitherTerminatesNorQueues(int signal)
    {
        Begin();
        Assert.Equal(ShellSignals.Outcome.Ignored, ShellSignals.Deliver(signal));
        Assert.Empty(_terminated);
        Assert.False(ShellSignals.Pending);
    }

    [Fact]
    public void Deliver_AfterATrapWasSet_QueuesForTheStatementBoundary()
    {
        Begin();
        ShellSignals.NoteTrapSet(_runspace);
        Assert.Equal(ShellSignals.Outcome.Queued, ShellSignals.Deliver(10));
        Assert.Equal(ShellSignals.Outcome.Queued, ShellSignals.Deliver(12));
        Assert.True(ShellSignals.Pending);
        Assert.Empty(_terminated);

        Assert.Equal(new[] { 10, 12 }, ShellSignals.TakePending(_runspace));
        Assert.False(ShellSignals.Pending);
        Assert.Empty(ShellSignals.TakePending(_runspace));
    }

    [Fact]
    public void TakePending_CoalescesRepeatsAndOrdersBySignalNumber()
    {
        // Oracle (bash 5.2): `sh -c "kill -USR2 $$; kill -USR1 $$; kill -USR2 $$"` runs the USR1 trap, then USR2, each once.
        Begin();
        ShellSignals.NoteTrapSet(_runspace);
        ShellSignals.Deliver(12);
        ShellSignals.Deliver(10);
        ShellSignals.Deliver(12);
        Assert.Equal(new[] { 10, 12 }, ShellSignals.TakePending(_runspace));
    }

    [Fact]
    public void Deliver_Kill9_TerminatesEvenWithTraps()
    {
        Begin();
        ShellSignals.NoteTrapSet(_runspace);
        Assert.Equal(ShellSignals.Outcome.Terminated, ShellSignals.Deliver(9));
        Assert.Equal(new[] { 137 }, _terminated);
        Assert.False(ShellSignals.Pending);
    }

    [Fact]
    public void NoteTrapSet_FromAnotherRunspace_DoesNotMakeSignalsWait()
    {
        // A background job's runspace setting a trap is not the shell's trap table.
        Begin();
        ShellSignals.NoteTrapSet(Guid.NewGuid());
        Assert.Equal(ShellSignals.Outcome.Terminated, ShellSignals.Deliver(15));
        Assert.Equal(new[] { 143 }, _terminated);
    }

    [Fact]
    public void TakePending_FromAnotherRunspace_TakesNothing()
    {
        Begin();
        ShellSignals.NoteTrapSet(_runspace);
        ShellSignals.Deliver(10);
        Assert.Empty(ShellSignals.TakePending(Guid.NewGuid()));
        Assert.True(ShellSignals.Pending);
    }

    [Fact]
    public void EndCommand_DropsQueuedSignalsAndTrapState()
    {
        // A signal never reaches the NEXT command a host runs, and that command starts with no traps.
        Begin();
        ShellSignals.NoteTrapSet(_runspace);
        ShellSignals.Deliver(10);
        ShellSignals.EndCommand(_runspace);
        Assert.False(ShellSignals.Pending);
        Assert.False(ShellSignals.HasCommand);
        Assert.Equal(ShellSignals.Outcome.NoCommand, ShellSignals.Deliver(10));

        Begin();
        Assert.Empty(ShellSignals.TakePending(_runspace));
        Assert.Equal(ShellSignals.Outcome.Terminated, ShellSignals.Deliver(10));
    }

    [Fact]
    public void EndCommand_OfAnotherRunspace_LeavesTheCommand()
    {
        Begin();
        ShellSignals.EndCommand(Guid.NewGuid());
        Assert.True(ShellSignals.HasCommand);
        Assert.True(ShellSignals.IsOwner(_runspace));
    }

    [Fact]
    public void Deliver_TerminateCallbackThrows_DoesNotEscape()
    {
        // Never take a host down on input: a failing stop is swallowed.
        ShellSignals.BeginCommand(_runspace, _ => throw new InvalidOperationException("stopping"));
        Assert.Equal(ShellSignals.Outcome.Terminated, ShellSignals.Deliver(10));
    }

    [Fact]
    public void SendToShell_NoRoute_IsNotAShell()
    {
        var saved = ShellSignals.Route;
        try
        {
            ShellSignals.Route = null;
            Assert.False(ShellSignals.SendToShell(12345, 10, out var outcome));
            Assert.Equal(ShellSignals.Outcome.NotAShell, outcome);
        }
        finally { ShellSignals.Route = saved; }
    }

    [Fact]
    public void SendToShell_RouteThrows_IsNotAShell()
    {
        var saved = ShellSignals.Route;
        try
        {
            ShellSignals.Route = (_, _) => throw new IOException("unreachable");
            Assert.False(ShellSignals.SendToShell(12345, 10, out var outcome));
            Assert.Equal(ShellSignals.Outcome.NotAShell, outcome);
        }
        finally { ShellSignals.Route = saved; }
    }

    [Fact]
    public void SendToShell_RouteReachesAShell_ReportsItsOutcome()
    {
        var saved = ShellSignals.Route;
        try
        {
            ShellSignals.Route = (pid, sig) => pid == 4242 && sig == 10 ? ShellSignals.Outcome.Queued : ShellSignals.Outcome.NotAShell;
            Assert.True(ShellSignals.SendToShell(4242, 10, out var outcome));
            Assert.Equal(ShellSignals.Outcome.Queued, outcome);
            Assert.False(ShellSignals.SendToShell(4243, 10, out _));
        }
        finally { ShellSignals.Route = saved; }
    }

    [Fact]
    public void Sender_ANonHostPid_IsNotAShell()
    {
        // Bad pids / signal numbers, and a pid no ps-bash host sidecar names, are no shell: nothing is delivered.
        Assert.Null(ShellSignalSender.EndpointOf(-1));
        Assert.Equal(ShellSignals.Outcome.NotAShell, ShellSignalSender.Send(0, 10));
        Assert.Equal(ShellSignals.Outcome.NotAShell, ShellSignalSender.Send(int.MaxValue, 10));
        Assert.Equal(ShellSignals.Outcome.NotAShell, ShellSignalSender.Send(12345, 0));
    }

    [Theory]
    [InlineData("ps-bash-host signal=Queued", ShellSignals.Outcome.Queued)]
    [InlineData("ps-bash-host signal=Terminated", ShellSignals.Outcome.Terminated)]
    [InlineData("ps-bash-host signal=Ignored", ShellSignals.Outcome.Ignored)]
    [InlineData("ps-bash-host signal=NoCommand", ShellSignals.Outcome.NoCommand)]
    [InlineData("ps-bash-host signal=NotAShell", ShellSignals.Outcome.NotAShell)]
    [InlineData("ps-bash-host signal=Bogus", ShellSignals.Outcome.NotAShell)]
    [InlineData("ps-bash-host error: unknown mode", ShellSignals.Outcome.NotAShell)]
    [InlineData(null, ShellSignals.Outcome.NotAShell)]
    public void ParseOutcome_OnlyAHostAnswerCounts(string? line, ShellSignals.Outcome expected)
        => Assert.Equal(expected, ShellSignalSender.ParseOutcome(line));

    [Fact]
    public void FormatOutcome_RoundTripsThroughParseOutcome()
    {
        foreach (var o in new[] { ShellSignals.Outcome.Queued, ShellSignals.Outcome.Ignored, ShellSignals.Outcome.Terminated, ShellSignals.Outcome.NoCommand })
            Assert.Equal(o, ShellSignalSender.ParseOutcome(ShellSignalSender.FormatOutcome(o)));
    }

    [Fact]
    public async Task Protocol_SignalRequest_RoundTrips()
    {
        await using var ms = new MemoryStream();
        await HostProtocol.WriteRequestAsync(ms, new Mode.Signal(10));
        ms.Position = 0;
        var decoded = Assert.IsType<Mode.Signal>(await HostProtocol.ReadRequestAsync(ms));
        Assert.Equal(10, decoded.Number);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65")]
    [InlineData("-1")]
    [InlineData("USR1")]
    [InlineData("")]
    public async Task Protocol_SignalRequest_WithABadNumber_IsRejected(string raw)
    {
        // A malformed request fails that connection (IOException), never the host.
        var frame = $"{HostProtocol.ModeHeaderPrefix}Signal\n{HostProtocol.SignalHeaderPrefix}{raw}\n{HostProtocol.EndSentinel}\n";
        await using var ms = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(frame));
        await Assert.ThrowsAsync<IOException>(() => HostProtocol.ReadRequestAsync(ms));
    }

    [Fact]
    public async Task Protocol_SignalRequest_MissingNumber_IsRejected()
    {
        var frame = $"{HostProtocol.ModeHeaderPrefix}Signal\n{HostProtocol.EndSentinel}\n";
        await using var ms = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(frame));
        await Assert.ThrowsAsync<IOException>(() => HostProtocol.ReadRequestAsync(ms));
    }
}
