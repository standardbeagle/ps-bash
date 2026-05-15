using System.Management.Automation;
using System.Text;
using System.Text.RegularExpressions;
using PsBash.Core.Transpiler;

namespace PsBash.Cmdlets;

/// <summary>
/// Source-capture variant of process substitution: runs the producer scriptblock,
/// collects its output as bash text, transpiles, and executes the resulting
/// PowerShell in the CALLER's scope so env vars and function definitions persist
/// after this cmdlet returns (bash <c>source &lt;(cmd)</c> semantics).
/// </summary>
/// <remarks>
/// RC-8a fix: previously implemented as a PowerShell function in PsBash.psm1.
/// The psm1 function introduced a script function scope, so even with
/// <c>InvokeScript(useNewScope: false, ...)</c> assignments and function defs
/// landed in the function's local scope — module scope at best — and were
/// discarded on return. Bash <c>source</c> requires the names to land in the
/// caller's persistent scope (the worker's eval scope). Cmdlets do not push a
/// script scope frame, so <see cref="PSCmdlet.InvokeCommand"/>.InvokeScript
/// with useNewScope=false targets the caller's scope (the eval pipeline scope).
/// </remarks>
[Cmdlet(VerbsLifecycle.Invoke, "ProcessSubSource")]
public sealed class InvokeProcessSubSourceCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public ScriptBlock? Command { get; set; }

    // Promote any "function NAME" definition to "function global:NAME" so
    // bash function defs survive past the eval pipeline. Variable assignments
    // emit as $env:VAR (process-global already) so they need no rewrite.
    private static readonly Regex GlobalFunctionRewrite =
        new(@"\bfunction\s+(\w+)\b", RegexOptions.Compiled);

    protected override void ProcessRecord()
    {
        if (Command is null)
            return;

        // Run the producer scriptblock in caller scope (useNewScope=false)
        // and capture its output objects. Collected lines are concatenated
        // with newlines into a single bash-text blob for re-transpilation.
        var output = InvokeCommand.InvokeScript(
            useLocalScope: false,
            Command,
            input: null,
            args: System.Array.Empty<object>());

        var sb = new StringBuilder();
        foreach (var item in output)
        {
            string text = GetBashText(item);
            sb.Append(text.TrimEnd('\r', '\n'));
            sb.Append('\n');
        }

        var bashContent = sb.ToString();
        if (string.IsNullOrWhiteSpace(bashContent))
            return;

        string psCode;
        try
        {
            psCode = BashTranspiler.Transpile(bashContent, TranspileContext.Eval);
        }
        catch (System.Exception ex)
        {
            // Mirror the psm1 behavior: log to stderr, set LASTEXITCODE=1, return.
            System.Console.Error.WriteLine($"ps-bash: source <(...): transpile error: {ex.Message}");
            SessionState.PSVariable.Set("global:LASTEXITCODE", 1);
            return;
        }

        if (string.IsNullOrEmpty(psCode))
            return;

        // Promote function defs to global scope so they persist past the eval
        // pipeline. Cmdlet scope passes through to the caller, but a script
        // 'function NAME' inside a transient scriptblock would still die on
        // return — explicit 'function global:NAME' guarantees survival.
        psCode = GlobalFunctionRewrite.Replace(psCode, "function global:$1");

        // Invoke in caller scope. Because PSCmdlet is a binary cmdlet (no
        // script scope frame), useNewScope=false targets the eval pipeline's
        // scope — exactly where bash 'source' wants the names to land.
        var inner = ScriptBlock.Create(psCode);
        InvokeCommand.InvokeScript(
            useLocalScope: false,
            inner,
            input: null,
            args: System.Array.Empty<object>());
    }

    /// <summary>
    /// Extract the bash text representation of a pipeline object. Matches the
    /// psm1 <c>Get-BashText</c> helper: prefer a <c>BashText</c> property,
    /// otherwise stringify.
    /// </summary>
    private static string GetBashText(object? obj)
    {
        if (obj is null) return string.Empty;
        if (obj is PSObject pso)
        {
            var prop = pso.Properties["BashText"];
            if (prop is not null && prop.Value is not null)
                return prop.Value.ToString() ?? string.Empty;
            if (pso.BaseObject is string s)
                return s;
            return pso.ToString() ?? string.Empty;
        }
        return obj.ToString() ?? string.Empty;
    }
}
