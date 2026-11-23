using System.Management.Automation;
using PsBash.Core.Parser;

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
/// Flag dispatch: <see cref="PsBash.Core.Parser.UnsetOptions"/>, the one reading the emitter shares (bash's
/// <c>internal_getopt "fnv"</c>: leading words, clusters, <c>--</c>; a later <c>-f</c> is a name, which unsets nothing).
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
public sealed class InvokeBashUnsetCommand : BashCmdlet
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

    /// <summary>
    /// <c>unset 'arr[i]'</c> / <c>unset 'map[key]'</c>: remove one element. A hashtable loses the key; a
    /// PowerShell array (not sparse) loses the slot and later elements close up, so the survivors keep their
    /// order and the count is right even though their indices shift.
    /// </summary>
    private bool TryUnsetElement(string name)
    {
        int open = name.IndexOf('[');
        if (open <= 0 || !name.EndsWith(']')) return false;
        string baseName = name[..open];
        string key = name[(open + 1)..^1], written = key;
        if (key is "@" or "*") return false;
        // `unset -v 'assoc["K"]'` / `unset 'assoc[K]'`: the quotes delimit the key, they are not part of it.
        if (key.Length >= 2 && key[0] is '"' or '\'' && key[^1] == key[0]) key = key[1..^1];
        // `unset -v 'a[$(cmd)]'`: bash expands the operand's subscript as a word, so the command runs and its output is
        // the index or key (BashSubscriptExpansion). That text is then used as is, not expanded again.
        bool expanded = false;
        if (BashSubscriptExpansion.NeedsWordExpansion(key))
        {
            string? word = BashSubscriptExpansion.Expand(InvokeCommand, key);
            if (word is null)
            {
                FileSystemHelpers.WriteStderr(this, $"unset: {name}: bad substitution");
                FileSystemHelpers.SetLastExitCode(this, 1);
                return true;
            }
            key = word;
            expanded = true;
        }

        var value = BashVariableLookup.ArrayHolder(SessionState, baseName);
        // A subscript that is not an expression is bash's expansion error, reported by TryIndex: nothing is unset.
        if (value is BashSparseArray sparse)
        {
            if (!TryIndex(key, out var sparseIdx)) return true;
            if (sparseIdx < 0 && sparse.Resolve(sparseIdx) is null)
            {
                FileSystemHelpers.WriteStderr(this, $"unset: [{key}]: bad array subscript");
                FileSystemHelpers.SetLastExitCode(this, 1);
                return true;
            }
            sparse.RemoveAt(sparseIdx);
            return true;
        }
        if (value is System.Collections.IDictionary dict)
        {
            // `unset "m[$k]"` arrives already expanded; a quoted `$k` is expanded here, by the one key rule.
            dict.Remove(expanded ? key : BashArrayElement.AssocKey(written, false, BashArrayElement.ReadOnly(SessionState).ReadRaw));
            return true;
        }
        // An indexed array's subscript is an arithmetic expression (`unset 'stack[n-1]'`).
        if (value is System.Collections.IList list)
        {
            if (!TryIndex(key, out long parsed)) return true;
            int index = (int)parsed;
            if (index < 0) index += list.Count;   // `unset 'a[-1]'`: counted from the end
            if (index < 0 && parsed < 0)
            {
                // Counted from the end past the first element: bash reports it and fails (status 1).
                FileSystemHelpers.WriteStderr(this, $"unset: [{key}]: bad array subscript");
                FileSystemHelpers.SetLastExitCode(this, 1);
                return true;
            }
            if (index >= 0 && index < list.Count)
            {
                // Subscripts of the other elements do not move (`unset 'a[1]'` leaves a[2] as a[2]): dropping the
                // last element just shortens the array, anything else turns it into a sparse one.
                object? replacement;
                if (index == list.Count - 1)
                {
                    var kept = new List<object?>(list.Count - 1);
                    for (int i = 0; i < index; i++) kept.Add(list[i]);
                    replacement = kept.ToArray();
                }
                else
                {
                    var sp = BashSparseArray.FromList(list);
                    sp.RemoveAt(index);
                    replacement = sp;
                }
                // Through the owning scope's variable, so a function unsetting a global array's element reaches the caller.
                BashVariableLookup.ReplaceArray(SessionState, baseName, replacement);
            }
            return true;
        }
        // A scalar is a one-element array: its subscript is evaluated too (`b=x; unset 'b[1/0]'` is an error). An unset
        // name's is not.
        if (value is null && BashVariableLookup.Scalar(SessionState, baseName) is not null && !TryIndex(key, out _)) return true;
        return false;
    }

    /// <summary>
    /// An indexed subscript as an arithmetic expression. False when it is not one: bash's expansion error, reported and
    /// handled by <see cref="BashArrayElement.FailSubscript"/> (the line is dropped; under <c>bash -c</c> the shell ends).
    /// </summary>
    private bool TryIndex(string key, out long result)
    {
        // Only a plain decimal skips the evaluator: `unset 'a[010]'` is element 8.
        if (PsBash.Core.Parser.BashArithmeticParser.TryPlainDecimal(key, out result)) return true;
        var vars = BashArrayElement.ReadOnly(SessionState);
        try { result = BashArith.Evaluate(key, vars.Read, vars.Write, vars.ReadRaw); return true; }
        catch (BashArith.BashArithException ex) { BashArrayElement.FailSubscript(this, key, ex); return false; }
    }

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

        // bash's unset options are -f, -v, -n; any other is its usage error, status 2 (REGRESSION: `unset --bogus`
        // was skipped silently, status 0).
        // The binder took a bare `-v` (the V decoy): it is an option word again, and options lead, so it goes first.
        if (V.IsPresent) args = ["-v", .. args];
        if (FileSystemHelpers.TryRejectBuiltinOptions(this, "unset", args, UnsetOptions.Letters)) return;

        // The options as the emitter reads them (UnsetOptions: clusters, `--`, the first name ends them).
        var options = UnsetOptions.Parse(args);
        if (options.Conflict)
        {
            FileSystemHelpers.WriteBashError(this, ShellDiagnostic.Prefix(SessionState) + UnsetOptions.ConflictMessage);
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }
        bool functionMode = options.Functions;
        bool namerefMode = options.ByReference;
        bool explicitVar = options.Variables;

        for (int i = options.FirstOperand; i < args.Length; i++)
        {
            string name = args[i];
            // A name bash cannot hold (`unset x -f`: -f is an operand here) unsets nothing, silently (bash 5.2).
            if (name.StartsWith('-')) continue;
            // A variable by its store name (`unset path` removes the bash variable path, never PATH: BashNameCase); a
            // function, and every message, by the name as written.
            string store = functionMode ? name : PsBash.Core.BashNameCase.ToStore(name);
            // `unset r` of a nameref whose target is known only at run time (`declare -n r; r=$v`) unsets the target; one
            // the emitter knew it already rewrote to the target. A nameref with no target yet is handled below.
            if (!functionMode && !namerefMode && BashShellState.NamerefTarget(store) is { Length: > 0 })
            {
                string target = BashShellState.ResolveNameref(store);
                if (BashShellState.NamerefTarget(target) is null) { name = PsBash.Core.BashNameCase.ToBash(target); store = PsBash.Core.BashNameCase.ToStore(target); }
            }
            if (!functionMode && BashShellState.IsReadonly(store))
            {
                FileSystemHelpers.WriteStderr(this, $"unset: {name}: cannot unset: readonly variable");
                FileSystemHelpers.SetLastExitCode(this, 1);
                continue;
            }
            if (!functionMode && namerefMode)
            {
                // `unset -n ref` removes the reference itself, never the variable it names; on a name that is no nameref it
                // does nothing (bash 5.2: `v=3; unset -n v` leaves v=3, and unsets no function of that name either).
                UnsetReference(store);
                continue;
            }
            // A nameref with no target yet names no variable, so a plain `unset r` removes the reference itself
            // (bash: `declare -n r; unset r; declare -p r` is not found).
            if (!functionMode && BashShellState.NamerefTarget(store) is { Length: 0 })
            {
                UnsetReference(store);
                continue;
            }
            if (!functionMode && TryUnsetElement(store)) continue;
            // A temp binding (`v=t f`) or a caller's local (`unlocal() { unset -v "$1"; }`): the frame goes and the value
            // it hid comes back, as in bash. A local of the current function falls through: unset, still local.
            if (!functionMode && VarFrameStack.Current.Unset(store, out var revealed))
            {
                BashVariableStore.Set(store, revealed);
                if (revealed is null) PsBash.Core.ShellExports.Forget(store);
                continue;
            }
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
                // Bash: with neither -f nor -v, a name that is no variable at all unsets the function of that name.
                object? arrayBefore = BashVariableLookup.ArrayHolder(SessionState, store);
                InvokeCommand.InvokeScript(
                    "param($n, $explicitVar, $fn) $isVar = (Get-Variable -Name $n -Scope 1 -ErrorAction SilentlyContinue) -or (Test-Path ('Env:\\' + $n)); " +
                    "Remove-Variable -Name $n -Scope 1 -ErrorAction SilentlyContinue; " +
                    "if (Test-Path ('Env:\\' + $n)) { Remove-Item -Path ('Env:\\' + $n) -Force -ErrorAction SilentlyContinue } " +
                    "elseif (-not $isVar -and -not $explicitVar -and -not $fn.Contains('[')) { Remove-Item -Path ('Function:\\' + $fn) -Force -ErrorAction SilentlyContinue }",
                    // `declare c; unset c`: a declared name is a variable even with no value yet (no function is unset).
                    store, explicitVar || BashShellState.HasAttributes(store), name);
                // An array further out than the caller's scope (a subshell's array is written in place) is unset too.
                BashVariableLookup.UnsetSurvivingArray(SessionState, store, arrayBefore);
                // SHELL has a startup value even when the environment lacks it (PsBuild maps a read of it to
                // BashRuntime.DefaultShell); once unset it reads as unset, as in bash.
                if (name == "SHELL") SessionState.PSVariable.Set("global:__BashShellUnset", true);
                // The attributes go with the variable: a later `NAME=v` is a new, unexported one, and `declare -p NAME`
                // finds nothing (the declared-but-unset mark and -i/-l/-u went too).
                PsBash.Core.ShellExports.Forget(store);
                BashShellState.TakeAttributes(store);
            }
        }
    }

    /// <summary>
    /// <c>unset -n NAME</c>: when NAME is a nameref, the reference goes with its attributes, so NAME is a plain, unset
    /// name again (<c>declare -p</c> and <c>set</c> no longer list it; a later <c>NAME=v</c> assigns NAME itself). Not a
    /// nameref: nothing changes.
    /// </summary>
    private static void UnsetReference(string store)
    {
        if (BashShellState.TakeNameref(store) is null) return;
        BashVariableStore.Set(store, null);
        PsBash.Core.ShellExports.Forget(store);
        BashShellState.TakeAttributes(store);
    }
}
