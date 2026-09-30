using System.Management.Automation;
using PsBash.Cmdlets.Args;
using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashEcho</c> function.
/// Joins operands with a single space, optionally expands C-style escapes
/// (<c>-e</c>), and appends a trailing newline unless <c>-n</c> is given —
/// byte-for-byte the behaviour of the psm1 oracle (which acted only on
/// <c>-e</c>; <c>-E</c> is accepted as the default-state no-op, and there is no
/// last-wins ordering between them).
///
/// Options follow the bash BUILTIN, not getopt: see <see cref="EchoArgScan"/> (a leading run of
/// <c>-[neE]+</c> words; everything else, including <c>--</c> and <c>--help</c>, is literal).
/// <c>echo</c> is on <c>PsEmitter.OrderedArgCommands</c>, so the transpiler single-quotes every
/// dash word and each arrives in <see cref="Arguments"/> verbatim, case intact, in order. A DIRECT
/// cmdlet call (Pester, <c>Import-Module PsBash</c>) still hits the binder: a bare <c>-e</c> /
/// <c>-E</c> is ambiguous with <c>-ErrorAction</c>, so the <see cref="E"/> decoy swallows it and
/// the original case is recovered from the invocation line and re-injected.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashEcho")]
[OutputType(typeof(string))]
public sealed class InvokeBashEchoCommand : PSCmdlet
{
    /// <summary>
    /// Decoy for the bare <c>-e</c> / <c>-E</c> tokens on a direct cmdlet call —
    /// without it the binder rejects <c>-e</c> as ambiguous with
    /// <c>-ErrorAction</c> / <c>-ErrorVariable</c> before reaching
    /// <see cref="Arguments"/>. The original case is recovered from the
    /// invocation line (see class remarks).
    /// </summary>
    [Parameter]
    public SwitchParameter E { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        // Direct-call decoy fired: the bare -e/-E was swallowed by the E switch
        // and is absent from Arguments. Recover its case from the invocation line
        // (the only place the original token survives) and re-inject it so the
        // case-sensitive option scan below sees it. bash is last-wins, so
        // take the last standalone -e/-E token; default to -e (enable) if the
        // line is somehow unavailable.
        if (E.IsPresent)
        {
            // Scope to echo's own pipeline segment so a later command's -e/-E
            // (e.g. `echo -e x | grep -E y`) cannot override echo's own flag.
            string line = BashRuntime.CurrentPipelineSegment(MyInvocation);
            var m = Regex.Matches(line, @"(?<=\s)-([eE])(?=\s|$)");
            string flag = m.Count > 0 ? "-" + m[m.Count - 1].Groups[1].Value : "-e";
            var argList = new List<string>(args.Length + 1) { flag };
            argList.AddRange(args);
            args = argList.ToArray();
        }

        // bash builtin: only a LEADING run of `-[neE]+` words are options; the first other word
        // (`-x`, `--`, `-n-`, `--help`, `-`) and everything after it is printed literally.
        var scan = EchoArgScan.Scan(args);
        var operands = scan.FirstOperand >= args.Length
            ? Array.Empty<string>()
            : args[scan.FirstOperand..];
        var text = string.Join(" ", operands);
        bool stopped = false;
        if (scan.Escapes)
            text = BashEscapes.Expand(text, EscapeDialect.Echo, out stopped);
        // \c stops ALL output, including the trailing newline.
        if (!scan.NoNewline && !stopped)
            text += "\n";

        foreach (var obj in BashRuntime.EmitBashLines(text, "echo"))
            WriteObject(obj);

        FileSystemHelpers.SetLastExitCode(this, 0);
        // $_ in the next command resolves to the last operand of this one.
        SessionState.PSVariable.Set("global:BashLastArg",
            operands.Length > 0 ? operands[^1] : "");
    }
}
