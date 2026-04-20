// Placeholder stub. Real implementation lives in a sibling task.
using System;
using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Transpiles a bash string to PowerShell source text without executing it.
/// </summary>
[Cmdlet(VerbsData.ConvertTo, "PowerShell")]
[OutputType(typeof(string))]
public sealed class ConvertToPowerShellCommand : PSCmdlet
{
    [Parameter(Position = 0, Mandatory = true, ValueFromPipeline = true)]
    public string? Source { get; set; }

    protected override void ProcessRecord()
    {
        throw new NotImplementedException(
            "ConvertTo-PowerShell is a scaffold stub. Implementation pending in a sibling task.");
    }
}
