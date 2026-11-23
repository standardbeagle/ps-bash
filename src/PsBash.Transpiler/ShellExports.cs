using System.Collections;

namespace PsBash.Core;

/// <summary>
/// Which shell variables are EXPORTED, and the environment a child process gets.
/// <para>
/// <b>Invariant.</b> A child process's environment is the shell's exported variables only, and it is built here
/// (<see cref="RemoveUnexported"/>). ps-bash keeps every scalar variable in the process environment (the emitter reads
/// <c>$env:NAME</c>), so "exported" is not "is in the environment": it is membership in this table.
/// </para>
/// <para>
/// <b>What is exported</b> (bash): what the shell inherited at startup (<see cref="Reset"/>), plus <c>export</c> /
/// <c>declare -x</c> / an assignment under <c>set -a</c>, minus <c>export -n</c> / <c>declare +x</c> / <c>unset</c>.
/// <c>NAME=v cmd</c> exports NAME for that command (<see cref="BeginTemp"/>). A name nobody exported, however it
/// got into the environment (a plain assignment, <c>read</c>, <c>for</c>, <c>printf -v</c>, a helper variable the
/// emitted code keeps there), is NOT exported: there is no "mark as unexported" step an assignment site could forget.
/// </para>
/// <para>
/// The foreground's table is process-wide, like the environment it describes. An in-process background job has its
/// own copy, with its own variables (<see cref="ScalarOverlay"/>): every member here works on the store of the code
/// running on the calling thread. It lives in this assembly because both the host (per-invocation reset, the
/// interactive shell's direct spawn) and the cmdlets use it. See <c>docs/specs/environment-model.md</c>.
/// </para>
/// <para>Every by-name member takes a bash name or a store name (<see cref="BashNameCase.ToStore(string)"/>): the table
/// holds store names, as the environment does (section 12).</para>
/// </summary>
public static class ShellExports
{
    /// <summary>Environment names are case-insensitive on Windows, as the OS treats them.</summary>
    public static readonly StringComparer NameComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Exported at startup whether or not the environment has them: bash exports <c>PWD</c> and <c>OLDPWD</c> itself,
    /// and <c>HOME</c> is always inherited there (on Windows the environment may lack it: ps-bash derives it).
    /// </summary>
    private static readonly string[] ShellExported = ["PWD", "OLDPWD", "HOME"];

    /// <summary>
    /// Shell variables bash binds at every shell start and keeps unexported (<c>IFS</c>, <c>OPTIND</c>, <c>PPID</c>,
    /// ...: <see cref="ShellVariables.AlwaysUnexported"/>; the host binds them, <see cref="ShellVariables.SeedShellStart"/>).
    /// </summary>
    private static readonly string[] ShellOwned = [.. ShellVariables.AlwaysUnexported];

    private static readonly object Gate = new();

    /// <summary>One store's export attribute: the foreground's (<see cref="s_process"/>) or a job's.</summary>
    internal sealed class Table
    {
        public HashSet<string> Exported = new(NameComparer);

        /// <summary>While <c>set -a</c> is on: the unexported variables and their values when it was turned on.</summary>
        public Dictionary<string, string>? Baseline;
    }

    private static readonly Table s_process = new();
    private static bool s_seeded;

    /// <summary>
    /// True once a shell host started (<see cref="Reset"/>). The module imported into a plain PowerShell has no
    /// shell start and no export attribute: PowerShell code sets <c>$env:NAME</c> directly and means the environment,
    /// so there every variable that is set counts as exported, as before the table existed.
    /// </summary>
    private static bool s_tracking;

    /// <summary>
    /// The store of the calling code, its gate held by the caller: the job's (<see cref="ScalarOverlay.Current"/>), or
    /// the foreground's, synced.
    /// </summary>
    private readonly struct Scope
    {
        public readonly ScalarOverlay? Overlay;
        public readonly Table Table;
        public Scope(ScalarOverlay? overlay) { Overlay = overlay; Table = overlay?.Exports ?? s_process; }
        public object Gate => (object?)Overlay?.Gate ?? ShellExports.Gate;

        public string? Value(string name) => Overlay is null ? ProcessScalars.Get(name) : Overlay.Get(name);

        public IEnumerable<KeyValuePair<string, string>> Values()
        {
            if (Overlay is not null)
            {
                foreach (var kv in Overlay.Entries()) yield return kv;
                yield break;
            }
            foreach (var kv in ProcessScalars.Entries()) yield return kv;
        }
    }

    /// <summary>
    /// A shell starts: every variable in the process environment now was inherited, so it is exported. Called by the
    /// host after it reset the environment to the launcher's block, and lazily by the first use otherwise.
    /// </summary>
    public static void Reset()
    {
        lock (Gate)
        {
            s_tracking = true;
            SeedLocked();
        }
    }

    private static void SeedLocked()
    {
        var set = new HashSet<string>(NameComparer);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string name) set.Add(name);
        foreach (var name in ShellExported) set.Add(name);
        foreach (var name in ShellOwned) set.Remove(name);
        s_process.Exported = set;
        s_process.Baseline = null;
        s_seeded = true;
    }

    /// <summary>Under the scope's gate: seeded, and the assignments made under <c>set -a</c> so far are exported.</summary>
    private static void SyncLocked(Scope scope)
    {
        if (scope.Overlay is null && (!s_seeded || !s_tracking)) SeedLocked();
        var t = scope.Table;
        if (t.Baseline is null) return;
        foreach (var (name, value) in scope.Values())
        {
            if (t.Exported.Contains(name)) continue;
            // Assigned since `set -a`: new, or its value changed. (An assignment of the SAME value is not seen.)
            if (!t.Baseline.TryGetValue(name, out var before) || !string.Equals(before, value, StringComparison.Ordinal))
                t.Exported.Add(name);
        }
    }

    /// <summary>A copy of the export attribute of <paramref name="from"/>'s store (the foreground's when null), for a new job.</summary>
    internal static Table CopyTable(ScalarOverlay? from)
    {
        var scope = new Scope(from);
        lock (scope.Gate)
        {
            SyncLocked(scope);
            return new Table
            {
                Exported = new HashSet<string>(scope.Table.Exported, NameComparer),
                Baseline = scope.Table.Baseline is null ? null : new Dictionary<string, string>(scope.Table.Baseline, NameComparer),
            };
        }
    }

    /// <summary>Whether <paramref name="name"/> has the export attribute (set or not).</summary>
    public static bool IsExported(string name)
    {
        var scope = new Scope(ScalarOverlay.Current);
        lock (scope.Gate)
        {
            SyncLocked(scope);
            return scope.Table.Exported.Contains(BashNameCase.ToStore(name));
        }
    }

    /// <summary><c>export NAME</c> / <c>declare -x NAME</c>. An unset name keeps the attribute for a later assignment.</summary>
    public static void Export(params string[] names)
    {
        var scope = new Scope(ScalarOverlay.Current);
        lock (scope.Gate)
        {
            SyncLocked(scope);
            foreach (var name in names)
                if (!string.IsNullOrEmpty(name)) scope.Table.Exported.Add(BashNameCase.ToStore(name));
        }
    }

    /// <summary><c>export -n NAME</c> / <c>declare +x NAME</c>: the variable stays, children stop seeing it.</summary>
    public static void Unexport(params string[] names)
    {
        var scope = new Scope(ScalarOverlay.Current);
        lock (scope.Gate)
        {
            SyncLocked(scope);
            foreach (var bashName in names)
            {
                var name = BashNameCase.ToStore(bashName);
                scope.Table.Exported.Remove(name);
                // Under `set -a` the name is unexported as of now; only a later assignment exports it again.
                if (scope.Table.Baseline is not null && scope.Value(name) is { } value)
                    scope.Table.Baseline[name] = value;
            }
        }
    }

    /// <summary><c>unset NAME</c>: the variable and its attributes are gone; a later <c>NAME=v</c> is a new, unexported one.</summary>
    public static void Forget(string name)
    {
        name = BashNameCase.ToStore(name);
        var scope = new Scope(ScalarOverlay.Current);
        lock (scope.Gate)
        {
            SyncLocked(scope);
            scope.Table.Exported.Remove(name);
            scope.Table.Baseline?.Remove(name);
        }
    }

    /// <summary><c>set -a</c> / <c>set -o allexport</c>: every variable assigned while it is on is exported.</summary>
    public static bool AllExport
    {
        get
        {
            var scope = new Scope(ScalarOverlay.Current);
            lock (scope.Gate) return scope.Table.Baseline is not null;
        }
        set
        {
            var scope = new Scope(ScalarOverlay.Current);
            lock (scope.Gate)
            {
                SyncLocked(scope); // turning it off: what was assigned under it stays exported
                var t = scope.Table;
                if (!value) { t.Baseline = null; return; }
                if (t.Baseline is not null) return;
                var baseline = new Dictionary<string, string>(NameComparer);
                foreach (var (name, v) in scope.Values())
                    if (!t.Exported.Contains(name)) baseline[name] = v;
                t.Baseline = baseline;
            }
        }
    }

    /// <summary>The value a child process would see for <paramref name="name"/>: null when unset or not exported.</summary>
    public static string? Get(string name)
    {
        name = BashNameCase.ToStore(name);
        var scope = new Scope(ScalarOverlay.Current);
        lock (scope.Gate)
        {
            SyncLocked(scope);
            return scope.Table.Exported.Contains(name) ? scope.Value(name) : null;
        }
    }

    /// <summary>The environment a child process gets: every exported variable that is set.</summary>
    public static List<KeyValuePair<string, string>> Enumerate()
    {
        var result = new List<KeyValuePair<string, string>>();
        var scope = new Scope(ScalarOverlay.Current);
        lock (scope.Gate)
        {
            SyncLocked(scope);
            foreach (var kv in scope.Values())
                if (scope.Table.Exported.Contains(kv.Key)) result.Add(kv);
        }
        return result;
    }

    /// <summary>The variables that are set but not exported (what a forked subshell still has and a child does not).</summary>
    public static List<string> UnexportedNames()
    {
        var result = new List<string>();
        var scope = new Scope(ScalarOverlay.Current);
        lock (scope.Gate)
        {
            SyncLocked(scope);
            foreach (var kv in scope.Values())
                if (!scope.Table.Exported.Contains(kv.Key)) result.Add(kv.Key);
        }
        return result;
    }

    /// <summary>
    /// THE child-environment primitive. <paramref name="childEnvironment"/> starts as a copy of the process environment
    /// (plus whatever the caller set for the child); every shell variable that is not exported is removed from it. An
    /// entry the caller set itself - a name the process environment lacks, or a different value - is kept.
    /// <para>In a job (<see cref="ScalarOverlay"/>) the process environment is the foreground's, not the job's: every
    /// entry copied from it is replaced by the job's exported variables, and the caller's own entries are kept.</para>
    /// </summary>
    public static void RemoveUnexported(IDictionary<string, string?> childEnvironment)
    {
        ArgumentNullException.ThrowIfNull(childEnvironment);
        var scope = new Scope(ScalarOverlay.Current);
        if (scope.Overlay is not null)
        {
            ReplaceWithJobExports(childEnvironment, scope);
            return;
        }
        List<string>? drop = null;
        List<KeyValuePair<string, string?>>? exact = null;
        lock (scope.Gate)
        {
            SyncLocked(scope);
            foreach (var kv in childEnvironment)
            {
                if (scope.Table.Exported.Contains(kv.Key))
                {
                    // Copied from the process environment, which cannot hold every value (an invalid byte on Linux):
                    // the child gets the variable's exact value (ProcessScalars).
                    if (kv.Value is { } copied && ProcessScalars.ExactIfStored(kv.Key, copied) is var value
                        && !ReferenceEquals(value, copied))
                        (exact ??= new List<KeyValuePair<string, string?>>()).Add(new(kv.Key, value));
                    continue;
                }
                var ours = Environment.GetEnvironmentVariable(kv.Key);
                if (ours is null || !string.Equals(ours, kv.Value, StringComparison.Ordinal)) continue;
                (drop ??= new List<string>()).Add(kv.Key);
            }
        }
        if (exact is not null)
            foreach (var kv in exact) childEnvironment[kv.Key] = kv.Value;
        if (drop is null) return;
        foreach (var name in drop) childEnvironment.Remove(name);
    }

    private static void ReplaceWithJobExports(IDictionary<string, string?> childEnvironment, Scope scope)
    {
        // The caller's own entries: a name the process environment lacks, or a different value.
        List<KeyValuePair<string, string?>>? callers = null;
        var given = new HashSet<string>(childEnvironment.Keys, NameComparer);
        foreach (var kv in childEnvironment)
        {
            var process = Environment.GetEnvironmentVariable(kv.Key);
            if (process is null || !string.Equals(process, kv.Value, StringComparison.Ordinal))
                (callers ??= new List<KeyValuePair<string, string?>>()).Add(kv);
        }
        childEnvironment.Clear();
        lock (scope.Gate)
        {
            SyncLocked(scope);
            foreach (var kv in scope.Values())
            {
                if (!scope.Table.Exported.Contains(kv.Key)) continue;
                // One the caller removed (the nested shell drops PSBASH_TIMEOUT) stays removed.
                if (!given.Contains(kv.Key) && Environment.GetEnvironmentVariable(kv.Key) is not null) continue;
                childEnvironment[kv.Key] = kv.Value;
            }
        }
        if (callers is null) return;
        foreach (var kv in callers) childEnvironment[kv.Key] = kv.Value;
    }

    /// <summary>
    /// The child environment as a Windows <c>CreateProcessW</c> block (<c>CREATE_UNICODE_ENVIRONMENT</c>): the exported
    /// variables, sorted case-insensitively, each <c>NAME=VALUE\0</c>, then a final <c>\0</c>. For the one spawn that
    /// calls <c>CreateProcessW</c> itself without a <c>ProcessStartInfo</c> (<c>nohup</c>).
    /// </summary>
    public static char[] WindowsEnvironmentBlock()
    {
        var sorted = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in Enumerate()) sorted[name] = value;
        var sb = new System.Text.StringBuilder();
        foreach (var (name, value) in sorted) sb.Append(name).Append('=').Append(value).Append('\0');
        if (sorted.Count == 0) sb.Append('\0');
        sb.Append('\0');
        return sb.ToString().ToCharArray();
    }

    /// <summary>The whole table, for a subshell's save / restore. Opaque.</summary>
    public sealed class Saved
    {
        internal Saved(HashSet<string> exported, Dictionary<string, string>? baseline)
        {
            Exported = exported;
            Baseline = baseline;
        }

        internal HashSet<string> Exported { get; }
        internal Dictionary<string, string>? Baseline { get; }
    }

    /// <summary>A copy of the table: a subshell's <c>export</c> ends with it.</summary>
    public static Saved Save()
    {
        var scope = new Scope(ScalarOverlay.Current);
        lock (scope.Gate)
        {
            SyncLocked(scope);
            return new Saved(new HashSet<string>(scope.Table.Exported, NameComparer),
                scope.Table.Baseline is null ? null : new Dictionary<string, string>(scope.Table.Baseline, NameComparer));
        }
    }

    /// <summary>Put back what <see cref="Save"/> took.</summary>
    public static void Restore(Saved saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        var scope = new Scope(ScalarOverlay.Current);
        lock (scope.Gate)
        {
            scope.Table.Exported = new HashSet<string>(saved.Exported, NameComparer);
            scope.Table.Baseline = saved.Baseline is null ? null : new Dictionary<string, string>(saved.Baseline, NameComparer);
            if (scope.Overlay is null) s_seeded = true;
        }
    }

    /// <summary>
    /// <c>NAME=v cmd</c>: the names are exported for the command. Returns what <see cref="EndTemp"/> needs to put each
    /// name's attribute back.
    /// </summary>
    public static bool[] BeginTemp(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var was = new bool[names.Count];
        var scope = new Scope(ScalarOverlay.Current);
        lock (scope.Gate)
        {
            SyncLocked(scope);
            for (int i = 0; i < names.Count; i++)
            {
                var name = BashNameCase.ToStore(names[i]);
                was[i] = scope.Table.Exported.Contains(name);
                scope.Table.Exported.Add(name);
            }
        }
        return was;
    }

    /// <summary>The command of <see cref="BeginTemp"/> ended: each name has the attribute it had before.</summary>
    public static void EndTemp(IReadOnlyList<string> names, bool[] was)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(was);
        var scope = new Scope(ScalarOverlay.Current);
        lock (scope.Gate)
        {
            for (int i = 0; i < names.Count && i < was.Length; i++)
            {
                var name = BashNameCase.ToStore(names[i]);
                if (was[i]) scope.Table.Exported.Add(name);
                else scope.Table.Exported.Remove(name);
            }
        }
    }

    /// <summary>Set one name's attribute (a function's local ends: the caller's variable has its own attribute back).</summary>
    public static void SetExported(string name, bool exported)
    {
        name = BashNameCase.ToStore(name);
        var scope = new Scope(ScalarOverlay.Current);
        lock (scope.Gate)
        {
            SyncLocked(scope);
            if (exported) scope.Table.Exported.Add(name);
            else scope.Table.Exported.Remove(name);
        }
    }
}
