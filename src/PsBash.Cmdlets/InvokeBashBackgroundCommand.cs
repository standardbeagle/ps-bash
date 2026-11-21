using System.Collections;
using System.Management.Automation;
using PsBash.Core.Stdio;

namespace PsBash.Cmdlets;

/// <summary>
/// bash's <c>cmd &amp;</c>: the emitter's one call for every background job (<c>Invoke-BashBackground [-Isolated] [-Live]
/// [-Vars @{..}] { body } -Text '..'</c>). A cmdlet, so a launch runs no PowerShell script and makes no script-to-.NET
/// call: each such call is an AMSI report under PowerShell's process-wide lock (docs/specs/background-jobs.md section
/// 12.4), and the psm1 function this replaces made five per launch (section 12.5, cause 8). An in-process job is
/// launched by <see cref="BashBgJobTable.LaunchInProcess"/>; a job in a host process of its own (section 11) by the
/// module's <c>Start-BashBgProcessJob</c>. Both land in the module's job table, which <c>jobs</c>, <c>wait</c>,
/// <c>kill %N</c> and <c>fg</c> resolve through <c>Find-BashBgJob</c> (section 8). Guards: BackgroundLaunchGuardTests.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashBackground")]
[OutputType(typeof(void))]
public sealed class InvokeBashBackgroundCommand : BashCmdlet
{
    /// <summary>The job's body, transpiled.</summary>
    [Parameter(Mandatory = true, Position = 0)]
    public ScriptBlock Command { get; set; } = null!;

    /// <summary>The emitter found the body able to change shell state: it gets a host process of its own (section 11).</summary>
    [Parameter]
    public SwitchParameter Isolated { get; set; }

    /// <summary>The launch-time values of the loop variables the body reads (the loop goes on before the job runs).</summary>
    [Parameter]
    public Hashtable? Vars { get; set; }

    /// <summary>The job's stdout/stderr are the shell's own: it writes to the session's output mux as it runs (section 12).</summary>
    [Parameter]
    public SwitchParameter Live { get; set; }

    /// <summary>The job's command as written (`sleep 5`): <c>fg</c> and <c>jobs</c> print it.</summary>
    [Parameter]
    public string? Text { get; set; }

    protected override void EndProcessing()
    {
        var table = BashBgJobTable.Of(SessionState);
        if (table is null)
        {
            ThrowTerminatingError(new ErrorRecord(
                new InvalidOperationException("Invoke-BashBackground: the PsBash module is not loaded in this runspace (no job table)"),
                "BashBackgroundNoJobTable", ErrorCategory.InvalidOperation, null));
            return;
        }
        if (table.Jobs.Count >= BashBgJobTable.MaxJobs && table.RemoveFinished is { } reap)
            Emit(InvokeCommand.InvokeScript(true, reap, null));
        var mux = Live ? Unwrap(SessionState.PSVariable.GetValue("global:__BashJobOutput")) as OutputMux : null;
        if (table.StartProcessJob is { } startProcess && BashBgJobTable.RunsInOwnProcess(Isolated))
        {
            Emit(InvokeCommand.InvokeScript(true, startProcess, null, Command, Vars, mux, Text));
            return;
        }
        table.LaunchInProcess(this, Command, Vars, mux, Text, Isolated);
    }

    private static object? Unwrap(object? v) => v is PSObject p ? p.BaseObject : v;

    private void Emit(IEnumerable<PSObject> output)
    {
        foreach (var o in output) WriteObject(o);
    }
}
