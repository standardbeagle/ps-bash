using System.Text;

namespace PsBash.Host.Runtime;

/// <summary>
/// Coalesces host stdout writes so the launcher receives a few large IPC frames
/// instead of one frame per line (fan-out epic 01KYKGFBY4WCHRNZ10R650H57B, slice S1).
///
/// <para><b>Why.</b> <see cref="SdkWorker"/> delivered output one call per item, and each
/// call becomes one IPC frame. The S0 baseline measured that per-line framing at roughly
/// 1 ms/line — the dominant end-to-end cost. Crucially this is the ONLY lever that helps a
/// pipeline whose producer we do not own (a database query, any <c>Get-*</c>, any external
/// command): the fused-pipeline lane requires EVERY stage to be an allowlisted
/// <c>Invoke-Bash*</c>, so those pipelines never reach it. Batching here is independent of
/// the emitter and of the plan lane, and applies to every pipeline shape.</para>
///
/// <para><b>Byte fidelity.</b> Each appended string already carries its own trailing
/// newline (<c>SdkWorker.GetOutputText</c> appends one per item; the formatter appends one
/// per row), and the launcher writes a frame's payload verbatim via <c>Console.Write</c>.
/// Concatenation is therefore byte-identical to the N-frame stream by construction — this
/// class never inserts, removes, or rewrites a single character.</para>
///
/// <para><b>Latency.</b> A size threshold ALONE would be wrong: a slow trickle
/// (<c>tail -f</c>, a progress line every second, an interactive prompt) would sit in the
/// buffer indefinitely and the shell would look hung. So the buffer also carries a
/// deadline. The timer is armed on the empty→non-empty transition and disarmed on flush,
/// NOT re-armed per append — that makes it one timer operation per batch rather than per
/// line (re-arming per line would reintroduce the very per-line syscall cost this class
/// exists to remove) and gives a hard upper bound on visible latency rather than a
/// quiet-period heuristic.</para>
///
/// <para><b>Ordering.</b> The sink is invoked while holding the lock. That is deliberate:
/// two concurrent flushes must never interleave, or a batch boundary would reorder output.
/// Callers that write to a DIFFERENT stream (stderr) must call <see cref="Flush"/> first,
/// so stdout/stderr relative order survives batching — see <c>SdkWorker.RunCommand</c>.</para>
/// </summary>
internal sealed class OutputBatcher : IDisposable
{
    /// <summary>Flush once the buffer reaches this many chars. 32 KiB matches
    /// <c>InvokeBashFusedPipelineCommand.FlushThresholdChars</c>, which has shipped with
    /// this batch size, so the two lanes produce the same frame granularity.</summary>
    internal const int DefaultThresholdChars = 32 * 1024;

    /// <summary>Maximum time buffered output may remain invisible. Small enough that a
    /// human reads it as immediate, large enough that a fast producer fills the size
    /// threshold long before it fires.</summary>
    internal static readonly TimeSpan DefaultDeadline = TimeSpan.FromMilliseconds(50);

    private readonly Action<string> _sink;
    private readonly int _thresholdChars;
    private readonly TimeSpan _deadline;
    private readonly StringBuilder _buffer;
    private readonly object _gate = new();
    private readonly System.Threading.Timer? _timer;
    private bool _armed;
    private bool _disposed;

    /// <param name="sink">Receives each coalesced batch. Called under the lock.</param>
    /// <param name="thresholdChars">Size trigger; see <see cref="DefaultThresholdChars"/>.</param>
    /// <param name="deadline">Latency bound, or <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>
    /// to disable the timer entirely (tests drive <see cref="FlushDueToDeadline"/> directly,
    /// which keeps them deterministic — the QA rubric forbids sleeping in tests).</param>
    internal OutputBatcher(Action<string> sink, int thresholdChars, TimeSpan deadline)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _thresholdChars = thresholdChars > 0 ? thresholdChars : DefaultThresholdChars;
        _deadline = deadline;
        _buffer = new StringBuilder(_thresholdChars + 4096);
        if (deadline != System.Threading.Timeout.InfiniteTimeSpan)
        {
            _timer = new System.Threading.Timer(
                _ => FlushDueToDeadline(), null,
                System.Threading.Timeout.InfiniteTimeSpan,
                System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Buffer one already-newline-terminated chunk, flushing if it crosses the
    /// size threshold. Never rewrites the text.</summary>
    internal void Append(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_gate)
        {
            if (_disposed)
            {
                // Post-dispose write (a late Out-Default forwarder call). Pass it
                // straight through rather than dropping it — losing output is worse
                // than an unbatched write on a path that should not happen.
                _sink(text);
                return;
            }

            _buffer.Append(text);

            if (_buffer.Length >= _thresholdChars)
            {
                FlushLocked();
                return;
            }

            // Arm on the empty->non-empty transition only. See the class remarks:
            // re-arming per append would cost a timer syscall per line.
            if (!_armed && _timer is not null)
            {
                _armed = true;
                _timer.Change(_deadline, System.Threading.Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>Emit anything buffered. Safe to call when empty. MUST be called before
    /// writing to another stream (stderr) and before the command's exit sentinel.</summary>
    internal void Flush()
    {
        lock (_gate) FlushLocked();
    }

    /// <summary>The deadline expiry path. Internal rather than private so tests can drive
    /// it without waiting on a real timer.</summary>
    internal void FlushDueToDeadline()
    {
        lock (_gate)
        {
            _armed = false;
            FlushLocked();
        }
    }

    /// <summary>True when output is currently held back. Test/diagnostic seam.</summary>
    internal bool HasBuffered
    {
        get { lock (_gate) return _buffer.Length > 0; }
    }

    private void FlushLocked()
    {
        if (_armed && _timer is not null)
        {
            _timer.Change(System.Threading.Timeout.InfiniteTimeSpan,
                          System.Threading.Timeout.InfiniteTimeSpan);
            _armed = false;
        }

        if (_buffer.Length == 0) return;

        var batch = _buffer.ToString();
        _buffer.Clear();

        // Under the lock on purpose (ordering). If the sink throws — the IPC stream
        // closed — the buffer has already been cleared, so a later flush cannot
        // re-emit these bytes. SdkWorker's sink swallows and stops the pipeline.
        _sink(batch);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            FlushLocked();       // never lose buffered output
            _disposed = true;
        }
        _timer?.Dispose();
    }
}
