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
    /// <summary>
    /// Set of loop variable names currently in scope.
    /// Variables declared by for-in or for-arith loops emit as <c>$var</c> rather than <c>$env:var</c>.
    /// </summary>
    [ThreadStatic]
    private static HashSet<string>? _loopVars;

    /// <summary>
    /// Tracks nesting depth for generating unique loop iteration counter variable names.
    /// </summary>
    [ThreadStatic]
    private static int _loopDepth;

    private const int DefaultMaxIterations = 100_000;

    private static string IterGuardPrefix(int depth)
    {
        var varName = depth == 0 ? "$__psbash_iter" : $"$__psbash_iter{depth}";
        return $"{varName} = 0; ";
    }

    private static string IterGuardCheck(int depth)
    {
        var varName = depth == 0 ? "$__psbash_iter" : $"$__psbash_iter{depth}";
        return $"if (++{varName} -gt ($env:PSBASH_MAX_ITERATIONS ?? {DefaultMaxIterations})) " +
               $"{{ throw \"ps-bash: loop iteration limit exceeded ($(($env:PSBASH_MAX_ITERATIONS ?? {DefaultMaxIterations})))\" }}; ";
    }

    /// <summary>
    /// Commands that have PowerShell built-in aliases and should NOT be mapped
    /// to Invoke-Bash* when used as standalone (non-piped) commands.
    /// These work fine via PS aliases; only their piped forms need mapping.
    /// </summary>
    private static readonly HashSet<string> PsBuiltinAliases = new(StringComparer.Ordinal)
    {
        "echo", "cat", "ls", "cd", "pwd", "mkdir", "cp", "mv", "rm",
        "sort", "diff", "sleep",
    };

    public static string Emit(Command cmd) => cmd switch
    {
        Command.If ifCmd => EmitIf(ifCmd),
        Command.BoolExpr boolExpr => EmitBoolExpr(boolExpr),
        Command.ForIn forIn => EmitForIn(forIn),
        Command.ForArith forArith => EmitForArith(forArith),
        Command.While whileCmd => EmitWhile(whileCmd),
        Command.Case caseCmd => EmitCase(caseCmd),
        Command.ArithCommand arith => EmitArithCommand(arith),
        Command.Subshell subshell => EmitSubshell(subshell),
        Command.BraceGroup braceGroup => EmitBraceGroup(braceGroup),
        Command.ShFunction func => EmitFunction(func),
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

    private static string EmitIf(Command.If ifCmd)
    {
        var sb = new StringBuilder();

        for (int i = 0; i < ifCmd.Arms.Length; i++)
        {
            var arm = ifCmd.Arms[i];
            if (i == 0)
                sb.Append("if");
            else
                sb.Append(" elseif");

            sb.Append(" (");
            sb.Append(EmitCondition(arm.Cond));
            sb.Append(") { ");
            sb.Append(Emit(arm.Body));
            sb.Append(" }");
        }

        if (ifCmd.ElseBody is not null)
        {
            sb.Append(" else { ");
            sb.Append(Emit(ifCmd.ElseBody));
            sb.Append(" }");
        }

        return sb.ToString();
    }

    private static string EmitForIn(Command.ForIn forIn)
    {
        int depth = _loopDepth++;
        try
        {
            var sb = new StringBuilder();
            sb.Append(IterGuardPrefix(depth));

            // Empty list means implicit $@ -> $args
            if (forIn.List.IsEmpty)
            {
                sb.Append($"foreach (${forIn.Var} in $args) {{ ");
            }
            else
            {
                sb.Append($"foreach (${forIn.Var} in ");
                sb.Append(FormatForInList(forIn.List));
                sb.Append(") { ");
            }

            sb.Append(IterGuardCheck(depth));

            var vars = _loopVars ??= new HashSet<string>();
            bool added = vars.Add(forIn.Var);
            try
            {
                sb.Append(Emit(forIn.Body));
            }
            finally
            {
                if (added) vars.Remove(forIn.Var);
            }

            sb.Append(" }");
            return sb.ToString();
        }
        finally
        {
            _loopDepth--;
        }
    }

    private static string FormatForInList(ImmutableArray<CompoundWord> list)
    {
        if (list.Length == 1)
        {
            var single = EmitWord(list[0]);
            if (HasGlobChars(single))
                return $"(Resolve-Path {single})";
            return single;
        }

        // Check for glob patterns
        bool hasGlob = false;
        foreach (var word in list)
        {
            if (HasGlobChars(EmitWord(word)))
            {
                hasGlob = true;
                break;
            }
        }

        if (hasGlob)
        {
            var sb = new StringBuilder("(Resolve-Path ");
            for (int i = 0; i < list.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(EmitWord(list[i]));
            }
            sb.Append(')');
            return sb.ToString();
        }

        // Multiple items: join with commas, quoting strings
        var items = new List<string>();
        foreach (var word in list)
        {
            var val = EmitWord(word);
            items.Add(FormatForItem(val));
        }
        return string.Join(",", items);
    }

    private static string FormatForItem(string item)
    {
        if (double.TryParse(item, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out _))
            return item;
        return $"'{item}'";
    }

    private static string EmitForArith(Command.ForArith forArith)
    {
        int depth = _loopDepth++;
        try
        {
            // Register loop var before emitting header so init/cond/step all use $var
            string? loopVar = ExtractArithVar(forArith.Init);
            var vars = _loopVars ??= new HashSet<string>();
            bool added = loopVar is not null && vars.Add(loopVar);
            try
            {
                var sb = new StringBuilder();
                sb.Append(IterGuardPrefix(depth));
                sb.Append("for (");
                sb.Append(TranslateArithClause(forArith.Init, isInit: true));
                sb.Append("; ");
                sb.Append(TranslateArithCondition(forArith.Cond));
                sb.Append("; ");
                sb.Append(TranslateArithClause(forArith.Step, isInit: false));
                sb.Append(") { ");
                sb.Append(IterGuardCheck(depth));
                sb.Append(Emit(forArith.Body));
                sb.Append(" }");
                return sb.ToString();
        }
        finally
        {
            if (added) vars.Remove(loopVar!);
        }
        }
        finally
        {
            _loopDepth--;
        }
    }

    private static string EmitWhile(Command.While whileCmd)
    {
        // Special case: while read VAR -> ForEach-Object pipeline (no infinite loop risk)
        if (IsWhileRead(whileCmd.Cond, out var readVar))
            return EmitWhileRead(readVar, whileCmd.Body);

        int depth = _loopDepth++;
        try
        {
            var sb = new StringBuilder();
            sb.Append(IterGuardPrefix(depth));
            sb.Append("while (");
            var condText = EmitWhileCondition(whileCmd.Cond);

            if (whileCmd.IsUntil)
            {
                sb.Append("-not (");
                sb.Append(condText);
                sb.Append(')');
            }
            else
            {
                sb.Append(condText);
            }

            sb.Append(") { ");
            sb.Append(IterGuardCheck(depth));
            sb.Append(Emit(whileCmd.Body));
            sb.Append(" }");
            return sb.ToString();
        }
        finally
        {
            _loopDepth--;
        }
    }

    private static string EmitCase(Command.Case caseCmd)
    {
        bool useWildcard = false;
        foreach (var arm in caseCmd.Arms)
        {
            foreach (var pattern in arm.Patterns)
            {
                if (pattern != "*" && HasGlobChars(pattern))
                {
                    useWildcard = true;
                    break;
                }
            }
            if (useWildcard) break;
        }

        var sb = new StringBuilder("switch");
        if (useWildcard)
            sb.Append(" -Wildcard");
        sb.Append(" (");
        sb.Append(EmitWord(caseCmd.Expr));
        sb.Append(") { ");

        for (int i = 0; i < caseCmd.Arms.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            var arm = caseCmd.Arms[i];
            var bodyText = Emit(arm.Body);

            if (arm.Patterns.Length == 1 && arm.Patterns[0] == "*")
            {
                sb.Append("default { ");
                sb.Append(bodyText);
                sb.Append(" }");
            }
            else
            {
                for (int p = 0; p < arm.Patterns.Length; p++)
                {
                    if (p > 0) sb.Append(' ');
                    sb.Append('\'');
                    sb.Append(arm.Patterns[p]);
                    sb.Append("' { ");
                    sb.Append(bodyText);
                    sb.Append(" }");
                }
            }
        }

        sb.Append(" }");
        return sb.ToString();
    }

    private static string EmitFunction(Command.ShFunction func)
    {
        var sb = new StringBuilder("function ");
        sb.Append(func.Name);
        sb.Append(" { ");
        sb.Append(Emit(func.Body));
        sb.Append(" }");
        return sb.ToString();
    }

    private static string EmitSubshell(Command.Subshell subshell)
    {
        var sb = new StringBuilder("& { ");
        sb.Append(Emit(subshell.Body));
        sb.Append(" }");

        Redirect? fileRedirect = null;
        foreach (var redirect in subshell.Redirects)
        {
            var target = TransformRedirectTarget(EmitWord(redirect.Target));
            bool isStdoutFile = redirect.Fd == 1
                && (redirect.Op == ">" || redirect.Op == ">>")
                && target != "$null";

            if (isStdoutFile && fileRedirect is null)
            {
                fileRedirect = redirect;
            }
            else
            {
                sb.Append(' ');
                sb.Append(EmitRedirect(redirect));
            }
        }

        if (fileRedirect is not null)
        {
            var target = TransformRedirectTarget(EmitWord(fileRedirect.Target));
            sb.Append(" | Invoke-BashRedirect -Path ");
            sb.Append(target);
            if (fileRedirect.Op == ">>")
                sb.Append(" -Append");
        }

        return sb.ToString();
    }

    private static string EmitBraceGroup(Command.BraceGroup braceGroup)
    {
        return Emit(braceGroup.Body);
    }

    private static string EmitArithCommand(Command.ArithCommand arith)
    {
        string expr = arith.Expr.Trim();

        // Increment/decrement: env vars need assignment with [int] cast;
        // loop/PS vars can use native ++ / -- operators.
        if (expr.EndsWith("++") || expr.EndsWith("--"))
        {
            string varName = expr[..^2].Trim();
            string varRef = varName.StartsWith('$') ? varName : ArithVarRef(varName);
            if (IsEnvVar(varRef))
            {
                string delta = expr.EndsWith("++") ? "1" : "-1";
                return $"{varRef} = [int]{varRef} + {delta}";
            }
            return varRef + expr[^2..];
        }
        if (expr.StartsWith("++") || expr.StartsWith("--"))
        {
            string varName = expr[2..].Trim();
            string varRef = varName.StartsWith('$') ? varName : ArithVarRef(varName);
            if (IsEnvVar(varRef))
            {
                string delta = expr.StartsWith("++") ? "1" : "-1";
                return $"{varRef} = [int]{varRef} + {delta}";
            }
            return expr[..2] + varRef;
        }

        // Ternary: x > 0 ? 1 : 0 -> if ($x -gt 0) { 1 } else { 0 }
        int qIdx = FindTernaryQuestion(expr);
        if (qIdx >= 0)
        {
            int cIdx = expr.IndexOf(':', qIdx + 1);
            if (cIdx >= 0)
            {
                string cond = expr[..qIdx].Trim();
                string trueVal = expr[(qIdx + 1)..cIdx].Trim();
                string falseVal = expr[(cIdx + 1)..].Trim();
                string psCond = TranslateArithCondition(cond);
                trueVal = PrefixBareVar(trueVal);
                falseVal = PrefixBareVar(falseVal);
                return $"if ({psCond}) {{ {trueVal} }} else {{ {falseVal} }}";
            }
        }

        // Comparison: x > 5 -> $x -gt 5 (contains comparison operator)
        if (HasComparisonOp(expr))
            return TranslateArithCondition(expr);

        // Assignment: x = 5 -> $x = 5
        if (expr.Contains('=') && !expr.Contains("==") && !expr.Contains("!=")
            && !expr.Contains("<=") && !expr.Contains(">="))
        {
            return TranslateArithClause(expr, isInit: true);
        }

        // General arithmetic: x + 1 -> $x + 1
        return PrefixBareVar(expr);
    }

    private static int FindTernaryQuestion(string expr)
    {
        for (int i = 0; i < expr.Length; i++)
        {
            if (expr[i] == '?' && i > 0 && (i + 1 >= expr.Length || expr[i + 1] != '?'))
                return i;
        }
        return -1;
    }

    private static bool HasComparisonOp(string expr) =>
        expr.Contains("==") || expr.Contains("!=")
        || expr.Contains("<=") || expr.Contains(">=")
        || ContainsBareComparison(expr);

    private static bool ContainsBareComparison(string expr)
    {
        for (int i = 0; i < expr.Length; i++)
        {
            char c = expr[i];
            if (c == '<' || c == '>')
            {
                // Skip <= >= (already checked) and << >> (shift operators)
                if (i + 1 < expr.Length && expr[i + 1] is '=' or '<' or '>')
                    continue;
                if (i > 0 && expr[i - 1] is '<' or '>')
                    continue;
                return true;
            }
        }
        return false;
    }

    private static string EmitWhileCondition(Command cond)
    {
        // Bare "true"/"false" commands map to PowerShell boolean constants
        if (cond is Command.Simple simple && simple.Words.Length == 1
            && simple.EnvPairs.IsEmpty && simple.Redirects.IsEmpty)
        {
            var word = GetLiteralValue(simple.Words[0]);
            if (word is "true") return "$true";
            if (word is "false") return "$false";
        }

        return EmitCondition(cond);
    }

    private static bool IsWhileRead(Command cond, out string varName)
    {
        varName = "";
        if (cond is not Command.Simple simple)
            return false;
        if (simple.Words.Length < 2)
            return false;
        if (GetLiteralValue(simple.Words[0]) != "read")
            return false;
        // Accept: read VAR  or  read -r VAR  or  read -r -a VAR etc.
        // The variable name is the last non-flag word.
        string? name = null;
        for (int i = 1; i < simple.Words.Length; i++)
        {
            var val = GetLiteralValue(simple.Words[i]);
            if (val is not null && !val.StartsWith('-'))
                name = val;
        }
        if (name is null)
            return false;
        varName = name;
        return true;
    }

    private static string EmitWhileRead(string varName, Command body)
    {
        var bodyText = Emit(body);
        // Replace $env:VAR with $_ (exact match, not prefix of longer name)
        // Note: $_ in Regex.Replace is a special substitution; use $$ to produce literal $.
        bodyText = System.Text.RegularExpressions.Regex.Replace(
            bodyText, @$"\$env:{varName}(?!\w)", "$$_");
        // Also replace bare $VAR references (loop var scope)
        bodyText = System.Text.RegularExpressions.Regex.Replace(
            bodyText, @$"\${varName}(?!\w)", "$$_");

        return $"ForEach-Object {{ if ($_.PSObject.Properties['BashText']) {{ $_.BashText }} else {{ \"$_\" }} }} | ForEach-Object {{ ($_ -replace \"`n$\",\"\") -split \"`n\" }} | ForEach-Object {{ {bodyText} }}";
    }

    private static string? ExtractArithVar(string init)
    {
        int eq = init.IndexOf('=');
        if (eq > 0)
            return init[..eq].Trim().TrimStart('$');
        return null;
    }

    private static string TranslateArithClause(string clause, bool isInit)
    {
        clause = clause.Trim();
        if (clause.Length == 0) return "";

        // Check for increment/decrement BEFORE PrefixBareVar to get clean var name
        if (clause.EndsWith("++") || clause.EndsWith("--"))
        {
            string varName = clause[..^2].Trim();
            string varRef = varName.StartsWith('$') ? varName : ArithVarRef(varName);
            if (IsEnvVar(varRef))
            {
                string delta = clause.EndsWith("++") ? "1" : "-1";
                return $"{varRef} = [int]{varRef} + {delta}";
            }
            return varRef + clause[^2..];
        }

        // Convert assignment before PrefixBareVar to separate LHS from RHS
        if (isInit && clause.Contains('=') && !clause.Contains("=="))
        {
            int eq = clause.IndexOf('=');
            string varName = clause[..eq].Trim();
            string valExpr = clause[(eq + 1)..].Trim();
            string varRef = varName.StartsWith('$') ? varName : ArithVarRef(varName);
            string valPart = PrefixBareVar(valExpr);
            return $"{varRef} = {valPart}";
        }

        // Prefix bare variables with $
        clause = PrefixBareVar(clause);

        return clause;
    }

    private static string TranslateArithCondition(string cond)
    {
        cond = cond.Trim();
        if (cond.Length == 0) return "";

        // Replace multi-char operators before single-char to avoid partial matches
        cond = cond.Replace("<=", " -le ")
                   .Replace(">=", " -ge ")
                   .Replace("!=", " -ne ")
                   .Replace("==", " -eq ");

        // Handle bare < and > (not already part of -le/-ge/-ne/-eq)
        cond = cond.Replace("<", " -lt ").Replace(">", " -gt ");

        cond = PrefixBareVar(cond);

        // Clean up multiple spaces
        cond = System.Text.RegularExpressions.Regex.Replace(cond, @"\s+", " ").Trim();

        return cond;
    }

    private static readonly HashSet<string> _psBuiltins = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "null", "HOME", "LASTEXITCODE", "PID", "args",
    };

    /// <summary>
    /// Return the PowerShell variable reference for a bare name:
    /// loop vars and PS builtins get <c>$name</c>, others get <c>$env:name</c>.
    /// </summary>
    private static string ArithVarRef(string name)
    {
        if (_psBuiltins.Contains(name))
            return "$" + name;
        if (_loopVars is not null && _loopVars.Contains(name))
            return "$" + name;
        return "$env:" + name;
    }

    private static bool IsEnvVar(string varRef) => varRef.StartsWith("$env:");

    /// <summary>
    /// Prefix bare variable names in arithmetic expressions with the correct
    /// PowerShell variable form. Loop variables (tracked in <c>_loopVars</c>)
    /// become <c>$var</c>; PS builtins keep <c>$var</c>; all other user
    /// variables become <c>$env:var</c>.
    /// Skips identifiers preceded by <c>$</c> or <c>-</c> (PowerShell operators).
    /// </summary>
    private static string PrefixBareVar(string expr)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            expr,
            @"\b([a-zA-Z_]\w*)\b(?!\s*\()",
            m =>
            {
                if (m.Index > 0 && expr[m.Index - 1] is '$' or '-')
                    return m.Value;
                string name = m.Value;
                if (_psBuiltins.Contains(name))
                    return "$" + name;
                if (_loopVars is not null && _loopVars.Contains(name))
                    return "$" + name;
                return "[int]$env:" + name;
            });
    }

    /// <summary>
    /// Emit a condition expression for use inside <c>if</c>/<c>elif</c>.
    /// BoolExpr nodes are unwrapped (no outer parens); plain commands are emitted as-is.
    /// </summary>
    private static string EmitCondition(Command cond)
    {
        if (cond is Command.BoolExpr boolExpr)
            return "(" + EmitBoolExprInner(boolExpr) + ")";

        if (cond is Command.Subshell subshell)
            return Emit(subshell.Body);

        // Bare true/false as condition need raw $true/$false (not [void] wrapped)
        if (cond is Command.Simple simple && simple.Words.Length == 1
            && simple.EnvPairs.IsEmpty && simple.Redirects.IsEmpty)
        {
            var word = GetLiteralValue(simple.Words[0]);
            if (word is "true") return "$true";
            if (word is "false") return "$false";
        }

        return Emit(cond);
    }

    private static string EmitBoolExpr(Command.BoolExpr expr)
    {
        return "(" + EmitBoolExprInner(expr) + ")";
    }

    private static string EmitBoolExprInner(Command.BoolExpr expr)
    {
        if (expr.Extended)
            return EmitExtendedTest(expr.Inner);
        return TranslateTestCondition(expr.Inner, extended: false);
    }

    private static string EmitExtendedTest(ImmutableArray<CompoundWord> inner)
    {
        // Split on logical operators (&& / ||) into sub-expressions.
        var segments = SplitLogical(inner);

        if (segments.Count == 1)
            return TranslateTestCondition(segments[0].Words, extended: true);

        var sb = new StringBuilder();
        for (int i = 0; i < segments.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
                sb.Append(segments[i - 1].TrailingOp);
                sb.Append(' ');
            }
            sb.Append(TranslateTestCondition(segments[i].Words, extended: true));
        }
        return sb.ToString();
    }

    private static string TranslateTestCondition(ImmutableArray<CompoundWord> words, bool extended)
    {
        if (words.Length >= 2)
        {
            var flag = GetLiteralValue(words[0]);
            if (flag == "-f")
                return $"Test-Path \"{EmitWord(words[1])}\" -PathType Leaf";
            if (flag == "-d")
                return $"Test-Path \"{EmitWord(words[1])}\" -PathType Container";
            if (flag == "-e")
                return $"Test-Path \"{EmitWord(words[1])}\"";
            if (flag == "-z")
                return $"[string]::IsNullOrEmpty({EmitTestArg(words[1])})";
            if (flag == "-n")
                return $"-not [string]::IsNullOrEmpty({EmitTestArg(words[1])})";
        }

        if (words.Length == 3)
        {
            var lhs = EmitWord(words[0]);
            var op = GetLiteralValue(words[1]);
            var rhs = EmitWord(words[2]);

            if (op == "=~")
                return $"{lhs} -match '{rhs}'";

            if (op is "==" or "=")
            {
                var unquoted = StripQuotes(rhs);
                if (HasGlobChars(unquoted))
                    return $"{lhs} -like '{unquoted}'";
                return $"{lhs} -eq {rhs}";
            }

            // In [[ ]], < and > are lexicographic string comparisons.
            // In [ ], they don't appear (bash uses -lt/-gt for numeric).
            if (extended && op is "<" or ">")
            {
                var cmpOp = op == "<" ? "-lt" : "-gt";
                return $"[string]::Compare({lhs}, {rhs}, [System.StringComparison]::Ordinal) {cmpOp} 0";
            }

            var psOp = op switch
            {
                "!=" => "-ne",
                "<" => "-lt",
                ">" => "-gt",
                _ => op,
            };
            return $"{lhs} {psOp} {rhs}";
        }

        // Fallback: emit the inner words as a plain expression.
        var sb = new StringBuilder();
        for (int i = 0; i < words.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(EmitWord(words[i]));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Emit a word used as a test expression argument, unwrapping outer double quotes
    /// so that variable references appear bare (e.g. <c>$HOME</c> not <c>"$HOME"</c>).
    /// </summary>
    private static string EmitTestArg(CompoundWord word)
    {
        if (word.Parts.Length == 1 && word.Parts[0] is WordPart.DoubleQuoted dq)
        {
            var sb = new StringBuilder();
            foreach (var part in dq.Parts)
                sb.Append(EmitWordPart(part));
            return sb.ToString();
        }
        return EmitWord(word);
    }

    private static bool HasGlobChars(string value) =>
        value.Contains('*') || value.Contains('?') || value.Contains('[');

    private static string StripQuotes(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1];
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1];
        return value;
    }

    private readonly record struct LogicalSegment(
        ImmutableArray<CompoundWord> Words,
        string? TrailingOp);

    private static List<LogicalSegment> SplitLogical(ImmutableArray<CompoundWord> inner)
    {
        var segments = new List<LogicalSegment>();
        var current = ImmutableArray.CreateBuilder<CompoundWord>();

        foreach (var word in inner)
        {
            var lit = GetLiteralValue(word);
            if (lit is "&&" or "||")
            {
                var psOp = lit == "&&" ? "-and" : "-or";
                segments.Add(new LogicalSegment(current.ToImmutable(), psOp));
                current.Clear();
            }
            else
            {
                current.Add(word);
            }
        }

        segments.Add(new LogicalSegment(current.ToImmutable(), null));
        return segments;
    }


    private static string EmitShAssignment(Command.ShAssignment cmd)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < cmd.Pairs.Length; i++)
        {
            if (i > 0)
                sb.Append("; ");

            var pair = cmd.Pairs[i];
            string varPrefix = cmd.IsLocal ? "$" : "$env:";

            // Array assignment: arr=(a b c) -> $arr = @('a','b','c')
            // Array append:     arr+=('x')  -> $arr += @('x')
            if (pair.ArrayValue is not null)
            {
                sb.Append('$');
                sb.Append(pair.Name);
                sb.Append(pair.Op == AssignOp.PlusEqual ? " += @(" : " = @(");
                for (int j = 0; j < pair.ArrayValue.Elements.Length; j++)
                {
                    if (j > 0)
                        sb.Append(',');
                    var elem = pair.ArrayValue.Elements[j];
                    // If the element is a pure single-quoted word, emit 'value' directly
                    // to avoid doubled quotes ('value' -> ''value'').
                    if (elem.Parts.Length == 1 && elem.Parts[0] is WordPart.SingleQuoted sq)
                    {
                        sb.Append('\'');
                        sb.Append(sq.Value);
                        sb.Append('\'');
                    }
                    else
                    {
                        sb.Append('\'');
                        sb.Append(EmitWord(elem));
                        sb.Append('\'');
                    }
                }
                sb.Append(')');
                continue;
            }

            // Subscript assignment: map[key]=val -> $map['key'] = 'val'
            if (pair.Name.Contains('['))
            {
                int bracketIdx = pair.Name.IndexOf('[');
                string baseName = pair.Name[..bracketIdx];
                string subscript = pair.Name[(bracketIdx + 1)..^1]; // strip [ and ]
                sb.Append('$');
                sb.Append(baseName);
                sb.Append("['");
                sb.Append(subscript);
                sb.Append("'] = '");
                sb.Append(pair.Value is not null ? EmitWord(pair.Value) : "");
                sb.Append('\'');
                continue;
            }

            sb.Append(varPrefix);
            sb.Append(pair.Name);
            sb.Append(pair.Op == AssignOp.PlusEqual ? " += " : " = ");
            sb.Append(EmitAssignmentValue(pair.Value));
        }
        return sb.ToString();
    }

    private static string EmitSimple(Command.Simple cmd)
    {
        // Heredoc: emit as @"body"@ | cmd (or @'body'@ for no-expand).
        if (!cmd.HereDocs.IsDefaultOrEmpty)
        {
            // When multiple heredocs exist, use the last one for stdin (bash behavior).
            var hereDoc = cmd.HereDocs[^1];
            var innerCmd = new Command.Simple(cmd.Words, cmd.EnvPairs, cmd.Redirects);
            string body = hereDoc.Body;
            if (hereDoc.Expand)
                body = TranslateHereDocVars(body);
            string hereString = hereDoc.Expand
                ? $"@\"\n{body}\n\"@"
                : $"@'\n{body}\n'@";
            string cmdText = EmitSimple(innerCmd);
            return $"{hereString} | Emit-BashLine | {cmdText}";
        }

        // Stdout-to-stderr redirects (>&2 or 1>&2) — PowerShell reserves 1>&2,
        // so pipe through [Console]::Error.WriteLine instead.
        var stderrRedirect = cmd.Redirects.FirstOrDefault(r =>
            r.Op == ">&" && r.Fd == 1 && GetLiteralValue(r.Target) == "2");
        if (stderrRedirect is not null)
        {
            var remaining = cmd.Redirects.Remove(stderrRedirect);
            var innerCmd = new Command.Simple(cmd.Words, cmd.EnvPairs, remaining);
            return $"{EmitSimple(innerCmd)} | ForEach-Object {{ [Console]::Error.WriteLine($_) }}";
        }

        // Input redirects (< file) become "Get-Content file | cmd".
        var inputRedirect = cmd.Redirects.FirstOrDefault(r => r.Op == "<");
        if (inputRedirect is not null)
        {
            var remaining = cmd.Redirects.Remove(inputRedirect);
            var innerCmd = new Command.Simple(cmd.Words, cmd.EnvPairs, remaining);
            return $"Get-Content {EmitWord(inputRedirect.Target)} | {EmitSimple(innerCmd)}";
        }

        // declare -A map -> $map = @{} (associative array / hashtable declaration)
        // declare -a arr -> $arr = @() (indexed array declaration)
        if (cmd.Words.Length >= 2)
        {
            var name = GetLiteralValue(cmd.Words[0]);
            if (name == "declare")
            {
                bool isAssoc = false;
                string? varName = null;
                foreach (var word in cmd.Words.Skip(1))
                {
                    var val = GetLiteralValue(word);
                    if (val == "-A") isAssoc = true;
                    else if (val == "-i") { /* integer — handled below */ }
                    else if (val is not null && !val.StartsWith('-')) varName = val;
                }
                if (varName is not null)
                {
                    if (isAssoc) return $"${varName} = @{{}}";
                    bool isInt = cmd.Words.Skip(1).Any(w => GetLiteralValue(w) == "-i");
                    return isInt ? $"[int]${varName} = 0" : $"${varName} = @()";
                }
            }
        }

        if (cmd.Words.Length >= 1)
        {
            var cmd0 = GetLiteralValue(cmd.Words[0]);

            // true -> no-op success; false -> silent failure (sets $? = $false for && / ||)
            if (cmd0 == "true" && cmd.Words.Length == 1)
                return "[void]$true";
            if (cmd0 == "false" && cmd.Words.Length == 1)
                return "Write-Error '' -ErrorAction SilentlyContinue";

            // read [-r] [-p "prompt"] VAR -> $VAR = Read-Host ["prompt"]
            if (cmd0 == "read")
            {
                string? prompt = null;
                string? targetVar = null;
                for (int i = 1; i < cmd.Words.Length; i++)
                {
                    var val = GetLiteralValue(cmd.Words[i]);
                    if (val == "-p" && i + 1 < cmd.Words.Length)
                    {
                        prompt = EmitWord(cmd.Words[i + 1]);
                        i++; // skip prompt value
                    }
                    else if (val is not null && !val.StartsWith('-'))
                        targetVar = val;
                }
                if (targetVar is not null)
                {
                    string readHostCall = prompt is not null
                        ? $"Read-Host {prompt}"
                        : "Read-Host";
                    return $"${targetVar} = {readHostCall}";
                }
            }

            // set -e / set -o errexit -> $ErrorActionPreference = 'Stop'
            // set -x / set -o xtrace -> Set-PSDebug -Trace 1
            // set -u / set -o nounset -> Set-StrictMode -Version Latest
            if (cmd0 == "set" && cmd.Words.Length >= 2)
            {
                var args = cmd.Words.Skip(1).Select(w => GetLiteralValue(w)).ToList();
                bool longOpt = args.Any(a => a == "-o");
                if (longOpt)
                {
                    // set -o OPTION form
                    var optVal = args.SkipWhile(a => a != "-o").Skip(1).FirstOrDefault();
                    if (optVal == "errexit") return "$ErrorActionPreference = 'Stop'";
                    if (optVal == "xtrace") return "Set-PSDebug -Trace 1";
                    if (optVal == "nounset") return "Set-StrictMode -Version Latest";
                }
                else
                {
                    // set -euo pipefail style — check if any flag contains 'e', 'x', or 'u'
                    var flags = args.Where(a => a is not null && a.StartsWith('-') && !a.StartsWith("--")).ToList();
                    bool e = flags.Any(f => f!.Contains('e'));
                    bool x = flags.Any(f => f!.Contains('x'));
                    bool u = flags.Any(f => f!.Contains('u'));
                    var parts = new List<string>();
                    if (e) parts.Add("$ErrorActionPreference = 'Stop'");
                    if (u) parts.Add("Set-StrictMode -Version Latest");
                    if (x) parts.Add("Set-PSDebug -Trace 1");
                    if (parts.Count > 0) return string.Join("; ", parts);
                }
            }

            // source FILE / . FILE -> . FILE (PS dot-source, .sh -> .ps1)
            if ((cmd0 == "source" || cmd0 == ".") && cmd.Words.Length >= 2)
            {
                string file = EmitWord(cmd.Words[1]);
                // Translate .sh extension to .ps1
                if (file.EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
                    file = file[..^3] + ".ps1";
                return $". {file}";
            }

            // Standalone mapped commands: rewrite through TryEmitMappedCommand
            // unless the command is a PS builtin alias that works fine as-is.
            if (cmd0 is not null
                && !PsBuiltinAliases.Contains(cmd0)
                && TryEmitMappedCommand(cmd, out var mapped))
            {
                if (cmd.Redirects.IsEmpty)
                    return mapped;

                // Append redirects (TryEmitMappedCommand only handles args)
                var mappedSb = new StringBuilder(mapped);
                EmitPipeTargetRedirects(cmd, mappedSb);
                return mappedSb.ToString();
            }
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

        // Separate stdout file redirects from other redirects.
        // Stdout file redirects (> file, >> file) use Invoke-BashRedirect to avoid
        // lingering file handles that cause IO.IOException in chained commands.
        Redirect? fileRedirect = null;
        foreach (var redirect in cmd.Redirects)
        {
            var target = TransformRedirectTarget(EmitWord(redirect.Target));
            bool isStdoutFile = redirect.Fd == 1
                && (redirect.Op == ">" || redirect.Op == ">>")
                && target != "$null";

            if (isStdoutFile && fileRedirect is null)
            {
                fileRedirect = redirect;
            }
            else
            {
                sb.Append(' ');
                sb.Append(EmitRedirect(redirect));
            }
        }

        if (fileRedirect is not null)
        {
            var target = TransformRedirectTarget(EmitWord(fileRedirect.Target));
            sb.Append(" | Invoke-BashRedirect -Path ");
            sb.Append(target);
            if (fileRedirect.Op == ">>")
                sb.Append(" -Append");
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
    /// Translate bash variable references ($VAR, ${VAR}) in heredoc body text
    /// to PowerShell equivalents ($env:VAR).
    /// </summary>
    private static string TranslateHereDocVars(string body)
    {
        var sb = new StringBuilder();
        int pos = 0;
        int len = body.Length;

        while (pos < len)
        {
            if (body[pos] == '$' && pos + 1 < len && body[pos + 1] == '{')
            {
                // ${VAR} -> $env:VAR
                int close = body.IndexOf('}', pos + 2);
                if (close >= 0)
                {
                    string name = body[(pos + 2)..close];
                    sb.Append(EmitSimpleVar(name));
                    pos = close + 1;
                }
                else
                {
                    sb.Append(body[pos]);
                    pos++;
                }
            }
            else if (body[pos] == '$' && pos + 1 < len && IsHereDocVarStart(body[pos + 1]))
            {
                pos++; // skip $
                int start = pos;
                while (pos < len && IsHereDocVarChar(body[pos]))
                    pos++;
                string name = body[start..pos];
                sb.Append(EmitSimpleVar(name));
            }
            else
            {
                sb.Append(body[pos]);
                pos++;
            }
        }

        return sb.ToString();
    }

    private static bool IsHereDocVarStart(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_';

    private static bool IsHereDocVarChar(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_' or (>= '0' and <= '9');

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
        // Check for brace expansion parts that need prefix/suffix combination.
        if (HasBraceExpansion(word.Parts))
            return EmitBraceExpandedWord(word.Parts);

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

    private static bool HasBraceExpansion(ImmutableArray<WordPart> parts)
    {
        foreach (var part in parts)
        {
            if (part is WordPart.BracedTuple or WordPart.BracedRange)
                return true;
        }
        return false;
    }

    private static string EmitBraceExpandedWord(ImmutableArray<WordPart> parts)
    {
        // Collect prefix (parts before brace), brace expansion, suffix (parts after brace).
        var prefix = new StringBuilder();
        var suffix = new StringBuilder();
        WordPart? bracePart = null;
        bool foundBrace = false;

        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (!foundBrace && part is WordPart.BracedTuple or WordPart.BracedRange)
            {
                bracePart = part;
                foundBrace = true;
            }
            else if (!foundBrace)
            {
                prefix.Append(EmitWordPart(part));
            }
            else
            {
                suffix.Append(EmitWordPart(part));
            }
        }

        string pre = prefix.ToString();
        string suf = suffix.ToString();
        var expanded = ExpandBrace(bracePart!);

        // If no prefix/suffix, emit the bare expansion.
        if (pre.Length == 0 && suf.Length == 0)
            return FormatBraceArray(expanded);

        // With prefix/suffix, generate explicit items.
        var items = new List<string>();
        foreach (string item in expanded)
            items.Add($"'{pre}{item}{suf}'");

        return $"@({string.Join(',', items)})";
    }

    private static List<string> ExpandBrace(WordPart part)
    {
        if (part is WordPart.BracedTuple tuple)
            return new List<string>(tuple.Items);

        var range = (WordPart.BracedRange)part;
        var items = new List<string>();
        int step = range.Step != 0 ? Math.Abs(range.Step) * (range.Start <= range.End ? 1 : -1)
                                   : (range.Start <= range.End ? 1 : -1);
        for (int v = range.Start;
             step > 0 ? v <= range.End : v >= range.End;
             v += step)
        {
            if (range.ZeroPad > 0)
                items.Add(v.ToString().PadLeft(range.ZeroPad, '0'));
            else
                items.Add(v.ToString());
        }
        return items;
    }

    private static string FormatBraceArray(List<string> items)
    {
        // Check if all items are integers — use PS range operator if sequential.
        if (items.Count >= 2 && items.All(IsPlainInteger))
        {
            int first = int.Parse(items[0]);
            int last = int.Parse(items[^1]);
            bool isSequential = true;
            int step = first <= last ? 1 : -1;
            for (int i = 0; i < items.Count; i++)
            {
                if (int.Parse(items[i]) != first + i * step)
                {
                    isSequential = false;
                    break;
                }
            }
            if (isSequential)
                return $"@({first}..{last})";
        }

        // Otherwise emit as array literal.
        var formatted = new List<string>();
        foreach (string item in items)
        {
            if (IsPlainInteger(item))
                formatted.Add(item);
            else
                formatted.Add($"'{item}'");
        }
        return $"@({string.Join(',', formatted)})";
    }

    private static bool IsPlainInteger(string s)
    {
        if (s.Length == 0) return false;
        int start = s[0] == '-' ? 1 : 0;
        if (start >= s.Length) return false;
        // Leading zeros mean it's a zero-padded string, not a plain integer.
        if (s.Length - start > 1 && s[start] == '0')
            return false;
        for (int i = start; i < s.Length; i++)
        {
            if (s[i] is not (>= '0' and <= '9'))
                return false;
        }
        return true;
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
        WordPart.ArithSub arith => EmitArithSub(arith),
        WordPart.TildeSub ts => ts.User is null ? "$HOME" : $"~{ts.User}",
        WordPart.GlobPart gp => gp.Pattern,
        WordPart.BracedTuple bt => FormatBraceArray(new List<string>(bt.Items)),
        WordPart.BracedRange br => FormatBraceArray(ExpandBrace(br)),
        WordPart.ProcessSub ps => EmitProcessSub(ps),
        _ => throw new NotSupportedException($"Unknown word part type: {part.GetType().Name}"),
    };

    private static string EmitProcessSub(WordPart.ProcessSub ps)
    {
        string inner = Emit((Command)ps.Body);
        return $"(Invoke-ProcessSub {{ {inner} }})";
    }

    private static string EmitArithSub(WordPart.ArithSub arith)
    {
        string expr = arith.Expr.Trim();

        // Handle increment/decrement inside $((i++)) etc.
        // For env vars, side-effects can't happen inside $(), so emit as a
        // scriptblock that performs the side-effect and returns the value.
        if (expr.EndsWith("++") || expr.EndsWith("--"))
        {
            string varName = expr[..^2].Trim();
            string varRef = varName.StartsWith('$') ? varName : ArithVarRef(varName);
            bool isInc = expr.EndsWith("++");
            if (IsEnvVar(varRef))
            {
                // Post-increment: return old value, then increment.
                // $( $__t = [int]$env:i; $env:i = [string]($__t + 1); $__t )
                string delta = isInc ? "1" : "-1";
                return $"$( $__t = [int]{varRef}; {varRef} = [string]($__t + {delta}); $__t )";
            }
            return $"$({varRef}{expr[^2..]})";
        }
        if (expr.StartsWith("++") || expr.StartsWith("--"))
        {
            string varName = expr[2..].Trim();
            string varRef = varName.StartsWith('$') ? varName : ArithVarRef(varName);
            bool isInc = expr.StartsWith("++");
            if (IsEnvVar(varRef))
            {
                // Pre-increment: increment, then return new value.
                string delta = isInc ? "1" : "-1";
                return $"$( {varRef} = [string]([int]{varRef} + {delta}); [int]{varRef} )";
            }
            return $"$({expr[..2]}{varRef})";
        }

        expr = PrefixBareVar(expr);
        return $"$({expr})";
    }

    private static string EmitSimpleVar(string name, bool inDoubleQuote = false)
    {
        // Loop variables emit as $var, not $env:var
        if (_loopVars is not null && _loopVars.Contains(name))
            return $"${name}";

        return name switch
        {
            "null" or "true" or "false" or "HOME" or "LASTEXITCODE" => $"${name}",
            "?" => "$LASTEXITCODE",
            "@" or "*" => "$args",
            "#" => inDoubleQuote ? "$($args.Count)" : "$args.Count",
            "0" => inDoubleQuote ? "$($MyInvocation.MyCommand.Name)" : "$MyInvocation.MyCommand.Name",
            "$" => "$PID",
            "!" => "$PID", // bash $! = last background PID; no PS equivalent, $PID is approximate
            "-" => "$PSBoundParameters",
            var d when d.Length == 1 && d[0] is >= '1' and <= '9' =>
                inDoubleQuote ? $"$($args[{int.Parse(d) - 1}])" : $"$args[{int.Parse(d) - 1}]",
            _ => $"$env:{name}",
        };
    }

    private static string EmitBracedVar(WordPart.BracedVarSub bvs, bool inDoubleQuote = false)
    {
        if (bvs.Suffix is null)
            return EmitSimpleVar(bvs.Name);

        // Array subscript access: ${arr[0]}, ${arr[@]}, ${arr[*]}, ${arr[key]}
        if (bvs.Suffix.StartsWith('[') && bvs.Suffix.EndsWith(']'))
        {
            string subscript = bvs.Suffix[1..^1]; // strip [ and ]
            string arrayVar = $"${bvs.Name}";
            if (subscript is "@" or "*")
                return arrayVar; // ${arr[@]} -> $arr (whole array)
            // Numeric subscript: ${arr[0]} -> $arr[0] (or $($arr[0]) in double quotes)
            if (int.TryParse(subscript, out _))
                return inDoubleQuote ? $"$({arrayVar}[{subscript}])" : $"{arrayVar}[{subscript}]";
            // Associative key: ${map[key]} -> $map['key']
            return inDoubleQuote ? $"$({arrayVar}['{subscript}'])" : $"{arrayVar}['{subscript}']";
        }

        // Array length: ${#arr[@]} or ${#arr[*]} -> $arr.Count
        if (bvs.Suffix.StartsWith("#[") && bvs.Suffix.EndsWith(']'))
        {
            string subscript = bvs.Suffix[2..^1]; // strip #[ and ]
            if (subscript is "@" or "*")
                return inDoubleQuote ? $"$(${bvs.Name}.Count)" : $"${bvs.Name}.Count";
        }

        string varRef = EmitSimpleVar(bvs.Name);
        string open = inDoubleQuote ? "$(" : "(";
        char q = inDoubleQuote ? '\'' : '"';

        // Length: ${#VAR}
        if (bvs.Suffix == "#")
            return inDoubleQuote ? $"$({varRef}.Length)" : $"{varRef}.Length";

        // Default value: ${VAR:-default}
        if (bvs.Suffix.StartsWith(":-"))
        {
            string defaultVal = bvs.Suffix[2..];
            return $"{open}{varRef} ?? {q}{defaultVal}{q})";
        }

        // Assign default: ${VAR:=default}
        if (bvs.Suffix.StartsWith(":="))
        {
            string defaultVal = bvs.Suffix[2..];
            return $"{open}{varRef} ?? ({varRef} = {q}{defaultVal}{q}))";
        }

        // Use alternative: ${VAR:+alt}
        if (bvs.Suffix.StartsWith(":+"))
        {
            string alt = bvs.Suffix[2..];
            return $"{open}{varRef} ? {q}{alt}{q} : {q}{q})";
        }

        // Error if unset: ${VAR:?message}
        if (bvs.Suffix.StartsWith(":?"))
        {
            string msg = bvs.Suffix[2..];
            return $"{open}{varRef} ?? $(throw {q}{msg}{q}))";
        }

        // Remove suffix: ${VAR%%pattern} or ${VAR%pattern}
        if (bvs.Suffix.StartsWith("%%"))
        {
            string pattern = bvs.Suffix[2..];
            return $"{open}{varRef} -replace '{pattern}$','')";
        }
        if (bvs.Suffix.StartsWith("%"))
        {
            string pattern = bvs.Suffix[1..];
            return $"{open}{varRef} -replace '{pattern}$','')";
        }

        // Remove prefix: ${VAR##pattern} or ${VAR#pattern}
        if (bvs.Suffix.StartsWith("##"))
        {
            string pattern = bvs.Suffix[2..];
            return $"{open}{varRef} -replace '^{pattern}','')";
        }
        if (bvs.Suffix.StartsWith("#"))
        {
            string pattern = bvs.Suffix[1..];
            return $"{open}{varRef} -replace '^{pattern}','')";
        }

        // Array keys: ${!arr[@]} -> $arr.Keys (or $($arr.Keys) in double quotes)
        if (bvs.Suffix.StartsWith("!"))
            return inDoubleQuote ? $"$(${bvs.Name}.Keys)" : $"${bvs.Name}.Keys";

        // Replace first: ${VAR/find/replace}
        if (bvs.Suffix.StartsWith("/") && !bvs.Suffix.StartsWith("//"))
        {
            var parts = bvs.Suffix[1..].Split('/', 2);
            string find = parts[0], replace = parts.Length > 1 ? parts[1] : "";
            return $"{open}{varRef} -replace [regex]::Escape('{find}'),'{replace}')";
        }

        // Replace all: ${VAR//find/replace}
        if (bvs.Suffix.StartsWith("//"))
        {
            var parts = bvs.Suffix[2..].Split('/', 2);
            string find = parts[0], replace = parts.Length > 1 ? parts[1] : "";
            return $"{open}{varRef} -replace '{find}','{replace}')";
        }

        // Slice: ${VAR:offset:length} or ${VAR:offset}
        if (bvs.Suffix.StartsWith(":") && bvs.Suffix.Length > 1 && (char.IsDigit(bvs.Suffix[1]) || bvs.Suffix[1] == '-'))
        {
            var sliceParts = bvs.Suffix[1..].Split(':', 2);
            if (sliceParts.Length == 2 && int.TryParse(sliceParts[0], out int offset) && int.TryParse(sliceParts[1], out int length))
                return inDoubleQuote ? $"$({varRef}.Substring({offset}, {length}))" : $"{varRef}.Substring({offset}, {length})";
            if (int.TryParse(sliceParts[0], out int off2))
                return inDoubleQuote ? $"$({varRef}.Substring({off2}))" : $"{varRef}.Substring({off2})";
        }

        // Case conversion: ${VAR^^} ${VAR,,} ${VAR^} ${VAR,}
        if (bvs.Suffix == "^^")
            return inDoubleQuote ? $"$({varRef}.ToUpper())" : $"{varRef}.ToUpper()";
        if (bvs.Suffix == ",,")
            return inDoubleQuote ? $"$({varRef}.ToLower())" : $"{varRef}.ToLower()";
        if (bvs.Suffix == "^")
            return $"{open}{varRef}.Substring(0,1).ToUpper() + {varRef}.Substring(1))";
        if (bvs.Suffix == ",")
            return $"{open}{varRef}.Substring(0,1).ToLower() + {varRef}.Substring(1))";

        // Fallback: emit as-is
        return varRef;
    }

    private static string EmitDoubleQuoted(WordPart.DoubleQuoted dq)
    {
        var sb = new StringBuilder();
        sb.Append('"');
        for (int i = 0; i < dq.Parts.Length; i++)
        {
            var part = dq.Parts[i];
            if (part is WordPart.BracedVarSub bvs)
                sb.Append(EmitBracedVar(bvs, inDoubleQuote: true));
            else if (part is WordPart.SimpleVarSub vs)
            {
                // Check if next part starts with ':' — PowerShell would misparse as a drive reference.
                // Use ${name} bracing to prevent this.
                bool needsBracing = i + 1 < dq.Parts.Length && NextPartNeedsBracing(dq.Parts[i + 1]);
                if (needsBracing)
                    sb.Append(EmitSimpleVarBraced(vs.Name));
                else
                    sb.Append(EmitSimpleVar(vs.Name, inDoubleQuote: true));
            }
            else
                sb.Append(EmitWordPart(part));
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static bool NextPartNeedsBracing(WordPart part)
    {
        // ':' → PS drive reference ($env:PATH: misparse)
        // '.' → PS property access ($file.txt misparse)
        if (part is WordPart.Literal lit)
            return lit.Value.Length > 0 && (lit.Value[0] == ':' || lit.Value[0] == '.');
        if (part is WordPart.EscapedLiteral el)
            return el.Value == ":" || el.Value == ".";
        return false;
    }

    private static string EmitSimpleVarBraced(string name)
    {
        // Emit ${name} or ${env:name} with braces to prevent PS drive-reference misparse.
        if (_loopVars is not null && _loopVars.Contains(name))
            return $"${{{name}}}";

        return name switch
        {
            "null" or "true" or "false" or "HOME" or "LASTEXITCODE" => $"${{{name}}}",
            "?" => "${LASTEXITCODE}",
            "@" or "*" => "${args}",
            "#" => "$($args.Count)",
            "0" => "$($MyInvocation.MyCommand.Name)",
            "$" => "${PID}",
            "!" => "${PID}",
            "-" => "${PSBoundParameters}",
            var d when d.Length == 1 && d[0] is >= '1' and <= '9' =>
                $"$($args[{int.Parse(d) - 1}])",
            _ => $"${{env:{name}}}",
        };
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

            var cmd = andOr.Commands[i];
            // Wrap BoolExpr and ShAssignment in [void](...) when used in && / || chains
            // so PowerShell doesn't output the boolean result or assignment value.
            if (cmd is Command.BoolExpr)
            {
                sb.Append("[void]");
                sb.Append(Emit(cmd));
            }
            else if (cmd is Command.ShAssignment)
            {
                sb.Append("[void](");
                sb.Append(Emit(cmd));
                sb.Append(')');
            }
            else
            {
                sb.Append(Emit(cmd));
            }
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
            {
                sb.Append(mapped);
                // Append any redirects from the pipe target (TryEmitMappedCommand only handles args)
                EmitPipeTargetRedirects(simple, sb);
            }
            else
                sb.Append(Emit(cmd));
        }

        if (pipeline.Negated)
            sb.Append("; $global:LASTEXITCODE = if ($?) { 1 } else { 0 }");

        return sb.ToString();
    }

    private static void EmitPipeTargetRedirects(Command.Simple cmd, StringBuilder sb)
    {
        if (cmd.Redirects.IsEmpty) return;

        Redirect? fileRedirect = null;
        foreach (var redirect in cmd.Redirects)
        {
            var target = TransformRedirectTarget(EmitWord(redirect.Target));
            bool isStdoutFile = redirect.Fd == 1
                && (redirect.Op == ">" || redirect.Op == ">>")
                && target != "$null";

            if (isStdoutFile && fileRedirect is null)
                fileRedirect = redirect;
            else
            {
                sb.Append(' ');
                sb.Append(EmitRedirect(redirect));
            }
        }

        if (fileRedirect is not null)
        {
            var target = TransformRedirectTarget(EmitWord(fileRedirect.Target));
            sb.Append(" | Invoke-BashRedirect -Path ");
            sb.Append(target);
            if (fileRedirect.Op == ">>")
                sb.Append(" -Append");
        }
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
                result = EmitPassthrough("Invoke-BashGrep", args);
                return true;
            case "head":
                result = EmitPassthrough("Invoke-BashHead", args);
                return true;
            case "tail":
                result = EmitPassthrough("Invoke-BashTail", args);
                return true;
            case "wc":
                result = EmitPassthrough("Invoke-BashWc", args);
                return true;
            case "sort":
                result = EmitPassthrough("Invoke-BashSort", args);
                return true;
            case "uniq":
                result = EmitPassthrough("Invoke-BashUniq", args);
                return true;
            case "sed":
                result = EmitPassthrough("Invoke-BashSed", args);
                return true;
            case "awk":
                result = EmitPassthrough("Invoke-BashAwk", args);
                return true;
            case "cut":
                result = EmitPassthrough("Invoke-BashCut", args);
                return true;
            case "xargs":
                result = EmitPassthrough("Invoke-BashXargs", args);
                return true;
            case "tr":
                result = EmitPassthrough("Invoke-BashTr", args);
                return true;
            case "tee":
                result = EmitPassthrough("Invoke-BashTee", args);
                return true;
            case "echo":
                result = EmitPassthrough("Invoke-BashEcho", args);
                return true;
            case "printf":
                result = EmitPassthrough("Invoke-BashPrintf", args);
                return true;
            case "cat":
                result = EmitPassthrough("Invoke-BashCat", args);
                return true;
            case "ls":
                result = EmitPassthrough("Invoke-BashLs", args);
                return true;
            case "find":
                result = EmitPassthrough("Invoke-BashFind", args);
                return true;
            case "stat":
                result = EmitPassthrough("Invoke-BashStat", args);
                return true;
            case "cp":
                result = EmitPassthrough("Invoke-BashCp", args);
                return true;
            case "mv":
                result = EmitPassthrough("Invoke-BashMv", args);
                return true;
            case "rm":
                result = EmitPassthrough("Invoke-BashRm", args);
                return true;
            case "mkdir":
                result = EmitPassthrough("Invoke-BashMkdir", args);
                return true;
            case "rmdir":
                result = EmitPassthrough("Invoke-BashRmdir", args);
                return true;
            case "touch":
                result = EmitPassthrough("Invoke-BashTouch", args);
                return true;
            case "ln":
                result = EmitPassthrough("Invoke-BashLn", args);
                return true;
            case "ps":
                result = EmitPassthrough("Invoke-BashPs", args);
                return true;
            case "rev":
                result = EmitPassthrough("Invoke-BashRev", args);
                return true;
            case "nl":
                result = EmitPassthrough("Invoke-BashNl", args);
                return true;
            case "diff":
                result = EmitPassthrough("Invoke-BashDiff", args);
                return true;
            case "comm":
                result = EmitPassthrough("Invoke-BashComm", args);
                return true;
            case "column":
                result = EmitPassthrough("Invoke-BashColumn", args);
                return true;
            case "join":
                result = EmitPassthrough("Invoke-BashJoin", args);
                return true;
            case "paste":
                result = EmitPassthrough("Invoke-BashPaste", args);
                return true;
            case "jq":
                result = EmitPassthrough("Invoke-BashJq", args);
                return true;
            case "date":
                result = EmitPassthrough("Invoke-BashDate", args);
                return true;
            case "seq":
                result = EmitPassthrough("Invoke-BashSeq", args);
                return true;
            case "expr":
                result = EmitPassthrough("Invoke-BashExpr", args);
                return true;
            case "du":
                result = EmitPassthrough("Invoke-BashDu", args);
                return true;
            case "tree":
                result = EmitPassthrough("Invoke-BashTree", args);
                return true;
            case "env":
                result = EmitPassthrough("Invoke-BashEnv", args);
                return true;
            case "basename":
                result = EmitPassthrough("Invoke-BashBasename", args);
                return true;
            case "dirname":
                result = EmitPassthrough("Invoke-BashDirname", args);
                return true;
            case "pwd":
                result = EmitPassthrough("Invoke-BashPwd", args);
                return true;
            case "hostname":
                result = EmitPassthrough("Invoke-BashHostname", args);
                return true;
            case "whoami":
                result = EmitPassthrough("Invoke-BashWhoami", args);
                return true;
            case "fold":
                result = EmitPassthrough("Invoke-BashFold", args);
                return true;
            case "expand":
                result = EmitPassthrough("Invoke-BashExpand", args);
                return true;
            case "unexpand":
                result = EmitPassthrough("Invoke-BashUnexpand", args);
                return true;
            case "strings":
                result = EmitPassthrough("Invoke-BashStrings", args);
                return true;
            case "split":
                result = EmitPassthrough("Invoke-BashSplit", args);
                return true;
            case "tac":
                result = EmitPassthrough("Invoke-BashTac", args);
                return true;
            case "base64":
                result = EmitPassthrough("Invoke-BashBase64", args);
                return true;
            case "md5sum":
                result = EmitPassthrough("Invoke-BashMd5sum", args);
                return true;
            case "sha1sum":
                result = EmitPassthrough("Invoke-BashSha1sum", args);
                return true;
            case "sha256sum":
                result = EmitPassthrough("Invoke-BashSha256sum", args);
                return true;
            case "file":
                result = EmitPassthrough("Invoke-BashFile", args);
                return true;
            case "rg":
                result = EmitPassthrough("Invoke-BashRg", args);
                return true;
            case "gzip":
                result = EmitPassthrough("Invoke-BashGzip", args);
                return true;
            case "tar":
                result = EmitPassthrough("Invoke-BashTar", args);
                return true;
            case "yq":
                result = EmitPassthrough("Invoke-BashYq", args);
                return true;
            case "xan":
                result = EmitPassthrough("Invoke-BashXan", args);
                return true;
            case "sleep":
                result = EmitPassthrough("Invoke-BashSleep", args);
                return true;
            case "time":
                result = EmitPassthrough("Invoke-BashTime", args);
                return true;
            case "which":
                result = EmitPassthrough("Invoke-BashWhich", args);
                return true;
            case "alias":
                result = EmitPassthrough("Invoke-BashAlias", args);
                return true;
            case "trap":
                result = EmitPassthrough("Invoke-BashTrap", args);
                return true;
            case "readlink":
                result = EmitPassthrough("Invoke-BashReadlink", args);
                return true;
            case "mktemp":
                result = EmitPassthrough("Invoke-BashMktemp", args);
                return true;
            case "type":
                result = EmitPassthrough("Invoke-BashType", args);
                return true;
            case "bash":
                result = EmitPassthrough("Invoke-BashBash", args);
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

    private static string EmitPassthrough(string cmdlet, ImmutableArray<CompoundWord> args)
    {
        if (args.IsEmpty)
            return cmdlet;

        var sb = new StringBuilder(cmdlet);
        foreach (var arg in args)
        {
            sb.Append(' ');
            var emitted = EmitWord(arg);
            if (NeedsPassthroughQuoting(emitted))
                sb.Append('"').Append(emitted).Append('"');
            else
                sb.Append(emitted);
        }
        return sb.ToString();
    }

    private static bool NeedsPassthroughQuoting(string arg)
    {
        // Bare {} is an empty scriptblock in PowerShell — must be quoted to pass as string.
        if (arg == "{}")
            return true;
        // Flags like -F, contain commas which are PowerShell array separators.
        // Flags like -I{} contain braces which PowerShell parses as scriptblocks.
        // Flags like -t: contain colons which PowerShell parses as drive references or named param syntax.
        // Only applies to flag-style args starting with -.
        if (arg.Length < 2 || arg[0] != '-')
            return false;
        return arg.Contains(',') || arg.Contains('{') || arg.Contains('}') || arg.Contains(':');
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
