using System.Diagnostics;
using System.Management.Automation;
using PsBash.Host.Runtime;
using Xunit;

namespace PsBash.Host.Tests.Runtime;

/// <summary>
/// `cmd &amp;` runs on a private <c>RunspacePool</c> (psm1 <c>$script:BashBgPool</c>). The pooled daemon
/// discards one runspace per <c>-c</c> command, and disposing a runspace does not dispose a pool its
/// variables point at, so before the release hook every command that used `&amp;` left a pool of runspaces
/// alive for the life of the host. <see cref="SdkRunspace"/> now asks the module to tear its job state
/// down (<c>Remove-BashBgState</c>) before the runspace goes; <see cref="SdkRunspace.BackgroundPoolsReleased"/>
/// counts the pools that existed to be torn down.
///
/// <para>Oracle note (qa-rubric Directive 1): SDK-runspace lifetime is ps-bash-specific, no bash oracle.</para>
/// </summary>
[Collection("SdkHost")]
public class BackgroundJobPoolLifetimeTests : IAsyncLifetime
{
    private readonly HostWorkerFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task ReleasingARunspaceThatUsedAmpersand_TearsDownItsJobPool()
    {
        int before = SdkRunspace.BackgroundPoolsReleased;
        var worker = _fixture.CreateWorker();
        var run = await HostWorkerFixture.CaptureAsync(worker, "Invoke-BashBackground { 'x' }; Invoke-BashWait");
        Assert.Equal(0, run.ExitCode);

        await worker.DisposeAsync();

        Assert.True(SdkRunspace.BackgroundPoolsReleased >= before + 1,
            $"released {SdkRunspace.BackgroundPoolsReleased - before} pools");
    }

    [Fact]
    public async Task ReleaseStopsJobsStillRunning_AndDoesNotWaitForThem()
    {
        int before = SdkRunspace.BackgroundPoolsReleased;
        var worker = _fixture.CreateWorker();
        await HostWorkerFixture.CaptureAsync(worker, "Invoke-BashBackground { Start-Sleep -Seconds 60 }");

        var sw = Stopwatch.StartNew();
        await worker.DisposeAsync();
        sw.Stop();

        Assert.True(SdkRunspace.BackgroundPoolsReleased >= before + 1);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"release took {sw.Elapsed}");
    }

    [Fact]
    public async Task ManyCommandsThatUsedAmpersand_LeaveNoJobRunspacesBehind()
    {
        // The count of every runspace alive in this process, before and after N workers each ran a
        // background job and were released. A leaked pool keeps (at least) its minimum runspace each.
        int Live()
        {
            using var ps = PowerShell.Create();
            return ps.AddCommand("Get-Runspace").Invoke().Count;
        }

        const int n = 6;
        int before = Live();
        for (int i = 0; i < n; i++)
        {
            var worker = _fixture.CreateWorker();
            await HostWorkerFixture.CaptureAsync(worker, "Invoke-BashBackground { 'x' }; Invoke-BashWait");
            await worker.DisposeAsync();
        }
        int after = Live();

        // Other test classes may hold a few runspaces of their own while this runs; a leak is >= n.
        Assert.True(after - before < n, $"live runspaces grew {before} -> {after} over {n} released workers");
    }
}
