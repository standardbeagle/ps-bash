using System.Management.Automation;
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
public sealed class InvokeProcessSubSourceCommand : BashCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public ScriptBlock? Command { get; set; }

    // Promote any "function NAME" definition to "function global:NAME" so
    // bash function defs survive past the eval pipeline. Variable assignments
    // emit as $env:VAR (process-global already) so they need no rewrite. A definition the Eval-context
    // transpile already scoped (`function global:greet`) is left alone: rewriting it made
    // `function global:global:greet`, and the function was never defined.
    private static readonly Regex GlobalFunctionRewrite =
        new(@"\bfunction\s+(?!(?:global|script|local|private):)(\w+)\b", RegexOptions.Compiled);

    protected override void ProcessRecord()
    {
        if (Command is null)
            return;

        // Run the producer scriptblock in caller scope (useNewScope=false)
        // and capture its output objects. The script is the BYTES the producer wrote (BashRuntime.RecordStreamText,
        // the one record → bytes rule): an exact piece (`printf 'echo a'`) runs on into the next record, so
        // `source <(printf 'echo a'; printf 'b\n')` runs `echo ab` as bash does. Joining every record with a newline
        // ran `echo a` and then `b`.
        var output = InvokeCommand.InvokeScript(
            useLocalScope: false,
            Command,
            input: null,
            args: System.Array.Empty<object>());

        var bashContent = BashRuntime.RecordStreamText(output);
        if (string.IsNullOrWhiteSpace(bashContent))
            return;

        string psCode;
        try
        {
            psCode = BashTranspiler.Transpile(bashContent, TranspileContext.Eval, null, SharedStdin.Get(SessionState) is not null);
        }
        catch (System.Exception ex)
        {
            // Mirror the psm1 behavior: log to stderr, set LASTEXITCODE=1, return.
            // The error stream, not Console.Error, so `2>` can redirect it.
            FileSystemHelpers.WriteStderrBytes(this, $"ps-bash: source <(...): transpile error: {ex.Message}\n");
            FileSystemHelpers.SetLastExitCode(this, 1);
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
        var produced = InvokeCommand.InvokeScript(
            useLocalScope: false,
            inner,
            input: null,
            args: System.Array.Empty<object>());
        // The sourced commands' stdout is this command's stdout (`source <(echo 'echo hi')` prints hi). It was dropped.
        foreach (var item in produced) WriteObject(item);
    }
}
