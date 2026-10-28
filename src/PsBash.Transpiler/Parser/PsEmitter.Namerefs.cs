using System.Collections.Immutable;
using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Parser;

public static partial class PsEmitter
{
    // `local -n ref=$1` names its target only at run time, but the script usually calls the function with
    // literal names (`setv foo`). The function body is emitted once per such name, with `ref` resolved to it
    // statically (the same machinery as `declare -n ref=foo`), and the variant is picked by the argument.
    [ThreadStatic]
    private static Dictionary<string, List<ImmutableArray<CompoundWord>>>? _callSites;

    /// <summary>Calls every command node of <paramref name="root"/> (statement structure only, not command substitutions).</summary>
    private static void VisitCommands(Command? root, Action<Command> visit)
    {
        if (root is null) return;
        visit(root);
        switch (root)
        {
            case Command.Pipeline p: foreach (var c in p.Commands) VisitCommands(c, visit); break;
            case Command.AndOrList a: foreach (var c in a.Commands) VisitCommands(c, visit); break;
            case Command.CommandList l: foreach (var c in l.Commands) VisitCommands(c, visit); break;
            case Command.If i:
                foreach (var arm in i.Arms) { VisitCommands(arm.Cond, visit); VisitCommands(arm.Body, visit); }
                VisitCommands(i.ElseBody, visit);
                break;
            case Command.While w: VisitCommands(w.Cond, visit); VisitCommands(w.Body, visit); break;
            case Command.ForIn f: VisitCommands(f.Body, visit); break;
            case Command.ForArith fa: VisitCommands(fa.Body, visit); break;
            case Command.Select s: VisitCommands(s.Body, visit); break;
            case Command.Case cs: foreach (var arm in cs.Arms) VisitCommands(arm.Body, visit); break;
            case Command.BraceGroup bg: VisitCommands(bg.Body, visit); break;
            case Command.Subshell sub: VisitCommands(sub.Body, visit); break;
            case Command.ShFunction fn: VisitCommands(fn.Body, visit); break;
            case Command.Background bk: VisitCommands(bk.Inner, visit); break;
        }
    }

    private static void CollectCallSites(Command root)
    {
        var sites = new Dictionary<string, List<ImmutableArray<CompoundWord>>>(StringComparer.Ordinal);
        VisitCommands(root, c =>
        {
            if (c is Command.Simple { Words.Length: > 0 } s && GetLiteralValue(s.Words[0]) is { } name)
            {
                if (!sites.TryGetValue(name, out var list)) sites[name] = list = new();
                list.Add(s.Words.RemoveAt(0));
            }
        });
        _callSites = sites;
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
                        && n.Length > 0 && n.All(char.IsAsciiDigit) && n != "0")
                    {
                        found = (pair.Name, int.Parse(n));
                        return;
                    }
        });
        return found;
    }

    /// <summary>The distinct literal variable names the script passes as argument <paramref name="index"/> (1-based) of <paramref name="function"/>.</summary>
    private static List<string> NamerefCandidates(string function, int index)
    {
        var result = new List<string>();
        if (_callSites is not null && _callSites.TryGetValue(function, out var calls))
            foreach (var args in calls)
                if (args.Length >= index && GetLiteralValue(args[index - 1]) is { } t && IsValidVarName(t) && !result.Contains(t))
                    result.Add(t);
        return result;
    }

    /// <summary>The function body once per candidate target, selected by the run-time value of the positional.</summary>
    private static string EmitNamerefVariants(string function, (string Name, int Index) nameref, Func<string> emitBody)
    {
        var targets = NamerefCandidates(function, nameref.Index);
        if (targets.Count == 0 || _namerefs is null) return emitBody();
        string positional = $"$(if ($global:BashPositional) {{ $global:BashPositional[{nameref.Index - 1}] }} else {{ $args[{nameref.Index - 1}] }})";
        var sb = new System.Text.StringBuilder("$__nrt = \"" + positional + "\"; ");
        for (int i = 0; i < targets.Count; i++)
        {
            _namerefs[nameref.Name] = targets[i];
            sb.Append(i == 0 ? "if" : " elseif").Append(" ($__nrt -ceq ").Append(PsBuild.SingleQuote(targets[i])).Append(") { ").Append(emitBody()).Append(" }");
        }
        _namerefs.Remove(nameref.Name);
        return sb.Append(" else { ").Append(emitBody()).Append(" }").ToString();
    }
}