using System.Collections;
using System.Management.Automation;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>
/// THE run-time access to a bash variable by NAME (<c>docs/specs/environment-model.md</c> section 8, "One storage per
/// variable"). Every cmdlet that reads or assigns a shell variable it was handed the name of (arithmetic,
/// <c>read</c>, <c>printf -v</c>, <c>getopts</c>, <c>mapfile</c>, <c>[[ -v ]]</c>, <c>declare -p</c>, <c>unset</c>,
/// <c>${!ref}</c>) comes through here.
///
/// <para><b>The rule.</b> A SCALAR lives only in <see cref="BashVariableStore"/> (the process environment, with the
/// function-local frames of <see cref="VarFrameStack"/>). An indexed or associative array lives only as a PowerShell
/// variable: it has no environment form. So a PowerShell variable answers for a bash name only when it holds an array.
/// A scalar PowerShell variable of that name is never the shell's value: it is PowerShell's own (<c>$input</c>,
/// <c>$Host</c>), a stale copy, or a CALLER's variable seen through PowerShell's dynamic scope. That last one was the
/// bug: a loop bound <c>$q</c> as a PowerShell variable as well, and a function called from the loop body read it in
/// place of its own <c>local q</c> (<c>$((q + 1))</c> was 1, not 10); <c>read q</c> in a function left a global
/// <c>$q</c> that every later <c>(( q &gt; 0 ))</c> read.</para>
///
/// <para><b>Names.</b> Every member takes a bash name or a store name and maps it (<see cref="BashNameCase.ToStore(string)"/>):
/// <c>path</c> is its own variable on Windows, not <c>PATH</c> (<c>environment-model.md</c> section 12).</para>
///
/// <para>Guard: <c>Core.Tests/ShellVariableStorageGuardTests</c>: no other cmdlet or module function touches a
/// PowerShell variable by a computed name, and the emitter binds no PowerShell variable for a bash scalar.</para>
/// </summary>
public static class BashVariableLookup
{
    /// <summary>
    /// The PowerShell variable an array write to <paramref name="name"/> goes to (<c>VarFrameStack.ArraySlot</c> decides
    /// the frame): with <paramref name="callerLocal"/> the nearest one the scope chain sees (a calling function's local),
    /// else the shell's global, made (unset) when missing. In a subshell, neither is past the subshell's own scope: there
    /// the variable is made holding what that scope sees, and ends with it. REGRESSION: <c>( f() { arr=(x y); }; f )</c>
    /// wrote a variable local to f, so the subshell never saw x y.
    /// </summary>
    public static PSVariable ArrayWriteSlot(SessionState state, string name, bool callerLocal)
    {
        name = BashNameCase.ToStore(name);
        if (BashRuntime.SubshellDepth(state) > 0
            && state.InvokeCommand.InvokeScript(true, SlotInSubshellScope, null, name, callerLocal) is [{ BaseObject: PSVariable slot }])
            return slot;
        if (callerLocal && state.PSVariable.Get(name) is { } nearest) return nearest;
        string global = "global:" + name;
        if (state.PSVariable.Get(global) is { } existing) return existing;
        state.PSVariable.Set(global, null);
        return state.PSVariable.Get(global)!;
    }

    /// <summary>
    /// The array that holds <paramref name="name"/> (an <see cref="IList"/> other than a string, or an
    /// <see cref="IDictionary"/>: indexed, sparse or associative) as the caller's scope sees it, out to the global
    /// scope; null when the name holds no array.
    /// </summary>
    public static object? ArrayHolder(SessionState state, string name)
    {
        PSVariable? variable = ArrayVariable(state, name);
        if (variable is null) return null;
        object? held = variable.Value is PSObject wrapped ? wrapped.BaseObject : variable.Value;
        return held;
    }

    /// <summary>
    /// The value <c>$name</c> expands to: an array's element 0 (key "0"; "" when there is none), else the shell
    /// variable, else null (unset).
    /// </summary>
    public static string? Scalar(SessionState state, string name)
    {
        object? holder = ArrayHolder(state, name);
        if (holder is IDictionary dict)
            return dict.Contains("0") ? dict["0"]?.ToString() ?? "" : "";
        if (holder is IList list)
            return list.Count > 0 ? list[0]?.ToString() ?? "" : "";
        return BashVariableStore.Get(name);
    }

    /// <summary>Whether <paramref name="name"/> is set: a shell variable, or an array (an empty one too).</summary>
    public static bool IsSet(SessionState state, string name)
        => BashVariableStore.Get(name) is not null || ArrayHolder(state, name) is not null;

    /// <summary>
    /// <c>name=value</c> from a builtin (<c>read</c>, <c>printf -v</c>, <c>getopts</c>, arithmetic): the shell variable,
    /// or element 0 of the array the name holds (<c>a=(1 2); read a &lt;&lt;&lt; z</c> leaves <c>z 2</c>).
    /// </summary>
    public static void SetScalar(SessionState state, string name, string? value)
    {
        switch (ArrayHolder(state, name))
        {
            case IDictionary dict:
                dict["0"] = value ?? "";
                return;
            case IList list:
                var items = new object?[Math.Max(list.Count, 1)];
                list.CopyTo(items, 0);
                items[0] = value ?? "";
                SetArray(state, name, items);
                return;
        }
        BashVariableStore.Set(name, value);
    }

    /// <summary>
    /// <c>NAME=value</c> / <c>NAME+=value</c> the emitter writes when NAME is a nameref whose target is known only at run
    /// time: one declared with no target (<c>declare -n r; r=$v</c>), or a <c>for</c> variable that is a nameref. The
    /// first write to one with no target names the target (<see cref="BashShellState.WriteTarget"/>); a later one writes
    /// the target (a scalar, or the element <c>a[2]</c>). Returns bash's diagnostic when nothing was written, else null.
    /// </summary>
    public static string? AssignThroughNameref(SessionState state, string name, string? value, bool append)
    {
        value ??= "";
        ViewNameref(state, name);
        if (BashShellState.WriteTarget(name, value, out bool invalid) is not { } target)
            return invalid ? "`" + value + "': not a valid identifier" : null;
        int br = target.IndexOf('[');
        if (br > 0 && target[^1] == ']')
        {
            string baseName = target[..br], subscript = target[(br + 1)..^1];
            var vars = BashArrayElement.ReadOnly(state);
            string old = append ? BashArrayElement.Read(state, baseName, subscript, vars) ?? "" : "";
            return BashArrayElement.Write(state, baseName, subscript, false, old + value, vars) ? null : target + ": bad array subscript";
        }
        SetScalar(state, target, append ? Scalar(state, target) + value : value);
        return null;
    }

    /// <summary>
    /// <c>declare -n NAME=value</c> / <c>local -n NAME=value</c> whose value the emitter cannot see (<c>declare -n r=$v</c>,
    /// <c>local -n r=$1</c>): NAME becomes a reference to the variable (or element, <c>a[2]</c>) the value names. Returns
    /// bash's diagnostic, without the builtin's name, when it names none: "`': not a valid identifier" for an empty one,
    /// else "invalid variable name for name reference", and NAME is not made (status 1). Naming itself is refused at the
    /// top level; in a function bash only warns (a message starting "warning:", status 0).
    /// </summary>
    public static string? DeclareNameref(SessionState state, string name, string? value, bool inFunction)
    {
        value ??= "";
        if (value.Length == 0) return "`': not a valid identifier";
        if (!BashArrayElement.IsTarget(value)) return "`" + value + "': invalid variable name for name reference";
        if (BashNameCase.ToStore(value) == BashNameCase.ToStore(name))
            return inFunction ? "warning: " + name + ": circular name reference" : name + ": nameref variable self references not allowed";
        BashShellState.SetNameref(name, value);
        ViewNameref(state, name);
        return null;
    }

    /// <summary>
    /// <c>$NAME</c> where NAME is a nameref whose target is known only at run time (see <see cref="AssignThroughNameref"/>):
    /// the target's value (an element's, for <c>a[2]</c>); NAME's own (unset) while it has no target.
    /// </summary>
    public static string? ReadThroughNameref(SessionState state, string name)
    {
        string target = BashShellState.ResolveNameref(name);
        int br = target.IndexOf('[');
        if (br > 0 && target[^1] == ']')
            return BashArrayElement.Read(state, target[..br], target[(br + 1)..^1], BashArrayElement.ReadOnly(state));
        return Scalar(state, target);
    }

    /// <summary>
    /// <c>${!NAME}</c> / <c>${NAME@a}</c> where NAME is a nameref whose target is known only at run time: the name of the
    /// variable it stands for, to the end of its chain (bash: after <c>declare -n b=c; a=b</c>, <c>${!a}</c> is c); null
    /// while it names none (bash's "invalid indirect expansion").
    /// </summary>
    public static string? NamerefName(string name)
        => BashShellState.NamerefTarget(name) is { Length: > 0 } ? BashShellState.ResolveNameref(name) : null;

    /// <summary>
    /// <c>for NAME in WORDS</c> where NAME is a nameref: each word becomes its target (<see cref="BashShellState.BindLoopNameref"/>),
    /// and <c>$NAME</c> follows it (<see cref="ViewNameref"/>). False for a word that names no variable.
    /// </summary>
    public static bool BindLoopNameref(SessionState state, string name, string word)
    {
        ViewNameref(state, name);
        return BashShellState.BindLoopNameref(name, word);
    }

    /// <summary>
    /// Makes the PowerShell variable <c>$NAME</c> follow NAME's nameref, for a nameref whose target is known only at run
    /// time (<c>declare -n r; r=$v</c>). Every array form the emitter writes reads <c>$NAME</c> (<c>${r[@]}</c>,
    /// <c>${r[1]}</c>, <c>${#r[@]}</c>, <c>${!r[@]}</c>, <c>"${r[@]}"</c> in a <c>for</c> list or an array assignment,
    /// a slice, a per-element operator), so the one place that follows the reference is this variable's value: the
    /// target's array, null for a scalar target (the forms then fall back to <see cref="ReadThroughNameref"/>, as for any
    /// scalar). Its writes (<c>r+=(z)</c>, <c>r[2]=q</c>) go to the target. Made in the caller's scope: the scope of the
    /// <c>declare -n</c> / <c>local -n</c> that made the reference. When NAME is no nameref any more (<c>unset -n</c>,
    /// <c>declare +n</c>, the function that held a local one returned) it is a plain variable again.
    /// </summary>
    public static void ViewNameref(SessionState state, string name)
    {
        string store = BashNameCase.ToStore(name);
        if (store.Length == 0 || PowerShellOwnedVariables.Contains(store)) return;
        try
        {
            if (state.PSVariable.Get(store) is NamerefView) return;
            state.PSVariable.Set(new NamerefView(store, state));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or SessionStateException)
        {
            // Not a name PowerShell accepts, or a read-only one: its array forms read nothing.
        }
    }

    /// <summary>The variable <see cref="ViewNameref"/> makes. Not a bash variable of its own: listings skip a nameref.</summary>
    private sealed class NamerefView(string name, SessionState state) : PSVariable(name)
    {
        private object? _own;

        /// <summary>The final target when NAME is a nameref that names a whole variable; else null.</summary>
        private string? Target(out bool isNameref)
        {
            isNameref = BashShellState.NamerefTarget(Name) is not null;
            if (!isNameref) return null;
            string target = BashShellState.ResolveNameref(Name);
            // No target yet, an element (`a[2]`: its scalar read is ReadThroughNameref's), or a cycle.
            if (target.Contains('[') || BashShellState.NamerefTarget(target) is not null) return null;
            return target;
        }

        public override object? Value
        {
            get => Target(out bool isNameref) is { } target ? ArrayHolder(state, target) : isNameref ? null : _own;
            set
            {
                if (Target(out bool isNameref) is { } target) SetArray(state, target, value!);
                else if (!isNameref) _own = value;
            }
        }
    }

    /// <summary>
    /// Assign an array the way bash scopes it: the existing variable (in whichever scope owns it: a function writing
    /// a caller's array reaches the caller), else a new GLOBAL. A bare <c>PSVariable.Set</c> from a cmdlet creates the
    /// variable in the calling function's own scope, so <c>mapfile arr</c> / <c>read -a arr</c> inside a function
    /// never reached the shell.
    /// </summary>
    public static void SetArray(SessionState state, string name, object holder)
    {
        name = BashNameCase.ToStore(name);
        PSVariable? existing = null;
        try { existing = state.PSVariable.Get(name); }
        catch (Exception e) when (e is ArgumentException or SessionStateException) { /* not a PowerShell name */ }
        // In a subshell (`( mapfile -t m <<< x )`, `x=$(read -a a <<< "1 2")`, a write through a nameref) the array is
        // the subshell's own and ends with it, as an assigned one (`( arr=(1 2) )`) does. REGRESSION: a new one was made
        // global, and an existing one the subshell inherited was written in place; both leaked out of it.
        if (BashRuntime.SubshellDepth(state) > 0)
            WriteInSubshell(state, name, holder);
        else if (existing is not null && !PowerShellOwnedVariables.Contains(name)
            && (existing.Options & (ScopedItemOptions.ReadOnly | ScopedItemOptions.Constant)) == 0)
            existing.Value = holder;
        else
            state.PSVariable.Set("global:" + name, holder);
    }

    // A PowerShell-owned name (`$args`, which every scope has) is never looked for nearer than the subshell's own scope.
    private static void WriteInSubshell(SessionState state, string name, object holder)
        => state.InvokeCommand.InvokeScript(true, SetInSubshellScope, null, name, holder, !PowerShellOwnedVariables.Contains(name));

    /// <summary>
    /// Sets variable <c>$args[0]</c> = <c>$args[1]</c> within the innermost subshell: in the nearest scope, out to that
    /// subshell's, that holds the variable itself (when <c>$args[2]</c>), else in the subshell's own scope, the nearest
    /// one holding <see cref="BashRuntime.SubshellDepthVariable"/> itself (BashShellState.Save sets it there); past the
    /// global scope, the global one. Run in a child scope (its own variables stay there); scope numbers count up from it.
    /// </summary>
    private static readonly ScriptBlock SetInSubshellScope = ScriptBlock.Create(
        "for ($i = 1; ; $i++) { try { "
        + "if ($args[2] -and $null -ne (Get-Variable -Name $args[0] -Scope $i -ErrorAction Ignore)) { Set-Variable -Name $args[0] -Value $args[1] -Scope $i; return }; "
        + "$null = Get-Variable -Name '" + BashRuntime.SubshellDepthVariable + "' -Scope $i -ErrorAction Stop; "
        + "Set-Variable -Name $args[0] -Value $args[1] -Scope $i; return } "
        + "catch [System.Management.Automation.ItemNotFoundException] { } "
        + "catch { Set-Variable -Name $args[0] -Value $args[1] -Scope Global; return } }");

    /// <summary>
    /// The variable <c>$args[0]</c> within the innermost subshell (see <see cref="SetInSubshellScope"/>): the nearest one
    /// out to that subshell's scope (when <c>$args[1]</c>), else the one in the subshell's own scope, made there holding
    /// what that scope sees when missing; nothing past the global scope.
    /// </summary>
    private static readonly ScriptBlock SlotInSubshellScope = ScriptBlock.Create(
        "for ($i = 1; ; $i++) { try { "
        + "$__psbSlot = Get-Variable -Name $args[0] -Scope $i -ErrorAction Ignore; "
        + "if ($args[1] -and $null -ne $__psbSlot) { return $__psbSlot }; "
        + "$null = Get-Variable -Name '" + BashRuntime.SubshellDepthVariable + "' -Scope $i -ErrorAction Stop; "
        + "if ($null -ne $__psbSlot) { return $__psbSlot }; "
        + "$__psbSeen = $null; "
        + "for ($j = $i + 1; ; $j++) { try { $__psbSeen = (Get-Variable -Name $args[0] -Scope $j -ErrorAction Stop).Value; break } "
        + "catch [System.Management.Automation.ItemNotFoundException] { } catch { break } }; "
        + "Set-Variable -Name $args[0] -Value $__psbSeen -Scope $i; return (Get-Variable -Name $args[0] -Scope $i) } "
        + "catch [System.Management.Automation.ItemNotFoundException] { } "
        + "catch { return } }");

    /// <summary>
    /// Replace the array <paramref name="name"/> holds with <paramref name="holder"/> in the scope that owns it (the
    /// caller's array when a function edits one element), else in the caller's own scope; in a subshell, within it
    /// (see <see cref="SetArray"/>).
    /// </summary>
    public static void ReplaceArray(SessionState state, string name, object holder)
    {
        name = BashNameCase.ToStore(name);
        if (BashRuntime.SubshellDepth(state) > 0) WriteInSubshell(state, name, holder);
        else if (ArrayVariable(state, name) is { } owner) owner.Value = holder;
        else state.PSVariable.Set(name, holder);
    }

    /// <summary>Remove an array variable (the shell variable store is not touched).</summary>
    public static void RemoveArray(SessionState state, string name)
    {
        name = BashNameCase.ToStore(name);
        if (ArrayVariable(state, name) is not null) state.PSVariable.Remove(name);
    }

    private static PSVariable? ArrayVariable(SessionState state, string name)
    {
        name = BashNameCase.ToStore(name);
        if (name.Length == 0 || PowerShellOwnedVariables.Contains(name)) return null;
        PSVariable? variable;
        try { variable = state.PSVariable.Get(name); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or SessionStateException)
        {
            return null; // not a name PowerShell accepts
        }
        object? held = variable?.Value is PSObject wrapped ? wrapped.BaseObject : variable?.Value;
        return held is IDictionary || (held is IList && held is not string) ? variable : null;
    }
}
