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
}
