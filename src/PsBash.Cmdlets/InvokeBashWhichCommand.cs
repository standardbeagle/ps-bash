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
public sealed class InvokeBashWhichCommand : BashCmdlet
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

        bool showAll = A.IsPresent, silent = false;
        // debianutils which: `getopts as`, so options end at the first name (`which ls -a` looks up "-a"), letters are
        // case-sensitive (-A is no option), and any other letter (`--bogus` is the letter '-') is its usage error:
        // "Illegal option -X" on stderr, the usage on stdout, status 2. It was looked up as a command name (status 1).
        int first = 0;
        for (; first < args.Length; first++)
        {
            var a = args[first];
            if (a == "--") { first++; break; }
            if (a.Length < 2 || a[0] != '-') break;
            foreach (var c in a.AsSpan(1))
            {
                if (c == 'a') showAll = true;
                else if (c == 's') silent = true;
                else
                {
                    FileSystemHelpers.WriteStderr(this, "Illegal option -" + c);
                    WriteObject("Usage: which [-as] args");
                    FileSystemHelpers.SetLastExitCode(this, 2);
                    return;
                }
            }
        }
        var operands = new List<string>(args.Skip(first));

        if (operands.Count == 0)
        {
            // debianutils which: no NAME is a silent status 1 (no message).
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }

        foreach (var name in operands)
        {
            // Resolve via Get-Command. The script body is fixed; $name and
            // $all bind positionally so the operand cannot escape into PS
            // syntax.
            System.Collections.ObjectModel.Collection<PSObject> cmds;
            using (PsBash.Core.CommandHashTable.Probe()) // a search, not a run: the shell's hash table is not touched
                cmds = InvokeCommand.InvokeScript(
                    "param($n, $all) @(Get-Command -Name $n -All:$all -ErrorAction SilentlyContinue)",
                    name, showAll);

            if (cmds.Count == 0 || cmds[0] == null)
            {
                if (!silent) FileSystemHelpers.WriteBashError(this, $"which: no {name} in PATH");
                FileSystemHelpers.SetLastExitCode(this, 1); // bash/GNU which: any miss = status 1
                continue;
            }

            foreach (var raw in cmds)
            {
                if (raw == null) continue;
                var pso = raw is PSObject pp ? pp : new PSObject(raw);

                var source = pso.Properties["Source"]?.Value as string;
                var definition = pso.Properties["Definition"]?.Value as string;
                var typeValue = pso.Properties["CommandType"]?.Value;

                // A ps-bash command (alias to an Invoke-Bash* cmdlet) has no file: report its bash name, which
                // still runs it — the cmdlet name is an internal detail (same rule as `command -v` / `type`).
                var path = definition is not null && definition.StartsWith("Invoke-Bash", StringComparison.Ordinal)
                    ? name
                    : !string.IsNullOrEmpty(source)
                        ? PsBash.Core.BashPath.ToShell(source)
                        : (!string.IsNullOrEmpty(definition) ? definition : name);
                var type = typeValue?.ToString()?.ToLowerInvariant() ?? "unknown";

                var output = new PSObject();
                output.TypeNames.Insert(0, "PsBash.WhichOutput");
                output.Properties.Add(new PSNoteProperty("Command", name));
                output.Properties.Add(new PSNoteProperty("Path", path));
                output.Properties.Add(new PSNoteProperty("Type", type));
                output.Properties.Add(new PSNoteProperty("BashText", path));
                if (!silent) WriteObject(output);
            }
        }
    }
}
