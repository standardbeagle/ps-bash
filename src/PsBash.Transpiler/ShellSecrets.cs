using System.Runtime.CompilerServices;

namespace PsBash.Core;

/// <summary>
/// The secrets of ONE session: variables its host (an embedding such as BeagleTerm's session server) leased to that
/// session, which never enter the process environment.
/// <para>
/// <b>Why.</b> ps-bash keeps every scalar shell variable in the process environment, which every runspace of the host
/// process shares (<c>docs/specs/environment-model.md</c>). A key written as <c>$env:OPENAI_API_KEY</c> in one session
/// is readable from every other session, and from every program started without the export filter. A secret is held
/// here instead, per runspace, and is seen only by:
/// </para>
/// <list type="bullet">
/// <item>the shell of that session: <c>$NAME</c>, <c>$env:NAME</c>, <c>printenv NAME</c> read the value
/// (<c>BashVariableStore.Get</c> resolves it before the process environment);</item>
/// <item>its children, when GRANTED: every child environment is built by <see cref="ShellExports"/>, which adds the
/// granted secrets and removes the others. A secret is passed on by grant, never by the export table:
/// <c>export NAME</c> grants nothing, <c>NAME=v cmd</c> grants it to that command only, and <c>env -i cmd</c> gives
/// it to nobody.</item>
/// </list>
/// <para>
/// Listings (<c>env</c>, <c>printenv</c>, <c>declare -p</c>/<c>-x</c>, <c>set</c>, <c>Get-ChildItem Env:</c>) show a
/// secret's name with <see cref="Mask"/>. A script's own assignment to a secret's name stays in this session (it
/// replaces the session's value; <c>unset</c> drops it), so it never reaches the process environment either.
/// </para>
/// <para>
/// Host side: create one per session, <see cref="Set"/> the leased values, <see cref="Register"/> it for the session's
/// runspace, and open that runspace through <c>PsBash.Cmdlets.ShellSecretsSession</c> (its <c>Env:</c> drive and
/// program lookup must be ps-bash's). Without a registration nothing changes. Spec: <c>docs/specs/environment-model.md</c>
/// section 13.
/// </para>
/// </summary>
public sealed class ShellSecrets
{
    /// <summary>What a listing prints in place of a secret's value.</summary>
    public const string Mask = "‹secret›";

    private static readonly ConditionalWeakTable<object, ShellSecrets> s_byRunspace = new();

    /// <summary>Set once any session registered secrets: until then every lookup is one read.</summary>
    private static int s_inUse;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _items;

    private sealed class Entry(string value, bool granted)
    {
        public string Value = value;
        public bool Granted = granted;
        /// <summary>Open <c>NAME=v cmd</c> commands that pass this name on although it is not granted.</summary>
        public int TempGrants;
    }

    public ShellSecrets() => _items = new Dictionary<string, Entry>(ShellExports.NameComparer);

    private ShellSecrets(Dictionary<string, Entry> items) => _items = items;

    /// <summary>
    /// Gives the session <paramref name="name"/> = <paramref name="value"/>. <paramref name="granted"/>: every child
    /// of the session gets it; otherwise only the shell reads it, and a child only through <c>NAME=v cmd</c>.
    /// Replaces the value and grant the name had (a rotation).
    /// </summary>
    public void Set(string name, string value, bool granted = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);
        name = BashNameCase.ToStore(name);
        lock (_gate)
        {
            if (_items.TryGetValue(name, out var e)) { e.Value = value; e.Granted = granted; }
            else _items[name] = new Entry(value, granted);
        }
    }

    /// <summary>Takes <paramref name="name"/> away (a revocation). Programs that already have it keep it.</summary>
    public bool Remove(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        name = BashNameCase.ToStore(name);
        lock (_gate) return _items.Remove(name);
    }

    /// <summary>The names the session holds now (store names).</summary>
    public IReadOnlyList<string> Names
    {
        get { lock (_gate) return [.. _items.Keys]; }
    }

    /// <summary>Gives <paramref name="runspace"/> these secrets; replaces any it had.</summary>
    public static void Register(object runspace, ShellSecrets secrets)
    {
        ArgumentNullException.ThrowIfNull(runspace);
        ArgumentNullException.ThrowIfNull(secrets);
        s_byRunspace.AddOrUpdate(runspace, secrets);
        Volatile.Write(ref s_inUse, 1);
    }

    /// <summary>Takes <paramref name="runspace"/>'s secrets away. Its jobs keep the copy they took at their <c>&amp;</c>.</summary>
    public static void Unregister(object runspace)
    {
        ArgumentNullException.ThrowIfNull(runspace);
        s_byRunspace.Remove(runspace);
    }

    /// <summary>The secrets registered for <paramref name="runspace"/>, if any.</summary>
    public static ShellSecrets? For(object? runspace)
        => runspace is not null && s_byRunspace.TryGetValue(runspace, out var s) ? s : null;

    /// <summary>
    /// The secrets of the code running on this thread: a job's or a command's own copy (<see cref="ScalarOverlay"/>,
    /// taken at its start), else its runspace's registration. Null when the session has none.
    /// </summary>
    public static ShellSecrets? Current
    {
        get
        {
            if (Volatile.Read(ref s_inUse) == 0) return null;
            if (ScalarOverlay.Current is { } overlay) return overlay.Secrets;
            return For(ScalarOverlay.CurrentOwner?.Invoke());
        }
    }

    /// <summary>Whether the session holds <paramref name="name"/> (a store name).</summary>
    public bool Contains(string name)
    {
        lock (_gate) return _items.ContainsKey(name);
    }

    /// <summary>The value of <paramref name="name"/> (a store name) for the shell's own reads.</summary>
    public bool TryGet(string name, out string value)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(name, out var e)) { value = e.Value; return true; }
        }
        value = "";
        return false;
    }

    /// <summary>
    /// A script's own write to <paramref name="name"/> (a store name): when the session holds it, the new value stays
    /// here (null drops it) and the call returns true; otherwise false, and the write goes to the shell's store.
    /// </summary>
    public bool TryAssign(string name, string? value)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(name, out var e)) return false;
            if (value is null) _items.Remove(name);
            else e.Value = value;
            return true;
        }
    }

    /// <summary>Whether a child gets <paramref name="name"/> now: granted, or inside a <c>NAME=v cmd</c>.</summary>
    public bool IsPassedOn(string name)
    {
        lock (_gate) return _items.TryGetValue(name, out var e) && (e.Granted || e.TempGrants > 0);
    }

    /// <summary><c>NAME=v cmd</c> starts: each held name goes to the command's children until <see cref="EndTempGrant"/>.</summary>
    public void BeginTempGrant(IReadOnlyList<string> names)
    {
        lock (_gate)
            foreach (var n in names)
                if (_items.TryGetValue(BashNameCase.ToStore(n), out var e)) e.TempGrants++;
    }

    /// <summary>The command of <see cref="BeginTempGrant"/> ended.</summary>
    public void EndTempGrant(IReadOnlyList<string> names)
    {
        lock (_gate)
            foreach (var n in names)
                if (_items.TryGetValue(BashNameCase.ToStore(n), out var e) && e.TempGrants > 0) e.TempGrants--;
    }

    /// <summary>
    /// Applies the session's secrets to a child's environment block: the ones passed on are set, the others removed
    /// (a same-named variable another session put in the shared process environment is not this session's to give).
    /// </summary>
    public void ApplyTo(IDictionary<string, string?> childEnvironment)
    {
        ArgumentNullException.ThrowIfNull(childEnvironment);
        lock (_gate)
        {
            foreach (var (name, e) in _items)
            {
                if (e.Granted || e.TempGrants > 0) childEnvironment[name] = e.Value;
                else childEnvironment.Remove(name);
            }
        }
    }

    /// <summary>The secrets a child gets now, with their values.</summary>
    public List<KeyValuePair<string, string>> PassedOn()
    {
        var result = new List<KeyValuePair<string, string>>();
        lock (_gate)
            foreach (var (name, e) in _items)
                if (e.Granted || e.TempGrants > 0) result.Add(new KeyValuePair<string, string>(name, e.Value));
        return result;
    }

    /// <summary>
    /// A copy, for a job (<paramref name="passedOnOnly"/> false: what the shell reads) or a command run with its own
    /// environment (true: what a child gets, all of it granted). Later writes to either do not reach the other.
    /// </summary>
    public ShellSecrets Copy(bool passedOnOnly)
    {
        var items = new Dictionary<string, Entry>(ShellExports.NameComparer);
        lock (_gate)
        {
            foreach (var (name, e) in _items)
            {
                bool passed = e.Granted || e.TempGrants > 0;
                if (passedOnOnly && !passed) continue;
                items[name] = new Entry(e.Value, passedOnOnly || e.Granted);
            }
        }
        return new ShellSecrets(items);
    }

    /// <summary>What a subshell may change, for <see cref="RestoreFrom"/> when it ends. Opaque.</summary>
    public object Save()
    {
        lock (_gate)
        {
            var saved = new Dictionary<string, (string, bool)>(ShellExports.NameComparer);
            foreach (var (name, e) in _items) saved[name] = (e.Value, e.Granted);
            return saved;
        }
    }

    /// <summary>A subshell ended: the names it assigned or unset have the values they had at its start.</summary>
    public void RestoreFrom(object saved)
    {
        if (saved is not Dictionary<string, (string Value, bool Granted)> was) return;
        lock (_gate)
        {
            foreach (var name in _items.Keys.ToList())
                if (!was.ContainsKey(name)) _items.Remove(name);
            foreach (var (name, (value, granted)) in was)
            {
                if (_items.TryGetValue(name, out var e)) e.Value = value;
                else _items[name] = new Entry(value, granted);
            }
        }
    }

}
