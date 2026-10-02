namespace PsBash.Cmdlets;

/// <summary>
/// jq's compile-time name resolution: an undefined function (<c>foo/1 is not defined</c>), variable (<c>$x is not defined</c>) or
/// <c>break</c> label is a compile error (exit 3) reported before any input is read, never in the middle of a run. Walks the AST
/// with the function / variable / label scope the evaluator will have.
/// </summary>
internal sealed class JqChecker
{
    private readonly List<(string Message, int Line)> _errors = new();
    private readonly HashSet<string> _globalVars;

    private JqChecker(IEnumerable<string> globalVars) { _globalVars = new HashSet<string>(globalVars, StringComparer.Ordinal) { "ENV", "__loc__" }; }

    private sealed class Scope
    {
        public Scope? Parent;
        public string? Func;          // "name/arity"
        public string? Var;
        public string? Label;
        public Scope With(string? func = null, string? v = null, string? label = null) => new() { Parent = this, Func = func, Var = v, Label = label };
        public bool HasFunc(string key) { for (var s = this; s != null; s = s.Parent) if (s.Func == key) return true; return false; }
        public bool HasVar(string name) { for (var s = this; s != null; s = s.Parent) if (s.Var == name) return true; return false; }
        public bool HasLabel(string name) { for (var s = this; s != null; s = s.Parent) if (s.Label == name) return true; return false; }
    }

    /// <summary>Returns the compile errors of <paramref name="program"/> (empty when it resolves).</summary>
    public static List<(string Message, int Line)> Check(JNode program, IEnumerable<string> globalVars)
    {
        var checker = new JqChecker(globalVars);
        checker.Walk(program, new Scope());
        return checker._errors;
    }

    private void Error(string message, JNode at)
    {
        if (!_errors.Any(e => e.Message == message)) _errors.Add((message, at.Line));
    }

    private void Walk(JNode? n, Scope s)
    {
        switch (n)
        {
            case null: return;
            case JIdentity or JRecurseDefault or JLiteral: return;
            case JFormat f:
                return;
            case JString str:
                foreach (var part in str.Parts) if (part is JNode pn) Walk(pn, s);
                return;
            case JIndex ix: Walk(ix.Target, s); Walk(ix.Key, s); return;
            case JSlice sl: Walk(sl.Target, s); Walk(sl.From, s); Walk(sl.To, s); return;
            case JIterate it: Walk(it.Target, s); return;
            case JTry t: Walk(t.Body, s); Walk(t.Catch, s); return;
            case JNeg neg: Walk(neg.Operand, s); return;
            case JPipe p: Walk(p.L, s); Walk(p.R, s); return;
            case JComma c: Walk(c.L, s); Walk(c.R, s); return;
            case JBinary b: Walk(b.L, s); Walk(b.R, s); return;
            case JAnd a: Walk(a.L, s); Walk(a.R, s); return;
            case JOr o: Walk(o.L, s); Walk(o.R, s); return;
            case JAlt al: Walk(al.L, s); Walk(al.R, s); return;
            case JAssign asg: Walk(asg.L, s); Walk(asg.R, s); return;
            case JIf i: Walk(i.Cond, s); Walk(i.Then, s); Walk(i.Else, s); return;
            case JArrayCons arr: Walk(arr.Body, s); return;
            case JObjectCons obj:
                foreach (var (k, v) in obj.Entries) { Walk(k, s); Walk(v, s); }
                return;
            case JVar v:
                if (!s.HasVar(v.Name) && !_globalVars.Contains(v.Name)) Error($"${v.Name} is not defined", v);
                return;
            case JBreak br:
                if (!s.HasLabel(br.Name)) Error($"$*label-{br.Name} is not defined", br);
                return;
            case JLabel label: Walk(label.Body, s.With(label: label.Name)); return;
            case JAs @as:
                {
                    Walk(@as.Source, s);
                    var inner = s;
                    var names = new List<string>();
                    foreach (var p in @as.Patterns) CollectPattern(p, names, s);
                    foreach (var name in names) inner = inner.With(v: name);
                    Walk(@as.Body, inner);
                    return;
                }
            case JReduce red:
                {
                    Walk(red.Source, s);
                    Walk(red.Init, s);
                    var inner = BindPattern(red.Pattern, s);
                    Walk(red.Update, inner);
                    return;
                }
            case JForeach fe:
                {
                    Walk(fe.Source, s);
                    Walk(fe.Init, s);
                    var inner = BindPattern(fe.Pattern, s);
                    Walk(fe.Update, inner);
                    Walk(fe.Extract, inner);
                    return;
                }
            case JFuncDef def:
                {
                    var key = def.Name + "/" + def.Params.Length;
                    var bodyScope = s.With(func: key);
                    foreach (var p in def.Params)
                    {
                        if (p[0] == '$') bodyScope = bodyScope.With(v: p.Substring(1)).With(func: p.Substring(1) + "/0");
                        else bodyScope = bodyScope.With(func: p + "/0");
                    }
                    Walk(def.Body, bodyScope);
                    Walk(def.Rest, s.With(func: key));
                    return;
                }
            case JCall call:
                {
                    foreach (var a in call.Args) Walk(a, s);
                    string key = call.Name + "/" + call.Args.Length;
                    if (!s.HasFunc(key) && !JqNatives.TryGet(call.Name, call.Args.Length, out _) && JqPrelude.Lookup(call.Name, call.Args.Length) == null)
                        Error($"{key} is not defined", call);
                    return;
                }
        }
    }

    private Scope BindPattern(JPattern p, Scope s)
    {
        var names = new List<string>();
        CollectPattern(p, names, s);
        foreach (var n in names) s = s.With(v: n);
        return s;
    }

    private void CollectPattern(JPattern p, List<string> names, Scope s)
    {
        switch (p)
        {
            case JPVar v: if (!names.Contains(v.Name)) names.Add(v.Name); break;
            case JPArray a: foreach (var e in a.Elements) CollectPattern(e, names, s); break;
            case JPObject o:
                foreach (var e in o.Entries)
                {
                    Walk(e.Key, s);
                    if (e.Var != null && !names.Contains(e.Var)) names.Add(e.Var);
                    if (e.Sub != null) CollectPattern(e.Sub, names, s);
                }
                break;
        }
    }
}
