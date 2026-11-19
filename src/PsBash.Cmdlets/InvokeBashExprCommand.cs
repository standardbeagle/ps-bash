using System.Management.Automation;
using System.Numerics;

namespace PsBash.Cmdlets;

/// <summary>
/// Coreutils <c>expr</c>: an arithmetic / string evaluator whose tokens are separate arguments
/// (<c>expr 2 + 3</c>). The grammar — precedence, left-associative chains (<c>expr 1 + 2 + 3</c> is 6),
/// <c>|</c> / <c>&amp;</c> short-circuit, <c>:</c> / <c>match</c> / <c>substr</c> / <c>index</c> / <c>length</c>,
/// parentheses and GNU's syntax errors — is <see cref="ExprEvaluator"/>; this cmdlet is the shell around it.
///
/// Output is a typed <c>PsBash.ExprOutput</c> PSObject with <c>Value</c> (long / BigInteger when numeric, else
/// string) and <c>BashText</c>. Status as GNU: 0, 1 when the result is null or 0, 2 for a syntax or argument error.
/// Only the catch-all <c>Arguments</c> parameter is declared: expr operands never prefix-collide with common
/// parameters. <c>--help</c> / <c>--version</c> are options only as the sole argument (<c>expr a = --help</c> is 0).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashExpr")]
[OutputType("PsBash.ExprOutput")]
public sealed class InvokeBashExprCommand : BashCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (args.Length == 1)
        {
            if (FileSystemHelpers.TryHandleVersion(this, "expr", args)) return;
            if (args[0] == "--help")
            {
                foreach (var line in InvokeCommand.InvokeScript("param($n) Show-BashHelp $n", "expr"))
                    WriteObject(line);
                return;
            }
        }

        // GNU skips one leading `--` (end of options); `expr -- 5` is 5.
        if (args.Length > 0 && args[0] == "--") args = args[1..];
        if (args.Length == 0)
        {
            FileSystemHelpers.WriteMissingOperand(this, "expr", exitCode: 2);
            return;
        }

        string result;
        try { result = ExprEvaluator.Evaluate(args); }
        catch (ExprEvaluator.ExprException ex)
        {
            FileSystemHelpers.WriteBashError(this, ex.Message);
            FileSystemHelpers.SetLastExitCode(this, ex.Status);
            return;
        }

        object value = result;
        if (long.TryParse(result, System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out var lv))
            value = lv;
        else if (result.Length > 1 && BigInteger.TryParse(result, System.Globalization.NumberStyles.AllowLeadingSign,
                     System.Globalization.CultureInfo.InvariantCulture, out var bv))
            value = bv;

        var obj = new PSObject();
        obj.TypeNames.Insert(0, "PsBash.ExprOutput");
        obj.Properties.Add(new PSNoteProperty("Value", value));
        obj.Properties.Add(new PSNoteProperty("BashText", result));
        WriteObject(obj);

        // GNU: exit 1 when the result is null (empty) or zero — `if expr "$x" : 'y' >/dev/null`.
        if (ExprEvaluator.IsNull(result)) FileSystemHelpers.SetLastExitCode(this, 1);
    }
}
