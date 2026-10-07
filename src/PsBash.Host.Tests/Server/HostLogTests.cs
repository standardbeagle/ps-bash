using PsBash.Host.Server;
using Xunit;

namespace PsBash.Host.Tests.Server;

/// <summary>
/// Every host on the machine appends to one log; before this, its lines carried no pid, endpoint or
/// version and no host logged why it exited, so a "connection error" could not be attributed and a
/// crash looked exactly like a clean stop. Serialized: the exit reason and path seam are per-process.
/// </summary>
[Collection(nameof(HostLogTests))]
[CollectionDefinition(nameof(HostLogTests), DisableParallelization = true)]
public class HostLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ps-bash", "hostlog-" + Guid.NewGuid().ToString("N")[..8]);
    private string LogPath => Path.Combine(_dir, "host.log");

    public HostLogTests()
    {
        Directory.CreateDirectory(_dir);
        HostLog.PathOverride = LogPath;
        HostLog.ResetExitReasonForTest();
    }

    public void Dispose()
    {
        HostLog.PathOverride = null;
        HostLog.ResetExitReasonForTest();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void FormatLine_CarriesPidEndpointAndVersion()
    {
        var line = HostLog.FormatLine(new DateTime(2026, 10, 7, 1, 2, 3, DateTimeKind.Utc), 123, "pipe:psbash-x", "0.11.2", "connection error: boom");
        Assert.Equal("2026-10-07T01:02:03.0000000Z pid=123 ep=pipe:psbash-x v=0.11.2 connection error: boom", line);
        Assert.Contains(" ep=- ", HostLog.FormatLine(DateTime.UtcNow, 1, null, "v", "m"));
    }

    [Fact]
    public void Write_AppendsTaggedLine_ForThisProcess()
    {
        HostLog.SetEndpoint("pipe", "psbash-hostlog-test");
        HostLog.Write("hello");
        var last = File.ReadAllLines(LogPath)[^1];
        Assert.Contains($" pid={Environment.ProcessId} ep=pipe:psbash-hostlog-test ", last);
        Assert.EndsWith(" hello", last);
    }

    [Fact]
    public void Write_RotatesBeyondMaxBytes()
    {
        File.WriteAllText(LogPath, new string('x', (int)HostLog.MaxBytes + 10));
        HostLog.Write("after rotation");
        Assert.True(File.Exists(LogPath + ".1"));
        Assert.True(new FileInfo(LogPath).Length < 1024);
    }

    [Fact]
    public void ExitReason_FirstWins()
    {
        HostLog.SetExitReason("launcher pid 9 exited");
        HostLog.SetExitReason("main returned (server stopped)");
        Assert.Equal("launcher pid 9 exited", HostLog.ExitReason);
    }

    [Fact]
    public async Task IdleShutdown_RecordsIdleTimeoutAsExitReason()
    {
        using var cts = new CancellationTokenSource();
        string? reason = null;
        using var idle = new IdleShutdown(cts, TimeSpan.FromMilliseconds(50), r => reason = r);
        await Task.Delay(TimeSpan.FromSeconds(10), cts.Token).ContinueWith(_ => { });
        Assert.True(cts.IsCancellationRequested);
        Assert.StartsWith("idle timeout", reason);
    }

    [Fact]
    public async Task ParentDeathWatcher_RecordsLauncherExitAsExitReason()
    {
        // A pid that is certainly gone: a process we started and waited for.
        var psi = OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c exit 0")
            : new System.Diagnostics.ProcessStartInfo("/bin/sh", "-c true");
        psi.UseShellExecute = false; psi.CreateNoWindow = true;
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
        int gonePid = p.Id;

        using var cts = new CancellationTokenSource();
        string? reason = null;
        await using var watcher = ParentDeathWatcher.TryCreate(gonePid, cts, TimeSpan.FromMilliseconds(20), r => reason = r);
        await Task.Delay(TimeSpan.FromSeconds(10), cts.Token).ContinueWith(_ => { });
        Assert.True(cts.IsCancellationRequested);
        Assert.Equal($"launcher pid {gonePid} exited", reason);
    }
}
