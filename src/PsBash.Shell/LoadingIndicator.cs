using System.Diagnostics;

namespace PsBash.Shell;

// Immediate, reassuring status output during shell startup. Writes to stderr so
// it never pollutes a piped stdout. Shows:
//   * a spinner frame (animated on a TTY)
//   * the current stage label
//   * elapsed seconds for the *current* stage (so the user can see whether
//     anything is progressing)
//
// After StallThreshold of no stage change, prints a hint line suggesting the
// user bail. We intentionally do NOT kill any child process on a timer: a slow
// profile is the user's profile. Reliability comes from visibility, not from
// guessing when to pull the plug.
internal sealed class LoadingIndicator : IDisposable
{
    private static readonly char[] Frames = ['|', '/', '-', '\\'];
    private static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(60);

    private readonly bool _enabled;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _spinnerTask;
    private readonly Stopwatch _stageTimer = Stopwatch.StartNew();
    private string _message;
    private int _frame;
    private int _lastRenderedLength;
    private int _stallNotificationsEmitted;
    private bool _finished;
    private readonly object _lock = new();

    private LoadingIndicator(string message, bool enabled)
    {
        _message = message;
        _enabled = enabled;
        _spinnerTask = enabled ? Task.Run(TickAsync) : Task.CompletedTask;
    }

    public static LoadingIndicator Start(string message)
    {
        bool enabled = !Console.IsErrorRedirected;
        var ind = new LoadingIndicator(message, enabled);
        ind.Render();
        return ind;
    }

    public void Update(string message)
    {
        lock (_lock)
        {
            if (_finished) return;
            _message = message;
            _stageTimer.Restart();
            _stallNotificationsEmitted = 0;
            Render();
        }
    }

    public void Finish()
    {
        lock (_lock)
        {
            if (_finished) return;
            _finished = true;
        }
        _cts.Cancel();
        try { _spinnerTask.Wait(500); } catch { }
        Clear();
    }

    public void Dispose() => Finish();

    private async Task TickAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(120, _cts.Token);
                lock (_lock)
                {
                    if (_finished) break;
                    _frame = (_frame + 1) % Frames.Length;
                    MaybeEmitStallHint();
                    Render();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* never let the indicator crash startup */ }
    }

    // Lock must be held.
    private void MaybeEmitStallHint()
    {
        var elapsed = _stageTimer.Elapsed;
        var expected = TimeSpan.FromTicks(StallThreshold.Ticks * (_stallNotificationsEmitted + 1));
        if (elapsed < expected) return;

        _stallNotificationsEmitted++;
        // Clear the spinner line, print a persistent hint on its own line,
        // then let Render() redraw the spinner below it.
        Clear();
        var mins = (int)elapsed.TotalMinutes;
        Console.Error.WriteLine(
            $"[ps-bash] still waiting on \"{_message}\" ({mins} min). " +
            "Press Ctrl+C to abort, or restart with --no-profile to skip profile loading.");
    }

    // Lock must be held.
    private void Render()
    {
        var elapsed = _stageTimer.Elapsed;
        var elapsedText = elapsed.TotalSeconds < 10
            ? $"{elapsed.TotalSeconds:0.0}s"
            : $"{(int)elapsed.TotalSeconds}s";

        if (!_enabled)
        {
            // Non-interactive: emit one line per stage, no overwrite.
            if (_lastRenderedLength == 0)
            {
                Console.Error.WriteLine($"[ps-bash] {_message}...");
                _lastRenderedLength = 1;
            }
            return;
        }

        var text = $"[ps-bash] {Frames[_frame]} {_message}... [{elapsedText}]";
        var pad = Math.Max(0, _lastRenderedLength - text.Length);
        Console.Error.Write('\r' + text + new string(' ', pad));
        _lastRenderedLength = text.Length;
    }

    // Lock must be held.
    private void Clear()
    {
        if (!_enabled || _lastRenderedLength == 0) return;
        Console.Error.Write('\r' + new string(' ', _lastRenderedLength) + '\r');
        _lastRenderedLength = 0;
    }
}
