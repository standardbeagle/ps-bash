using System.Collections;
using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Operations on a dense bash indexed array. A literal (<c>a=(x y)</c>) is a PowerShell <c>object[]</c>; an array a
/// script grows or writes to is a <see cref="BashVector"/>, which these operations return.
/// </summary>
public static class BashIndexedArray
{
    /// <summary>
    /// <c>a+=(items)</c>. <paramref name="current"/> is the array, or the variable's scalar value (bash keeps it as
    /// element 0), or null. <paramref name="items"/> flattens as <c>@($items)</c> does. A sparse array takes the
    /// items after its highest subscript, in place; any other value becomes a <see cref="BashVector"/>, and appending
    /// to a vector costs amortized O(1) per item where an <c>object[]</c> copy cost O(n).
    /// </summary>
    public static object Append(object? current, object? items)
    {
        if (current is PSObject w) current = w.BaseObject;
        if (current is BashSparseArray sparse)
        {
            long n = sparse.NextIndex;
            foreach (var item in Flatten(items)) sparse[n++] = item;
            return sparse;
        }
        return ToVector(current).AppendRange(Flatten(items));
    }

    /// <summary>A dense array (or scalar, or null) as a <see cref="BashVector"/>: O(1) when it already is one.</summary>
    public static BashVector ToVector(object? value)
    {
        if (value is PSObject w) value = w.BaseObject;
        return value as BashVector ?? BashVector.From(Flatten(value));
    }

    /// <summary>
    /// <c>a[index]=value</c> (or <c>+=</c>, which appends to the element's text) on a dense array, with
    /// 0 ≤ <paramref name="index"/> ≤ Count (the caller resolves a negative subscript and makes a gap sparse).
    /// O(log32 n): the result shares all but one path with <paramref name="array"/>, which is left as it was.
    /// </summary>
    public static BashVector SetElement(BashVector array, long index, object? value, bool append)
    {
        if (append)
        {
            object? old = index < array.Count ? array[(int)index] : null;
            value = (LanguagePrimitives.ConvertTo<string>(old) ?? "") + (LanguagePrimitives.ConvertTo<string>(value) ?? "");
        }
        return index == array.Count ? array.Append(value) : array.SetItem(checked((int)index), value);
    }

    /// <summary>
    /// <c>@(@($current) + @($items))</c> as an <c>object[]</c>, with PowerShell's flattening (<see cref="Flatten"/>):
    /// the rule <see cref="Append"/> uses.
    /// </summary>
    /// <summary>
    /// <c>${a[@]:offset}</c> / <c>${a[@]:offset:length}</c> (<paramref name="length"/> null): the elements bash selects,
    /// reading only those. A dense array (an <c>object[]</c>, a <see cref="BashVector"/>) is indexed directly, so a loop
    /// taking one element per pass (<c>${a[@]:i:1}</c>) is O(1) per pass, not a copy of the array. A negative offset
    /// counts from the end and one before the first element selects nothing; a negative length selects nothing. A
    /// sparse array's offset is a SUBSCRIPT (<see cref="BashSparseArray.Slice"/>); an associative array slices its
    /// values; a scalar is a one-element array.
    /// </summary>
    public static object?[] Slice(object? source, long offset, long? length)
    {
        if (source is PSObject pso) source = pso.BaseObject;
        if (source is BashSparseArray sparse) return sparse.Slice(offset, length is { } l ? Math.Max(0, l) : -1);
        IList list = source switch
        {
            null => Array.Empty<object?>(),
            IDictionary dict => new ArrayList(dict.Values),
            IList l => l,
            _ => new[] { source },
        };
        int count = list.Count;
        long o = offset;
        if (o < 0) { o += count; if (o < 0) o = count; }
        o = Math.Clamp(o, 0, count);
        long n = length ?? count - o;
        n = Math.Min(Math.Max(n, 0), count - o);
        if (n <= 0) return Array.Empty<object?>();
        var result = new object?[n];
        if (list is object?[] arr) Array.Copy(arr, o, result, 0, n);
        else for (int i = 0; i < n; i++) result[i] = list[(int)(o + i)];
        return result;
    }

    public static object?[] Concat(object? current, object? items) => ToVector(current).AppendRange(Flatten(items)).ToArray();

    // What @($v) contributes, by PowerShell's own enumeration rule: null adds nothing, a string or dictionary is one value.
    private static IEnumerable Flatten(object? v)
    {
        if (v is PSObject w && w.BaseObject is IEnumerable) v = w.BaseObject;
        if (v is null) return Array.Empty<object?>();
        if (v is object?[] or BashVector) return (IEnumerable)v;
        return LanguagePrimitives.GetEnumerable(v) ?? new[] { v };
    }
}
