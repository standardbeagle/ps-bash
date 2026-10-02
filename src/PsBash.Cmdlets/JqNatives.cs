using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>The built-in functions implemented in C#; the rest of jq's library is <see cref="JqPrelude"/> (jq source).</summary>
internal static class JqNatives
{
    private static readonly Dictionary<string, JqNative> Table = new(StringComparer.Ordinal);

    public static bool TryGet(string name, int arity, out JqNative fn) => Table.TryGetValue(name + "/" + arity, out fn!);

    public static IEnumerable<string> Names => Table.Keys;

    private static JqError Err(string m) => new(m);

    private static JV NV(object? value, JV input) => new(value, input.P == null ? null : JPath.Invalid);

    private static IEnumerable<object?> Vals(JqInterp ip, JNode arg, JEnv env, JV v) => ip.Eval(arg, v.Untracked, env).Select(r => r.V);

    private static void Reg(string name, int arity, JqNative f) => Table[name + "/" + arity] = f;

    private static void F0(string name, Func<object?, object?> f) =>
        Reg(name, 0, (ip, a, env, v) => Lazy(() => NV(f(v.V), v)));

    /// <summary>One result computed when the consumer first pulls it, so an error is raised where <c>try</c> can see it.</summary>
    private static IEnumerable<JV> Lazy(Func<JV> compute) { yield return compute(); }

    private static void F1(string name, Func<object?, object?, object?> f) =>
        Reg(name, 1, (ip, a, env, v) => Map1(ip, a, env, v, f));

    private static void F2(string name, Func<object?, object?, object?, object?> f) =>
        Reg(name, 2, (ip, a, env, v) => Map2(ip, a, env, v, f));

    private static IEnumerable<JV> Single(JV v) { yield return v; }

    private static IEnumerable<JV> Map1(JqInterp ip, JNode[] a, JEnv env, JV v, Func<object?, object?, object?> f)
    {
        foreach (var x in Vals(ip, a[0], env, v)) yield return NV(f(v.V, x), v);
    }

    private static IEnumerable<JV> Map2(JqInterp ip, JNode[] a, JEnv env, JV v, Func<object?, object?, object?, object?> f)
    {
        foreach (var x in Vals(ip, a[0], env, v))
            foreach (var y in Vals(ip, a[1], env, v))
                yield return NV(f(v.V, x, y), v);
    }

    private static object Num(double d) => d;

    private static double NumArg(object? v, string what)
    {
        if (!JqValue.IsNumber(v)) throw Err($"{JqInterp.Describe(v)} number required");
        return JqValue.ToDouble(v);
    }

    static JqNatives()
    {
        RegisterCore();
        RegisterStrings();
        RegisterCollections();
        RegisterPaths();
        RegisterMath();
        RegisterControl();
        RegisterRegex();
    }

    // ───────────── core ─────────────

    private static void RegisterCore()
    {
        Reg("empty", 0, (ip, a, env, v) => Enumerable.Empty<JV>());
        Reg("error", 0, (ip, a, env, v) => ErrorZero(v));
        Reg("error", 1, ErrorWith);
        F0("not", v => !JqValue.IsTruthy(v));
        F0("type", v => JqValue.TypeName(v));
        F0("length", Length);
        F0("utf8bytelength", v => v is string s ? Num(RawBytes.GetByteCount(s)) : throw Err($"{JqInterp.Describe(v)} only strings have UTF-8 byte length"));
        F0("tostring", v => JqValue.ToText(v));
        F0("tojson", v => JqValue.ToCompactJson(v));
        F0("fromjson", FromJson);
        F0("tonumber", ToNumber);
        F0("infinite", _ => double.PositiveInfinity);
        F0("nan", _ => double.NaN);
        F0("isinfinite", v => double.IsInfinity(NumArg(v, "isinfinite")));
        F0("isnan", v => double.IsNaN(NumArg(v, "isnan")));
        F0("isnormal", v => double.IsNormal(NumArg(v, "isnormal")));
        Reg("input_filename", 0, (ip, a, env, v) => Lazy(() => NV(ip.CurrentFilename, v)));
        Reg("input", 0, (ip, a, env, v) => InputOne(ip, v));
        Reg("inputs", 0, Inputs);
        Reg("debug", 0, (ip, a, env, v) => Debug(ip, v));
        Reg("stderr", 0, (ip, a, env, v) => StderrOut(ip, v));
        Reg("halt", 0, (ip, a, env, v) => Halt());
        Reg("halt_error", 0, (ip, a, env, v) => HaltError(v.V, 5));
        Reg("input_line_number", 0, (ip, a, env, v) => Lazy(() => NV(Num(ip.InputLine), v)));
        Reg("halt_error", 1, (ip, a, env, v) => Vals(ip, a[0], env, v).SelectMany(c =>
            JqValue.IsNumber(c) ? HaltError(v.V, (int)JqValue.ToDouble(c)) : throw Err("halt_error/1: number required")));
        Reg("builtins", 0, (ip, a, env, v) => Lazy(() => NV(Table.Keys.Concat(JqPrelude.Names).Where(n => !n.StartsWith('_')).Distinct().Select(n => (object?)n).ToArray(), v)));
        Reg("now", 0, (ip, a, env, v) => Lazy(() => NV(Num((DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds), v)));
    }

    // Iterators on purpose: jq raises these LAZILY, when the stream is pulled (`limit(0; error)` raises nothing).
#pragma warning disable CS0162 // the yield break only makes the method an iterator
    private static IEnumerable<JV> ErrorZero(JV v) { throw new JqError(v.V); yield break; }

    private static IEnumerable<JV> Halt() { throw new JqHalt(0, null); yield break; }
#pragma warning restore CS0162

    private static IEnumerable<JV> ErrorWith(JqInterp ip, JNode[] a, JEnv env, JV v)
    {
        foreach (var m in Vals(ip, a[0], env, v)) throw new JqError(m);
        yield break;
    }

    private static IEnumerable<JV> HaltError(object? value, int code)
    {
        throw new JqHalt(code, value is string s ? s : value == null ? null : JqValue.ToCompactJson(value) + "\n");
        #pragma warning disable CS0162
        yield break;
        #pragma warning restore CS0162
    }

    private static IEnumerable<JV> InputOne(JqInterp ip, JV v)
    {
        var (ok, value) = ip.NextInput();
        if (!ok) throw Err("break");   // jq 1.7 as shipped (oracle): the message of an exhausted `input` is "break"
        yield return NV(value, v);
    }

    private static IEnumerable<JV> Inputs(JqInterp ip, JNode[] a, JEnv env, JV v)
    {
        while (true)
        {
            var (ok, value) = ip.NextInput();
            if (!ok) yield break;
            yield return NV(value, v);
        }
    }

    private static IEnumerable<JV> Debug(JqInterp ip, JV v)
    {
        ip.DebugSink?.Invoke(JqValue.ToCompactJson(new object?[] { "DEBUG:", v.V }));
        yield return v;
    }

    private static IEnumerable<JV> StderrOut(JqInterp ip, JV v)
    {
        ip.StderrSink?.Invoke(v.V is string s ? s : JqValue.ToCompactJson(v.V));
        yield return v;
    }

    private static object? Length(object? v)
    {
        switch (v)
        {
            case null: return Num(0);
            case bool: throw Err($"{JqInterp.Describe(v)} has no length");
            case string s: return Num(JqValue.CodePoints(s).Length);
            case object?[] a: return Num(a.Length);
            case IDictionary d: return Num(d.Count);
        }
        if (JqValue.IsNumber(v)) return Math.Abs(JqValue.ToDouble(v));
        throw Err($"{JqInterp.Describe(v)} has no length");
    }

    private static object? ToNumber(object? v)
    {
        if (JqValue.IsNumber(v)) return v;
        if (v is string s)
        {
            object? parsed;
            try { parsed = JqJsonReader.ParseSingle(s); }
            catch (JqJsonException ex) { throw Err(ex.Message); }
            if (JqValue.IsNumber(parsed)) return parsed;
        }
        throw Err($"{JqInterp.Describe(v)} cannot be parsed as a number");
    }

    private static object? FromJson(object? v)
    {
        if (v is not string s) throw Err($"{JqInterp.Describe(v)} only strings can be parsed");
        try { return JqJsonReader.ParseSingle(s); }
        catch (JqJsonException ex) { throw Err(ex.Message); }
    }
    // ───────────── strings ─────────────

    internal static object?[] SplitString(string input, string sep)
    {
        if (input.Length == 0) return Array.Empty<object?>();
        if (sep.Length == 0) return JqValue.CodePoints(input).Select(cp => (object?)JqValue.FromCodePoints(new[] { cp })).ToArray();
        return input.Split(sep, StringSplitOptions.None).Select(x => (object?)x).ToArray();
    }

    private static void RegisterStrings()
    {
        F0("ascii_downcase", v => v is string s ? MapAscii(s, false) : throw Err("explode input must be a string"));
        F0("ascii_upcase", v => v is string s ? MapAscii(s, true) : throw Err("explode input must be a string"));
        F0("explode", v => v is string s ? JqValue.CodePoints(s).Select(cp => (object?)Num(cp)).ToArray() : throw Err("explode input must be a string"));
        F0("implode", Implode);
        F1("ltrimstr", (v, x) => v is string s && x is string p && s.StartsWith(p, StringComparison.Ordinal) ? s.Substring(p.Length) : v);
        F1("rtrimstr", (v, x) => v is string s && x is string p && s.EndsWith(p, StringComparison.Ordinal) && p.Length > 0 ? s.Substring(0, s.Length - p.Length) : v);
        F1("startswith", (v, x) => v is string s && x is string p ? s.StartsWith(p, StringComparison.Ordinal) : throw Err("startswith() requires string inputs"));
        F1("endswith", (v, x) => v is string s && x is string p ? s.EndsWith(p, StringComparison.Ordinal) : throw Err("endswith() requires string inputs"));
        F1("split", (v, x) => v is string s && x is string p ? SplitString(s, p) : throw Err("split input and separator must be strings"));
        F1("_strindices", (v, x) => v is string s && x is string p ? StringIndices(s, p) : throw Err("Cannot determine indices of non-strings"));
        Reg("join", 1, Join);
    }

    private static string MapAscii(string s, bool upper)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (upper && c >= 'a' && c <= 'z') sb.Append((char)(c - 32));
            else if (!upper && c >= 'A' && c <= 'Z') sb.Append((char)(c + 32));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static object? Implode(object? v)
    {
        if (v is not object?[] a) throw Err("implode input must be an array");
        var cps = new List<int>();
        foreach (var x in a)
        {
            if (!JqValue.IsNumber(x)) throw Err($"{JqInterp.Describe(x)} can't be imploded, unicode codepoint needs to be numeric");
            double d = JqValue.ToDouble(x);
            cps.Add(d < 0 || d > 0x10FFFF || double.IsNaN(d) ? 0xFFFD : (int)d);
        }
        return JqValue.FromCodePoints(cps);
    }

    private static object StringIndices(string s, string needle)
    {
        var result = new List<object?>();
        if (needle.Length == 0) return Array.Empty<object?>();
        int from = 0;
        while (true)
        {
            int idx = s.IndexOf(needle, from, StringComparison.Ordinal);
            if (idx < 0) break;
            int cp = 0;
            for (int i = 0; i < idx; i++) { if (char.IsHighSurrogate(s[i])) i++; cp++; }
            result.Add(Num(cp));
            from = idx + 1;
        }
        return result.ToArray();
    }

    private static IEnumerable<JV> Join(JqInterp ip, JNode[] a, JEnv env, JV v)
    {
        foreach (var sep in Vals(ip, a[0], env, v))
        {
            // jq 1.7: reduce .[] as $i (null; (if . == null then "" else . + $x end) + ($i | if null then "" elif number/bool then tojson else . end)) // ""
            // — an array or object element is added as is, which is an error.
            object? acc = null;
            foreach (var item in IterateValues(v.V))
            {
                object? piece = item == null ? "" : (item is bool || JqValue.IsNumber(item)) ? JqValue.ToCompactJson(item) : item;
                object? left = acc == null ? "" : JqInterp.Add(acc, sep);
                acc = JqInterp.Add(left, piece);
            }
            yield return NV(JqValue.IsTruthy(acc) ? acc : "", v);
        }
    }

    /// <summary>The values <c>.[]</c> would give (error for a non-iterable).</summary>
    internal static IEnumerable<object?> IterateValues(object? v)
    {
        switch (v)
        {
            case object?[] a: return a;
            case IDictionary d: return JqValue.Keys(d).ToList().Select(k => d[k]);
            default: throw Err($"Cannot iterate over {JqInterp.Describe(v)}");
        }
    }

    // ───────────── collections ─────────────

    private static void RegisterCollections()
    {
        F0("keys", v => Keys(v, sorted: true));
        F0("keys_unsorted", v => Keys(v, sorted: false));
        F1("has", Has);
        F1("contains", (v, x) => Contains(v, x));
        F0("add", v => Add(v));
        Reg("add", 1, (ip, a, env, v) => AddOf(ip, a, env, v));
        F0("sort", v => v is object?[] arr ? SortStable(arr) : throw Err($"{JqInterp.Describe(v)} cannot be sorted, as it is not an array"));
        F0("unique", v => v is object?[] arr ? Unique(arr) : throw Err($"{JqInterp.Describe(v)} cannot be sorted, as it is not an array"));
        F0("min", v => MinMax(v, max: false));
        F0("max", v => MinMax(v, max: true));
        Reg("sort_by", 1, (ip, a, env, v) => ByKey(ip, a, env, v, "sort"));
        Reg("group_by", 1, (ip, a, env, v) => ByKey(ip, a, env, v, "group"));
        Reg("unique_by", 1, (ip, a, env, v) => ByKey(ip, a, env, v, "unique"));
        Reg("min_by", 1, (ip, a, env, v) => ByKey(ip, a, env, v, "min"));
        Reg("max_by", 1, (ip, a, env, v) => ByKey(ip, a, env, v, "max"));
    }

    private static object? Keys(object? v, bool sorted)
    {
        switch (v)
        {
            case IDictionary d:
                {
                    var keys = JqValue.Keys(d).ToList();
                    if (sorted) keys.Sort(JqValue.CompareStrings);
                    return keys.Select(k => (object?)k).ToArray();
                }
            case object?[] a: return Enumerable.Range(0, a.Length).Select(i => (object?)Num(i)).ToArray();
        }
        throw Err($"{JqInterp.Describe(v)} has no keys");
    }

    private static object? Has(object? v, object? key)
    {
        if (v is IDictionary d && key is string s) return d.Contains(s);
        if (v is object?[] a && JqValue.IsNumber(key)) { double i = JqValue.ToDouble(key); return i >= 0 && i < a.Length; }
        throw Err($"Cannot check whether {JqValue.TypeName(v)} has a {JqValue.TypeName(key)} key");
    }

    private static bool Contains(object? a, object? b)
    {
        if (a is IDictionary da && b is IDictionary db)
        {
            foreach (DictionaryEntry e in db)
            {
                if (!da.Contains(e.Key) || !Contains(da[e.Key], e.Value)) return false;
            }
            return true;
        }
        if (a is object?[] aa && b is object?[] ba) return ba.All(y => aa.Any(x => Contains(x, y)));
        if (a is string sa && b is string sb) return sa.Contains(sb, StringComparison.Ordinal);
        if (JqValue.TypeName(a) == JqValue.TypeName(b)) return JqValue.Equal(a, b);
        throw Err($"{JqInterp.Describe(a)} and {JqInterp.Describe(b)} cannot have their containment checked");
    }

    private static object? Add(object? v)
    {
        if (v == null) return null;
        object? acc = null;
        foreach (var x in IterateValues(v)) acc = JqInterp.Add(acc, x);
        return acc;
    }

    private static IEnumerable<JV> AddOf(JqInterp ip, JNode[] a, JEnv env, JV v)
    {
        object? acc = null;
        foreach (var x in ip.Eval(a[0], v.Untracked, env)) acc = JqInterp.Add(acc, x.V);
        yield return NV(acc, v);
    }

    internal static object?[] SortStable(object?[] arr) =>
        arr.Select((x, i) => (x, i)).OrderBy(p => p.x, Comparer<object?>.Create(JqValue.Compare)).ThenBy(p => p.i).Select(p => p.x).ToArray();

    private static object?[] Unique(object?[] arr)
    {
        var sorted = SortStable(arr);
        var result = new List<object?>();
        foreach (var x in sorted)
            if (result.Count == 0 || !JqValue.Equal(result[^1], x)) result.Add(x);
        return result.ToArray();
    }

    private static object? MinMax(object? v, bool max)
    {
        if (v is not object?[] a) throw Err($"{JqInterp.Describe(v)} cannot be iterated over");
        if (a.Length == 0) return null;
        object? best = a[0];
        for (int i = 1; i < a.Length; i++)
        {
            int c = JqValue.Compare(a[i], best);
            if (max ? c >= 0 : c < 0) best = a[i];
        }
        return best;
    }

    private static IEnumerable<JV> ByKey(JqInterp ip, JNode[] a, JEnv env, JV v, string mode)
    {
        IEnumerable<object?> elements;
        try { elements = IterateValues(v.V); }
        catch (JqError) { throw; }
        var items = new List<(object? Item, object?[] Key)>();
        foreach (var e in elements)
        {
            var key = ip.Eval(a[0], new JV(e, null), env).Select(r => r.V).ToArray();
            items.Add((e, key));
        }
        if (v.V is not object?[])
        {
            var keysArray = items.Select(i => (object?)i.Key).ToArray();
            throw Err($"{JqInterp.Describe(v.V)} and {JqInterp.Describe(keysArray)} cannot be sorted, as they are not both arrays");
        }

        var cmp = Comparer<object?>.Create(JqValue.Compare);
        switch (mode)
        {
            case "sort":
                yield return NV(items.Select((x, i) => (x, i)).OrderBy(p => (object?)p.x.Key, cmp).ThenBy(p => p.i).Select(p => p.x.Item).ToArray(), v);
                break;
            case "group":
            case "unique":
                {
                    var sorted = items.Select((x, i) => (x, i)).OrderBy(p => (object?)p.x.Key, cmp).ThenBy(p => p.i).Select(p => p.x).ToList();
                    var groups = new List<List<object?>>();
                    object?[]? lastKey = null;
                    foreach (var (item, key) in sorted)
                    {
                        if (lastKey == null || !JqValue.Equal(lastKey, key)) groups.Add(new List<object?>());
                        groups[^1].Add(item);
                        lastKey = key;
                    }
                    if (mode == "group") yield return NV(groups.Select(g => (object?)g.ToArray()).ToArray(), v);
                    else yield return NV(groups.Select(g => g[0]).ToArray(), v);
                    break;
                }
            case "min":
            case "max":
                {
                    if (items.Count == 0) { yield return NV(null, v); break; }
                    var best = items[0];
                    for (int i = 1; i < items.Count; i++)
                    {
                        int c = JqValue.Compare(items[i].Key, best.Key);
                        if (mode == "max" ? c >= 0 : c < 0) best = items[i];
                    }
                    yield return NV(best.Item, v);
                    break;
                }
        }
    }

    // ───────────── paths ─────────────

    private static void RegisterPaths()
    {
        Reg("path", 1, PathOf);
        Reg("getpath", 1, GetPath);
        Reg("setpath", 2, SetPath);
        Reg("delpaths", 1, DelPaths);
        Reg("del", 1, (ip, a, env, v) => Lazy(() => NV(JqPaths.DeletePaths(v.V, ip.Paths(a[0], v.V, env)), v)));
        Reg("to_entries", 0, (ip, a, env, v) => Lazy(() => NV(ToEntries(v.V), v)));
    }

    private static IEnumerable<JV> PathOf(JqInterp ip, JNode[] a, JEnv env, JV v)
    {
        foreach (var r in ip.Eval(a[0], new JV(v.V, JPath.Root), env))
        {
            if (r.P == JPath.Invalid) throw Err($"Invalid path expression with result {JqInterp.Trunc(r.V)}");
            yield return NV(r.P!.ToArray(), v);
        }
    }

    private static IEnumerable<JV> GetPath(JqInterp ip, JNode[] a, JEnv env, JV v)
    {
        foreach (var p in Vals(ip, a[0], env, v))
        {
            if (p is not object?[] path) throw Err("Path must be specified as an array");
            object? result;
            result = JqPaths.GetPath(v.V, path);
            JPath? np = v.P;
            if (np != null && np != JPath.Invalid) foreach (var k in path) np = np.Append(k);
            yield return new JV(result, v.P == null ? null : (v.P == JPath.Invalid ? JPath.Invalid : np));
        }
    }

    private static IEnumerable<JV> SetPath(JqInterp ip, JNode[] a, JEnv env, JV v)
    {
        foreach (var p in Vals(ip, a[0], env, v))
            foreach (var nv in Vals(ip, a[1], env, v))
            {
                if (p is not object?[] path) throw Err("Path must be specified as an array");
                yield return NV(JqPaths.SetPath(v.V, path, nv), v);
            }
    }

    private static IEnumerable<JV> DelPaths(JqInterp ip, JNode[] a, JEnv env, JV v)
    {
        foreach (var ps in Vals(ip, a[0], env, v))
        {
            if (ps is not object?[] list) throw Err("Paths must be specified as an array");
            var paths = new List<object?[]>();
            foreach (var p in list)
            {
                if (p is not object?[] path) throw Err("Path must be specified as an array");
                paths.Add(path);
            }
            yield return NV(JqPaths.DeletePaths(v.V, paths), v);
        }
    }

    private static object? ToEntries(object? v)
    {
        switch (v)
        {
            case IDictionary d:
                return JqValue.Keys(d).ToList().Select(k => (object?)new OrderedDictionary { ["key"] = k, ["value"] = d[k] }).ToArray();
            case object?[] a:
                return a.Select((x, i) => (object?)new OrderedDictionary { ["key"] = Num(i), ["value"] = x }).ToArray();
        }
        throw Err($"{JqInterp.Describe(v)} has no keys");
    }

    // ───────────── math ─────────────

    private static void Math1(string name, Func<double, double> f) =>
        F0(name, v => Num(f(NumArg(v, name))));

    private static void RegisterMath()
    {
        Math1("floor", Math.Floor);
        Math1("ceil", Math.Ceiling);
        Math1("round", d => Math.Round(d, MidpointRounding.AwayFromZero));
        Math1("trunc", Math.Truncate);
        Math1("rint", d => Math.Round(d, MidpointRounding.ToEven));
        Math1("nearbyint", d => Math.Round(d, MidpointRounding.ToEven));
        Math1("sqrt", Math.Sqrt);
        Math1("cbrt", Math.Cbrt);
        Math1("fabs", Math.Abs);
        Math1("exp", Math.Exp);
        Math1("exp2", d => Math.Pow(2, d));
        Math1("exp10", d => Math.Pow(10, d));
        Math1("log", Math.Log);
        Math1("log2", Math.Log2);
        Math1("log10", Math.Log10);
        Math1("sin", Math.Sin);
        Math1("cos", Math.Cos);
        Math1("tan", Math.Tan);
        Math1("asin", Math.Asin);
        Math1("acos", Math.Acos);
        Math1("atan", Math.Atan);
        Math1("sinh", Math.Sinh);
        Math1("cosh", Math.Cosh);
        Math1("tanh", Math.Tanh);
        Math1("asinh", Math.Asinh);
        Math1("acosh", Math.Acosh);
        Math1("atanh", Math.Atanh);
        F2("pow", (v, a, b) => Num(Math.Pow(NumArg(a, "pow"), NumArg(b, "pow"))));
        F2("atan2", (v, a, b) => Num(Math.Atan2(NumArg(a, "atan2"), NumArg(b, "atan2"))));
        F2("fmin", (v, a, b) => Num(Math.Min(NumArg(a, "fmin"), NumArg(b, "fmin"))));
        F2("fmax", (v, a, b) => Num(Math.Max(NumArg(a, "fmax"), NumArg(b, "fmax"))));
        F2("copysign", (v, a, b) => Num(Math.CopySign(NumArg(a, "copysign"), NumArg(b, "copysign"))));
        F2("ldexp", (v, a, b) => Num(NumArg(a, "ldexp") * Math.Pow(2, NumArg(b, "ldexp"))));
        Reg("range", 2, Range2);
        Reg("fma", 3, (ip, a, env, v) => Vals(ip, a[0], env, v).SelectMany(x => Vals(ip, a[1], env, v).SelectMany(y => Vals(ip, a[2], env, v).Select(z =>
            NV(Num(NumArg(x, "fma") * NumArg(y, "fma") + NumArg(z, "fma")), v)))));
    }

    private static IEnumerable<JV> Range2(JqInterp ip, JNode[] a, JEnv env, JV v)
    {
        foreach (var from in Vals(ip, a[0], env, v))
            foreach (var to in Vals(ip, a[1], env, v))
            {
                if (!JqValue.IsNumber(from) || !JqValue.IsNumber(to)) throw Err("Range bounds must be numeric");
                double end = JqValue.ToDouble(to);
                for (double x = JqValue.ToDouble(from); x < end; x += 1) yield return NV(Num(x), v);
            }
    }

    // ───────────── control ─────────────

    private static void RegisterControl()
    {
        Reg("limit", 2, Limit);
        Reg("nth", 2, Nth);
        Reg("repeat", 1, Repeat);
        Reg("first", 1, First);
        Reg("last", 1, Last);
        Reg("isempty", 1, IsEmpty);
    }

    private static IEnumerable<JV> Limit(JqInterp ip, JNode[] a, JEnv env, JV v)
    {
        foreach (var n in Vals(ip, a[0], env, v))
        {
            if (!JqValue.IsNumber(n)) throw Err("Invalid limit: number required");
            double count = JqValue.ToDouble(n);
            if (count == 0) continue;
            double remaining = count;
            foreach (var r in ip.Eval(a[1], v, env))
            {
                yield return r;
                if (count > 0 && --remaining <= 0) break;
            }
        }
    }

    private static IEnumerable<JV> Nth(JqInterp ip, JNode[] a, JEnv env, JV v)
    {
        foreach (var n in Vals(ip, a[0], env, v))
        {
            if (!JqValue.IsNumber(n)) throw Err("Invalid nth: number required");
            double skip = JqValue.ToDouble(n);
            if (skip < 0) throw Err("nth doesn't support negative indices");
            double seen = 0;
            foreach (var r in ip.Eval(a[1], v, env))
            {
                if (seen++ < skip) continue;
                yield return r;
                break;
            }
        }
    }

    /// <summary>jq 1.7 as shipped (oracle): <c>repeat(f)</c> yields <c>f</c> applied to the ORIGINAL input, again and again (not the
    /// documented <c>., f, f|f</c>): <c>1|[limit(4;repeat(.*2))]</c> is <c>[2,2,2,2]</c>.</summary>
    private static IEnumerable<JV> Repeat(JqInterp ip, JNode[] a, JEnv env, JV v)
    {
        while (true)
        {
            bool any = false;
            foreach (var r in ip.Eval(a[0], v, env)) { any = true; yield return r; }
            if (!any) yield break;
        }
    }

    private static IEnumerable<JV> First(JqInterp ip, JNode[] a, JEnv env, JV v)
    {
        foreach (var r in ip.Eval(a[0], v, env))
        {
            yield return r;
            yield break;
        }
    }

    private static IEnumerable<JV> Last(JqInterp ip, JNode[] a, JEnv env, JV v)
    {
        bool any = false;
        JV last = default;
        foreach (var r in ip.Eval(a[0], v, env)) { last = r; any = true; }
        yield return any ? last : NV(null, v);
    }

    private static IEnumerable<JV> IsEmpty(JqInterp ip, JNode[] a, JEnv env, JV v)
    {
        foreach (var _ in ip.Eval(a[0], v.Untracked, env)) { yield return NV(false, v); yield break; }
        yield return NV(true, v);
    }

    // ───────────── regex ─────────────

    private static IEnumerable<(object? Re, object? Flags)> RegexArgs(JqInterp ip, JNode[] a, JEnv env, JV v)
    {
        if (a.Length == 1)
        {
            foreach (var x in Vals(ip, a[0], env, v))
            {
                if (x is string) yield return (x, null);
                else if (x is object?[] arr && arr.Length > 1) yield return (arr[0], arr[1]);
                else if (x is object?[] arr1 && arr1.Length > 0) yield return (arr1[0], null);
                else throw Err($"{JqValue.TypeName(x)} not a string or array");
            }
            yield break;
        }
        foreach (var re in Vals(ip, a[0], env, v))
            foreach (var flags in Vals(ip, a[1], env, v))
                yield return (re, flags);
    }

    private static string InputString(object? v)
    {
        if (v is string s) return s;
        throw Err($"{JqInterp.Describe(v)} cannot be matched, as it is not a string");
    }

    private static void RegisterRegex()
    {
        foreach (int arity in new[] { 1, 2 })
        {
            Reg("test", arity, (ip, a, env, v) => RegexArgs(ip, a, env, v).Select(p =>
                NV(JqRegex.Get(p.Re, p.Flags).IsMatch(InputString(v.V)) is var r ? r : false, v)));
            Reg("match", arity, (ip, a, env, v) => RegexArgs(ip, a, env, v).SelectMany(p =>
                JqRegex.Get(p.Re, p.Flags).MatchObjects(InputString(v.V)).Select(m => NV(m, v))));
            Reg("capture", arity, (ip, a, env, v) => RegexArgs(ip, a, env, v).SelectMany(p =>
            {
                var rx = JqRegex.Get(p.Re, p.Flags);
                return rx.MatchObjects(InputString(v.V)).Select(m => NV(rx.CaptureObject(m), v));
            }));
            Reg("scan", arity, (ip, a, env, v) => RegexArgs(ip, a, env, v).SelectMany(p =>
            {
                var rx = JqRegex.Get(p.Re, WithGlobal(p.Flags));
                return rx.MatchObjects(InputString(v.V)).Select(m =>
                {
                    var caps = (object?[])m["captures"]!;
                    return NV(caps.Length > 0 ? caps.Select(c => ((OrderedDictionary)c!)["string"]).ToArray() : m["string"], v);
                });
            }));
            Reg("splits", arity, (ip, a, env, v) => RegexArgs(ip, a, env, v).SelectMany(p =>
                RegexSplit(InputString(v.V), JqRegex.Get(p.Re, WithGlobal(p.Flags))).Select(s => NV(s, v))));
        }
        Reg("split", 2, (ip, a, env, v) => RegexArgs(ip, a, env, v).Select(p =>
            NV(RegexSplit(InputString(v.V), JqRegex.Get(p.Re, WithGlobal(p.Flags))).Select(s => (object?)s).ToArray(), v)));
        Reg("sub", 2, (ip, a, env, v) => Sub(ip, a, env, v, global: false));
        Reg("sub", 3, (ip, a, env, v) => Sub(ip, a, env, v, global: false));
        Reg("gsub", 2, (ip, a, env, v) => Sub(ip, a, env, v, global: true));
        Reg("gsub", 3, (ip, a, env, v) => Sub(ip, a, env, v, global: true));
    }

    private static string? WithGlobal(object? flags) => flags is string f ? "g" + f : flags == null ? "g" : throw Err($"{JqInterp.Describe(flags)} is not a string");

    private static IEnumerable<string> RegexSplit(string input, JqRegex rx)
    {
        int prev = 0;
        foreach (var m in rx.RawMatches(input))
        {
            yield return input.Substring(prev, m.Index - prev);
            prev = m.Index + m.Length;
        }
        yield return input.Substring(prev);
    }

    /// <summary>
    /// jq 1.7 <c>sub</c>/<c>gsub</c>: <c>str</c> runs once per match with the named-capture object as input; when it yields several
    /// values the K-th result uses every match's K-th value (a match with fewer values contributes nothing, gap included); a
    /// program with no match, or one whose <c>str</c> never yields, returns the input unchanged.
    /// </summary>
    private static IEnumerable<JV> Sub(JqInterp ip, JNode[] a, JEnv env, JV v, bool global)
    {
        IEnumerable<object?> flagValues = a.Length == 3 ? Vals(ip, a[2], env, v) : new object?[] { null };
        foreach (var re in Vals(ip, a[0], env, v))
            foreach (var flags in flagValues)
            {
                string input = InputString(v.V);
                object? effective = global ? (flags is string f ? f + "g" : flags == null ? "g" : flags) : flags;
                var rx = JqRegex.Get(re, effective);
                var matches = rx.RawMatches(input).ToList();
                if (matches.Count == 0) { yield return NV(input, v); continue; }

                var outputs = new List<List<string>>();
                foreach (var m in matches)
                {
                    var obj = rx.ToObject(input, m);
                    var capture = rx.CaptureObject(obj);
                    var list = new List<string>();
                    foreach (var r in ip.Eval(a[1], new JV(capture, null), env))
                    {
                        if (r.V is not string rs) throw Err($"{JqInterp.Describe(capture)} and {JqInterp.Describe(r.V)} cannot be added");
                        list.Add(rs);
                    }
                    outputs.Add(list);
                }
                int k = outputs.Max(o => o.Count);
                if (k == 0) { yield return NV(input, v); continue; }
                for (int j = 0; j < k; j++)
                {
                    var sb = new StringBuilder();
                    int prev = 0;
                    for (int i = 0; i < matches.Count; i++)
                    {
                        var m = matches[i];
                        if (j < outputs[i].Count) sb.Append(input, prev, m.Index - prev).Append(outputs[i][j]);
                        prev = m.Index + m.Length;
                    }
                    sb.Append(input, prev, input.Length - prev);
                    yield return NV(sb.ToString(), v);
                }
            }
    }
}
