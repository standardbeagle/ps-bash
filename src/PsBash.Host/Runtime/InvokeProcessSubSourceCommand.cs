using System.Management.Automation;
using System.Text;
using PsBash.Core.Transpiler;

namespace PsBash.Host.Runtime;

/// <summary>
/// Runs a scriptblock, collects its output as bash text, transpiles the text
/// to PowerShell, and executes the result in the caller's scope.
/// Used by the emitter for <c>source &lt;(cmd)</c> process-substitution expansion.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "ProcessSubSource")]
public sealed class InvokeProcessSubSourceCommand : PSCmdlet
{
    [Parameter(Position = 0, Mandatory = true)]
    public ScriptBlock? ScriptBlock { get; set; }

    protected override void ProcessRecord()
    {
        if (ScriptBlock == null) return;

        // Run the producer scriptblock and collect its output as bash text lines.
        var objects = InvokeCommand.InvokeScript(
            useLocalScope: false,
            ScriptBlock,
            input: null,
            args: null);

        var sb = new StringBuilder();
        foreach (var obj in objects)
        {
            // BashObjects carry their text in BashText (with trailing \n).
            // Trim and re-add \n so blank lines are preserved but CRLF is normalised.
            var text = GetBashText(obj).TrimEnd('\r', '\n');
            sb.Append(text);
            sb.Append('\n');
        }
        var bashContent = sb.ToString();

        if (string.IsNullOrWhiteSpace(bashContent))
            return;

        // Transpile the collected bash text to PowerShell.
        string psContent;
        try
        {
            psContent = BashTranspiler.Transpile(bashContent, TranspileContext.Eval) ?? "";
        }
        catch (Exception ex)
        {
            WriteError(new ErrorRecord(
                new InvalidOperationException(
                    $"ps-bash: source <(...): transpile error: {ex.Message}", ex),
                "ProcessSubSourceTranspileError",
                ErrorCategory.InvalidData,
                bashContent));
            return;
        }

        if (string.IsNullOrEmpty(psContent))
            return;

        // Execute the transpiled PowerShell in the caller's scope (source semantics:
        // variables and functions defined in the body persist in the calling context).
        var script = ScriptBlock.Create(psContent);
        InvokeCommand.InvokeScript(
            useLocalScope: false,
            script,
            input: null,
            args: null);
    }

    private static string GetBashText(PSObject? obj)
    {
        if (obj == null) return "";
        if (obj.BaseObject is string s) return s;
        var prop = obj.Properties["BashText"]?.Value;
        if (prop != null) return prop.ToString() ?? "";
        return obj.ToString() ?? "";
    }
}
