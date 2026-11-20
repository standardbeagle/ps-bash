using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// The bash <c>let</c> builtin for operands known only at run time (<c>let "$v |= 1"</c>); a literal
/// <c>let</c> is emitted as <c>(( ))</c> by the transpiler (PsEmitter.TryEmitLetAsArithmetic).
///
/// <para>Each operand is one arithmetic expression, evaluated by <c>Invoke-BashArith</c> (<see cref="BashArith"/>)
/// in the caller's scope: the full grammar (assignment operators <c>|= &lt;&lt;= ^=</c>, ternary, comparisons,
/// number bases, <c>++</c>), and the shell's own variables, read and written where the rest of the script sees them.
/// It used a small parser of its own that knew only <c>+ - * / % **</c> and wrote PowerShell variables the emitted
/// <c>$env:NAME</c> reads never saw.</para>
///
/// <para>Security (Directive 12): the operand is passed to <c>Invoke-BashArith</c> as an argument, never as script
/// text, and is parsed by the arithmetic lexer, so <c>$(throw 'pwn')</c> or <c>;rm -rf</c> in it is an arithmetic
/// syntax error, not code.</para>
///
/// <para>Status (bash): 1 when the LAST operand evaluates to 0, else 0. A failed operand is reported, status 1, and
/// the rest are not evaluated.</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashLet")]
public sealed class InvokeBashLetCommand : BashCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "let", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "let"))
            {
                WriteObject(line);
            }
            return;
        }

        if (args.Length == 0)
        {
            FileSystemHelpers.WriteBashError(this, "let: expression expected");
            return;
        }

        // The operand is the block's argument (data), never part of its text. Created here: a script block is bound
        // to the runspace that first runs it, and the host has several.
        // 2>&1: the nested script's error stream is not this cmdlet's, so `let /x 2>/dev/null` printed the error anyway
        // (InvokeBashCommandCommand.RunInner has the same rule); each ErrorRecord is re-written here as `let: …`.
        var evaluate = ScriptBlock.Create("Invoke-BashArith $args[0] 2>&1");
        long last = 0;
        foreach (var raw in args)
        {
            var result = InvokeCommand.InvokeScript(false, evaluate, null, raw ?? "");
            object? value = null;
            foreach (var item in result)
            {
                if (item?.BaseObject is ErrorRecord er) FileSystemHelpers.WriteBashError(this, "let: " + er.Exception.Message);
                else value = item?.BaseObject;
            }
            if (value is not long n)
            {
                // Invoke-BashArith reported the error and set status 1.
                FileSystemHelpers.SetLastExitCode(this, 1);
                return;
            }
            last = n;
        }

        FileSystemHelpers.SetLastExitCode(this, last == 0 ? 1 : 0);
    }
}
