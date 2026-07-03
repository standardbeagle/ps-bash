using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashWhich</c>
/// (REFACTOR-2). Resolves each operand command name to its on-disk path,
/// matching the bash <c>which</c> builtin / GNU <c>which</c> command. With
/// <c>-a</c>, emits every match (alias, function, cmdlet, external) rather
/// than just the first.
///
/// Behavioral parity oracle: the original psm1 function. Output is a typed
/// <c>PsBash.WhichOutput</c> PSObject per match with <c>Command</c>,
/// <c>Path</c>, <c>Type</c> properties and <c>BashText = Path</c> (so the
/// default rendered form is the resolved path string).
///
/// Implementation: command resolution goes through PowerShell's
/// <c>Get-Command</c> via parameter-bound <c>InvokeCommand.InvokeScript</c>
/// — there is no straight System.IO equivalent that knows about loaded
/// aliases, functions, and cmdlets in the runspace. The script body uses
/// <c>Get-Command -Name $n -All:$all -ErrorAction SilentlyContinue</c> with
/// the args bound positionally, so user-supplied command names cannot
/// escape into PowerShell code (Directive 12).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashWhich")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashWhichCommand : PSCmdlet
{
    // Decoy for the bare `-a` (show-all) flag. Without it `-a` prefix-collides with
    // this cmdlet's own -Arguments parameter and the binder consumes it (and the
    // following token) BEFORE ProcessRecord runs — so `which -a foo` silently lost
    // the -a. A declared single-letter [Parameter] is an EXACT match that wins over
    // the -Arguments prefix. (The literal `-a` scan below still handles the emitter's
    // force-quoted / bundled forms.) Note: the binder is case-insensitive, so a bare
    // `-A` also binds here — an accepted, negligible divergence from bash treating
    // `-A` as a literal command name.
    [Parameter]
    public SwitchParameter A { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "which", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "which"))
            {
                WriteObject(line);
            }
            return;
        }

        bool showAll = A.IsPresent;
        var operands = new List<string>();
        foreach (var a in args)
        {
            // Case-sensitive: bash which treats -A as a literal, only -a is
            // the all-flag. Use ordinal equals to match the psm1 oracle's
            // `-ceq` comparison.
            if (string.Equals(a, "-a", StringComparison.Ordinal)) { showAll = true; continue; }
            operands.Add(a);
        }

        if (operands.Count == 0)
        {
            FileSystemHelpers.WriteBashError(this, "which: missing operand");
            return;
        }

        foreach (var name in operands)
        {
            // Resolve via Get-Command. The script body is fixed; $name and
            // $all bind positionally so the operand cannot escape into PS
            // syntax.
            var cmds = InvokeCommand.InvokeScript(
                "param($n, $all) @(Get-Command -Name $n -All:$all -ErrorAction SilentlyContinue)",
                name, showAll);

            if (cmds.Count == 0 || cmds[0] == null)
            {
                FileSystemHelpers.WriteBashError(this, $"which: no {name} in PATH");
                continue;
            }

            foreach (var raw in cmds)
            {
                if (raw == null) continue;
                var pso = raw is PSObject pp ? pp : new PSObject(raw);

                var source = pso.Properties["Source"]?.Value as string;
                var definition = pso.Properties["Definition"]?.Value as string;
                var typeValue = pso.Properties["CommandType"]?.Value;

                var path = !string.IsNullOrEmpty(source)
                    ? source
                    : (!string.IsNullOrEmpty(definition) ? definition : name);
                var type = typeValue?.ToString()?.ToLowerInvariant() ?? "unknown";

                var output = new PSObject();
                output.TypeNames.Insert(0, "PsBash.WhichOutput");
                output.Properties.Add(new PSNoteProperty("Command", name));
                output.Properties.Add(new PSNoteProperty("Path", path));
                output.Properties.Add(new PSNoteProperty("Type", type));
                output.Properties.Add(new PSNoteProperty("BashText", path));
                WriteObject(output);
            }
        }
    }
}
