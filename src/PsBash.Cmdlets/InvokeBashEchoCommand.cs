using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashEcho</c> function.
/// Joins operands with a single space, optionally expands C-style escapes
/// (<c>-e</c>), and appends a trailing newline unless <c>-n</c> is given —
/// byte-for-byte the behaviour of the psm1 oracle (which acted only on
/// <c>-e</c>; <c>-E</c> is accepted as the default-state no-op, and there is no
/// last-wins ordering between them).
///
/// Flag binding: <c>-e</c> and <c>-E</c> both prefix-collide with the common
/// parameters <c>-ErrorAction</c> / <c>-ErrorVariable</c>, and being
/// case-insensitive the binder cannot even tell them apart. Rather than a
/// switch decoy (which would lose the case needed to distinguish enable vs
/// disable), the emitter force-quotes <c>-e</c> / <c>-E</c>
/// (<see cref="PsBash.Core.Parser.PsEmitter"/> EchoForceQuoteFlags) so they
/// arrive as plain operands in <see cref="Arguments"/> with case intact, where
/// <see cref="BashRuntime.ConvertFromBashArgs"/> parses them case-sensitively
/// (its flag table is ordinal). <c>-n</c> shares no prefix and needs no quoting.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashEcho")]
[OutputType(typeof(string))]
public sealed class InvokeBashEchoCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript("param($n) Show-BashHelp $n", "echo"))
                WriteObject(line);
            return;
        }

        var defs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["-n"] = "no trailing newline",
            ["-e"] = "enable escape sequences",
            ["-E"] = "disable escape sequences",
        };
        var parsed = BashRuntime.ConvertFromBashArgs(args, defs);

        var text = string.Join(" ", parsed.Operands);
        if (parsed.Flags["-e"])
            text = BashRuntime.ExpandEscapeSequences(text);
        if (!parsed.Flags["-n"])
            text += "\n";

        foreach (var obj in BashRuntime.EmitBashLines(text, "echo"))
            WriteObject(obj);

        FileSystemHelpers.SetLastExitCode(this, 0);
        // $_ in the next command resolves to the last operand of this one.
        SessionState.PSVariable.Set("global:BashLastArg",
            parsed.Operands.Count > 0 ? parsed.Operands[^1] : "");
    }
}
