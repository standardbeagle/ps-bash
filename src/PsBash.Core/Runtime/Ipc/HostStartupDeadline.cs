using System.Diagnostics;

namespace PsBash.Core.Runtime.Ipc;

/// <summary>
/// The ONE clock a launcher keeps while a host comes up (docs/specs/host-lifecycle-contract.md, startup timeout).
/// Its expiry is a checkpoint, not a verdict: a host that is still starting (alive, and its CPU time advanced
/// since the last checkpoint) gets another full window, at most <see cref="MaxExtensions"/> times. A host that is
/// alive but idle at a checkpoint is stuck and fails then; a host that exits fails at once (callers check that
/// first). Without this a correct-but-slow cold start on a loaded machine (runspace init + module import compete
/// for every core) failed at the fixed deadline: "ps-bash-host did not accept connections within 15s", exit 125,
/// in a differential run whose oracle sets <c>PSBASH_TIMEOUT=15</c>, which the startup timeout inherits.
/// </summary>
internal sealed class HostStartupDeadline
{
    /// <summary>A host still burning CPU after this many extra windows is not starting, it is spinning.</summary>
    internal const int MaxExtensions = 3;

    private readonly TimeSpan _window;
    private readonly Func<DateTime> _now;
    private DateTime _deadline;
    private TimeSpan _cpuAtLastCheckpoint;

    public HostStartupDeadline(TimeSpan window, Func<DateTime>? utcNow = null)
    {
        _window = window;
        _now = utcNow ?? (static () => DateTime.UtcNow);
        _deadline = _now() + window;
    }

    /// <summary>How many times the deadline was moved out.</summary>
    public int Extensions { get; private set; }

    /// <summary>
    /// True while the launcher should keep waiting: before the deadline, or past it when
    /// <paramref name="hostCpu"/> (read only then) shows the starting host advanced since the last checkpoint,
    /// in which case the deadline moves out one window. Null CPU = no host to watch, or it is gone.
    /// </summary>
    public bool Continue(Func<TimeSpan?> hostCpu)
    {
        if (_now() < _deadline) return true;
        if (Extensions >= MaxExtensions || hostCpu() is not { } cpu || cpu <= _cpuAtLastCheckpoint) return false;
        _cpuAtLastCheckpoint = cpu;
        Extensions++;
        _deadline = _now() + _window;
        return true;
    }

    /// <summary>"within Ns" text for a timeout message, naming any extensions.</summary>
    public string Describe() => Extensions == 0
        ? $"{_window.TotalSeconds:0.##}s"
        : $"{_window.TotalSeconds:0.##}s (extended {Extensions}x while the host was still starting)";

    /// <summary>Total CPU time of a host process, or null when it has exited or cannot be read.</summary>
    internal static TimeSpan? CpuOf(Process? process)
    {
        if (process is null) return null;
        try
        {
            process.Refresh();
            return process.HasExited ? null : process.TotalProcessorTime;
        }
        catch { return null; }
    }

    /// <summary>Total CPU time of the process <paramref name="id"/> names (start time guards a recycled pid).</summary>
    internal static TimeSpan? CpuOf(ProcessIdentity? id)
    {
        if (id is not { } identity) return null;
        try
        {
            using var proc = Process.GetProcessById(identity.Pid);
            if (ProcessIdentity.StartTicksOf(proc) != identity.StartTimeUtcTicks) return null;
            return CpuOf(proc);
        }
        catch { return null; }
    }

    /// <summary>Total CPU time of process <paramref name="pid"/>, when it is running.</summary>
    internal static TimeSpan? CpuOfPid(int pid)
    {
        if (pid <= 0) return null;
        try
        {
            using var proc = Process.GetProcessById(pid);
            return CpuOf(proc);
        }
        catch { return null; }
    }
}
