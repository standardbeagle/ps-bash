using System.Diagnostics;
using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// The clock of one in-process background job (docs/specs/background-jobs.md section 12.5): its sleeps are measured
/// from the moment <c>&amp;</c> ran, not from the moment its body began. Bash forks the job at <c>&amp;</c> and its first
/// statement runs a millisecond later; here the body begins after a runspace bind, the pipeline's start and the
/// launch-order turn, 2-10 ms (more on a loaded host) that a <c>sleep 0.01</c> race cannot always absorb. That start lag
/// is the job's debt: each <c>sleep D</c> sleeps <c>max(0, D - debt)</c> and pays that much of it, and a debt larger
/// than D carries to the next sleep. Only the lag before the body counts: time the body itself spends is never debt, so
/// a job ends at <c>launch + its own time</c> whenever its sleeps can absorb the lag, and a job with no sleep is
/// untouched.
///
/// <para>Created at <c>&amp;</c> by <see cref="BashJobRunspaces.NewJob"/>, stored in the job runspace's global
/// <see cref="VariableName"/> by the launcher before the job starts (removed with the job's other globals by
/// <see cref="BashJobRunspaces.EndJob"/>), begun by <c>Enter-BashJobTurn</c> as the body starts, read by
/// <c>Invoke-BashSleep</c>. The foreground shell and a job in a host of its own have none, so their sleeps are exact.</para>
/// </summary>
public sealed class JobClock
{
    /// <summary>The job runspace's global variable that holds its clock.</summary>
    public const string VariableName = "__psbashJobClock";

    private readonly object _gate = new();
    private readonly long _launched;
    // Start lag still unpaid, in Stopwatch ticks; -1 until the body begins.
    private long _debt = -1;

    internal JobClock(long launchedTimestamp) => _launched = launchedTimestamp;

    /// <summary>The job's launch-order turn (<see cref="BashJobRunspaces.JobTurn"/>), or null.</summary>
    internal BashJobRunspaces.JobTurn? Turn { get; set; }

    /// <summary>
    /// The job is about to block (a <c>sleep</c>): the job launched after it may begin. In bash it would be running by
    /// now; holding it for the grace would only add start lag.
    /// </summary>
    public void Blocking() => Turn?.Pass();

    /// <summary>A clock for a job launched now.</summary>
    public static JobClock Launch() => new(Stopwatch.GetTimestamp());

    /// <summary>The job's body begins now: the time since the launch becomes its debt. Only the first call counts.</summary>
    public void Begin() => Begin(Stopwatch.GetTimestamp());

    internal void Begin(long nowTimestamp)
    {
        lock (_gate)
        {
            if (_debt < 0) _debt = Math.Max(0, nowTimestamp - _launched);
        }
    }

    /// <summary>
    /// How long a <c>sleep</c> of <paramref name="requestedMs"/> sleeps in this job: the request less the unpaid debt,
    /// which the sleep pays off. Begins the clock if the body never did.
    /// </summary>
    public long Settle(long requestedMs) => Settle(requestedMs, Stopwatch.GetTimestamp());

    internal long Settle(long requestedMs, long nowTimestamp)
    {
        lock (_gate)
        {
            if (_debt < 0) _debt = Math.Max(0, nowTimestamp - _launched);
            // A sleep of months: the lag is noise, and the tick count would overflow.
            if (requestedMs >= long.MaxValue / Stopwatch.Frequency) return requestedMs;
            var (sleepTicks, left) = Pay(MsToTicks(requestedMs), _debt);
            _debt = left;
            return TicksToMsCeiling(sleepTicks);
        }
    }

    /// <summary>The debt still unpaid, in milliseconds (rounded up); -1 before the body began.</summary>
    internal long DebtMs { get { lock (_gate) return _debt < 0 ? -1 : TicksToMsCeiling(_debt); } }

    /// <summary>A sleep of <paramref name="requested"/> against <paramref name="debt"/>: what it sleeps and what is left owed.</summary>
    internal static (long Sleep, long DebtLeft) Pay(long requested, long debt) =>
        debt >= requested ? (0, debt - requested) : (requested - debt, 0);

    /// <summary>The clock of the job running in <paramref name="session"/>'s runspace, or null outside a job.</summary>
    public static JobClock? Current(SessionState session)
    {
        object? value;
        try { value = session.PSVariable.GetValue(VariableName); }
        catch (Exception) { return null; }
        return (value is PSObject p ? p.BaseObject : value) as JobClock;
    }

    private static long MsToTicks(long ms) => ms * Stopwatch.Frequency / 1000;

    private static long TicksToMsCeiling(long ticks) => (ticks * 1000 + Stopwatch.Frequency - 1) / Stopwatch.Frequency;
}
