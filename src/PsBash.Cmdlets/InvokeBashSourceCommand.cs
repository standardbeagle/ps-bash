// Placeholder stub. Real implementation lives in a sibling task.
using System;
using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Reads a bash script file, transpiles it, and sources it into the caller's scope.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashSource")]
public sealed class InvokeBashSourceCommand : PSCmdlet
{
    [Parameter(Position = 0, Mandatory = true)]
    public string? Path { get; set; }

    protected override void ProcessRecord()
    {
        throw new NotImplementedException(
            "Invoke-BashSource is a scaffold stub. Implementation pending in a sibling task.");
    }
}
