using PsBash.Core.Transpiler;
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

    [Parameter]
    public SwitchParameter PassThru { get; set; }

    [Parameter]
    public SwitchParameter NoLocalScope { get; set; }

    protected override void ProcessRecord()
    {
        if (string.IsNullOrEmpty(Source))
            return;

        TranspileResult result;
        try
        {
            result = BashTranspiler.TranspileWithMap(Source, TranspileContext.Eval);
        }
        catch (PsBash.Core.Parser.ParseException ex)
        {
            ThrowTranspileError($"bash:{ex.Line}: {ex.Message}", ex);
            return;
        }

        ScriptBlock sb;
        try
        {
            sb = ScriptBlock.Create(result.PowerShell);
        }
        catch (ParseException ex)
        {
            ThrowTranspileError(ex.Message, ex);
            return;
        }

        try
        {
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
        catch (RuntimeException ex)
        {
            ThrowRuntimeError(ex, result.LineMap);
        }
    }

    private void ThrowTranspileError(string message, Exception innerException)
    {
        var errorRecord = new ErrorRecord(
            new ParseException(message, innerException),
            "PsBash.TranspileFailed",
            ErrorCategory.ParserError,
            Source);
        ThrowTerminatingError(errorRecord);
    }

    private void ThrowRuntimeError(RuntimeException ex, IReadOnlyList<LineMapping> lineMap)
    {
        var originalRecord = ex.ErrorRecord;
        int pwshLine = originalRecord?.InvocationInfo?.ScriptLineNumber ?? 0;
        int bashLine = MapPwshLineToBashLine(pwshLine, lineMap);

        string originalMessage = originalRecord?.ErrorDetails?.Message
            ?? originalRecord?.Exception?.Message
            ?? ex.Message;

        string rewrittenMessage = $"bash:{bashLine}: {originalMessage}";
        if (pwshLine > 0)
            rewrittenMessage += $" (pwsh:{pwshLine})";

        var newRecord = new ErrorRecord(
            ex,
            "PsBash.RuntimeFailed",
            originalRecord?.CategoryInfo.Category ?? ErrorCategory.NotSpecified,
            originalRecord?.TargetObject);

        newRecord.ErrorDetails = new ErrorDetails(rewrittenMessage);
        ThrowTerminatingError(newRecord);
    }

    private static int MapPwshLineToBashLine(int pwshLine, IReadOnlyList<LineMapping> lineMap)
    {
        if (pwshLine <= 0 || lineMap.Count == 0)
            return pwshLine;

        int bashLine = lineMap[0].BashLine;
        foreach (var mapping in lineMap)
        {
            if (mapping.PwshLine <= pwshLine)
                bashLine = mapping.BashLine;
            else
                break;
        }
        return bashLine;
    }
}
