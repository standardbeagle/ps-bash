using System.Collections;

namespace PsBash.Cmdlets;

/// <summary>
/// A bash associative array (<c>declare -A</c>). Two things a PowerShell <c>@{}</c> gets wrong, both silent:
/// keys are CASE-SENSITIVE here (<c>m[a]=1; m[A]=2</c> is two entries), and enumeration follows bash's own order,
/// so <c>${!m[@]}</c>, <c>${m[@]}</c>, <c>for k in "${!m[@]}"</c> and <c>declare -p m</c> list keys the way bash does.
/// <para>
/// bash 5.2 keeps the keys in a 1024-bucket hash table (FNV-1 over the key's bytes, a new key goes at the head of
/// its bucket, an existing key keeps its place) and walks the buckets in index order. That order is reproduced here
/// by remembering each key's insertion sequence and sorting by <c>(bucket, newest first)</c> when enumerated; verified
/// against the bash oracle over random key sets.
/// </para>
/// It is a <see cref="Hashtable"/>, so every existing <c>-is [IDictionary]</c> / <c>.Keys</c> / <c>$m[k]</c> path works.
/// </summary>
public sealed class BashAssoc : Hashtable, IEnumerable
{
    private const int Buckets = 1024;
    private readonly Dictionary<string, long> _seq = new(StringComparer.Ordinal);
    private long _next;

    public BashAssoc() : base(StringComparer.Ordinal) { }

    /// <summary>The bucket index bash 5.2 files <paramref name="key"/> under.</summary>
    public static int BucketOf(string key)
    {
        uint h = 2166136261;
        foreach (var b in PsBash.Core.RawBytes.Encoding.GetBytes(key))
        {
            h *= 16777619;
            h ^= (uint)(int)(sbyte)b;   // bash hashes `char`, which is signed: a byte >= 0x80 sign-extends
        }
        return (int)(h & (Buckets - 1));
    }

    /// <summary>True when <paramref name="value"/> is an INDEXED array (a list or a sparse array): <c>declare -A</c> refuses it.</summary>
    public static bool IsIndexedValue(object? value)
    {
        if (value is System.Management.Automation.PSObject w) value = w.BaseObject;
        return value is BashSparseArray || (value is IList && value is not string);
    }

    /// <summary>True when <paramref name="value"/> is an associative array: <c>declare -a</c> refuses it.</summary>
    public static bool IsAssocValue(object? value)
    {
        if (value is System.Management.Automation.PSObject w) value = w.BaseObject;
        return value is BashAssoc;
    }
    /// <summary>Builds a table from key/value pairs in the order given (a state snapshot replays them oldest first).</summary>
    public static BashAssoc FromPairs(params object?[] keyValuePairs)
    {
        var t = new BashAssoc();
        for (int i = 0; i + 1 < keyValuePairs.Length; i += 2) t[keyValuePairs[i]!] = keyValuePairs[i + 1];
        return t;
    }

    /// <summary><c>declare -A a; a=(k1 v1 k2)</c> (bash 5.1+): words alternate key, value; a last key with no value gets "".</summary>
    public static BashAssoc FromWords(object?[] words)
    {
        var t = new BashAssoc();
        for (int i = 0; i < words.Length; i += 2) t[words[i] ?? ""] = i + 1 < words.Length ? words[i + 1] : "";
        return t;
    }

    /// <summary>
    /// <c>a=(w1 w2 …)</c> where the emitter cannot know <c>a</c>'s kind (eval'd text: <c>declare -A a; eval 'a=(1 2)'</c>):
    /// an associative <c>a</c> takes the words as key/value pairs (<see cref="FromWords"/>), anything else the list.
    /// </summary>
    public static object ListOrPairs(object? existing, object?[] words)
    {
        var raw = existing is System.Management.Automation.PSObject pso ? pso.BaseObject : existing;
        return raw is BashAssoc ? FromWords(words) : words;
    }

    /// <summary><c>a+=([k]=v ...)</c>: the added pairs go into the existing table; with no table yet, they are the table.</summary>
    public static BashAssoc Merge(object? existing, BashAssoc added)
    {
        var raw = existing is System.Management.Automation.PSObject pso ? pso.BaseObject : existing;
        // `s=world; declare -A s+=([a]=x)`: the string becomes element "0" (bash: `([0]="world" [a]="x")`).
        if (raw is string scalar)
        {
            var promoted = new BashAssoc();
            promoted["0"] = scalar;
            raw = promoted;
        }
        if (raw is not BashAssoc target) return added;
        foreach (var kv in added.ReplayOrder()) target[kv.Key] = kv.Value;
        return target;
    }

    private static string K(object key) => key as string ?? Convert.ToString(key, System.Globalization.CultureInfo.InvariantCulture) ?? "";

    /// <summary>Keys in bash's enumeration order.</summary>
    public List<string> OrderedKeys()
    {
        var keys = new List<string>(_seq.Count);
        foreach (var k in _seq.Keys) keys.Add(k);
        keys.Sort((a, b) =>
        {
            int c = BucketOf(a).CompareTo(BucketOf(b));
            return c != 0 ? c : _seq[b].CompareTo(_seq[a]);   // newest first within a bucket
        });
        return keys;
    }

    /// <summary>The pairs in the order a snapshot must replay them to rebuild the same table: reverse of enumeration.</summary>
    public List<KeyValuePair<string, object?>> ReplayOrder()
    {
        var ordered = OrderedKeys();
        ordered.Reverse();
        var pairs = new List<KeyValuePair<string, object?>>(ordered.Count);
        foreach (var k in ordered) pairs.Add(new(k, base[k]));
        return pairs;
    }

    public override object? this[object key]
    {
        get => base[K(key)];
        set
        {
            var k = K(key);
            if (!base.ContainsKey(k)) _seq[k] = _next++;
            base[k] = value;
        }
    }

    public override void Add(object key, object? value)
    {
        var k = K(key);
        base.Add(k, value);
        _seq[k] = _next++;
    }

    public override void Remove(object key)
    {
        var k = K(key);
        base.Remove(k);
        _seq.Remove(k);
    }

    public override void Clear()
    {
        base.Clear();
        _seq.Clear();
    }

    public override bool Contains(object key) => base.ContainsKey(K(key));
    public override bool ContainsKey(object key) => base.ContainsKey(K(key));

    public override ICollection Keys => OrderedKeys();

    public override ICollection Values
    {
        get
        {
            var keys = OrderedKeys();
            var values = new List<object?>(keys.Count);
            foreach (var k in keys) values.Add(base[k]);
            return values;
        }
    }

    public override IDictionaryEnumerator GetEnumerator() => new Enumerator(this);

    IEnumerator IEnumerable.GetEnumerator() => new Enumerator(this);

    public override object Clone()
    {
        var copy = new BashAssoc();
        foreach (var kv in ReplayOrder()) copy[kv.Key] = kv.Value;
        return copy;
    }

    private sealed class Enumerator : IDictionaryEnumerator
    {
        private readonly List<DictionaryEntry> _entries;
        private int _i = -1;

        public Enumerator(BashAssoc owner)
        {
            var keys = owner.OrderedKeys();
            _entries = new List<DictionaryEntry>(keys.Count);
            foreach (var k in keys) _entries.Add(new DictionaryEntry(k, owner.GetBase(k)));
        }

        public bool MoveNext() => ++_i < _entries.Count;
        public void Reset() => _i = -1;
        public object Current => Entry;
        public DictionaryEntry Entry => _entries[_i];
        public object Key => Entry.Key;
        public object? Value => Entry.Value;
    }

    private object? GetBase(string key) => base[key];
}
