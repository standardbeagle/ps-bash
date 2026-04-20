// Placeholder stub. Real implementation lives in a sibling task.
using System;
using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Parses a bash string and reports whether it is syntactically valid.
/// </summary>
[Cmdlet(VerbsDiagnostic.Test, "BashSyntax")]
[OutputType(typeof(bool))]
public sealed class TestBashSyntaxCommand : PSCmdlet
{
    [Parameter(Position = 0, Mandatory = true, ValueFromPipeline = true)]
    public string? Source { get; set; }

    protected override void ProcessRecord()
    {
        throw new NotImplementedException(
            "Test-BashSyntax is a scaffold stub. Implementation pending in a sibling task.");
    }
}
