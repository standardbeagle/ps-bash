using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// The statement before an in-process job's body (<see cref="BashJobRunspaces.NewJob"/>): waits until the jobs launched
/// before it from the same runspace have begun, so jobs begin in launch order (<see cref="BashJobRunspaces.JobTurn"/>,
/// docs/specs/background-jobs.md section 12.5). A cmdlet, not a .NET method call from script: a method call is an AMSI
/// report under the process-wide lock this ordering works around. Without a turn it does not wait. Its last act begins the job's
/// <see cref="JobClock"/>: the time from the launch to here is the start lag the job's sleeps pay back.
/// </summary>
[Cmdlet(VerbsCommon.Enter, "BashJobTurn")]
[OutputType(typeof(void))]
public sealed class EnterBashJobTurnCommand : PSCmdlet
{
    /// <summary>The job's turn (<see cref="BashJobRunspaces.JobTurn"/>), or null.</summary>
    [Parameter(Position = 0)]
    public object? Turn { get; set; }

    /// <summary>The job's clock (<see cref="JobClock"/>), or null.</summary>
    [Parameter(Position = 1)]
    public object? Clock { get; set; }

    protected override void EndProcessing()
    {
        if ((Turn is PSObject p ? p.BaseObject : Turn) is BashJobRunspaces.JobTurn turn)
            turn.Enter(() => Stopping);
        ((Clock is PSObject c ? c.BaseObject : Clock) as JobClock)?.Begin();
    }
}
