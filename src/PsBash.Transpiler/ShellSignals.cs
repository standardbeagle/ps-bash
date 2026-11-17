namespace PsBash.Core;

/// <summary>
/// Signals sent to a ps-bash SHELL (docs/specs/background-jobs.md §13). A shell's <c>$$</c> is the pid of the host process
/// that runs its script, and a host runs one command at a time (the executor lease, docs/specs/executor-slots.md), so "the
/// shell whose <c>$$</c> is P" is "the command host P is running now". A signal for it is never an OS signal or a process
/// kill (that ended every script of the session, and the host): it is a message to that command, which runs the trap at
/// the next statement boundary, as bash runs a trap between commands, or takes the signal's default action.
///
/// <para>Two halves share this type, because the Transpiler assembly is the one the host, the cmdlets and the emitted
/// script all see (as <see cref="BackgroundJobTable"/>):</para>
/// <list type="bullet">
/// <item><b>Sending</b>: <see cref="SendToShell"/> is the ONE way <c>kill</c> signals another ps-bash shell. The host
/// registers <see cref="Route"/> (IPC to the host that owns the pid); a guard test keeps every kill path on it.</item>
/// <item><b>Receiving</b>: the host brackets each command with <see cref="BeginCommand"/>/<see cref="EndCommand"/>;
/// <see cref="Deliver"/> (any thread: an IPC request, a POSIX signal handler) queues the signal or applies its default
/// action; the script's statement-boundary poll (<see cref="Pending"/>) takes the queue with <see cref="TakePending"/>.</item>
/// </list>
/// </summary>
public static class ShellSignals
{
    /// <summary>What <see cref="Deliver"/> / <see cref="SendToShell"/> did with a signal.</summary>
    public enum Outcome
    {
        /// <summary>The pid is no ps-bash shell (or none this process can reach): signal it as any other process.</summary>
        NotAShell,
        /// <summary>Queued for the command's trap: it runs at the command's next statement boundary.</summary>
        Queued,
        /// <summary>No trap, and the signal's default is to ignore it (CHLD, URG, WINCH, the stop family): nothing happens.</summary>
        Ignored,
        /// <summary>No trap: the command was stopped with status 128+N, as the signal's default action ends a shell.</summary>
        Terminated,
        /// <summary>The host runs no command right now: there is no shell to receive the signal, so nothing happens.</summary>
        NoCommand,
    }

    private static readonly object Gate = new();
    // Pending signals, as POSIX keeps them for a process: one per signal (a repeat while pending coalesces), taken in
    // ascending number order, which is the order bash runs their traps (`kill -USR2 $$; kill -USR1 $$` runs USR1 first).
    private static readonly SortedSet<int> Queue = new();
    private static Guid _owner;
    private static Action<int>? _terminate;
    private static bool _trapsSet;
    private static volatile bool _pending;

    /// <summary>
    /// How this process reaches the shell another host runs: (pid, signal) → <see cref="Outcome"/>, or
    /// <see cref="Outcome.NotAShell"/> when no ps-bash host owns the pid. Registered by the host (Core's
    /// <c>ShellSignalSender</c>); null in a process that is no host (plain PowerShell with the module).
    /// </summary>
    public static Func<int, int, Outcome>? Route { get; set; }

    /// <summary>
    /// The ONE primitive by which <c>kill</c> signals a ps-bash shell other than the one running it: true when
    /// <paramref name="pid"/> is such a shell and the signal went to it as a message (<paramref name="outcome"/> says
    /// what it did); false when the pid is no ps-bash shell, and the caller signals it as an ordinary process.
    /// </summary>
    public static bool SendToShell(int pid, int signal, out Outcome outcome)
    {
        outcome = Outcome.NotAShell;
        if (Route is not { } route) return false;
        try { outcome = route(pid, signal); }
        catch (Exception) { outcome = Outcome.NotAShell; }
        return outcome != Outcome.NotAShell;
    }

    /// <summary>A queued signal waits for the running command's next statement boundary. Read by the emitted poll.</summary>
    public static bool Pending => _pending;

    /// <summary>
    /// The host starts running a command in the runspace <paramref name="runspace"/>. <paramref name="terminate"/> ends it
    /// with the given exit status (a signal's default action); it may be called from any thread.
    /// </summary>
    public static void BeginCommand(Guid runspace, Action<int> terminate)
    {
        lock (Gate)
        {
            _owner = runspace;
            _terminate = terminate;
            _trapsSet = false;
            Queue.Clear();
            _pending = false;
        }
    }

    /// <summary>The command of <paramref name="runspace"/> ended: signals still queued for it go with it.</summary>
    public static void EndCommand(Guid runspace)
    {
        lock (Gate)
        {
            if (_owner != runspace) return;
            _owner = Guid.Empty;
            _terminate = null;
            _trapsSet = false;
            Queue.Clear();
            _pending = false;
        }
    }

    /// <summary>True when <paramref name="runspace"/> runs the command signals are delivered to (not a job's runspace).</summary>
    public static bool IsOwner(Guid runspace)
    {
        lock (Gate) return runspace != Guid.Empty && _owner == runspace;
    }

    /// <summary>
    /// <c>trap</c> gave a real signal (not EXIT/ERR/DEBUG/RETURN) a disposition in <paramref name="runspace"/>. From
    /// then on <see cref="Deliver"/> queues every signal for the statement-boundary poll, which reads the trap table
    /// on the script's own thread; until then no trap can exist and the default action applies at once.
    /// </summary>
    public static void NoteTrapSet(Guid runspace)
    {
        lock (Gate)
            if (_owner != Guid.Empty && _owner == runspace) _trapsSet = true;
    }

    /// <summary>
    /// Deliver <paramref name="signal"/> to the command this process runs. Any thread; never throws; never ends the
    /// process. SIGKILL cannot be trapped: it always ends the command.
    /// </summary>
    public static Outcome Deliver(int signal)
    {
        lock (Gate)
        {
            if (_owner == Guid.Empty) return Outcome.NoCommand;
            if (_trapsSet && signal != 9)
            {
                Queue.Add(signal);
                _pending = true;
                return Outcome.Queued;
            }
            if (signal != 9 && IgnoredByDefault(signal)) return Outcome.Ignored;
            // Under the lock, so it can never reach a command that began after this one ended. It only records the
            // status and asks the pipeline to stop (asynchronously): nothing here waits.
            try { _terminate?.Invoke(128 + signal); }
            catch (Exception) { /* the command may be ending already; the host stays up */ }
            return Outcome.Terminated;
        }
    }

    /// <summary>True while the host runs a command (<see cref="BeginCommand"/> .. <see cref="EndCommand"/>).</summary>
    public static bool HasCommand
    {
        get { lock (Gate) return _owner != Guid.Empty; }
    }

    /// <summary>The signals pending for <paramref name="runspace"/>'s command, each once, lowest number first; empty for any other runspace.</summary>
    public static int[] TakePending(Guid runspace)
    {
        lock (Gate)
        {
            if (_owner == Guid.Empty || _owner != runspace || Queue.Count == 0) return [];
            var taken = Queue.ToArray();
            Queue.Clear();
            _pending = false;
            return taken;
        }
    }

    /// <summary>
    /// A shell with no trap for <paramref name="signal"/> keeps running: SIGCHLD (17), SIGCONT (18), SIGURG (23) and
    /// SIGWINCH (28) are ignored by default, and the stop family (SIGSTOP 19, SIGTSTP 20, SIGTTIN 21, SIGTTOU 22) would
    /// suspend it, which a host command cannot be. Every other signal ends it with 128+N.
    /// </summary>
    public static bool IgnoredByDefault(int signal) => signal is 0 or 17 or 18 or 19 or 20 or 21 or 22 or 23 or 28;
}
