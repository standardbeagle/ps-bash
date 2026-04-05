using System.Collections.Immutable;
using System.Text;
using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Parser;

/// <summary>
/// Walks parsed AST nodes and emits equivalent PowerShell strings.
/// </summary>
public static class PsEmitter
{
    /// <summary>
    /// Emit PowerShell for the given command AST node.
    /// </summary>
    public static string Emit(Command cmd) => cmd switch
    {
        Command.Simple simple => EmitSimple(simple),
        Command.Pipeline pipeline => EmitPipeline(pipeline),
        Command.AndOrList andOr => EmitAndOrList(andOr),
        Command.ShAssignment assign => EmitShAssignment(assign),
        Command.CommandList list => EmitCommandList(list),
        _ => throw new NotSupportedException($"Unknown command type: {cmd.GetType().Name}"),
    };

    /// <summary>
    /// Parse bash input and emit equivalent PowerShell.
    /// Returns null if the input is empty or whitespace-only.
    /// </summary>
    public static string? Transpile(string bash)
    {
        var cmd = BashParser.Parse(bash);
        if (cmd is null)
            return null;
        return Emit(cmd);
    }

    private static string EmitShAssignment(Command.ShAssignment cmd)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < cmd.Pairs.Length; i++)
        {
            if (i > 0)
                sb.Append("; ");

            var pair = cmd.Pairs[i];
            sb.Append("$env:");
            sb.Append(pair.Name);
            sb.Append(" = ");
            sb.Append(EmitAssignmentValue(pair.Value));
        }
        return sb.ToString();
    }

    private static string EmitSimple(Command.Simple cmd)
    {
        // Bail on bash test constructs ([ -f ... ], [ -z ... ]) so the
        // caller can fall back to the regex pipeline which handles them.
        if (!cmd.Words.IsEmpty)
        {
            var name = cmd.Words[0].Parts.Length == 1 && cmd.Words[0].Parts[0] is WordPart.Literal lit
                ? lit.Value : null;
            if (name == "[")
                throw new NotSupportedException("Test constructs are not yet supported by the parser.");
        }

        // Input redirects (< file) become "Get-Content file | cmd".
        var inputRedirect = cmd.Redirects.FirstOrDefault(r => r.Op == "<");
        if (inputRedirect is not null)
        {
            var remaining = cmd.Redirects.Remove(inputRedirect);
            var innerCmd = new Command.Simple(cmd.Words, cmd.EnvPairs, remaining);
            return $"Get-Content {EmitWord(inputRedirect.Target)} | {EmitSimple(innerCmd)}";
        }

        var sb = new StringBuilder();

        foreach (var envPair in cmd.EnvPairs)
        {
            sb.Append("$env:");
            sb.Append(envPair.Name);
            sb.Append(" = ");
            sb.Append(EmitAssignmentValue(envPair.Value));
            sb.Append("; ");
        }

        for (var i = 0; i < cmd.Words.Length; i++)
        {
            if (i > 0)
                sb.Append(' ');
            sb.Append(EmitWord(cmd.Words[i]));
        }

        foreach (var redirect in cmd.Redirects)
        {
            sb.Append(' ');
            sb.Append(EmitRedirect(redirect));
        }

        return sb.ToString();
    }

    private static string EmitRedirect(Redirect r)
    {
        var target = TransformRedirectTarget(EmitWord(r.Target));

        return r.Op switch
        {
            ">" => r.Fd == 1 ? $">{target}" : $"{r.Fd}>{target}",
            ">>" => r.Fd == 1 ? $">>{target}" : $"{r.Fd}>>{target}",
            ">&" => $"{r.Fd}>&{target}",
            _ => $"{r.Fd}{r.Op}{target}",
        };
    }

    private static string TransformRedirectTarget(string target)
    {
        if (target == "/dev/null")
            return "$null";
        if (target.StartsWith("/tmp/"))
            return $"$env:TEMP\\{target[5..]}";
        return target;
    }

    /// <summary>
    /// Emit a value in assignment context: bare literals get wrapped in double quotes,
    /// already-quoted values are emitted as-is.
    /// </summary>
    private static string EmitAssignmentValue(CompoundWord? value)
    {
        if (value is null)
            return "\"\"";

        // If the value is a single double-quoted part, emit it directly (already has quotes).
        if (value.Parts.Length == 1 && value.Parts[0] is WordPart.DoubleQuoted)
            return EmitWordPart(value.Parts[0]);

        // If the value is a single single-quoted part, emit it directly.
        if (value.Parts.Length == 1 && value.Parts[0] is WordPart.SingleQuoted)
            return EmitWordPart(value.Parts[0]);

        // Otherwise wrap the emitted content in double quotes.
        return $"\"{EmitWord(value)}\"";
    }

    private static string EmitWord(CompoundWord word)
    {
        if (word.Parts.Length == 1)
            return TransformWordPath(EmitWordPart(word.Parts[0]));

        var sb = new StringBuilder();
        for (int i = 0; i < word.Parts.Length; i++)
        {
            var part = word.Parts[i];
            sb.Append(EmitWordPart(part));

            // After TildeSub, insert backslash separator if more parts follow.
            if (part is WordPart.TildeSub && i + 1 < word.Parts.Length)
                sb.Append('\\');
        }
        return TransformWordPath(sb.ToString());
    }

    private static string TransformWordPath(string value)
    {
        if (value == "/dev/null")
            return "$null";
        if (value.StartsWith("/tmp/"))
            return $"$env:TEMP\\{value[5..]}";
        return value;
    }

    private static string EmitWordPart(WordPart part) => part switch
    {
        WordPart.Literal lit => lit.Value,
        WordPart.EscapedLiteral el => $"`{el.Value}",
        WordPart.SingleQuoted sq => $"'{sq.Value}'",
        WordPart.DoubleQuoted dq => EmitDoubleQuoted(dq),
        WordPart.SimpleVarSub vs => EmitSimpleVar(vs.Name),
        WordPart.BracedVarSub bvs => EmitBracedVar(bvs),
        WordPart.CommandSub cs => $"$({Emit((Command)cs.Body)})",
        WordPart.TildeSub ts => ts.User is null ? "$HOME" : $"~{ts.User}",
        _ => throw new NotSupportedException($"Unknown word part type: {part.GetType().Name}"),
    };

    private static string EmitSimpleVar(string name) => name switch
    {
        "null" or "true" or "false" or "HOME" or "LASTEXITCODE" => $"${name}",
        "?" => "$LASTEXITCODE",
        "@" or "*" => "$args",
        "#" => "$args.Count",
        "0" => "$MyInvocation.MyCommand.Name",
        "$" => "$PID",
        "!" => "$PID",
        "-" => "$PSBoundParameters",
        var d when d.Length == 1 && d[0] is >= '1' and <= '9' => $"$args[{int.Parse(d) - 1}]",
        _ => $"$env:{name}",
    };

    private static string EmitBracedVar(WordPart.BracedVarSub bvs)
    {
        if (bvs.Suffix is null)
            return EmitSimpleVar(bvs.Name);

        string varRef = EmitSimpleVar(bvs.Name);

        // Length: ${#VAR}
        if (bvs.Suffix == "#")
            return $"{varRef}.Length";

        // Default value: ${VAR:-default}
        if (bvs.Suffix.StartsWith(":-"))
        {
            string defaultVal = bvs.Suffix[2..];
            return $"({varRef} ?? \"{defaultVal}\")";
        }

        // Assign default: ${VAR:=default}
        if (bvs.Suffix.StartsWith(":="))
        {
            string defaultVal = bvs.Suffix[2..];
            return $"({varRef} ?? ({varRef} = \"{defaultVal}\"))";
        }

        // Use alternative: ${VAR:+alt}
        if (bvs.Suffix.StartsWith(":+"))
        {
            string alt = bvs.Suffix[2..];
            return $"({varRef} ? \"{alt}\" : \"\")";
        }

        // Error if unset: ${VAR:?message}
        if (bvs.Suffix.StartsWith(":?"))
        {
            string msg = bvs.Suffix[2..];
            return $"({varRef} ?? $(throw \"{msg}\"))";
        }

        // Remove suffix: ${VAR%%pattern} or ${VAR%pattern}
        if (bvs.Suffix.StartsWith("%%"))
        {
            string pattern = bvs.Suffix[2..];
            return $"({varRef} -replace '{pattern}$','')";
        }
        if (bvs.Suffix.StartsWith("%"))
        {
            string pattern = bvs.Suffix[1..];
            return $"({varRef} -replace '{pattern}$','')";
        }

        // Remove prefix: ${VAR##pattern} or ${VAR#pattern}
        if (bvs.Suffix.StartsWith("##"))
        {
            string pattern = bvs.Suffix[2..];
            return $"({varRef} -replace '^{pattern}','')";
        }
        if (bvs.Suffix.StartsWith("#"))
        {
            string pattern = bvs.Suffix[1..];
            return $"({varRef} -replace '^{pattern}','')";
        }

        // Fallback: emit as-is with comment about unsupported operator
        return varRef;
    }

    private static string EmitDoubleQuoted(WordPart.DoubleQuoted dq)
    {
        var sb = new StringBuilder();
        sb.Append('"');
        foreach (var part in dq.Parts)
            sb.Append(EmitWordPart(part));
        sb.Append('"');
        return sb.ToString();
    }

    private static string EmitCommandList(Command.CommandList list)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < list.Commands.Length; i++)
        {
            if (i > 0)
                sb.Append("; ");
            sb.Append(Emit(list.Commands[i]));
        }
        return sb.ToString();
    }

    private static string EmitAndOrList(Command.AndOrList andOr)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < andOr.Commands.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
                sb.Append(andOr.Ops[i - 1]);
                sb.Append(' ');
            }

            sb.Append(Emit(andOr.Commands[i]));
        }

        return sb.ToString();
    }

    private static string EmitPipeline(Command.Pipeline pipeline)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < pipeline.Commands.Length; i++)
        {
            if (i > 0)
            {
                var op = pipeline.Ops[i - 1];
                if (op == "|&")
                    sb.Append(" 2>&1 | ");
                else
                    sb.Append(" | ");
            }

            var cmd = pipeline.Commands[i];
            if (i > 0 && cmd is Command.Simple simple && TryEmitMappedCommand(simple, out var mapped))
                sb.Append(mapped);
            else
                sb.Append(Emit(cmd));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Tries to emit a mapped PowerShell equivalent for known bash pipe-target commands.
    /// Returns false if the command is not a recognized pipe target.
    /// </summary>
    private static bool TryEmitMappedCommand(Command.Simple cmd, out string result)
    {
        result = "";
        if (cmd.Words.IsEmpty)
            return false;

        var name = GetLiteralValue(cmd.Words[0]);
        if (name is null)
            return false;

        var args = cmd.Words.RemoveAt(0);

        switch (name)
        {
            case "grep":
                result = EmitGrep(args);
                return true;
            case "head":
                result = EmitHead(args);
                return true;
            case "tail":
                result = EmitTail(args);
                return true;
            case "wc":
                result = EmitWc(args);
                return true;
            case "sort":
                result = EmitSort(args);
                return true;
            case "uniq":
                result = "Get-Unique";
                return true;
            case "sed":
                result = EmitPassthrough("Invoke-Sed", args);
                return true;
            case "awk":
                result = EmitPassthrough("Invoke-Awk", args);
                return true;
            case "cut":
                result = EmitCut(args);
                return true;
            case "xargs":
                result = EmitPassthrough("Invoke-Xargs", args);
                return true;
            case "tr":
                result = EmitPassthrough("Invoke-Tr", args);
                return true;
            case "tee":
                result = EmitTee(args);
                return true;
            default:
                return false;
        }
    }

    private static string? GetLiteralValue(CompoundWord word)
    {
        if (word.Parts.Length == 1 && word.Parts[0] is WordPart.Literal lit)
            return lit.Value;
        return null;
    }

    private static string EmitGrep(ImmutableArray<CompoundWord> args)
    {
        var flags = new List<string>();
        string? pattern = null;
        var rest = new List<string>();

        foreach (var arg in args)
        {
            var val = GetLiteralValue(arg);
            if (val is not null && val.StartsWith('-') && pattern is null)
            {
                foreach (var c in val.AsSpan(1))
                {
                    switch (c)
                    {
                        case 'v': flags.Add("-NotMatch"); break;
                        case 'i': flags.Add("-CaseInsensitive"); break;
                        case 'r': flags.Add("-Recurse"); break;
                    }
                }
            }
            else if (pattern is null)
            {
                pattern = $"\"{EmitWord(arg)}\"";
            }
            else
            {
                rest.Add(EmitWord(arg));
            }
        }

        var parts = new List<string> { "Invoke-Grep" };
        parts.AddRange(flags);
        if (pattern is not null)
            parts.Add(pattern);
        parts.AddRange(rest);

        return string.Join(' ', parts);
    }

    private static string EmitHead(ImmutableArray<CompoundWord> args)
    {
        var count = ExtractNumericFlag(args, "-n");
        return count is not null
            ? $"Select-Object -First {count}"
            : $"Select-Object -First 10";
    }

    private static string EmitTail(ImmutableArray<CompoundWord> args)
    {
        var count = ExtractNumericFlag(args, "-n");
        return count is not null
            ? $"Select-Object -Last {count}"
            : $"Select-Object -Last 10";
    }

    private static string EmitWc(ImmutableArray<CompoundWord> args)
    {
        if (args.Any(a => GetLiteralValue(a) == "-l"))
            return "Measure-Object -Line | Select-Object -Expand Lines";
        return "Measure-Object -Line";
    }

    private static string EmitSort(ImmutableArray<CompoundWord> args)
    {
        var hasReverse = args.Any(a => GetLiteralValue(a) == "-r");
        return hasReverse ? "Sort-Object -Descending" : "Sort-Object";
    }

    private static string EmitCut(ImmutableArray<CompoundWord> args)
    {
        string? delim = null;
        string? field = null;

        for (int i = 0; i < args.Length; i++)
        {
            var val = GetLiteralValue(args[i]);
            if (val is null) continue;

            if (val.StartsWith("-d") && val.Length > 2)
            {
                delim = val[2..];
            }
            else if (val == "-d" && i + 1 < args.Length)
            {
                delim = GetLiteralValue(args[++i]);
            }
            else if (val.StartsWith("-f") && val.Length > 2)
            {
                field = val[2..];
            }
            else if (val == "-f" && i + 1 < args.Length)
            {
                field = GetLiteralValue(args[++i]);
            }
        }

        var sb = new StringBuilder("Invoke-Cut");
        if (delim is not null)
            sb.Append($" -Delimiter {delim}");
        if (field is not null)
            sb.Append($" -Field {field}");
        return sb.ToString();
    }

    private static string EmitTee(ImmutableArray<CompoundWord> args)
    {
        if (args.Length > 0)
            return $"Tee-Object {EmitWord(args[0])}";
        return "Tee-Object";
    }

    private static string EmitPassthrough(string cmdlet, ImmutableArray<CompoundWord> args)
    {
        if (args.IsEmpty)
            return cmdlet;

        var sb = new StringBuilder(cmdlet);
        foreach (var arg in args)
        {
            sb.Append(' ');
            sb.Append(EmitWord(arg));
        }
        return sb.ToString();
    }

    private static string? ExtractNumericFlag(ImmutableArray<CompoundWord> args, string flag)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var val = GetLiteralValue(args[i]);
            if (val is null) continue;

            if (val == flag && i + 1 < args.Length)
                return GetLiteralValue(args[i + 1]);

            if (val.StartsWith(flag) && val.Length > flag.Length)
                return val[flag.Length..];
        }
        return null;
    }
}
