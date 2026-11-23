using System.Diagnostics;
using System.Management.Automation;
using PsBash.Host.Runtime;
using PsBash.Testing;
using Xunit;

namespace PsBash.Host.Tests.Runtime;

/// <summary>
/// `cmd &amp;` runs in a runspace from the process-wide set (<c>PsBash.Cmdlets.BashJobRunspaces</c>). The pooled daemon
/// discards one runspace per <c>-c</c> command; a job still running then must not run on, and the job runspaces must
/// not pile up (before the release hook every command that used `&amp;` left a private RunspacePool alive for the life
/// of the host). <see cref="SdkRunspace"/> asks the module to tear its job state down (<c>Remove-BashBgState</c>)
/// before the runspace goes; <see cref="SdkRunspace.BackgroundJobStatesReleased"/> counts the runspaces that had any.
///
/// <para>Oracle note (qa-rubric Directive 1): SDK-runspace lifetime is ps-bash-specific, no bash oracle.</para>
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)] // PSBASH_BG_MODE; runs alone, so off the SdkHost lane too
public class BackgroundJobPoolLifetimeTests : IAsyncLifetime
{
    private readonly HostWorkerFixture _fixture = new();

    private ProcessEnv? _env;

    // These tests cover in-process jobs; the default (process) mode has no job runspace to tear down.
    public async Task InitializeAsync()
    {
        _env = ProcessEnv.Set("PSBASH_BG_MODE", "inproc");
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        _env?.Dispose();
        await _fixture.DisposeAsync();
    }

    [Fact]
    public async Task ReleasingARunspaceThatUsedAmpersand_TearsDownItsJobs()
    {
        int before = SdkRunspace.BackgroundJobStatesReleased;
        var worker = _fixture.CreateWorker();
        var run = await HostWorkerFixture.CaptureAsync(worker, "Invoke-BashBackground { 'x' }; Invoke-BashWait");
        Assert.Equal(0, run.ExitCode);

        await worker.DisposeAsync();

        Assert.True(SdkRunspace.BackgroundJobStatesReleased >= before + 1,
            $"released {SdkRunspace.BackgroundJobStatesReleased - before} job states");
    }

    [Fact]
    public async Task ReleaseStopsJobsStillRunning_AndDoesNotWaitForThem()
    {
        int before = SdkRunspace.BackgroundJobStatesReleased;
        var worker = _fixture.CreateWorker();
        await HostWorkerFixture.CaptureAsync(worker, "Invoke-BashBackground { Start-Sleep -Seconds 60 }");

        var sw = Stopwatch.StartNew();
        await worker.DisposeAsync();
        sw.Stop();

        Assert.True(SdkRunspace.BackgroundJobStatesReleased >= before + 1);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"release took {sw.Elapsed}");
    }

    [Fact]
    public async Task ManyCommandsThatUsedAmpersand_LeaveNoJobRunspacesBehind()
    {
        // The count of every runspace alive in this process, before and after N workers each ran a background job and
        // were released. Job runspaces are reused, at most BashJobRunspaces.MaxIdle (4) kept; a leak keeps one per job.
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
