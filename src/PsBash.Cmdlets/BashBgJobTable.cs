using System.Collections;
using System.Management.Automation;
using PsBash.Core.Stdio;

namespace PsBash.Cmdlets;

/// <summary>
/// A shell's background-job table, as the module's own functions see it (<c>$script:BashBgJobs</c>: <c>jobs</c>,
/// <c>wait</c>, <c>kill %N</c>, <c>fg</c>, all through <c>Find-BashBgJob</c>, docs/specs/background-jobs.md section 8),
/// plus what a launch needs from the module: its path (for the job runspaces), its process-job launcher and its reaper.
/// PsBash.psm1 creates one per runspace as it loads and keeps it in the global <see cref="VariableName"/>, where
/// <c>Invoke-BashBackground</c> (<see cref="InvokeBashBackgroundCommand"/>) finds it.
/// <para>The in-process launch (<see cref="LaunchInProcess"/>) is C#, start to end: it used to be about 4 ms of psm1
/// script per <c>&amp;</c> with five script-to-.NET calls, each an AMSI report under PowerShell's process-wide lock
/// (section 12.4) that waited 10-35 ms whenever another command compiled. Three launches in a row then came more than
/// 10 ms apart, and <c>for i in 3 2 1; do { sleep 0.0$i; echo $i; } &amp; done</c> printed out of bash's order
/// (section 12.5, cause 8).</para>
/// </summary>
public sealed class BashBgJobTable
{
    /// <summary>The global variable that holds the runspace's table.</summary>
    public const string VariableName = "__psbashBgJobTable";

    /// <summary>Past this many entries a launch first reaps the finished jobs (a shell that never waits).</summary>
    internal const int MaxJobs = 256;

    /// <summary>The module's job records (the same list as its <c>$script:BashBgJobs</c>).</summary>
    public List<object> Jobs { get; }

    internal string? ModulePath { get; }

    /// <summary>The module's <c>Start-BashBgProcessJob</c>: a job in a host process of its own (section 11).</summary>
    internal ScriptBlock? StartProcessJob { get; }

    /// <summary>The module's <c>Remove-BashBgFinished</c>.</summary>
    internal ScriptBlock? RemoveFinished { get; }

    /// <summary>The next synthetic <c>$!</c> of an in-process job (bash's would be an OS pid; there is none).</summary>
    private int _nextId = 1000;

    public BashBgJobTable(List<object> jobs, string? modulePath, ScriptBlock? startProcessJob, ScriptBlock? removeFinished)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        Jobs = jobs;
        ModulePath = string.IsNullOrEmpty(modulePath) ? null : modulePath;
        StartProcessJob = startProcessJob;
        RemoveFinished = removeFinished;
    }

    /// <summary>The table of the runspace that runs <paramref name="session"/>, or null (the module is not loaded).</summary>
    internal static BashBgJobTable? Of(SessionState session)
        => Unwrap(session.PSVariable.GetValue("global:" + VariableName)) as BashBgJobTable;

    /// <summary>
    /// The job goes to a host process of its own (section 11): <c>PSBASH_BG_MODE=process</c>, or an <c>-Isolated</c> job
    /// unless <c>PSBASH_BG_MODE=inproc</c>, and only where the host provides the launcher type (not a plain module import).
    /// <c>PSBASH_*</c> is host configuration, read from the process environment (BashVariableStore).
    /// <para>
    /// A session that holds secrets (<see cref="PsBash.Core.ShellSecrets"/>) runs its jobs in this process: an in-process
    /// job takes its copy of them at its <c>&amp;</c> (<see cref="PsBash.Core.ScalarOverlay.Capture"/>), while a host
    /// process of its own would have to be handed the values, which never leave the process holding them.
    /// </para>
    /// </summary>
    internal static bool RunsInOwnProcess(bool isolated)
        => WantsOwnProcess(isolated, Environment.GetEnvironmentVariable("PSBASH_BG_MODE"), PsBash.Core.ShellSecrets.Current)
           && JobProcessTypeExists();

    /// <summary>The decision of <see cref="RunsInOwnProcess"/>, before asking whether the launcher type exists.</summary>
    internal static bool WantsOwnProcess(bool isolated, string? mode, PsBash.Core.ShellSecrets? secrets)
    {
        if (secrets is { Names.Count: > 0 }) return false;
        return string.Equals(mode, "process", StringComparison.OrdinalIgnoreCase)
            || (isolated && !string.Equals(mode, "inproc", StringComparison.OrdinalIgnoreCase));
    }

    private static bool _jobProcessTypeFound;

    private static bool JobProcessTypeExists()
    {
        if (_jobProcessTypeFound) return true;
        // PowerShell's own type lookup (`-as [type]`): the host assembly is loaded by then or never.
        _jobProcessTypeFound = LanguagePrimitives.TryConvertTo<Type>("PsBash.Host.Runtime.BashJobProcess", out var type) && type is not null;
        return _jobProcessTypeFound;
    }

    /// <summary>
    /// Start <paramref name="body"/> as an in-process job (a job runspace, <see cref="BashJobRunspaces.NewJob"/>):
    /// record it in the table with its job number, start it, keep its start result and set <c>$!</c>. A start that is
    /// refused is reported and leaves no record, and the script goes on. No PowerShell script runs here and nothing is
    /// compiled; the job's own path to its body compiles nothing either (section 12.5, cause 7).
    /// </summary>
    internal void LaunchInProcess(PSCmdlet cmdlet, ScriptBlock body, IDictionary? vars, OutputMux? mux, string? text, bool isolated)
    {
        // The job's exit status for `wait`, written by its wrapper (JobScript.Text): Code, Signal, At.
        var status = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase) { ["Code"] = 0, ["At"] = 0L });
        var ps = BashJobRunspaces.NewJob(cmdlet.SessionState, body, vars, status, isolated, ModulePath);
        int id = _nextId++;
        var job = NewRecord(id, ps, status, live: mux is not null, text);
        job.Properties.Add(new PSNoteProperty("JobNumber", NextJobNumber(Jobs)));
        Jobs.Add(job);
        object started;
        try
        {
            started = mux is not null ? BashJobOutput.BeginLive(ps, mux, "job " + id) : ps.BeginInvoke();
        }
        catch (Exception e)
        {
            Jobs.Remove(job);
            cmdlet.WriteError(e is IContainsErrorRecord r
                ? r.ErrorRecord
                : new ErrorRecord(e, "BashBackgroundStart", ErrorCategory.OperationStopped, body));
            return;
        }
        job.Properties["AsyncResult"].Value = started;
        cmdlet.SessionState.PSVariable.Set("global:BashBgLastPid", id);
    }

    /// <summary>
    /// An in-process job's record, the shape the module's functions read: Id, PowerShell, AsyncResult, Done, Status,
    /// ExitCode, Live, Text, and the Process-like HasExited / Kill() that tests use on <c>$script:BashBgPids</c>.
    /// </summary>
    internal static PSObject NewRecord(int id, PowerShell ps, Hashtable status, bool live, string? text)
    {
        var job = new PSObject();
        job.Properties.Add(new PSNoteProperty("Id", id));
        job.Properties.Add(new PSNoteProperty("PowerShell", ps));
        job.Properties.Add(new PSNoteProperty("AsyncResult", null));
        job.Properties.Add(new PSNoteProperty("Done", false));
        job.Properties.Add(new PSNoteProperty("Status", status));
        job.Properties.Add(new PSNoteProperty("ExitCode", null));
        job.Properties.Add(new PSNoteProperty("Live", live));
        job.Properties.Add(new PSNoteProperty("Text", text));
        job.Properties.Add(new PSCodeProperty("HasExited", typeof(BashBgJobTable).GetMethod(nameof(HasExited))!));
        job.Methods.Add(new PSCodeMethod("Kill", typeof(BashBgJobTable).GetMethod(nameof(Kill))!));
        return job;
    }

    /// <summary>bash's job number for a new job: one past the highest in the table (1 in an empty one), as <c>Get-BashJobNumber</c>
    /// reads it: a record's JobNumber, else its position.</summary>
    internal static int NextJobNumber(IReadOnlyList<object> jobs)
    {
        int number = 1;
        for (int i = 0; i < jobs.Count; i++)
        {
            var n = PSObject.AsPSObject(jobs[i]).Properties["JobNumber"]?.Value is { } v ? LanguagePrimitives.ConvertTo<int>(v) : i + 1;
            if (n >= number) number = n + 1;
        }
        return number;
    }

    /// <summary>The record's HasExited: marked done, or its pipeline completed.</summary>
    public static bool HasExited(PSObject instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (LanguagePrimitives.IsTrue(instance.Properties["Done"]?.Value)) return true;
        return Unwrap(instance.Properties["AsyncResult"]?.Value) is IAsyncResult { IsCompleted: true };
    }

    /// <summary>The record's Kill(): stop its pipeline and mark it done.</summary>
    public static void Kill(PSObject instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        try { (Unwrap(instance.Properties["PowerShell"]?.Value) as PowerShell)?.Stop(); }
        catch (Exception) { }
        if (instance.Properties["Done"] is { } done) done.Value = true;
    }

    private static object? Unwrap(object? v) => v is PSObject p ? p.BaseObject : v;
}
