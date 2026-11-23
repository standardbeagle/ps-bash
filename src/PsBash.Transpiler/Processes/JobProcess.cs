using PsBash.Core.Runtime.Ipc;

namespace PsBash.Core.Runtime.Processes;

/// <summary>
/// The child's stdin, stdout and stderr as raw OS handles (Windows <c>HANDLE</c>, POSIX fd) the CALLER owns and
/// closes. A null member is the null device. On Windows each handle must be inheritable (the handle list names
/// exactly these); on POSIX they are <c>dup2</c>ed onto fd 0/1/2 and should be close-on-exec themselves, so no
/// other child started at the same time inherits them. Stdout and Stderr may be the SAME handle (<c>2&gt;&amp;1</c>).
/// </summary>
/// <param name="Stdin">The child's fd 0; null is the null device unless <paramref name="InheritStdin"/>.</param>
/// <param name="Stdout">The child's fd 1; null is the null device.</param>
/// <param name="Stderr">The child's fd 2; null is the null device.</param>
/// <param name="InheritStdin">With a null <paramref name="Stdin"/>: the child reads THIS process's stdin.</param>
public readonly record struct JobStdio(IntPtr? Stdin, IntPtr? Stdout, IntPtr? Stderr, bool InheritStdin = false);

/// <summary>What to start. Without <see cref="Stdio"/> the three standard streams are the null device.</summary>
/// <param name="FileName">Program; resolved through PATH when not rooted.</param>
/// <param name="Arguments">argv[1..], not shell-quoted.</param>
/// <param name="Environment">Overrides applied on top of this process's environment; a null value removes the variable.</param>
public sealed record JobProcessStart(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?>? Environment = null)
{
    /// <summary>The child's standard streams; null = all three are the null device (executors speak IPC).</summary>
    public JobStdio? Stdio { get; init; }

    /// <summary>The child's working directory; null/empty = this process's.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary><see cref="Environment"/> is the child's WHOLE environment, not overrides.</summary>
    public bool EnvironmentIsComplete { get; init; }

    /// <summary>
    /// Windows only: an already-quoted argument string appended to the quoted <see cref="FileName"/> in place of
    /// <see cref="Arguments"/> (<c>cmd /d /s /c "..."</c>, whose quoting is cmd's own).
    /// </summary>
    public string? RawArguments { get; init; }

    /// <summary>Windows: CREATE_NO_WINDOW. False lets a console program share the caller's console.</summary>
    public bool CreateNoWindow { get; init; } = true;

    /// <summary>Windows: CREATE_NEW_PROCESS_GROUP (the child ignores this console's Ctrl+C). A background job's shape.</summary>
    public bool NewConsoleProcessGroup { get; init; } = true;

    /// <summary>
    /// Windows: the Job Object lets a process that ASKS to leave (<c>CREATE_BREAKAWAY_FROM_JOB</c>, as <c>nohup</c>'s
    /// <c>DetachedProcess</c> does) leave - the counterpart of POSIX <c>setsid</c>, which leaves the process group.
    /// Everything else stays contained.
    /// </summary>
    public bool AllowBreakaway { get; init; }

    /// <summary>
    /// POSIX: the child leads its own process group, so one signal reaches the tree. False keeps it in this process's
    /// group - required when this process is its terminal's foreground job (a child in another group that reads the
    /// terminal is stopped by SIGTTIN); a stop then reaches the leader and what is still linked to it by parent pid.
    /// </summary>
    public bool OwnProcessGroup { get; init; } = true;
}

/// <summary>How <see cref="JobProcess.Signal"/> asks a job tree to stop.</summary>
public enum JobSignal
{
    /// <summary>SIGTERM to the process group on POSIX. Windows has no console-app equivalent that can be
    /// delivered across consoles, so it is an immediate tree termination there (status 143).</summary>
    Terminate,

    /// <summary>SIGKILL to the process group / <c>TerminateJobObject</c> (status 137).</summary>
    Kill,
}

/// <summary>
/// A child process plus everything it spawns, as one unit (docs/specs/background-jobs.md §6). THE process-tree
/// primitive: background-job hosts and every child a cmdlet starts (<c>ChildStdio</c>) go through it, so "stop this
/// command" ends the tree by construction instead of by walking parent-pid links. One implementation per OS:
/// <list type="bullet">
/// <item>Windows — <c>CreateProcessW</c> with an explicit handle list (no inherited handles), started
/// suspended, assigned to its own kill-on-close Job Object, then resumed, so no descendant can escape before
/// containment. Disposing the object kills the tree.</item>
/// <item>POSIX — <c>posix_spawn</c> into its own process group (default signal dispositions, empty mask);
/// signals go to the group, so grandchildren die with the leader. A dedicated thread <c>waitpid</c>s, so a
/// finished job never lingers as a zombie.</item>
/// </list>
/// "Dies with its parent" is deliberately NOT here: on Windows the Job Object does it; on POSIX the child
/// runs <c>ParentDeathWatcher</c> (no PDEATHSIG through posix_spawn, none on macOS).
/// </summary>
public sealed partial class JobProcess : IDisposable
{
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private readonly ManualResetEvent _exitSignal = new(false);
    private int _exitCode;
    private volatile bool _hasExitCode;
    private int _published;
    private bool _released;
    private bool _disposed;

    private JobProcess(int pid, ProcessIdentity identity)
    {
        Pid = pid;
        Identity = identity;
    }

    /// <summary>OS process id of the job leader.</summary>
    public int Pid { get; }

    /// <summary>pid + start time: survives PID reuse, for sidecars and the orphan reaper.</summary>
    public ProcessIdentity Identity { get; }

    /// <summary>Completes with the exit status once the leader has exited: its code, or 128+n when a signal ended it.</summary>
    public Task<int> Exited => _exited.Task;

    /// <summary>The leader has exited (its descendants may still run; <see cref="KillTree"/> still reaches them).</summary>
    public bool HasExited => _hasExitCode || PollExit();

    /// <summary>The leader's exit status (128+n when a signal ended it). Only after <see cref="HasExited"/>.</summary>
    public int ExitCode => HasExited
        ? _exitCode
        : throw new InvalidOperationException("The process has not exited.");

    /// <summary>
    /// Block until the leader exits, <paramref name="milliseconds"/> pass (<see cref="Timeout.Infinite"/> allowed) or
    /// <paramref name="stop"/> is cancelled; true when it has exited. Waits on OS handles on the calling thread: no
    /// thread-pool callback sits between the exit (or the cancel) and the return.
    /// </summary>
    public bool WaitForExit(int milliseconds, CancellationToken stop = default)
    {
        if (HasExited) return true;
        try
        {
            // Windows: the process handle itself. POSIX: set by the thread that reaps the child.
            WaitHandle exit = OperatingSystem.IsWindows() ? (_exitEvent ?? (WaitHandle)_exitSignal) : _exitSignal;
            if (stop.CanBeCanceled)
                WaitHandle.WaitAny([exit, stop.WaitHandle], milliseconds);
            else
                exit.WaitOne(milliseconds);
        }
        catch (ObjectDisposedException) { /* disposed under the wait: report what is known */ }
        return HasExited;
    }

    /// <summary>Windows: read the exit status straight from the process handle (no thread-pool callback needed).</summary>
    private bool PollExit()
    {
        if (!OperatingSystem.IsWindows()) return _hasExitCode;
        lock (_gate)
        {
            if (_hasExitCode) return true;
            if (_disposed || !TryReadExitCodeWindows(out int code)) return false;
            PublishExit(code);
            return true;
        }
    }

    /// <summary>Record the exit status and wake every waiter. Called once, by the OS-specific exit watcher.</summary>
    private void PublishExit(int code)
    {
        if (Interlocked.Exchange(ref _published, 1) != 0) return;
        _exitCode = code;
        _hasExitCode = true;
        try { _exitSignal.Set(); } catch (ObjectDisposedException) { }
        _exited.TrySetResult(code);
    }

    /// <summary>Start a job. Throws <see cref="HostUnavailableException"/> when the OS refuses; its
    /// <see cref="Exception.InnerException"/> is a <see cref="System.ComponentModel.Win32Exception"/> carrying the OS
    /// error (ERROR_FILE_NOT_FOUND / ENOENT for a program that does not exist).</summary>
    public static JobProcess Start(JobProcessStart spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return OperatingSystem.IsWindows() ? StartWindows(spec) : StartPosix(spec);
    }

    /// <summary>Ask the job (leader and descendants) to stop. False when there was nothing to signal.</summary>
    public bool Signal(JobSignal signal)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            return OperatingSystem.IsWindows() ? SignalWindows(signal) : SignalPosix(signal);
        }
    }

    /// <summary>Kill the whole tree now.</summary>
    public bool KillTree() => Signal(JobSignal.Kill);

    /// <summary>
    /// <c>kill -N</c>: send bash signal <paramref name="bashSignal"/> (its Linux number, <see cref="PsBash.Core.Platform.OsSignals"/>)
    /// to the job's process group, which may catch or ignore it, as a released job (<c>nohup</c>) still can be sent
    /// one. Windows has no signals: KILL is a tree kill (137), anything else a tree termination (143). False when there
    /// was nothing to signal.
    /// </summary>
    public bool SendSignal(int bashSignal)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (OperatingSystem.IsWindows())
                return SignalWindows(bashSignal == PsBash.Core.Platform.OsSignals.SIGKILL ? JobSignal.Kill : JobSignal.Terminate);
            return SendToGroup(PsBash.Core.Platform.OsSignals.ToNative(bashSignal));
        }
    }

    /// <summary>Freeze every process of the job (SIGSTOP on POSIX; <c>NtSuspendProcess</c> per member on Windows).</summary>
    public bool Suspend() => ChangeRunState(suspend: true);

    public bool Resume() => ChangeRunState(suspend: false);

    private bool ChangeRunState(bool suspend)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            return OperatingSystem.IsWindows() ? SuspendWindows(suspend) : SuspendPosix(suspend);
        }
    }

    /// <summary>
    /// Pids of the tree that are alive now (leader and descendants): the Job Object's members on Windows, the
    /// process group's on Linux (read from <c>/proc</c>). Diagnostics and tests; empty where the OS cannot say.
    /// </summary>
    public IReadOnlyList<int> TreePids()
    {
        lock (_gate)
        {
            if (_disposed) return [];
            return OperatingSystem.IsWindows() ? TreePidsWindows() : TreePidsPosix();
        }
    }

    /// <summary>
    /// Let the job outlive this object (and, on Windows, this process): after this, <see cref="Dispose"/>
    /// only frees handles. Used for jobs that are deliberately detached at command end.
    /// </summary>
    public void Release()
    {
        lock (_gate)
        {
            if (_released || _disposed) return;
            _released = true;
            if (OperatingSystem.IsWindows()) ReleaseWindows();
        }
    }

    /// <summary>Kills the tree unless <see cref="Release"/>d, then frees OS handles. Never blocks.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!_released)
            {
                if (OperatingSystem.IsWindows()) SignalWindows(JobSignal.Kill);
                else SignalPosix(JobSignal.Kill);
            }
            _disposed = true;
            if (OperatingSystem.IsWindows()) DisposeWindows();
        }
        // POSIX: the reaper thread still sets _exitSignal (a thread may be waiting on it), so it is left to the
        // finalizer. Windows waits on the process handle and never uses it.
        if (OperatingSystem.IsWindows()) _exitSignal.Dispose();
    }

    private static HostUnavailableException Failure(string call, int osError)
    {
        var inner = new System.ComponentModel.Win32Exception(osError);
        return new HostUnavailableException($"{call} failed: {inner.Message} (OS error {osError}).", inner);
    }
}
