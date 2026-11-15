using System.Runtime.CompilerServices;

namespace PsBash.Core;

/// <summary>
/// bash's hash table: the PATH lookups of external commands a shell has made, with a hit count each. The host records
/// a command word that resolved to a program file (its command-lookup hook) and the <c>hash</c> builtin lists, adds,
/// forgets and prints entries. One table per runspace (one per shell), held weakly so a closed runspace frees it.
/// Lives in the Transpiler assembly because that is the one the host and the cmdlets both load.
/// <para>As in bash, a change of <c>PATH</c> empties the table: each operation first compares the PATH it was
/// built under with the current one.</para>
/// </summary>
public sealed class CommandHashTable
{
    private static readonly ConditionalWeakTable<object, CommandHashTable> Tables = new();

    /// <summary>One remembered command: the file it resolved to and how often it was used.</summary>
    public readonly record struct Entry(string Name, string Path, int Hits);

    private readonly List<Entry> _entries = new();
    private string? _pathValue;

    [ThreadStatic] private static int _probeDepth;

    /// <summary>
    /// True while a ps-bash command is only asking what a name resolves to (<see cref="Probe"/>): PowerShell runs the
    /// same command-lookup hook for that question as for running the command, and a question is not a use.
    /// </summary>
    public static bool InProbe => _probeDepth > 0;

    /// <summary>Marks the lookups made until disposal as questions, not uses: the host records none of them.</summary>
    public static ProbeScope Probe()
    {
        _probeDepth++;
        return default;
    }

    /// <summary>Ends a <see cref="Probe"/>.</summary>
    public readonly struct ProbeScope : IDisposable
    {
        public void Dispose()
        {
            if (_probeDepth > 0) _probeDepth--;
        }
    }

    /// <summary>The table of the shell <paramref name="owner"/> (its runspace).</summary>
    public static CommandHashTable For(object owner) => Tables.GetValue(owner, _ => new CommandHashTable());

    private int IndexOf(string name)
    {
        for (int i = 0; i < _entries.Count; i++)
            if (string.Equals(_entries[i].Name, name, StringComparison.Ordinal)) return i;
        return -1;
    }

    private void Sync(string? pathValue)
    {
        if (string.Equals(_pathValue, pathValue, StringComparison.Ordinal)) return;
        _entries.Clear();
        _pathValue = pathValue;
    }

    /// <summary>The command <paramref name="name"/> ran from <paramref name="path"/>: one more hit, remembered if new.</summary>
    public void RecordUse(string name, string path, string? pathValue)
    {
        lock (_entries)
        {
            Sync(pathValue);
            int i = IndexOf(name);
            if (i < 0) _entries.Add(new Entry(name, path, 1));
            else _entries[i] = _entries[i] with { Path = path, Hits = _entries[i].Hits + 1 };
        }
    }

    /// <summary><c>hash NAME</c> / <c>hash -p PATH NAME</c>: remember NAME as PATH with no hits yet.</summary>
    public void Add(string name, string path, string? pathValue)
    {
        lock (_entries)
        {
            Sync(pathValue);
            int i = IndexOf(name);
            if (i >= 0) _entries[i] = new Entry(name, path, 0);
            else _entries.Add(new Entry(name, path, 0));
        }
    }

    /// <summary><c>hash -t NAME</c>: the remembered path (a lookup, so one more hit), or null when NAME is not hashed.</summary>
    public string? Lookup(string name, string? pathValue)
    {
        lock (_entries)
        {
            Sync(pathValue);
            int i = IndexOf(name);
            if (i < 0) return null;
            _entries[i] = _entries[i] with { Hits = _entries[i].Hits + 1 };
            return _entries[i].Path;
        }
    }

    /// <summary><c>hash -d NAME</c>: false when NAME was not hashed.</summary>
    public bool Remove(string name, string? pathValue)
    {
        lock (_entries)
        {
            Sync(pathValue);
            int i = IndexOf(name);
            if (i < 0) return false;
            _entries.RemoveAt(i);
            return true;
        }
    }

    /// <summary><c>hash -r</c>.</summary>
    public void Clear(string? pathValue)
    {
        lock (_entries)
        {
            _entries.Clear();
            _pathValue = pathValue;
        }
    }

    /// <summary>The remembered commands, oldest first.</summary>
    public Entry[] Snapshot(string? pathValue)
    {
        lock (_entries)
        {
            Sync(pathValue);
            return [.. _entries];
        }
    }
}
