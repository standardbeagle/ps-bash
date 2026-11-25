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
