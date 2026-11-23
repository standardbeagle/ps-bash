using System.Runtime.CompilerServices;

namespace PsBash.Core;

/// <summary>
/// The scalar variables of one in-process background job: its own copy of the shell's variables and their export
/// attribute, taken at the job's <c>&amp;</c>.
/// <para>
/// <b>Invariant</b> (bash forks a job): a job sees the values its parent had at the <c>&amp;</c>; nothing the parent
/// writes afterwards reaches it, and nothing it writes reaches the parent or another job. The foreground keeps its
/// scalars in the process environment (<c>docs/specs/environment-model.md</c>). An in-process job runs in the same
/// process, so its scalars live here instead, and every reader and writer of a scalar resolves the store through
/// <see cref="Current"/>:
/// <c>$env:NAME</c> in the job's runspace (its <c>Env</c> drive is <c>PsBash.Cmdlets.JobEnvironmentProvider</c>),
/// <c>BashVariableStore</c> for the cmdlets, and <see cref="ShellExports"/> for the export attribute and a child's
/// environment. See <c>docs/specs/background-jobs.md</c> section 3.
/// </para>
/// <para>
/// Keyed by the job's runspace (<see cref="Attach"/>), never by variable name. This assembly does not reference
/// PowerShell, so the cmdlets install <see cref="CurrentOwner"/> (the thread's default runspace).
/// </para>
/// </summary>
public sealed class ScalarOverlay
{
    private static readonly ConditionalWeakTable<object, ScalarOverlay> ByOwner = new();
    private static int s_attached;

    /// <summary>The owner of the code running on this thread (the cmdlets set it to <c>Runspace.DefaultRunspace</c>).</summary>
    public static Func<object?>? CurrentOwner { get; set; }

    /// <summary>
    /// The store of the job running on this thread; null in the foreground, where the process environment is the store.
    /// While no job holds one this is a single read.
    /// </summary>
    public static ScalarOverlay? Current
    {
        get
        {
            if (Volatile.Read(ref s_attached) == 0) return null;
            return CurrentOwner?.Invoke() is { } owner && ByOwner.TryGetValue(owner, out var overlay) ? overlay : null;
        }
    }

    internal readonly object Gate = new();
    private readonly Dictionary<string, string> _values;
    internal ShellExports.Table Exports { get; }

    private ScalarOverlay(Dictionary<string, string> values, ShellExports.Table exports)
    {
        _values = values;
        Exports = exports;
    }

    /// <summary>
    /// The store a job launched now starts with: a copy of the launching code's view (the process environment in the
    /// foreground, the launching job's own store in a job) and of its export attribute.
    /// </summary>
    public static ScalarOverlay Capture()
    {
        var from = Current;
        Dictionary<string, string> values;
        if (from is null)
        {
            values = new Dictionary<string, string>(ShellExports.NameComparer);
            foreach (var (name, value) in ProcessScalars.Entries()) values[name] = value;
        }
        else
        {
            lock (from.Gate) values = new Dictionary<string, string>(from._values, ShellExports.NameComparer);
        }
        return new ScalarOverlay(values, ShellExports.CopyTable(from));
    }

    /// <summary>
    /// The environment of ONE command a wrapper runs with a modified environment (<c>env [-i] [-u NAME] [NAME=VALUE]
    /// COMMAND</c>): the calling code's EXPORTED variables (none with <paramref name="ignoreEnvironment"/>), minus
    /// <paramref name="unset"/>, plus <paramref name="assignments"/>, every one of them exported. The command is a
    /// program in bash, so it sees only what env hands it; nothing it writes reaches the shell.
    /// <para>
    /// <b>Invariant</b> (environment-model.md section 11): building a child's environment never writes the process
    /// environment. <c>env -i</c> used to clear the PROCESS environment around its command, so every other runspace
    /// of the host process (a concurrent test, a job, the host's own TEMP lookups) saw TEMP and PATH unset meanwhile.
    /// The store is attached to the wrapper's runspace for the command (<see cref="AttachScoped"/>).
    /// </para>
    /// </summary>
    public static ScalarOverlay ForCommand(bool ignoreEnvironment, IEnumerable<string> unset,
        IEnumerable<KeyValuePair<string, string>> assignments)
    {
        ArgumentNullException.ThrowIfNull(unset);
        ArgumentNullException.ThrowIfNull(assignments);
        var values = new Dictionary<string, string>(ShellExports.NameComparer);
        if (!ignoreEnvironment)
            foreach (var (name, value) in ShellExports.Enumerate()) values[name] = value;
        // Bash names (`env -u path`, `env path=x cmd`): the store's keys are store names (BashNameCase, section 12).
        foreach (var name in unset) values.Remove(BashNameCase.ToStore(name));
        foreach (var (name, value) in assignments) values[BashNameCase.ToStore(name)] = value;
        var exports = new ShellExports.Table { Exported = new HashSet<string>(values.Keys, ShellExports.NameComparer) };
        return new ScalarOverlay(values, exports);
    }

    /// <summary>The job running in <paramref name="owner"/> (its runspace) uses this store from now on.</summary>
    public void Attach(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ByOwner.AddOrUpdate(owner, this);
        Interlocked.Increment(ref s_attached);
    }

    /// <summary>
    /// The code running in <paramref name="owner"/> uses this store until the result is disposed, when the store it
    /// had before (a job's, or none: the process environment) is back. For a command whose environment is its own
    /// (<see cref="ForCommand"/>), which may itself run inside a job.
    /// </summary>
    public IDisposable AttachScoped(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ByOwner.TryGetValue(owner, out var previous);
        ByOwner.AddOrUpdate(owner, this);
        if (previous is null) Interlocked.Increment(ref s_attached);
        return new Restore(owner, previous);
    }

    private sealed class Restore(object owner, ScalarOverlay? previous) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            if (previous is not null) ByOwner.AddOrUpdate(owner, previous);
            else Detach(owner);
        }
    }

    /// <summary>The store attached to <paramref name="owner"/>, or null (test seam).</summary>
    internal static ScalarOverlay? Of(object owner) => ByOwner.TryGetValue(owner, out var overlay) ? overlay : null;

    /// <summary>The job in <paramref name="owner"/> ended: its runspace goes back to the process environment.</summary>
    public static void Detach(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (ByOwner.Remove(owner)) Interlocked.Decrement(ref s_attached);
    }

    /// <summary>The value of <paramref name="name"/>; null when unset.</summary>
    public string? Get(string name)
    {
        lock (Gate) return _values.TryGetValue(name, out var v) ? v : null;
    }

    /// <summary>Assign <paramref name="name"/>; null unsets it. An empty value is a set variable, as in the environment.</summary>
    public void Set(string name, string? value)
    {
        if (string.IsNullOrEmpty(name)) return;
        lock (Gate)
        {
            if (value is null) _values.Remove(name);
            else _values[name] = value;
        }
    }

    /// <summary>Every set variable, copied under the lock (the caller may write while it walks the result).</summary>
    public List<KeyValuePair<string, string>> Entries()
    {
        lock (Gate) return new List<KeyValuePair<string, string>>(_values);
    }

    /// <summary>Test seam: the number of runspaces holding a store.</summary>
    internal static int AttachedCount => Volatile.Read(ref s_attached);
}
