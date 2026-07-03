using System.Management.Automation;
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
/// Flag binding: <c>-e</c> and <c>-E</c> both prefix-collide with the common
/// parameters <c>-ErrorAction</c> / <c>-ErrorVariable</c>, and being
/// case-insensitive the binder cannot even tell them apart. Two paths reach
/// this cmdlet and both must work:
/// <list type="bullet">
/// <item><b>Transpiler</b> (<c>ps-bash -c 'echo -e ...'</c>): the emitter
/// force-quotes <c>-e</c> / <c>-E</c>
/// (<see cref="PsBash.Core.Parser.PsEmitter"/> EchoForceQuoteFlags) so they
/// arrive as plain operands in <see cref="Arguments"/> with case intact, where
/// <see cref="BashRuntime.ConvertFromBashArgs"/> parses them case-sensitively
/// (its flag table is ordinal).</item>
/// <item><b>Direct cmdlet call</b> (<c>Invoke-BashEcho -e ...</c>, e.g. from
/// the Pester suite or <c>Import-Module PsBash</c> users): a bare <c>-e</c>
/// never reaches <see cref="Arguments"/> — the binder throws
/// "ambiguous" first. The <see cref="E"/> SwitchParameter decoy resolves that
/// (an explicit name beats a common-parameter prefix), and because the switch
/// alone cannot carry the original case, the enable-vs-disable choice is
/// recovered from <see cref="System.Management.Automation.InvocationInfo.Line"/>
/// (case-sensitive, bash last-wins) and re-injected so the same
/// <c>ConvertFromBashArgs</c> path handles it.</item>
/// </list>
/// <c>-n</c> shares no prefix and needs no quoting.
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
        // case-sensitive ConvertFromBashArgs below sees it. bash is last-wins, so
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
