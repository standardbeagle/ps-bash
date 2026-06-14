using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

/// <summary>
/// Executes a parsed <see cref="AwkProgram"/> against a record stream. Owns the
/// variable / array / field state, the special variables (NR, NF, FNR, FS, OFS,
/// ORS, SUBSEP, RSTART, RLENGTH, CONVFMT, OFMT, FILENAME), the builtin-function
/// library, and the output buffer (line-oriented, matching ps-bash's
/// BashObject-per-line model). The driving cmdlet feeds records via
/// <see cref="StartFile"/> / <see cref="ProcessRecord"/> and bookends with
/// <see cref="RunBegin"/> / <see cref="RunEnd"/> / <see cref="Flush"/>.
/// </summary>
internal sealed class AwkMachine
{
    private readonly AwkProgram _prog;
    private readonly Action<string> _emitLine;

    private readonly Dictionary<string, AwkValue> _vars = new();
    private readonly Dictionary<string, Dictionary<string, AwkValue>> _arrays = new();
    private readonly List<string> _fields = new() { "" }; // [0] = $0
    private int _nf;
    private double _nr, _fnr, _rstart, _rlength;
    private readonly StringBuilder _outBuf = new();
    private Random _rand = new(0);
    private double _prevSeed;
    // Per-machine (per awk invocation): caches compiled regexes across the
    // record loop without sharing mutable state between concurrently-pooled
    // host runspaces. Discarded with the machine after the run.
    private readonly Dictionary<string, Regex> _regexCache = new();

    public bool Exited { get; private set; }
    public int ExitCode { get; private set; }

    public AwkMachine(AwkProgram program, Action<string> emitLine)
    {
        _prog = program;
        _emitLine = emitLine;
        _vars["FS"] = AwkValue.Str(" ");
        _vars["OFS"] = AwkValue.Str(" ");
        _vars["ORS"] = AwkValue.Str("\n");
        _vars["RS"] = AwkValue.Str("\n");
        _vars["SUBSEP"] = AwkValue.Str("\x1c");
        _vars["CONVFMT"] = AwkValue.Str("%.6g");
        _vars["OFMT"] = AwkValue.Str("%.6g");
        _vars["FILENAME"] = AwkValue.Str("");
        _vars["RSTART"] = AwkValue.Number(0);
        _vars["RLENGTH"] = AwkValue.Number(-1);
    }

    // ── public driver API ────────────────────────────────────────────────────

    public void SetVarInitial(string name, AwkValue value) => SetVar(name, value);

    public void SetFieldSeparator(string fs) => _vars["FS"] = AwkValue.Str(fs);

    public void StartFile(string filename)
    {
        _vars["FILENAME"] = AwkValue.Str(filename);
        _fnr = 0;
    }

    public void RunBegin()
    {
        foreach (var rule in _prog.Begin)
        {
            try { ExecStmt(rule.Action!); }
            catch (ExitSignal) { Exited = true; return; }
        }
    }

    public void ProcessRecord(string record)
    {
        _nr++;
        _fnr++;
        SetRecord(record);
        foreach (var rule in _prog.Main)
        {
            bool match;
            switch (rule.Kind)
            {
                case RuleKind.Always: match = true; break;
                case RuleKind.Regex: match = Eval(rule.Pattern!).ToBool(); break;
                default: match = Eval(rule.Pattern!).ToBool(); break;
            }
            if (!match) continue;
            try
            {
                if (rule.Action == null) Output(GetField(0).ToStr(Ofmt) + Ors);
                else ExecStmt(rule.Action);
            }
            catch (NextSignal) { return; }
            catch (NextFileSignal) { return; }
            catch (ExitSignal) { Exited = true; return; }
        }
    }

    public void RunEnd()
    {
        foreach (var rule in _prog.End)
        {
            try { ExecStmt(rule.Action!); }
            catch (ExitSignal) { Exited = true; return; }
            catch (NextSignal) { /* next in END is a no-op */ }
            catch (NextFileSignal) { }
        }
    }

    public void Flush()
    {
        if (_outBuf.Length > 0)
        {
            _emitLine(_outBuf.ToString());
            _outBuf.Clear();
        }
    }

    // ── output ───────────────────────────────────────────────────────────────

    private void Output(string s)
    {
        _outBuf.Append(s);
        int nl;
        while ((nl = IndexOfNewline(_outBuf)) >= 0)
        {
            _emitLine(_outBuf.ToString(0, nl));
            _outBuf.Remove(0, nl + 1);
        }
    }

    private static int IndexOfNewline(StringBuilder sb)
    {
        for (int i = 0; i < sb.Length; i++) if (sb[i] == '\n') return i;
        return -1;
    }

    // ── fields ─────────────────────────────────────────────────────────────

    private string Ofs => _vars["OFS"].ToStr(Convfmt);
    private string Ors => _vars["ORS"].ToStr(Convfmt);
    private string Ofmt => _vars["OFMT"].ToStr("%.6g");
    private string Convfmt => _vars.TryGetValue("CONVFMT", out var v) ? v.ToStr("%.6g") : "%.6g";
    private string Subsep => _vars["SUBSEP"].ToStr(Convfmt);

    private void SetRecord(string record)
    {
        _fields.Clear();
        _fields.Add(record);
        var parts = SplitWithFS(record, _vars["FS"].ToStr(Convfmt), false);
        _fields.AddRange(parts);
        _nf = parts.Count;
    }

    private AwkValue GetField(int i)
    {
        if (i == 0) return AwkValue.StrNum(_fields[0]);
        if (i < 0) return AwkValue.Str("");
        if (i <= _nf) return AwkValue.StrNum(_fields[i]);
        return AwkValue.Uninitialized;
    }

    // Ceiling on how many fields a single record may hold. A field index comes
    // from `(int)Eval(...).ToNumber()`, so an expression like `$(1e30) = "x"` or
    // `NF = 1e30` would otherwise saturate the cast to int.MaxValue and try to
    // append ~2.1 billion empty strings — instant OOM that wedges the host. Real
    // awk would also exhaust memory here; we fail with a bounded awk error.
    private const int MaxFields = 10_000_000;

    private void SetField(int i, string value)
    {
        if (i == 0) { SetRecord(value); return; }
        if (i < 0) return;
        if (i > MaxFields)
            throw new AwkInterpreter.AwkRuntimeException($"field index {i} exceeds the maximum of {MaxFields}");
        if (i > _nf)
        {
            while (_fields.Count <= i) _fields.Add("");
            _nf = i;
        }
        _fields[i] = value;
        RebuildRecord();
    }

    private void SetNF(int newNf)
    {
        if (newNf < 0) newNf = 0;
        if (newNf > MaxFields)
            throw new AwkInterpreter.AwkRuntimeException($"NF value {newNf} exceeds the maximum of {MaxFields}");
        if (newNf < _nf)
        {
            _fields.RemoveRange(newNf + 1, _fields.Count - (newNf + 1));
        }
        else
        {
            while (_fields.Count <= newNf) _fields.Add("");
        }
        _nf = newNf;
        RebuildRecord();
    }

    private void RebuildRecord()
    {
        var sb = new StringBuilder();
        string ofs = Ofs;
        for (int i = 1; i <= _nf; i++)
        {
            if (i > 1) sb.Append(ofs);
            sb.Append(_fields[i]);
        }
        _fields[0] = sb.ToString();
    }

    private List<string> SplitWithFS(string s, string fs, bool fsIsRegexLiteral)
    {
        if (s.Length == 0) return new List<string>();
        // An empty separator — whether the literal regex // or the empty string —
        // splits into individual characters in gawk. .NET's Regex.Split("") instead
        // matches at every boundary including the ends, yielding spurious leading
        // and trailing empty fields (split("ab",a,//) → ["","a","b",""], n=4 vs
        // gawk's 2). Route both empty forms to the character split.
        if (fs.Length == 0)
        {
            var chars = new List<string>(s.Length);
            foreach (char c in s) chars.Add(c.ToString());
            return chars;
        }
        if (fsIsRegexLiteral)
            return new List<string>(GetRegex(fs).Split(s));
        if (fs == " ")
            return SplitWhitespace(s);
        if (fs.Length == 1)
            return new List<string>(s.Split(fs[0]));
        return new List<string>(GetRegex(fs).Split(s));
    }

    /// <summary>
    /// Default field split (FS == " "): split on runs of spaces/tabs/newlines,
    /// ignoring leading/trailing whitespace. A manual scan on the per-record hot
    /// path — avoids a <see cref="Regex"/> match and the trimmed-string + array
    /// allocations the regex route required.
    /// </summary>
    private static List<string> SplitWhitespace(string s)
    {
        var result = new List<string>();
        int i = 0, n = s.Length;
        while (i < n)
        {
            while (i < n && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n')) i++;
            if (i >= n) break;
            int start = i;
            while (i < n && s[i] != ' ' && s[i] != '\t' && s[i] != '\n') i++;
            result.Add(s.Substring(start, i - start));
        }
        return result;
    }

    // ── variables / arrays ───────────────────────────────────────────────────

    private AwkValue GetVar(string name) => name switch
    {
        "NF" => AwkValue.Number(_nf),
        "NR" => AwkValue.Number(_nr),
        "FNR" => AwkValue.Number(_fnr),
        "RSTART" => AwkValue.Number(_rstart),
        "RLENGTH" => AwkValue.Number(_rlength),
        _ => _vars.TryGetValue(name, out var v) ? v : AwkValue.Uninitialized,
    };

    private void SetVar(string name, AwkValue value)
    {
        switch (name)
        {
            case "NF": SetNF((int)value.ToNumber()); return;
            case "NR": _nr = value.ToNumber(); return;
            case "FNR": _fnr = value.ToNumber(); return;
            case "RSTART": _rstart = value.ToNumber(); _vars["RSTART"] = value; return;
            case "RLENGTH": _rlength = value.ToNumber(); _vars["RLENGTH"] = value; return;
            default: _vars[name] = value; return;
        }
    }

    private Dictionary<string, AwkValue> GetArray(string name)
    {
        if (!_arrays.TryGetValue(name, out var arr))
        {
            arr = new Dictionary<string, AwkValue>();
            _arrays[name] = arr;
        }
        return arr;
    }

    private string SubscriptKey(IReadOnlyList<AwkExpr> subs)
    {
        if (subs.Count == 1) return Eval(subs[0]).ToStr(Convfmt);
        var parts = new string[subs.Count];
        for (int i = 0; i < subs.Count; i++) parts[i] = Eval(subs[i]).ToStr(Convfmt);
        return string.Join(Subsep, parts);
    }

    // ── statement execution ──────────────────────────────────────────────────

    private void ExecStmt(AwkStmt stmt)
    {
        switch (stmt)
        {
            case BlockStmt b:
                foreach (var s in b.Statements) ExecStmt(s);
                break;
            case PrintStmt p: ExecPrint(p); break;
            case PrintfStmt pf: ExecPrintf(pf); break;
            case ExprStmt e: Eval(e.Expr); break;
            case IfStmt iff:
                if (Eval(iff.Cond).ToBool()) ExecStmt(iff.Then);
                else if (iff.Else != null) ExecStmt(iff.Else);
                break;
            case WhileStmt w:
                while (Eval(w.Cond).ToBool())
                {
                    try { ExecStmt(w.Body); }
                    catch (BreakSignal) { break; }
                    catch (ContinueSignal) { }
                }
                break;
            case DoWhileStmt dw:
                do
                {
                    try { ExecStmt(dw.Body); }
                    catch (BreakSignal) { break; }
                    catch (ContinueSignal) { }
                } while (Eval(dw.Cond).ToBool());
                break;
            case ForStmt f:
                if (f.Init != null) ExecStmt(f.Init);
                while (f.Cond == null || Eval(f.Cond).ToBool())
                {
                    try { ExecStmt(f.Body); }
                    catch (BreakSignal) { break; }
                    catch (ContinueSignal) { }
                    if (f.Post != null) ExecStmt(f.Post);
                }
                break;
            case ForInStmt fi:
            {
                var arr = GetArray(fi.ArrayName);
                foreach (var key in new List<string>(arr.Keys))
                {
                    SetVar(fi.Var, AwkValue.StrNum(key));
                    try { ExecStmt(fi.Body); }
                    catch (BreakSignal) { break; }
                    catch (ContinueSignal) { }
                }
                break;
            }
            case BreakStmt: throw new BreakSignal();
            case ContinueStmt: throw new ContinueSignal();
            case NextStmt: throw new NextSignal();
            case NextFileStmt: throw new NextFileSignal();
            case ExitStmt ex:
                if (ex.Code != null) ExitCode = (int)Eval(ex.Code).ToNumber();
                throw new ExitSignal();
            case DeleteStmt del: ExecDelete(del); break;
        }
    }

    private void ExecPrint(PrintStmt p)
    {
        if (p.Args.Count == 0)
        {
            Output(GetField(0).ToStr(Ofmt) + Ors);
            return;
        }
        string ofs = Ofs;
        var sb = new StringBuilder();
        for (int i = 0; i < p.Args.Count; i++)
        {
            if (i > 0) sb.Append(ofs);
            sb.Append(Eval(p.Args[i]).ToStr(Ofmt));
        }
        sb.Append(Ors);
        Output(sb.ToString());
    }

    private void ExecPrintf(PrintfStmt pf)
    {
        if (pf.Args.Count == 0) return;
        string fmt = Eval(pf.Args[0]).ToStr(Convfmt);
        var vals = new List<AwkValue>(pf.Args.Count - 1);
        for (int i = 1; i < pf.Args.Count; i++) vals.Add(Eval(pf.Args[i]));
        Output(AwkPrintf.Format(fmt, vals, Convfmt));
    }

    private void ExecDelete(DeleteStmt del)
    {
        var arr = GetArray(del.ArrayName);
        if (del.Subscripts == null) { arr.Clear(); return; }
        string key = SubscriptKey(del.Subscripts);
        arr.Remove(key);
    }

    // ── expression evaluation ────────────────────────────────────────────────

    private AwkValue Eval(AwkExpr expr)
    {
        switch (expr)
        {
            case NumLit n: return AwkValue.Number(n.Value);
            case StrLit s: return AwkValue.Str(s.Value);
            case RegexLit r: return AwkValue.Bool(GetRegex(r.Pattern).IsMatch(_fields[0]));
            case Grouping g: return Eval(g.Inner);
            case VarRef v: return GetVar(v.Name);
            case FieldRef fr: return GetField((int)Eval(fr.Index).ToNumber());
            case ArrayRef ar:
            {
                var arr = GetArray(ar.Name);
                string key = SubscriptKey(ar.Subscripts);
                if (!arr.TryGetValue(key, out var val)) { val = AwkValue.Uninitialized; arr[key] = val; }
                return val;
            }
            case Assign a: return EvalAssign(a);
            case Ternary t: return Eval(t.Cond).ToBool() ? Eval(t.Then) : Eval(t.Else);
            case Logical l:
                if (l.Op == "&&") return AwkValue.Bool(Eval(l.Left).ToBool() && Eval(l.Right).ToBool());
                return AwkValue.Bool(Eval(l.Left).ToBool() || Eval(l.Right).ToBool());
            case MatchExpr m:
            {
                string subject = Eval(m.Left).ToStr(Convfmt);
                string pattern = m.Right is RegexLit rl ? rl.Pattern : Eval(m.Right).ToStr(Convfmt);
                bool matched = GetRegex(pattern).IsMatch(subject);
                return AwkValue.Bool(m.Negated ? !matched : matched);
            }
            case InExpr ine:
            {
                var arr = GetArray(ine.ArrayName);
                string key = SubscriptKey(ine.Keys);
                return AwkValue.Bool(arr.ContainsKey(key));
            }
            case Compare c: return EvalCompare(c);
            case Concat cc: return AwkValue.Str(Eval(cc.Left).ToStr(Convfmt) + Eval(cc.Right).ToStr(Convfmt));
            case Arith ar2: return EvalArith(ar2);
            case Power pw: return AwkValue.Number(Math.Pow(Eval(pw.Left).ToNumber(), Eval(pw.Right).ToNumber()));
            case Unary u: return EvalUnary(u);
            case IncDec id: return EvalIncDec(id);
            case Call call: return EvalCall(call);
            default: throw new InvalidOperationException("unknown expr node");
        }
    }

    private AwkValue EvalAssign(Assign a)
    {
        AwkValue result;
        if (a.Op == "=")
        {
            result = Eval(a.Value);
        }
        else
        {
            double cur = GetLvalue(a.Target).ToNumber();
            double rhs = Eval(a.Value).ToNumber();
            double n = a.Op switch
            {
                "+=" => cur + rhs,
                "-=" => cur - rhs,
                "*=" => cur * rhs,
                "/=" => cur / rhs,
                "%=" => Fmod(cur, rhs),
                "^=" => Math.Pow(cur, rhs),
                _ => rhs,
            };
            result = AwkValue.Number(n);
        }
        SetLvalue(a.Target, result);
        return result;
    }

    private AwkValue EvalCompare(Compare c)
    {
        var a = Eval(c.Left);
        var b = Eval(c.Right);
        int cmp;
        if (a.IsNumericContext && b.IsNumericContext)
            cmp = a.ToNumber().CompareTo(b.ToNumber());
        else
            cmp = string.CompareOrdinal(a.ToStr(Convfmt), b.ToStr(Convfmt));
        bool res = c.Op switch
        {
            "<" => cmp < 0,
            "<=" => cmp <= 0,
            ">" => cmp > 0,
            ">=" => cmp >= 0,
            "==" => cmp == 0,
            "!=" => cmp != 0,
            _ => false,
        };
        return AwkValue.Bool(res);
    }

    private AwkValue EvalArith(Arith a)
    {
        double l = Eval(a.Left).ToNumber();
        double r = Eval(a.Right).ToNumber();
        return AwkValue.Number(a.Op switch
        {
            '+' => l + r,
            '-' => l - r,
            '*' => l * r,
            '/' => l / r,
            '%' => Fmod(l, r),
            _ => 0,
        });
    }

    private static double Fmod(double a, double b) => b == 0 ? double.NaN : a - b * Math.Truncate(a / b);

    private AwkValue EvalUnary(Unary u)
    {
        if (u.Op == '!') return AwkValue.Bool(!Eval(u.Operand).ToBool());
        double v = Eval(u.Operand).ToNumber();
        return AwkValue.Number(u.Op == '-' ? -v : v);
    }

    private AwkValue EvalIncDec(IncDec id)
    {
        double old = GetLvalue(id.Target).ToNumber();
        double updated = id.Increment ? old + 1 : old - 1;
        SetLvalue(id.Target, AwkValue.Number(updated));
        return AwkValue.Number(id.Prefix ? updated : old);
    }

    private AwkValue GetLvalue(AwkExpr target) => target switch
    {
        VarRef v => GetVar(v.Name),
        FieldRef f => GetField((int)Eval(f.Index).ToNumber()),
        ArrayRef a => EvalArrayRead(a),
        Grouping g => GetLvalue(g.Inner),
        _ => AwkValue.Uninitialized,
    };

    private AwkValue EvalArrayRead(ArrayRef a)
    {
        var arr = GetArray(a.Name);
        string key = SubscriptKey(a.Subscripts);
        return arr.TryGetValue(key, out var v) ? v : AwkValue.Uninitialized;
    }

    private void SetLvalue(AwkExpr target, AwkValue value)
    {
        switch (target)
        {
            case VarRef v: SetVar(v.Name, value); break;
            case FieldRef f: SetField((int)Eval(f.Index).ToNumber(), value.ToStr(Convfmt)); break;
            case ArrayRef a: GetArray(a.Name)[SubscriptKey(a.Subscripts)] = value; break;
            case Grouping g: SetLvalue(g.Inner, value); break;
        }
    }

    // ── builtin functions ────────────────────────────────────────────────────

    private AwkValue EvalCall(Call call)
    {
        switch (call.Name)
        {
            case "length": return BuiltinLength(call.Args);
            case "substr": return BuiltinSubstr(call.Args);
            case "index": return BuiltinIndex(call.Args);
            case "split": return BuiltinSplit(call.Args);
            case "sub": return BuiltinSubGsub(call.Args, global: false);
            case "gsub": return BuiltinSubGsub(call.Args, global: true);
            case "match": return BuiltinMatch(call.Args);
            case "sprintf": return BuiltinSprintf(call.Args);
            case "tolower": return AwkValue.Str(Arg(call, 0).ToLowerInvariant());
            case "toupper": return AwkValue.Str(Arg(call, 0).ToUpperInvariant());
            case "sin": return AwkValue.Number(Math.Sin(ArgN(call, 0)));
            case "cos": return AwkValue.Number(Math.Cos(ArgN(call, 0)));
            case "atan2": return AwkValue.Number(Math.Atan2(ArgN(call, 0), ArgN(call, 1)));
            case "exp": return AwkValue.Number(Math.Exp(ArgN(call, 0)));
            case "log": return AwkValue.Number(Math.Log(ArgN(call, 0)));
            case "sqrt": return AwkValue.Number(Math.Sqrt(ArgN(call, 0)));
            case "int": return AwkValue.Number(Math.Truncate(ArgN(call, 0)));
            case "rand": return AwkValue.Number(_rand.NextDouble());
            case "srand": return BuiltinSrand(call.Args);
            case "systime": return AwkValue.Number(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            case "strftime": return BuiltinStrftime(call.Args);
            case "system": return AwkValue.Number(0); // unsupported; report success
            case "close": return AwkValue.Number(0);
            case "fflush": return AwkValue.Number(0);
            default: throw new AwkInterpreter.AwkSyntaxException($"awk: calling undefined function {call.Name}");
        }
    }

    private string Arg(Call c, int i) => i < c.Args.Count ? Eval(c.Args[i]).ToStr(Convfmt) : "";
    private double ArgN(Call c, int i) => i < c.Args.Count ? Eval(c.Args[i]).ToNumber() : 0;

    private AwkValue BuiltinLength(List<AwkExpr> args)
    {
        if (args.Count == 0) return AwkValue.Number(_fields[0].Length);
        if (args[0] is VarRef vr && _arrays.ContainsKey(vr.Name))
            return AwkValue.Number(_arrays[vr.Name].Count);
        return AwkValue.Number(Eval(args[0]).ToStr(Convfmt).Length);
    }

    private AwkValue BuiltinSubstr(List<AwkExpr> args)
    {
        if (args.Count < 2) return AwkValue.Str("");
        string str = Eval(args[0]).ToStr(Convfmt);
        double mD = Math.Truncate(Eval(args[1]).ToNumber());
        double start1 = mD;
        double end1;
        if (args.Count >= 3)
        {
            double lenD = Math.Truncate(Eval(args[2]).ToNumber());
            end1 = start1 + lenD;
        }
        else
        {
            end1 = str.Length + 1;
        }
        double lo = Math.Max(1, start1);
        double hi = Math.Min(str.Length + 1, end1);
        if (hi <= lo) return AwkValue.Str("");
        int from = (int)lo - 1;
        int count = (int)(hi - lo);
        return AwkValue.Str(str.Substring(from, count));
    }

    private AwkValue BuiltinIndex(List<AwkExpr> args)
    {
        if (args.Count < 2) return AwkValue.Number(0);
        string s = Eval(args[0]).ToStr(Convfmt);
        string t = Eval(args[1]).ToStr(Convfmt);
        return AwkValue.Number(s.IndexOf(t, StringComparison.Ordinal) + 1);
    }

    private AwkValue BuiltinSplit(List<AwkExpr> args)
    {
        if (args.Count < 2 || args[1] is not VarRef arrRef) return AwkValue.Number(0);
        string s = Eval(args[0]).ToStr(Convfmt);
        var arr = GetArray(arrRef.Name);
        arr.Clear();
        _arrays[arrRef.Name] = arr;

        List<string> parts;
        if (args.Count >= 3)
        {
            if (args[2] is RegexLit rl) parts = SplitWithFS(s, rl.Pattern, true);
            else parts = SplitWithFS(s, Eval(args[2]).ToStr(Convfmt), false);
        }
        else
        {
            parts = SplitWithFS(s, _vars["FS"].ToStr(Convfmt), false);
        }

        for (int i = 0; i < parts.Count; i++)
            arr[(i + 1).ToString(CultureInfo.InvariantCulture)] = AwkValue.StrNum(parts[i]);
        return AwkValue.Number(parts.Count);
    }

    private AwkValue BuiltinSubGsub(List<AwkExpr> args, bool global)
    {
        if (args.Count < 2) return AwkValue.Number(0);
        string pattern = args[0] is RegexLit rl ? rl.Pattern : Eval(args[0]).ToStr(Convfmt);
        string repl = Eval(args[1]).ToStr(Convfmt);
        AwkExpr targetExpr = args.Count >= 3 ? args[2] : DollarZero;
        string input = GetLvalue(targetExpr).ToStr(Convfmt);

        var regex = GetRegex(pattern);
        int count = 0;
        string result;
        if (global)
        {
            result = regex.Replace(input, m => { count++; return BuildReplacement(repl, m.Value); });
        }
        else
        {
            result = regex.Replace(input, m => { count++; return BuildReplacement(repl, m.Value); }, 1);
        }

        if (count > 0 && IsAssignable(targetExpr))
            SetLvalue(targetExpr, AwkValue.Str(result));
        return AwkValue.Number(count);
    }

    private static readonly FieldRef DollarZero = new() { Index = new NumLit { Value = 0 } };

    private static bool IsAssignable(AwkExpr e) => e is VarRef or FieldRef or ArrayRef
        || (e is Grouping g && IsAssignable(g.Inner));

    /// <summary>Resolve awk replacement specials: <c>&amp;</c> = whole match, <c>\&amp;</c> = literal &amp;.</summary>
    private static string BuildReplacement(string repl, string matched)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < repl.Length; i++)
        {
            char c = repl[i];
            if (c == '\\' && i + 1 < repl.Length)
            {
                char nx = repl[i + 1];
                if (nx == '&') { sb.Append('&'); i++; continue; }
                if (nx == '\\') { sb.Append('\\'); i++; continue; }
                sb.Append('\\');
                continue;
            }
            if (c == '&') { sb.Append(matched); continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private AwkValue BuiltinMatch(List<AwkExpr> args)
    {
        if (args.Count < 2) return AwkValue.Number(0);
        string s = Eval(args[0]).ToStr(Convfmt);
        string pattern = args[1] is RegexLit rl ? rl.Pattern : Eval(args[1]).ToStr(Convfmt);
        var m = GetRegex(pattern).Match(s);
        if (m.Success)
        {
            _rstart = m.Index + 1;
            _rlength = m.Length;
        }
        else
        {
            _rstart = 0;
            _rlength = -1;
        }
        _vars["RSTART"] = AwkValue.Number(_rstart);
        _vars["RLENGTH"] = AwkValue.Number(_rlength);
        return AwkValue.Number(_rstart);
    }

    private AwkValue BuiltinSprintf(List<AwkExpr> args)
    {
        if (args.Count == 0) return AwkValue.Str("");
        string fmt = Eval(args[0]).ToStr(Convfmt);
        var vals = new List<AwkValue>(args.Count - 1);
        for (int i = 1; i < args.Count; i++) vals.Add(Eval(args[i]));
        return AwkValue.Str(AwkPrintf.Format(fmt, vals, Convfmt));
    }

    private AwkValue BuiltinSrand(List<AwkExpr> args)
    {
        double prev = _prevSeed;
        double seed = args.Count >= 1 ? Eval(args[0]).ToNumber() : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _prevSeed = seed;
        _rand = new Random(unchecked((int)(long)seed));
        return AwkValue.Number(prev);
    }

    private AwkValue BuiltinStrftime(List<AwkExpr> args)
    {
        string fmt = args.Count >= 1 ? Eval(args[0]).ToStr(Convfmt) : "%a %b %e %H:%M:%S %Z %Y";
        DateTime dt;
        if (args.Count >= 2)
            dt = DateTimeOffset.FromUnixTimeSeconds((long)Eval(args[1]).ToNumber()).UtcDateTime;
        else
            dt = DateTimeOffset.UtcNow.UtcDateTime;

        fmt = fmt.Replace("%%", "\x01");
        fmt = fmt.Replace("%Y", dt.ToString("yyyy", CultureInfo.InvariantCulture));
        fmt = fmt.Replace("%m", dt.ToString("MM", CultureInfo.InvariantCulture));
        fmt = fmt.Replace("%d", dt.ToString("dd", CultureInfo.InvariantCulture));
        fmt = fmt.Replace("%H", dt.ToString("HH", CultureInfo.InvariantCulture));
        fmt = fmt.Replace("%M", dt.ToString("mm", CultureInfo.InvariantCulture));
        fmt = fmt.Replace("%S", dt.ToString("ss", CultureInfo.InvariantCulture));
        fmt = fmt.Replace("%j", dt.DayOfYear.ToString("000", CultureInfo.InvariantCulture));
        fmt = fmt.Replace("%a", dt.ToString("ddd", CultureInfo.InvariantCulture));
        fmt = fmt.Replace("%A", dt.ToString("dddd", CultureInfo.InvariantCulture));
        fmt = fmt.Replace("%b", dt.ToString("MMM", CultureInfo.InvariantCulture));
        fmt = fmt.Replace("%B", dt.ToString("MMMM", CultureInfo.InvariantCulture));
        fmt = fmt.Replace("%p", dt.ToString("tt", CultureInfo.InvariantCulture));
        fmt = fmt.Replace("%I", dt.ToString("hh", CultureInfo.InvariantCulture));
        fmt = fmt.Replace("\x01", "%");
        return AwkValue.Str(fmt);
    }

    // Bound on how long any single regex operation may run. .NET's backtracking
    // engine is vulnerable to catastrophic backtracking (e.g. /^(a+)+b/ against a
    // long run of 'a's); without a timeout one pathological match hangs the
    // single-threaded host indefinitely. On expiry the engine throws
    // RegexMatchTimeoutException, which GetRegex's callers see surfaced as an awk
    // runtime error rather than a hang.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    private Regex GetRegex(string pattern)
    {
        if (!_regexCache.TryGetValue(pattern, out var rx))
        {
            try
            {
                rx = new Regex(pattern, RegexOptions.None, RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                // Malformed pattern (unbalanced paren/class, bad quantifier). Real
                // awk reports a fatal regex error; surface it the same way instead
                // of letting RegexParseException escape and crash the runspace.
                throw new AwkInterpreter.AwkRuntimeException($"invalid regular expression: /{pattern}/: {ex.Message}");
            }
            _regexCache[pattern] = rx;
        }
        return rx;
    }

    // ── control-flow signals ─────────────────────────────────────────────────

    private sealed class BreakSignal : Exception { }
    private sealed class ContinueSignal : Exception { }
    private sealed class NextSignal : Exception { }
    private sealed class NextFileSignal : Exception { }
    private sealed class ExitSignal : Exception { }
}
