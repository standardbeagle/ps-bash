using System.Management.Automation;
using PsBash.Core.Parser;

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
        if (string.IsNullOrEmpty(Source))
        {
            WriteObject(true);
            return;
        }

        try
        {
            BashParser.ParseTopLevelWithPositions(Source);
            WriteObject(true);
        }
        catch (PsBash.Core.Parser.ParseException ex)
        {
            var errorRecord = new ErrorRecord(
                ex,
                "BashSyntaxError",
                ErrorCategory.ParserError,
                Source)
            {
                ErrorDetails = new ErrorDetails(ex.Message)
            };
            WriteError(errorRecord);
            WriteObject(false);
        }
    }
}
