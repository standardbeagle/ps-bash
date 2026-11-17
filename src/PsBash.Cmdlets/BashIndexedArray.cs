using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Operations on a dense bash indexed array, which ps-bash holds as a PowerShell <c>object[]</c>.
/// </summary>
public static class BashIndexedArray
{
    /// <summary>
    /// <c>a+=(items)</c> on a dense array. Same result as PowerShell's <c>@(@($current) + @($items))</c>,
    /// with the same flattening: a <c>$null</c> adds nothing, an enumerable adds its elements, and anything
    /// else adds itself. The difference is that object arrays are block-copied. PowerShell's array
    /// <c>+</c> enumerates every element through the pipeline machinery, so an append loop of n items cost
    /// O(n²) script-speed steps: 123 µs per append at 5000 elements, against a few µs for a block copy.
    /// </summary>
    public static object?[] Concat(object? current, object? items)
    {
        if (current is object?[] a && items is object?[] b)
        {
            var joined = new object?[a.Length + b.Length];
            Array.Copy(a, joined, a.Length);
            Array.Copy(b, 0, joined, a.Length, b.Length);
            return joined;
        }
        var list = new List<object?>();
        AddFlattened(list, current);
        AddFlattened(list, items);
        return list.ToArray();
    }

    // What @($v) contributes, by PowerShell's own enumeration rule (strings and dictionaries are single values).
    private static void AddFlattened(List<object?> list, object? v)
    {
        if (v is null) return;
        if (v is object?[] arr) { list.AddRange(arr); return; }
        var e = LanguagePrimitives.GetEnumerable(v);
        if (e is null) { list.Add(v); return; }
        foreach (var o in e) list.Add(o);
    }
}
