// Placeholder stub. Real implementation lives in a sibling task.
// This file exists only so the binary module loads and Get-Command lists the cmdlet.
using System;
using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Transpiles a bash string and evaluates the resulting PowerShell in the caller's scope.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashEval")]
public sealed class InvokeBashEvalCommand : PSCmdlet
{
    [Parameter(Position = 0, Mandatory = true, ValueFromPipeline = true)]
    public string? Source { get; set; }

    protected override void ProcessRecord()
    {
        throw new NotImplementedException(
            "Invoke-BashEval is a scaffold stub. Implementation pending in a sibling task.");
    }
}
