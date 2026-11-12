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
/// <item>the per-runspace <c>shopt</c> table;</item>
/// <item><c>Set-PSDebug</c> tracing (<c>set -x</c>) — session-wide and not queryable, so it is tracked by
/// <c>$global:__BashXtrace</c> and <see cref="Restore"/> returns the level the caller must re-apply.</item>
/// </list>
/// NOT restored, on purpose: <c>$?</c>/<c>$LASTEXITCODE</c> (the subshell's status IS the parent's <c>$?</c>)
/// and the working directory (the emitter's <c>Push-Location</c>/<c>Pop-Location</c> pair owns it).
/// </summary>
public static class BashShellState
{
    private static readonly HashSet<string> ReadonlyNames = new(StringComparer.Ordinal);

    /// <summary>Records <c>readonly NAME</c>; only <c>unset</c> consults it (assignment is not blocked: see intentional-differences.md).</summary>
    public static void MarkReadonly(string name) { lock (ReadonlyNames) ReadonlyNames.Add(name); }

    /// <summary>A new, independent invocation starts with nothing readonly (the daemon outlives the scripts it runs).</summary>
    public static void ClearReadonly() { lock (ReadonlyNames) ReadonlyNames.Clear(); lock (Attributes) Attributes.Clear(); lock (Namerefs) Namerefs.Clear(); }

    private static readonly Dictionary<string, string> Namerefs = new(StringComparer.Ordinal);

    /// <summary>Records <c>declare -n NAME=TARGET</c> so <c>declare -p</c> can list it (the emitter resolves the reference itself).</summary>
    public static void SetNameref(string name, string target) { lock (Namerefs) Namerefs[name] = target; }

    /// <summary><c>declare +n NAME</c>: the name is a plain variable again.</summary>
    public static void ClearNameref(string name) { lock (Namerefs) Namerefs.Remove(name); }

    internal static string? NamerefTarget(string name) { lock (Namerefs) return Namerefs.TryGetValue(name, out var t) ? t : null; }

    internal static string[] NamerefNames() { lock (Namerefs) return [.. Namerefs.Keys]; }

    private static readonly Dictionary<string, string> Attributes = new(StringComparer.Ordinal);

    /// <summary>Records a <c>declare</c> attribute letter (<c>i</c> integer, <c>x</c> exported) for <c>declare -p</c>.</summary>
    public static void MarkAttribute(string name, string letter)
    {
        lock (Attributes)
            if (!(Attributes.TryGetValue(name, out var cur) && cur.Contains(letter)))
                Attributes[name] = (cur ?? "") + letter;
    }

    /// <summary><c>declare x</c> (no value, no -a): declared as a scalar that is still unset.</summary>
    internal static bool IsDeclaredScalar(string name) { lock (Attributes) return Attributes.TryGetValue(name, out var s) && s.Contains('-'); }

    /// <summary><c>declare x</c> / <c>declare -a x</c> with no value: declared but unset, so <c>declare -p</c> shows no <c>=()</c>.</summary>
    internal static bool IsDeclaredUnset(string name) { lock (Attributes) return Attributes.TryGetValue(name, out var s) && (s.Contains('-') || s.Contains('~')); }

    /// <summary>The <c>declare -p</c> flag letters of NAME in bash order (<c>i</c>, <c>r</c>, <c>x</c>), "" for none.</summary>
    internal static string AttributeLetters(string name)
    {
        string set;
        lock (Attributes) set = Attributes.TryGetValue(name, out var s) ? s : "";
        var sb = new System.Text.StringBuilder();
        if (set.Contains('i')) sb.Append('i');
        if (IsReadonly(name)) sb.Append('r');
        if (set.Contains('x')) sb.Append('x');
        return sb.ToString();
    }

    internal static bool IsReadonly(string name) { lock (ReadonlyNames) return ReadonlyNames.Contains(name); }

    /// <summary>The first of <paramref name="names"/> that is read-only, or null (the emitter's run-time assignment guard).</summary>
    public static string? FirstReadonly(params string[] names)
    {
        lock (ReadonlyNames)
            foreach (var n in names)
                if (ReadonlyNames.Contains(n)) return n;
        return null;
    }

    /// <summary>The <c>$global:</c> variables that are shell state (see the class summary).</summary>
    private static readonly string[] GlobalNames =
    [
        "__BashErrexit", "__BashErrexitSuppress", "__BashErrexitTail", "__BashXtrace", "__BashNoglob", "__BashPipefail", "__BashNounset", "__BashNoclobber", "__BashErrtrace",
        "BashFlags", "BashPositional", "BashPositional0", "BASH_REMATCH", "BASH_SOURCE",
        "BashTrapHandlers", "__BashTrapERR", "__BashTrapEXIT", "__BashTrapRETURN", "BashBgLastPid",
        "BashUserAliases", "BashLastArg", "BashDirStack",
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
            Dictionary<string, bool> shopt, bool xtrace, HashSet<string> readonlyNames,
            Dictionary<string, ScriptBlock?> aliasFunctions)
        {
            Env = env; Globals = globals; Shopt = shopt; Xtrace = xtrace; Readonly = readonlyNames; AliasFunctions = aliasFunctions;
        }

        internal HashSet<string> Readonly { get; }

        /// <summary>The function behind each alias (an alias runs as a global function), so one defined or removed
        /// inside the body is undone with the alias table.</summary>
        internal Dictionary<string, ScriptBlock?> AliasFunctions { get; }

        internal Dictionary<string, string> Env { get; }
        internal (bool Exists, object? Value)[] Globals { get; }
        internal Dictionary<string, bool> Shopt { get; }
        internal bool Xtrace { get; }
    }

    /// <summary>Capture the state a subshell body may change. Call at the top of the subshell.</summary>
    public static Snapshot Save(SessionState session)
    {
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

        HashSet<string> readonlyNow;
        lock (ReadonlyNames) readonlyNow = new HashSet<string>(ReadonlyNames, StringComparer.Ordinal);
        return new Snapshot(env, globals, InvokeBashShoptCommand.SnapshotOptions(),
            IsTrue(session.PSVariable.GetValue("global:__BashXtrace")), readonlyNow, SaveAliasFunctions(session));
    }

    /// <summary>
    /// Put <paramref name="saved"/> back. Returns <c>$null</c> when tracing is unchanged, else the tracing
    /// state to re-apply (<c>$true</c> = <c>Set-PSDebug -Trace 1</c>, <c>$false</c> = <c>Set-PSDebug -Off</c>);
    /// <c>Set-PSDebug</c> is a cmdlet, so the emitted <c>finally</c> runs it.
    /// </summary>
    public static object? Restore(SessionState session, Snapshot saved)
    {
        // In a background job's pooled runspace the variables, readonly set and shopt table are the PARENT's
        // (process-wide, shared): putting the snapshot back would undo what the parent wrote while the job ran
        // (`(sleep 1) & x=1; wait` lost x). Such a job's own writes are handled by Invoke-BashBackground (-Isolated).
        if (IsTrue(session.PSVariable.GetValue("global:" + BackgroundRunspaceMark)))
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

        InvokeBashShoptCommand.RestoreOptions(saved.Shopt);
        // `readonly` made inside the body ends with it, like the variable it protects.
        lock (ReadonlyNames) { ReadonlyNames.Clear(); ReadonlyNames.UnionWith(saved.Readonly); }
        return RestoreRunspaceGlobals(session, saved);
    }

    /// <summary>Set to <c>$true</c> in a background job's pooled runspace by Invoke-BashBackground.</summary>
    public const string BackgroundRunspaceMark = "__psbashBgRunspace";

    // The $global: shell state lives in the runspace, so it is always the body's own to put back.
    private static object? RestoreRunspaceGlobals(SessionState session, Snapshot saved)
    {
        RestoreAliasFunctions(session, saved.AliasFunctions);
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
        => (session.InvokeCommand.GetCommand(name, CommandTypes.Function) as FunctionInfo)?.ScriptBlock;

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
    /// array, <c>trap</c> on the handler table); scalars and strings are immutable and kept as-is.</summary>
    private static object? CloneMutable(object? value)
    {
        var raw = value is PSObject pso ? pso.BaseObject : value;
        return raw switch
        {
            object[] a => a.Clone(),
            Hashtable h => h.Clone(),   // shallow, keeps the table's key comparer
            IDictionary d => new Hashtable(d),
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
        : base(name + (string.IsNullOrEmpty(message) ? "parameter null or not set" : message)) { }

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
}
