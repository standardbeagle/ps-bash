using System;
using System.Diagnostics;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Runtime.Ipc;

/// <summary>
/// The startup deadline is a checkpoint (docs/specs/host-lifecycle-contract.md, startup timeout): a host still
/// starting when it passes gets another window; a stuck or vanished one fails there. Regression: under a loaded
/// differential run a cold host failed at a fixed 15 s ("did not accept connections within 15s", exit 125).
/// </summary>
public class HostStartupDeadlineTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(15);

    private sealed class Clock
    {
        public DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public void Advance(TimeSpan by) => Now += by;
    }

    [Fact]
    public void BeforeTheDeadline_ContinuesWithoutReadingTheHost()
    {
        var clock = new Clock();
        var deadline = new HostStartupDeadline(Window, () => clock.Now);
        clock.Advance(TimeSpan.FromSeconds(14));

        Assert.True(deadline.Continue(() => throw new InvalidOperationException("must not read CPU before the deadline")));
        Assert.Equal(0, deadline.Extensions);
    }

    [Fact]
    public void PastTheDeadline_HostStillBurningCpu_GetsAnotherWindow()
    {
        var clock = new Clock();
        var deadline = new HostStartupDeadline(Window, () => clock.Now);
        clock.Advance(Window);

        Assert.True(deadline.Continue(() => TimeSpan.FromSeconds(2)));
        Assert.Equal(1, deadline.Extensions);

        // Inside the new window: still waiting.
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(deadline.Continue(() => TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void PastTheDeadline_HostIdleSinceLastCheckpoint_IsStuck()
    {
        var clock = new Clock();
        var deadline = new HostStartupDeadline(Window, () => clock.Now);
        clock.Advance(Window);
        Assert.True(deadline.Continue(() => TimeSpan.FromSeconds(2)));

        clock.Advance(Window);
        Assert.False(deadline.Continue(() => TimeSpan.FromSeconds(2))); // no CPU since the last checkpoint
        Assert.Contains("extended 1x", deadline.Describe());
    }

    [Fact]
    public void PastTheDeadline_NoHostToWatch_Fails()
    {
        var clock = new Clock();
        var deadline = new HostStartupDeadline(Window, () => clock.Now);
        clock.Advance(Window);

        Assert.False(deadline.Continue(() => null));
        Assert.Equal("15s", deadline.Describe());
    }

    [Fact]
    public void HostSpinningForever_StopsAtTheExtensionCap()
    {
        var clock = new Clock();
        var deadline = new HostStartupDeadline(Window, () => clock.Now);
        var cpu = TimeSpan.Zero;
        var rounds = 0;
        while (true)
        {
            clock.Advance(Window);
            cpu += TimeSpan.FromSeconds(1);
            if (!deadline.Continue(() => cpu)) break;
            rounds++;
            Assert.True(rounds <= HostStartupDeadline.MaxExtensions, "the cap must end the wait");
        }

        Assert.Equal(HostStartupDeadline.MaxExtensions, deadline.Extensions);
    }

    [Fact]
    public void CpuOf_ReadsThisProcess_AndNullForNoProcess()
    {
        using var self = Process.GetCurrentProcess();
        Assert.NotNull(HostStartupDeadline.CpuOf(self));
        Assert.NotNull(HostStartupDeadline.CpuOfPid(Environment.ProcessId));
        Assert.Null(HostStartupDeadline.CpuOf((Process?)null));
        Assert.Null(HostStartupDeadline.CpuOf((ProcessIdentity?)null));
        Assert.Null(HostStartupDeadline.CpuOfPid(0));
    }
}
