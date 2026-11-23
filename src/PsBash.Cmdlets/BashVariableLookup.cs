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
            journal?.Note(nearest, made: null, state);
            return nearest;
        }
        string global = "global:" + name;
        if (state.PSVariable.Get(global) is { } existing)
        {
            // A name PowerShell owns and keeps read-only (`Error`): a subshell's array of that name is its own variable,
            // gone with its scope. REGRESSION: `( Error=(e); Error+=(f) )` failed "Cannot overwrite variable Error"; then
            // the second write made it afresh (`f`, not `e f`): one the subshell already made is reused.
            if (journal is not null && !Writable(existing) && PowerShellOwnedVariables.Contains(name))
            {
                if (state.PSVariable.Get(name) is { } made && Writable(made) && !ReferenceEquals(made, existing)) return made;
                try
                {
                    state.PSVariable.Set(name, null);
                    if (state.PSVariable.Get(name) is { } own && Writable(own)) return own;
                }
                catch (SessionStateException) { /* AllScope (`Host`): the write fails as it always did */ }
            }
            journal?.Note(existing, made: null, state);
            return existing;
        }
        state.PSVariable.Set(global, null);
        PSVariable created = state.PSVariable.Get(global)!;
        journal?.Note(created, made: name, state);
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
    internal sealed class ArrayWriteJournal
    {
        private readonly VarFrameStack _frames;
        private readonly int _depth;
        private readonly long _openedAt;

        // When each journal opened and each entry was noted: one clock for every journal, so a journal can tell which of a
        // concurrent sibling's entries came after it opened.
        private static long s_clock;

        private readonly List<Entry> _writes = [];

        // The entries of each name (PowerShell variable names ignore case), and of each variable noted: O(1) per write.
        private readonly Dictionary<string, List<int>> _byName = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<PSVariable> _seen = new(ReferenceEqualityComparer.Instance);

        private readonly record struct Entry(PSVariable Variable, object? Before, string? Made, long NotedAt);

        internal ArrayWriteJournal(VarFrameStack frames, int depth)
        {
            _frames = frames;
            _depth = depth;
            _openedAt = Interlocked.Increment(ref s_clock);
            frames.OpenJournals.Add(this);
        }

        /// <summary>The variables noted so far (each is put back once).</summary>
        internal int Count => _writes.Count;

        /// <summary>
        /// A write to <paramref name="variable"/> follows; <paramref name="made"/> is its name when this write made it
        /// global. Only the first write to a variable counts (a later note of it that is not recognized is harmless:
        /// the entries are put back newest first). The value kept is a copy when the write may change it in place (an
        /// associative or sparse array: <c>m[k]=v</c>, <c>sp+=(x)</c>), else the subshell's write would be kept too.
        /// </summary>
        public void Note(PSVariable variable, string? made, SessionState? state = null)
        {
            if (made is null && IsLocalOfACallInside(variable, state)) return;
            if (_seen.Contains(variable)) return;
            string name = variable.Name;
            object? now = variable.Value;
            if (_byName.TryGetValue(name, out var entries))
            {
                // A function's local may be a new PSVariable per lookup: it is known by the array it holds (every write
                // stores a new one). Only the newest entries are looked at, so a name noted many times stays O(1).
                for (int k = entries.Count - 1, stop = Math.Max(0, entries.Count - 4); k >= stop; k--)
                    if (now is not null && ReferenceEquals(_writes[entries[k]].Variable.Value, now)) return;
            }
            else _byName[name] = entries = new List<int>(1);
            entries.Add(_writes.Count);
            _seen.Add(variable);
            object? before;
            if (made is null && SiblingBefore(variable, now) is { } sibling)
            {
                before = Kept(sibling.Before);
                made = sibling.Made;
            }
            else before = Kept(now);
            _writes.Add(new Entry(variable, before, made, Interlocked.Increment(ref s_clock)));
        }

        private static object? Kept(object? value)
        {
            object? raw = value is PSObject wrapped ? wrapped.BaseObject : value;
            return raw is IDictionary ? BashShellState.CloneMutable(raw) : value;
        }

        /// <summary>
        /// The variable is a local array of a function called inside the subshell: it ends with that call, so it is not
        /// noted (REGRESSION: every call's <c>local -a p</c> was, and <c>seq N | while read n; do parse; done</c> went
        /// quadratic). Decided by the variable written, not its name alone: a function inside that names it local
        /// (its prologue lists every <c>local</c>, run or not) and no function outside does, and the variable is not the
        /// shell's global. REGRESSION: <c>g() { if false; then local -a a; fi; h; }</c> with <c>h() { a+=(3); }</c>, run as
        /// <c>( g )</c>, kept the 3 in the shell's <c>a</c>. Noting a variable that dies anyway is harmless; missing one
        /// outside is not, so a doubt notes it.
        /// </summary>
        private bool IsLocalOfACallInside(PSVariable variable, SessionState? state)
        {
            _frames.LocalArrayDepths(variable.Name, out int innermost, out int outermost);
            if (innermost <= _depth || outermost <= _depth) return false;
            if (state is null) return true;
            PSVariable? global = null;
            try { global = state.PSVariable.Get("global:" + variable.Name); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or SessionStateException) { }
            return global is null || !(ReferenceEquals(global, variable) || global.Value is not null && ReferenceEquals(global.Value, variable.Value));
        }

        /// <summary>
        /// A pipe stage's sibling stages run interleaved with it, each a subshell with its own journal: the entry of
        /// another open journal for this variable noted after this one opened, the earliest, holds the value the variable
        /// had when this subshell started. REGRESSION: <c>a=(0); { a+=(u); echo 1; } | while read x; do a+=(d); done</c>
        /// left <c>0 u</c>: the loop's journal kept the upstream's write as its "before" and put it back last.
        /// </summary>
        private Entry? SiblingBefore(PSVariable variable, object? now)
        {
            Entry? earliest = null;
            foreach (var other in _frames.OpenJournals)
            {
                if (ReferenceEquals(other, this) || !other._byName.TryGetValue(variable.Name, out var entries)) continue;
                foreach (int i in entries)
                {
                    Entry e = other._writes[i];
                    if (e.NotedAt < _openedAt || earliest is { } best && best.NotedAt <= e.NotedAt) continue;
                    if (ReferenceEquals(e.Variable, variable) || now is not null && ReferenceEquals(e.Variable.Value, now)) earliest = e;
                }
            }
            return earliest;
        }

        /// <summary>
        /// The subshell ended: every variable it wrote holds what it held before, newest first. Each is put back on its
        /// own, so one that cannot be (made read-only meanwhile) leaves the others, and the rest of the restore, done.
        /// </summary>
        public void Undo(SessionState? state)
        {
            _frames.OpenJournals.Remove(this);
            for (int i = _writes.Count - 1; i >= 0; i--)
            {
                var (variable, before, made, _) = _writes[i];
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
            _seen.Clear();
        }

        /// <summary>
        /// The variable lives outside this subshell (the shell's global, a calling function's local, or one the
        /// subshell already wrote): an <c>unset</c> in the subshell empties it, journaled, rather than removing it.
        /// </summary>
        internal bool HoldsOutside(PSVariable variable, SessionState state)
            => _seen.Contains(variable) || !IsLocalOfACallInside(variable, state);
    }

    /// <summary>
    /// <c>unset NAME</c>, after the builtin's own removal from its caller's scope: when NAME still shows the array it
    /// showed (a function's local, which PowerShell cannot remove from a compiled scope, or the shell's global), bash's
    /// unset of that variable, which depends on whose it is:
    /// <list type="bullet">
    /// <item>In a subshell, one that lives outside it (the shell's, written in place: <see cref="ArrayWriteJournal"/>) is
    /// emptied, journaled, so the subshell's end puts it back. REGRESSION: <c>( mapfile -t m &lt;&lt;&lt; x; unset m;
    /// echo ${#m[@]} )</c> printed 1.</item>
    /// <item>The shell's global is removed (<c>declare -p</c> then finds nothing).</item>
    /// <item>A local of the function running the unset stays its local, unset: emptied
    /// (<c>f() { local -a q=(l); unset q; echo ${#q[@]}; }</c> prints 0, whatever the global holds).</item>
    /// <item>A calling function's local goes, so the shell's array of the name shows again there
    /// (<c>a=(G); g() { unset a; }; f() { local -a a=(1); g; echo ${a[@]}; }</c> prints G): it takes the global's
    /// value. REGRESSION: it was emptied, so the empty local hid the global (<c>[]</c>).</item>
    /// </list>
    /// </summary>
    public static void UnsetSurvivingArray(SessionState state, string name, object? before)
    {
        if (before is null || ArrayVariable(state, name) is not { } survivor || !Writable(survivor)) return;
        object? held = survivor.Value is PSObject wrapped ? wrapped.BaseObject : survivor.Value;
        if (!ReferenceEquals(held, before)) return;
        ArrayWriteJournal? journal = Journal(state);
        if (journal is not null && journal.HoldsOutside(survivor, state))
        {
            journal.Note(survivor, made: null, state);
            survivor.Value = null;
            return;
        }
        string store = BashNameCase.ToStore(name);
        PSVariable? global = null;
        try { global = state.PSVariable.Get("global:" + store); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or SessionStateException) { }
        try
        {
            if (global is not null && (ReferenceEquals(global, survivor) || ReferenceEquals(global.Value, survivor.Value)))
                state.PSVariable.Remove("global:" + store);
            else
            {
                var frames = VarFrameStack.Current;
                frames.LocalArrayDepths(store, out int innermost, out _);
                survivor.Value = innermost == frames.Depth ? null : global?.Value;
            }
        }
        catch (Exception e) when (e is SessionStateException or InvalidOperationException or ArgumentException) { }
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
    /// never reached the shell.
    /// <para>A nameref writes its target (<c>read -a ref</c>, <c>mapfile ref</c>). When the array written is the shell's
    /// global, the global scalar of the name is no longer its value and is removed: a scalar lives only in the store, an
    /// array only as a PowerShell variable (<c>environment-model.md</c> section 8), and <c>$x</c> reads the array's
    /// element 0 (the emitter marks <c>read -a NAME</c> / <c>mapfile NAME</c> operands as arrays). A function's local array
    /// leaves the global scalar alone: it comes back when the function returns. No copy of element 0 goes to the store
    /// either: it went stale on the next element write and a child saw it exported, where bash exports no array.
    /// REGRESSION: <c>declare -n ref=x; x=X; read -ra ref &lt;&lt;&lt; "m n"</c> made an array named ref and left x X;
    /// <c>read -a x</c> left x's old scalar (or the words joined) in the store, which <c>$x</c> and a child's environment
    /// still read.</para>
    /// </summary>
    public static void SetArray(SessionState state, string name, object holder)
    {
        if (BashShellState.ResolveNameref(name) is { } target && target.IndexOf('[') < 0) name = target;
        name = BashNameCase.ToStore(name);
        if (WriteArray(state, name, holder) && BashVariableStore.Get(name) is not null) BashVariableStore.Set(name, null);
    }

    /// <summary>The write <see cref="SetArray"/> makes; true when the array written is the shell's global (not a function's local).</summary>
    private static bool WriteArray(SessionState state, string name, object holder)
    {
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
            journal?.Note(existing, made: null, state);
            existing.Value = holder;
            return ReferenceEquals(existing, state.PSVariable.Get(global));
        }
        PSVariable? before = journal is null ? null : state.PSVariable.Get(global);
        if (before is not null) journal!.Note(before, made: null, state);
        state.PSVariable.Set(global, holder);
        if (journal is not null && before is null && state.PSVariable.Get(global) is { } created) journal.Note(created, made: name, state);
        return true;
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
            Journal(state)?.Note(owner, made: null, state);
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
