using System.Diagnostics;

namespace PsBash.Testing;

/// <summary>
/// Tells "slow because the machine is starved" from "stuck", so test waits need no
/// absolute wall-clock verdict. A fixed deadline fails a correct-but-slow run on a
/// loaded CI runner (one ps-bash invocation costs seconds of CPU; with the cores
/// saturated it finishes in 12-23 s instead of 3 s) and says nothing about whether the
/// child was alive. The deadline here is only a checkpoint: when it passes, the child
/// is still given more time for as long as the machine measurably is contended, and
/// declared stuck only once the machine is quiet and the child still has not finished.
/// <para>
/// Contention is a RATIO against this process's own best sample, so it needs no
/// calibrated constant and adapts to whatever hardware runs the suite.
/// </para>
/// </summary>
public static class ContentionProbe
{
    private const int SpinIterations = 2_000_000;

    // Fastest probe seen in this process = the uncontended cost on this machine.
    private static long _bestTicks = long.MaxValue;

    /// <summary>Contention above this ratio over the uncontended probe counts as starved.</summary>
    public const double StarvedRatio = 1.5;

    /// <summary>Never extend past this many multiples of the base deadline (hang backstop).</summary>
    public const int MaxExtensions = 20;

    /// <summary>True when the machine is measurably contended right now (<see cref="StarvedRatio"/>).</summary>
    public static async Task<bool> IsStarvedAsync() => await MeasureAsync() >= StarvedRatio;

    /// <summary>
    /// How many times slower than its fastest run a fixed CPU spin is right now
    /// (1.0 = idle machine). The spin runs on its own thread so a busy pool does not skew it.
    /// </summary>
    public static async Task<double> MeasureAsync()
    {
        var ticks = await Task.Factory.StartNew(
            () =>
            {
                var sw = Stopwatch.StartNew();
                double acc = 1;
                for (var i = 1; i <= SpinIterations; i++) acc += Math.Sqrt(i) / (acc + i);
                GC.KeepAlive(acc);
                return sw.ElapsedTicks;
            },
            TaskCreationOptions.LongRunning);

        long best;
        do
        {
            best = Interlocked.Read(ref _bestTicks);
            if (ticks >= best) break;
        } while (Interlocked.CompareExchange(ref _bestTicks, ticks, best) != best);

        return (double)ticks / Math.Min(best, ticks);
    }

    /// <summary>
    /// Waits for <paramref name="process"/> to exit. <paramref name="checkpoint"/> is how
    /// long a healthy run is expected to take on an idle machine; past it the wait keeps
    /// going only while the machine is starved. Returns false when the child is still
    /// running on a quiet machine (genuinely stuck) or the extension cap is reached.
    /// </summary>
    public static async Task<bool> WaitForExitAsync(Process process, TimeSpan checkpoint, bool extendWhenStarved = true)
    {
        // Sample before waiting so the baseline is taken as early (and as quiet) as possible.
        await MeasureAsync();
        var deadline = new CheckpointDeadline(checkpoint, extendWhenStarved ? null : static () => Task.FromResult(false));
        var exited = process.WaitForExitAsync();
        while (true)
        {
            using (var delayCts = new CancellationTokenSource())
            {
                var winner = await Task.WhenAny(exited, Task.Delay(deadline.Remaining, delayCts.Token));
                delayCts.Cancel();
                if (winner == exited) return true;
            }
            if (!await deadline.ContinueAsync()) return exited.IsCompleted;
        }
    }
}

/// <summary>
/// The ONE clock of a contention-aware wait. Its expiry is a checkpoint: <see cref="ContinueAsync"/> moves
/// the deadline out by a full window while the machine is starved (at most
/// <see cref="ContentionProbe.MaxExtensions"/> times) and says stop on a quiet machine.
/// <para>
/// A wait must not keep a second deadline beside this one. The interactive harness did: its pending read was
/// extended while starved, but a per-char <c>UtcNow &gt; deadline</c> check against the ORIGINAL deadline then
/// failed the wait on the first char of a prompt that arrived inside the extension
/// ("stdout received (1 chars) --- P"). ContentionAwareWaitGuardTests keeps every probe use behind this type.
/// </para>
/// </summary>
public sealed class CheckpointDeadline
{
    private readonly TimeSpan _window;
    private readonly Func<Task<bool>> _isStarved;
    private DateTime _deadline;

    /// <param name="window">How long a healthy run takes on an idle machine.</param>
    /// <param name="isStarved">Contention test; <see cref="ContentionProbe.IsStarvedAsync"/> when null.
    /// Pass <c>() =&gt; Task.FromResult(false)</c> for a strict, never-extended deadline.</param>
    public CheckpointDeadline(TimeSpan window, Func<Task<bool>>? isStarved = null)
    {
        _window = window;
        _isStarved = isStarved ?? ContentionProbe.IsStarvedAsync;
        _deadline = DateTime.UtcNow + window;
    }

    /// <summary>The base window (for messages).</summary>
    public TimeSpan Window => _window;

    /// <summary>How many times the deadline was moved out.</summary>
    public int Extensions { get; private set; }

    /// <summary>Time left before the next checkpoint (zero once it has passed).</summary>
    public TimeSpan Remaining
    {
        get
        {
            var left = _deadline - DateTime.UtcNow;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// True while the wait should go on: before the deadline, or past it on a starved machine (the deadline
    /// then moves out one window). False once the deadline has passed on a quiet machine or the cap is reached.
    /// </summary>
    public async Task<bool> ContinueAsync()
    {
        if (DateTime.UtcNow < _deadline) return true;
        if (Extensions >= ContentionProbe.MaxExtensions || !await _isStarved()) return false;
        Extensions++;
        _deadline = DateTime.UtcNow + _window;
        return true;
    }
}
