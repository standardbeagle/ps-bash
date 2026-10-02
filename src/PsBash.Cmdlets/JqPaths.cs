using System.Collections;
using System.Collections.Specialized;

namespace PsBash.Cmdlets;

/// <summary><c>getpath</c>, <c>setpath</c>, <c>delpaths</c> — jq's path algebra over the value model.</summary>
internal static class JqPaths
{
    private const int MaxArrayIndex = 536870911;

    private static JqError Err(string m) => new(m);

    public static object? GetPath(object? value, object?[] path)
    {
        object? cur = value;
        foreach (var key in path)
        {
            if (cur == null) return null;
            cur = JqInterp.Index(cur, key);
        }
        return cur;
    }

    public static object? SetPath(object? value, object?[] path, object? nv) => SetPath(value, path, 0, nv);

    private static object? SetPath(object? value, object?[] path, int i, object? nv)
    {
        if (i == path.Length) return nv;
        object? key = path[i];
        switch (key)
        {
            case string s:
                {
                    if (value != null && value is not IDictionary) throw Err($"Cannot index {JqValue.TypeName(value)} with string \"{s}\"");
                    var d = value == null ? new OrderedDictionary() : JqValue.CopyObject((IDictionary)value);
                    object? child = d.Contains(s) ? d[s] : null;
                    d[s] = SetPath(child, path, i + 1, nv);
                    return d;
                }
            case var k when JqValue.IsNumber(k):
                {
                    if (value != null && value is not object?[]) throw Err($"Cannot index {JqValue.TypeName(value)} with number");
                    var arr = value as object?[] ?? Array.Empty<object?>();
                    double dbl = JqValue.ToDouble(k);
                    int idx = (int)Math.Floor(dbl);
                    if (idx < 0)
                    {
                        idx += arr.Length;
                        if (idx < 0) throw Err("Out of bounds negative array index");
                    }
                    if (idx > MaxArrayIndex) throw Err("Array index too large");
                    object? child = idx < arr.Length ? arr[idx] : null;
                    var result = new object?[Math.Max(arr.Length, idx + 1)];
                    Array.Copy(arr, result, arr.Length);
                    result[idx] = SetPath(child, path, i + 1, nv);
                    return result;
                }
            case IDictionary slice:
                {
                    if (value != null && value is not object?[]) throw Err($"Cannot update field at object index of {JqValue.TypeName(value)}");
                    var arr = value as object?[] ?? Array.Empty<object?>();
                    var from = slice.Contains("start") ? slice["start"] : null;
                    var to = slice.Contains("end") ? slice["end"] : null;
                    var (a, b) = Bounds(arr.Length, from, to);
                    var current = arr.Skip(a).Take(b - a).ToArray();
                    var replacement = SetPath(current, path, i + 1, nv);
                    if (replacement is not object?[] ra) throw Err("A slice of an array can only be assigned another array");
                    return arr.Take(a).Concat(ra).Concat(arr.Skip(b)).ToArray();
                }
            default:
                throw Err($"Invalid path component {JqInterp.Trunc(key)}");
        }
    }

    private static (int, int) Bounds(int length, object? from, object? to)
    {
        double a = from == null ? 0 : JqValue.ToDouble(from);
        double b = to == null ? length : JqValue.ToDouble(to);
        if (a < 0) a += length;
        if (b < 0) b += length;
        a = Math.Min(Math.Max(Math.Floor(a), 0), length);
        b = Math.Min(Math.Max(Math.Ceiling(b), 0), length);
        if (b < a) b = a;
        return ((int)a, (int)b);
    }

    public static object? DeletePaths(object? value, List<object?[]> paths)
    {
        var sorted = paths.Select(p => (object?)p).ToList();
        sorted.Sort((x, y) => JqValue.Compare(x, y));
        object? result = value;
        for (int i = sorted.Count - 1; i >= 0; i--) result = DeletePath(result, (object?[])sorted[i]!, 0);
        return result;
    }

    private static object? DeletePath(object? value, object?[] path, int i)
    {
        if (path.Length == 0) return null;   // del(.) leaves null
        if (i == path.Length) return value;
        if (value == null) return null;
        object? key = path[i];
        bool last = i == path.Length - 1;
        switch (value)
        {
            case IDictionary d:
                {
                    if (key is not string s) throw Err($"Cannot delete field at object index of {JqValue.TypeName(key)}");
                    if (!d.Contains(s)) return value;
                    var copy = JqValue.CopyObject(d);
                    if (last) copy.Remove(s);
                    else copy[s] = DeletePath(copy[s], path, i + 1);
                    return copy;
                }
            case object?[] arr:
                {
                    if (JqValue.IsNumber(key))
                    {
                        int idx = (int)Math.Floor(JqValue.ToDouble(key));
                        if (idx < 0) idx += arr.Length;
                        if (idx < 0 || idx >= arr.Length) return value;
                        if (last) return arr.Take(idx).Concat(arr.Skip(idx + 1)).ToArray();
                        var copy = (object?[])arr.Clone();
                        copy[idx] = DeletePath(copy[idx], path, i + 1);
                        return copy;
                    }
                    if (key is IDictionary slice)
                    {
                        var from = slice.Contains("start") ? slice["start"] : null;
                        var to = slice.Contains("end") ? slice["end"] : null;
                        var (a, b) = Bounds(arr.Length, from, to);
                        if (last) return arr.Take(a).Concat(arr.Skip(b)).ToArray();
                        var middle = DeletePath(arr.Skip(a).Take(b - a).ToArray(), path, i + 1);
                        return arr.Take(a).Concat((object?[])middle!).Concat(arr.Skip(b)).ToArray();
                    }
                    throw Err($"Cannot delete field at array index of {JqValue.TypeName(key)}");
                }
            default:
                throw Err($"Cannot delete field at {(key is string ? "object" : "array")} index of {JqValue.TypeName(value)}");
        }
    }
}
