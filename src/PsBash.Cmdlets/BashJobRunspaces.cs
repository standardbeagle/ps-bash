using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Runtime.CompilerServices;

namespace PsBash.Cmdlets;

/// <summary>
/// The runspaces in-process background jobs (<c>cmd &amp;</c>) run in: one set per process, opened ahead of use and
/// reused by later jobs and later commands (docs/specs/background-jobs.md section 12.5).
/// <para>
/// Opening a job runspace (a runspace that imports the module) takes hundreds of milliseconds. Each command used to
/// open a private RunspacePool at its first <c>&amp;</c> and the pool opened one more runspace for each job that found
/// the others busy, so a job started up to that long after its <c>&amp;</c>, and jobs launched 10 ms apart started out of
/// order. <see cref="Bind"/> gives a job a runspace that is already open: <see cref="Spares"/> are opened ahead on a
/// background thread, and a runspace whose job is done goes back to the idle set (up to <see cref="MaxIdle"/>). When none
/// is idle the launcher waits for the one the warm-up thread is opening, or, when none is opening, opens one itself; jobs
/// still start in launch order, late.
/// </para>
/// <para>
/// A runspace goes back to the idle set only when its job completed and left it clean (<see cref="EndJob"/>): the
/// globals the job created are removed; a job that changed a function or alias, or started jobs of its own, or was
/// stopped, leaves a runspace that is closed instead.
/// </para>
/// </summary>
public static class BashJobRunspaces
{
    private static readonly object Gate = new();
    // A job's scalar store is keyed by its runspace: the one PowerShell runs the calling thread's pipeline in.
    static BashJobRunspaces() => PsBash.Core.ScalarOverlay.CurrentOwner = static () => Runspace.DefaultRunspace;

    private static readonly List<Runspace> Idle = [];
    private static readonly ConditionalWeakTable<Runspace, Baseline> Baselines = new();
    private static string? _modulePath;
    private static bool _warming;
    private static int _owned; // open runspaces held here: idle + running a job

    /// <summary>Runspaces opened ahead of the first jobs (<c>PSBASH_JOB_SPARES</c>, 0-16, default 4).</summary>
    public static int Spares { get; } = ReadSpares();

    /// <summary>The most runspaces kept idle once their jobs are done (a job runspace holds about 4 MB).</summary>
    public static int MaxIdle => Math.Max(Spares, 8);

    /// <summary>Test seam: runspaces opened by the launcher itself because no spare was ready.</summary>
    public static int ColdOpens => _coldOpens;
    private static int _coldOpens;

    /// <summary>Idle runspaces right now (test seam).</summary>
    public static int IdleCount { get { lock (Gate) return Idle.Count; } }

    private static int ReadSpares()
        => int.TryParse(Environment.GetEnvironmentVariable("PSBASH_JOB_SPARES"), out var n) && n is >= 0 and <= 16 ? n : 4;

    /// <summary>
    /// The in-process job of <c>Invoke-BashBackground</c>, built and bound, not started: <paramref name="head"/>, the
    /// parent's options (<see cref="BashStateSnapshot.BuildJobOptions"/>), the launch-time values of the loop variables
    /// the body reads (<paramref name="vars"/>), <c>Enter-BashJobTurn</c> (launch order, <see cref="JobTurn"/>), the body,
    /// <paramref name="tail"/>; arguments status, restore, stdin, vars (the job's <c>BashVariableStore</c> snapshot), turn,
    /// clock (<see cref="JobClock"/>, launched once the job has its runspace: the head stores it in the job's global <see cref="JobClock.VariableName"/>).
    /// One call, because every script-to-.NET call on the launch path is an AMSI report under PowerShell's process-wide
    /// lock (docs/specs/background-jobs.md section 12.4) that the job launched just before waits on to start.
    /// </summary>
    public static PowerShell NewJob(SessionState session, string head, ScriptBlock body, string tail, System.Collections.IDictionary? vars,
        object status, bool restore, string? modulePath)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(body);
        var text = new System.Text.StringBuilder(head).Append('\n').Append(BashStateSnapshot.BuildJobOptions(session));
        if (vars is { Count: > 0 })
        {
            bool first = true;
            foreach (System.Collections.DictionaryEntry e in vars)
            {
                if (!first) text.Append("; ");
                first = false;
                text.Append('$').Append(e.Key).Append(" = ").Append(PsBash.Core.Parser.PsBuild.SingleQuote(LanguagePrimitives.ConvertTo<string>(e.Value) ?? ""));
            }
            text.Append('\n');
        }
        // The body begins only after the job launched before it from the same runspace has begun (JobTurn); its clock
        // begins there.
        text.Append("Enter-BashJobTurn $__bgTurn $__bgClock\n");
        text.Append(body.ToString()).Append('\n').Append(tail);
        var turn = JobTurn.Next(Runspace.DefaultRunspace);
        // The job's scalars as of the `&` (section 3): copied here, on the launching thread, before the shell goes on.
        var scalars = PsBash.Core.ScalarOverlay.Capture();
        var ps = PowerShell.Create();
        try
        {
            ps.AddScript(text.ToString()).AddArgument(status).AddArgument(restore).AddArgument(BackgroundJobStdin.Inherit(session))
                .AddArgument(vars).AddArgument(turn);
            Bind(ps, modulePath, turn);
            scalars.Attach(ps.Runspace);
            // The launch: the job's sleeps count from here (JobClock), as a forked job's do in bash. After the bind: time the
            // launcher spent waiting for a runspace delays the shell as much as the job, so it is not the job's to pay back.
            ps.AddArgument(JobClock.Launch());
            return ps;
        }
        catch
        {
            if (ps.Runspace is { } bound) PsBash.Core.ScalarOverlay.Detach(bound);
            ps.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Run <paramref name="ps"/> (not started yet) in a ready job runspace. The launch path: it only takes a runspace from
    /// the idle set, unless none is idle, when it opens one itself.
    /// <paramref name="modulePath"/> is the PsBash.psm1 a job runspace imports.
    /// </summary>
    public static void Bind(PowerShell ps, string? modulePath) => Bind(ps, modulePath, null);

    private static void Bind(PowerShell ps, string? modulePath, JobTurn? turn)
    {
        ArgumentNullException.ThrowIfNull(ps);
        if (!string.IsNullOrEmpty(modulePath)) Interlocked.CompareExchange(ref _modulePath, modulePath, null);
        var rs = TakeIdle() ?? TakeWhenWarmed();
        if (rs is null)
        {
            Interlocked.Increment(ref _coldOpens);
            rs = Open();
        }
        ps.Runspace = rs;
        int released = 0;
        ps.InvocationStateChanged += (_, e) =>
        {
            var state = e.InvocationStateInfo.State;
            if (state is PSInvocationState.NotStarted or PSInvocationState.Running or PSInvocationState.Stopping) return;
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            // A job that ended before its body began (stopped, failed to compile) must not hold up the next one.
            turn?.Pass();
            Release(rs, reusable: state == PSInvocationState.Completed);
        };
        // Where the host did not warm ahead (a plain module import, a one-shot host), the first job starts it.
        WarmAhead(null);
    }

    /// <summary>
    /// Open runspaces in the background until <see cref="Spares"/> are held (idle or running a job). The daemon host calls
    /// it when a command that uses <c>&amp;</c> is about to run (not at start: opens slow the commands beside them), so
    /// that command's jobs find them ready or opening; a runspace closed because its job left it dirty is replaced the
    /// same way. Taking one does not open another: an open compiles the psm1 and holds the
    /// process-wide AMSI lock for much of that (section 12.4), and opening beside the jobs being launched delayed each of
    /// their starts by about 100 ms (measured).
    /// </summary>
    public static void WarmAhead(string? modulePath)
    {
        if (!string.IsNullOrEmpty(modulePath)) Interlocked.CompareExchange(ref _modulePath, modulePath, null);
        // Without the module a runspace cannot run a job body (Enter-BashJobTurn, the Invoke-Bash* commands are missing).
        if (Volatile.Read(ref _modulePath) is null) return;
        lock (Gate)
        {
            if (_warming || _owned >= Spares) return;
            _warming = true;
        }
        // One thread opens them one after another: concurrent opens compete for the CPU and for PowerShell's
        // process-wide AMSI lock with the command that is running.
        new Thread(WarmLoop) { IsBackground = true, Name = "psbash-job-runspace-warm" }.Start();
    }

    private static void WarmLoop()
    {
        try
        {
            while (true)
            {
                lock (Gate)
                {
                    if (_owned >= Spares) return;
                }
                var rs = Open();
                lock (Gate)
                {
                    Idle.Add(rs);
                    Monitor.PulseAll(Gate);
                }
            }
        }
        catch (Exception)
        {
            // A spare that cannot open is the cold path's problem to report: Bind opens one and the job's start fails there.
        }
        finally
        {
            lock (Gate)
            {
                _warming = false;
                Monitor.PulseAll(Gate);
            }
        }
    }

    /// <summary>
    /// No runspace is idle but the warm-up thread is opening one: wait for it rather than open another beside it. Two
    /// opens at once compete for the CPU and the AMSI lock and each took about twice as long (a fresh host's first job:
    /// 2.3-3.0 s beside the warm-up's open vs 1.1-1.8 s alone, measured), so the job starts sooner this way, and the
    /// host does not end up with a runspace more than <see cref="Spares"/>. Null when nothing is warming
    /// (every runspace held is running a job) or the warm-up gives up: the launcher then opens one itself.
    /// </summary>
    private static Runspace? TakeWhenWarmed()
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            lock (Gate)
            {
                while (Idle.Count == 0)
                {
                    var left = WarmWaitBound - waited.Elapsed;
                    if (!_warming || left <= TimeSpan.Zero) return null;
                    Monitor.Wait(Gate, left);
                }
            }
            // Another launcher may take it first; then wait for the next one.
            var rs = TakeIdle();
            if (rs is not null) return rs;
        }
    }

    // Far above an open (0.2-4 s measured on a loaded host): it only ends a wait on a warm-up that stopped making progress.
    private static readonly TimeSpan WarmWaitBound = TimeSpan.FromSeconds(30);

    private static Runspace? TakeIdle()
    {
        Runspace? busy = null;
        lock (Gate)
        {
            for (int i = 0; i < Idle.Count; i++)
            {
                var rs = Idle[i];
                if (rs.RunspaceStateInfo.State != RunspaceState.Opened) { Idle.RemoveAt(i--); CloseLater(rs); continue; }
                if (rs.RunspaceAvailability == RunspaceAvailability.Available) { Idle.RemoveAt(i); return rs; }
                busy ??= rs;
            }
            // A runspace whose job has just completed is put back before PowerShell marks it available.
            if (busy is not null) Idle.Remove(busy);
        }
        if (busy is not null && SpinWait.SpinUntil(() => busy.RunspaceAvailability == RunspaceAvailability.Available, 200))
            return busy;
        if (busy is not null) CloseLater(busy);
        return null;
    }

    private static void Release(Runspace rs, bool reusable)
    {
        PsBash.Core.ScalarOverlay.Detach(rs);
        reusable = reusable && rs.RunspaceStateInfo.State == RunspaceState.Opened
            && Baselines.TryGetValue(rs, out var baseline) && baseline.Clean;
        if (reusable)
        {
            lock (Gate)
            {
                if (Idle.Count < MaxIdle) { Idle.Add(rs); return; }
            }
        }
        // Not from the job's own thread: closing waits for the pipeline that raised this event.
        CloseLater(rs);
        if (!reusable) WarmAhead(null);
    }

    private static void CloseLater(Runspace rs)
    {
        lock (Gate) _owned--;
        ThreadPool.QueueUserWorkItem(static r => { try { ((Runspace)r!).Dispose(); } catch (Exception) { } }, rs);
    }

    /// <summary>The session state a job runspace is opened with (Windows: Bypass, as the shell's own runspace).</summary>
    public static InitialSessionState InitialState(string? modulePath)
    {
        var iss = InitialSessionState.CreateDefault();
        // Like the shell's own runspace (SdkRunspace): without it the runspace takes the machine policy and, where that
        // resolves to Restricted, Open() cannot load the psm1 ("running scripts is disabled") and every `cmd &` fails.
        // Windows only: elsewhere setting it throws PlatformNotSupportedException at Open().
        if (OperatingSystem.IsWindows()) iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        // `$env:NAME` is the job's own scalar store, not the process environment the foreground shares (section 3).
        // By index: CreateDefault's entries come from the core snap-in, and Remove(name, type) leaves them in place (the
        // Env drive then stays PowerShell's, measured).
        for (int i = iss.Providers.Count - 1; i >= 0; i--)
            if (iss.Providers[i].Name == "Environment") iss.Providers.RemoveItem(i);
        iss.Providers.Add(new SessionStateProviderEntry(JobEnvironmentProvider.ProviderName, typeof(JobEnvironmentProvider), null));
        if (!string.IsNullOrEmpty(modulePath) && File.Exists(modulePath)) iss.ImportPSModule([modulePath]);
        return iss;
    }

    private static Runspace Open()
    {
        // No host: a job's `exit N` calls PSHost.SetShouldExit, which on the shell's own host ended the whole script.
        var rs = RunspaceFactory.CreateRunspace(InitialState(Volatile.Read(ref _modulePath)));
        // One pipeline thread for the runspace's life. The default (a new thread per invocation) made BeginInvoke take
        // 40-100 ms and the job's first statement run 35-150 ms after it on a loaded host; reused, 0.3-10 and 2-30 ms.
        rs.ThreadOptions = PSThreadOptions.ReuseThread;
        try
        {
            rs.Open();
            JobProgramLookup.Install(rs.SessionStateProxy.InvokeCommand);
            Baselines.AddOrUpdate(rs, Baseline.Capture(rs.SessionStateProxy.InvokeProvider));
            lock (Gate) _owned++;
            return rs;
        }
        catch
        {
            rs.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The job's last statement (its wrapper's <c>finally</c>): remove the globals the job created and restore the ones
    /// it changed, so the next job does not see them. A job that changed a function or alias, or started jobs of its
    /// own (<c>$global:BashBgLastPid</c> set), leaves a runspace that is closed instead of reused.
    /// </summary>
    public static void EndJob(SessionState session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var rs = Runspace.DefaultRunspace;
        if (rs is null || !Baselines.TryGetValue(rs, out var baseline)) return;
        try { baseline.Clean = baseline.Restore(session); }
        catch (Exception) { baseline.Clean = false; }
    }

    /// <summary>
    /// The place of an in-process job among the jobs launched from the same runspace. In bash a job launched first
    /// begins first (the parent forks them in order); here each job's pipeline compiles on its own thread and waits on
    /// PowerShell's process-wide AMSI lock (section 12.4), which is not handed out in arrival order, so with ready
    /// runspaces <c>echo word_a &amp; echo word_b &amp;</c> printed word_b first. <c>Enter-BashJobTurn</c>, the statement
    /// before the job's body (a cmdlet call: no AMSI report), waits until every job launched before it has begun its
    /// body or ended. A wait gives up after <see cref="MaxWait"/> (a job whose start was refused never begins).
    /// </summary>
    public sealed class JobTurn
    {
        private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(50);
        private static readonly ConditionalWeakTable<Runspace, Order> Orders = new();

        private readonly Order _order;
        private readonly long _ticket;
        private int _passed;

        private JobTurn(Order order, long ticket)
        {
            _order = order;
            _ticket = ticket;
        }

        /// <summary>The next turn of jobs launched from <paramref name="launcher"/>; none without a runspace.</summary>
        internal static JobTurn? Next(Runspace? launcher)
        {
            if (launcher is null) return null;
            var order = Orders.GetValue(launcher, static _ => new Order());
            lock (order) return new JobTurn(order, ++order.Issued);
        }

        /// <summary>Wait (bounded, ends early when <paramref name="stopping"/>) for the earlier jobs, then begin.</summary>
        internal void Enter(Func<bool> stopping)
        {
            var waited = System.Diagnostics.Stopwatch.StartNew();
            lock (_order)
            {
                while (_order.Through < _ticket - 1)
                {
                    var left = MaxWait - waited.Elapsed;
                    if (left <= TimeSpan.Zero || stopping()) break;
                    Monitor.Wait(_order, left < Slice ? left : Slice);
                }
                PassLocked(force: _order.Through < _ticket - 1);
            }
        }

        /// <summary>This job has begun or ended: the next one may begin.</summary>
        internal void Pass()
        {
            lock (_order) PassLocked(force: false);
        }

        private void PassLocked(bool force)
        {
            if (Interlocked.Exchange(ref _passed, 1) != 0) return;
            if (force)
            {
                // Gave up on an earlier job: those before this one no longer hold anyone up.
                if (_order.Through < _ticket) _order.Through = _ticket;
                _order.Passed.RemoveWhere(t => t <= _order.Through);
            }
            else if (_ticket > _order.Through)
            {
                _order.Passed.Add(_ticket);
            }
            while (_order.Passed.Remove(_order.Through + 1)) _order.Through++;
            Monitor.PulseAll(_order);
        }

        private sealed class Order
        {
            public long Issued;   // last ticket handed out
            public long Through;  // every ticket up to this one has passed
            public readonly HashSet<long> Passed = [];
        }
    }

    private sealed class Baseline
    {
        private readonly Dictionary<string, object?> _variables;
        private readonly Dictionary<string, object> _commands;

        /// <summary>False once a job left state that cannot be undone; the runspace is then closed, not reused.</summary>
        public bool Clean = true;

        private Baseline(Dictionary<string, object?> variables, Dictionary<string, object> commands)
        {
            _variables = variables;
            _commands = commands;
        }

        public static Baseline Capture(ProviderIntrinsics provider)
        {
            var variables = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in Variables(provider)) variables[v.Name] = v.Value;
            var commands = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, info) in Commands(provider)) commands[key] = info;
            return new Baseline(variables, commands);
        }

        public bool Restore(SessionState session)
        {
            var provider = session.InvokeProvider;
            foreach (var v in Variables(provider))
            {
                if (!_variables.TryGetValue(v.Name, out var was))
                {
                    if (v.Name == "BashBgLastPid") return false;
                    try { session.PSVariable.Remove("global:" + v.Name); } catch (Exception) { }
                }
                else if (!ReferenceEquals(was, v.Value) && (v.Options & (ScopedItemOptions.ReadOnly | ScopedItemOptions.Constant)) == 0)
                {
                    // The job started jobs of its own: their records are in this runspace's job table.
                    if (v.Name == "BashBgLastPid") return false;
                    try { v.Value = was; } catch (Exception) { }
                }
            }
            int seen = 0;
            foreach (var (key, info) in Commands(provider))
            {
                if (!_commands.TryGetValue(key, out var was) || !ReferenceEquals(was, info)) return false;
                seen++;
            }
            return seen == _commands.Count;
        }

        private static IEnumerable<PSVariable> Variables(ProviderIntrinsics provider)
        {
            foreach (var item in provider.ChildItem.Get("variable:", false))
                if (item.BaseObject is PSVariable v) yield return v;
        }

        private static IEnumerable<(string, object)> Commands(ProviderIntrinsics provider)
        {
            foreach (var item in provider.ChildItem.Get("function:", false))
                if (item.BaseObject is CommandInfo c) yield return ("f:" + c.Name, c);
            foreach (var item in provider.ChildItem.Get("alias:", false))
                if (item.BaseObject is CommandInfo c) yield return ("a:" + c.Name, c);
        }
    }
}
