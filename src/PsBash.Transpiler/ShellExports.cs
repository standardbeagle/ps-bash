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
/// The table is process-wide, like the environment it describes. It lives in this assembly because both the host
/// (per-invocation reset, the interactive shell's direct spawn) and the cmdlets use it. See
/// <c>docs/specs/environment-model.md</c>.
/// </para>
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
    /// Shell variables the host puts in the environment before a command runs that bash keeps unexported
    /// (<c>OPTIND</c> is seeded to 1 by <c>SdkWorker.ResetEnvironment</c>).
    /// </summary>
    private static readonly string[] ShellOwned = ["OPTIND"];

    private static readonly object Gate = new();
    private static HashSet<string> s_exported = new(NameComparer);
    private static bool s_seeded;

    /// <summary>
    /// True once a shell host started (<see cref="Reset"/>). The module imported into a plain PowerShell has no
    /// shell start and no export attribute: PowerShell code sets <c>$env:NAME</c> directly and means the environment,
    /// so there every variable that is set counts as exported, as before the table existed.
    /// </summary>
    private static bool s_tracking;

    /// <summary>While <c>set -a</c> is on: the unexported variables and their values when it was turned on.</summary>
    private static Dictionary<string, string>? s_allExportBaseline;

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
        s_exported = set;
        s_allExportBaseline = null;
        s_seeded = true;
    }

    /// <summary>Under the gate: seeded, and the assignments made under <c>set -a</c> so far are exported.</summary>
    private static void SyncLocked()
    {
        if (!s_seeded || !s_tracking) SeedLocked();
        if (s_allExportBaseline is null) return;
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            if (e.Key is not string name || s_exported.Contains(name)) continue;
            var value = e.Value as string ?? "";
            // Assigned since `set -a`: new, or its value changed. (An assignment of the SAME value is not seen.)
            if (!s_allExportBaseline.TryGetValue(name, out var before) || !string.Equals(before, value, StringComparison.Ordinal))
                s_exported.Add(name);
        }
    }

    /// <summary>Whether <paramref name="name"/> has the export attribute (set or not).</summary>
    public static bool IsExported(string name)
    {
        lock (Gate)
        {
            SyncLocked();
            return s_exported.Contains(name);
        }
    }

    /// <summary><c>export NAME</c> / <c>declare -x NAME</c>. An unset name keeps the attribute for a later assignment.</summary>
    public static void Export(params string[] names)
    {
        lock (Gate)
        {
            SyncLocked();
            foreach (var name in names)
                if (!string.IsNullOrEmpty(name)) s_exported.Add(name);
        }
    }

    /// <summary><c>export -n NAME</c> / <c>declare +x NAME</c>: the variable stays, children stop seeing it.</summary>
    public static void Unexport(params string[] names)
    {
        lock (Gate)
        {
            SyncLocked();
            foreach (var name in names)
            {
                s_exported.Remove(name);
                // Under `set -a` the name is unexported as of now; only a later assignment exports it again.
                if (s_allExportBaseline is not null && Environment.GetEnvironmentVariable(name) is { } value)
                    s_allExportBaseline[name] = value;
            }
        }
    }

    /// <summary><c>unset NAME</c>: the variable and its attributes are gone; a later <c>NAME=v</c> is a new, unexported one.</summary>
    public static void Forget(string name)
    {
        lock (Gate)
        {
            SyncLocked();
            s_exported.Remove(name);
            s_allExportBaseline?.Remove(name);
        }
    }

    /// <summary><c>set -a</c> / <c>set -o allexport</c>: every variable assigned while it is on is exported.</summary>
    public static bool AllExport
    {
        get { lock (Gate) return s_allExportBaseline is not null; }
        set
        {
            lock (Gate)
            {
                SyncLocked(); // turning it off: what was assigned under it stays exported
                if (!value) { s_allExportBaseline = null; return; }
                if (s_allExportBaseline is not null) return;
                var baseline = new Dictionary<string, string>(NameComparer);
                foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
                    if (e.Key is string name && !s_exported.Contains(name)) baseline[name] = e.Value as string ?? "";
                s_allExportBaseline = baseline;
            }
        }
    }

    /// <summary>The value a child process would see for <paramref name="name"/>: null when unset or not exported.</summary>
    public static string? Get(string name)
        => IsExported(name) ? Environment.GetEnvironmentVariable(name) : null;

    /// <summary>The environment a child process gets: every exported variable that is set.</summary>
    public static List<KeyValuePair<string, string>> Enumerate()
    {
        var result = new List<KeyValuePair<string, string>>();
        lock (Gate)
        {
            SyncLocked();
            foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
                if (e.Key is string name && s_exported.Contains(name))
                    result.Add(new KeyValuePair<string, string>(name, e.Value as string ?? ""));
        }
        return result;
    }

    /// <summary>The variables that are set but not exported (what a forked subshell still has and a child does not).</summary>
    public static List<string> UnexportedNames()
    {
        var result = new List<string>();
        lock (Gate)
        {
            SyncLocked();
            foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
                if (e.Key is string name && !s_exported.Contains(name)) result.Add(name);
        }
        return result;
    }

    /// <summary>
    /// THE child-environment primitive. <paramref name="childEnvironment"/> starts as a copy of the process environment
    /// (plus whatever the caller set for the child); every shell variable that is not exported is removed from it. An
    /// entry the caller set itself - a name the process environment lacks, or a different value - is kept.
    /// </summary>
    public static void RemoveUnexported(IDictionary<string, string?> childEnvironment)
    {
        ArgumentNullException.ThrowIfNull(childEnvironment);
        List<string>? drop = null;
        lock (Gate)
        {
            SyncLocked();
            foreach (var kv in childEnvironment)
            {
                if (s_exported.Contains(kv.Key)) continue;
                var ours = Environment.GetEnvironmentVariable(kv.Key);
                if (ours is null || !string.Equals(ours, kv.Value, StringComparison.Ordinal)) continue;
                (drop ??= new List<string>()).Add(kv.Key);
            }
        }
        if (drop is null) return;
        foreach (var name in drop) childEnvironment.Remove(name);
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
        lock (Gate)
        {
            SyncLocked();
            return new Saved(new HashSet<string>(s_exported, NameComparer),
                s_allExportBaseline is null ? null : new Dictionary<string, string>(s_allExportBaseline, NameComparer));
        }
    }

    /// <summary>Put back what <see cref="Save"/> took.</summary>
    public static void Restore(Saved saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        lock (Gate)
        {
            s_exported = new HashSet<string>(saved.Exported, NameComparer);
            s_allExportBaseline = saved.Baseline is null ? null : new Dictionary<string, string>(saved.Baseline, NameComparer);
            s_seeded = true;
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
        lock (Gate)
        {
            SyncLocked();
            for (int i = 0; i < names.Count; i++)
            {
                was[i] = s_exported.Contains(names[i]);
                s_exported.Add(names[i]);
            }
        }
        return was;
    }

    /// <summary>The command of <see cref="BeginTemp"/> ended: each name has the attribute it had before.</summary>
    public static void EndTemp(IReadOnlyList<string> names, bool[] was)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(was);
        lock (Gate)
        {
            for (int i = 0; i < names.Count && i < was.Length; i++)
            {
                if (was[i]) s_exported.Add(names[i]);
                else s_exported.Remove(names[i]);
            }
        }
    }

    /// <summary>Set one name's attribute (a function's local ends: the caller's variable has its own attribute back).</summary>
    public static void SetExported(string name, bool exported)
    {
        lock (Gate)
        {
            SyncLocked();
            if (exported) s_exported.Add(name);
            else s_exported.Remove(name);
        }
    }
}
