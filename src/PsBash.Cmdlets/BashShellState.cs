using System.Collections;
using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Save / restore of the shell state a bash SUBSHELL (<c>( … )</c>, a wrapped <c>$( … )</c>) must not leak
/// into its parent. bash forks, so nothing a subshell changes survives it; ps-bash runs the body in the same
/// runspace and process, so the emitter (<c>PsBuild.ShellStateScope</c>) brackets it with
/// <see cref="Save"/> / <see cref="Restore"/>. The body also runs in a child PowerShell scope (<c>&amp; { }</c>),
/// which already isolates PS-scoped state: bash arrays (<c>$arr</c>), functions, <c>$ErrorActionPreference</c>
/// (<c>set -e</c>), <c>Set-StrictMode</c> (<c>set -u</c>). What a child scope does NOT isolate is saved here:
/// <list type="bullet">
/// <item>bash variables — <c>$env:NAME</c>, read and written through <see cref="BashVariableStore"/>;</item>
/// <item>the <c>$global:</c> shell state: errexit, <c>$-</c> flags, positional parameters, <c>BASH_REMATCH</c>,
/// traps, <c>$!</c>, the xtrace flag;</item>
/// <item>the per-runspace <c>shopt</c> table and file-creation mask (<c>umask</c>);</item>
/// <item><c>Set-PSDebug</c> tracing (<c>set -x</c>) — session-wide and not queryable, so it is tracked by
/// <c>$global:__BashXtrace</c> and <see cref="Restore"/> returns the level the caller must re-apply.</item>
/// <item>the working directory, both halves (the PowerShell location and the process's
/// <c>Environment.CurrentDirectory</c>, which raw .NET path APIs and the fused lane read), and the
/// <c>pushd</c> stack (PowerShell's default location stack).</item>
/// </list>
/// The working directory lives here, not in the emitter, so that every subshell form gets it:
/// <c>( … )</c>, <c>$( … )</c>, and each pipeline stage. When only <c>( … )</c> wrapped its body in
/// <c>Push-Location</c>/<c>Pop-Location</c>, <c>x=$(cd /)</c> and <c>f | cat</c> (with <c>f</c> calling
/// <c>cd</c>) moved the parent shell.
/// NOT restored, on purpose: <c>$?</c>/<c>$LASTEXITCODE</c> (the subshell's status IS the parent's <c>$?</c>).
/// </summary>
public static class BashShellState
{
    // The readonly, attribute and nameref tables are keyed by STORE name: every member takes a bash name or a store name
    // (`readonly path` is not PATH on Windows; PsBash.Core.BashNameCase, environment-model.md section 12).
    private static string Key(string name) => PsBash.Core.BashNameCase.ToStore(name);

    private static readonly HashSet<string> ReadonlyNames = new(StringComparer.Ordinal);

    /// <summary>Records <c>readonly NAME</c>; only <c>unset</c> consults it (assignment is not blocked: see intentional-differences.md).</summary>
    public static void MarkReadonly(string name) { lock (ReadonlyNames) ReadonlyNames.Add(Key(name)); }

    /// <summary>A function's read-only local goes when the function returns (VarFrameStack.Exit): the name is writable again.</summary>
    internal static void UnmarkReadonly(string name) { lock (ReadonlyNames) ReadonlyNames.Remove(Key(name)); }

    /// <summary>Removes and returns NAME's nameref target (null when none): a local of that name is a plain variable.</summary>
    internal static string? TakeNameref(string name) { lock (Namerefs) return Namerefs.Remove(Key(name), out var t) ? t : null; }

    /// <summary>Puts back the target <see cref="TakeNameref"/> returned (null: NAME is no nameref).</summary>
    internal static void RestoreNameref(string name, string? target)
    {
        lock (Namerefs)
            if (target is null) Namerefs.Remove(Key(name));
            else Namerefs[Key(name)] = target;
    }

    /// <summary>A new, independent invocation starts with nothing readonly, declared or referring (the daemon outlives the
    /// scripts it runs): every per-shell table is emptied.</summary>
    public static void ClearReadonly() { foreach (var table in ShellTables) PutTable(table, null); }

    private static readonly Dictionary<string, string> Namerefs = new(StringComparer.Ordinal);

    /// <summary>Records <c>declare -n NAME=TARGET</c> so <c>declare -p</c> can list it (the emitter resolves the reference itself).
    /// The target is kept as its bash name: it is printed (<c>declare -p</c>) and handed back as a value (<c>typeset +n</c>).
    /// An empty target is a nameref declared with no target yet (<c>declare -n r</c>): <c>declare -p</c> prints
    /// <c>declare -n r</c>, <c>set</c> lists nothing, and resolving it gives the name itself (<see cref="ResolveNameref"/>).</summary>
    public static void SetNameref(string name, string target)
    {
        lock (Namerefs) Namerefs[Key(name)] = PsBash.Core.BashNameCase.ToBash(target);
    }

    /// <summary>
    /// <c>declare +n NAME</c>: the name is a plain variable again, whose value is the reference's own: the target name
    /// (<c>typeset -n ref=x; typeset +n ref</c> leaves ref=x; it was left unset). A nameref with no target leaves a
    /// declared, unset scalar (bash: <c>declare -n r; declare +n r; declare -p r</c> prints <c>declare -- r</c>). Not a
    /// nameref: <paramref name="current"/>.
    /// </summary>
    public static string? ClearNameref(string name, object? current)
    {
        string? target;
        lock (Namerefs)
            if (!Namerefs.Remove(Key(name), out target)) return current?.ToString();
        if (target.Length > 0) return target;
        MarkAttribute(name, "-");
        return null;
    }

    internal static string? NamerefTarget(string name) { lock (Namerefs) return Namerefs.TryGetValue(Key(name), out var t) ? t : null; }

    /// <summary>
    /// The variable NAME stands for when the shell resolves it at run time: its nameref target, followed through a chain
    /// (a cycle stops where it closes), or NAME itself. <c>c[1]</c> resolves the base (<c>m[1]</c>); a base that names an
    /// element (<c>declare -n c='a[2]'</c>) takes no subscript, so the name is left as written.
    /// </summary>
    public static string ResolveNameref(string name)
    {
        lock (Namerefs)
        {
            if (Namerefs.Count == 0) return name;
            int bracket = name.IndexOf('[');
            string current = bracket < 0 ? name : name[..bracket];
            for (int hop = 0; hop < 16 && Namerefs.TryGetValue(Key(current), out var next) && next.Length > 0 && next != current; hop++)
                current = next;
            if (bracket < 0) return current;
            return current.Contains('[') ? name : current + name[bracket..];
        }
    }

    /// <summary>
    /// Where a write of <paramref name="value"/> to NAME goes: <see cref="ResolveNameref"/>, unless the chain ends at a
    /// nameref with NO target yet. bash then makes the value its target instead of storing it, whichever writer it is
    /// (<c>declare -n r; r=$v</c>, <c>read r</c>, <c>printf -v r</c>, <c>getopts o r</c>, <c>local -n r; r=$1</c>). That
    /// returns null, with <paramref name="invalid"/> true when the value names no variable (<c>r=5</c>, <c>r='a b'</c>,
    /// <c>r=</c>: "`5': not a valid identifier", nothing changes). An element (<c>a[2]</c>) is a target, as in
    /// <c>declare -n r='a[2]'</c>. THE one place this rule lives (<c>BashVariableLookup.AssignThroughNameref</c>,
    /// <c>BashArrayElement.Assign</c>). REGRESSION: only a literal <c>r=name</c> the emitter saw named the target; any other
    /// writer stored the value in r.
    /// </summary>
    public static string? WriteTarget(string name, string? value, out bool invalid)
    {
        invalid = false;
        if (name.IndexOf('[') >= 0) return ResolveNameref(name);
        lock (Namerefs)
        {
            string current = name;
            for (int hop = 0; hop < 16 && Namerefs.TryGetValue(Key(current), out var next) && next != current; hop++)
            {
                if (next.Length == 0)
                {
                    if (value is null || !BashArrayElement.IsTarget(value)) invalid = true;
                    else Namerefs[Key(current)] = PsBash.Core.BashNameCase.ToBash(value);
                    return null;
                }
                current = next;
            }
            return current;
        }
    }

    /// <summary>
    /// One pass of <c>for NAME in WORDS</c> where NAME is a nameref: the word becomes its target, whatever it referred to
    /// (bash: <c>declare -n r; for r in a b; do r=1; done</c> sets a and b). False when the word names no variable
    /// (<c>`b c': not a valid identifier</c>): the loop ends there, status 1.
    /// </summary>
    public static bool BindLoopNameref(string name, string word)
    {
        if (!BashArrayElement.IsTarget(word)) return false;
        SetNameref(name, word);
        return true;
    }

    internal static string[] NamerefNames() { lock (Namerefs) return [.. Namerefs.Keys]; }

    private static readonly Dictionary<string, string> Attributes = new(StringComparer.Ordinal);

    /// <summary>
    /// Records a <c>declare</c> attribute letter (<c>i</c> integer) for <c>declare -p</c>. <c>x</c> is not kept here: the
    /// export attribute has one home, <see cref="PsBash.Core.ShellExports"/>, so <c>declare -x NAME</c> exports.
    /// </summary>
    public static void MarkAttribute(string name, string letter)
    {
        if (letter == "x") { PsBash.Core.ShellExports.Export(name); return; }
        name = Key(name);
        lock (Attributes)
        {
            Attributes.TryGetValue(name, out var cur);
            // -l and -u replace each other (bash keeps the last).
            if (letter is "l" or "u" && cur is not null) cur = cur.Replace(letter == "l" ? "u" : "l", "");
            Attributes[name] = cur is not null && cur.Contains(letter) ? cur : (cur ?? "") + letter;
        }
    }

    /// <summary>
    /// <c>NAME=VALUE</c> / <c>NAME+=VALUE</c> written in a unit that also runs declarations built at run time
    /// (<c>declare "$1"</c>, <c>eval "$c"</c>, <c>$d x</c>): the emitter cannot know NAME's attributes, so the write
    /// consults this table (PsBuild.AssignAttributed). It goes to NAME's nameref target; <c>-u</c>/<c>-l</c> convert the
    /// value (the appended whole, for <c>+=</c>). False, writing nothing, when the target is <c>-i</c>: the caller then
    /// evaluates the value as arithmetic and stores it with <see cref="AssignEvaluated"/>. A write that names a target-less
    /// nameref's target is made here (<see cref="WriteTarget"/>) and is true.
    /// </summary>
    public static bool AssignScalar(string name, string? value, bool append)
    {
        string? target = WriteTarget(name, value, out _);
        if (target is null) return true;
        if (target.IndexOf('[') >= 0) target = name;   // an element target: the name's own variable, as before
        string? letters = null;
        lock (Attributes)
            if (Attributes.Count > 0) Attributes.TryGetValue(Key(target), out letters);
        if (letters is not null && letters.Contains('i')) return false;
        string written = append ? (BashVariableStore.Get(target) ?? "") + (value ?? "") : value ?? "";
        if (letters is not null)
        {
            if (letters.Contains('u')) written = written.ToUpperInvariant();
            else if (letters.Contains('l')) written = written.ToLowerInvariant();
        }
        BashVariableStore.Set(target, written);
        return true;
    }

    /// <summary>The value an integer <see cref="AssignScalar"/> refused, evaluated by the caller: stored in NAME's target.</summary>
    public static void AssignEvaluated(string name, string? value)
    {
        string target = ResolveNameref(name);
        BashVariableStore.Set(target.IndexOf('[') >= 0 ? name : target, value ?? "");
    }

    /// <summary>True when NAME has declare attribute letters (it was declared, if only with no value).</summary>
    internal static bool HasAttributes(string name) { lock (Attributes) return Attributes.ContainsKey(Key(name)); }

    /// <summary>Removes and returns NAME's attribute letters (null when it has none): a local now hides them (VarFrameStack.TakeAttributes).</summary>
    internal static string? TakeAttributes(string name) { lock (Attributes) return Attributes.Remove(Key(name), out var s) ? s : null; }

    /// <summary>Puts back the letters <see cref="TakeAttributes"/> returned, when the local that hid them goes.</summary>
    internal static void RestoreAttributes(string name, string? saved)
    {
        lock (Attributes)
            if (saved is null) Attributes.Remove(Key(name));
            else Attributes[Key(name)] = saved;
    }

    /// <summary><c>declare x</c> (no value, no -a): declared as a scalar that is still unset.</summary>
    internal static bool IsDeclaredScalar(string name) { lock (Attributes) return Attributes.TryGetValue(Key(name), out var s) && s.Contains('-'); }

    /// <summary><c>declare x</c> / <c>declare -a x</c> with no value: declared but unset, so <c>declare -p</c> shows no <c>=()</c>.</summary>
    internal static bool IsDeclaredUnset(string name) { lock (Attributes) return Attributes.TryGetValue(Key(name), out var s) && (s.Contains('-') || s.Contains('~')); }

    /// <summary>The <c>declare -p</c> flag letters of NAME in bash order (<c>i</c>, <c>r</c>, <c>x</c>, <c>l</c>/<c>u</c>), "" for none.</summary>
    internal static string AttributeLetters(string name)
    {
        string set;
        lock (Attributes) set = Attributes.TryGetValue(Key(name), out var s) ? s : "";
        var sb = new System.Text.StringBuilder();
        if (set.Contains('i')) sb.Append('i');
        if (IsReadonly(name)) sb.Append('r');
        if (PsBash.Core.ShellExports.IsExported(name)) sb.Append('x');
        if (set.Contains('l')) sb.Append('l');
        else if (set.Contains('u')) sb.Append('u');
        return sb.ToString();
    }

    internal static bool IsReadonly(string name) { lock (ReadonlyNames) return ReadonlyNames.Contains(Key(name)); }

    /// <summary>
    /// The per-shell name tables: readonly names, declare attributes, namerefs. They are process-wide statics, not runspace
    /// state, so nothing scopes them for free: a subshell copies every one at <see cref="Save"/> and puts it back at
    /// <see cref="Restore"/> (<c>( declare -n r=y ); declare -p r</c> finds nothing, as in bash), and a new invocation
    /// empties every one (<see cref="ClearReadonly"/>). A new table is added HERE, which gives it both (guard
    /// <c>BashShellStateTests.EveryPerShellTable_IsSavedByTheSubshellSnapshot</c>). Declared after the tables: static fields
    /// initialize in text order.
    /// </summary>
    private static readonly object[] ShellTables = [ReadonlyNames, Attributes, Namerefs];

    /// <summary>A copy of each table in <see cref="ShellTables"/>, taken under its lock.</summary>
    private static object[] SaveTables()
    {
        var copies = new object[ShellTables.Length];
        for (int i = 0; i < ShellTables.Length; i++)
            lock (ShellTables[i])
                copies[i] = ShellTables[i] switch
                {
                    HashSet<string> set => new HashSet<string>(set, set.Comparer),
                    Dictionary<string, string> map => new Dictionary<string, string>(map, map.Comparer),
                    var other => throw new InvalidOperationException("no copy for per-shell table " + other.GetType()),
                };
        return copies;
    }

    /// <summary>Makes <paramref name="table"/> hold what <paramref name="saved"/> (a <see cref="SaveTables"/> copy) holds;
    /// null empties it.</summary>
    private static void PutTable(object table, object? saved)
    {
        lock (table)
            switch (table)
            {
                case HashSet<string> set:
                    set.Clear();
                    if (saved is HashSet<string> names) set.UnionWith(names);
                    break;
                case Dictionary<string, string> map:
                    map.Clear();
                    if (saved is Dictionary<string, string> entries)
                        foreach (var (name, value) in entries) map[name] = value;
                    break;
                default:
                    throw new InvalidOperationException("no restore for per-shell table " + table.GetType());
            }
    }

    /// <summary>The first of <paramref name="names"/> that is read-only, by its bash name (for the message), or null (the
    /// emitter's run-time assignment guard).</summary>
    public static string? FirstReadonly(params string[] names)
    {
        lock (ReadonlyNames)
            foreach (var n in names)
                if (ReadonlyNames.Contains(Key(n))) return PsBash.Core.BashNameCase.ToBash(n);
        return null;
    }

    /// <summary>The <c>$global:</c> variables that are shell state (see the class summary).</summary>
    private static readonly string[] GlobalNames =
    [
        "__BashErrexit", "__BashErrexitSuppress", "__BashErrexitTail", "__BashXtrace", "__BashVerbose", "__BashNoglob", "__BashPipefail", "__BashNounset", "__BashNoclobber", "__BashErrtrace",
        "BashFlags", "BashPositional", "BashPositional0", "BASH_REMATCH", "BASH_SOURCE",
        "BashTrapHandlers", "__BashTrapERR", "__BashTrapEXIT", "__BashTrapRETURN", "BashBgLastPid",
        "BashUserAliases", "BashLastArg", "BashDirStack", "__BashShellUnset", FunctionSources.TableVariable,
        // The session history list (InvokeBashHistoryCommand.StateVariable): `( history -s x )`, `fc -s | cat` change only the
        // subshell's copy (bash 5.2).
        "__BashHistoryList",
    ];

    /// <summary>Every shell-state global, for the inventory guard (<see cref="BashStateSnapshot"/>).</summary>
    internal static IReadOnlyList<string> AllGlobalNames => GlobalNames;

    /// <summary>The globals a background job inherits (<see cref="BashStateSnapshot"/>).</summary>
    internal static IEnumerable<string> InheritedGlobalNames
        => GlobalNames.Where(n => Array.IndexOf(BashStateSnapshot.NotInherited, n) < 0);

    private static readonly StringComparer EnvNames =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>A snapshot taken by <see cref="Save"/>. Opaque to PowerShell.</summary>
    public sealed class Snapshot
    {
        internal Snapshot(Dictionary<string, string> env, (bool Exists, object? Value)[] globals,
            Dictionary<string, bool> shopt, bool xtrace, object[] tables,
            Dictionary<string, ScriptBlock?> aliasFunctions, VarFrameStack.Saved varFrames, PsBash.Core.ShellExports.Saved exports,
            WorkingDirectory cwd)
        {
            Exports = exports; Cwd = cwd;
            Env = env; Globals = globals; Shopt = shopt; Xtrace = xtrace; Tables = tables; AliasFunctions = aliasFunctions;
            VarFrames = varFrames;
        }

        /// <summary>The local/temp-binding frames: an <c>unset</c> in the body pops them for the body only.</summary>
        internal VarFrameStack.Saved VarFrames { get; }

        /// <summary>The per-shell tables (<see cref="ShellTables"/>, same order): a <c>readonly</c>, <c>declare -i</c> or
        /// <c>declare -n</c> made in the body ends with it.</summary>
        internal object[] Tables { get; }

        /// <summary>The export table: an <c>export</c> made in the body ends with it.</summary>
        internal PsBash.Core.ShellExports.Saved Exports { get; }

        /// <summary>The function behind each alias (an alias runs as a global function), so one defined or removed
        /// inside the body is undone with the alias table.</summary>
        internal Dictionary<string, ScriptBlock?> AliasFunctions { get; }

        internal Dictionary<string, string> Env { get; }
        internal (bool Exists, object? Value)[] Globals { get; }
        internal Dictionary<string, bool> Shopt { get; }
        internal bool Xtrace { get; }
        internal WorkingDirectory Cwd { get; }

        /// <summary>The file-creation mask: <c>umask 077</c> in the body ends with it.</summary>
        internal int Umask { get; init; }

        /// <summary>The arrays outside the body that it writes (<see cref="BashVariableLookup.ArrayWriteJournal"/>):
        /// each is put back when it ends.</summary>
        internal BashVariableLookup.ArrayWriteJournal? ArrayWrites { get; init; }
    }

    /// <summary>The working directory a subshell must put back (see <see cref="SaveWorkingDirectory"/>).</summary>
    internal sealed record WorkingDirectory(string Location, string ProviderPath, string ProcessCwd, string[] Stack);

    /// <summary>
    /// Reads only: the location, the process cwd, and the <c>pushd</c> stack paths (top first; empty when
    /// nothing is pushed). PowerShell's location cmdlets are expensive on Linux. Each <c>Set-Location</c> /
    /// <c>Push-Location</c> / <c>Pop-Location</c> takes time proportional to the entries in the location's
    /// parent directory: ~47 ms per call under a <c>/tmp</c> with 39k entries. So a subshell must not call
    /// them unless its body actually moved. The old unconditional pair made every <c>( : )</c> cost 114 ms
    /// in a <c>mktemp -d</c> directory.
    /// </summary>
    private static WorkingDirectory SaveWorkingDirectory(SessionState session)
    {
        var here = session.Path.CurrentLocation;
        var stack = session.Path.LocationStack(null);
        var paths = stack.Count == 0 ? [] : new string[stack.Count];
        int i = 0;
        foreach (var entry in stack) paths[i++] = entry.Path;
        return new WorkingDirectory(here.Path, here.ProviderPath, Environment.CurrentDirectory, paths);
    }

    /// <summary>
    /// Puts back what <see cref="SaveWorkingDirectory"/> read, touching PowerShell's location only for
    /// what the body changed. It runs in a <c>finally</c>, so it never throws: when a saved directory is
    /// gone (the body removed it), the restore stops there, and the parent stays where the body left it.
    /// </summary>
    private static void RestoreWorkingDirectory(SessionState session, WorkingDirectory saved)
    {
        var path = session.Path;
        bool moved = false;
        try
        {
            var stackNow = path.LocationStack(null);
            if (!SameStack(stackNow, saved.Stack))
            {
                // `( pushd sub )` / `( popd )`: rebuild the stack the parent had. Rare, so the slow cmdlets are fine.
                for (int n = stackNow.Count; n > 0; n--) path.PopLocation(null);
                for (int k = saved.Stack.Length - 1; k >= 0; k--)
                {
                    path.SetLocation(WildcardPattern.Escape(saved.Stack[k]));
                    path.PushCurrentLocation(null);
                }
                moved = true;
            }
            if (moved || !string.Equals(path.CurrentLocation.Path, saved.Location, StringComparison.Ordinal))
            {
                path.SetLocation(WildcardPattern.Escape(saved.Location));
                moved = true;
            }
        }
        catch (Exception) { /* a removed directory: see the summary */ }

        if (!moved && string.Equals(Environment.CurrentDirectory, saved.ProcessCwd, StringComparison.Ordinal)) return;
        // A non-filesystem location (a registry drive) has no process working directory to give back.
        if (!Directory.Exists(saved.ProviderPath)) return;
        try { Environment.CurrentDirectory = saved.ProviderPath; } catch (Exception) { return; }
        session.PSVariable.Set("global:__PsBashCwd", saved.ProviderPath);
        // $PWD is the script-visible shell form (C:/x, docs/specs/paths.md), as every cd writes it.
        BashVariableStore.Set("PWD", BashRuntime.ToShellPath(saved.ProviderPath));
    }

    private static bool SameStack(PathInfoStack now, string[] saved)
    {
        if (now.Count != saved.Length) return false;
        int i = 0;
        foreach (var entry in now)
            if (!string.Equals(entry.Path, saved[i++], StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>Capture the state a subshell body may change. Call at the top of the subshell.</summary>
    public static Snapshot Save(SessionState session)
    {
        // The subshell's own $BASHPID, in the subshell's scope (it ends with it): bash's subshell is a new process.
        session.PSVariable.Set(BashRuntime.SubshellPidVariable, BashRuntime.NextSubshellPid());
        // ... and its $BASH_SUBSHELL, one deeper than the code that opened it (read through dynamic scoping before the set).
        session.PSVariable.Set(BashRuntime.SubshellDepthVariable, BashRuntime.SubshellDepth(session) + 1);

        // Through the variable-store seam (BashVariableStoreGuardTests): the store is where bash
        // variables live, so a future per-invocation store keeps subshell scoping correct for free.
        var env = new Dictionary<string, string>(EnvNames);
        foreach (var (name, value) in BashVariableStore.Enumerate())
            env[name] = value;

        var globals = new (bool, object?)[GlobalNames.Length];
        for (int i = 0; i < GlobalNames.Length; i++)
        {
            var v = session.PSVariable.Get("global:" + GlobalNames[i]);
            globals[i] = v is null ? (false, null) : (true, CloneMutable(v.Value));
        }
        // The body starts without the ERR trap unless errtrace is on (bash does not hand it to a subshell); Restore puts
        // it back with the rest of the globals.
        ErrTrapInheritance.Hide(session);

        return new Snapshot(env, globals, InvokeBashShoptCommand.SnapshotOptions(),
            IsTrue(session.PSVariable.GetValue("global:__BashXtrace")), SaveTables(), SaveAliasFunctions(session),
            VarFrameStack.Current.Save(), PsBash.Core.ShellExports.Save(), SaveWorkingDirectory(session))
        { Umask = InvokeBashUmaskCommand.ShellMask, ArrayWrites = BashVariableLookup.OpenJournal(session) };
    }

    /// <summary>
    /// Put <paramref name="saved"/> back. Returns <c>$null</c> when tracing is unchanged, else the tracing
    /// state to re-apply (<c>$true</c> = <c>Set-PSDebug -Trace 1</c>, <c>$false</c> = <c>Set-PSDebug -Off</c>);
    /// <c>Set-PSDebug</c> is a cmdlet, so the emitted <c>finally</c> runs it.
    /// </summary>
    public static object? Restore(SessionState session, Snapshot saved)
    {
        // In a background job's pooled runspace the readonly set and shopt table are the PARENT's (process-wide,
        // shared): putting the snapshot back would undo what the parent wrote while the job ran. The job's scalars
        // and their export attribute are its own (ScalarOverlay, taken at the `&`), so those are put back.
        bool inJob = IsTrue(session.PSVariable.GetValue("global:" + BackgroundRunspaceMark));
        if (inJob && PsBash.Core.ScalarOverlay.Current is null)
            return RestoreRunspaceGlobals(session, saved);

        // Variables: drop what the body added, reset what it changed. Only differences are written.
        var current = new Dictionary<string, string>(EnvNames);
        foreach (var (name, value) in BashVariableStore.Enumerate())
            current[name] = value;
        foreach (var name in current.Keys)
        {
            if (!saved.Env.ContainsKey(name))
                BashVariableStore.Set(name, null);
        }
        foreach (var (name, value) in saved.Env)
        {
            if (!current.TryGetValue(name, out var now) || !string.Equals(now, value, StringComparison.Ordinal))
                BashVariableStore.Set(name, value);
        }

        PsBash.Core.ShellExports.Restore(saved.Exports);
        if (inJob) return RestoreRunspaceGlobals(session, saved);
        InvokeBashShoptCommand.RestoreOptions(saved.Shopt);
        // A `readonly`, an attribute or a nameref made inside the body ends with it, like the variable it describes.
        for (int i = 0; i < ShellTables.Length; i++) PutTable(ShellTables[i], saved.Tables[i]);
        return RestoreRunspaceGlobals(session, saved);
    }

    /// <summary>Set to <c>$true</c> in a background job's pooled runspace by Invoke-BashBackground.</summary>
    public const string BackgroundRunspaceMark = "__psbashBgRunspace";

    // The $global: shell state lives in the runspace, so it is always the body's own to put back. So is the
    // location; the process cwd is put back with it, after the variables (it rewrites $PWD).
    private static object? RestoreRunspaceGlobals(SessionState session, Snapshot saved)
    {
        // `( mapfile -t arr <<< x )`, `( f )` with `f() { arr+=(z); }`: the arrays are the runspace's, so in a job too.
        saved.ArrayWrites?.Undo(session);
        RestoreWorkingDirectory(session, saved.Cwd);
        // REGRESSION: `( umask 077 ); umask` printed 0077 - the mask was not part of the snapshot.
        InvokeBashUmaskCommand.ShellMask = saved.Umask;
        RestoreAliasFunctions(session, saved.AliasFunctions);
        VarFrameStack.Current.Restore(saved.VarFrames);
        bool tracingNow = IsTrue(session.PSVariable.GetValue("global:__BashXtrace"));
        for (int i = 0; i < GlobalNames.Length; i++)
        {
            var (exists, value) = saved.Globals[i];
            if (exists) session.PSVariable.Set("global:" + GlobalNames[i], value);
            else session.PSVariable.Remove("global:" + GlobalNames[i]);
        }
        return tracingNow == saved.Xtrace ? null : saved.Xtrace;
    }

    private static IEnumerable<string> AliasNames(SessionState session)
    {
        object? value = session.PSVariable.GetValue("global:BashUserAliases");
        if ((value is PSObject pso ? pso.BaseObject : value) is not IDictionary aliases) yield break;
        foreach (var key in aliases.Keys)
            if (key is string name) yield return name;
    }

    private static ScriptBlock? AliasFunction(SessionState session, string name)
    {
        using var probe = PsBash.Core.CommandHashTable.Probe();
        return (session.InvokeCommand.GetCommand(name, CommandTypes.Function) as FunctionInfo)?.ScriptBlock;
    }

    private static Dictionary<string, ScriptBlock?> SaveAliasFunctions(SessionState session)
    {
        var saved = new Dictionary<string, ScriptBlock?>(StringComparer.Ordinal);
        foreach (var name in AliasNames(session)) saved[name] = AliasFunction(session, name);
        return saved;
    }

    // `echo $(alias hi='echo hi'); hi` is command not found in bash: the alias ended with the subshell.
    private static void RestoreAliasFunctions(SessionState session, Dictionary<string, ScriptBlock?> saved)
    {
        foreach (var name in AliasNames(session).ToList())
            if (!saved.ContainsKey(name))
                SetAliasFunction(session, name, null);
        foreach (var (name, body) in saved)
        {
            if (ReferenceEquals(AliasFunction(session, name), body)) continue;
            SetAliasFunction(session, name, body);
        }
    }

    private static void SetAliasFunction(SessionState session, string name, ScriptBlock? body)
    {
        // The provider ignores a `global:` scope in a literal path, so a removal names the function plainly (the lookup
        // walks up to the global one) and a set goes through Set-Item, which honours `Function:\global:`.
        try
        {
            if (body is null) session.InvokeProvider.Item.Remove([@"Function:\" + name], recurse: false, force: true, literalPath: true);
            else session.InvokeCommand.InvokeScript(
                "param($n, $b) Set-Item -Path ('Function:\\global:' + [WildcardPattern]::Escape($n)) -Value $b -Force", name, body);
        }
        catch (ItemNotFoundException) { }
    }

    private static bool IsTrue(object? v) => LanguagePrimitives.IsTrue(v);

    /// <summary>A copy of a collection value the body could mutate in place (<c>shift</c> on the positional
    /// array, <c>trap</c> on the handler table); scalars and strings are immutable and kept as-is. Also the copy an
    /// in-process job gets (<see cref="BashStateSnapshot.JobGlobals"/>): it shares the process, not the objects.</summary>
    internal static object? CloneMutable(object? value)
    {
        var raw = value is PSObject pso ? pso.BaseObject : value;
        return raw switch
        {
            object[] a => a.Clone(),
            Hashtable h => h.Clone(),   // shallow, keeps the table's key comparer (and BashAssoc's type)
            Dictionary<string, string> sd => new Dictionary<string, string>(sd, sd.Comparer),   // the alias table
            IDictionary d => new Hashtable(d),
            List<string> ls => new List<string>(ls),   // the history list keeps its type (InvokeBashHistoryCommand.Entries)
            ArrayList l => new ArrayList(l),
            IList l => new ArrayList(l),
            _ => value,
        };
    }
}

/// <summary>
/// `${x:?msg}` on an unset or empty variable. Thrown as a typed error so a subshell scope can catch it
/// (bash ends only the subshell, status 1) while the top level still ends the script.
/// </summary>
public sealed class BashParamErrorException : Exception
{
    public BashParamErrorException(string name, string? message)
        : base(PsBash.Core.BashNameCase.ToBashText(name + (string.IsNullOrEmpty(message) ? "parameter null or not set" : message))) { }

    /// <summary>
    /// The error, already written to the error stream by the emitted code (so the command's own
    /// redirects applied to it); catchers must not print it again. Marked via <c>Data</c> so the host,
    /// which does not reference this assembly, can test it by key.
    /// </summary>
    public static BashParamErrorException Reported(string name, string? message)
    {
        var ex = new BashParamErrorException(name, message);
        ex.Data["psbash.reported"] = true;
        // bash ends a non-interactive shell with 127 for `${x?}` / unbound variable, 1 for a bad substitution.
        ex.Data["psbash.exitcode"] = message == "bad substitution" ? 1 : 127;
        return ex;
    }

    /// <summary>A builtin usage error under <c>bash -c</c> (<c>shift 1 2</c>): reported already, and the shell ends with 1.</summary>
    public static BashParamErrorException UsageEnd() => End("usage error");

    /// <summary>
    /// A refused assignment (<c>r=2</c> with r read-only, <c>a[]=1</c>) while <c>set -e</c> is on: reported already, and
    /// the shell ends with 1 even where errexit is otherwise ignored (<c>r=2 || echo</c>, an <c>if</c> body); a subshell or
    /// <c>$( )</c> ends only itself, and its status 1 ends the caller under the same errexit.
    /// </summary>
    public static BashParamErrorException AssignmentEnd() => End("assignment error");

    // A reported error that ends a non-interactive shell (or the subshell it runs in) with status 1.
    private static BashParamErrorException End(string kind)
    {
        var ex = Reported("", kind);
        ex.Data["psbash.exitcode"] = 1;
        return ex;
    }

    /// <summary>
    /// An expansion error that discards the rest of the shell's current input line (bash jump_to_top_level(DISCARD)):
    /// a bad substitution, an invalid indirect name. The line's <see cref="BashLineDiscard"/> scope catches it (status 1)
    /// and the shell reads the next line; a subshell scope ends its subshell.
    /// </summary>
    public static BashParamErrorException Discard(string name, string? message)
    {
        var ex = Reported(name, message);
        ex.Data["psbash.exitcode"] = 1;
        ex.Data["psbash.discard"] = true;
        return ex;
    }
}

/// <summary>
/// <c>exit N</c> run by a function (or eval / sourced text) while a SUBSHELL scope is open somewhere up the call chain:
/// <c>f | cat</c>, <c>(f)</c>, <c>$(f)</c>. bash ends only that subshell; a PowerShell <c>exit</c> would end the whole
/// script, and a <c>return</c> only the function. The subshell scope (<c>PsBuild.ShellStateGuard</c>) catches it and
/// takes <see cref="Status"/> as its own.
/// </summary>
public sealed class BashSubshellExitException : Exception
{
    public BashSubshellExitException(int status) : base("exit " + status) => Status = status;

    /// <summary>The exit status, already reduced to 0..255.</summary>
    public int Status { get; }
}

/// <summary>
/// The scope around each top-level command of a script, <c>-c</c> string, eval or sourced text whose expansions can
/// discard the line (<c>PsBuild.DiscardLineScope</c>). While one is open (<see cref="Armed"/>) the runtime throws
/// <see cref="BashParamErrorException.Discard"/> for such an error; outside any (a function defined by text that had
/// none) it keeps the older per-command abandon mark.
/// </summary>
public static class BashLineDiscard
{
    [ThreadStatic] private static int t_depth;

    public static bool Armed => t_depth > 0;

    public static void Enter() => t_depth++;

    public static void Leave() { if (t_depth > 0) t_depth--; }

    /// <summary>
    /// Whether the line scope ends <paramref name="ex"/> (and the shell goes on): a discard error always; in an
    /// interactive shell (<c>bash -i -c</c>) also a parameter error (<c>x: unbound variable</c>, <c>${x:?}</c>), which
    /// ends a non-interactive one.
    /// </summary>
    public static bool Discards(Exception? ex) =>
        ex?.Data["psbash.discard"] is true || InvokeBashShoptCommand.InteractiveShell;

    /// <summary>
    /// A builtin usage error that drops the rest of the input line (bash <c>no_args</c>: <c>shift 1 2</c>, <c>return 1 2</c>,
    /// <c>exit 1 2</c>, <c>break 1 2</c>), already reported: status 1, and inside a line scope the discard error ends the
    /// line (a loop or function on it too); outside one the line mark (<c>PsBuild.UnlessGlobFailed</c>) skips the commands
    /// after it on its line. Under <c>bash -c</c> the shell ends, status 1 (the reported parameter error; a subshell ends
    /// only itself). The emitted twin is <c>PsBuild.UsageDiscard</c>.
    /// </summary>
    public static void UsageError(PSCmdlet cmdlet)
    {
        if (ShellReader.DashC)
            cmdlet.ThrowTerminatingError(new ErrorRecord(BashParamErrorException.UsageEnd(),
                "BashUsageError", ErrorCategory.InvalidArgument, null));
        if (Armed)
            cmdlet.ThrowTerminatingError(new ErrorRecord(BashParamErrorException.Discard("", "usage error"),
                "BashUsageDiscard", ErrorCategory.InvalidArgument, null));
        FileSystemHelpers.SetLastExitCode(cmdlet, 1);
        cmdlet.SessionState.PSVariable.Set("global:__BashFailGlob", true);
    }
}
