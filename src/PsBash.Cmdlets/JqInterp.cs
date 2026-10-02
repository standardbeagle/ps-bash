using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>A <c>break $label</c> in flight; caught by the <c>label</c> that created it.</summary>
internal sealed class JqBreak : Exception
{
    public JqBreak(object id) { Id = id; }
    public object Id { get; }
}

/// <summary>Raised by <c>halt</c> / <c>halt_error</c>: stop the whole run with this exit status.</summary>
internal sealed class JqHalt : Exception
{
    public JqHalt(int code, string? text) { Code = code; Text = text; }
    public int Code { get; }
    public string? Text { get; }
}

/// <summary>A path through a value (<c>["a",0]</c>) as an immutable chain, shared between the values that extend it.</summary>
internal sealed class JPath
{
    public static readonly JPath Root = new(null, null, 0);
    /// <summary>The path of a value no path expression produced (a literal, an arithmetic result).</summary>
    public static readonly JPath Invalid = new(null, null, -1);

    private JPath(JPath? parent, object? key, int length) { Parent = parent; Key = key; Length = length; }
    public JPath? Parent { get; }
    public object? Key { get; }
    public int Length { get; }

    public JPath Append(object? key) => new(this, key, Length + 1);

    public object?[] ToArray()
    {
        var arr = new object?[Length];
        var p = this;
        for (int i = Length - 1; i >= 0; i--) { arr[i] = p!.Key; p = p.Parent; }
        return arr;
    }
}

/// <summary>A value together with its path while a path expression (<c>path(f)</c>, <c>|=</c>, <c>del</c>) is being evaluated; path is null otherwise.</summary>
internal readonly struct JV
{
    public readonly object? V;
    public readonly JPath? P;
    public JV(object? v, JPath? p) { V = v; P = p; }
    public JV Untracked => new(V, null);
}

/// <summary>The lexical environment: an immutable chain of variable, function, closure-parameter and label bindings.</summary>
internal sealed class JEnv
{
    public enum EntryKind { Var, Func, Closure, Label }

    public JEnv? Parent;
    public EntryKind Kind;
    public string Name = "";
    public int Arity;
    public object? Value;                 // Var: the value; Label: unique id
    public JFuncDef? Def;                 // Func
    public JEnv? DefEnv;                  // Func: environment the body closes over (itself, for recursion); Closure: the caller's environment
    public JNode? ClosureNode;            // Closure

    public JEnv WithVar(string name, object? value) => new() { Parent = this, Kind = EntryKind.Var, Name = name, Value = value };
    public JEnv WithLabel(string name, object id) => new() { Parent = this, Kind = EntryKind.Label, Name = name, Value = id };
    public JEnv WithClosure(string name, JNode node, JEnv callerEnv) => new() { Parent = this, Kind = EntryKind.Closure, Name = name, ClosureNode = node, DefEnv = callerEnv };

    public JEnv WithFunc(JFuncDef def)
    {
        var entry = new JEnv { Parent = this, Kind = EntryKind.Func, Name = def.Name, Arity = def.Params.Length, Def = def };
        entry.DefEnv = entry;   // the body sees its own name (recursion)
        return entry;
    }
}

internal delegate IEnumerable<JV> JqNative(JqInterp ip, JNode[] args, JEnv env, JV input);

/// <summary>
/// The jq evaluator: a generator-based tree walker (<see cref="IEnumerable{T}"/> per node, so backtracking is the iterator protocol).
/// Every node is evaluated with a <see cref="JV"/> input; when a path expression is being evaluated the value carries its
/// <see cref="JPath"/> and the path-capable nodes (identity, <c>..</c>, indexing, slicing, iteration, pipe, comma, <c>if</c>, <c>//</c>,
/// <c>try</c>, <c>getpath</c>, user functions and closures) extend it. Evaluation order matches jq: binary operators run the right
/// operand as the outer loop, object construction the first entry, string interpolation the last interpolation.
/// </summary>
internal sealed partial class JqInterp
{
    private readonly Func<(bool Ok, object? Value)> _nextInput;
    private readonly IDictionary<string, object?> _globalVars;
    private int _labelCounter;

    public JqInterp(Func<(bool Ok, object? Value)> nextInput, IDictionary<string, object?> globalVars)
    {
        _nextInput = nextInput;
        _globalVars = globalVars;
    }

    /// <summary>The next top-level input for <c>input</c>/<c>inputs</c> (false when exhausted).</summary>
    public (bool Ok, object? Value) NextInput() => _nextInput();

    /// <summary>The line count <c>input_line_number</c> reports (set by the command line as inputs are read).</summary>
    public int InputLine { get; set; }

    public string? CurrentFilename { get; set; }
    public Action<string>? StderrSink { get; set; }
    public Action<string>? DebugSink { get; set; }

    private static JqError Err(string message) => new(message);

    /// <summary>jq's error value dump: compact JSON cut to 11 characters plus "..." when longer than 14.</summary>
    internal static string Trunc(object? v)
    {
        string s = JqValue.ToCompactJson(v);
        return s.Length > 14 ? s.Substring(0, 11) + "..." : s;
    }

    internal static string Describe(object? v) => $"{JqValue.TypeName(v)} ({Trunc(v)})";

    // ───────────── entry points ─────────────

    public IEnumerable<object?> Run(JNode program, JEnv root, object? input)
    {
        foreach (var r in Eval(program, new JV(input, null), root)) yield return r.V;
    }

    private static JV Result(object? value, JV input) => new(value, input.P == null ? null : JPath.Invalid);

    // ───────────── evaluation ─────────────

    public IEnumerable<JV> Eval(JNode n, JV v, JEnv env)
    {
        switch (n)
        {
            case JIdentity: return Single(v);
            case JLiteral lit: return Single(Result(lit.Value, v));
            case JRecurseDefault: return RecurseDefault(v);
            case JIndex ix: return EvalIndex(ix, v, env);
            case JSlice sl: return EvalSlice(sl, v, env);
            case JIterate it: return EvalIterate(it, v, env);
            case JPipe pipe: return EvalPipe(pipe, v, env);
            case JComma comma: return EvalComma(comma, v, env);
            case JBinary bin: return EvalBinary(bin, v, env);
            case JAnd and: return EvalAnd(and, v, env);
            case JOr or: return EvalOr(or, v, env);
            case JAlt alt: return EvalAlt(alt, v, env);
            case JNeg neg: return EvalNeg(neg, v, env);
            case JIf iff: return EvalIf(iff, v, env);
            case JTry t: return EvalTry(t, v, env);
            case JCall call: return EvalCall(call, v, env);
            case JVar variable: return Single(Result(LookupVar(variable.Name, env, variable), v));
            case JArrayCons arr: return EvalArray(arr, v, env);
            case JObjectCons obj: return EvalObject(obj, v, env);
            case JString str: return EvalString(str, v, env);
            case JFormat fmt: return EvalFormat(fmt, v);
            case JAs @as: return EvalAs(@as, v, env);
            case JReduce red: return EvalReduce(red, v, env);
            case JForeach fe: return EvalForeach(fe, v, env);
            case JFuncDef def: return Eval(def.Rest, v, env.WithFunc(def));
            case JLabel label: return EvalLabel(label, v, env);
            case JBreak br: throw new JqBreak(LookupLabel(br.Name, env));
            case JAssign asg: return EvalAssign(asg, v, env);
            default: throw new JqCompileException($"unsupported jq construct {n.GetType().Name}");
        }
    }

    private static IEnumerable<JV> Single(JV v) { yield return v; }

    private IEnumerable<JV> RecurseDefault(JV v)
    {
        yield return v;
        switch (v.V)
        {
            case IDictionary d:
                foreach (var k in JqValue.Keys(d).ToList())
                    foreach (var r in RecurseDefault(new JV(d[k], v.P?.Append(k)))) yield return r;
                break;
            case object?[] a:
                for (int i = 0; i < a.Length; i++)
                    foreach (var r in RecurseDefault(new JV(a[i], v.P?.Append(JqNum(i))))) yield return r;
                break;
        }
    }

    private static object JqNum(double d) => new JqNumber(d, JqValue.FormatDouble(d));

    private IEnumerable<JV> EvalPipe(JPipe pipe, JV v, JEnv env)
    {
        foreach (var a in Eval(pipe.L, v, env))
            foreach (var b in Eval(pipe.R, a, env))
                yield return b;
    }

    private IEnumerable<JV> EvalComma(JComma comma, JV v, JEnv env)
    {
        foreach (var a in Eval(comma.L, v, env)) yield return a;
        foreach (var b in Eval(comma.R, v, env)) yield return b;
    }

    // ───────────── indexing ─────────────

    private IEnumerable<JV> EvalIndex(JIndex ix, JV v, JEnv env)
    {
        foreach (var k in Eval(ix.Key, v.Untracked, env))
            foreach (var t in Eval(ix.Target, v, env))
            {
                CheckPath(t, "index");
                yield return new JV(Index(t.V, k.V), PathAppend(t, k.V));
            }
    }

    private static JPath? PathAppend(JV t, object? key) => t.P == null ? null : t.P.Append(key);

    private static void CheckPath(JV t, string what)
    {
        if (t.P == JPath.Invalid) throw Err($"Invalid path expression with result {Trunc(t.V)}");
    }

    internal static object? Index(object? target, object? key)
    {
        switch (target)
        {
            case null when key is string or null || JqValue.IsNumber(key) || key is IDictionary:
                return null;
            case IDictionary d when key is string s:
                return d.Contains(s) ? d[s] : null;
            case object?[] a when JqValue.IsNumber(key):
                {
                    double dbl = JqValue.ToDouble(key);
                    if (double.IsNaN(dbl)) return null;
                    int i = (int)Math.Floor(dbl);
                    if (i < 0) i += a.Length;
                    return i >= 0 && i < a.Length ? a[i] : null;
                }
            case object?[] a when key is object?[] sub:
                return ArrayIndices(a, sub);
            case object?[] or string or null when key is IDictionary slice:
                {
                    var from = slice.Contains("start") ? slice["start"] : null;
                    var to = slice.Contains("end") ? slice["end"] : null;
                    return Slice(target, from, to);
                }
        }
        if (key is string ks) throw Err($"Cannot index {JqValue.TypeName(target)} with string \"{ks}\"");
        throw Err($"Cannot index {JqValue.TypeName(target)} with {JqValue.TypeName(key)}");
    }

    private static object ArrayIndices(object?[] a, object?[] sub)
    {
        var result = new List<object?>();
        if (sub.Length == 0) return Array.Empty<object?>();
        for (int i = 0; i + sub.Length <= a.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < sub.Length && match; j++) match = JqValue.Equal(a[i + j], sub[j]);
            if (match) result.Add(JqNum(i));
        }
        return result.ToArray();
    }

    private IEnumerable<JV> EvalSlice(JSlice sl, JV v, JEnv env)
    {
        IEnumerable<JV> Froms() => sl.From == null ? Single(new JV(null, null)) : Eval(sl.From, v.Untracked, env);
        IEnumerable<JV> Tos() => sl.To == null ? Single(new JV(null, null)) : Eval(sl.To, v.Untracked, env);
        foreach (var from in Froms())
            foreach (var to in Tos())
                foreach (var t in Eval(sl.Target, v, env))
                {
                    CheckPath(t, "slice");
                    var key = new OrderedDictionary { ["start"] = from.V, ["end"] = to.V };
                    yield return new JV(Slice(t.V, from.V, to.V), PathAppend(t, key));
                }
    }

    internal static object? Slice(object? target, object? from, object? to)
    {
        if (target == null) return null;
        if (!(from == null || JqValue.IsNumber(from)) || !(to == null || JqValue.IsNumber(to)))
            throw Err("Start and end indices of an array slice must be numbers");
        if (target is string s)
        {
            var cps = JqValue.CodePoints(s);
            var (a, b) = SliceBounds(cps.Length, from, to);
            return JqValue.FromCodePoints(cps.Skip(a).Take(b - a));
        }
        if (target is object?[] arr)
        {
            var (a, b) = SliceBounds(arr.Length, from, to);
            return arr.Skip(a).Take(b - a).ToArray();
        }
        throw Err($"Cannot index {JqValue.TypeName(target)} with object");
    }

    private static (int Start, int End) SliceBounds(int length, object? from, object? to)
    {
        double a = from == null ? 0 : JqValue.ToDouble(from);
        double b = to == null ? length : JqValue.ToDouble(to);
        if (a < 0) a += length;
        if (b < 0) b += length;
        a = Math.Floor(a);
        b = Math.Ceiling(b);
        a = Math.Min(Math.Max(a, 0), length);
        b = Math.Min(Math.Max(b, 0), length);
        if (b < a) b = a;
        return ((int)a, (int)b);
    }

    private IEnumerable<JV> EvalIterate(JIterate it, JV v, JEnv env)
    {
        foreach (var t in Eval(it.Target, v, env))
        {
            CheckPath(t, "iterate");
            switch (t.V)
            {
                case object?[] a:
                    for (int i = 0; i < a.Length; i++) yield return new JV(a[i], t.P?.Append(JqNum(i)));
                    break;
                case IDictionary d:
                    foreach (var k in JqValue.Keys(d).ToList()) yield return new JV(d[k], t.P?.Append(k));
                    break;
                case null: throw Err("Cannot iterate over null (null)");
                default: throw Err($"Cannot iterate over {Describe(t.V)}");
            }
        }
    }

    // ───────────── operators ─────────────

    private IEnumerable<JV> EvalNeg(JNeg neg, JV v, JEnv env)
    {
        foreach (var o in Eval(neg.Operand, v.Untracked, env))
        {
            if (!JqValue.IsNumber(o.V)) throw Err($"{Describe(o.V)} cannot be negated");
            yield return Result(-JqValue.ToDouble(o.V), v);
        }
    }

    private IEnumerable<JV> EvalBinary(JBinary bin, JV v, JEnv env)
    {
        foreach (var r in Eval(bin.R, v.Untracked, env))
            foreach (var l in Eval(bin.L, v.Untracked, env))
                yield return Result(Binary(bin.Op, l.V, r.V), v);
    }

    internal static object? Binary(string op, object? l, object? r)
    {
        switch (op)
        {
            case "+": return Add(l, r);
            case "-": return Subtract(l, r);
            case "*": return Multiply(l, r);
            case "/": return Divide(l, r);
            case "%": return Modulo(l, r);
            case "==": return JqValue.Equal(l, r);
            case "!=": return !JqValue.Equal(l, r);
            case "<": return JqValue.Compare(l, r) < 0;
            case "<=": return JqValue.Compare(l, r) <= 0;
            case ">": return JqValue.Compare(l, r) > 0;
            case ">=": return JqValue.Compare(l, r) >= 0;
        }
        throw new InvalidOperationException(op);
    }

    internal static object? Add(object? l, object? r)
    {
        if (l == null) return r;
        if (r == null) return l;
        if (JqValue.IsNumber(l) && JqValue.IsNumber(r)) return JqValue.ToDouble(l) + JqValue.ToDouble(r);
        if (l is string ls && r is string rs) return ls + rs;
        if (l is object?[] la && r is object?[] ra) return la.Concat(ra).ToArray();
        if (l is IDictionary ld && r is IDictionary rd)
        {
            var merged = JqValue.CopyObject(ld);
            foreach (DictionaryEntry e in rd) merged[e.Key] = e.Value;
            return merged;
        }
        throw Err($"{Describe(l)} and {Describe(r)} cannot be added");
    }

    private static object? Subtract(object? l, object? r)
    {
        if (JqValue.IsNumber(l) && JqValue.IsNumber(r)) return JqValue.ToDouble(l) - JqValue.ToDouble(r);
        if (l is object?[] la && r is object?[] ra) return la.Where(x => !ra.Any(y => JqValue.Equal(x, y))).ToArray();
        throw Err($"{Describe(l)} and {Describe(r)} cannot be subtracted");
    }

    private static object? Multiply(object? l, object? r)
    {
        if (JqValue.IsNumber(l) && JqValue.IsNumber(r)) return JqValue.ToDouble(l) * JqValue.ToDouble(r);
        if ((l is string && JqValue.IsNumber(r)) || (JqValue.IsNumber(l) && r is string))
        {
            string s = (l as string) ?? (string)r!;
            double n = JqValue.ToDouble(JqValue.IsNumber(l) ? l : r);
            // jq 1.7: a negative / NaN count is null; 0 and fractions below 1 give the empty string
            if (n < 0 || double.IsNaN(n)) return null;
            int times = n > int.MaxValue ? int.MaxValue : (int)n;
            var sb = new StringBuilder(s.Length * times);
            for (int i = 0; i < times; i++) sb.Append(s);
            return sb.ToString();
        }
        if (l is IDictionary ld && r is IDictionary rd) return DeepMerge(ld, rd);
        throw Err($"{Describe(l)} and {Describe(r)} cannot be multiplied");
    }

    private static IDictionary DeepMerge(IDictionary a, IDictionary b)
    {
        var result = JqValue.CopyObject(a);
        foreach (DictionaryEntry e in b)
        {
            if (result.Contains(e.Key) && result[e.Key] is IDictionary x && e.Value is IDictionary y) result[e.Key] = DeepMerge(x, y);
            else result[e.Key] = e.Value;
        }
        return result;
    }

    private static object? Divide(object? l, object? r)
    {
        if (JqValue.IsNumber(l) && JqValue.IsNumber(r))
        {
            double d = JqValue.ToDouble(r);
            if (d == 0) throw Err($"{Describe(l)} and {Describe(r)} cannot be divided because the divisor is zero");
            return JqValue.ToDouble(l) / d;
        }
        if (l is string a && r is string b) return JqNatives.SplitString(a, b);
        throw Err($"{Describe(l)} and {Describe(r)} cannot be divided");
    }

    private static object? Modulo(object? l, object? r)
    {
        if (JqValue.IsNumber(l) && JqValue.IsNumber(r))
        {
            double x = JqValue.ToDouble(l), y = JqValue.ToDouble(r);
            if (double.IsNaN(x) || double.IsNaN(y)) return double.NaN;
            long xi = ClampToLong(x), yi = ClampToLong(y);
            if (yi == 0) throw Err($"{Describe(l)} and {Describe(r)} cannot be divided (remainder) because the divisor is zero");
            if (yi == -1) return 0.0;
            return (double)(xi % Math.Abs(yi));
        }
        throw Err($"{Describe(l)} and {Describe(r)} cannot be divided");
    }

    private static long ClampToLong(double d) => d >= 9.2233720368547758E18 ? long.MaxValue : d <= -9.2233720368547758E18 ? long.MinValue : (long)d;

    private IEnumerable<JV> EvalAnd(JAnd and, JV v, JEnv env)
    {
        foreach (var l in Eval(and.L, v.Untracked, env))
        {
            if (!JqValue.IsTruthy(l.V)) { yield return Result(false, v); continue; }
            foreach (var r in Eval(and.R, v.Untracked, env)) yield return Result(JqValue.IsTruthy(r.V), v);
        }
    }

    private IEnumerable<JV> EvalOr(JOr or, JV v, JEnv env)
    {
        foreach (var l in Eval(or.L, v.Untracked, env))
        {
            if (JqValue.IsTruthy(l.V)) { yield return Result(true, v); continue; }
            foreach (var r in Eval(or.R, v.Untracked, env)) yield return Result(JqValue.IsTruthy(r.V), v);
        }
    }

    private IEnumerable<JV> EvalAlt(JAlt alt, JV v, JEnv env)
    {
        bool any = false;
        using (var e = Eval(alt.L, v, env).GetEnumerator())
        {
            while (true)
            {
                bool has;
                try { has = e.MoveNext(); }
                catch (JqError) { break; }      // errors on the left are swallowed
                if (!has) break;
                if (!JqValue.IsTruthy(e.Current.V)) continue;
                any = true;
                yield return e.Current;
            }
        }
        if (any) yield break;
        foreach (var r in Eval(alt.R, v, env)) yield return r;
    }

    private IEnumerable<JV> EvalIf(JIf iff, JV v, JEnv env)
    {
        foreach (var c in Eval(iff.Cond, v.Untracked, env))
        {
            if (JqValue.IsTruthy(c.V))
            {
                foreach (var r in Eval(iff.Then, v, env)) yield return r;
            }
            else if (iff.Else == null) yield return v;
            else foreach (var r in Eval(iff.Else, v, env)) yield return r;
        }
    }

    private IEnumerable<JV> EvalTry(JTry t, JV v, JEnv env)
    {
        using var e = Eval(t.Body, v, env).GetEnumerator();
        while (true)
        {
            bool has;
            JqError? error = null;
            try { has = e.MoveNext(); }
            catch (JqError ex) { error = ex; has = false; }
            if (error != null)
            {
                // `break` is not an error; an error whose value is the break sentinel passes through (not modelled: JqBreak is its own exception).
                if (t.Catch != null)
                {
                    foreach (var r in Eval(t.Catch, new JV(error.Value, v.P == null ? null : JPath.Invalid), env)) yield return r;
                }
                yield break;
            }
            if (!has) yield break;
            yield return e.Current;
        }
    }

    // ───────────── constructors ─────────────

    private IEnumerable<JV> EvalArray(JArrayCons arr, JV v, JEnv env)
    {
        if (arr.Body == null) { yield return Result(Array.Empty<object?>(), v); yield break; }
        var items = new List<object?>();
        foreach (var r in Eval(arr.Body, v.Untracked, env)) items.Add(r.V);
        yield return Result(items.ToArray(), v);
    }

    private IEnumerable<JV> EvalObject(JObjectCons obj, JV v, JEnv env)
    {
        foreach (var o in BuildObject(obj, 0, v.Untracked, env, new List<(string, object?)>()))
            yield return Result(o, v);
    }

    private IEnumerable<object?> BuildObject(JObjectCons obj, int index, JV v, JEnv env, List<(string Key, object? Value)> acc)
    {
        if (index == obj.Entries.Count)
        {
            var d = new OrderedDictionary();
            foreach (var (k, val) in acc) d[k] = val;
            yield return d;
            yield break;
        }
        var (keyNode, valueNode) = obj.Entries[index];
        foreach (var k in Eval(keyNode, v, env))
        {
            if (k.V is not string key)
                throw Err($"Cannot use {Describe(k.V)} as object key");
            foreach (var val in Eval(valueNode, v, env))
            {
                acc.Add((key, val.V));
                foreach (var o in BuildObject(obj, index + 1, v, env, acc)) yield return o;
                acc.RemoveAt(acc.Count - 1);
            }
        }
    }

    private IEnumerable<JV> EvalString(JString str, JV v, JEnv env)
    {
        // The LAST interpolation is the outermost loop (the first one varies fastest), as in jq.
        foreach (var s in BuildString(str, str.Parts.Count - 1, v.Untracked, env, new string[str.Parts.Count]))
            yield return Result(s, v);
    }

    private IEnumerable<string> BuildString(JString str, int index, JV v, JEnv env, string[] pieces)
    {
        if (index < 0)
        {
            yield return string.Concat(pieces);
            yield break;
        }
        if (str.Parts[index] is string literal)
        {
            pieces[index] = literal;
            foreach (var s in BuildString(str, index - 1, v, env, pieces)) yield return s;
            yield break;
        }
        var node = (JNode)str.Parts[index];
        foreach (var r in Eval(node, v, env))
        {
            pieces[index] = str.Format == null ? JqValue.ToText(r.V) : JqFormats.Apply(str.Format, r.V);
            foreach (var s in BuildString(str, index - 1, v, env, pieces)) yield return s;
        }
    }

    private IEnumerable<JV> EvalFormat(JFormat fmt, JV v)
    {
        yield return Result(JqFormats.Apply(fmt.Name, v.V), v);
    }

    // ───────────── variables, functions ─────────────

    private object? LookupVar(string name, JEnv env, JNode at)
    {
        for (var e = env; e != null; e = e.Parent)
            if (e.Kind == JEnv.EntryKind.Var && e.Name == name) return e.Value;
        if (name == "__loc__")
            return new OrderedDictionary { ["file"] = "<top-level>", ["line"] = JqNum(at.Line) };
        if (_globalVars.TryGetValue(name, out var global)) return global;
        throw new JqCompileException($"${name} is not defined");
    }

    private static object LookupLabel(string name, JEnv env)
    {
        for (var e = env; e != null; e = e.Parent)
            if (e.Kind == JEnv.EntryKind.Label && e.Name == name) return e.Value!;
        throw new JqCompileException($"$*label-{name} is not defined");
    }

    private IEnumerable<JV> EvalLabel(JLabel label, JV v, JEnv env)
    {
        object id = new object();
        var inner = env.WithLabel(label.Name, id);
        using var e = Eval(label.Body, v, inner).GetEnumerator();
        while (true)
        {
            bool has;
            try { has = e.MoveNext(); }
            catch (JqBreak b) when (ReferenceEquals(b.Id, id)) { yield break; }
            if (!has) yield break;
            yield return e.Current;
        }
    }

    private IEnumerable<JV> EvalCall(JCall call, JV v, JEnv env)
    {
        int arity = call.Args.Length;
        for (var e = env; e != null; e = e.Parent)
        {
            if (e.Name != call.Name) continue;
            if (e.Kind == JEnv.EntryKind.Closure && arity == 0)
                return Eval(e.ClosureNode!, v, e.DefEnv!);
            if (e.Kind == JEnv.EntryKind.Func && e.Arity == arity)
                return CallUser(e, call, v, env);
        }
        if (JqNatives.TryGet(call.Name, arity, out var native)) return native(this, call.Args, env, v);
        var prelude = JqPrelude.Lookup(call.Name, arity);
        if (prelude != null) return CallUser(prelude, call, v, env);
        throw new JqCompileException($"{call.Name}/{arity} is not defined");
    }

    private IEnumerable<JV> CallUser(JEnv fn, JCall call, JV v, JEnv callerEnv)
    {
        var def = fn.Def!;
        if (def.Params.Length == 0) return Eval(def.Body, v, fn.DefEnv!);
        return CallWithParams(fn, def, call, 0, v, callerEnv, fn.DefEnv!);
    }

    private IEnumerable<JV> CallWithParams(JEnv fn, JFuncDef def, JCall call, int i, JV v, JEnv callerEnv, JEnv bodyEnv)
    {
        if (i == def.Params.Length)
        {
            foreach (var r in Eval(def.Body, v, bodyEnv)) yield return r;
            yield break;
        }
        string p = def.Params[i];
        if (p[0] == '$')
        {
            string name = p.Substring(1);
            foreach (var arg in Eval(call.Args[i], v.Untracked, callerEnv))
            {
                var withVar = bodyEnv.WithVar(name, arg.V).WithClosure(name, new JLiteral(arg.V), bodyEnv);
                foreach (var r in CallWithParams(fn, def, call, i + 1, v, callerEnv, withVar)) yield return r;
            }
        }
        else
        {
            var withClosure = bodyEnv.WithClosure(p, call.Args[i], callerEnv);
            foreach (var r in CallWithParams(fn, def, call, i + 1, v, callerEnv, withClosure)) yield return r;
        }
    }

    // ───────────── reduce / foreach / as ─────────────

    private IEnumerable<JV> EvalReduce(JReduce red, JV v, JEnv env)
    {
        foreach (var init in Eval(red.Init, v.Untracked, env))
        {
            object? state = init.V;
            foreach (var item in Eval(red.Source, v.Untracked, env))
            {
                foreach (var bound in Destructure(red.Pattern, item.V, env))
                {
                    object? next = null;
                    bool any = false;
                    foreach (var u in Eval(red.Update, new JV(state, null), bound)) { next = u.V; any = true; }
                    state = any ? next : null;
                }
            }
            yield return Result(state, v);
        }
    }

    private IEnumerable<JV> EvalForeach(JForeach fe, JV v, JEnv env)
    {
        foreach (var init in Eval(fe.Init, v.Untracked, env))
        {
            object? state = init.V;
            foreach (var item in Eval(fe.Source, v.Untracked, env))
            {
                foreach (var bound in Destructure(fe.Pattern, item.V, env))
                {
                    foreach (var u in Eval(fe.Update, new JV(state, null), bound))
                    {
                        state = u.V;
                        if (fe.Extract == null) yield return Result(u.V, v);
                        else foreach (var x in Eval(fe.Extract, new JV(u.V, null), bound)) yield return Result(x.V, v);
                    }
                }
            }
        }
    }

    private IEnumerable<JV> EvalAs(JAs @as, JV v, JEnv env)
    {
        foreach (var src in Eval(@as.Source, v.Untracked, env))
        {
            if (@as.Patterns.Count == 1)
            {
                foreach (var bound in Destructure(@as.Patterns[0], src.V, env))
                    foreach (var r in Eval(@as.Body, v, bound)) yield return r;
                continue;
            }
            // `?//`: try the patterns in turn; an error while running the body moves on to the next pattern.
            var allVars = new List<string>();
            foreach (var p in @as.Patterns) CollectVars(p, allVars);
            for (int pi = 0; pi < @as.Patterns.Count; pi++)
            {
                bool last = pi == @as.Patterns.Count - 1;
                var baseEnv = env;
                foreach (var name in allVars) baseEnv = baseEnv.WithVar(name, null);
                bool failed = false;
                var results = new List<JV>();
                try
                {
                    foreach (var bound in Destructure(@as.Patterns[pi], src.V, baseEnv))
                        foreach (var r in Eval(@as.Body, v, bound)) results.Add(r);
                }
                catch (JqError) when (!last) { failed = true; }
                if (failed) continue;
                foreach (var r in results) yield return r;
                break;
            }
        }
    }

    private static void CollectVars(JPattern p, List<string> names)
    {
        switch (p)
        {
            case JPVar v: if (!names.Contains(v.Name)) names.Add(v.Name); break;
            case JPArray a: foreach (var e in a.Elements) CollectVars(e, names); break;
            case JPObject o:
                foreach (var e in o.Entries)
                {
                    if (e.Var != null && !names.Contains(e.Var)) names.Add(e.Var);
                    if (e.Sub != null) CollectVars(e.Sub, names);
                }
                break;
        }
    }

    private IEnumerable<JEnv> Destructure(JPattern pattern, object? value, JEnv env)
    {
        switch (pattern)
        {
            case JPVar v:
                yield return env.WithVar(v.Name, value);
                break;
            case JPArray arr:
                {
                    if (value != null && value is not object?[]) throw Err($"Cannot index {JqValue.TypeName(value)} with number");
                    foreach (var e in DestructureArray(arr, 0, value as object?[], env)) yield return e;
                    break;
                }
            case JPObject obj:
                foreach (var e in DestructureObject(obj, 0, value, env)) yield return e;
                break;
        }
    }

    private IEnumerable<JEnv> DestructureArray(JPArray arr, int i, object?[]? value, JEnv env)
    {
        if (i == arr.Elements.Count) { yield return env; yield break; }
        object? element = value != null && i < value.Length ? value[i] : null;
        foreach (var e in Destructure(arr.Elements[i], element, env))
            foreach (var rest in DestructureArray(arr, i + 1, value, e)) yield return rest;
    }

    private IEnumerable<JEnv> DestructureObject(JPObject obj, int i, object? value, JEnv env)
    {
        if (i == obj.Entries.Count) { yield return env; yield break; }
        var entry = obj.Entries[i];
        foreach (var k in Eval(entry.Key, new JV(value, null), env))
        {
            if (k.V is not string key) throw Err($"Cannot index {JqValue.TypeName(value)} with {JqValue.TypeName(k.V)}");
            object? element = Index(value, key);
            var e1 = entry.Var != null ? env.WithVar(entry.Var, element) : env;
            if (entry.Sub == null)
            {
                foreach (var rest in DestructureObject(obj, i + 1, value, e1)) yield return rest;
                continue;
            }
            foreach (var e2 in Destructure(entry.Sub, element, e1))
                foreach (var rest in DestructureObject(obj, i + 1, value, e2)) yield return rest;
        }
    }

    // ───────────── assignment ─────────────

    private IEnumerable<JV> EvalAssign(JAssign asg, JV v, JEnv env)
    {
        switch (asg.Op)
        {
            case "=":
                foreach (var rhs in Eval(asg.R, v.Untracked, env))
                {
                    object? result = v.V;
                    foreach (var path in Paths(asg.L, v.V, env)) result = JqPaths.SetPath(result, path, rhs.V);
                    yield return Result(result, v);
                }
                break;
            case "|=":
                yield return Result(Modify(asg.L, v.V, env, (old, _) => FirstOutput(asg.R, old, env)), v);
                break;
            default:
                {
                    string op = asg.Op.Substring(0, asg.Op.Length - 1);
                    foreach (var rhs in Eval(asg.R, v.Untracked, env))
                    {
                        object? rv = rhs.V;
                        yield return Result(Modify(asg.L, v.V, env, (old, _) =>
                        {
                            if (op == "//") return (true, JqValue.IsTruthy(old) ? old : rv);
                            return (true, Binary(op, old, rv));
                        }), v);
                    }
                    break;
                }
        }
    }

    private (bool Has, object? Value) FirstOutput(JNode f, object? input, JEnv env)
    {
        foreach (var r in Eval(f, new JV(input, null), env)) return (true, r.V);
        return (false, null);
    }

    /// <summary>All paths of <paramref name="expr"/> over <paramref name="input"/> (computed up front, as jq's <c>reduce path(...)</c> does).</summary>
    internal List<object?[]> Paths(JNode expr, object? input, JEnv env)
    {
        var list = new List<object?[]>();
        foreach (var r in Eval(expr, new JV(input, JPath.Root), env))
        {
            if (r.P == JPath.Invalid) throw Err($"Invalid path expression with result {Trunc(r.V)}");
            list.Add(r.P!.ToArray());
        }
        return list;
    }

    /// <summary>jq 1.7 <c>_modify</c>: apply <paramref name="update"/> at every path; a path whose update yields nothing is deleted (after all updates).</summary>
    internal object? Modify(JNode paths, object? input, JEnv env, Func<object?, object?, (bool Has, object? Value)> update)
    {
        object? result = input;
        var toDelete = new List<object?[]>();
        foreach (var path in Paths(paths, input, env))
        {
            object? old = JqPaths.GetPath(result, path);
            var (has, value) = update(old, null);
            if (has) result = JqPaths.SetPath(result, path, value);
            else toDelete.Add(path);
        }
        if (toDelete.Count > 0) result = JqPaths.DeletePaths(result, toDelete);
        return result;
    }
}
