using System.Diagnostics;
using PsBash.Host.Server;
using Xunit;

namespace PsBash.Host.Tests.Server;

/// <summary>
/// Tests for ParentDeathWatcher: verifies the watcher cancels the CTS when the
/// monitored process exits, and leaves it alone while the process is alive.
/// Oracle note (Directive 1): no bash oracle — ps-bash-specific lifecycle component.
/// </summary>
[Collection("SdkHost")]
public sealed class ParentDeathWatcherTests
{
    [Fact]
    public void TryCreate_NullPid_ReturnsNull()
    {
        using var cts = new CancellationTokenSource();
        var watcher = ParentDeathWatcher.TryCreate(null, cts);
        Assert.Null(watcher);
    }

    [Fact]
    public void DeadProcess_CancelsCts()
    {
        // Start a process, kill it, then watch — watcher must detect it quickly.
        using var proc = StartShortLivedProcess();
        var pid = proc.Id;
        proc.Kill();
        proc.WaitForExit();

        using var cts = new CancellationTokenSource();
        using var watcher = ParentDeathWatcher.TryCreate(pid, cts, TimeSpan.FromMilliseconds(20));

        var fired = cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
        Assert.True(fired, "Watcher should detect the dead process and cancel the CTS");
    }

    [Fact]
    public void AliveProcess_DoesNotCancelCts()
    {
        // Watch a process that's still running.
        using var proc = StartLongLivedProcess();
        try
        {
            using var cts = new CancellationTokenSource();
            using var watcher = ParentDeathWatcher.TryCreate(proc.Id, cts, TimeSpan.FromMilliseconds(20));

            // CTS must not be cancelled while the process is alive.
            Assert.False(cts.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(200)),
                "CTS must not fire while the monitored process is alive");
        }
        finally
        {
            proc.Kill();
            proc.WaitForExit();
        }
    }

    [Fact]
    public void ProcessDiesMidWatch_CancelsCts()
    {
        // Start watching a live process, then kill it — watcher must detect death.
        using var proc = StartLongLivedProcess();
        using var cts = new CancellationTokenSource();
        using var watcher = ParentDeathWatcher.TryCreate(proc.Id, cts, TimeSpan.FromMilliseconds(20));

        proc.Kill();
        proc.WaitForExit();

        var fired = cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
        Assert.True(fired, "Watcher should detect process death and cancel the CTS");
    }

    // ─── helpers ────────────────────────────────────────────────────────────────

    private static Process StartShortLivedProcess()
    {
        // A process that exits almost immediately.
        var si = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true }
            : new ProcessStartInfo("true") { UseShellExecute = false };
        return Process.Start(si)!;
    }

    private static Process StartLongLivedProcess()
    {
        // A process that sleeps long enough for us to observe it's alive.
        var si = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }
            : new ProcessStartInfo("sleep", "60") { UseShellExecute = false };
        return Process.Start(si)!;
    }
}
