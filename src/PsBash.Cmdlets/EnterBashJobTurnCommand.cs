using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// The statement before an in-process job's body (<see cref="BashJobRunspaces.NewJob"/>): waits until the jobs launched
/// before it from the same runspace have begun, so jobs begin in launch order (<see cref="BashJobRunspaces.JobTurn"/>,
/// docs/specs/background-jobs.md section 12.5). A cmdlet, not a .NET method call from script: a method call is an AMSI
/// report under the process-wide lock this ordering works around. Without a turn it does nothing.
/// </summary>
[Cmdlet(VerbsCommon.Enter, "BashJobTurn")]
[OutputType(typeof(void))]
public sealed class EnterBashJobTurnCommand : PSCmdlet
{
    /// <summary>The job's turn (<see cref="BashJobRunspaces.JobTurn"/>), or null.</summary>
    [Parameter(Position = 0)]
    public object? Turn { get; set; }

    protected override void EndProcessing()
    {
        if ((Turn is PSObject p ? p.BaseObject : Turn) is BashJobRunspaces.JobTurn turn)
            turn.Enter(() => Stopping);
    }
}
