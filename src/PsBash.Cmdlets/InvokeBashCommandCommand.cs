using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashCommand</c> function
/// (REFACTOR-2 follow-on). Implements the bash <c>command</c> builtin.
///
/// Behavioral parity oracle: the original psm1 function. Behavior (matches
/// the oracle byte-for-byte):
/// <list type="bullet">
/// <item><c>--help</c> → <c>Show-BashHelp 'command'</c>.</item>
/// <item>Walks every <c>-</c>-prefixed token; if any token contains <c>v</c> or
/// <c>V</c> (i.e. <c>-v</c>, <c>-V</c>, <c>-pv</c>, ...), enables verbose mode.
/// All other dash tokens (including <c>-p</c>) are accepted but ignored — the
/// oracle treated every dash token uniformly.</item>
/// <item>For each non-flag operand, runs <c>Get-Command NAME</c>: on a hit emit
/// the definition (alias) / name (function) / source (else) via
/// <see cref="BashRuntime.EmitBashLines"/> if verbose was set; on a miss set
/// <c>$global:LASTEXITCODE = 1</c> and return immediately (no further
/// operands processed — exact oracle parity).</item>
/// </list>
///
/// <b>Documented gap:</b> the psm1 oracle never implemented the bash semantics
/// of <c>command NAME ARGS</c> (run <c>NAME</c> bypassing alias/function
/// lookup). It only ever did a metadata lookup. This cmdlet preserves the
/// oracle's exact behavior — any "run-command" form falls into the no-verbose
/// branch and produces no output, matching the oracle byte-for-byte.
///
/// Flag collisions per the playbook table:
/// <list type="bullet">
/// <item><c>-v</c> — prefix-collides with <c>-Verbose</c>; declared as an
/// explicit <see cref="SwitchParameter"/> named <c>V</c>. Exact-name match
/// beats common-parameter prefix-match.</item>
/// <item><c>-V</c> — under the case-insensitive cmdlet binder, <c>-V</c>
/// collapses onto the same <c>V</c> switch. This matches the oracle exactly:
/// the psm1 oracle treated <c>-v</c> and <c>-V</c> identically (both just set
/// verbose).</item>
/// <item><c>-p</c> — prefix-collides with <c>-PipelineVariable</c> /
/// <c>-ProgressAction</c>; declared as an explicit
/// <see cref="SwitchParameter"/> named <c>P</c>. The oracle accepted but
/// ignored <c>-p</c> (use default PATH), and this cmdlet preserves that
/// — the bound switch is silently dropped.</item>
/// </list>
///
/// Directive 12: command names are passed only to <c>Get-Command -Name</c>
/// via a parameter-bound <see cref="PSCmdlet.InvokeCommand"/> script body —
/// never concatenated into the body — so a name containing <c>;</c> /
/// <c>$()</c> / scriptblock chars / backticks stays a literal string and is
/// not re-parsed as PowerShell. A miss lands in the not-found branch.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashCommand")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashCommandCommand : PSCmdlet
{
    // Declared because the bare token -v prefix-matches the -Verbose common
    // parameter. Captures both -v and -V (binder is case-insensitive — same
    // shape the oracle had since it treated -v and -V identically).
    [Parameter] public SwitchParameter V { get; set; }

    // Declared because the bare token -p prefix-matches -PipelineVariable /
    // -ProgressAction. The oracle accepted -p as a no-op — preserved here.
    [Parameter] public SwitchParameter P { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "command", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "command"))
            {
                WriteObject(line);
            }
            return;
        }

        // Match the oracle's loop exactly: any dash-prefixed token is a flag;
        // verbose iff any flag token contains 'v' or 'V' (so -v / -V / -pv all
        // light up verbose). Non-flag tokens become operands.
        bool verbose = V.IsPresent;
        _ = P; // -p is accepted but ignored, matching the oracle.

        var operands = new List<string>();
        foreach (var arg in args)
        {
            if (arg.Length > 0 && arg[0] == '-')
            {
                // Oracle: `$flags -contains '-v' -or $flags -contains '-V'`.
                // The bundled-flag form (e.g. -pv) was not in the oracle so we
                // do not synthesize it here; preserve byte-for-byte parity.
                if (string.Equals(arg, "-v", StringComparison.Ordinal) ||
                    string.Equals(arg, "-V", StringComparison.Ordinal))
                {
                    verbose = true;
                }
            }
            else
            {
                operands.Add(arg);
            }
        }

        foreach (var name in operands)
        {
            string? output = null;

            var cmd = ResolveCommand(name);
            if (cmd != null)
            {
                switch (cmd.CommandType)
                {
                    case CommandTypes.Alias:
                        output = ((AliasInfo)cmd).Definition;
                        break;
                    case CommandTypes.Function:
                        output = cmd.Name;
                        break;
                    default:
                        // Oracle: $cmd.Source (Application / Cmdlet / etc.).
                        output = cmd.Source;
                        break;
                }
            }

            if (output != null)
            {
                if (verbose)
                {
                    foreach (var line in BashRuntime.EmitBashLines(output))
                    {
                        WriteObject(line);
                    }
                }
            }
            else
            {
                FileSystemHelpers.SetLastExitCode(this, 1);
                return;
            }
        }
    }

    /// <summary>
    /// Look up <paramref name="name"/> via <c>Get-Command</c>. The lookup is
    /// parameter-bound through <see cref="PSCmdlet.InvokeCommand"/> so the
    /// name token is never re-parsed as PowerShell (Directive 12).
    /// </summary>
    private CommandInfo? ResolveCommand(string name)
    {
        try
        {
            var results = InvokeCommand.InvokeScript(
                "param($n) Get-Command $n -ErrorAction SilentlyContinue", name);
            foreach (var r in results)
            {
                if (r?.BaseObject is CommandInfo ci) return ci;
            }
        }
        catch
        {
            return null;
        }
        return null;
    }
}
