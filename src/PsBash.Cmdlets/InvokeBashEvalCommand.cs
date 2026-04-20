using System.Management.Automation;
using PsBash.Core.Transpiler;

namespace PsBash.Cmdlets;

/// <summary>
/// Transpiles a bash string and evaluates the resulting PowerShell in the caller's scope.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashEval")]
public sealed class InvokeBashEvalCommand : PSCmdlet
{
    [Parameter(Position = 0, Mandatory = true, ValueFromPipeline = true)]
    public string? Source { get; set; }

    [Parameter]
    public SwitchParameter PassThru { get; set; }

    [Parameter]
    public SwitchParameter NoLocalScope { get; set; }

    protected override void ProcessRecord()
    {
        if (string.IsNullOrEmpty(Source))
            return;

        var result = BashTranspiler.TranspileWithMap(Source, TranspileContext.Eval);
        var sb = ScriptBlock.Create(result.PowerShell);
        var output = InvokeCommand.InvokeScript(
            useLocalScope: NoLocalScope.IsPresent,
            sb,
            input: null,
            args: null);

        if (PassThru.IsPresent)
        {
            foreach (var o in output)
                WriteObject(o, enumerateCollection: false);
        }
    }
}
