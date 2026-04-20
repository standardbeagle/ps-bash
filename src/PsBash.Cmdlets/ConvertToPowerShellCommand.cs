using System.Management.Automation;
using PsBash.Core.Transpiler;

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

    [Parameter]
    public SwitchParameter WithMap { get; set; }

    protected override void ProcessRecord()
    {
        if (string.IsNullOrEmpty(Source))
            return;

        if (WithMap.IsPresent)
        {
            var result = BashTranspiler.TranspileWithMap(Source, TranspileContext.Eval);
            var obj = new PSObject();
            obj.Properties.Add(new PSNoteProperty("PowerShell", result.PowerShell));
            obj.Properties.Add(new PSNoteProperty("Map", result.LineMap));
            WriteObject(obj);
        }
        else
        {
            var result = BashTranspiler.Transpile(Source, TranspileContext.Eval);
            WriteObject(result);
        }
    }
}
