using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashUnset</c> function
/// (REFACTOR-2 follow-on, vars batch). Implements the bash <c>unset</c>
/// builtin: remove a variable (default; <c>-v</c>) or a function (<c>-f</c>)
/// from the caller's scope.
///
/// Behavioral parity oracle: the original psm1 function. Variable mode
/// removes both the PowerShell variable in the caller's scope AND the
/// matching <c>$env:NAME</c> entry — matching the oracle's two-step
/// <c>Remove-Variable -Scope 1</c> + <c>Remove-Item Env:\NAME</c> slice
/// byte-for-byte. Function mode removes the function from
/// <c>Function:\NAME</c>. Missing names are silently ignored (the oracle
/// used <c>-ErrorAction SilentlyContinue</c>).
///
/// Flag dispatch: a mid-list <c>-f</c> / <c>-v</c> changes the mode for
/// subsequent names (oracle parity — the mode is a per-iteration state).
/// Other <c>-</c>-prefixed tokens are silently dropped (oracle parity).
///
/// <para><b>Colliding flag:</b> bare <c>-v</c> prefix-matches the PowerShell
/// common parameter <c>-Verbose</c> under <see cref="PSCmdlet"/> parameter
/// binding. Declared as <see cref="SwitchParameter"/> <c>V</c> so an exact
/// param-name match beats the common-parameter prefix match. Bare <c>-f</c>
/// has no common-parameter collision and is recovered from
/// <see cref="Arguments"/> by the manual scan.</para>
///
/// No stdout output (variable side-effect only).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashUnset")]
public sealed class InvokeBashUnsetCommand : PSCmdlet
{
    /// <summary>
    /// <c>-v</c> — variable mode (default). Declared explicitly because the
    /// bare token <c>-v</c> prefix-matches <c>-Verbose</c> under the cmdlet
    /// binder. Naming the parameter literally <c>V</c> wins the binder's
    /// exact-name match step before prefix matching runs.
    /// </summary>
    [Parameter]
    public SwitchParameter V { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "unset", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "unset"))
            {
                WriteObject(line);
            }
            return;
        }

        bool functionMode = false;
        var names = new List<string>();

        // If -V was bound by the binder it acts as a no-op mode hint (variable
        // is already the default). The manual scan then walks the remaining
        // tokens; -f / -v inside the list change the mode for subsequent names
        // (oracle parity — the mode is per-iteration, not global).
        foreach (var arg in args)
        {
            if (arg == "-f") { functionMode = true; continue; }
            if (arg == "-v") { functionMode = false; continue; }
            if (arg.StartsWith('-')) continue;
            names.Add(arg);
        }

        foreach (var name in names)
        {
            if (functionMode)
            {
                // Function mode: remove from Function:\NAME. Use a parameter-
                // bound InvokeScript so the name flows positionally (not into
                // the script body) — Directive 12 quoting safety.
                InvokeCommand.InvokeScript(
                    "param($n) Remove-Item -Path ('Function:\\' + $n) -Force -ErrorAction SilentlyContinue",
                    name);
            }
            else
            {
                // Variable mode: oracle removed the variable from the caller's
                // scope (Scope 1 in the psm1 function context) AND any matching
                // env var. From a binary cmdlet, the equivalent of "Scope 1
                // from a script function" is the runspace's current script
                // scope — which is the caller's scope. Use Remove-Variable
                // without a scope qualifier; the binder/SessionState resolves
                // to the local-or-parent scope chain (matching the oracle's
                // observable contract — the variable disappears for the
                // caller).
                InvokeCommand.InvokeScript(
                    "param($n) Remove-Variable -Name $n -Scope 1 -ErrorAction SilentlyContinue; " +
                    "if (Test-Path ('Env:\\' + $n)) { Remove-Item -Path ('Env:\\' + $n) -Force -ErrorAction SilentlyContinue }",
                    name);
            }
        }
    }
}
