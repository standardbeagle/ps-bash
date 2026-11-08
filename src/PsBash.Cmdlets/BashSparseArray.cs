using System.Collections;

namespace PsBash.Cmdlets;

/// <summary>
/// A bash indexed array that has holes (<c>a[7]=q</c> on a 3-element array, <c>unset 'a[1]'</c>). A dense
/// array stays a plain PowerShell array; the first operation that leaves a gap converts it to this type.
/// It is an <see cref="IDictionary"/> keyed by the integer subscript, so the dictionary paths the associative
/// arrays already use (<c>.Keys</c>, <c>.Values</c>, <c>.Count</c>, <c>$a[i]</c>) work unchanged, except that
/// keys and values come out in ascending subscript order and a negative subscript counts back from the end.
/// </summary>
public sealed class BashSparseArray : IDictionary
{
    private readonly SortedDictionary<long, object?> _items = new();

    public BashSparseArray() { }

    public static BashSparseArray FromList(IEnumerable? items)
    {
        var sparse = new BashSparseArray();
        if (items is null) return sparse;
        long i = 0;
        foreach (var item in items)
        {
            if (item is not null) sparse._items[i] = item;
            i++;
        }
        return sparse;
    }

    /// <summary><c>a=([2]=p [5]=q)</c>: subscript/value pairs (later pairs win). A run 0..n-1 with no gap is an ordinary dense
    /// array (returned as <c>object[]</c>); anything else stays sparse so the holes remain unset.</summary>
    public static object FromEntries(params object?[] subscriptValuePairs)
    {
        var sparse = new BashSparseArray();
        for (int i = 0; i + 1 < subscriptValuePairs.Length; i += 2)
            if (long.TryParse(Convert.ToString(subscriptValuePairs[i], System.Globalization.CultureInfo.InvariantCulture), out var at))
                sparse._items[at] = subscriptValuePairs[i + 1] ?? "";
        long n = 0;
        foreach (var k in sparse._items.Keys) { if (k != n) return sparse; n++; }
        return sparse._items.Values.ToArray();
    }

    /// <summary><c>${a[@]:offset:length}</c>: the offset is a SUBSCRIPT, not a position (the elements at index >= offset), and a
    /// negative one counts back from one past the highest subscript. <paramref name="length"/> &lt; 0 means "to the end".</summary>
    public object[] Slice(long offset, long length)
    {
        if (offset < 0) { offset += NextIndex; if (offset < 0) return Array.Empty<object>(); }
        var picked = new List<object>();
        foreach (var kv in _items)
        {
            if (kv.Key < offset) continue;
            if (length >= 0 && picked.Count >= length) break;
            picked.Add(kv.Value ?? "");
        }
        return picked.ToArray();
    }

    /// <summary><c>a+=(x y)</c>: the new elements take the subscripts after the highest one in use.</summary>
    public static BashSparseArray operator +(BashSparseArray array, object? appended)
    {
        long next = array.NextIndex;
        if (appended is string || appended is not IEnumerable items) { array._items[next] = appended; return array; }
        foreach (var item in items) { if (item is not null) array._items[next] = item; next++; }
        return array;
    }

    /// <summary>One past the highest subscript in use (bash's "next index" for <c>a+=(x)</c>).</summary>
    public long NextIndex
    {
        get
        {
            long next = 0;
            foreach (var k in _items.Keys) next = k + 1;
            return next;
        }
    }

    /// <summary>Resolves a subscript; a negative one counts back from <see cref="NextIndex"/>. Null when out of range.</summary>
    public long? Resolve(long index)
    {
        if (index >= 0) return index;
        long resolved = NextIndex + index;
        return resolved < 0 ? null : resolved;
    }

    /// <summary>The integer subscript of a key, or null when it is not one (PowerShell probes a dictionary with member names such as <c>Count</c> first).</summary>
    private static long? ToIndex(object? key) => key switch
    {
        long l => l,
        int i => i,
        string s when long.TryParse(s, out var p) => p,
        null => 0,
        string => null,
        IConvertible c when key is not string => c.ToInt64(System.Globalization.CultureInfo.InvariantCulture),
        _ => null,
    };

    public object? this[object key]
    {
        get
        {
            var at = ToIndex(key) is { } k ? Resolve(k) : null;
            return at is { } i && _items.TryGetValue(i, out var v) ? v : null;
        }
        set
        {
            var at = (ToIndex(key) is { } k ? Resolve(k) : null) ?? throw new ArgumentOutOfRangeException(nameof(key), "bad array subscript");
            if (value is null) _items.Remove(at); else _items[at] = value;
        }
    }

    /// <summary>Removes one element, keeping every other subscript. Returns false when it was not set.</summary>
    public bool RemoveAt(long index) => Resolve(index) is { } i && _items.Remove(i);

    public ICollection Keys => _items.Keys.ToArray();
    public ICollection Values => _items.Values.ToArray();
    public int Count => _items.Count;
    public bool IsReadOnly => false;
    public bool IsFixedSize => false;
    public bool IsSynchronized => false;
    public object SyncRoot => this;

    public bool Contains(object key) => ToIndex(key) is { } k && Resolve(k) is { } i && _items.ContainsKey(i);
    public void Add(object key, object? value) => this[key] = value;
    public void Clear() => _items.Clear();
    public void Remove(object key) { if (ToIndex(key) is { } k) RemoveAt(k); }

    public IDictionaryEnumerator GetEnumerator() => new Enumerator(_items.ToArray());
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void CopyTo(Array array, int index)
    {
        foreach (var kv in _items) array.SetValue(new DictionaryEntry(kv.Key, kv.Value), index++);
    }

    private sealed class Enumerator : IDictionaryEnumerator
    {
        private readonly KeyValuePair<long, object?>[] _snapshot;
        private int _pos = -1;
        public Enumerator(KeyValuePair<long, object?>[] snapshot) => _snapshot = snapshot;
        public DictionaryEntry Entry => new(_snapshot[_pos].Key, _snapshot[_pos].Value);
        public object Key => _snapshot[_pos].Key;
        public object? Value => _snapshot[_pos].Value;
        public object Current => Entry;
        public bool MoveNext() => ++_pos < _snapshot.Length;
        public void Reset() => _pos = -1;
    }
}
