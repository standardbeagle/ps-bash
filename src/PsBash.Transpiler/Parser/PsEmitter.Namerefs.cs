using System.Collections.Immutable;
using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Parser;

public static partial class PsEmitter
{
    // `local -n ref=$1` names its target only at run time, but the script usually calls the function with
    // literal names (`setv foo`). The function body is emitted once per such name, with `ref` resolved to it
    // statically (the same machinery as `declare -n ref=foo`), and the variant is picked by the argument.
    // A target passed on through another function (`f2() { f3 "$@"; }; f2 F1`) is followed back to that function's
    // own call sites, so each call site records the function it sits in (null at top level).
    [ThreadStatic]
    private static Dictionary<string, List<(ImmutableArray<CompoundWord> Args, string? Caller)>>? _callSites;

    /// <summary>Calls every command node of <paramref name="root"/> (statement structure only, not command substitutions).</summary>
    private static void VisitCommands(Command? root, Action<Command> visit) => VisitCommands(root, visit, null);

    /// <summary>Visits <paramref name="root"/> and the commands inside it; a command <paramref name="descend"/> rejects is
    /// visited but not entered.</summary>
    private static void VisitCommands(Command? root, Action<Command> visit, Func<Command, bool>? descend)
    {
        if (root is null) return;
        visit(root);
        if (descend is not null && !descend(root)) return;
        switch (root)
        {
            case Command.Pipeline p: foreach (var c in p.Commands) VisitCommands(c, visit, descend); break;
            case Command.AndOrList a: foreach (var c in a.Commands) VisitCommands(c, visit, descend); break;
            case Command.CommandList l: foreach (var c in l.Commands) VisitCommands(c, visit, descend); break;
            case Command.If i:
                foreach (var arm in i.Arms) { VisitCommands(arm.Cond, visit, descend); VisitCommands(arm.Body, visit, descend); }
                VisitCommands(i.ElseBody, visit, descend);
                break;
            case Command.While w: VisitCommands(w.Cond, visit, descend); VisitCommands(w.Body, visit, descend); break;
            case Command.ForIn f: VisitCommands(f.Body, visit, descend); break;
            case Command.ForArith fa: VisitCommands(fa.Body, visit, descend); break;
            case Command.Select s: VisitCommands(s.Body, visit, descend); break;
            case Command.Case cs: foreach (var arm in cs.Arms) VisitCommands(arm.Body, visit, descend); break;
            case Command.BraceGroup bg: VisitCommands(bg.Body, visit, descend); break;
            case Command.Subshell sub: VisitCommands(sub.Body, visit, descend); break;
            case Command.ShFunction fn: VisitCommands(fn.Body, visit, descend); break;
            case Command.Background bk: VisitCommands(bk.Inner, visit, descend); break;
        }
    }

    /// <summary>Function names this unit defines: a call to one is the FUNCTION, never the same-named mapped command.</summary>
    [ThreadStatic] private static HashSet<string>? _definedFunctions;

    private static void CollectCallSites(Command root)
    {
        var sites = new Dictionary<string, List<(ImmutableArray<CompoundWord>, string?)>>(StringComparer.Ordinal);
        void Walk(Command? node, string? caller) => VisitCommands(node, c =>
        {
            if (c is Command.Simple { Words.Length: > 0 } s && GetLiteralValue(s.Words[0]) is { } name)
            {
                if (!sites.TryGetValue(name, out var list)) sites[name] = list = new();
                list.Add((s.Words.RemoveAt(0), caller));
            }
            else if (c is Command.ShFunction f) Walk(f.Body, f.Name);
        }, c => c is not Command.ShFunction);
        Walk(root, null);
        var fns = new HashSet<string>(StringComparer.Ordinal);
        VisitCommands(root, c => { if (c is Command.ShFunction f) fns.Add(f.Name); });
        _definedFunctions = fns;
        _callSites = sites;
    }

    /// <summary>
    /// Names the unit assigns an array literal to (<c>a=(…)</c>, outside <c>local</c>), wherever that happens: a function
    /// defined ABOVE the assignment reads <c>$a</c> / <c>${a-d}</c> as element 0 too (<c>f() { echo "${a-x}"; }; a=(""); f</c>
    /// prints an empty line, not x).
    /// </summary>
    private static void CollectArrayAssignments(Command root)
    {
        if (_arrayVars is null) return;
        VisitCommands(root, c =>
        {
            if (c is Command.ShAssignment { IsLocal: false } a)
            {
                foreach (var pair in a.Pairs)
                    if (pair.ArrayValue is not null && IsValidVarName(pair.Name)) _arrayVars.Add(pair.Name);
            }
            else if (c is Command.Simple s && BuiltinArrayOperand(s) is { } operand && IsValidVarName(operand)) _arrayVars.Add(operand);
        });
    }

    /// <summary>
    /// <paramref name="name"/> holds an array from here on: a bare <c>$name</c> reads element 0 (<see cref="EmitSimpleVar"/>).
    /// Through a nameref the emitter resolves, its target does too: the array is the target's, and the scalar the target
    /// held is no longer its value (bash: <c>declare -n ref=x; x=X; ref=(p q); echo "$ref $x"</c> prints <c>p p</c>).
    /// REGRESSION: only <c>ref</c> was marked, so <c>$x</c> (and <c>$ref</c>, which reads <c>$x</c>) still read X.
    /// </summary>
    private static void MarkArrayVar(string name)
    {
        if (_arrayVars is null || !IsValidVarName(name)) return;
        _arrayVars.Add(name);
        string target = ResolveNameref(name);
        if (target != name && IsValidVarName(target)) _arrayVars.Add(target);
    }

    /// <summary>
    /// The array a <c>read -a NAME</c> / <c>mapfile NAME</c> / <c>readarray NAME</c> writes, when the words name it
    /// literally (<c>MAPFILE</c> when mapfile names none); null otherwise. Only finds the name, so a bare <c>$NAME</c>
    /// reads element 0 (<see cref="MarkArrayVar"/>): the flags still go to the cmdlet as written.
    /// </summary>
    private static string? BuiltinArrayOperand(Command.Simple s)
    {
        if (s.Words.Length < 2 || GetLiteralValue(s.Words[0]) is not { } command) return null;
        bool read = command == "read";
        if (!read && command is not ("mapfile" or "readarray")) return null;
        string takesValue = read ? "dinNptu" : "dnOsuCc";
        for (int i = 1; i < s.Words.Length; i++)
        {
            if (GetLiteralValue(s.Words[i]) is not { } word) return null;   // known only at run time
            if (word == "--")
                return read ? null : i + 1 < s.Words.Length ? GetLiteralValue(s.Words[i + 1]) : "MAPFILE";
            if (word.Length < 2 || word[0] != '-') return read ? null : word;
            for (int k = 1; k < word.Length; k++)
            {
                if (read && word[k] == 'a')
                    return k + 1 < word.Length ? word[(k + 1)..] : i + 1 < s.Words.Length ? GetLiteralValue(s.Words[i + 1]) : null;
                if (takesValue.IndexOf(word[k]) < 0) continue;
                if (k + 1 == word.Length) i++;   // the value is the next word, whatever it is
                break;
            }
        }
        return read ? null : "MAPFILE";
    }

    /// <summary>The first <c>local -n NAME=$N</c> of a function body: the nameref and the positional it takes its target from.</summary>
    private static (string Name, int Index)? FindDynamicNameref(Command body)
    {
        (string, int)? found = null;
        VisitCommands(body, c =>
        {
            if (found is null && c is Command.ShAssignment { IsLocal: true, IsNameref: true } a)
                foreach (var pair in a.Pairs)
                    if (pair.Value is { Parts.Length: 1 } v
                        && (v.Parts[0] is WordPart.SimpleVarSub { Name: var n } || v.Parts[0] is WordPart.DoubleQuoted { Parts: [WordPart.SimpleVarSub { Name: var n2 }] } && (n = n2) is not null)
                        && PositionalIndex.TryParse(n, out int index) && index > 0)
                    {
                        found = (pair.Name, index);
                        return;
                    }
        });
        return found;
    }

    /// <summary>The distinct literal variable names the script passes as argument <paramref name="index"/> (1-based) of <paramref name="function"/>.</summary>
    /// <para>
    /// A call site that passes its OWN positionals on (<c>f3 "$@"</c>, <c>f3 "$1"</c>) inside function <c>f2</c> is
    /// followed to <c>f2</c>'s call sites at the matching index, transitively, so <c>f1 → f2 F1 → f3 "$@"</c> still
    /// yields <c>F1</c> (bash resolves the name at run time, by dynamic scope).
    /// </para>
    private static List<string> NamerefCandidates(string function, int index)
    {
        var result = new List<string>();
        CollectNamerefCandidates(function, index, result, new HashSet<(string, int)>());
        return result;
    }

    private static void CollectNamerefCandidates(string function, int index, List<string> result, HashSet<(string, int)> seen)
    {
        if (_callSites is null || !seen.Add((function, index)) || !_callSites.TryGetValue(function, out var calls)) return;
        foreach (var (args, caller) in calls)
        {
            // Walk the words up to the target position; a "$@" splat stands for the caller's positionals from there on.
            for (int j = 0; j < args.Length && j < index; j++)
            {
                if (ForwardedPositional(args[j]) is { } fwd)
                {
                    if (caller is null) break;
                    if (fwd == 0) { CollectNamerefCandidates(caller, index - j, result, seen); break; }   // "$@"
                    if (j == index - 1) CollectNamerefCandidates(caller, fwd, result, seen);              // "$N"
                    continue;
                }
                if (j == index - 1 && GetLiteralValue(args[j]) is { } t && IsValidVarName(t) && !result.Contains(t))
                    result.Add(t);
            }
        }
    }

    /// <summary>A word that is just the caller's positional: <c>$N</c> / <c>"$N"</c> / <c>${N}</c> → N, <c>"$@"</c> / <c>$@</c> → 0.</summary>
    private static int? ForwardedPositional(CompoundWord word)
    {
        var part = word.Parts is [WordPart.DoubleQuoted { Parts: [var inner] }] ? inner : word.Parts.Length == 1 ? word.Parts[0] : null;
        string? name = part switch
        {
            WordPart.SimpleVarSub v => v.Name,
            WordPart.BracedVarSub { Suffix: null } b => b.Name,
            _ => null,
        };
        if (name == "@") return 0;
        return PositionalIndex.TryParse(name, out int index) && index > 0 ? index : null;
    }

    /// <summary>The function body once per candidate target, selected by the run-time value of the positional.</summary>
    private static string EmitNamerefVariants(string function, (string Name, int Index) nameref, Func<string> emitBody)
    {
        var targets = NamerefCandidates(function, nameref.Index);
        if (targets.Count == 0 || _namerefs is null) return emitBody();
        string positional = $"$(if ({PsBuild.PositionalIsShells}) {{ $global:BashPositional[{nameref.Index - 1}] }} else {{ $args[{nameref.Index - 1}] }})";
        // The run-time nameref table learns the target too (whichever variant runs, the fallback included): code that resolves
        // names when it runs, arithmetic above all (`(( c++ ))`), reaches it. REGRESSION: it read and wrote a variable named c.
        var sb = new System.Text.StringBuilder("$__nrt = \"" + positional + "\"; [PsBash.Cmdlets.VarFrameStack]::LocalNameref("
            + PsBuild.SingleQuote(nameref.Name) + ", $__nrt); ");
        for (int i = 0; i < targets.Count; i++)
        {
            _namerefs[nameref.Name] = targets[i];
            sb.Append(i == 0 ? "if" : " elseif").Append(" ($__nrt -ceq ").Append(PsBuild.SingleQuote(targets[i])).Append(") { ").Append(emitBody()).Append(" }");
        }
        _namerefs.Remove(nameref.Name);
        return sb.Append(" else { ").Append(emitBody()).Append(" }").ToString();
    }
}
