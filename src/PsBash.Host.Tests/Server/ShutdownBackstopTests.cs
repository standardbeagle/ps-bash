using PsBash.Host.Server;
using Xunit;

namespace PsBash.Host.Tests.Server;

/// <summary>
/// Once a host starts shutting down, the process ends within a deadline whatever teardown does: a host whose
/// session was gone logged that it would exit and then never logged an exit, running on for 25+ minutes.
/// Oracle note (Directive 1): no bash oracle — ps-bash host lifecycle surface.
/// </summary>
public sealed class ShutdownBackstopTests
{
    [Fact]
    public void ShutdownStarted_ProcessStillUpAfterTheDeadline_IsEnded()
    {
        using var cts = new CancellationTokenSource();
        using var ended = new ManualResetEventSlim();
        string? reason = null;
        ShutdownBackstop.Arm(cts.Token, TimeSpan.FromMilliseconds(100), why => { reason = why; ended.Set(); });

        cts.Cancel();

        Assert.True(ended.Wait(TimeSpan.FromSeconds(5)), "a host that began shutting down must end");
        Assert.Contains("did not finish", reason);
    }

    [Fact]
    public void NoShutdown_NeverEndsTheProcess()
    {
        using var cts = new CancellationTokenSource();
        using var ended = new ManualResetEventSlim();
        ShutdownBackstop.Arm(cts.Token, TimeSpan.FromMilliseconds(50), _ => ended.Set());

        Assert.False(ended.Wait(TimeSpan.FromMilliseconds(400)), "a serving host is never ended by the backstop");
    }
}
