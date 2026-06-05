using System.Collections;
using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashEnv</c>
/// (REFACTOR-2 follow-on). Prints environment variables — with no args,
/// emits all variables sorted by name; with one operand, emits just that
/// variable (or a bash-style error if unset).
///
/// Behavioral parity oracle: the original psm1 function. Output is a
/// typed <c>PsBash.EnvEntry</c> PSObject per variable with <c>Name</c>,
/// <c>Value</c>, <c>BashText = "NAME=VALUE"</c>. Missing-variable error
/// message matches the oracle byte-for-byte: <c>env: 'NAME': not set</c>.
///
/// No colliding flags. The only flag the psm1 oracle accepts is
/// <c>--help</c>, which delegates to psm1 <c>Show-BashHelp</c> via
/// parameter-bound <c>InvokeCommand.InvokeScript</c> (AOT-safe — no
/// concatenation of user-controlled tokens into the script body).
///
/// Aliases <c>env</c> and <c>printenv</c> stay in psm1 and resolve to
/// this cmdlet automatically once <c>PsBash.Cmdlets.dll</c> is loaded.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashEnv")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashEnvCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        if (FileSystemHelpers.TryHandleVersion(this, "env", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "env"))
            {
                WriteObject(line);
            }
            return;
        }

        // `printenv NAME...` prints the VALUE only (one per line), matching bash;
        // `env NAME` is a ps-bash-ism that keeps the NAME=VALUE shape. The two
        // resolve to this same cmdlet via aliases, so distinguish them by the
        // invocation name (Dart wpCPSd25qMuI). `printenv` is never emitter-mapped,
        // so the alias name survives into InvocationName (like gunzip/zcat).
        bool asPrintenv = string.Equals(
            MyInvocation?.InvocationName, "printenv", StringComparison.OrdinalIgnoreCase);

        if (args.Length > 0)
        {
            if (asPrintenv)
            {
                // bash printenv prints each named var's value; a missing var
                // contributes no line (bash exits 1 silently, no "env:" error).
                foreach (var name in args)
                {
                    var v = Environment.GetEnvironmentVariable(name);
                    if (v != null) WriteObject(BuildEntry(name, v, valueOnly: true));
                }
                return;
            }

            var varName = args[0];
            var val = Environment.GetEnvironmentVariable(varName);
            if (val == null)
            {
                FileSystemHelpers.WriteBashError(this, $"env: '{varName}': not set");
                return;
            }
            WriteObject(BuildEntry(varName, val));
            return;
        }

        // No args: enumerate all environment variables, sorted by name
        // (the psm1 oracle's `Sort-Object` step).
        var entries = Environment.GetEnvironmentVariables();
        var names = new List<string>(entries.Count);
        foreach (DictionaryEntry e in entries)
        {
            names.Add(e.Key?.ToString() ?? string.Empty);
        }
        names.Sort(StringComparer.Ordinal);

        foreach (var key in names)
        {
            var v = entries[key]?.ToString() ?? string.Empty;
            WriteObject(BuildEntry(key, v));
        }
    }

    private static PSObject BuildEntry(string name, string value, bool valueOnly = false)
    {
        var pso = new PSObject();
        pso.TypeNames.Insert(0, "PsBash.EnvEntry");
        pso.Properties.Add(new PSNoteProperty("Name", name));
        pso.Properties.Add(new PSNoteProperty("Value", value));
        // printenv prints value-only; env keeps the NAME=VALUE shape.
        pso.Properties.Add(new PSNoteProperty("BashText", valueOnly ? value : $"{name}={value}"));
        return pso;
    }
}
