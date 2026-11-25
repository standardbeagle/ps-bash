using System.Collections;
using System.Runtime.CompilerServices;

namespace PsBash.Cmdlets;

/// <summary>
/// A dense bash indexed array that a script has grown or written to (<c>a+=(x)</c>, <c>a[i]=v</c>). An array literal
/// (<c>a=(x y)</c>) stays a plain <c>object[]</c>; the first append or element write turns it into this.
/// <para>
/// It is a persistent vector: a 32-way trie of leaves plus a tail leaf, as in Clojure. Every operation returns a NEW
/// vector and leaves the old one as it was, so copies made by a subshell, a function scope or a background job never
/// see each other's writes. Appending is amortized O(1); reading and writing an element is O(log32 n), at most four
/// levels for a million elements. A write copies one path of at most four 32-slot nodes, not the array.
/// </para>
/// <para>
/// The tail is shared. The newest vector built on a tail fills the next slot in place, so an append loop allocates
/// one small vector object per append and one node per 32 appends. An older vector over the same tail sees only its
/// own <see cref="Count"/>, and appending to it copies the tail. A slot below some vector's count is never rewritten.
/// </para>
/// <para>
/// An array of up to 32 elements is only its tail: there is no trie, and reading an element is one array access.
/// </para>
/// It is read-only as an <see cref="IList"/>: PowerShell reads it (<c>$a[i]</c>, <c>$a.Count</c>, <c>foreach</c>,
/// <c>@($a)</c>) like an array, and writes go through <see cref="BashIndexedArray"/>.
/// </summary>
public sealed class BashVector : IList, IReadOnlyList<object?>
{
    private const int Bits = 5;
    private const int Width = 1 << Bits;
    private const int Mask = Width - 1;

    /// <summary>The 32 slots of a node, held in the node object itself (one allocation per node, not two).</summary>
    [InlineArray(Width)]
    internal struct Slots { private object? _e; }

    /// <summary>A trie node: an inner node's slots hold nodes, a leaf's hold elements. Never changed once published.</summary>
    internal class Node { public Slots Items; }

    /// <summary>The tail leaf. <see cref="Used"/> is how many slots the newest vector over it has filled.</summary>
    internal sealed class TailNode : Node { public int Used; }

    private static readonly Node EmptyRoot = new();
    // Its tail is never filled in place (Used never equals a length), so the shared empty vector keeps no element alive.
    public static readonly BashVector Empty = new(0, Bits, EmptyRoot, new TailNode { Used = -1 }, 0);

    private readonly int _count;
    private readonly int _shift;      // the root's level: leaves are level 0, the root's children level _shift - 5
    private readonly Node _root;      // the trie holds elements [0, _count - _tailLen)
    private readonly TailNode _tail;
    private readonly int _tailLen;

    private BashVector(int count, int shift, Node root, TailNode tail, int tailLen)
    {
        _count = count; _shift = shift; _root = root; _tail = tail; _tailLen = tailLen;
    }

    public int Count => _count;

    /// <summary>Same as <see cref="Count"/>: a script that reads <c>$a.Length</c> on an array gets the element count.</summary>
    public int Length => _count;

    private int TailOffset => _count - _tailLen;

    /// <summary>The vector holding <paramref name="items"/> in order.</summary>
    public static BashVector From(IEnumerable items) => Empty.AppendRange(items);

    /// <summary>
    /// The element at <paramref name="index"/>; a negative index counts back from the end, as PowerShell does for an
    /// array. Out of range is null (an unset element), not an exception. The setter throws: a vector is a value.
    /// </summary>
    public object? this[int index]
    {
        get
        {
            if (index < 0) index += _count;
            return (uint)index < (uint)_count ? Get(index) : null;
        }
        set => throw ReadOnly();
    }

    private object? Get(int i)
    {
        int off = TailOffset;
        if (i >= off) return _tail.Items[i - off];
        Node n = _root;
        for (int level = _shift; level > 0; level -= Bits) n = (Node)n.Items[(i >> level) & Mask]!;
        return n.Items[i & Mask];
    }

    /// <summary>A vector equal to this one with element <paramref name="index"/> (0 ≤ index &lt; Count) set to <paramref name="value"/>.</summary>
    public BashVector SetItem(int index, object? value)
    {
        if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
        int off = TailOffset;
        if (index >= off)
        {
            var t = CopyTail(_tail, _tailLen);
            t.Items[index - off] = value;
            return new BashVector(_count, _shift, _root, t, _tailLen);
        }
        return new BashVector(_count, _shift, AssocPath(_shift, _root, index, value), _tail, _tailLen);
    }

    private static Node AssocPath(int level, Node node, int i, object? value)
    {
        var copy = new Node { Items = node.Items };
        if (level == 0) copy.Items[i & Mask] = value;
        else
        {
            int sub = (i >> level) & Mask;
            copy.Items[sub] = AssocPath(level - Bits, (Node)node.Items[sub]!, i, value);
        }
        return copy;
    }

    public BashVector Append(object? item)
    {
        if (_tailLen < Width)
        {
            lock (_tail)
            {
                if (_tail.Used == _tailLen)
                {
                    _tail.Items[_tailLen] = item;
                    _tail.Used = _tailLen + 1;
                    return new BashVector(_count + 1, _shift, _root, _tail, _tailLen + 1);
                }
            }
            var t = CopyTail(_tail, _tailLen);
            t.Items[_tailLen] = item;
            t.Used = _tailLen + 1;
            return new BashVector(_count + 1, _shift, _root, t, _tailLen + 1);
        }
        var (root, shift) = PushTail(_count, _shift, _root, _tail);
        var fresh = new TailNode { Used = 1 };
        fresh.Items[0] = item;
        return new BashVector(_count + 1, shift, root, fresh, 1);
    }

    /// <summary>
    /// <paramref name="items"/> appended in order, as ONE new vector: the run fills tails in place and allocates no
    /// intermediate vectors.
    /// </summary>
    public BashVector AppendRange(IEnumerable items)
    {
        int count = _count, shift = _shift, tailLen = _tailLen;
        Node root = _root;
        TailNode tail = _tail;
        bool own = false;   // the tail was made by this call, so nobody else can see it yet
        foreach (var item in items)
        {
            if (tailLen == Width)
            {
                (root, shift) = PushTail(count, shift, root, tail);
                tail = new TailNode();
                tailLen = 0;
                own = true;
            }
            if (!own)
            {
                lock (tail)
                {
                    if (tail.Used == tailLen)
                    {
                        tail.Items[tailLen] = item;
                        tail.Used = ++tailLen;
                        count++;
                        continue;
                    }
                }
                tail = CopyTail(tail, tailLen);
                own = true;
            }
            tail.Items[tailLen] = item;
            tail.Used = ++tailLen;
            count++;
        }
        return count == _count ? this : new BashVector(count, shift, root, tail, tailLen);
    }

    /// <summary>
    /// The trie after a full tail is added to it. <paramref name="count"/> includes that tail. The tail becomes a
    /// leaf as it is: it is full, so no vector over it can append in place again.
    /// </summary>
    private static (Node Root, int Shift) PushTail(int count, int shift, Node root, Node fullTail)
    {
        if ((count >> Bits) > (1 << shift))
        {
            var grown = new Node();
            grown.Items[0] = root;
            grown.Items[1] = NewPath(shift, fullTail);
            return (grown, shift + Bits);
        }
        return (PushTail(count, shift, root, fullTail, level: shift), shift);
    }

    private static Node PushTail(int count, int shift, Node parent, Node fullTail, int level)
    {
        int sub = ((count - 1) >> level) & Mask;
        var copy = new Node { Items = parent.Items };
        Node insert;
        if (level == Bits) insert = fullTail;
        else insert = parent.Items[sub] is Node child
            ? PushTail(count, shift, child, fullTail, level - Bits)
            : NewPath(level - Bits, fullTail);
        copy.Items[sub] = insert;
        return copy;
    }

    private static Node NewPath(int level, Node leaf)
    {
        if (level == 0) return leaf;
        var n = new Node();
        n.Items[0] = NewPath(level - Bits, leaf);
        return n;
    }

    /// <summary>A private copy of a tail's first <paramref name="len"/> slots (the rest cleared, so they hold no garbage).</summary>
    private static TailNode CopyTail(TailNode tail, int len)
    {
        var t = new TailNode { Items = tail.Items, Used = len };
        Span<object?> slots = t.Items;
        slots[len..].Clear();
        return t;
    }

    public object?[] ToArray()
    {
        var a = new object?[_count];
        int i = 0;
        foreach (var leaf in Leaves())
        {
            ReadOnlySpan<object?> slots = leaf.Items;
            int n = Math.Min(Width, _count - i);
            slots[..n].CopyTo(a.AsSpan(i));
            i += n;
        }
        return a;
    }

    // The leaves in order: one trie walk per 32 elements, not one per element.
    private IEnumerable<Node> Leaves()
    {
        int off = TailOffset;
        for (int i = 0; i < off; i += Width)
        {
            Node n = _root;
            for (int level = _shift; level > 0; level -= Bits) n = (Node)n.Items[(i >> level) & Mask]!;
            yield return n;
        }
        if (_tailLen > 0) yield return _tail;
    }

    public IEnumerator<object?> GetEnumerator()
    {
        int i = 0;
        foreach (var leaf in Leaves())
        {
            int n = Math.Min(Width, _count - i);
            for (int j = 0; j < n; j++) yield return leaf.Items[j];
            i += n;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Contains(object? value) => IndexOf(value) >= 0;

    public int IndexOf(object? value)
    {
        int i = 0;
        foreach (var item in this)
        {
            if (Equals(item, value)) return i;
            i++;
        }
        return -1;
    }

    public void CopyTo(Array array, int index)
    {
        foreach (var item in this) array.SetValue(item, index++);
    }

    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;

    public int Add(object? value) => throw ReadOnly();
    public void Clear() => throw ReadOnly();
    public void Insert(int index, object? value) => throw ReadOnly();
    public void Remove(object? value) => throw ReadOnly();
    public void RemoveAt(int index) => throw ReadOnly();

    private static NotSupportedException ReadOnly()
        => new("A bash array is a value: write an element with BashIndexedArray.SetElement, which returns a new array.");
}
