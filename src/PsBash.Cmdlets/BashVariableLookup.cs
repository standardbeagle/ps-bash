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
    /// else the shell's global, made (unset) when missing. In a subshell it is always the nearest one (the subshell's
    /// own, a caller's local or the shell's: the one the subshell's code reads), and the subshell's
    /// <see cref="ArrayWriteJournal"/> notes it, so the write ends with the subshell. REGRESSION: <c>( f() { arr=(x y); };
    /// f )</c> wrote a variable local to f, so the subshell never saw x y.
    /// </summary>
    public static PSVariable ArrayWriteSlot(SessionState state, string name, bool callerLocal)
    {
        name = BashNameCase.ToStore(name);
        ArrayWriteJournal? journal = Journal(state);
        if ((callerLocal || journal is not null && !PowerShellOwnedVariables.Contains(name))
            && state.PSVariable.Get(name) is { } nearest && Writable(nearest))
        {
            journal?.Note(nearest, made: null);
            return nearest;
        }
        string global = "global:" + name;
        if (state.PSVariable.Get(global) is { } existing)
        {
            journal?.Note(existing, made: null);
            return existing;
        }
        state.PSVariable.Set(global, null);
        PSVariable created = state.PSVariable.Get(global)!;
        journal?.Note(created, made: name);
        return created;
    }

    private static bool Writable(PSVariable variable)
        => (variable.Options & (ScopedItemOptions.ReadOnly | ScopedItemOptions.Constant)) == 0;

    // The variable a subshell's journal sits in (BashShellState.Save opens one in the subshell's scope).
    private const string JournalVariable = "__psbash_subshell_arrays";

    /// <summary>
    /// The innermost subshell's <see cref="ArrayWriteJournal"/>, null outside a subshell: one variable lookup, no
    /// exception and no script, since every array write in a pipe stage, <c>$( )</c> or <c>( )</c> asks.
    /// </summary>
    private static ArrayWriteJournal? Journal(SessionState state)
    {
        object? value = null;
        try { value = state.PSVariable.GetValue(JournalVariable); } catch (InvalidOperationException) { }
        return (value is PSObject wrapped ? wrapped.BaseObject : value) as ArrayWriteJournal;
    }

    /// <summary>
    /// A subshell starts (<see cref="BashShellState.Save"/>, in the subshell's scope): its array writes are journaled
    /// from here until <see cref="ArrayWriteJournal.Undo"/>.
    /// </summary>
    internal static ArrayWriteJournal OpenJournal(SessionState state)
    {
        var frames = VarFrameStack.Current;
        var journal = new ArrayWriteJournal(frames, frames.Depth);
        state.PSVariable.Set(JournalVariable, journal);
        return journal;
    }

    /// <summary>
    /// The arrays a subshell wrote that are not its own (the shell's, a caller's local), each with the value it held
    /// before the first write: bash forks, so the subshell writes its copy. ps-bash writes the variable itself (the one
    /// the subshell's code reads, found as any write finds it: no scope walk) and puts each back when the subshell ends.
    /// A variable the subshell made global is removed then. REGRESSION: a scope walk per write (<c>Get-Variable -Scope
    /// $i</c> in a script, each miss an exception) made <c>seq 10000 | while read n; do add "$n"; done</c> with
    /// <c>add() { items+=("$1"); }</c> pay two scripts per append.
    /// <para>Its size is the number of distinct outside arrays written, whatever the number of writes: a local array of a
    /// function called inside the subshell is not noted (it ends with the call), and a name is looked up in O(1).
    /// REGRESSION: every call's <c>local -a p; read -ra p</c> was noted (each call's local is a new variable) and each
    /// note scanned all earlier ones, so <c>seq N | while read n; do parse "$n"; done</c> was quadratic in N.</para>
    /// </summary>
    internal sealed class ArrayWriteJournal(VarFrameStack frames, int depth)
    {
        private readonly List<(PSVariable Variable, object? Before, string? Made)> _writes = [];

        // The entries of each name (PowerShell variable names ignore case): almost always one.
        private readonly Dictionary<string, List<int>> _byName = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The variables noted so far (each is put back once).</summary>
        internal int Count => _writes.Count;

        /// <summary>
        /// A write to <paramref name="variable"/> follows; <paramref name="made"/> is its name when this write made it
        /// global. Only the first write to a variable counts. A function's local is a new <see cref="PSVariable"/> per
        /// lookup, so one is known by its name and the value it holds (every array write stores a new array). The value
        /// kept is a copy when the write may change it in place (an associative or sparse array: <c>m[k]=v</c>,
        /// <c>sp+=(x)</c>), else the subshell's write would be kept too.
        /// </summary>
        public void Note(PSVariable variable, string? made)
        {
            string name = variable.Name;
            // A local array of a function the subshell called: it ends with that call, inside the subshell.
            if (made is null && frames.InnermostLocalArrayDepth(name) > depth) return;
            object? now = variable.Value;
            if (_byName.TryGetValue(name, out var entries))
            {
                foreach (int i in entries)
                {
                    PSVariable seen = _writes[i].Variable;
                    if (ReferenceEquals(seen, variable) || now is not null && ReferenceEquals(seen.Value, now)) return;
                }
            }
            else _byName[name] = entries = new List<int>(1);
            entries.Add(_writes.Count);
            object? raw = now is PSObject wrapped ? wrapped.BaseObject : now;
            _writes.Add((variable, raw is IDictionary ? BashShellState.CloneMutable(raw) : now, made));
        }

        /// <summary>
        /// The subshell ended: every variable it wrote holds what it held before, newest first. Each is put back on its
        /// own, so one that cannot be (made read-only meanwhile) leaves the others, and the rest of the restore, done.
        /// </summary>
        public void Undo(SessionState? state)
        {
            for (int i = _writes.Count - 1; i >= 0; i--)
            {
                var (variable, before, made) = _writes[i];
                try
                {
                    if (made is not null) state?.PSVariable.Remove("global:" + made);
                    else variable.Value = before;
                }
                catch (Exception e) when (e is SessionStateException or InvalidOperationException or ArgumentException)
                {
                    // That variable keeps the subshell's value; the others are still put back.
                }
            }
            _writes.Clear();
            _byName.Clear();
        }
    }

    /// <summary>
    /// <c>unset NAME</c>, after the builtin removed the variable from its caller's scope: when the array NAME held is
    /// still the one seen (it lives further out: the shell's global, a calling function's local the caller's scope does
    /// not own, an array a subshell wrote in place), that variable is emptied, so NAME reads as unset as bash's does. In
    /// a subshell its journal puts the array back when the subshell ends. REGRESSION: <c>( mapfile -t m &lt;&lt;&lt; x;
    /// unset m; echo ${#m[@]} )</c> printed 1, since the subshell's array is written in place (the journal) and the
    /// builtin removed only its caller's scope.
    /// </summary>
    public static void UnsetSurvivingArray(SessionState state, string name, object? before)
    {
        if (before is null || ArrayVariable(state, name) is not { } survivor || !Writable(survivor)) return;
        object? held = survivor.Value is PSObject wrapped ? wrapped.BaseObject : survivor.Value;
        if (!ReferenceEquals(held, before)) return;
        Journal(state)?.Note(survivor, made: null);
        survivor.Value = null;
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
        // The subscript-error mark describes THIS write only: one left by an earlier write whose emitted code does not
        // take it (`${r:=w}`) must not make a later failure (`q=1bad`) end the shell.
        BashArrayElement.TakeSubscriptError();
        ViewNameref(state, name);
        if (BashShellState.WriteTarget(name, value, out bool invalid) is not { } target)
            return invalid ? "`" + value + "': not a valid identifier" : null;
        int br = target.IndexOf('[');
        if (br > 0 && target[^1] == ']')
            return BashArrayElement.WriteThroughReference(state, target[..br], target[(br + 1)..^1], value, append);
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
    /// An array assignment to NAME while it is a nameref with no target (<c>typeset -n ref; ref=(x y)</c>): bash removes
    /// the nameref attribute, with a warning, and assigns the array to NAME itself. True when NAME was such a nameref
    /// (the emitted code then prints the warning: <c>PsBuild.ArrayAssignToNameref</c>). Its <see cref="ViewNameref"/>
    /// variable, no nameref's view any more, then holds the array as a plain variable does.
    /// </summary>
    public static bool DropTargetlessNameref(string name)
    {
        if (BashShellState.NamerefTarget(name) is not { Length: 0 }) return false;
        BashShellState.TakeNameref(name);
        return true;
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
    /// never reached the shell. True when the array written is the shell's global (not a function's local).
    /// </summary>
    public static bool SetArray(SessionState state, string name, object holder)
    {
        name = BashNameCase.ToStore(name);
        PSVariable? existing = null;
        try { existing = state.PSVariable.Get(name); }
        catch (Exception e) when (e is ArgumentException or SessionStateException) { /* not a PowerShell name */ }
        // In a subshell (`( mapfile -t m <<< x )`, `x=$(read -a a <<< "1 2")`, a write through a nameref) the write ends
        // with it, as an assigned one (`( arr=(1 2) )`) does: its journal puts the variable back. REGRESSION: a new one
        // was made global, and an existing one the subshell inherited was written in place; both leaked out of it.
        ArrayWriteJournal? journal = Journal(state);
        string global = "global:" + name;
        if (existing is not null && !PowerShellOwnedVariables.Contains(name) && Writable(existing))
        {
            journal?.Note(existing, made: null);
            existing.Value = holder;
            return ReferenceEquals(existing, state.PSVariable.Get(global));
        }
        PSVariable? before = journal is null ? null : state.PSVariable.Get(global);
        if (before is not null) journal!.Note(before, made: null);
        state.PSVariable.Set(global, holder);
        if (journal is not null && before is null && state.PSVariable.Get(global) is { } created) journal.Note(created, made: name);
        return true;
    }

    /// <summary>
    /// <see cref="SetArray"/> for a builtin that fills a whole array (<c>read -a</c>, <c>mapfile</c>): when the array is
    /// the shell's global, the shell variable of the name is its element 0 (<c>read -a r &lt;&lt;&lt; "x y"; echo $r</c>
    /// prints x, also over an earlier <c>r=old</c>), unset when it has none. A function's local array gets no such copy:
    /// it would be a global that outlives the local. REGRESSION: the JOINED value always went to the shell variable, so
    /// <c>$r</c> printed <c>x y</c>, and after <c>f(){ local -a r; read -a r &lt;&lt;&lt; "x y"; }; f</c> a global r remained.
    /// </summary>
    public static void SetWholeArray(SessionState state, string name, string[] elements)
    {
        if (!SetArray(state, name, elements)) return;
        try { BashVariableStore.Set(BashNameCase.ToStore(name), elements.Length > 0 ? elements[0] : null); }
        catch (Exception) { /* a name the store refuses (restricted): the array stands, as before */ }
    }

    /// <summary>
    /// Replace the array <paramref name="name"/> holds with <paramref name="holder"/> in the scope that owns it (the
    /// caller's array when a function edits one element), else in the caller's own scope; in a subshell, until it
    /// ends (see <see cref="SetArray"/>).
    /// </summary>
    public static void ReplaceArray(SessionState state, string name, object holder)
    {
        name = BashNameCase.ToStore(name);
        if (ArrayVariable(state, name) is { } owner)
        {
            Journal(state)?.Note(owner, made: null);
            owner.Value = holder;
        }
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
