using System.Diagnostics;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Runtime.Ipc;

/// <summary>
/// The record of a shared host a launcher started but may not live to see come up
/// (<see cref="PendingHostSpawn"/>): the next spawn-lock holder waits for that host instead of
/// starting a second one (the status line, cancelled mid-start, left a trail of ~150 MB hosts).
/// Oracle note (Directive 1): no bash oracle — ps-bash host lifecycle surface.
/// </summary>
public class PendingHostSpawnTests : IDisposable
{
    private readonly string _endpoint =
        Path.Combine(Path.GetTempPath(), "psb-pend-" + Guid.NewGuid().ToString("N")[..8] + ".sock");

    public void Dispose() => PendingHostSpawn.Clear(_endpoint);

    private static readonly DateTime Now = new(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(20);

    [Fact]
    public void Judge_NoRecord_IsNone()
        => Assert.Equal(PendingHostSpawn.Verdict.None, PendingHostSpawn.Judge(null, Now, Window, _ => true));

    [Fact]
    public void Judge_RecordedProcessGone_IsNone()
    {
        var id = new ProcessIdentity(4242, Now.AddSeconds(-1).Ticks);
        Assert.Equal(PendingHostSpawn.Verdict.None, PendingHostSpawn.Judge(id, Now, Window, _ => false));
    }

    [Fact]
    public void Judge_RunningInsideWindow_IsWaitForIt()
    {
        var id = new ProcessIdentity(4242, Now.AddSeconds(-5).Ticks);
        Assert.Equal(PendingHostSpawn.Verdict.WaitForIt, PendingHostSpawn.Judge(id, Now, Window, _ => true));
    }

    [Fact]
    public void Judge_RunningPastWindow_IsAbandon()
    {
        // Measured from the host's own start, not from when this launcher began waiting: a chain of
        // killed launchers cannot keep extending a host that never binds.
        var id = new ProcessIdentity(4242, Now.AddSeconds(-21).Ticks);
        Assert.Equal(PendingHostSpawn.Verdict.Abandon, PendingHostSpawn.Judge(id, Now, Window, _ => true));
    }

    [Fact]
    public void Record_ThenTryRead_RoundTripsTheProcessIdentity()
    {
        using var self = Process.GetCurrentProcess();
        PendingHostSpawn.Record(_endpoint, self);

        var read = PendingHostSpawn.TryRead(_endpoint);

        Assert.Equal(new ProcessIdentity(self.Id, ProcessIdentity.StartTicksOf(self)), read);
        Assert.True(read!.Value.IsRunning());
    }

    [Fact]
    public void TryRead_MissingOrGarbage_IsNull()
    {
        Assert.Null(PendingHostSpawn.TryRead(_endpoint));
        File.WriteAllText(PendingHostSpawn.PathFor(_endpoint), "not-an-identity");
        Assert.Null(PendingHostSpawn.TryRead(_endpoint));
    }

    [Fact]
    public void Clear_RemovesTheRecord()
    {
        File.WriteAllText(PendingHostSpawn.PathFor(_endpoint), "4242:1");
        PendingHostSpawn.Clear(_endpoint);
        Assert.False(File.Exists(PendingHostSpawn.PathFor(_endpoint)));
    }

    [Fact]
    public void TryKill_RecycledPid_LeavesTheProcessAlone()
    {
        // Our own pid with a different start time is "a recycled pid": must not be killed (and the
        // launcher's own pid is excluded outright).
        using var self = Process.GetCurrentProcess();
        PendingHostSpawn.TryKill(new ProcessIdentity(self.Id, 1));
        Assert.False(self.HasExited);
    }
}
