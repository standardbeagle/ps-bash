using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashTrap</c> function
/// (REFACTOR-2 follow-on, alias/trap batch). Registers signal handler script
/// blocks. Maintains the psm1 module-scoped <c>$global:BashTrapHandlers</c>
/// dictionary (signal name → action text) and, for ERR / EXIT signals, sets
/// the corresponding <c>$global:__BashTrapERR</c> / <c>$global:__BashTrapEXIT</c>
/// scriptblock so <c>InvokeBashEvalCommand</c> and the EXIT-on-shutdown path
/// fire them. This cmdlet only registers / lists; firing is done elsewhere.
///
/// State ownership: the dictionary stays in psm1 module scope; the cmdlet
/// reads / mutates it via parameter-bound <see cref="PSCmdlet.InvokeCommand"/>
/// scripts. <c>$global:__BashTrapERR</c> / <c>$global:__BashTrapEXIT</c> are
/// set in the same scripts so the eval pipeline observes them.
///
/// <para><b>Colliding flag:</b> <c>-p</c> prefix-collides with
/// <c>-PipelineVariable</c> / <c>-ProgressAction</c> — declared as
/// <see cref="SwitchParameter"/> <c>P</c> so the binder's exact-name match
/// routes the bare token here. <c>-l</c> has no PowerShell common-parameter
/// prefix overlap and stays in <see cref="Arguments"/>.</para>
///
/// <para><b>Directive 12:</b> the action body is bound positionally via
/// <c>$args</c> on the parameter-bound InvokeScript call. The body is then
/// converted into a scriptblock inside the runspace via
/// <c>[scriptblock]::Create</c> — same as the oracle. The cmdlet host never
/// re-parses the action as PowerShell on its own; an attacker-controlled
/// action only runs when the trap fires (which is by design — the user asked
/// for it).</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashTrap")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashTrapCommand : PSCmdlet
{
    /// <summary>
    /// <c>-p</c> print. Declared explicitly because the bare token prefix-
    /// matches <c>-PipelineVariable</c> / <c>-ProgressAction</c>. Naming the
    /// parameter literally <c>P</c> wins the binder's exact-name match before
    /// prefix matching runs. Bash <c>trap -p</c> just lists handlers (same as
    /// the no-arg form), so <c>-p</c> is accepted and treated as a list hint.
    /// </summary>
    [Parameter]
    public SwitchParameter P { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    private static readonly string[] WellKnownSignals =
    {
        "EXIT", "ERR", "INT", "TERM", "HUP", "QUIT", "PIPE", "ALRM", "USR1", "USR2"
    };

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "trap", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "trap"))
            {
                WriteObject(line);
            }
            return;
        }

        // -p print mode: list current handlers (same shape as no-args).
        // -l listing mode: enumerate well-known signal names.
        if (P.IsPresent || args.Length == 0)
        {
            EmitHandlerList();
            return;
        }

        if (args.Length == 1 && args[0] == "-l")
        {
            var obj = new PSObject();
            obj.TypeNames.Insert(0, "PsBash.TrapOutput");
            obj.Properties.Add(new PSNoteProperty("Signal", null));
            obj.Properties.Add(new PSNoteProperty("Action", null));
            obj.Properties.Add(new PSNoteProperty("BashText", string.Join(" ", WellKnownSignals)));
            WriteObject(obj);
            return;
        }

        // trap ACTION SIGNAL... or trap - SIGNAL... (reset) or trap '' SIGNAL...
        // (also reset — empty action drops the handler).
        string? action;
        bool resetMode;
        int firstSignal;

        if (args[0] == "-" || args[0] == "--")
        {
            action = null;
            resetMode = true;
            firstSignal = 1;
        }
        else
        {
            action = args[0];
            resetMode = false;
            firstSignal = 1;
        }

        var signals = new List<string>();
        for (int i = firstSignal; i < args.Length; i++)
        {
            signals.Add(args[i].ToUpperInvariant());
        }

        if (signals.Count == 0)
        {
            // Bash default when only an action is given: EXIT.
            signals.Add("EXIT");
        }

        var dict = GetTrapDict();
        foreach (var signal in signals)
        {
            bool drop = resetMode || action == "";
            if (drop)
            {
                if (dict != null && dict.Contains(signal))
                {
                    dict.Remove(signal);
                }
                if (signal == "EXIT" || signal == "ERR")
                {
                    var name = signal == "EXIT" ? "__BashTrapEXIT" : "__BashTrapERR";
                    InvokeCommand.InvokeScript(
                        "param($n) Set-Variable -Name $n -Value $null -Scope Global -Force",
                        name);
                }
                continue;
            }

            // Register the action: store text in the dict directly; publish
            // ERR/EXIT scriptblocks via parameter-bound InvokeScript (action
            // text flows positionally, never into the script body).
            if (dict != null)
            {
                dict[signal] = action!;
            }
            if (signal == "EXIT" || signal == "ERR")
            {
                var slotName = signal == "EXIT" ? "__BashTrapEXIT" : "__BashTrapERR";
                InvokeCommand.InvokeScript(
                    "param($name, $a) Set-Variable -Name $name -Value ([scriptblock]::Create($a)) -Scope Global -Force",
                    slotName, action!);
            }
        }
    }

    /// <summary>
    /// Read the psm1-owned global dictionary directly via SessionState. Same
    /// reason as <c>InvokeBashAliasCommand.GetAliasDict</c>: going through
    /// InvokeScript's binary-module scope wrapper obscured enumeration of a
    /// non-empty dict under .NET 8 PowerShell SDK in the test runspace.
    /// </summary>
    private System.Collections.IDictionary? GetTrapDict()
    {
        var v = SessionState.PSVariable.Get("global:BashTrapHandlers")
                ?? SessionState.PSVariable.Get("BashTrapHandlers");
        return v?.Value as System.Collections.IDictionary;
    }

    private void EmitHandlerList()
    {
        var dict = GetTrapDict();
        if (dict == null)
        {
            return;
        }
        foreach (System.Collections.DictionaryEntry entry in dict)
        {
            var signal = entry.Key?.ToString() ?? "";
            var action = entry.Value?.ToString() ?? "";
            var obj = new PSObject();
            obj.TypeNames.Insert(0, "PsBash.TrapOutput");
            obj.Properties.Add(new PSNoteProperty("Signal", signal));
            obj.Properties.Add(new PSNoteProperty("Action", action));
            obj.Properties.Add(new PSNoteProperty("BashText", $"trap -- '{action}' {signal}"));
            WriteObject(obj);
        }
    }
}
