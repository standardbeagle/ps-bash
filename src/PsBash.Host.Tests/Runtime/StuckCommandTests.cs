using System.Text.RegularExpressions;
using PsBash.Host.Runtime;
using Xunit;

namespace PsBash.Host.Tests.Runtime;

/// <summary>
/// A command that cannot be stopped used to wedge the shared host: it held the process-wide exec gate
/// after its launcher left, so every later command hung (`grep -E '(a+)+$'` pegged a host for 26 min
/// while `echo alive` waited behind it). Two layers: a default regex match timeout prevents the common
/// cause; the stuck-command backstop poisons (exits) a host whose cancelled command never returns.
/// Serialized: the watchdog's Enabled/Grace/Terminate are process-wide seams.
/// </summary>
[Collection(nameof(StuckCommandTests))]
[CollectionDefinition(nameof(StuckCommandTests), DisableParallelization = true)]
public class StuckCommandTests : IDisposable
{
    private readonly bool _enabled = StuckCommandWatchdog.Enabled;
    private readonly TimeSpan _grace = StuckCommandWatchdog.Grace;
    private readonly Action<string> _terminate = StuckCommandWatchdog.Terminate;

    public void Dispose()
    {
        StuckCommandWatchdog.Enabled = _enabled;
        StuckCommandWatchdog.Grace = _grace;
        StuckCommandWatchdog.Terminate = _terminate;
    }

    [Fact]
    public async Task CancelledCommandThatNeverStops_PoisonsTheHost()
    {
        var poisoned = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        StuckCommandWatchdog.Enabled = true;
        StuckCommandWatchdog.Grace = TimeSpan.FromMilliseconds(100);
        StuckCommandWatchdog.Terminate = r => poisoned.TrySetResult(r);

        var never = new TaskCompletionSource<int>().Task;
        StuckCommandWatchdog.OnCancelled(never, "command");

        var reason = await poisoned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("did not stop within", reason);
        Assert.Contains("exec gate", reason);
    }

    [Fact]
    public async Task CancelledCommandThatStops_DoesNotPoison()
    {
        bool poisoned = false;
        StuckCommandWatchdog.Enabled = true;
        StuckCommandWatchdog.Grace = TimeSpan.FromMilliseconds(300);
        StuckCommandWatchdog.Terminate = _ => poisoned = true;

        var stops = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = StuckCommandWatchdog.OnCancelled(stops.Task, "command");
        stops.SetResult(130);                 // the command honoured Stop
        await watch.WaitAsync(TimeSpan.FromSeconds(10)); // the watchdog's own decision, not a sleep
        Assert.False(poisoned);
    }

    [Fact]
    public void Disabled_NeverPoisons()
    {
        bool poisoned = false;
        StuckCommandWatchdog.Enabled = false;
        StuckCommandWatchdog.Grace = TimeSpan.Zero;
        StuckCommandWatchdog.Terminate = _ => poisoned = true;
        StuckCommandWatchdog.OnCancelled(new TaskCompletionSource<int>().Task, "command");
        Assert.False(poisoned);
    }

    [Theory]
    [InlineData(null, 15.0)]
    [InlineData("5", 5.0)]
    [InlineData("2.5", 2.5)]
    [InlineData("garbage", 15.0)]
    [InlineData("-1", 15.0)]
    public void RegexTimeout_Resolve(string? env, double expectedSecs)
        => Assert.Equal(TimeSpan.FromSeconds(expectedSecs), HostRegexTimeout.Resolve(env));

    [Fact]
    public void RegexTimeout_ZeroDisables() => Assert.Null(HostRegexTimeout.Resolve("0"));

    [Theory]
    [InlineData("600", 0L, 600L * 1024 * 1024)]
    [InlineData("0", 8L << 30, 0L)]                 // explicit 0 disables
    [InlineData(null, 8L << 30, 4L << 30)]          // default: half of what is available
    [InlineData("garbage", 8L << 30, 4L << 30)]
    public void MemoryLimit_Resolve(string? env, long available, long expected)
        => Assert.Equal(expected, HostMemoryGuard.ResolveLimit(env, available));

    [Fact]
    public void MemoryGuard_OverLimit_CancelsTheCommandOnce()
    {
        var priorLimit = HostMemoryGuard.LimitBytes;
        var priorRead = HostMemoryGuard.ReadUsage;
        try
        {
            HostMemoryGuard.LimitBytes = 1000;
            long usage = 10;
            HostMemoryGuard.ReadUsage = () => usage;
            using var cts = new CancellationTokenSource();
            using var guard = HostMemoryGuard.Watch(cts);

            guard.Check();
            Assert.False(guard.Breached);
            Assert.False(cts.IsCancellationRequested);

            usage = 5000;
            guard.Check();
            Assert.True(guard.Breached);
            Assert.True(cts.IsCancellationRequested);
            Assert.Contains("memory limit", guard.Message);
        }
        finally
        {
            HostMemoryGuard.LimitBytes = priorLimit;
            HostMemoryGuard.ReadUsage = priorRead;
        }
    }

    [Fact]
    public void CatastrophicPattern_WithTheHostTimeout_FailsInsteadOfRunningForever()
    {
        // The exact grep probe: exponential backtracking. With the host's timeout applied it throws
        // promptly (the process-wide default is read once, so this pins an explicit instance).
        var rx = new Regex("(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(500));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<RegexMatchTimeoutException>(() => rx.Match(new string('a', 40) + "!"));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }
}
