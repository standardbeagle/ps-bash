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

        // Wrap transpiled script in try/finally so traps run inside the same pipeline.
        // Test-Path on the Variable: provider is strict-mode-safe AND does not write
        // to $error, so callers running under Set-StrictMode -Version Latest don't
        // see spurious errors when the trap variables have never been set.
        var wrappedScript = $@"
try {{
    {result.PowerShell}
}} finally {{
    try {{
        if ((Test-Path Variable:Global:__BashTrapEXIT) -and $global:__BashTrapEXIT) {{ & $global:__BashTrapEXIT }}
    }} catch {{ }}
    if ($global:LASTEXITCODE) {{
        try {{
            if ((Test-Path Variable:Global:__BashTrapERR) -and $global:__BashTrapERR) {{ & $global:__BashTrapERR }}
        }} catch {{ }}
    }}
}}
";

        ScriptBlock sb;
        try
        {
            sb = ScriptBlock.Create(wrappedScript);
        }
        catch (ParseException ex)
        {
            ThrowTranspileError(ex.Message, ex);
            return;
        }

        // 1. Snapshot caller's LASTEXITCODE before invocation
        var savedLastExitCode = SessionState.PSVariable.GetValue("LASTEXITCODE");

        // Clear LASTEXITCODE so we can detect whether the script wrote it.
        SessionState.PSVariable.Set("LASTEXITCODE", null);

        bool caughtRuntimeException = false;

        try
        {
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
                // Don't treat the synthetic false+errexit throw as a runtime error;
                // let finally surface it as PsBash.ErrexitFailure.
                if (ex.Message == "PsBash.FalseErrexit")
                {
                    throw;
                }
                caughtRuntimeException = true;
                ThrowRuntimeError(ex, result.LineMap);
            }
        }
        finally
        {
            // 3. Read LASTEXITCODE from scope after script execution
            var lastExitCodeObj = SessionState.PSVariable.GetValue("LASTEXITCODE");
            int exitCode = 0;
            bool wasSet = false;

            if (lastExitCodeObj != null && LanguagePrimitives.TryConvertTo<int>(lastExitCodeObj, out int parsedCode))
            {
                exitCode = parsedCode;
                wasSet = true;
            }

            // Don't clobber on success paths that set nothing
            if (wasSet)
            {
                SessionState.PSVariable.Set("LASTEXITCODE", exitCode);
            }
            else if (savedLastExitCode != null)
            {
                SessionState.PSVariable.Set("LASTEXITCODE", savedLastExitCode);
            }

            // 4. Errexit: throw if enabled and no runtime exception was caught
            if (exitCode != 0 && !caughtRuntimeException)
            {
                var errexitObj = SessionState.PSVariable.GetValue("__BashErrexit");
                if (errexitObj is true || (errexitObj is bool b && b))
                {
                    var errorRecord = new ErrorRecord(
                        new Exception($"Exit code {exitCode}"),
                        "PsBash.ErrexitFailure",
                        ErrorCategory.OperationStopped,
                        null);
                    ThrowTerminatingError(errorRecord);
                }
            }
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
