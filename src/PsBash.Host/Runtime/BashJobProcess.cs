using PsBash.Core;
using PsBash.Core.Runtime;

namespace PsBash.Host.Runtime;

/// <summary>
/// A background job (<c>cmd &amp;</c>) that runs in its OWN <c>ps-bash-host</c> process (docs/specs/background-jobs.md).
/// Env and cwd are process-global, so a runspace in the parent's process could only share them: the job's
/// assignments leaked into the parent and, in the shared daemon, into other sessions. A private child process
/// has its own copy, exactly as bash's fork does.
/// <para>
/// The shape (<see cref="AsyncResult"/>, <see cref="EndInvoke"/>, <see cref="Streams"/>, <see cref="Stop"/>,
/// <see cref="Dispose"/>) is what <c>Complete-BashBgJob</c> already uses on a PowerShell job, so the module's
/// wait/jobs/kill code treats both the same. Driven from PowerShell (<c>[PsBash.Host.Runtime.BashJobProcess]</c>).
/// </para>
/// </summary>
public sealed class BashJobProcess : IDisposable
{
    private readonly IpcWorker _worker;
    private readonly List<string> _stdout = [];
    private readonly List<string> _stderr = [];
    private readonly Handle _handle = new();
    private readonly CancellationTokenSource _cancel = new();
    private int _disposed;
    private volatile int _signal; // non-zero once stopped by `kill`; the job's status is then 128 + signal

    private BashJobProcess(IpcWorker worker)
    {
        _worker = worker;
        Pid = worker.OwnedHostPid;
        Streams = new StreamSet(_stderr);
    }

    /// <summary>OS pid of the job's host process — what <c>$!</c> reports.</summary>
    public int Pid { get; }

    /// <summary>Exit status of the body; valid once <see cref="AsyncResult"/> completed.</summary>
    public int Code { get; private set; }

    /// <summary>Mirrors <c>PowerShell.Streams</c> for <c>Complete-BashBgJob</c>.</summary>
    public StreamSet Streams { get; }

    public IAsyncResult AsyncResult => _handle;

    /// <summary>Start the job: spawn a private host and run <paramref name="body"/> in it, after <paramref name="prelude"/>.</summary>
    public static BashJobProcess Start(string prelude, string body, KeyValuePair<string, string>[] environment)
    {
        int max = int.TryParse(Environment.GetEnvironmentVariable("PSBASH_JOBS_MAX"), out var m) && m > 0 ? m : 32;
        if (BackgroundJobTable.Count >= max)
            throw new InvalidOperationException("too many background jobs (PSBASH_JOBS_MAX=" + max + ")");
        var hostPath = Environment.ProcessPath
            ?? throw new HostUnavailableException("cannot locate ps-bash-host to run a background job");
        var worker = JobHostSpares.Take(hostPath);
        var job = new BashJobProcess(worker);
        worker.OutputCallback = line => { lock (job._stdout) job._stdout.Add(line); };
        worker.ErrorCallback = line => { lock (job._stderr) job._stderr.Add(line); };
        BackgroundJobTable.Register(job.Pid, job.Stop);
        _ = job.RunAsync(prelude + "\n" + body, environment);
        return job;
    }

    private async Task RunAsync(string command, KeyValuePair<string, string>[] environment)
    {
        try { Code = await _worker.ExecuteAsync(command, _cancel.Token, environment).ConfigureAwait(false); }
        catch (OperationCanceledException) { Code = 128 + (_signal != 0 ? _signal : 15); }
        catch (Exception) when (_signal != 0) { Code = 128 + _signal; }
        catch (Exception ex)
        {
            lock (_stderr) _stderr.Add("bash: background job failed: " + ex.Message + "\n");
            Code = 1;
        }
        finally { _handle.Complete(); }
    }

    /// <summary>The job's stdout, in order. Call after the job completed.</summary>
    public string[] EndInvoke(IAsyncResult _)
    {
        lock (_stdout) return [.. _stdout];
    }

    /// <summary>Stop the job and everything it started (kills the host's process tree).</summary>
    public void Stop() => Stop(15);

    /// <summary>Stop with <paramref name="signal"/> (`kill -s`): the host's whole tree dies, the status is 128 + signal.</summary>
    public void Stop(int signal)
    {
        _signal = signal;
        try { _cancel.Cancel(); } catch (ObjectDisposedException) { }
        Dispose();
        WaitUntilGone();
    }

    /// <summary>
    /// `kill $!; wait $!; kill -0 $!` must see the job gone: the kill is asynchronous in the OS, so a status
    /// reported before the process is torn down lets a following existence probe still find it (Linux CI).
    /// </summary>
    private void WaitUntilGone()
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(Pid);
            proc.WaitForExit(5000);
        }
        catch (ArgumentException) { /* already gone */ }
        catch (InvalidOperationException) { /* already gone */ }
        catch (System.ComponentModel.Win32Exception) { /* cannot open: nothing more to wait on */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        BackgroundJobTable.Unregister(Pid);
        try { _cancel.Cancel(); } catch (ObjectDisposedException) { }
        try { _worker.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* best effort */ }
        _handle.Complete();
    }

    public sealed class StreamSet
    {
        private readonly List<string> _errors;
        internal StreamSet(List<string> errors) => _errors = errors;

        /// <summary>The job's stderr frames, each with its own line terminator.</summary>
        public string[] Error { get { lock (_errors) return [.. _errors]; } }
    }

    private sealed class Handle : IAsyncResult
    {
        private readonly ManualResetEvent _done = new(false);
        public bool IsCompleted => _done.WaitOne(0);
        public WaitHandle AsyncWaitHandle => _done;
        public object? AsyncState => null;
        public bool CompletedSynchronously => false;
        public void Complete() { try { _done.Set(); } catch (ObjectDisposedException) { } }
    }
}
