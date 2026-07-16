using PsBash.Host.Server;
using Xunit;

namespace PsBash.Host.Tests.Server;

/// <summary>
/// Tests for IdleShutdown: verifies the idle timer cancels the CTS when no
/// connections are in-flight, and resets when connections arrive.
/// Oracle note (Directive 1): no bash oracle — ps-bash-specific lifecycle component.
/// </summary>
[Collection("SdkHost")]
public sealed class IdleShutdownTests
{
    [Fact]
    public void NeverConnected_CancelsAfterTimeout()
    {
        using var cts = new CancellationTokenSource();
        using var idle = new IdleShutdown(cts, TimeSpan.FromMilliseconds(50));

        // Event-driven wait: no sleep, just wait for the signal.
        var fired = cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
        Assert.True(fired, "Idle timer should have cancelled the CTS within 5 s");
    }

    [Fact]
    public void ConnectionStarted_BlocksTimer_ThenEndedAllowsFire()
    {
        using var cts = new CancellationTokenSource();
        using var idle = new IdleShutdown(cts, TimeSpan.FromMilliseconds(50));

        idle.ConnectionStarted();
        // Timer should be cancelled — CTS should NOT be signaled yet.
        Assert.False(cts.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(200)),
            "CTS must not fire while a connection is in-flight");

        // Releasing the connection restarts the idle timer.
        idle.ConnectionEnded();

        var fired = cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
        Assert.True(fired, "Idle timer should have cancelled the CTS after connection ended");
    }

    [Fact]
    public void MultipleConnections_OnlyFiresWhenAllEnd()
    {
        using var cts = new CancellationTokenSource();
        using var idle = new IdleShutdown(cts, TimeSpan.FromMilliseconds(50));

        idle.ConnectionStarted(); // in-flight = 1
        idle.ConnectionStarted(); // in-flight = 2
        idle.ConnectionStarted(); // in-flight = 3

        idle.ConnectionEnded();   // in-flight = 2
        idle.ConnectionEnded();   // in-flight = 1

        // Still one in-flight — timer must not fire.
        Assert.False(cts.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(200)),
            "CTS must not fire while connections are still in-flight");

        idle.ConnectionEnded();   // in-flight = 0 → timer starts

        var fired = cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
        Assert.True(fired, "Idle timer should fire after all connections ended");
    }

    [Fact]
    public void Dispose_StopsTimer_NoCancelAfterDispose()
    {
        using var cts = new CancellationTokenSource();
        var idle = new IdleShutdown(cts, TimeSpan.FromMilliseconds(50));

        // Dispose before the timer fires.
        idle.Dispose();

        // Timer is gone — CTS must not be cancelled.
        Assert.False(cts.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(200)),
            "Timer should not fire after Dispose");
    }

    // --- Regression coverage for the timer-callback-vs-ConnectionStarted race -----------------
    //
    // Timer.Dispose() cannot abort a callback that the threadpool has already dequeued and is
    // about to run. Sequence under test: _inFlight==0, the idle timer fires (callback queued),
    // then a NEW connection starts (ConnectionStarted takes _gate, increments _inFlight,
    // CancelTimer()s) before the queued callback actually executes. The callback must not tear
    // down the CTS. Reproduced deterministically via the SimulateTimerFired/CurrentGenerationForTest
    // test seams rather than racing a real Timer with sleeps (Directive 6: no Thread.Sleep in tests).

    [Fact]
    public void TimerFiresConcurrentlyWithConnectionStart_DoesNotCancel()
    {
        using var cts = new CancellationTokenSource();
        using var idle = new IdleShutdown(cts, TimeSpan.FromHours(1)); // long enough it never fires for real

        var scheduledGeneration = idle.CurrentGenerationForTest;

        // A connection arrives concurrently with the (simulated) already-fired callback.
        idle.ConnectionStarted();

        // The stale callback, from the generation scheduled before the connection started, runs late.
        idle.SimulateTimerFired(scheduledGeneration);

        Assert.False(cts.IsCancellationRequested,
            "A timer callback racing a concurrent ConnectionStarted() must not cancel the CTS");
    }

    [Fact]
    public void StaleGenerationCallback_NeverCancels_EvenWhenIdle()
    {
        using var cts = new CancellationTokenSource();
        using var idle = new IdleShutdown(cts, TimeSpan.FromHours(1));

        var staleGeneration = idle.CurrentGenerationForTest;

        // Superseding activity bumps the generation without the callback for the OLD
        // generation ever being cancelled in time (Timer.Dispose can't abort a running callback).
        idle.ConnectionStarted();
        idle.ConnectionEnded();

        Assert.NotEqual(staleGeneration, idle.CurrentGenerationForTest);

        // Even though the host is idle again (_inFlight == 0), a callback for the superseded
        // generation must be a no-op.
        idle.SimulateTimerFired(staleGeneration);

        Assert.False(cts.IsCancellationRequested,
            "A callback for a superseded timer generation must never cancel the CTS");
    }

    [Fact]
    public void CurrentGenerationCallback_WhenIdle_StillCancels()
    {
        using var cts = new CancellationTokenSource();
        using var idle = new IdleShutdown(cts, TimeSpan.FromHours(1));

        var currentGeneration = idle.CurrentGenerationForTest;

        // No connections in flight, callback belongs to the current (not superseded) generation:
        // normal idle expiry must still tear down the CTS.
        idle.SimulateTimerFired(currentGeneration);

        Assert.True(cts.IsCancellationRequested,
            "A non-superseded, non-racing timer callback must still cancel the CTS on idle expiry");
    }
}
