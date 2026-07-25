using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using PsBash.Core.Parser.Ast;
using PsBash.Core.Transpiler;

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

    /// <summary>
    /// Active transpile context for the current call. Set by
    /// <see cref="Transpile(string, TranspileContext)"/> and read by
    /// <see cref="EmitSimple"/> when deciding whether to bypass the
    /// <c>PsBuiltinAliases</c> short-circuit.
    /// </summary>
    [ThreadStatic]
    private static TranspileContext _context;

    /// <summary>
    /// Current transpile context. Defaults to <see cref="TranspileContext.Default"/>.
    /// </summary>
    public static TranspileContext Context => _context;

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
    /// Commands that have PowerShell built-in aliases. When the active
    /// <see cref="TranspileContext"/> is <see cref="TranspileContext.Default"/>,
    /// a future optimization may skip rewriting these as standalone invocations
    /// (relying on the host's alias). Under <see cref="TranspileContext.Eval"/>
    /// the short-circuit is always disabled so every mapped command emits as
    /// <c>Invoke-Bash*</c>, independent of the host's alias table.
    /// </summary>
    internal static readonly HashSet<string> PsBuiltinAliases = new(StringComparer.Ordinal)
    {
        "echo", "cat", "ls", "cd", "pwd", "mkdir",
        "cp", "mv", "rm", "sort", "diff", "sleep",
    };

    // ── Fused-pipeline lane (PERF phase 2) ───────────────────────────────────

    /// <summary>
    /// Line-oriented commands whose pipelines are eligible for the fused lane.
    /// Every stage of an all-mapped pipeline must be one of these for the emitter
    /// to wrap the whole pipeline in <c>Invoke-BashFusedPipeline { … }</c> (which
    /// batches the terminal flush — the phase-1 profile's dominant bottleneck).
    /// The runtime cmdlet runs the SAME <c>Invoke-Bash*</c> stages, so fidelity is
    /// guaranteed by construction; this set is intentionally the line-oriented
    /// text subset (no ls/find/awk/jq typed-object producers, whose boundary
    /// object shape must survive `ls | grep .txt`).
    /// </summary>
    internal static readonly HashSet<string> FusePipelineAllowlist = new(StringComparer.Ordinal)
    {
        "cat", "grep", "sed", "head", "tail", "wc",
        "sort", "uniq", "tr", "cut", "seq", "rev", "tac", "nl",
    };

    /// <summary>
    /// Depth of the current command / process substitution nesting. Fusion only
    /// applies to a terminal-bound pipeline (<c>_captureDepth == 0</c>): inside
    /// <c>$(…)</c> / <c>&lt;(…)</c> the output never crosses the IPC return path,
    /// so batching buys nothing and would change the captured object shape.
    /// </summary>
    [ThreadStatic]
    private static int _captureDepth;

    /// <summary>
    /// Emit a command whose output is CAPTURED (command / process substitution),
    /// not written to the terminal — suppresses fused-pipeline wrapping for the
    /// duration so nested pipelines keep today's per-object shape.
    /// </summary>
    private static string EmitCaptured(Command body)
    {
        _captureDepth++;
        try { return Emit(body); }
        finally { _captureDepth--; }
    }

    /// <summary>
    /// Test seam: force the fused lane on/off for the current thread, bypassing the
    /// <c>PSBASH_FUSED</c> env read. <c>[ThreadStatic]</c> so a test toggling it
    /// cannot race a fusable-pipeline transpile running on another xUnit thread.
    /// </summary>
    // Assigned only from the test assembly (InternalsVisibleTo); the compiler
    // can't see that cross-assembly, hence the CS0649 suppression.
#pragma warning disable CS0649
    [ThreadStatic]
    internal static bool? FusionEnabledOverride;
#pragma warning restore CS0649

    /// <summary>
    /// The fused lane is ON by default; <c>PSBASH_FUSED</c> set to a falsy token
    /// (<c>0</c> / <c>false</c> / <c>no</c> / <c>off</c>) disables detection so the
    /// old PowerShell-pipeline path is used. (Read here rather than via
    /// <c>PsBash.Core.Runtime.EnvFlags</c> because PsBash.Transpiler is a leaf
    /// project that must not reference PsBash.Core — it would be a reference cycle.)
    /// </summary>
    private static bool FusionEnabled()
    {
        if (FusionEnabledOverride is bool forced) return forced;
        return !IsFusionDisabledByEnvValue(
            Environment.GetEnvironmentVariable("PSBASH_FUSED"));
    }

    /// <summary>
    /// Pure kill-switch parse (env-value → disabled?), testable without touching
    /// the process environment. A null/empty/unrecognized value keeps the lane ON
    /// (default); only an explicit falsy token disables it.
    /// </summary>
    internal static bool IsFusionDisabledByEnvValue(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return false;
        return v.Equals("0", StringComparison.OrdinalIgnoreCase)
            || v.Equals("false", StringComparison.OrdinalIgnoreCase)
            || v.Equals("no", StringComparison.OrdinalIgnoreCase)
            || v.Equals("off", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when every stage of <paramref name="pipeline"/> maps to an allowlisted
    /// line-oriented command with no per-stage redirect / heredoc / env-prefix, the
    /// pipeline is a plain <c>|</c> chain (no <c>|&amp;</c>) of at least two stages,
    /// and it is not negated — the conditions under which
    /// <c>Invoke-BashFusedPipeline</c> is byte-identical to today's path.
    /// </summary>
    private static bool IsFusablePipeline(Command.Pipeline pipeline)
    {
        if (pipeline.Negated) return false;
        if (pipeline.Commands.Length < 2) return false;
        foreach (var op in pipeline.Ops)
            if (op != "|") return false; // |& stderr-merge falls back this slice
        foreach (var stage in pipeline.Commands)
        {
            if (stage is not Command.Simple s) return false;
            if (!s.Redirects.IsEmpty || !s.HereDocs.IsEmpty || !s.EnvPairs.IsEmpty)
                return false;
            var name = s.Words.IsEmpty ? null : GetLiteralValue(s.Words[0]);
            if (name is null || !FusePipelineAllowlist.Contains(name))
                return false;
            // Unbounded/streaming stage guard: the fused cmdlet runs the inner
            // pipeline via InvokeScript, which only returns after the pipeline
            // COMPLETES — so a never-ending stage (e.g. `tail -f`) would batch-buffer
            // forever and hang silently where the unfused lane streams live. Any such
            // stage forces the fallback. See StageIsUnbounded.
            if (StageIsUnbounded(name, s.Words))
                return false;
        }
        return true;
    }

    /// <summary>
    /// True when an allowlisted stage's args put it into an UNBOUNDED / never-terminating
    /// mode that the batched fused lane cannot serve (it buffers until the inner pipeline
    /// completes). The fused chain must never hang where the unfused chain streams, so any
    /// such stage forces the PowerShell-pipeline fallback.
    /// <para>
    /// General deny seam keyed by command so future allowlist additions inherit the check.
    /// Today only <c>tail</c> has an unbounded flag (<c>-f</c>/<c>-F</c>/<c>--follow</c>).
    /// For <c>tail</c>, a non-literal arg (variable / command-sub / glob) is treated as
    /// potentially <c>-f</c> and is conservatively unsafe — correctness (no hang) over the
    /// perf win on an uncommon shape.
    /// </para>
    /// </summary>
    private static bool StageIsUnbounded(string command, ImmutableArray<CompoundWord> words)
    {
        if (command != "tail") return false;

        // words[0] is the command name; scan the operands/flags after it.
        for (int i = 1; i < words.Length; i++)
        {
            var lit = GetLiteralValue(words[i]);
            if (lit is null)
                return true; // non-literal arg could expand to -f → be safe, fall back

            if (lit == "--follow" || lit.StartsWith("--follow=", StringComparison.Ordinal))
                return true;
            if (lit == "--")
                break; // end of flags — remaining tokens are operands, not -f

            // Short-flag group (`-f`, `-F`, `-qf`, `-fn`, …): any 'f'/'F' flag char = follow.
            if (lit.Length >= 2 && lit[0] == '-' && lit[1] != '-'
                && (lit.IndexOf('f') >= 0 || lit.IndexOf('F') >= 0))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Build the runtime <c>-Stages</c> array for a fusable pipeline — one
    /// <c>@('cmd', 'arg', …)</c> element per stage — or <c>null</c> when ANY stage
    /// carries a non-literal argument. Only compile-time-static args
    /// (<see cref="TryGetStaticArgValue"/>: literals, single/double-quoted literals,
    /// escaped chars) are emitted; each is a PS single-quoted literal so the value
    /// the streaming core receives is exactly what the unfused cmdlet's
    /// <c>Arguments</c> would get. A variable / command-sub / glob / brace-expansion
    /// arg returns null, so the pipeline falls back to the phase-2a scriptblock lane
    /// (which evaluates those correctly). Called only on an already-validated
    /// <see cref="IsFusablePipeline"/> pipeline (every stage is a mapped
    /// <see cref="Command.Simple"/>).
    /// </summary>
    private static string? TryBuildFusedStages(Command.Pipeline pipeline)
    {
        var stageStrs = new List<string>(pipeline.Commands.Length);
        foreach (var stageCmd in pipeline.Commands)
        {
            if (stageCmd is not Command.Simple s || s.Words.IsEmpty) return null;
            var name = GetLiteralValue(s.Words[0]);
            if (name is null) return null;
            var parts = new List<string>(s.Words.Length) { PsBuild.SingleQuote(name) };
            for (int i = 1; i < s.Words.Length; i++)
            {
                var val = TryGetStaticArgValue(s.Words[i]);
                if (val is null) return null; // non-literal arg → no stage list, use fallback
                // The fallback path runs the arg through TransformWordPath (operand
                // path rewrites: /tmp/… → $env:TEMP\…, MSYS drive paths). The stage
                // list carries the RAW literal, so if the transform would change the
                // value the two paths would diverge — decline (conservatively also for
                // a quoted path that the fallback would leave literal; correctness over
                // the perf win on that rare shape).
                if (TransformWordPath(val) != val) return null;
                parts.Add(PsBuild.SingleQuote(val));
            }
            stageStrs.Add("@(" + string.Join(", ", parts) + ")");
        }
        return "@(" + string.Join(", ", stageStrs) + ")";
    }

    public static string Emit(Command cmd) => cmd switch
    {
        Command.If ifCmd => EmitIf(ifCmd),
        Command.BoolExpr boolExpr => EmitBoolExpr(boolExpr),
        Command.ForIn forIn => EmitForIn(forIn),
        Command.ForArith forArith => EmitForArith(forArith),
        Command.Select sel => EmitSelect(sel),
        Command.While whileCmd => EmitWhile(whileCmd),
        Command.Case caseCmd => EmitCase(caseCmd),
        Command.ArithCommand arith => EmitArithCommand(arith),
        Command.Subshell subshell => EmitSubshell(subshell),
        Command.BraceGroup braceGroup => EmitBraceGroup(braceGroup),
        Command.ShFunction func => EmitFunction(func),
        Command.Background bg => EmitBackground(bg),
        Command.Simple simple => EmitSimple(simple),
        Command.Pipeline pipeline => EmitPipeline(pipeline),
        Command.AndOrList andOr => EmitAndOrList(andOr),
        Command.ShAssignment assign => EmitShAssignment(assign),
        Command.CommandList list => EmitCommandList(list),
        _ => throw new NotSupportedException($"Unknown command type: {cmd.GetType().Name}"),
    };

    /// <summary>
    /// Parse bash input and emit equivalent PowerShell using the
    /// <see cref="TranspileContext.Default"/> context.
    /// Returns null if the input is empty or whitespace-only.
    /// </summary>
    public static string? Transpile(string bash) => Transpile(bash, TranspileContext.Default);

    /// <summary>
    /// Parse bash input and emit equivalent PowerShell under the given
    /// <see cref="TranspileContext"/>. Returns null if the input is empty
    /// or whitespace-only.
    /// </summary>
    public static string? Transpile(string bash, TranspileContext context)
    {
        var prior = _context;
        _context = context;
        try
        {
            var cmd = BashParser.Parse(bash);
            if (cmd is null)
                return null;
            return Emit(cmd);
        }
        finally
        {
            _context = prior;
        }
    }

    /// <summary>
    /// Emit PowerShell for a pre-parsed AST under the given
    /// <see cref="TranspileContext"/>. Used by transpilers that need to
    /// emit per-statement while sharing a single context.
    /// </summary>
    public static string EmitWithContext(Command cmd, TranspileContext context)
    {
        var prior = _context;
        _context = context;
        try
        {
            return Emit(cmd);
        }
        finally
        {
            _context = prior;
        }
    }

    private static string? TryGetLastArgWord(Command cmd)
    {
        Command target = cmd;
        if (cmd is Command.CommandList list && list.Commands.Length > 0)
            target = list.Commands[^1];
        else if (cmd is Command.AndOrList andOr && andOr.Commands.Length > 0)
            target = andOr.Commands[^1];

        CompoundWord? lastWordObj = null;
        if (target is Command.Simple simple && simple.Words.Length > 0)
            lastWordObj = simple.Words[^1];
        else if (target is Command.Pipeline pipeline && pipeline.Commands.Length > 0 && pipeline.Commands[^1] is Command.Simple lastSimple && lastSimple.Words.Length > 0)
            lastWordObj = lastSimple.Words[^1];

        if (lastWordObj is null)
            return null;

        // Only track plain literal words to avoid breaking complex expressions
        if (lastWordObj.Parts.Length == 1 && lastWordObj.Parts[0] is WordPart.Literal lit)
            return $"'{SqEsc(lit.Value)}'";

        return null;
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

        return ApplyCompoundRedirects(sb.ToString(), ifCmd.Redirects);
    }

    private static string EmitForIn(Command.ForIn forIn)
    {
        int depth = _loopDepth++;
        try
        {
            var sb = new StringBuilder();
            sb.Append(IterGuardPrefix(depth));

            // Empty list means implicit $@ -> use BashPositional if set, else $args
            if (forIn.List.IsEmpty)
            {
                sb.Append($"foreach (${forIn.Var} in {PsBuild.BuildPositionalExpansion("@")}) {{ ");
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
            return ApplyCompoundRedirects(sb.ToString(), forIn.Redirects);
        }
        finally
        {
            _loopDepth--;
        }
    }

    /// <summary>
    /// True when a word expands to one element per positional parameter in a
    /// for-in list: bare <c>$@</c> / <c>$*</c> (both word-split) and quoted
    /// <c>"$@"</c> (each positional preserved). Quoted <c>"$*"</c> is excluded —
    /// it joins all positionals into a single word (IFS first char), so it must
    /// fall through to the normal stringified emission and iterate once.
    /// </summary>
    private static bool IsPositionalAllWord(CompoundWord w)
    {
        if (w.Parts.Length != 1)
            return false;
        return w.Parts[0] switch
        {
            WordPart.SimpleVarSub sv => sv.Name is "@" or "*",
            WordPart.DoubleQuoted dq when dq.Parts.Length == 1
                && dq.Parts[0] is WordPart.SimpleVarSub dsv => dsv.Name is "@",
            _ => false,
        };
    }

    /// <summary>
    /// True when a word is a whole-array expansion that iterates once per element
    /// in a for-in list: bare <c>${arr[@]}</c>/<c>${arr[*]}</c> and quoted
    /// <c>"${arr[@]}"</c> (each element preserved). Quoted <c>"${arr[*]}"</c> is
    /// excluded — it joins the array into one word (IFS), so it iterates once via
    /// the normal stringified path. Outputs the bare array name.
    /// </summary>
    private static bool IsArrayAllWord(CompoundWord w, out string arrayName)
    {
        arrayName = "";
        if (w.Parts.Length != 1)
            return false;
        if (w.Parts[0] is WordPart.BracedVarSub b && b.Suffix is "[@]" or "[*]")
        {
            arrayName = b.Name;
            return true;
        }
        if (w.Parts[0] is WordPart.DoubleQuoted dq && dq.Parts.Length == 1
            && dq.Parts[0] is WordPart.BracedVarSub qb && qb.Suffix == "[@]")
        {
            arrayName = qb.Name;
            return true;
        }
        return false;
    }

    private static string FormatForInList(ImmutableArray<CompoundWord> list)
    {
        if (list.Length == 1)
        {
            // `for x in "$@"` (or $@ / "$*" / $*) iterates once per positional
            // parameter, preserving spaces within each. EmitWord would render
            // "$@" as a double-quoted string that $OFS-joins the array into a
            // single space-separated word, so the loop would run once over the
            // whole join. Emit the positional array directly instead.
            if (IsPositionalAllWord(list[0]))
                return PsBuild.BuildPositionalExpansion("@");

            // `for x in "${arr[@]}"` (and bare ${arr[@]}/${arr[*]}) iterates per
            // element. Quoted "${arr[@]}" would otherwise stringify the array into
            // one space-joined word and run once. Emit the array variable directly.
            if (IsArrayAllWord(list[0], out var arrName))
                return "$" + arrName;

            var single = EmitWord(list[0]);
            if (HasGlobChars(single))
                // nullglob is OFF by default in bash: an unmatched glob iterates
                // ONCE with the literal pattern word, never zero times. Resolve-Path
                // errors + yields nothing on no-match (=> zero iterations), diverging.
                // Resolve-BashGlob expands matches but falls back to the literal word
                // when there is no match, matching bash.
                return $"(Resolve-BashGlob {single})";
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
            // nullglob-off + per-item fallback: Resolve-Path throws on the FIRST
            // non-matching operand and drops the ENTIRE list. Resolve-BashGlob
            // resolves each operand independently — expanding matches, passing an
            // unmatched glob (or a literal) through as the word itself — so one
            // non-matching item never nukes the rest of the list.
            var sb = new StringBuilder("(Resolve-BashGlob ");
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
        if (item.Length == 0) return "''";
        // Only a BARE literal needs quoting. An item EmitWord already rendered as a PS value —
        // a single/double-quoted string ('a\t1'), a variable ($x), a subexpression ($(…)/(…)),
        // or a brace-expansion array (@('a','')) — must pass through untouched; re-wrapping it
        // in '…' produced ''a\t1'' and '@('a','')', neither of which parses (grammar-fuzz find).
        char c = item[0];
        if (c is '$' or '"' or '\'' or '(' or '@') return item;
        if (double.TryParse(item, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out _))
            return item;
        return "'" + item.Replace("'", "''") + "'";
    }

    /// <summary>
    /// Degrades <c>select</c> (interactive menu loop) — which ps-bash does not
    /// implement — to a PowerShell BLOCK comment rather than aborting the whole
    /// transpile. A block comment (<c>&lt;# … #&gt;</c>) is inline-safe: unlike a
    /// <c>#</c> line comment it does not swallow following <c>;</c>-joined
    /// statements, so the rest of the script still transpiles.
    /// </summary>
    private static string EmitSelect(Command.Select sel)
        => $"<# ps-bash: 'select {sel.Var}' menu loop is not supported (omitted) #>";

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
                sb.Append(EmitForArithClause(forArith.Init, initializeLoopVar: loopVar));
                sb.Append("; ");
                sb.Append(EmitForArithCondition(forArith.Cond));
                sb.Append("; ");
                sb.Append(EmitForArithClause(forArith.Step, initializeLoopVar: null));
                sb.Append(") { ");
                sb.Append(IterGuardCheck(depth));
                sb.Append(Emit(forArith.Body));
                sb.Append(" }");
                return ApplyCompoundRedirects(sb.ToString(), forArith.Redirects);
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
        // Special case: while read VAR -> ForEach-Object pipeline (no infinite loop risk).
        // The classic `while read line; do ...; done < input.txt` idiom attaches its input
        // redirect here; ApplyCompoundRedirects feeds it in via Get-Content | & { $input | ... }.
        if (IsWhileRead(whileCmd.Cond, out var readVar))
            return ApplyCompoundRedirects(EmitWhileRead(readVar, whileCmd.Body), whileCmd.Redirects);

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
            return ApplyCompoundRedirects(sb.ToString(), whileCmd.Redirects);
        }
        finally
        {
            _loopDepth--;
        }
    }

    /// <summary>
    /// Normalize a raw case pattern (as sliced from the token stream, quotes and all)
    /// into a PowerShell <c>switch -Wildcard</c> clause pattern. Bash REMOVES the quote
    /// characters from a case pattern — <c>"foo"</c> matches the string <c>foo</c>, not
    /// the 5-char literal <c>"foo"</c> — but a glob metacharacter that was INSIDE quotes
    /// is literal, so it is backtick-escaped (the PowerShell wildcard escape, preserved
    /// verbatim inside the single-quoted clause). Glob metacharacters OUTSIDE quotes stay
    /// active (<c>"foo"*</c> → <c>foo*</c>). Backslash escapes the next char in both
    /// contexts. (Variable expansion inside a pattern — <c>$pat</c> — is a separate,
    /// larger change: the parser stores patterns as raw text, not words.)
    /// </summary>
    private static string NormalizeCasePattern(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        char quote = '\0';
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (quote != '\0')
            {
                if (c == quote) { quote = '\0'; continue; }      // drop closing quote
                if (quote == '"' && c == '\\' && i + 1 < raw.Length)
                {
                    AppendLiteralWildcardChar(sb, raw[++i]);      // "\x" → literal x
                    continue;
                }
                AppendLiteralWildcardChar(sb, c);                 // quoted glob char is literal
                continue;
            }
            if (c is '\'' or '"') { quote = c; continue; }        // drop opening quote
            if (c == '\\' && i + 1 < raw.Length)
            {
                AppendLiteralWildcardChar(sb, raw[++i]);          // \x → literal x
                continue;
            }
            sb.Append(c);                                          // unquoted: glob stays active
        }
        return sb.ToString();
    }

    private static void AppendLiteralWildcardChar(StringBuilder sb, char c)
    {
        // Backtick is the PowerShell -Wildcard escape; it survives verbatim inside the
        // single-quoted clause string the caller wraps this in.
        if (c is '*' or '?' or '[' or ']') sb.Append('`');
        sb.Append(c);
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
        sb.Append(EmitCaseExpr(caseCmd.Expr));
        sb.Append(") { ");

        for (int i = 0; i < caseCmd.Arms.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            var arm = caseCmd.Arms[i];
            var bodyText = EmitCaseArmBody(caseCmd.Arms, i);

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
                    sb.Append(SqEsc(NormalizeCasePattern(arm.Patterns[p])));
                    sb.Append("' { ");
                    sb.Append(bodyText);
                    sb.Append(" }");
                }
            }
        }

        sb.Append(" }");
        return ApplyCompoundRedirects(sb.ToString(), caseCmd.Redirects);
    }

    /// <summary>
    /// Effective body for case arm <paramref name="i"/>. For a <c>;&amp;</c>
    /// (fall-through) arm, bash runs this arm's body AND the next arm's body
    /// unconditionally; PowerShell's <c>switch</c> has no clause fall-through, so
    /// we inline the subsequent bodies, chaining while the terminator stays
    /// <see cref="CaseTerminator.FallThrough"/>. <c>;;</c> (Break) and <c>;;&amp;</c>
    /// (ContinueTest) arms emit just their own body — identical to the prior
    /// behavior, so non-fall-through cases are unchanged.
    ///
    /// NOTE on <c>;;&amp;</c> (ContinueTest): bash re-tests the remaining patterns
    /// after running the arm. We approximate this by emitting the arm body with
    /// NO PowerShell <c>break</c> — a <c>switch</c> clause without <c>break</c>
    /// already continues testing subsequent clauses. This matches bash for
    /// non-overlapping pattern sets (the common case); it can diverge only when
    /// multiple patterns match the same subject (e.g. overlapping wildcards),
    /// which is rare. A fully faithful rendering is deferred.
    /// </summary>
    private static string EmitCaseArmBody(
        System.Collections.Immutable.ImmutableArray<CaseArm> arms, int i)
    {
        var sb = new StringBuilder();
        int j = i;
        while (true)
        {
            if (j > i) sb.Append("; ");
            sb.Append(Emit(arms[j].Body));
            if (arms[j].Terminator == CaseTerminator.FallThrough && j + 1 < arms.Length)
                j++;
            else
                break;
        }
        // Bash runs only the first matching arm; PowerShell's switch runs EVERY
        // matching clause. Emit an explicit break so overlapping patterns don't
        // fire multiple arms. ;;& (ContinueTest) intentionally omits it (bash
        // re-tests remaining patterns — a switch clause with no break does too).
        if (arms[j].Terminator != CaseTerminator.ContinueTest)
            sb.Append("; break");
        return sb.ToString();
    }

    /// <summary>
    /// Emits the subject expression of a case/esac for use inside <c>switch (...)</c>.
    /// In bash a bare word in the case subject is a string literal, but PowerShell
    /// would treat an unquoted bare word as a command invocation.  This method wraps
    /// pure-literal subjects in single quotes so that <c>case hello in</c> becomes
    /// <c>switch ('hello')</c> rather than the broken <c>switch (hello)</c>.
    ///
    /// Expressions that are already PowerShell values — variable refs ($env:x),
    /// string literals starting with <c>"</c> or <c>'</c>, subexpressions starting
    /// with <c>(</c> or <c>$</c> — are forwarded unchanged.
    /// </summary>
    private static string EmitCaseExpr(CompoundWord expr)
    {
        var emitted = EmitWord(expr);
        // Already a PowerShell expression: variable, quoted string, subexpression.
        if (emitted.Length > 0 && (emitted[0] == '$' || emitted[0] == '"' || emitted[0] == '\'' || emitted[0] == '('))
            return emitted;
        // Bare literal — wrap in single quotes so PowerShell treats it as a string.
        return $"'{SqEsc(emitted)}'";
    }

    private static string EmitFunction(Command.ShFunction func)
    {
        var localVars = CollectLocalVars(func.Body);
        var vars = _loopVars ??= new HashSet<string>();
        var added = new List<string>();
        foreach (var v in localVars)
            if (vars.Add(v)) added.Add(v);

        try
        {
            var body = Emit(func.Body);
            // Wrap the function body with save/restore of $global:BashPositional so
            // that recursive calls each see their own positional args ($1, $2, $@, $#)
            // rather than the top-level caller's args. Without this, a recursive call
            // overwrites the global and the outer frame sees the inner frame's values.
            var sb = new StringBuilder("function ");
            sb.Append(func.Name);
            sb.Append(" { $__bp = $global:BashPositional; $global:BashPositional = @() + $args; try { ");
            sb.Append(body);
            sb.Append(" } finally { $global:BashPositional = $__bp } }");
            return sb.ToString();
        }
        finally
        {
            foreach (var v in added) vars.Remove(v);
        }
    }

    /// <summary>
    /// Recursively collect variable names declared with 'local' in a command tree.
    /// </summary>
    private static List<string> CollectLocalVars(Command cmd)
    {
        var result = new List<string>();
        CollectLocalVars(cmd, result);
        return result;
    }

    private static void CollectLocalVars(Command cmd, List<string> result)
    {
        switch (cmd)
        {
            case Command.ShAssignment assign when assign.IsLocal:
                foreach (var pair in assign.Pairs)
                {
                    int bracketIdx = pair.Name.IndexOf('[');
                    result.Add(bracketIdx >= 0 ? pair.Name[..bracketIdx] : pair.Name);
                }
                break;
            case Command.CommandList list:
                foreach (var child in list.Commands) CollectLocalVars(child, result);
                break;
            case Command.AndOrList andOr:
                foreach (var child in andOr.Commands) CollectLocalVars(child, result);
                break;
            case Command.Pipeline pipeline:
                foreach (var child in pipeline.Commands) CollectLocalVars(child, result);
                break;
            case Command.If ifCmd:
                foreach (var arm in ifCmd.Arms) CollectLocalVars(arm.Body, result);
                if (ifCmd.ElseBody is not null) CollectLocalVars(ifCmd.ElseBody, result);
                break;
            case Command.While whileCmd:
                CollectLocalVars(whileCmd.Body, result);
                break;
            case Command.ForIn forIn:
                CollectLocalVars(forIn.Body, result);
                break;
            case Command.ForArith forArith:
                CollectLocalVars(forArith.Body, result);
                break;
            case Command.Case caseCmd:
                foreach (var arm in caseCmd.Arms) CollectLocalVars(arm.Body, result);
                break;
            case Command.BraceGroup bg:
                CollectLocalVars(bg.Body, result);
                break;
            case Command.Subshell sub:
                CollectLocalVars(sub.Body, result);
                break;
            case Command.ShFunction fn:
                CollectLocalVars(fn.Body, result);
                break;
        }
    }

    private static string EmitSubshell(Command.Subshell subshell)
    {
        var sb = new StringBuilder("try { Push-Location; ");
        sb.Append(Emit(subshell.Body));
        sb.Append(" } finally { Pop-Location }");

        // Partition out the input redirect: `(cmd) < file` feeds the file to the
        // subshell's stdin and wraps the WHOLE result (`Get-Content file | & { … }`),
        // so it can't go through the shared stdout/other tail — without this it fell to
        // the raw `0<file` fallback ("'<' is reserved for future use"), common as
        // `(while read l; ...) < file`. Last input redirect wins (bash). Everything
        // else (stdout file + `2>&1` etc.) uses the shared AppendRedirectTail.
        Redirect? inputRedirect = null;
        var tailRedirects = new List<Redirect>(subshell.Redirects.Length);
        foreach (var redirect in subshell.Redirects)
        {
            if (redirect.Fd == 0 && redirect.Op == "<")
                inputRedirect = redirect;
            else
                tailRedirects.Add(redirect);
        }
        AppendRedirectTail(sb, tailRedirects);

        string result = sb.ToString();
        if (inputRedirect is not null)
        {
            var inTarget = TransformRedirectTarget(EmitWord(inputRedirect.Target));
            // /dev/null maps to $null; `Get-Content $null` errors, and empty stdin
            // is the same as no piped input, so skip the pipe in that case.
            if (inTarget != "$null")
                result = $"Get-Content {inTarget} | & {{ {result} }}";
        }

        return result;
    }

    private static string EmitBraceGroup(Command.BraceGroup braceGroup)
    {
        return ApplyCompoundRedirects(Emit(braceGroup.Body), braceGroup.Redirects);
    }

    private static string EmitBackground(Command.Background bg)
    {
        string inner = Emit(bg.Inner);
        return $"Invoke-BashBackground {{ {inner} }}";
    }

    /// <summary>
    /// Standalone <c>(( expr ))</c> statement. Bash evaluates the expression for
    /// its side effects and sets <c>$?</c> to 0 when the result is non-zero, 1
    /// otherwise. We evaluate via <c>Invoke-BashArith</c> (so <c>**</c>, integer
    /// division, bitwise/shift, etc. are correct) and set <c>$LASTEXITCODE</c>
    /// accordingly with no stdout — which also makes <c>(( … )) &amp;&amp; cmd</c>
    /// and <c>(( … )) || cmd</c> chains work. Positional/special-parameter
    /// expressions keep the legacy translation. The condition context
    /// (<c>if (( … ))</c> / <c>while (( … ))</c>) is handled in
    /// <see cref="EmitCondition"/> via <see cref="EmitArithConditionExpr"/>.
    /// </summary>
    private static string EmitArithCommand(Command.ArithCommand arith)
        => PsBuild.SilentExitFromBool($"({EmitArithmeticInvocation(arith.Expr)}) -ne 0");

    /// <summary>
    /// Boolean expression for <c>(( expr ))</c> used as an <c>if</c>/<c>while</c>
    /// condition: true iff the evaluated result is non-zero.
    /// </summary>
    private static string EmitArithConditionExpr(ArithmeticSyntax syntax)
        => $"(({EmitArithmeticInvocation(syntax)}) -ne 0)";

    private static string EmitArithCommandLegacy(string expr)
    {
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
        // The `read` variable is the loop binding for this construct: register
        // it as a loop var so the body emits it bare ($line, not $env:line) and
        // RC-7 word-splitting does not splat it (a `read` var holds a single
        // bound line, exactly like a for-loop variable).
        var vars = _loopVars ??= new HashSet<string>();
        bool added = vars.Add(varName);

        string bodyText;
        try
        {
            bodyText = Emit(body);
        }
        finally
        {
            if (added)
                vars.Remove(varName);
        }

        // Bind the read variable to the current line with a REAL PowerShell
        // assignment at the top of the loop body — `${VAR} = $_`. The body already
        // emits the read var bare (loop-var scope), so it resolves naturally. The
        // old approach text-rewrote every `$VAR` in the emitted body to `$_`, which
        // clobbered occurrences of the name inside single-quoted PS literals (e.g. a
        // bash `'has $line literal'` that must stay literal), corrupting output.
        // `$input |` is required: when this `while read` is a pipe target it is wrapped in `& { ... }`,
        // and a scriptblock's piped input does NOT auto-feed a leading `ForEach-Object` — it must be
        // drained explicitly via `$input`. Without it the chain got zero input and the leading
        // ForEach-Object ran once with a null `$_`, hitting "Cannot index into a null array" on the
        // property probe. The `$null -ne $_` guard makes that probe null-safe regardless.
        return $"$input | ForEach-Object {{ {PsBuild.NullSafeBashText} }} | ForEach-Object {{ ($_ -replace \"`n$\",\"\") -split \"`n\" }} | ForEach-Object {{ ${{{varName}}} = $_; {bodyText} }}";
    }

    private static string? ExtractArithVar(ArithmeticSyntax? init) =>
        init?.Root is ArithmeticExpr.Assignment assignment ? assignment.Name : null;

    private static string EmitForArithClause(ArithmeticSyntax? syntax, string? initializeLoopVar)
    {
        if (syntax is null) return "";
        string invocation = EmitArithmeticInvocation(syntax);
        return initializeLoopVar is null
            ? $"$null = {invocation}"
            : $"${{{initializeLoopVar}}} = $({invocation})";
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

    /// <summary>
    /// Condition clause of a C-style <c>for ((init; cond; step))</c> header.
    /// An empty clause is bash's infinite-true loop, and PowerShell treats an
    /// empty <c>for</c> condition the same, so it emits nothing. A non-empty
    /// clause is evaluated through <see cref="EmitArithConditionExpr"/> so the
    /// FULL C operator set is honored by <c>Invoke-BashArith</c> — in
    /// particular the <c>&lt;&lt;</c>/<c>&gt;&gt;</c> shift operators that the
    /// old naive <c>&lt;</c>/<c>&gt;</c> string-replace shredded (e.g.
    /// <c>i &lt; (n&lt;&lt;1)</c> became <c>[int]$env:n -lt -lt 1</c>, an
    /// invalid PowerShell parse). Mirrors how a standalone/if/while
    /// <c>(( … ))</c> condition is emitted.
    /// </summary>
    private static string EmitForArithCondition(ArithmeticSyntax? syntax) =>
        syntax is null ? "" : EmitArithConditionExpr(syntax);

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

        // AndOrList (cmd1 && cmd2 / cmd1 || cmd2): PowerShell's && / || are pipeline
        // chain operators and cannot appear inside if (...). Convert to -and / -or so
        // the condition is a proper PS boolean expression.
        if (cond is Command.AndOrList andOrCond)
            return EmitConditionAndOrList(andOrCond);

        // Negated pipeline (! cmd): the general EmitPipeline emits a semicolon-separated
        // statement which is invalid inside if (...). Wrap in a scriptblock so the
        // negated exit-code check is a proper boolean expression.
        // $global:LASTEXITCODE -ne 0 means the original command failed -> negation succeeds.
        if (cond is Command.Pipeline { Negated: true } negPipeline)
        {
            var unnegated = negPipeline with { Negated = false };
            return PsBuild.ExitCodeTest(EmitPipeline(unnegated), negate: true);
        }

        // A simple command or pipeline as a condition: bash tests its EXIT CODE, not its output.
        // The old `if (cmd)` form tested the command's OUTPUT truthiness, which is wrong for any
        // silent success — `grep -q`, `cmd >/dev/null` — which produce no output and so were always
        // treated as false. Route through the exit-code form already used for and-or-list conditions
        // (output is [void]-suppressed there too, so this is consistent with the existing convention).
        // (Subshell conditions are handled above; arith `(( ))` keeps its native-bool emission below.)
        if (cond is Command.Simple or Command.Pipeline)
            return EmitConditionAsExpr(cond);

        // Arithmetic (( expr )) as a condition: true iff the evaluated value is
        // non-zero. Routed through the bash-arithmetic evaluator so **, integer
        // division, bitwise/shift, etc. are correct (the old native emission
        // mistranslated them — e.g. `if (( 2**10 > 1000 ))` was a parse error).
        if (cond is Command.ArithCommand arithCmd)
            return EmitArithConditionExpr(arithCmd.Expr);

        return Emit(cond);
    }

    /// <summary>
    /// Converts a Command to a PowerShell expression that evaluates to a boolean,
    /// suitable for use inside if/while condition parentheses.
    /// </summary>
    private static string EmitConditionAsExpr(Command cmd)
    {
        // BoolExpr: already a PS boolean expression
        if (cmd is Command.BoolExpr boolExpr)
            return "(" + EmitBoolExprInner(boolExpr) + ")";

        // Bare true/false
        if (cmd is Command.Simple simple && simple.Words.Length == 1
            && simple.EnvPairs.IsEmpty && simple.Redirects.IsEmpty)
        {
            var word = GetLiteralValue(simple.Words[0]);
            if (word is "true") return "$true";
            if (word is "false") return "$false";
        }

        // Nested AndOrList: recurse
        if (cmd is Command.AndOrList nested)
            return EmitConditionAndOrList(nested);

        // Arithmetic (( expr )): non-zero result is true (see EmitCondition).
        if (cmd is Command.ArithCommand arithCmd)
            return EmitArithConditionExpr(arithCmd.Expr);

        // General command: run it and evaluate the exit code (bash semantics).
        // PsBuild.ExitCodeTest wraps the command in [void](...) so its output does not
        // pollute the boolean — without that the scriptblock returns the output objects
        // alongside the boolean and PowerShell reads the array as truthy.
        return PsBuild.ExitCodeTest(Emit(cmd), negate: false);
    }

    /// <summary>
    /// Emits a bash AndOrList used as an if/while condition as a PowerShell
    /// boolean expression using -and / -or instead of the pipeline-chain
    /// &amp;&amp; / || operators (which cannot appear inside if (...)).
    /// </summary>
    private static string EmitConditionAndOrList(Command.AndOrList andOr)
    {
        var sb = new StringBuilder("(");
        for (int i = 0; i < andOr.Commands.Length; i++)
        {
            if (i > 0)
            {
                var op = andOr.Ops[i - 1];
                sb.Append(op == "&&" ? " -and " : " -or ");
            }
            sb.Append(EmitConditionAsExpr(andOr.Commands[i]));
        }
        sb.Append(')');
        return sb.ToString();
    }

    // A BoolExpr ([ ... ] / [[ ... ]]) as its OWN statement: bash `[ x ]` is silent and sets only the
    // exit code. Emitting the bare `(expr)` boolean here leaked "True"/"False" to stdout and never set
    // $LASTEXITCODE. Emit a silent form that sets the exit code instead (mirrors the &&/|| chain form).
    // Callers needing the bare boolean expression (conditions, && / || chains) use EmitBoolExprInner.
    private static string EmitBoolExpr(Command.BoolExpr expr)
    {
        return PsBuild.SilentExitFromBool("(" + EmitBoolExprInner(expr) + ")");
    }

    private static string EmitBoolExprInner(Command.BoolExpr expr)
    {
        if (expr.Extended)
            return EmitExtendedTest(expr.Inner);
        return TranslateTestCondition(expr.Inner, extended: false);
    }

    // The full GNU set of unary file-test operators. Any of these in a `[ -X file ]` /
    // `[[ -X file ]]` unary position must map to a valid PowerShell boolean (see the switch
    // in TranslateTestCondition). `-z`/`-n` are string tests handled separately.
    private static readonly System.Collections.Generic.HashSet<string> UnaryFileTestOps = new(System.StringComparer.Ordinal)
    {
        "-e", "-a", "-f", "-d", "-r", "-w", "-x", "-s", "-h", "-L",
        "-b", "-c", "-p", "-S", "-g", "-u", "-k", "-t", "-N", "-O", "-G",
    };

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
            // Wrap each segment in parens so that -and / -or are not parsed as
            // additional flags to the preceding cmdlet (e.g. Test-Path ... -and ...).
            sb.Append('(');
            sb.Append(TranslateTestCondition(segments[i].Words, extended: true));
            sb.Append(')');
        }
        return sb.ToString();
    }

    private static string TranslateTestCondition(ImmutableArray<CompoundWord> words, bool extended)
    {
        // Handle leading ! negation: [ ! -f x ] → !(Test-Path ...)
        if (words.Length >= 1 && GetLiteralValue(words[0]) == "!")
        {
            var inner = words.RemoveAt(0);
            return $"!({TranslateTestCondition(inner, extended)})";
        }

        // POSIX `[ ]` combinators `-a` (AND) / `-o` (OR): split the clause list on a
        // top-level operator and recurse per clause, joining with -and / -or. A single
        // unary/binary clause is ≤3 words, so only >3 words can hold a combinator; an
        // operator at index 0 is the unary `-a` file-exists test, not AND (handled
        // below). Without this, `[ x = y -a c = d ]` fell through to the bare-operand
        // fallback and emitted adjacent quoted strings — invalid PowerShell.
        // Precedence: -o binds looser than -a, so split on -o first.
        if (!extended && words.Length > 3)
        {
            if (TrySplitTestCombinator(words, "-o", "-or", out var orResult)) return orResult;
            if (TrySplitTestCombinator(words, "-a", "-and", out var andResult)) return andResult;
        }

        if (words.Length >= 2)
        {
            var flag = GetLiteralValue(words[0]);
            // String tests.
            if (flag == "-z")
                return $"[string]::IsNullOrEmpty({EmitTestOperand(words[1])})";
            if (flag == "-n")
                return $"-not [string]::IsNullOrEmpty({EmitTestOperand(words[1])})";

            // Unary file tests. Bash file tests have no faithful cross-platform PowerShell
            // equivalent for POSIX permission/mode bits, so these are best-effort:
            //   - existence-class ops (-e -a -f -d -r -w -x -O -G) -> Test-Path
            //   - -s -> exists and non-empty
            //   - -h/-L -> reparse-point (symlink) check
            //   - special-file / mode-bit / terminal ops -> $false (Windows has no equivalent)
            // Crucially, EVERY recognized unary op maps to a valid PowerShell boolean so the
            // old broken fallback ('-x' $f) — two adjacent tokens, a parse error — never fires.
            if (flag is not null && UnaryFileTestOps.Contains(flag))
            {
                // Wrap a BARE literal in double quotes (`Test-Path "dir"`), but pass an operand
                // EmitWord already rendered as a PS value — a quoted string ("$env:file"), a
                // variable, or a subexpression — through unchanged. The old code blindly wrapped
                // every operand, so `[ -f "$file" ]` (extremely common) became `""$env:file""`,
                // unparseable PowerShell (grammar-fuzz find). Passing the quoted form through also
                // keeps the variable expanding, which single-quoting would have suppressed.
                var operand = EmitWord(words[1]);
                var path = operand.Length > 0 && operand[0] is '"' or '\'' or '$' or '('
                    ? operand
                    : $"\"{operand}\"";
                return flag switch
                {
                    "-f" => $"Test-Path {path} -PathType Leaf",
                    "-d" => $"Test-Path {path} -PathType Container",
                    "-s" => $"((Test-Path {path} -PathType Leaf) -and ((Get-Item -LiteralPath {path} -Force -ErrorAction SilentlyContinue).Length -gt 0))",
                    "-h" or "-L" => $"([bool]((Get-Item -LiteralPath {path} -Force -ErrorAction SilentlyContinue).Attributes -band [System.IO.FileAttributes]::ReparsePoint))",
                    "-b" or "-c" or "-p" or "-S" or "-g" or "-u" or "-k" or "-t" or "-N" => "$false",
                    // -e -a -r -w -x -O -G and any remaining existence-class op.
                    _ => $"Test-Path {path}",
                };
            }
        }

        if (words.Length == 3)
        {
            var lhs = EmitTestOperand(words[0]);
            var op = GetLiteralValue(words[1]);

            // Glob/regex RHS build their own single-quoted pattern from the
            // stripped literal; everything else compares two quoted operands.
            if (op == "=~")
            {
                // `re='^a.b$'; [[ $x =~ $re ]]` is THE idiomatic way to write a bash
                // regex (it avoids bash's own quoting pitfalls). Single-quoting the
                // emitted `$env:re` would match the literal text "$env:re" instead of
                // the pattern it holds, so pass a lone expansion through bare.
                if (IsSoleExpansionWord(words[2]))
                    return $"{lhs} -match {EmitWord(words[2])}";
                return $"{lhs} -match '{SqEsc(StripQuotes(EmitWord(words[2])))}'";
            }

            if (op is "==" or "=")
            {
                var unquoted = StripQuotes(EmitWord(words[2]));
                if (HasGlobChars(unquoted))
                    return $"{lhs} -like '{SqEsc(unquoted)}'";
                return $"{lhs} -eq {EmitTestOperand(words[2])}";
            }

            // In [[ ]], < and > are lexicographic string comparisons.
            // In [ ], they don't appear (bash uses -lt/-gt for numeric).
            if (extended && op is "<" or ">")
            {
                var cmpOp = op == "<" ? "-lt" : "-gt";
                return $"[string]::Compare({lhs}, {EmitTestOperand(words[2])}, [System.StringComparison]::Ordinal) {cmpOp} 0";
            }

            // Numeric comparison operators are ALWAYS integer comparisons in bash
            // (-eq -ne -lt -le -gt -ge). Without an explicit cast PowerShell compares
            // by the LHS's runtime type — and an env-backed operand ($env:x) is a
            // String, so `'10' -gt 9` coerces 9 to "9" and does a LEXICOGRAPHIC
            // compare ('10' sorts before '9'), silently taking the wrong branch at 10.
            // Cast both operands to [long] so the compare is numeric like bash. (The
            // string operators == / = / != are handled above/below and stay string.)
            if (op is "-eq" or "-ne" or "-lt" or "-le" or "-gt" or "-ge")
                return $"[long]({lhs}) {op} [long]({EmitTestOperand(words[2])})";

            var psOp = op switch
            {
                "!=" => "-ne",
                "<" => "-lt",
                ">" => "-gt",
                _ => op,
            };
            return $"{lhs} {psOp} {EmitTestOperand(words[2])}";
        }

        // Fallback (e.g. `[ str ]` = non-empty test): emit each operand quoted so
        // a bare literal is a PowerShell string, not a command invocation.
        var sb = new StringBuilder();
        for (int i = 0; i < words.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(EmitTestOperand(words[i]));
        }
        return sb.ToString();
    }

    // Split a `[ ]` clause list on a top-level POSIX combinator (`-a` / `-o`) and
    // recurse on each clause, joining with the PowerShell logical op (-and / -or).
    // Only separators at index 1..len-2 count (operands on both sides); an operator
    // at the edge is a unary file test, not a combinator. Returns false if the token
    // does not appear in a separator position, so the caller falls through to the
    // single-clause translation.
    private static bool TrySplitTestCombinator(
        ImmutableArray<CompoundWord> words, string token, string psOp, out string result)
    {
        result = string.Empty;
        var clauses = new List<ImmutableArray<CompoundWord>>();
        int start = 0;
        for (int i = 1; i < words.Length - 1; i++)
        {
            if (GetLiteralValue(words[i]) == token)
            {
                clauses.Add(ImmutableArray.CreateRange(words.Skip(start).Take(i - start)));
                start = i + 1;
            }
        }
        if (clauses.Count == 0) return false; // no separator → not a combinator here
        clauses.Add(ImmutableArray.CreateRange(words.Skip(start)));

        var sb = new StringBuilder();
        for (int i = 0; i < clauses.Count; i++)
        {
            if (i > 0) { sb.Append(' '); sb.Append(psOp); sb.Append(' '); }
            sb.Append('(');
            sb.Append(TranslateTestCondition(clauses[i], extended: false));
            sb.Append(')');
        }
        result = sb.ToString();
        return true;
    }

    /// <summary>
    /// Emit a <c>[ ]</c>/<c>[[ ]]</c> operand as a PowerShell value. A bare
    /// string literal (<c>abc</c>) MUST be single-quoted or PowerShell treats it
    /// as a command to invoke (<c>[ abc = abc ]</c> → <c>abc -eq abc</c> →
    /// "command not found"). Variable references (<c>$env:x</c>), already-quoted
    /// strings, subexpressions, and pure integers (for numeric <c>-eq</c>/<c>-lt</c>)
    /// pass through unquoted. Builds on <see cref="EmitTestArg"/> (which unwraps a
    /// double-quoted word and quotes it if it is a bare literal).
    /// </summary>
    private static string EmitTestOperand(CompoundWord word)
        => QuoteIfBareLiteral(EmitTestArg(word));

    /// <summary>
    /// True when the word is exactly ONE parameter/command expansion and nothing else
    /// (<c>$re</c>, <c>${re}</c>, <c>$(cmd)</c>) — so its emitted text is already a
    /// PowerShell expression and must not be wrapped in a single-quoted literal.
    /// A mixed word (<c>^${p}[0-9]+$</c>) is not sole and keeps the literal path.
    /// </summary>
    private static bool IsSoleExpansionWord(CompoundWord word)
        => word.Parts.Length == 1
        && word.Parts[0] is WordPart.SimpleVarSub
            or WordPart.BracedVarSub
            or WordPart.CommandSub;

    private static string QuoteIfBareLiteral(string emitted)
    {
        if (emitted.Length == 0) return "''";
        char c = emitted[0];
        // $var / "already" / 'already' / (subexpr) / @(array) are valid PS values.
        if (c is '$' or '"' or '\'' or '(' or '@') return emitted;
        // A pure integer stays bare so numeric -eq/-ne/-lt/-gt compare as numbers.
        if (long.TryParse(emitted, System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out _))
            return emitted;
        return "'" + emitted.Replace("'", "''") + "'";
    }

    /// <summary>
    /// Emit a word used as a test expression argument, unwrapping outer double quotes
    /// so that variable references appear bare (e.g. <c>$HOME</c> not <c>"$HOME"</c>).
    /// String literals must be single-quoted so PowerShell doesn't treat them as commands.
    /// </summary>
    private static string EmitTestArg(CompoundWord word)
    {
        if (word.Parts.Length == 1 && word.Parts[0] is WordPart.DoubleQuoted dq)
        {
            var sb = new StringBuilder();
            foreach (var part in dq.Parts)
                sb.Append(EmitWordPart(part));
            var inner = sb.ToString();
            // Variable references (start with $) are valid PS expressions as-is.
            // Bare literals must be quoted or PowerShell treats them as commands.
            if (inner.Length == 0 || inner[0] != '$')
                return "'" + inner.Replace("'", "''") + "'";
            return inner;
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

            // PROMPT_COMMAND='CMD' or PROMPT_COMMAND="CMD" ->
            //   Register-BashPromptHook -Name 'prompt-command' -ScriptBlock { Invoke-Expression 'CMD_transpiled' }
            // Only intercept plain (non-local, non-append) assignments with a value.
            if (!cmd.IsLocal && pair.Name == "PROMPT_COMMAND"
                && pair.Op == AssignOp.Equal && pair.Value is not null && pair.ArrayValue is null)
            {
                var rawCmd = ExtractSingleQuotedOrLiteralValue(pair.Value);
                if (rawCmd is not null)
                {
                    var transpiledCmd = Transpile(rawCmd) ?? rawCmd;
                    var scriptBody = transpiledCmd.Replace("'", "''");
                    sb.Append($"Register-BashPromptHook -Name 'prompt-command' -ScriptBlock {{ Invoke-Expression '{scriptBody}' }}");
                }
                else
                {
                    // Value is complex (variable refs, command subs) — emit as env var assignment;
                    // the hook registry call requires a static command string.
                    sb.Append("$env:PROMPT_COMMAND = ");
                    sb.Append(EmitAssignmentValue(pair.Value));
                }
                continue;
            }

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
                    // Emit each element as a properly-quoted PS value expression. Naively
                    // wrapping EmitWord() in single quotes stored the literal string "$env:x"
                    // for a "$x" element and produced unbalanced quotes for $'a\'b' elements.
                    sb.Append(EmitAssignmentValue(elem));
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
                sb.Append("'] = ");
                // Emit the value as a properly-quoted PS expression; single-quoting
                // EmitWord() stored the literal "$env:x" for a $x value.
                sb.Append(EmitAssignmentValue(pair.Value));
                continue;
            }

            sb.Append(varPrefix);
            sb.Append(pair.Name);
            if (pair.Value is null)
            {
                // Bare export/local VAR with no value: ensure variable exists
                sb.Append(" = ");
                sb.Append(varPrefix);
                sb.Append(pair.Name);
            }
            else
            {
                sb.Append(pair.Op == AssignOp.PlusEqual ? " += " : " = ");
                sb.Append(EmitAssignmentValue(pair.Value));
            }
        }
        return sb.ToString();
    }

    private static string EmitSimple(Command.Simple cmd)
    {
        if (TryEmitEnvPairsWrap(cmd, out var envWrapResult))
            return envWrapResult;

        if (TryEmitHereDoc(cmd, out var hereDocResult))
            return hereDocResult;

        if (TryEmitStderrRedirect(cmd, out var stderrResult))
            return stderrResult;

        if (TryEmitInputRedirect(cmd, out var inputResult))
            return inputResult;

        if (TryEmitDeclare(cmd, out var declareResult))
            return declareResult;

        if (TryEmitSpecialBuiltins(cmd, out var builtinResult))
            return builtinResult;

        return EmitGeneralCommand(cmd);
    }

    // Env-var prefix (`VAR=val cmd`) must wrap the ENTIRE command — including
    // the mapped (Invoke-Bash*), heredoc, and special-form paths, all of which
    // return early below. The save/set/restore block further down only wraps
    // the general fallback, so `IFS=, read ...`, `X=1 cat <<< ...`, and any
    // `VAR=val <mapped-command>` silently DROPPED the assignment. Strip the
    // pairs, re-emit the bare command through every path, then wrap once.
    private static bool TryEmitEnvPairsWrap(Command.Simple cmd, out string result)
    {
        if (cmd.EnvPairs.IsEmpty)
        {
            result = "";
            return false;
        }

        var bare = cmd with { EnvPairs = ImmutableArray<EnvPair>.Empty };
        result = WrapEnvPairs(cmd.EnvPairs, EmitSimple(bare));
        return true;
    }

    // Heredoc: emit as @"body"@ | cmd (or @'body'@ for no-expand).
    private static bool TryEmitHereDoc(Command.Simple cmd, out string result)
    {
        if (cmd.HereDocs.IsDefaultOrEmpty)
        {
            result = "";
            return false;
        }

        // When multiple heredocs exist, use the last one for stdin (bash behavior).
        var hereDoc = cmd.HereDocs[^1];
        var innerCmd = new Command.Simple(cmd.Words, cmd.EnvPairs, cmd.Redirects);
        string body = hereDoc.Body;
        // <<-EOF: strip leading tabs from each line (bash semantics).
        if (hereDoc.StripTabs)
            body = StripLeadingTabs(body);
        if (hereDoc.Expand)
            body = TranslateHereDocVars(body);
        // A PowerShell here-string ends at the first line that begins with its
        // terminator (`"@` for @"..."@, `'@` for @'...'@). A heredoc body
        // containing such a line would close the string early — bash prints
        // the line literally, ps-bash threw "missing terminator". When that
        // collision is present fall back to an ordinary quoted string (whose
        // value is identical to the here-string content); otherwise keep the
        // here-string, which is the readable common-case form.
        string hereString = HereStringWouldBreak(body, hereDoc.Expand)
            ? (hereDoc.Expand
                ? "\"" + EscapeForDoubleQuotedHereDocBody(body) + "\""
                : PsBuild.SingleQuote(body))
            : (hereDoc.Expand
                ? $"@\"\n{body}\n\"@"
                : $"@'\n{body}\n'@");
        string cmdText = EmitSimple(innerCmd);
        result = $"{hereString} | Emit-BashLine | {cmdText}";
        return true;
    }

    // Stdout-to-stderr redirects (>&2 or 1>&2). REFACTOR-4: route through
    // Write-BashHostStderr, which writes into the host's STDERR-tagged IPC
    // frame, instead of [Console]::Error.WriteLine. The host's inherited
    // fd 2 is detached to /dev/null (commit cc8bf88's hang fix), so a direct
    // [Console]::Error write would be silently lost — all host output must
    // travel the single IPC channel the launcher drains.
    private static bool TryEmitStderrRedirect(Command.Simple cmd, out string result)
    {
        var stderrRedirect = cmd.Redirects.FirstOrDefault(r =>
            r.Op == ">&" && r.Fd == 1 && GetLiteralValue(r.Target) == "2");
        if (stderrRedirect is null)
        {
            result = "";
            return false;
        }

        var remaining = cmd.Redirects.Remove(stderrRedirect);
        var innerCmd = new Command.Simple(cmd.Words, cmd.EnvPairs, remaining);
        result = $"{EmitSimple(innerCmd)} | ForEach-Object {{ Write-BashHostStderr $_ }}";
        return true;
    }

    // Input redirects (< file) become "Get-Content file | cmd".
    // Special case: `< /dev/null` means "no input" — `Get-Content $null`
    // throws in PowerShell, so just drop the redirect (the command runs
    // with whatever stdin it would otherwise have).
    private static bool TryEmitInputRedirect(Command.Simple cmd, out string result)
    {
        var inputRedirect = cmd.Redirects.FirstOrDefault(r => r.Op == "<");
        if (inputRedirect is null)
        {
            result = "";
            return false;
        }

        var remaining = cmd.Redirects.Remove(inputRedirect);
        var innerCmd = new Command.Simple(cmd.Words, cmd.EnvPairs, remaining);
        var target = TransformRedirectTarget(EmitWord(inputRedirect.Target));
        result = target == "$null" ? EmitSimple(innerCmd) : $"Get-Content {target} | {EmitSimple(innerCmd)}";
        return true;
    }

    // declare/typeset -A map -> $map = @{} (associative array / hashtable declaration)
    // declare/typeset -a arr -> $arr = @() (indexed array declaration)
    private static bool TryEmitDeclare(Command.Simple cmd, out string result)
    {
        result = "";
        if (cmd.Words.Length < 2)
            return false;

        var name = GetLiteralValue(cmd.Words[0]);
        if (name is not ("declare" or "typeset"))
            return false;

        bool isAssoc = false;
        bool isPrint = false;
        string? varName = null;
        foreach (var word in cmd.Words.Skip(1))
        {
            var val = GetLiteralValue(word);
            if (val == "-A") isAssoc = true;
            else if (val == "-i") { /* integer — handled below */ }
            else if (val == "-p" || val == "-f" || val == "-F") isPrint = true;
            else if (val is not null && !val.StartsWith('-')) varName = val;
        }
        if (isPrint)
        {
            result = EmitPassthrough("Invoke-BashType", cmd.Words.Skip(1).ToImmutableArray());
            return true;
        }
        if (varName is null)
            return false;

        // An initializer (`declare -i n=5`, `declare x=hello`) arrives as a
        // single literal `name=value`; split it so we never emit the broken
        // `[int]$global:n=5 = 0`. No `=` → the bare-declaration defaults below.
        int eq = varName.IndexOf('=');
        bool isInt = cmd.Words.Skip(1).Any(w => GetLiteralValue(w) == "-i");
        if (eq >= 0)
        {
            string declName = varName[..eq];
            string rawVal = varName[(eq + 1)..];
            if (isAssoc)
            {
                result = "$global:" + declName + " = @{}";
                return true;
            }
            if (isInt)
            {
                // Integer attribute: bash evaluates the RHS arithmetically
                // (`declare -i n=2+3` -> 5, `declare -i n=x*2` reads $x). A plain
                // integer literal is emitted directly; any expression routes through
                // the shared arithmetic evaluator instead of silently collapsing to 0.
                string intVal = long.TryParse(rawVal, out _)
                    ? rawVal
                    : $"(Invoke-BashArith {PsBuild.SingleQuote(rawVal)})";
                result = "[int]$global:" + declName + " = " + intVal;
                return true;
            }
            result = "$global:" + declName + " = " + PsBuild.SingleQuote(rawVal);
            return true;
        }
        if (isAssoc)
        {
            result = "$global:" + varName + " = @{}";
            return true;
        }
        result = isInt ? "[int]$global:" + varName + " = 0" : "$global:" + varName + " = @()";
        return true;
    }

    // Dispatch ladder for the bash-keyword / builtin branches that used to live
    // in one ~200-line else-if chain inside EmitSimple. Order is preserved
    // exactly (it is behavior): `unset`/`trap DEBUG` return immediately (outside
    // the else-if chain, same as before) so other `unset`/`trap` forms still fall
    // through to TryEmitMappedCommand; everything else sets specialResult.
    private static bool TryEmitSpecialBuiltins(Command.Simple cmd, out string result)
    {
        result = "";
        if (cmd.Words.Length < 1)
            return false;

        var cmd0 = GetLiteralValue(cmd.Words[0]);

        // unset PROMPT_COMMAND -> Unregister-BashPromptHook -Name 'prompt-command'
        // Checked early (outside the else-if chain) so that `unset OTHER_VAR` still
        // falls through to TryEmitMappedCommand -> Invoke-BashUnset.
        if (cmd0 == "unset" && cmd.Words.Length >= 2
            && GetLiteralValue(cmd.Words[1]) == "PROMPT_COMMAND")
        {
            result = "Unregister-BashPromptHook -Name 'prompt-command'";
            return true;
        }

        // trap 'CMD' DEBUG  -> Register-BashChpwdHook with first-use warning
        // trap - DEBUG      -> Unregister-BashChpwdHook for the previously registered hook
        // Checked early (outside the else-if chain) so that `trap 'CMD' EXIT` and
        // other non-DEBUG signals still fall through to TryEmitMappedCommand -> Invoke-BashTrap.
        if (cmd0 == "trap" && cmd.Words.Length >= 3
            && GetLiteralValue(cmd.Words[^1]) == "DEBUG")
        {
            var trapArgs = cmd.Words.Skip(1).Take(cmd.Words.Length - 2).ToList();
            var rawCmd = string.Join(" ", trapArgs.Select(w =>
                ExtractSingleQuotedOrLiteralValue(w) ?? EmitWord(w)));
            var firstArg = GetLiteralValue(cmd.Words[1]);
            if (firstArg == "-")
            {
                result = EmitTrapDebugUnregister(rawCmd);
                return true;
            }
            var transpiledCmd = Transpile(rawCmd) ?? rawCmd;
            result = EmitTrapDebugRegister(rawCmd, transpiledCmd);
            return true;
        }

        string? specialResult = null;

        // return N -> capture exit code for $?
        if (cmd0 == "return")
        {
            if (cmd.Words.Length >= 2)
            {
                var retCode = EmitWord(cmd.Words[1]);
                specialResult = $"$global:LASTEXITCODE = {retCode}; return";
            }
            else
            {
                specialResult = "return";
            }
        }
        // true -> no-op success; false -> silent failure (sets $? = $false for && / ||)
        // Wrapped in $(...) so the multi-statement body parses as a single
        // expression — required when used as an operand of `||` or `&&`,
        // e.g. `cmd || true` would otherwise emit `cmd || $g:LASTEXITCODE = 0; ...`
        // and the `||` would only consume `$g:LASTEXITCODE`. Subexpression
        // (not script block `& { }`) is required so Write-Error's $?=$false
        // propagates to the outer pipeline; `& { }` invocation resets $? = $true.
        else if (cmd0 == "true" && cmd.Words.Length == 1)
            specialResult = "$($global:LASTEXITCODE = 0; [void]$true)";
        else if (cmd0 == "false" && cmd.Words.Length == 1)
        {
            if (_context == TranspileContext.Eval)
                // try/catch on (1/0) is the mechanism that flips $? to $false so bash `&&` short-circuits;
                // Write-Error can't be used here because it propagates as a terminating error in eval scope.
                specialResult = "$($global:LASTEXITCODE = 1; try { [void](1/0) } catch { }; if ($global:__BashErrexit) { throw 'PsBash.FalseErrexit' })";
            else
                specialResult = "$($global:LASTEXITCODE = 1; Write-Error '' -ErrorAction SilentlyContinue)";
        }

        else if (cmd0 == "cd")
            specialResult = EmitCd(cmd.Words.RemoveAt(0));

        // read [-r] [-p "prompt"] VAR -> Invoke-BashRead [-p "prompt"] VAR
        else if (cmd0 == "read")
            specialResult = EmitPassthrough("Invoke-BashRead", cmd.Words.RemoveAt(0));

        // eval "cmd" -> inline-transpile the reconstructed bash source
        // of the args at parse time. Command/arith/process substitutions
        // inside the eval body raise a ParseException; there is no runtime
        // eval path.
        else if (cmd0 == "eval")
            specialResult = EmitEval(cmd.Words.RemoveAt(0));

        else if (cmd0 == "readonly")
            specialResult = EmitReadonly(cmd);

        else if (cmd0 == "set" && cmd.Words.Length >= 2)
            specialResult = EmitSet(cmd);

        else if ((cmd0 == "source" || cmd0 == ".") && cmd.Words.Length >= 2)
            specialResult = EmitSourceOrDot(cmd);

        // Standalone mapped commands: rewrite through TryEmitMappedCommand.
        // Under TranspileContext.Eval the rewrite is mandatory for every
        // mapped command — including those in PsBuiltinAliases — because
        // the in-process eval host (e.g. Invoke-BashEval cmdlet) MUST NOT
        // depend on global PsBash aliases hijacking pwsh builtins.
        // Under TranspileContext.Default the same rewrite happens today;
        // the PsBuiltinAliases set is reserved as a forward-compat hook
        // for a future optimization that would let those commands resolve
        // through the host's existing aliases.
        else if (cmd0 is not null
            && TryEmitMappedCommand(cmd, out var mapped))
            specialResult = EmitMappedStandalone(cmd, mapped);

        if (specialResult is not null)
        {
            result = specialResult;
            return true;
        }
        return false;
    }

    // readonly VAR=val -> Set-Variable -Name VAR -Value val -Option Constant
    private static string EmitReadonly(Command.Simple cmd)
    {
        var roSb = new StringBuilder();
        for (int i = 1; i < cmd.Words.Length; i++)
        {
            if (i > 1) roSb.Append("; ");
            var val = GetLiteralValue(cmd.Words[i]);
            if (val is null) continue;
            if (val.StartsWith('-')) continue; // skip flags like -p, -r
            int eq = val.IndexOf('=');
            if (eq > 0)
            {
                string varName = val[..eq];
                string varVal = val[(eq + 1)..];
                roSb.Append($"Set-Variable -Name {varName} -Value '{SqEsc(varVal)}' -Option Constant -Scope Global");
            }
            else
            {
                roSb.Append($"Set-Variable -Name {val} -Option Constant -Scope Global");
            }
        }
        return string.IsNullOrEmpty(roSb.ToString()) ? "[void]$true" : roSb.ToString();
    }

    // set -- a b c -> reset positional parameters
    // set -e / set -o errexit -> $ErrorActionPreference = 'Stop'
    // set -x / set -o xtrace -> Set-PSDebug -Trace 1
    // set -u / set -o nounset -> Set-StrictMode -Version Latest
    private static string? EmitSet(Command.Simple cmd)
    {
        string? specialResult = null;
        var literalArgs = cmd.Words.Skip(1).Select(w => GetLiteralValue(w)).ToList();
        int dashDashIdx = literalArgs.IndexOf("--");
        if (dashDashIdx >= 0)
        {
            var positionalWords = cmd.Words.Skip(1 + dashDashIdx + 1).ToList();
            if (positionalWords.Count == 0)
                specialResult = "$global:BashPositional = @()";
            else
            {
                // Quote each element so that bare literals like `a`, `b`, `c`
                // are treated as strings, not variable/command references in PS.
                var items = string.Join(", ", positionalWords.Select(w =>
                {
                    var emitted = EmitWord(w);
                    // Already quoted (starts with ' or "), a variable ($), subexpr ((), or array (@): pass through.
                    if (emitted.Length > 0 && emitted[0] is '\'' or '"' or '$' or '(' or '@')
                        return emitted;
                    // Bare literal — wrap in single quotes.
                    return PsBuild.SingleQuote(emitted);
                }));
                specialResult = $"$global:BashPositional = @({items})";
            }
        }
        else
        {
            var args = literalArgs;
            bool longOpt = args.Any(a => a == "-o");
            if (longOpt)
            {
                var optVal = args.SkipWhile(a => a != "-o").Skip(1).FirstOrDefault();
                if (optVal == "errexit") specialResult = "$ErrorActionPreference = 'Stop'; $global:__BashErrexit = $true";
                else if (optVal == "xtrace") specialResult = "Set-PSDebug -Trace 1";
                else if (optVal == "nounset") specialResult = "Set-StrictMode -Version Latest";
            }
            else
            {
                var flags = args.Where(a => a is not null && a.StartsWith('-') && !a.StartsWith("--")).ToList();
                bool e = flags.Any(f => f!.Contains('e'));
                bool x = flags.Any(f => f!.Contains('x'));
                bool u = flags.Any(f => f!.Contains('u'));
                var parts = new List<string>();
                if (e) parts.AddRange(new[]{"$ErrorActionPreference = 'Stop'", "$global:__BashErrexit = $true"});
                if (u) parts.Add("Set-StrictMode -Version Latest");
                if (x) parts.Add("Set-PSDebug -Trace 1");
                if (parts.Count > 0) specialResult = string.Join("; ", parts);
            }
        }
        return specialResult;
    }

    // source FILE / . FILE -> Invoke-BashSource FILE @args
    // source <(cmd) / . <(cmd) -> Invoke-ProcessSubSource { transpiled_cmd }
    // The process-substitution form captures the producer's stdout as bash text,
    // transpiles it, and executes the result in the caller's scope (source semantics).
    private static string EmitSourceOrDot(Command.Simple cmd)
    {
        var fileArg = cmd.Words[1];
        if (fileArg.Parts.Length == 1 && fileArg.Parts[0] is WordPart.ProcessSub procSub)
        {
            string inner = EmitCaptured((Command)procSub.Body);
            return $"Invoke-ProcessSubSource {{ {inner} }}";
        }
        return EmitPassthrough("Invoke-BashSource", cmd.Words.RemoveAt(0));
    }

    private static string EmitMappedStandalone(Command.Simple cmd, string mapped)
    {
        if (cmd.Redirects.IsEmpty)
            return mapped;

        var mappedSb = new StringBuilder(mapped);
        EmitPipeTargetRedirects(cmd, mappedSb);
        return mappedSb.ToString();
    }

    // The general fallback: no special form matched, emit the command word(s),
    // arguments (with env-prefix save/restore and unquoted-var splat handling),
    // and any trailing redirects.
    private static string EmitGeneralCommand(Command.Simple cmd)
    {
        var sb = new StringBuilder();

        // Env-var prefix: VAR=value cmd — set VAR only for the duration of cmd,
        // then restore to its previous value to prevent leaking into the shell.
        if (!cmd.EnvPairs.IsEmpty)
        {
            // Save original values, then open a try block.
            foreach (var envPair in cmd.EnvPairs)
                sb.Append($"$__saved_{envPair.Name} = $env:{envPair.Name}; ");
            sb.Append("try { ");
            // Set each env var inside the try block.
            foreach (var envPair in cmd.EnvPairs)
            {
                sb.Append("$env:");
                sb.Append(envPair.Name);
                sb.Append(" = ");
                sb.Append(EmitAssignmentValue(envPair.Value));
                sb.Append("; ");
            }
        }

        // RC-7: an unquoted ordinary variable argument is word-split and
        // elided-when-empty. The command word itself (index 0) is never split —
        // it must resolve to a name — so only operands (index >= 1) are scanned.
        var commandArgs = cmd.Words.Length > 1
            ? cmd.Words.RemoveAt(0)
            : ImmutableArray<CompoundWord>.Empty;
        // Command-word prefix: a path-like `.sh`/`.bash` script runs through `bash` (so it executes
        // correctly AND composes in a pipeline — see IsLocalShellScriptCommand); otherwise a quoted
        // command word needs the `& ` call operator; a bare command word needs nothing.
        // Guard the Words[0] access: a Simple command CAN have zero words (an empty
        // inner body of a malformed `<(` / `$(` / bare heredoc that the parser accepts).
        // Without the guard this threw IndexOutOfRange — a crash, not the clean
        // parseable-or-ParseException the fuzz invariant requires.
        string leading = cmd.Words.Length == 0 ? ""
            : IsLocalShellScriptCommand(cmd.Words[0]) ? "bash "
            : IsQuotedCommandWord(cmd.Words[0]) ? "& "
            : IsVariableCommandWord(cmd.Words[0]) ? "& "
            : "";

        if (HasUnquotedVarSplatArg(commandArgs))
        {
            // Hoist each unquoted-variable operand to a temp var and splat it.
            // The command word keeps its existing emission (incl. the `& `
            // call-operator prefix for a quoted command word, or the `bash ` prefix for a script).
            sb.Append(EmitCommandWithSplatArgs(
                leading + EmitWord(cmd.Words[0]),
                commandArgs,
                argIndex => EmitWord(commandArgs[argIndex])));
        }
        else
        {
            for (var i = 0; i < cmd.Words.Length; i++)
            {
                if (i > 0)
                    sb.Append(' ');
                else if (leading.Length > 0)
                    sb.Append(leading);
                sb.Append(EmitWord(cmd.Words[i]));
            }
        }

        // Stdout file redirects (> file, >> file) route through Invoke-BashRedirect
        // (avoids lingering file handles that cause IO.IOException in chained
        // commands); every other redirect is emitted inline. Shared selection helper.
        AppendRedirectTail(sb, cmd.Redirects);

        // Close the try/finally for env-var prefix save/restore.
        if (!cmd.EnvPairs.IsEmpty)
        {
            sb.Append(" } finally { ");
            foreach (var envPair in cmd.EnvPairs)
                sb.Append($"$env:{envPair.Name} = $__saved_{envPair.Name}; ");
            sb.Append('}');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Wrap an already-emitted command in the <c>VAR=val cmd</c> env-prefix
    /// save/set/restore so the assignment applies for the duration of the command
    /// and is then restored (no leak), regardless of which emission path produced
    /// <paramref name="inner"/>.
    /// </summary>
    private static string WrapEnvPairs(ImmutableArray<EnvPair> pairs, string inner)
    {
        var sb = new StringBuilder();
        foreach (var p in pairs)
            sb.Append($"$__saved_{p.Name} = $env:{p.Name}; ");
        sb.Append("try { ");
        foreach (var p in pairs)
            sb.Append($"$env:{p.Name} = {EmitAssignmentValue(p.Value)}; ");
        sb.Append(inner);
        sb.Append(" } finally { ");
        foreach (var p in pairs)
            sb.Append($"$env:{p.Name} = $__saved_{p.Name}; ");
        sb.Append('}');
        return sb.ToString();
    }

    private static string EmitRedirect(Redirect r)
    {
        var target = TransformRedirectTarget(EmitWord(r.Target));

        return r.Op switch
        {
            ">" => r.Fd == 1 ? $">{target}" : $"{r.Fd}>{target}",
            ">>" => r.Fd == 1 ? $">>{target}" : $"{r.Fd}>>{target}",
            // `n>&-` (close output fd) — PowerShell has no fd-close; the practical
            // equivalent for an output fd is to discard (redirect to $null).
            // Without this, target "-" emitted invalid `1>&-`. The `target == "-"`
            // guard keeps the normal merge form (`2>&1`, target "1") intact.
            ">&" when target == "-" => r.Fd == 1 ? ">$null" : $"{r.Fd}>$null",
            // `cmd >&file` (non-numeric, non-close target) is a bash synonym for
            // `&>file`: redirect BOTH stdout and stderr to the FILE. The `{fd}>&{target}`
            // form is invalid PowerShell for a file (the `>&` operator only accepts a
            // stream number), so emit the `&>` form. A numeric target (`>&2`, `>&1`) is a
            // genuine fd-merge and keeps the `{fd}>&{n}` form below.
            // Only the UNPREFIXED `>&file` (fd defaults to 1) is the both-streams
            // synonym for `&>file`. A prefixed `2>&file` is not that form, so it is
            // left to the fallback rather than wrongly redirecting stdout.
            ">&" when r.Fd == 1 && !IsAllDigits(target) => $">{target} 2>&1",
            ">&" => $"{r.Fd}>&{target}",
            // `n<&-` (close stdin) — no PowerShell equivalent; degrade to a
            // documented inline no-op comment rather than the invalid `0<&-`.
            "<&" when target == "-" => "<# ps-bash: stdin fd-close (<&-) has no PowerShell equivalent #>",
            // `&>file` / `&>>file` — redirect (or append) BOTH stdout and stderr.
            // PowerShell: `>file 2>&1` / `>>file 2>&1` (stdout to the file, then
            // stderr to wherever stdout now points).
            "&>" => $">{target} 2>&1",
            "&>>" => $">>{target} 2>&1",
            _ => $"{r.Fd}{r.Op}{target}",
        };
    }

    // True only for a non-empty run of ASCII digits — used to tell a numeric fd-merge
    // target (`>&2`) from a file target (`>&out.txt`).
    private static bool IsAllDigits(string s)
    {
        if (s.Length == 0)
            return false;
        foreach (char c in s)
            if (c < '0' || c > '9')
                return false;
        return true;
    }

    private static string TransformRedirectTarget(string target)
    {
        if (target == "/dev/null")
            return "$null";
        if (target.StartsWith("/tmp/"))
            return $"$env:TEMP\\{target[5..]}";
        if (TryTranslateMsysDrivePath(target, out var translated))
            return translated;
        return target;
    }

    /// <summary>
    /// True when unix-path translation is opted in via <c>PSBASH_UNIX_PATHS=1</c>
    /// (set by the Shell layer from the <c>--unix-paths</c> CLI flag). Off by
    /// default so direct ps-bash users don't get surprise path rewrites; on for
    /// wrappers (Claude Code, OpenCode) that emit unix-shaped paths assuming a
    /// POSIX filesystem.
    /// </summary>
    private static bool UnixPathTranslationEnabled
        => Environment.GetEnvironmentVariable("PSBASH_UNIX_PATHS") == "1";

    /// <summary>
    /// Translate a unix-style drive path (<c>/c/Users/...</c>, <c>/mnt/c/...</c>)
    /// into a Windows path (<c>C:\Users\...</c>) when translation is enabled, using
    /// the shared <see cref="WindowsPath"/> mapper. Applied uniformly to redirect
    /// targets, command operands (<see cref="TransformWordPath"/>), and <c>cd</c>
    /// targets (<see cref="EmitCd"/>) so an absolute drive path resolves the same
    /// way everywhere — otherwise <c>cat /c/x</c> / <c>cd /c/x</c> resolve
    /// <c>/c/x</c> against the current drive and become <c>C:\c\x</c>.
    /// </summary>
    private static bool TryTranslateMsysDrivePath(string path, out string windowsPath)
    {
        if (UnixPathTranslationEnabled && WindowsPath.TryMapUnixDrivePath(path, out windowsPath))
            return true;
        windowsPath = path;
        return false;
    }

    /// <summary>
    /// Translate bash variable references ($VAR, ${VAR}) in heredoc body text
    /// to PowerShell equivalents ($env:VAR).
    /// </summary>
    /// <summary>
    /// Strips leading tab characters from each line of a heredoc body.
    /// Used for <c>&lt;&lt;-EOF</c> (DLessDash) heredocs where bash strips leading tabs.
    /// </summary>
    private static string StripLeadingTabs(string body)
    {
        var lines = body.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            lines[i] = lines[i].TrimStart('\t');
        return string.Join('\n', lines);
    }

    /// <summary>
    /// True when <paramref name="body"/> contains a line that begins with the
    /// PowerShell here-string terminator (<c>"@</c> for an expanding heredoc,
    /// <c>'@</c> for a literal one). Such a line closes the here-string early, so
    /// the emitter must fall back to an ordinary quoted string instead.
    /// </summary>
    private static bool HereStringWouldBreak(string body, bool expand)
    {
        string terminator = expand ? "\"@" : "'@";
        int pos = 0;
        while (pos < body.Length)
        {
            int nl = body.IndexOf('\n', pos);
            int lineStart = pos;
            // A '\r' before the '\n' is part of the line, not its start.
            if (string.CompareOrdinal(body, lineStart, terminator, 0, terminator.Length) == 0)
                return true;
            if (nl < 0) break;
            pos = nl + 1;
        }
        return false;
    }

    /// <summary>
    /// Escape a heredoc body for placement inside a non-here double-quoted string
    /// while preserving the <c>@"..."@</c> expansion contract: <c>$</c> and
    /// backtick keep their here-string meaning (interpolation / escape), and only
    /// a literal <c>"</c> — which would close the ordinary string — is backtick
    /// escaped. (A body that already contains a backtick-escaped quote is a
    /// documented corner the fallback does not round-trip; the common heredoc
    /// bodies that hit this path do not.)
    /// </summary>
    private static string EscapeForDoubleQuotedHereDocBody(string body) =>
        body.IndexOf('"') < 0 ? body : body.Replace("\"", "`\"");

    private static string TranslateHereDocVars(string body)
    {
        var sb = new StringBuilder();
        int pos = 0;
        int len = body.Length;

        while (pos < len)
        {
            // Backslash escapes an expansion char in an unquoted heredoc: bash
            // renders \$ \` \\ as the literal char (no expansion). Emit the literal
            // backtick-escaped so the interpolating @"..."@ here-string does not
            // re-interpret it — this is also what stops `\$(cmd)` from executing.
            if (body[pos] == '\\' && pos + 1 < len)
            {
                char n = body[pos + 1];
                if (n == '$') { sb.Append("`$"); pos += 2; continue; }
                if (n == '`') { sb.Append("``"); pos += 2; continue; }
                if (n == '\\') { sb.Append('\\'); pos += 2; continue; }
                // Other \x: bash keeps the backslash literally.
                sb.Append(body[pos]); pos++;
                continue;
            }
            if (body[pos] == '$' && pos + 1 < len && body[pos + 1] == '(')
            {
                // $(...) command substitution. bash EXPANDS it in an unquoted
                // heredoc — so transpile the inner command to PowerShell and keep
                // it as a subexpression. Passing the raw `$(date)` through executed
                // it as PowerShell (Get-Date), a quoting-seam code-execution leak.
                int cclose = FindMatchingParenQuoteAware(body, pos + 1);
                if (cclose >= 0)
                {
                    sb.Append(EmitHereDocCommandSub(body[pos..(cclose + 1)]));
                    pos = cclose + 1;
                }
                else
                {
                    // Unbalanced — emit a literal, escaped `$` so nothing executes.
                    sb.Append("`$"); pos++;
                }
            }
            else if (body[pos] == '`')
            {
                // `...` command substitution — same story; a raw backtick was also
                // swallowed as a PowerShell escape, silently destroying the sub.
                int bclose = body.IndexOf('`', pos + 1);
                if (bclose >= 0)
                {
                    sb.Append(EmitHereDocCommandSub(body[pos..(bclose + 1)]));
                    pos = bclose + 1;
                }
                else
                {
                    sb.Append("``"); pos++;
                }
            }
            else if (body[pos] == '$' && pos + 1 < len && body[pos + 1] == '{')
            {
                // ${...} — find the matching (balanced) close brace so nested
                // expansions like ${x:-${y}} are captured whole.
                int close = FindMatchingBrace(body, pos + 1);
                if (close >= 0)
                {
                    string content = body[(pos + 2)..close];
                    if (IsSimpleHereDocVarName(content))
                    {
                        // Plain ${VAR} -> $env:VAR (cheap, known-good).
                        sb.Append(EmitSimpleVar(content));
                    }
                    else
                    {
                        // Parameter expansion with an operator (${x:-fallback},
                        // ${x#pre}, ${x^^}, …). Emitting the raw content as a
                        // variable name produced $env:x:-fallback. Decompose the
                        // whole ${...} and route through the real braced-var
                        // emitter, then wrap in $(...) so it expands inside the
                        // @"..."@ here-string.
                        var parts = BashParser.DecomposeWord(body[pos..(close + 1)]);
                        sb.Append("$(");
                        sb.Append(EmitWord(new CompoundWord(parts)));
                        sb.Append(')');
                    }
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

    /// <summary>Index of the <c>)</c> matching the <c>(</c> at <paramref name="openParen"/>,
    /// honoring nesting and quoted/escaped spans; -1 if unbalanced.</summary>
    private static int FindMatchingParenQuoteAware(string s, int openParen)
    {
        int depth = 0;
        for (int i = openParen; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length) { i++; continue; }
            if (c == '\'')
            {
                i++;
                while (i < s.Length && s[i] != '\'') i++;
                continue;
            }
            if (c == '"')
            {
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length) i++;
                    i++;
                }
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>
    /// Transpile a heredoc-embedded command substitution (<c>$(cmd)</c> or
    /// <c>`cmd`</c>) to the newline-preserving PowerShell subexpression form.
    /// Heredoc expansion follows double-quote rules, so the STRING form
    /// (<see cref="EmitCommandSubString"/>) is correct, not the word-split array.
    /// </summary>
    private static string EmitHereDocCommandSub(string raw)
    {
        var parts = BashParser.DecomposeWord(raw);
        var sb = new StringBuilder();
        foreach (var p in parts)
            sb.Append(p is WordPart.CommandSub cs ? EmitCommandSubString(cs) : EmitWordPart(p));
        return sb.ToString();
    }

    /// <summary>Index of the <c>}</c> matching the <c>{</c> at <paramref name="openBrace"/>,
    /// honoring nesting; -1 if unbalanced.</summary>
    private static int FindMatchingBrace(string s, int openBrace)
    {
        int depth = 0;
        for (int i = openBrace; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>True when the brace content is a bare variable name (no expansion
    /// operator), so <c>${VAR}</c> can take the cheap <c>$env:VAR</c> path.</summary>
    private static bool IsSimpleHereDocVarName(string content)
    {
        if (content.Length == 0) return false;
        foreach (char c in content)
            if (!IsHereDocVarChar(c)) return false;
        return true;
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

        // Multi-part value: flatten into one PS double-quoted string.
        // EmitWord wraps each DoubleQuoted part in its own "...", which produces
        // ""segment1":"segment2"" when combined — invalid PowerShell.
        // Instead open one outer double-quote and inline the content of each part.
        return FlattenPartsToDoubleQuotedString(value.Parts);
    }

    private static string EmitWord(CompoundWord word)
    {
        // Check for brace expansion parts that need prefix/suffix combination.
        if (HasBraceExpansion(word.Parts))
            return EmitBraceExpandedWord(word.Parts);

        if (word.Parts.Length == 1)
            return TransformWordPath(EmitWordPart(word.Parts[0]));

        // Adjacent-quote concatenation. When a word's FIRST part is a
        // self-delimiting token ('...', "...", or $(...)), PowerShell tokenizes a
        // following part as a SEPARATE argument: `echo 'a'"b"` -> two args
        // (`a`, `b`), and `echo 'a''b'` collapses to the single string `a'b`.
        // bash treats the whole word as one concatenated argument. Flatten the
        // word into a single PS double-quoted string so it is exactly one
        // argument with bash-correct concatenation (single-quoted parts stay
        // literal via `$/backtick escaping; double-quoted parts and $(...) still
        // expand). A bareword/$var first part already absorbs following parts
        // (`pre"mid"suf`, `$x"a"`), so those keep their cheaper emission.
        if (NeedsAdjacencyFlatten(word.Parts))
            return TransformWordPath(FlattenPartsToDoubleQuotedString(word.Parts));

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

    /// <summary>
    /// True when a multi-part word's leading part is a self-delimiting PowerShell
    /// token ('...', "...", or $(...)) so naive part concatenation would split
    /// into multiple arguments or corrupt content. Words containing glob,
    /// process-substitution, tilde, or brace parts are excluded — they require
    /// their own emission and do not lead with a self-delimiting quote in
    /// practice.
    /// </summary>
    private static bool NeedsAdjacencyFlatten(ImmutableArray<WordPart> parts)
    {
        if (parts.Length < 2)
            return false;
        if (parts[0] is not (WordPart.SingleQuoted or WordPart.DoubleQuoted
                          or WordPart.AnsiCQuoted or WordPart.CommandSub))
            return false;
        foreach (var p in parts)
        {
            if (p is WordPart.GlobPart or WordPart.ProcessSub or WordPart.TildeSub
                  or WordPart.BracedTuple or WordPart.BracedRange)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Flatten a word's parts into one PowerShell double-quoted string. Mirrors
    /// the multi-part branch of <see cref="EmitAssignmentValue"/>: single-quoted
    /// and literal content is escaped so `$` / backtick / `"` stay literal, while
    /// double-quoted parts and command substitutions keep their expansion.
    /// </summary>
    private static string FlattenPartsToDoubleQuotedString(ImmutableArray<WordPart> parts)
    {
        var sb = new StringBuilder();
        sb.Append('"');
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part is WordPart.DoubleQuoted dq)
                AppendDoubleQuotedInner(sb, dq.Parts);
            else if (part is WordPart.SingleQuoted sq)
                sb.Append(PsBuild.EscapeForDoubleQuote(sq.Value));
            else if (part is WordPart.AnsiCQuoted aq)
                sb.Append(PsBuild.EscapeForDoubleQuote(ExpandAnsiCEscapes(aq.Value)));
            else if (part is WordPart.Literal lit)
                sb.Append(PsBuild.EscapeForDoubleQuote(lit.Value));
            // A NESTED expansion (e.g. ${x:-${y:-z}}) must be emitted in double-quote
            // context: its bare form `($env:y ?? "z")` carries raw `"`, which would
            // break out of this surrounding "...". The inDoubleQuote form uses `$(...)`
            // with single-quoted inner literals, which is safe nested here.
            else if (part is WordPart.BracedVarSub nbvs)
                sb.Append(EmitBracedVar(nbvs, inDoubleQuote: true));
            else if (part is WordPart.SimpleVarSub nvs)
            {
                // Same drive-reference guard as AppendDoubleQuotedInner: a following literal
                // starting with ':' (or '.') would fold into the variable token (`$env:x:b`
                // misparses as a provider-qualified path), so brace it (${x}/${env:x}) — this
                // flatten path was missing the bracing its double-quoted sibling applies.
                bool needsBracing = i + 1 < parts.Length && NextPartNeedsBracing(parts[i + 1]);
                sb.Append(needsBracing
                    ? EmitSimpleVarBraced(nvs.Name)
                    : EmitSimpleVar(nvs.Name, inDoubleQuote: true));
            }
            else if (part is WordPart.CommandSub ncs)
                // Assignment/flatten context (x=$(cmd)) preserves internal newlines and
                // strips trailing ones, like bash — not the array $OFS-join a space.
                sb.Append(EmitCommandSubString(ncs));
            else
                sb.Append(EmitWordPart(part));
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Emit a parameter-expansion ARGUMENT word (the W in ${VAR:-W} / ${VAR:+W} / …) as a
    /// PowerShell value expression. A pure-literal word — including the EMPTY word — is emitted
    /// single-quoted (<c>'foo'</c>, <c>''</c>); only a word that genuinely needs interpolation
    /// (a variable / command-sub / "$@") falls through to the double-quoted flatten. This is
    /// load-bearing: the value can land inside <c>"$( … )"</c> (when the whole ${} sits in a
    /// bash double-quoted string), and PowerShell mis-parses a nested *double-quoted* string that
    /// is empty (<c>""</c>) or contains a quote — but a single-quoted literal and a non-empty
    /// interpolating string are both safe there. (RC3 — the ${CLAUDE_CODE_EXECPATH:-} break.)
    /// </summary>
    private static string EmitBracedArgWordValue(ImmutableArray<WordPart> parts)
    {
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            string? lit = part switch
            {
                WordPart.Literal l => l.Value,
                WordPart.EscapedLiteral el => el.Value,
                WordPart.SingleQuoted sq => sq.Value,
                WordPart.AnsiCQuoted aq => ExpandAnsiCEscapes(aq.Value),
                _ => null,
            };
            if (lit is null)            // a real expansion (var / $( ) / "$@") — needs interpolation
                return FlattenPartsToDoubleQuotedString(parts);
            sb.Append(lit);
        }
        // Pure literal (possibly empty): single-quote it, doubling embedded single quotes.
        return $"'{sb.Replace("'", "''")}'";
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
        // Adjacent brace expansions cross-multiply: bash `{a..c}{1..2}` -> a1 a2 b1 b2 c1 c2.
        // With 2+ brace parts, compute the full Cartesian product across all segments
        // (each brace contributes its item list; every other part is one literal segment).
        int braceCount = 0;
        foreach (var p in parts)
            if (p is WordPart.BracedTuple or WordPart.BracedRange)
                braceCount++;
        if (braceCount >= 2)
        {
            var combos = new List<string> { "" };
            foreach (var part in parts)
            {
                List<string> segment = part is WordPart.BracedTuple or WordPart.BracedRange
                    ? ExpandBrace(part)
                    : new List<string> { EmitWordPart(part) };
                var next = new List<string>(combos.Count * segment.Count);
                foreach (string head in combos)
                    foreach (string s in segment)
                        next.Add(head + s);
                combos = next;
            }
            var crossItems = new List<string>(combos.Count);
            foreach (string c in combos)
                crossItems.Add(PsBuild.SingleQuote(c));
            return $"@({string.Join(',', crossItems)})";
        }

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

        // With prefix/suffix, generate explicit items. Route through PsBuild so an
        // item/prefix/suffix containing a quote is escaped (a hand-built `'{...}'`
        // emitted `@('a','b'c'd')` — a PowerShell parse error — for `{a,b'c'd}`).
        var items = new List<string>();
        foreach (string item in expanded)
            items.Add(PsBuild.SingleQuote(pre + item + suf));

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
        // IsPlainInteger accepts arbitrary-length digit runs (`{10000000000,2}`),
        // so parse with long/TryParse: an out-of-range item just disables the
        // range optimization (falls through to the literal array) instead of
        // throwing OverflowException.
        if (items.Count >= 2 && items.All(IsPlainInteger)
            && long.TryParse(items[0], out long first)
            && long.TryParse(items[^1], out long last))
        {
            bool isSequential = true;
            long step = first <= last ? 1 : -1;
            for (int i = 0; i < items.Count; i++)
            {
                if (!long.TryParse(items[i], out long v) || v != first + i * step)
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
                formatted.Add(PsBuild.SingleQuote(item));
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
        // NOTE: do NOT map /dev/null -> $null here. As a command OPERAND (vs a redirect target,
        // which TransformRedirectTarget still maps to $null), bash's /dev/null is an empty FILE —
        // `grep x /dev/null` reads an empty file (no match, exit 1), not the $null discard sink
        // (which crashed cmdlets with "Value cannot be null"). The literal /dev/null flows to the
        // runtime, where BashFileSystem.OpenRead (via FileSystemHelpers.IsNullDevice) serves it
        // from the OS-native null device as empty.
        if (value.StartsWith("/tmp/"))
            return $"$env:TEMP\\{value[5..]}";
        if (TryTranslateMsysDrivePath(value, out var translated))
            return translated;
        return value;
    }

    private static string EmitWordPart(WordPart part) => part switch
    {
        WordPart.Literal lit => lit.Value,
        WordPart.EscapedLiteral el => $"`{el.Value}",
        WordPart.SingleQuoted sq => $"'{sq.Value}'",
        WordPart.AnsiCQuoted aq => EmitAnsiCQuoted(aq),
        WordPart.DoubleQuoted dq => EmitDoubleQuoted(dq),
        WordPart.SimpleVarSub vs => EmitSimpleVar(vs.Name),
        WordPart.BracedVarSub bvs => EmitBracedVar(bvs),
        // RC-8d: wrap user command-substitution output so the captured value is
        // always the bash text payload, never a typed BashObject's default
        // hashtable-style ToString(). Without this, `dir=$(pwd)` captures
        // `@{BashText=/path; Command=pwd}` because PowerShell's string
        // interpolation calls PSObject.ToString() on the typed PwdLine object,
        // not its .BashText property. ForEach-Object on an empty pipeline
        // produces nothing (preserving empty-capture semantics), and a
        // multi-element pipeline is space-joined by $OFS when interpolated in
        // a double-quoted context — matching bash IFS default-field-splitting.
        WordPart.CommandSub cs => EmitCommandSub(cs),
        WordPart.ArithSub arith => EmitArithSub(arith),
        WordPart.TildeSub ts => ts.User switch
        {
            null => "$HOME",
            "+" => "$PWD",          // ~+  -> $PWD
            "-" => "$env:OLDPWD",   // ~-  -> $OLDPWD
            // ~user / ~N (dirstack) have no faithful PowerShell equivalent; keep
            // the literal `~user` (degrade) rather than emitting a bogus var.
            _ => $"~{ts.User}",
        },
        WordPart.GlobPart gp => NormalizePosixGlobClasses(gp.Pattern),
        WordPart.BracedTuple bt => FormatBraceArray(new List<string>(bt.Items)),
        WordPart.BracedRange br => FormatBraceArray(ExpandBrace(br)),
        WordPart.ProcessSub ps => EmitProcessSub(ps),
        _ => throw new NotSupportedException($"Unknown word part type: {part.GetType().Name}"),
    };

    private static string NormalizePosixGlobClasses(string pattern)
    {
        if (!pattern.Contains("[:", StringComparison.Ordinal) ||
            pattern.Length < 2 || pattern[0] != '[' || pattern[^1] != ']')
            return pattern;

        var result = new StringBuilder(pattern.Length);
        result.Append('[');
        for (int pos = 1; pos < pattern.Length - 1;)
        {
            if (pattern[pos] == '[' && pos + 1 < pattern.Length - 1 &&
                pattern[pos + 1] is '.' or '=')
            {
                char marker = pattern[pos + 1];
                int close = pattern.IndexOf($"{marker}]", pos + 2, StringComparison.Ordinal);
                if (close < 0) return pattern;
                result.Append(pattern, pos, close + 2 - pos);
                pos = close + 2;
                continue;
            }

            if (pattern[pos] == '[' && pos + 1 < pattern.Length - 1 && pattern[pos + 1] == ':')
            {
                int close = pattern.IndexOf(":]", pos + 2, StringComparison.Ordinal);
                if (close < 0) return pattern;
                string name = pattern[(pos + 2)..close];
                string? replacement = name switch
                {
                    "alnum" => "A-Za-z0-9",
                    "alpha" => "A-Za-z",
                    // Backticks keep whitespace inside the unquoted PowerShell token.
                    "blank" => "` `t",
                    "digit" => "0-9",
                    "lower" => "a-z",
                    "space" => "` `t`r`n`f`v",
                    "upper" => "A-Z",
                    "word" => "A-Za-z0-9_",
                    "xdigit" => "A-Fa-f0-9",
                    _ => null,
                };
                if (replacement is null) return pattern;
                result.Append(replacement);
                pos = close + 2;
                continue;
            }

            result.Append(pattern[pos++]);
        }
        result.Append(']');
        return result.ToString();
    }

    /// <summary>
    /// Emit an ANSI-C quoted string ($'...'). The C-style escapes are expanded at
    /// transpile time into the literal characters, then emitted as a PowerShell
    /// single-quoted literal so no PowerShell expansion can occur on the result
    /// (Directive 12: a payload like $'$(rm)' becomes the literal text, never code).
    /// </summary>
    private static string EmitAnsiCQuoted(WordPart.AnsiCQuoted aq)
        => PsBuild.SingleQuote(ExpandAnsiCEscapes(aq.Value));

    /// <summary>
    /// Expand bash ANSI-C ($'...') escape sequences into their literal characters.
    /// Supports \a \b \e \E \f \n \r \t \v \\ \' \" \?, octal \nnn (1-3 digits),
    /// hex \xHH (1-2), unicode \uHHHH (1-4) / \UHHHHHHHH (1-8), and control \cX.
    /// An unrecognized escape keeps the backslash and the following character,
    /// matching bash.
    /// </summary>
    private static string ExpandAnsiCEscapes(string s)
    {
        var sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c != '\\' || i + 1 >= s.Length)
            {
                sb.Append(c);
                i++;
                continue;
            }

            char n = s[i + 1];
            switch (n)
            {
                case 'a': sb.Append('\a'); i += 2; break;
                case 'b': sb.Append('\b'); i += 2; break;
                case 'e': case 'E': sb.Append('\x1b'); i += 2; break;
                case 'f': sb.Append('\f'); i += 2; break;
                case 'n': sb.Append('\n'); i += 2; break;
                case 'r': sb.Append('\r'); i += 2; break;
                case 't': sb.Append('\t'); i += 2; break;
                case 'v': sb.Append('\v'); i += 2; break;
                case '\\': sb.Append('\\'); i += 2; break;
                case '\'': sb.Append('\''); i += 2; break;
                case '"': sb.Append('"'); i += 2; break;
                case '?': sb.Append('?'); i += 2; break;

                case 'x':
                {
                    int j = i + 2, val = 0, cnt = 0;
                    while (j < s.Length && cnt < 2 && Uri.IsHexDigit(s[j]))
                    {
                        val = val * 16 + HexValue(s[j]);
                        j++; cnt++;
                    }
                    if (cnt == 0) { sb.Append('\\').Append('x'); i += 2; }
                    else { sb.Append((char)val); i = j; }
                    break;
                }

                case 'u':
                case 'U':
                {
                    int maxDigits = n == 'u' ? 4 : 8;
                    int j = i + 2; long val = 0; int cnt = 0;
                    while (j < s.Length && cnt < maxDigits && Uri.IsHexDigit(s[j]))
                    {
                        val = val * 16 + HexValue(s[j]);
                        j++; cnt++;
                    }
                    if (cnt == 0) { sb.Append('\\').Append(n); i += 2; }
                    else
                    {
                        if (val <= 0x10FFFF && !(val >= 0xD800 && val <= 0xDFFF))
                            sb.Append(char.ConvertFromUtf32((int)val));
                        else
                            sb.Append((char)(val & 0xFFFF));
                        i = j;
                    }
                    break;
                }

                case 'c':
                {
                    if (i + 2 < s.Length)
                    {
                        char x = s[i + 2];
                        sb.Append((char)(char.ToUpperInvariant(x) ^ 0x40));
                        i += 3;
                    }
                    else { sb.Append('\\').Append('c'); i += 2; }
                    break;
                }

                case >= '0' and <= '7':
                {
                    int j = i + 1, val = 0, cnt = 0;
                    while (j < s.Length && cnt < 3 && s[j] is >= '0' and <= '7')
                    {
                        val = val * 8 + (s[j] - '0');
                        j++; cnt++;
                    }
                    sb.Append((char)(val & 0xFF));
                    i = j;
                    break;
                }

                default:
                    sb.Append('\\').Append(n);
                    i += 2;
                    break;
            }
        }
        return sb.ToString();
    }

    private static int HexValue(char c)
        => c is >= '0' and <= '9' ? c - '0'
         : c is >= 'a' and <= 'f' ? c - 'a' + 10
         : c - 'A' + 10;

    // T10 status (parent BsgwTbCEPoQO):
    //   step 1+2 (string-capture): DONE — see EmitSimple `source/.` branch
    //                              that emits Invoke-ProcessSubSource directly.
    //   step 3+4 (pipeline-object): DONE — EmitPassthrough classifies a single
    //                               stdin-substitutable mapped-command operand as
    //                               (Invoke-ProcessSubPipeline { ... }).
    //   step 5+6 (bridge):          DEFERRED — do not route external consumers
    //                               through a fake pipe filename. Unknown,
    //                               seekable, and multi-file consumers stay on
    //                               the temp-file path until a real stream-path
    //                               design is added.
    //   step 7 (trace metric):      DEFERRED until a real bridge branch exists.
    // Default classifier today: temp-file path via Invoke-ProcessSub. This is the
    // correctness-first fallback for unknown external, seekable, and multi-file consumers.
    // RC-8d: capture command-substitution output as bash text (see the CommandSub
    // case for the ForEach-Object rationale). A simple command or pipeline is a
    // valid PowerShell pipeline head, so it pipes directly. A COMPOUND body
    // (if/for/while/case/subshell/brace-group/list/and-or/assignment/arith) emits
    // a PowerShell *statement*, which CANNOT head a pipeline — `switch (...) {} |
    // ForEach-Object` is a parse error ("An empty pipe element is not allowed").
    // Wrap those in `& { ... }` so the statement runs in a child scope (matching
    // bash command-sub subshell semantics) and yields a pipeable result.
    private static string EmitCommandSub(WordPart.CommandSub cs)
    {
        var body = (Command)cs.Body;
        string inner = EmitCaptured(body);
        bool pipeableHead = body is Command.Simple or Command.Pipeline;
        string head = pipeableHead ? inner : $"& {{ {inner} }}";
        return $"$({head} | ForEach-Object {{ Get-BashText $_ }})";
    }

    /// <summary>
    /// Command substitution used in a QUOTED or ASSIGNMENT context (<c>x=$(cmd)</c>,
    /// <c>"$(cmd)"</c>). Unlike <see cref="EmitCommandSub"/>, which yields the per-line
    /// object ARRAY so an UNQUOTED arg word-splits via PowerShell's argument-array
    /// splat, this rejoins the lines with a newline and strips only the TRAILING
    /// newline run — exactly bash's rule for <c>$()</c> in a quoted/assignment context.
    /// Without it the array's every stringification <c>$OFS</c>-joins with a space, so
    /// <c>x=$(printf "a\nb")</c> collapsed to <c>a b</c> and <c>x=$(cat file)</c>
    /// flattened the file to one line.
    /// </summary>
    private static string EmitCommandSubString(WordPart.CommandSub cs)
    {
        var body = (Command)cs.Body;
        string inner = EmitCaptured(body);
        bool pipeableHead = body is Command.Simple or Command.Pipeline;
        string head = pipeableHead ? inner : $"& {{ {inner} }}";
        return $"$((@({head} | ForEach-Object {{ Get-BashText $_ }}) -join \"`n\") -replace '(\\r?\\n)+$','')";
    }

    private static string EmitProcessSub(WordPart.ProcessSub ps)
    {
        string inner = EmitCaptured((Command)ps.Body);
        return $"(Invoke-ProcessSub {{ {inner} }})";
    }

    private static string EmitProcessSubPipeline(WordPart.ProcessSub ps)
    {
        string inner = EmitCaptured((Command)ps.Body);
        return $"(Invoke-ProcessSubPipeline {{ {inner} }})";
    }

    private static string EmitArithSub(WordPart.ArithSub arith)
        => $"$({EmitArithmeticInvocation(arith.Expr)})";

    private static string EmitArithmeticInvocation(ArithmeticSyntax syntax)
    {
        string expr = syntax.Source.Trim();
        string argument = ReferencesPositional(expr)
            ? EmitPositionalArithArgument(expr)
            : PsBuild.SingleQuote(expr);
        return $"Invoke-BashArith {argument}";
    }

    /// <summary>
    /// True when an arithmetic expression references a positional or special
    /// parameter (<c>$1</c>..<c>$9</c>, <c>$#</c>, <c>$@</c>, <c>$*</c>,
    /// <c>$?</c>, <c>$$</c>, <c>$!</c>) — a <c>$</c> immediately followed by a
    /// digit or special-parameter sigil. These are substituted with their
    /// runtime values by <see cref="EmitPositionalArithArgument"/> before the string is
    /// handed to the bash-arithmetic evaluator.
    /// </summary>
    private static bool ReferencesPositional(string expr) =>
        ArithmeticParameterRegex().IsMatch(expr);

    private static System.Text.RegularExpressions.Regex ArithmeticParameterRegex() =>
        new(@"\$\{(?<braced>[0-9]+|[@#*?$!])\}|\$(?<plain>[0-9]|[@#*?$!])");

    /// <summary>
    /// Emit an arithmetic subexpression that references positional/special
    /// parameters. Each reference is replaced by a PowerShell subexpression
    /// producing its runtime value; the surrounding text (ordinary variables,
    /// numbers, operators) is preserved verbatim as single-quoted literal
    /// fragments, and the whole is reassembled by string concatenation and
    /// handed to <c>Invoke-BashArith</c>. This lets the evaluator resolve
    /// ordinary <c>$var</c>/bare-name references itself while still seeing a
    /// concrete numeric literal in place of each positional — so
    /// <c>$(($1 ** 2))</c> becomes <c>$(Invoke-BashArith ('' + &lt;value-of-$1&gt; + ' ** 2'))</c>
    /// and the bash-correct <c>**</c> operator is applied (the legacy verbatim
    /// <c>$()</c> path emitted a literal <c>**</c>, which is not a PowerShell
    /// operator and failed to parse).
    ///
    /// A leading <c>'' + </c> forces string concatenation so a positional whose
    /// value happens to be an integer object does not trigger numeric addition.
    /// Values are concatenated as text (not <c>[int]</c>-cast) so the evaluator
    /// applies bash's expand-then-evaluate semantics — a non-numeric or unset
    /// positional degrades exactly as bash would.
    /// </summary>
    private static string EmitPositionalArithArgument(string expr)
    {
        var rx = ArithmeticParameterRegex();
        var parts = new List<string>();
        int last = 0;
        foreach (System.Text.RegularExpressions.Match m in rx.Matches(expr))
        {
            if (m.Index > last)
                parts.Add(PsBuild.SingleQuote(expr[last..m.Index]));
            string key = m.Groups["braced"].Success
                ? m.Groups["braced"].Value
                : m.Groups["plain"].Value;
            parts.Add(PositionalRefExpr(key));
            last = m.Index + m.Length;
        }
        if (last < expr.Length)
            parts.Add(PsBuild.SingleQuote(expr[last..]));
        if (parts.Count == 0)
            parts.Add("''");
        string joined = "'' + " + string.Join(" + ", parts);
        return $"({joined})";
    }

    /// <summary>
    /// A PowerShell subexpression yielding the runtime value of a positional or
    /// special parameter, for interpolation into an arithmetic string.
    /// <c>$global:BashPositional</c> is set inside function bodies
    /// (<see cref="EmitFunction"/> save/restore); at script top level positional
    /// params come from <c>$args</c>. An unset/empty result is defaulted to
    /// <c>0</c> (see <see cref="ZeroDefault"/>) so a reference to a missing
    /// positional matches bash's zero-fill instead of producing a malformed
    /// arithmetic string.
    /// </summary>
    private static string PositionalRefExpr(string sigil)
    {
        string inner;
        if (int.TryParse(sigil, out int position))
        {
            if (position == 0)
                inner = "$global:BashPositional0";
            else
            {
                int idx = position - 1;
                inner = $"if ($global:BashPositional) {{ $global:BashPositional[{idx}] }} else {{ $args[{idx}] }}";
            }
        }
        else
        {
            inner = sigil switch
            {
                "#" => "if ($global:BashPositional) { $global:BashPositional.Count } else { $args.Count }",
                "@" or "*" => "if ($global:BashPositional) { $global:BashPositional -join ' ' } else { $args -join ' ' }",
                "?" => "$global:LASTEXITCODE",
                "$" => "$PID",
                "!" => "$global:BashBgLastPid",
                _ => "0",
            };
        }
        return ZeroDefault(inner);
    }

    /// <summary>
    /// Wrap a value-producing PowerShell expression so an unset/empty result
    /// stringifies to <c>0</c> rather than an empty fragment. bash treats an
    /// unset positional as <c>0</c> in arithmetic; without this default,
    /// <c>$(($1 * 2))</c> with no args would reassemble to the malformed string
    /// <c>" * 2"</c> and the evaluator would error instead of yielding 0. The
    /// value is stringified (<c>$null</c> → empty) and an empty result is
    /// replaced with <c>0</c>; a non-numeric value passes through for
    /// <c>Invoke-BashArith</c> to resolve as a bare name (also 0), matching
    /// bash's expand-then-evaluate.
    /// </summary>
    private static string ZeroDefault(string valueExpr) =>
        $"$(\"$({valueExpr})\" -replace '^$','0')";

    private static string EmitSimpleVar(string name, bool inDoubleQuote = false)
    {
        // Loop variables emit as $var, not $env:var
        if (_loopVars is not null && _loopVars.Contains(name))
            return $"${name}";

        return PsBuild.TryMapSpecialVar(name, braced: false, inDoubleQuote) ?? $"$env:{name}";
    }

    // -- RC-7: unquoted variable word-splitting -------------------------------
    //
    // bash: an unquoted parameter expansion ($x, not "$x") is subject to word
    // splitting on IFS, and an empty result contributes NO word at all. ps-bash
    // previously emitted the bare variable reference as a single argument, so
    // `x=; echo start $x end` produced `start  end` (two spaces — a spurious
    // empty argument) instead of bash's `start end`.
    //
    // Fix: when a command-argument word is a pure unquoted ordinary variable,
    // emit a splitting expression instead of the bare reference:
    //   (if ([string]::IsNullOrEmpty($env:x)) { @() } else { @($env:x -split '\s+') })
    // PowerShell splats an array argument's elements as separate arguments and
    // drops an empty array entirely, reproducing bash word-splitting + elision.
    // This is intentionally scoped to ordinary variables ($env:NAME and loop
    // vars). Special parameters ($@, $*, $#, $1..$9, $?) keep their existing
    // dedicated expansions, and quoted "$x" is unaffected (it is a DoubleQuoted
    // part, not a bare SimpleVarSub).

    /// <summary>
    /// True when <paramref name="word"/> is a single bare (unquoted) ordinary
    /// variable reference eligible for RC-7 word-splitting: a lone
    /// <see cref="WordPart.SimpleVarSub"/>, or a suffix-less
    /// <see cref="WordPart.BracedVarSub"/>, whose name is an ordinary variable
    /// (not a bash special parameter).
    /// </summary>
    private static bool IsPureUnquotedVarWord(CompoundWord word)
    {
        if (word.Parts.Length != 1)
            return false;

        string name = word.Parts[0] switch
        {
            WordPart.SimpleVarSub vs => vs.Name,
            WordPart.BracedVarSub bvs when bvs.Suffix is null => bvs.Name,
            _ => null!,
        };

        return name is not null && IsOrdinaryVarName(name);
    }

    /// <summary>
    /// True when <paramref name="name"/> is a genuine <c>$env:</c>-backed
    /// ordinary shell variable — i.e. <see cref="EmitSimpleVar"/> renders it as
    /// <c>$env:NAME</c>. This deliberately EXCLUDES:
    /// <list type="bullet">
    ///   <item>tracked loop variables (a <c>for</c>/<c>while</c> binding holds a
    ///   single already-bound value and the emitter emits it bare as
    ///   <c>$i</c> — splatting it would both break that contract and is
    ///   semantically a single value);</item>
    ///   <item>bash special parameters (<c>?</c>, <c>@</c>, <c>*</c>, <c>#</c>,
    ///   <c>$</c>, <c>!</c>, <c>-</c>, <c>_</c>, positional digits, and the
    ///   recognized magic names) which have dedicated expansions;</item>
    ///   <item>PowerShell-builtin-backed names (<c>HOME</c>, <c>null</c>,
    ///   <c>true</c>, <c>false</c>, <c>PWD</c>, <c>LASTEXITCODE</c>) which the
    ///   emitter keeps as bare <c>$NAME</c> references.</item>
    /// </list>
    /// Only a true <c>$env:</c>-backed variable can be word-split by RC-7: that
    /// is the case the differential oracle
    /// (<c>Differential_UnquotedVar_WordSplitsOnSpaces</c>,
    /// <c>Differential_EmptyVar_UnquotedIsOmitted</c>) proves against real bash.
    /// </summary>
    private static bool IsOrdinaryVarName(string name)
    {
        if (name.Length == 0)
            return false;

        // The single source of truth: a name is RC-7-splat-eligible iff
        // EmitSimpleVar would render it as `$env:NAME`. Loop vars, special
        // params, positional digits, and PS-builtin-backed names all take a
        // different branch in EmitSimpleVar and must not be word-split.
        return EmitSimpleVar(name).StartsWith("$env:", StringComparison.Ordinal);
    }

    /// <summary>
    /// Emits the RC-7 word-splitting array expression for a pure unquoted
    /// ordinary variable word: an empty array when the variable is null/empty
    /// (so the word is elided), the IFS-whitespace-split element array
    /// otherwise. This value is assigned to a temp variable and then PowerShell-
    /// splatted (<c>@var</c>) into the command — see
    /// <see cref="EmitCommandWithSplatArgs"/>. True <c>@</c> splatting is
    /// required: a bare <c>$(...)</c> subexpression that yields nothing still
    /// contributes a <c>$null</c> argument, which the command would see as a
    /// spurious empty operand.
    /// </summary>
    private static string EmitUnquotedVarSplitArray(CompoundWord word)
    {
        string varRef = word.Parts[0] switch
        {
            WordPart.SimpleVarSub vs => EmitSimpleVar(vs.Name),
            WordPart.BracedVarSub bvs => EmitSimpleVar(bvs.Name),
            _ => throw new InvalidOperationException(
                "EmitUnquotedVarSplitArray requires a pure unquoted variable " +
                "word; guard with IsPureUnquotedVarWord."),
        };

        return PsBuild.WordSplitArray(varRef);
    }

    /// <summary>
    /// True when any operand in <paramref name="args"/> (optionally skipping the
    /// pipeline-process-sub operand at <paramref name="skipIndex"/>) is a pure
    /// unquoted ordinary variable that needs RC-7 word-splitting.
    /// </summary>
    private static bool HasUnquotedVarSplatArg(
        ImmutableArray<CompoundWord> args, int skipIndex = -1)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (i != skipIndex && IsPureUnquotedVarWord(args[i]))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Builds a command invocation that PowerShell-splats its RC-7 unquoted
    /// variable operands. Each such operand is hoisted to a temp variable
    /// (<c>$__bashsplatN</c>) holding the word-split array, then referenced as
    /// <c>@__bashsplatN</c> so PowerShell expands its elements as separate
    /// arguments — and contributes nothing for an empty array. The whole thing
    /// is wrapped in <c>&amp; { ... }</c> so it stays a single pipeline-safe
    /// command element (the temp assignments never leak into a surrounding
    /// pipeline or and-or list).
    /// <para>
    /// <paramref name="leadingTokens"/> are emitted verbatim before the command
    /// name (e.g. the <c>&amp; </c> call-operator prefix for a quoted command
    /// word). <paramref name="emitPlainArg"/> renders a non-splat operand at a
    /// given index (callers differ: passthrough applies quoting, the general
    /// path may emit a process-sub pipeline).
    /// </para>
    /// </summary>
    private static string EmitCommandWithSplatArgs(
        string commandName,
        ImmutableArray<CompoundWord> args,
        Func<int, string> emitPlainArg,
        string leadingTokens = "",
        string trailingTokens = "")
    {
        var prelude = new StringBuilder();
        var call = new StringBuilder();
        call.Append(leadingTokens);
        call.Append(commandName);

        int splatCounter = 0;
        for (int i = 0; i < args.Length; i++)
        {
            call.Append(' ');
            if (IsPureUnquotedVarWord(args[i]))
            {
                string tempVar = $"$__bashsplat{splatCounter++}";
                prelude.Append(tempVar)
                       .Append(" = ")
                       .Append(EmitUnquotedVarSplitArray(args[i]))
                       .Append("; ");
                call.Append('@').Append(tempVar.AsSpan(1));
            }
            else
            {
                call.Append(emitPlainArg(i));
            }
        }
        call.Append(trailingTokens);

        return $"& {{ {prelude}{call} }}";
    }

    private static string EmitBracedVar(WordPart.BracedVarSub bvs, bool inDoubleQuote = false)
    {
        if (bvs.Suffix is null)
            return EmitSimpleVar(bvs.Name);

        // Array subscript access: ${arr[0]}, ${arr[@]}, ${arr[*]}, ${arr[key]}, plus
        // the subscript-PLUS-operator forms ${arr[@]:1:2}, ${arr[0]##*/}, ${arr[i]:-x}.
        if (bvs.Suffix.StartsWith('['))
        {
            int close = bvs.Suffix.IndexOf(']');
            if (close > 0)
            {
                string subscript = bvs.Suffix[1..close];
                string subOp = bvs.Suffix[(close + 1)..]; // trailing operator, "" if none
                // Special bash array vars use their mapped name, not $Name
                string arrayVar = bvs.Name switch
                {
                    "BASH_VERSINFO" => "$global:BashVersionInfo",
                    var n => $"${n}"
                };

                if (subOp.Length == 0)
                {
                    if (subscript is "@" or "*")
                        return arrayVar; // ${arr[@]} -> $arr (whole array)
                    if (int.TryParse(subscript, out _))
                        return inDoubleQuote ? $"$({arrayVar}[{subscript}])" : $"{arrayVar}[{subscript}]";
                    return inDoubleQuote ? $"$({arrayVar}['{SqEsc(subscript)}'])" : $"{arrayVar}['{SqEsc(subscript)}']";
                }

                // Whole-array operator: only slicing (${arr[@]:off:len}) maps cleanly to
                // a PowerShell range index; other ops degrade to the whole array.
                if (subscript is "@" or "*")
                    return EmitArraySlice(arrayVar, subOp, inDoubleQuote) ?? arrayVar;

                // Single element: the indexed value is a scalar string — apply the same
                // scalar suffix operator the plain-variable path uses.
                string elemRef = int.TryParse(subscript, out _)
                    ? $"{arrayVar}[{subscript}]"
                    : $"{arrayVar}['{SqEsc(subscript)}']";
                return EmitScalarSuffix(elemRef, subOp, inDoubleQuote)
                    ?? (inDoubleQuote ? $"$({elemRef})" : elemRef);
            }
        }

        // Array length: ${#arr[@]} or ${#arr[*]} -> $arr.Count
        if (bvs.Suffix.StartsWith("#[") && bvs.Suffix.EndsWith(']'))
        {
            string subscript = bvs.Suffix[2..^1]; // strip #[ and ]
            if (subscript is "@" or "*")
                return inDoubleQuote ? $"$(${bvs.Name}.Count)" : $"${bvs.Name}.Count";
        }

        string varRef = EmitSimpleVar(bvs.Name);

        // Name-prefix expansion: ${!prefix*} / ${!prefix@} -> the NAMES of
        // variables starting with `prefix`. ps-bash models bash variables as
        // env vars, so match over env:. (bash * joins on IFS, @ keeps words
        // separate; both yield the same name set here.)
        if (bvs.Suffix == "!names")
        {
            string expr = $"@(Get-ChildItem env: | Where-Object {{ $_.Name -like '{bvs.Name}*' }} | ForEach-Object {{ $_.Name }})";
            return inDoubleQuote ? $"$({expr})" : expr;
        }

        // Array keys/indices: ${!arr[@]}. bash gives the KEYS of an associative
        // array but the numeric INDICES (0..n-1) of an indexed array — and `.Keys`
        // does not exist on a PowerShell array, so the old emission crashed
        // ("Object reference not set") on a normal `arr=(a b c)`. Branch on the
        // runtime type; guard the empty array (0..-1 would descend to @(0,-1)).
        //
        // As a BARE argument use the array-subexpression `@(...)`, not `$(...)`:
        // a `$(...)` that yields an empty collection still binds ONE $null
        // positional argument (`echo ${!arr[@]}` on an empty array → the cmdlet
        // sees a lone null → NPE in ConvertFromBashArgs). `@(...)` unrolls an
        // empty array to ZERO arguments (bash parity: empty line) and a populated
        // one to N separate args. Inside double quotes the expansion is joined
        // into the string, so `$(...)` is correct there.
        if (bvs.Suffix.StartsWith("!"))
        {
            string idxBody = $"if (${bvs.Name} -is [System.Collections.IDictionary]) "
                + $"{{ ${bvs.Name}.Keys }} elseif (${bvs.Name}.Count -gt 0) "
                + $"{{ 0..(${bvs.Name}.Count - 1) }} else {{ @() }}";
            return inDoubleQuote ? $"$({idxBody})" : $"@({idxBody})";
        }

        // Scalar suffix operators (default/assign/alt/error, removal, replace, slice,
        // case, @-transform). Shared with array-element expansions like ${arr[0]##*/}.
        return EmitScalarSuffix(varRef, bvs.Suffix, inDoubleQuote, bvs.ArgWord) ?? varRef;
    }

    // Emit a scalar parameter-expansion suffix operator applied to an arbitrary base
    // reference (a plain variable, or an array element like `$arr[0]`). Returns null if
    // `suffix` is not a scalar operator handled here — the caller falls back to the bare
    // reference. Keeping this independent of the variable name lets ${arr[0]##*/},
    // ${arr[i]:-x}, etc. reuse the exact same operator semantics as ${VAR##*/}.
    private static string? EmitScalarSuffix(string varRef, string suffix, bool inDoubleQuote,
        ImmutableArray<WordPart>? argWord = null)
    {
        string open = inDoubleQuote ? "$(" : "(";
        char q = inDoubleQuote ? '\'' : '"';

        // The word-bearing operators (default/assign/alt/error message) carry a DECOMPOSED
        // argument word (parser populates BracedVarSub.ArgWord). Emit it through the normal
        // word machinery so $var/$(cmd)/"$@" expand — the ${1+"$@"} fix. FlattenParts always
        // yields a PowerShell double-quoted string, valid in both the bare `(...)` and the
        // double-quote `$(...)` contexts (the subexpression reopens a fresh parse scope).
        // When ArgWord is null (array-element path, or a non-word operator) fall back to the
        // raw-slice literal so existing behavior is unchanged.
        // Bare context: the double-quoted flatten is safe (not nested) and matches the
        // historical literal emission. Inside an outer "$( … )" string a nested double-quoted
        // value mis-parses when empty / quote-bearing, so use the single-quote-safe path there.
        string ArgVal(string rawSlice) =>
            !argWord.HasValue ? $"{q}{rawSlice}{q}"
            : inDoubleQuote ? EmitBracedArgWordValue(argWord.Value)
            : FlattenPartsToDoubleQuotedString(argWord.Value);

        // Length: ${#VAR}
        if (suffix == "#")
            return inDoubleQuote ? $"$({varRef}.Length)" : $"{varRef}.Length";

        // Default value: ${VAR:-default}
        if (suffix.StartsWith(":-"))
            return $"{open}{varRef} ?? {ArgVal(suffix[2..])})";
        // Assign default: ${VAR:=default}
        if (suffix.StartsWith(":="))
            return $"{open}{varRef} ?? ({varRef} = {ArgVal(suffix[2..])}))";
        // Use alternative: ${VAR:+alt}
        if (suffix.StartsWith(":+"))
            return $"{open}{varRef} ? {ArgVal(suffix[2..])} : {q}{q})";
        // Error if unset: ${VAR:?message}
        if (suffix.StartsWith(":?"))
            return $"{open}{varRef} ?? $(throw {ArgVal(suffix[2..])}))";

        // Colon-less unset-only variants: ${VAR-w} ${VAR=w} ${VAR+w} ${VAR?msg}.
        // Bash distinguishes these (act only when UNSET) from the `:`-prefixed forms
        // (act when unset OR empty); ps-bash models vars as env vars where `$env:X`
        // is $null when unset, so `??` already approximates the unset-only test.
        if (suffix.StartsWith("-"))
            return $"{open}{varRef} ?? {ArgVal(suffix[1..])})";
        if (suffix.StartsWith("="))
            return $"{open}{varRef} ?? ({varRef} = {ArgVal(suffix[1..])}))";
        if (suffix.StartsWith("+"))
            return $"{open}{varRef} ? {ArgVal(suffix[1..])} : {q}{q})";
        if (suffix.StartsWith("?"))
            return $"{open}{varRef} ?? $(throw {ArgVal(suffix[1..])}))";

        // Remove suffix: ${VAR%%pattern} (longest) / ${VAR%pattern} (shortest).
        // Both anchor the pattern at end-of-string ($). For the LONGEST match a
        // greedy pattern starting at the leftmost feasible position is correct.
        // For the SHORTEST suffix a lazy quantifier does NOT help — the match
        // still starts at the leftmost position, so `foo.bar.baz` %`.*` wrongly
        // gave `foo` instead of `foo.bar`. Instead capture a GREEDY prefix and
        // keep it: `^(.*)PAT$` -> `$1` leaves the shortest trailing match for PAT.
        // Every embed below lands inside a PowerShell single-quoted string, so any literal
        // ' in the pattern/replacement (e.g. from a $'…' word inside ${VAR##pat}) must be
        // doubled or it breaks out and emits unparseable PowerShell (grammar-fuzz find).
        if (suffix.StartsWith("%%"))
            return $"{open}{varRef} -replace '{SqEsc(GlobToRegex(suffix[2..], lazy: false))}$','')";
        if (suffix.StartsWith("%"))
            return $"{open}{varRef} -replace '^(.*){SqEsc(GlobToRegex(suffix[1..], lazy: false))}$','$1')";

        // Remove prefix: ${VAR##pattern} (greedy) / ${VAR#pattern} (lazy).
        if (suffix.StartsWith("##"))
            return $"{open}{varRef} -replace '^{SqEsc(GlobToRegex(suffix[2..], lazy: false))}','')";
        if (suffix.StartsWith("#"))
            return $"{open}{varRef} -replace '^{SqEsc(GlobToRegex(suffix[1..], lazy: true))}','')";

        // Replace first: ${VAR/find/replace} — instance overload .Replace(str, rep, 1).
        if (suffix.StartsWith("/") && !suffix.StartsWith("//"))
        {
            var parts = suffix[1..].Split('/', 2);
            string find = parts[0], replace = parts.Length > 1 ? parts[1] : "";
            return $"{open}([regex][regex]::Escape('{SqEsc(find)}')).Replace({varRef}, '{SqEsc(replace)}', 1))";
        }
        // Replace all: ${VAR//find/replace} — escape find literally; 2-arg overload = all.
        if (suffix.StartsWith("//"))
        {
            var parts = suffix[2..].Split('/', 2);
            string find = parts[0], replace = parts.Length > 1 ? parts[1] : "";
            return $"{open}([regex][regex]::Escape('{SqEsc(find)}')).Replace({varRef}, '{SqEsc(replace)}'))";
        }

        // Slice: ${VAR:offset:length} or ${VAR:offset}. The body is trimmed so a
        // negative offset written with the disambiguating space — ${VAR: -2}
        // (without the space it would be the ${VAR:-default} operator) — parses.
        // A negative offset counts from the end (bash), so it maps to
        // Length - |offset|; indices are clamped so an out-of-range slice yields
        // "" instead of throwing (.NET Substring is strict where bash is lenient).
        if (suffix.StartsWith(":") && suffix.Length > 1)
        {
            var sliceBody = suffix[1..].Trim();
            bool looksNumeric = sliceBody.Length > 0 && (char.IsDigit(sliceBody[0])
                || (sliceBody[0] == '-' && sliceBody.Length > 1 && char.IsDigit(sliceBody[1])));
            if (looksNumeric)
            {
                var sliceParts = sliceBody.Split(':', 2);
                if (int.TryParse(sliceParts[0].Trim(), out int offset))
                {
                    string len = $"{varRef}.Length";
                    string start = offset >= 0
                        ? $"[Math]::Min({offset}, {len})"
                        : $"[Math]::Max(0, {len} - {-offset})";
                    string? expr = null;
                    if (sliceParts.Length == 1)
                        expr = $"{varRef}.Substring({start})";
                    else if (int.TryParse(sliceParts[1].Trim(), out int length) && length >= 0)
                        expr = $"{varRef}.Substring({start}, [Math]::Min({length}, {len} - ({start})))";
                    if (expr is not null)
                        return inDoubleQuote ? $"$({expr})" : expr;
                }
            }

            // Non-literal offset/length (e.g. ${x:$i}, ${x:$i:$n}, ${x:i+1}). Bash
            // treats both as ARITHMETIC expressions, so route each through
            // Invoke-BashArith (which resolves bare and $-prefixed variables) in a
            // scriptblock that evaluates each component ONCE and normalizes/clamps at
            // runtime. Without this, ${x:$i} failed the digit test above and silently
            // fell through to the bare variable — dropping the slice entirely.
            {
                var sliceParts = sliceBody.Split(':', 2);
                string offArith = $"[int](Invoke-BashArith {PsBuild.SingleQuote(sliceParts[0].Trim())})";
                var sb = new StringBuilder("& { $__psbS = [string](");
                sb.Append(varRef);
                sb.Append("); $__psbO = ").Append(offArith).Append("; ");
                sb.Append("if ($__psbO -lt 0) { $__psbO = [Math]::Max(0, $__psbS.Length + $__psbO) } ");
                sb.Append("else { $__psbO = [Math]::Min($__psbO, $__psbS.Length) }; ");
                if (sliceParts.Length == 2 && sliceParts[1].Trim().Length > 0)
                {
                    string lenArith = $"[int](Invoke-BashArith {PsBuild.SingleQuote(sliceParts[1].Trim())})";
                    sb.Append("$__psbL = ").Append(lenArith).Append("; ");
                    // bash: negative length ends |length| chars from the string end.
                    sb.Append("$__psbE = if ($__psbL -ge 0) { $__psbO + $__psbL } else { $__psbS.Length + $__psbL }; ");
                    sb.Append("$__psbC = [Math]::Max(0, [Math]::Min($__psbE, $__psbS.Length) - $__psbO); ");
                    sb.Append("$__psbS.Substring($__psbO, $__psbC) }");
                }
                else
                {
                    sb.Append("$__psbS.Substring($__psbO) }");
                }
                // Wrap in a subexpression in EVERY context: a bare `& { … }` is not a
                // valid command argument (`&` would parse as the background operator).
                return $"$({sb})";
            }
        }

        // Case conversion: ${VAR^^} ${VAR,,} ${VAR^} ${VAR,}
        if (suffix == "^^")
            return inDoubleQuote ? $"$({varRef}.ToUpper())" : $"{varRef}.ToUpper()";
        if (suffix == ",,")
            return inDoubleQuote ? $"$({varRef}.ToLower())" : $"{varRef}.ToLower()";
        if (suffix == "^")
            return $"{open}{varRef}.Substring(0,1).ToUpper() + {varRef}.Substring(1))";
        if (suffix == ",")
            return $"{open}{varRef}.Substring(0,1).ToLower() + {varRef}.Substring(1))";

        // Transform operators ${VAR@op} (bash 4.4+/5.1). @Q (quote-for-reuse) and the case
        // transforms map cleanly; @E/@P/@A/@a have no PowerShell equivalent and degrade to
        // the bare value (documented gap, emitter-strategy.md §6) — never silently dropped.
        if (suffix.StartsWith("@") && suffix.Length >= 2)
        {
            switch (suffix[1])
            {
                case 'Q':
                    return $"{open}\"'\" + ({varRef} -replace \"'\",\"'\\''\") + \"'\")";
                case 'U':
                    return inDoubleQuote ? $"$({varRef}.ToUpper())" : $"{varRef}.ToUpper()";
                case 'L':
                    return inDoubleQuote ? $"$({varRef}.ToLower())" : $"{varRef}.ToLower()";
                case 'u':
                    return $"{open}{varRef}.Substring(0,1).ToUpper() + {varRef}.Substring(1))";
                case 'l':
                    return $"{open}{varRef}.Substring(0,1).ToLower() + {varRef}.Substring(1))";
                default:
                    return varRef;
            }
        }

        return null;
    }

    // Emit a bash array slice ${arr[@]:offset[:length]} as a PowerShell range index.
    // Returns null when `op` is not a `:`-prefixed numeric slice (caller degrades to the
    // whole array). bash element indices map directly: offset..(offset+length-1), and
    // negative offsets index from the end (PowerShell supports negative range bounds).
    private static string? EmitArraySlice(string arrayVar, string op, bool inDoubleQuote)
    {
        if (!op.StartsWith(":"))
            return null;
        var body = op[1..].Trim();
        var parts = body.Split(':', 2);
        if (!int.TryParse(parts[0].Trim(), out int offset))
            return null;

        int? length = null;
        if (parts.Length == 2)
        {
            if (!int.TryParse(parts[1].Trim(), out int len))
                return null;
            length = len;
        }

        // ${arr[@]:offset[:length]} with bash clamping, computed at RUNTIME because
        // the element count is not known until execution. The old code emitted a raw
        // PowerShell range `$arr[offset..end]`; when offset ran past the end (e.g.
        // `${arr[@]:2}` on a 2-element array) that became a DESCENDING range
        // (`$arr[2..1]`), which PowerShell walks backwards — returning the trailing
        // elements instead of the empty slice bash produces. The scriptblock
        // normalizes a negative offset (from the end), clamps to [0, Count], and
        // yields @() whenever the resulting count is <= 0 so no reversed range forms.
        var sb = new StringBuilder("& { $__psbA = @(");
        sb.Append(arrayVar);
        sb.Append("); $__psbO = ").Append(offset).Append("; ");
        sb.Append("if ($__psbO -lt 0) { $__psbO = $__psbA.Count + $__psbO }; ");
        sb.Append("$__psbO = [Math]::Max(0, [Math]::Min($__psbO, $__psbA.Count)); ");
        if (length is int litLen)
            sb.Append("$__psbN = ").Append(litLen).Append("; ");
        else
            sb.Append("$__psbN = $__psbA.Count - $__psbO; ");
        sb.Append("if ($__psbN -lt 0) { $__psbN = 0 }; ");
        sb.Append("$__psbN = [Math]::Min($__psbN, $__psbA.Count - $__psbO); ");
        sb.Append("if ($__psbN -le 0) { @() } else { $__psbA[$__psbO..($__psbO + $__psbN - 1)] } }");
        // Always wrap the scriptblock invocation in a subexpression: a bare `& { … }`
        // is NOT a valid command argument (`cmd & {…}` parses `&` as the PS background
        // operator). `$(& { … })` is a valid argument in every context and splats the
        // array like the old bare `$a[1..2]` did.
        return $"$({sb})";
    }

    private static void AppendDoubleQuotedInner(StringBuilder sb, ImmutableArray<WordPart> parts)
    {
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part is WordPart.BracedVarSub bvs)
                sb.Append(EmitBracedVar(bvs, inDoubleQuote: true));
            else if (part is WordPart.SimpleVarSub vs)
            {
                // Check if next part starts with ':' — PowerShell would misparse as a drive reference.
                // Use ${name} bracing to prevent this.
                bool needsBracing = i + 1 < parts.Length && NextPartNeedsBracing(parts[i + 1]);
                if (needsBracing)
                    sb.Append(EmitSimpleVarBraced(vs.Name));
                else
                    sb.Append(EmitSimpleVar(vs.Name, inDoubleQuote: true));
            }
            else if (part is WordPart.CommandSub qcs)
                // Quoted `"$(cmd)"` preserves internal newlines (see EmitCommandSubString);
                // the array-yielding EmitCommandSub would $OFS-join with spaces here.
                sb.Append(EmitCommandSubString(qcs));
            else if (part is WordPart.Literal lit)
            {
                // Inside double quotes, bash's \$ \" \` are each parsed as Literal("$"/"\""/"`")
                // — the parser consumed the backslash and stored only the escaped char (see
                // BashParser.Words ParseDoubleQuoted). In a PS double-quoted string `$` starts
                // variable expansion, `"` ends the string, and backtick is the escape char, so
                // each must be backtick-escaped. PsBuild.EscapeForDoubleQuote does this in the
                // load-bearing order (backtick first). Mirrors FlattenPartsToDoubleQuotedString.
                sb.Append(PsBuild.EscapeForDoubleQuote(lit.Value));
            }
            else
                sb.Append(EmitWordPart(part));
        }
    }

    private static string EmitDoubleQuoted(WordPart.DoubleQuoted dq)
    {
        var sb = new StringBuilder();
        sb.Append('"');
        AppendDoubleQuotedInner(sb, dq.Parts);
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

        return PsBuild.TryMapSpecialVar(name, braced: true, inDoubleQuote: true) ?? $"${{env:{name}}}";
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
            // BoolExpr in && / || chains must propagate exit code so the chain works correctly.
            // [void] would suppress the boolean result AND discard $LASTEXITCODE, breaking the chain.
            // Instead, evaluate the expression and set $global:LASTEXITCODE explicitly.
            if (cmd is Command.BoolExpr boolExprInChain)
            {
                // Use the BARE boolean expression here (not Emit(), which now produces the silent
                // statement form for a standalone BoolExpr) — this site supplies its own exit-code
                // wrapper below.
                var boolText = "(" + EmitBoolExprInner(boolExprInChain) + ")";
                sb.Append(PsBuild.SetExitFromBool(boolText));
            }
            // ShAssignment in && / || chains: wrap so PS doesn't output the value.
            // A multi-pair assignment (e.g. `TEMP=x TMP=y`, as Claude Code's Bash
            // tool emits in its env-setup preamble) emits two statements joined by
            // "; ". PowerShell's grouping `(...)` cannot hold a statement list
            // ("Missing closing ')'"), only the subexpression `$(...)` can — so use
            // `[void]$(...)` for the multi-statement case. A single pair stays in the
            // cheaper `[void](...)` form (bit-identical to prior output).
            else if (cmd is Command.ShAssignment)
            {
                var assignText = Emit(cmd);
                sb.Append(PsBuild.VoidStatement(assignText));
            }
            else if (cmd is Command.Pipeline { Negated: true } negPipelineInChain)
            {
                // Negated pipeline (! cmd) in && / || chain: mirror the BoolExpr-in-chain
                // pattern. PS pipeline-chain operators check $?, not $LASTEXITCODE.
                // Use Write-Error to set $?=false when negation yields failure so the
                // chain operators observe the correct success/failure state.
                var unnegated = negPipelineInChain with { Negated = false };
                sb.Append(PsBuild.SetExitFromBool(PsBuild.ExitCodeTest(EmitPipeline(unnegated), negate: true)));
            }
            else if (cmd is Command.Pipeline pipeline)
            {
                // Pipeline in && / || chain: bridge $LASTEXITCODE to $? so PS chain
                // operators (&&/||) observe runtime exit codes (e.g. grep -q).
                //
                // Pattern: run the pipeline as a plain statement, then use
                //   $(if ($global:LASTEXITCODE -ne 0) { Write-Error '' })
                // as the actual chain-operator LHS. PowerShell's $(if ...) correctly
                // propagates $?=false to the && / || operator when the else-branch
                // calls Write-Error. A $(& { ... }) wrapper does NOT propagate $?.
                var pipelineText = EmitPipeline(pipeline);
                sb.Append(pipelineText);
                sb.Append("; " + PsBuild.SignalFailIfNonZero());
            }
            else if (cmd is Command.Simple simple)
            {
                // Some simple-command rewrites, notably cd, emit PowerShell statements
                // such as assignments plus if/else blocks. Pipeline-chain operators
                // require a pipeline operand, so wrap only those statement rewrites.
                var simpleText = EmitSimple(simple);
                sb.Append(NeedsChainOperandSubexpression(simpleText) ? $"$({simpleText})" : simpleText);
            }
            else
            {
                sb.Append(Emit(cmd));
            }
        }

        return sb.ToString();
    }

    private static bool NeedsChainOperandSubexpression(string emitted)
    {
        if (emitted.StartsWith("$(", StringComparison.Ordinal))
            return false;

        return emitted.Contains(';') || emitted.StartsWith("if ", StringComparison.Ordinal);
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
            if (i > 0 && cmd is Command.Simple simple)
            {
                // true/false as pipe targets need a cmdlet form, not a subexpression.
                // $($global:LASTEXITCODE = 0; [void]$true) cannot be a pipeline segment.
                var word0 = simple.Words.Length == 1 ? GetLiteralValue(simple.Words[0]) : null;
                // Redirects are allowed here (e.g. `… | true 2>&1`): `true`/`false` ignore
                // stdin, so we emit Out-Null and append any redirects. Excluding the redirect
                // case sent it to the Emit fallback, which emitted the bare subexpression
                // `$($global:LASTEXITCODE = 0; [void]$true)` — not a valid pipeline segment.
                if (simple.Words.Length == 1 && simple.EnvPairs.IsEmpty)
                {
                    if (word0 == "true")
                    {
                        // Consume all piped input and succeed (exit 0).
                        // Out-Null is a valid pipeline cmdlet; after the pipeline set LASTEXITCODE=0.
                        bool isLast = i == pipeline.Commands.Length - 1;
                        sb.Append("Out-Null");
                        EmitPipeTargetRedirects(simple, sb);
                        if (isLast) sb.Append("; $global:LASTEXITCODE = 0");
                        continue;
                    }
                    if (word0 == "false")
                    {
                        // Consume all piped input and fail (exit 1).
                        bool isLast = i == pipeline.Commands.Length - 1;
                        sb.Append("Out-Null");
                        EmitPipeTargetRedirects(simple, sb);
                        if (isLast) sb.Append("; $($global:LASTEXITCODE = 1; Write-Error '' -ErrorAction SilentlyContinue)");
                        continue;
                    }
                }
                if (TryEmitMappedCommand(simple, out var mapped))
                {
                    sb.Append(mapped);
                    // Append any redirects from the pipe target (TryEmitMappedCommand only handles args)
                    EmitPipeTargetRedirects(simple, sb);
                }
                else
                    sb.Append(Emit(cmd));
            }
            else if (pipeline.Commands.Length > 1 && IsCompoundPipelineStage(cmd))
            {
                // A compound command used as a pipeline stage emits PowerShell
                // *statements*, not a pipeable expression: a subshell / brace group
                // becomes `try { } finally { }`; a for/while loop becomes
                // `$__psbash_iter = 0; foreach (...) { ... }`; an `if`/`case` becomes
                // an `if`/`switch` block. None of these are a valid bare pipeline
                // segment — PowerShell rejects `foreach (...) {} | sort` with
                // "An empty pipe element is not allowed". Wrapping in `& { ... }`
                // turns the statement list into a single pipeable command, and
                // matches bash, which runs every pipe stage in its own subshell.
                // This applies at ANY position (incl. the first stage), not just i > 0.
                sb.Append($"& {{ {Emit(cmd)} }}");
            }
            else
                sb.Append(Emit(cmd));
        }

        var body = sb.ToString();

        // Fused lane: an all-mapped, terminal-bound, plain-`|` pipeline runs inside
        // Invoke-BashFusedPipeline so its output returns to the launcher in a few
        // large batched frames instead of one IPC frame per line. IsFusablePipeline
        // already excludes negated pipelines, so this never collides with the
        // negation tail below.
        if (_captureDepth == 0 && FusionEnabled() && IsFusablePipeline(pipeline))
        {
            // Phase-2b: also emit a structured stage list when every stage's args are
            // plain compile-time literals, so the runtime can run a true streaming
            // line→line chain (no per-line PSObject). Any non-literal arg (variable,
            // command-sub, glob, brace expansion) leaves the stage list off and the
            // cmdlet uses the phase-2a scriptblock (still batched, still correct).
            var stages = TryBuildFusedStages(pipeline);
            return stages is null
                ? $"Invoke-BashFusedPipeline {{ {body} }}"
                : $"Invoke-BashFusedPipeline -Stages {stages} -Fallback {{ {body} }}";
        }

        if (pipeline.Negated)
            // Negate the bash exit code stored in $global:LASTEXITCODE.
            // Using $global:LASTEXITCODE (not PowerShell's $?) ensures we check the bash
            // exit code set by the runtime (e.g. Invoke-BashGrep sets it to 1 on no-match)
            // rather than PowerShell's own cmdlet-success flag.
            body += "; $global:LASTEXITCODE = if ($global:LASTEXITCODE -eq 0) { 1 } else { 0 }";

        return body;
    }

    /// <summary>
    /// True for compound commands whose emission is a PowerShell statement (or
    /// statement list) rather than a pipeable expression. Such a command must be
    /// wrapped in <c>&amp; { ... }</c> when it appears as a stage of a multi-stage
    /// pipeline, or PowerShell rejects the pipe ("An empty pipe element is not
    /// allowed"). Simple commands and and-or/command lists are not included —
    /// simple commands already emit pipeable cmdlet calls, and lists never appear
    /// directly as a pipeline stage (bash grammar: a stage is compound-or-simple).
    /// </summary>
    private static bool IsCompoundPipelineStage(Command cmd) =>
        cmd is Command.Subshell or Command.BraceGroup or Command.ForIn or Command.ForArith
            or Command.While or Command.If or Command.Case or Command.ArithCommand
            or Command.ShFunction;

    private static void EmitPipeTargetRedirects(Command.Simple cmd, StringBuilder sb)
    {
        if (cmd.Redirects.IsEmpty) return;
        AppendRedirectTail(sb, cmd.Redirects);
    }

    /// <summary>
    /// Append a command's redirect tail to <paramref name="sb"/>: every NON-stdout-file
    /// redirect (<c>2&gt;&amp;1</c>, <c>&amp;&gt;</c>, <c>&lt;&amp;-</c>, …) is emitted
    /// inline via <see cref="EmitRedirect"/>, while the FIRST stdout file redirect
    /// (<c>&gt; file</c> / <c>&gt;&gt; file</c>) is piped through
    /// <c>Invoke-BashRedirect</c> instead — a raw <c>&gt;</c> left a lingering file
    /// handle that threw IOException in chained commands.
    /// <para>Single source for EmitSimple, EmitSubshell, and
    /// <see cref="EmitPipeTargetRedirects"/>: the subshell <c>&lt; file</c> bug came
    /// from this selection being copy-pasted three times and a fix landing in only one.
    /// Input redirects (<c>&lt; file</c>) are the caller's responsibility — they wrap
    /// the whole command (<c>Get-Content | …</c>), so partition them out first.</para>
    /// </summary>
    private static void AppendRedirectTail(StringBuilder sb, IEnumerable<Redirect> redirects)
    {
        Redirect? fileRedirect = null;
        foreach (var redirect in redirects)
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
    /// Apply a COMPOUND command's trailing redirects (<c>while ...; done &lt; in &gt; out</c>,
    /// <c>{ ...; } &gt; out</c>, …) to its already-emitted body. Unlike a subshell this does
    /// NOT open a new location/variable scope — a loop / conditional / brace-group runs in the
    /// current shell — it only groups the statement list so a single redirect covers the whole
    /// construct. The emitted body is wrapped in <c>&amp; { ... }</c> (a valid pipeline segment),
    /// output/other redirects are appended via the shared <see cref="AppendRedirectTail"/>, and the
    /// LAST input redirect (bash: last wins) feeds the group via <c>Get-Content &lt;file&gt; | …</c>.
    /// Empty/absent redirects return the body untouched, so non-redirected compounds are unchanged.
    /// <para>
    /// STDIN-DELIVERY LIMITATION (parity with <see cref="EmitSubshell"/>): the
    /// <c>Get-Content &lt;file&gt; |</c> prefix places the file on the group's PowerShell pipeline,
    /// so it reaches any body that consumes <c>$input</c> — the <c>while read</c> form (which drains
    /// <c>$input</c> explicitly) and any inner stage that reads the pipeline. A bare stdin-reading
    /// command inside a non-<c>while-read</c> body (e.g. <c>read</c>/<c>cat</c> with no operand in
    /// <c>if …; read x; fi &lt; f</c>) does NOT auto-receive it, exactly as the identical
    /// <c>(read x) &lt; f</c> subshell emission does not. This is a pre-existing, codebase-wide
    /// limitation of the <c>Get-Content | &amp; { … }</c> stdin model, NOT a regression introduced
    /// here: before this fix the redirect was dropped entirely, so those readers already fell back
    /// to the console. A general per-command fd-0 bridge is out of scope for this node-set fix and
    /// would have to land uniformly for subshells too.
    /// </para>
    /// </summary>
    private static string ApplyCompoundRedirects(string body, ImmutableArray<Redirect> redirects)
    {
        if (redirects.IsDefaultOrEmpty)
            return body;

        Redirect? inputRedirect = null;
        var tailRedirects = new List<Redirect>(redirects.Length);
        foreach (var redirect in redirects)
        {
            if (redirect.Fd == 0 && redirect.Op == "<")
                inputRedirect = redirect; // last input redirect wins (bash)
            else
                tailRedirects.Add(redirect);
        }

        var sb = new StringBuilder("& { ");
        sb.Append(body);
        sb.Append(" }");
        AppendRedirectTail(sb, tailRedirects);
        string result = sb.ToString();

        if (inputRedirect is not null)
        {
            var inTarget = TransformRedirectTarget(EmitWord(inputRedirect.Target));
            // /dev/null maps to $null; `Get-Content $null` errors, and empty stdin is the
            // same as no piped input, so skip the pipe in that case (matches EmitSubshell).
            if (inTarget != "$null")
                result = $"Get-Content {inTarget} | {result}";
        }

        return result;
    }

    private static string EmitCd(ImmutableArray<CompoundWord> args)
    {
        string targetExpr;
        // `cd -` switches to the previous directory ($OLDPWD) and, like bash, echoes
        // the directory it lands in. Bash also records $OLDPWD on EVERY successful cd,
        // which is what makes `-` work — see the success branch below.
        var isDash = false;
        if (args.IsEmpty)
        {
            targetExpr = "$HOME";
        }
        else if (GetLiteralValue(args[0]) is { } literal)
        {
            if (literal == "-")
            {
                targetExpr = "$env:OLDPWD";
                isDash = true;
            }
            else if (literal == "~")
                targetExpr = "$HOME";
            else if (literal.StartsWith("~/") || literal.StartsWith("~\\"))
                targetExpr = "$HOME + " + QuotePsString("\\" + literal[2..]);
            else if (TryTranslateMsysDrivePath(literal, out var winPath))
                targetExpr = QuotePsString(winPath);
            else
                targetExpr = QuotePsString(literal);
        }
        else if (TryReconstructWindowsPath(args[0], out var winLiteral))
        {
            // A bare Windows path like `cd C:\Users\andyb\work` — bash's lexer treats
            // each `\X` as an escape and drops the backslash (`C:\Users` -> `C:Users`),
            // which is correct POSIX but useless on Windows. Reconstruct the path with
            // the separators restored so the drive path resolves as the user intended.
            targetExpr = QuotePsString(winLiteral);
        }
        else
        {
            var emitted = EmitWord(args[0]);
            if (emitted.StartsWith("$HOME\\", StringComparison.Ordinal) ||
                emitted.StartsWith("$HOME/", StringComparison.Ordinal))
                targetExpr = "$HOME + " + QuotePsString("\\" + emitted["$HOME\\".Length..]);
            else if (emitted.StartsWith('~'))
                // Unresolved ~user / ~N (dirstack) has no PowerShell equivalent and degrades
                // to the literal bareword text (WordPart.TildeSub's `_ => $"~{ts.User}"`
                // branch) — quote it through PsBuild so the assignment is a valid PS string
                // literal, not an invalid bareword (`$__psbash_cd_target = ~user`).
                targetExpr = QuotePsString(emitted);
            else
                targetExpr = emitted;
        }

        // The success branch captures the dir we are leaving into $OLDPWD BEFORE
        // overwriting CurrentDirectory, so the next `cd -` can return to it.
        var resolveAndAct =
            "$__psbash_cd_resolved = [System.IO.Path]::GetFullPath([string]$__psbash_cd_target, [System.Environment]::CurrentDirectory); " +
            "if ([System.IO.Directory]::Exists($__psbash_cd_resolved)) { " +
            "$env:OLDPWD = [System.Environment]::CurrentDirectory; " +
            "$global:__PsBashCwd = $__psbash_cd_resolved; " +
            "[System.Environment]::CurrentDirectory = $__psbash_cd_resolved; " +
            "$env:PWD = $__psbash_cd_resolved; " +
            "Set-Location -LiteralPath $__psbash_cd_resolved -ErrorAction SilentlyContinue; " +
            (isDash ? "Write-Output $__psbash_cd_resolved; " : "") +
            "$global:LASTEXITCODE = 0 " +
            "} else { Write-Error -Message (\"cd: \" + $__psbash_cd_target + \": No such file or directory\") -ErrorAction Continue; $global:LASTEXITCODE = 1 }";

        var prefix = "$__psbash_cd_target = " + targetExpr + "; ";
        if (isDash)
        {
            // `cd -` with no prior directory: bash prints "cd: OLDPWD not set" and fails
            // rather than trying to resolve an empty path (GetFullPath would throw).
            return prefix +
                "if ([string]::IsNullOrEmpty([string]$__psbash_cd_target)) { " +
                "Write-Error -Message 'cd: OLDPWD not set' -ErrorAction Continue; $global:LASTEXITCODE = 1 " +
                "} else { " + resolveAndAct + " }";
        }

        return prefix + resolveAndAct;
    }

    /// <summary>
    /// Reconstructs a bare Windows-style path from a word whose backslash separators
    /// bash's lexer swallowed as escapes. In bash <c>C:\Users</c> decomposes to
    /// <c>Literal("C:")</c> + <c>EscapedLiteral("U")</c> + <c>Literal("sers")</c> — the
    /// backslash is gone. A Windows user typing that path means it as a separator, so we
    /// put the backslash back for every "useless" escape (an escaped ordinary character)
    /// while preserving genuine bash escapes (<c>\ </c> space, <c>\"</c>, <c>\'</c>,
    /// <c>\$</c>, <c>\`</c>). Returns <c>false</c> for any word that carries an
    /// expansion / glob / tilde part (not a plain path) or contains no restorable
    /// backslash escape (nothing to fix — let normal emission handle it).
    /// </summary>
    private static bool TryReconstructWindowsPath(CompoundWord word, out string path)
    {
        path = "";
        var sb = new System.Text.StringBuilder();
        foreach (var part in word.Parts)
        {
            switch (part)
            {
                case WordPart.Literal lit:
                    sb.Append(lit.Value);
                    break;
                case WordPart.SingleQuoted sq:
                    sb.Append(sq.Value);
                    break;
                case WordPart.DoubleQuoted dq when dq.Parts.All(p => p is WordPart.Literal):
                    foreach (var p in dq.Parts)
                        sb.Append(((WordPart.Literal)p).Value);
                    break;
                case WordPart.EscapedLiteral el:
                    // Restore the backslash for an escaped ordinary character (a Windows
                    // separator); keep the bare char for a genuine bash escape.
                    if (el.Value.Length == 1 && IsBashEscapeChar(el.Value[0]))
                        sb.Append(el.Value);
                    else
                        sb.Append('\\').Append(el.Value);
                    break;
                default:
                    // Variable/command substitution, glob, tilde, brace expansion, etc. —
                    // not a plain path; leave it to normal word emission.
                    return false;
            }
        }
        // Only claim the word when it actually reads as a Windows path (has a separator);
        // a plain name or a genuinely escaped word (`my\ dir` -> "my dir") has no backslash
        // left and stays on the normal emission path.
        var candidate = sb.ToString();
        if (!candidate.Contains('\\'))
            return false;
        path = candidate;
        return true;
    }

    /// <summary>
    /// True for characters where a leading backslash is a meaningful bash escape rather
    /// than a Windows path separator, so <see cref="TryReconstructWindowsPath"/> must not
    /// re-insert the backslash.
    /// </summary>
    private static bool IsBashEscapeChar(char c) =>
        c is ' ' or '\t' or '\n' or '"' or '\'' or '$' or '`';

    private static string QuotePsString(string value) => PsBuild.SingleQuote(value);

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
            case "less":
                result = EmitPassthrough("Invoke-BashLess", args);
                return true;
            case "more":
                result = EmitPassthrough("Invoke-BashMore", args);
                return true;
            case "echo":
                // `-e` (enable escapes) and `-E` (disable) both prefix-collide with
                // -ErrorAction/-ErrorVariable and are case-indistinguishable to the
                // binder; force-quote them so they reach the cmdlet's Arguments with
                // case intact (the cmdlet parses -e/-E case-sensitively). `-n` is safe.
                result = EmitPassthrough("Invoke-BashEcho", args, EchoForceQuoteFlags);
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
                // find's `-o` is the OR operator (infix, position-critical), and it
                // prefix-collides with -OutVariable/-OutBuffer. Quote it so it reaches
                // the cmdlet's Arguments in place; the find cmdlet's expression
                // evaluator needs the position a switch decoy would discard.
                result = EmitPassthrough("Invoke-BashFind", args, FindForceQuoteFlags);
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
            case "browse":
                result = EmitPassthrough("Invoke-BashBrowse", args);
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
            case "ping":
                result = EmitPassthrough("Invoke-BashPing", args);
                return true;
            case "tracert":
            case "traceroute":
                result = EmitPassthrough("Invoke-BashTraceroute", args);
                return true;
            case "uname":
                result = EmitPassthrough("Invoke-BashUname", args);
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
            case "wait":
                result = EmitPassthrough("Invoke-BashWait", args);
                return true;
            case "jobs":
                result = EmitPassthrough("Invoke-BashJobs", args);
                return true;
            case "fg":
                result = EmitPassthrough("Invoke-BashFg", args);
                return true;
            case "bg":
                result = EmitPassthrough("Invoke-BashBg", args);
                return true;
            case "mapfile":
            case "readarray":
                result = EmitPassthrough("Invoke-BashMapfile", args);
                return true;
            case "eval":
                result = EmitEval(args);
                return true;
            case "shift":
                result = EmitPassthrough("Invoke-BashShift", args);
                return true;
            case "realpath":
                result = EmitPassthrough("Invoke-BashRealpath", args);
                return true;
            case "command":
                result = EmitPassthrough("Invoke-BashCommand", args);
                return true;
            case "unset":
                result = EmitPassthrough("Invoke-BashUnset", args);
                return true;
            case "pushd":
                result = EmitPassthrough("Invoke-BashPushd", args);
                return true;
            case "popd":
                result = EmitPassthrough("Invoke-BashPopd", args);
                return true;
            case "dirs":
                result = EmitPassthrough("Invoke-BashDirs", args);
                return true;
            case "yes":
                result = EmitPassthrough("Invoke-BashYes", args);
                return true;
            case "tput":
                result = EmitPassthrough("Invoke-BashTput", args);
                return true;
            case "shopt":
                result = EmitPassthrough("Invoke-BashShopt", args);
                return true;
            case "kill":
                result = EmitPassthrough("Invoke-BashKill", args);
                return true;
            case "test":
                result = EmitPassthrough("Invoke-BashTest", args);
                return true;
            case "let":
                result = EmitPassthrough("Invoke-BashLet", args);
                return true;
            case "id":
                result = EmitPassthrough("Invoke-BashId", args);
                return true;
            case "shuf":
                result = EmitPassthrough("Invoke-BashShuf", args);
                return true;
            case "install":
                result = EmitPassthrough("Invoke-BashInstall", args);
                return true;
            default:
                return false;
        }
    }

    public static string? GetLiteralValue(CompoundWord word)
    {
        if (word.Parts.Length == 1 && word.Parts[0] is WordPart.Literal lit)
            return lit.Value;
        return null;
    }

    /// <summary>
    /// Extracts the raw command text from a <see cref="CompoundWord"/> when the word is a
    /// plain single-quoted string, a plain double-quoted string containing only literals,
    /// or a bare literal — i.e., when no variable expansion or command substitution is present.
    /// Returns <c>null</c> for complex words (variable refs, command subs, etc.).
    /// </summary>
    private static string? ExtractSingleQuotedOrLiteralValue(CompoundWord word)
    {
        if (word.Parts.Length == 1)
        {
            return word.Parts[0] switch
            {
                WordPart.Literal lit => lit.Value,
                WordPart.SingleQuoted sq => sq.Value,
                WordPart.DoubleQuoted dq when dq.Parts.All(p => p is WordPart.Literal) =>
                    string.Concat(dq.Parts.Cast<WordPart.Literal>().Select(p => p.Value)),
                _ => null,
            };
        }
        // Multi-part plain literals (e.g. adjacent literal tokens): collapse to single string.
        if (word.Parts.All(p => p is WordPart.Literal))
            return string.Concat(word.Parts.Cast<WordPart.Literal>().Select(p => p.Value));
        return null;
    }

    /// <summary>
    /// Collapses a word to its literal string VALUE when every part is static (bare
    /// literals, escaped literals, single-quoted, or double-quoted runs of literals) —
    /// i.e. no variable/command/arith substitution, no glob. Unlike
    /// <see cref="ExtractSingleQuotedOrLiteralValue"/> this handles MIXED words such as
    /// <c>-F","</c> (a bare <c>-F</c> literal adjacent to a double-quoted <c>,</c>).
    /// Returns <c>null</c> for any word carrying dynamic parts. Used by passthrough
    /// force-quoting so a flag needing protection becomes ONE single-quoted PS string
    /// instead of re-wrapping already-quoted emitted text.
    /// </summary>
    private static string? TryGetStaticArgValue(CompoundWord word)
    {
        var sb = new StringBuilder();
        foreach (var part in word.Parts)
        {
            switch (part)
            {
                case WordPart.Literal lit:
                    sb.Append(lit.Value);
                    break;
                case WordPart.EscapedLiteral esc:
                    sb.Append(esc.Value);
                    break;
                case WordPart.SingleQuoted sq:
                    sb.Append(sq.Value);
                    break;
                case WordPart.DoubleQuoted dq when dq.Parts.All(p => p is WordPart.Literal or WordPart.EscapedLiteral):
                    foreach (var inner in dq.Parts)
                        sb.Append(inner is WordPart.Literal il ? il.Value : ((WordPart.EscapedLiteral)inner).Value);
                    break;
                default:
                    return null;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Returns the first 8 hex characters of the MD5 hash of <paramref name="text"/>.
    /// Used to produce stable short names for hook registrations derived from command strings.
    /// </summary>
    public static string ShortHash(string text)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }

    /// <summary>
    /// Emits a <c>Register-BashChpwdHook</c> call for a <c>trap 'CMD' DEBUG</c> statement.
    /// Includes a first-use warning emitted once per process via <c>$global:__BashHookDebugTrapWarned</c>.
    /// </summary>
    private static string EmitTrapDebugRegister(string rawCmd, string transpiledCmd)
    {
        var hookName = ShortHash(rawCmd);
        // Single-quote the transpiled command inside the scriptblock using backtick escaping.
        // Use double-quoted string for the scriptblock body so we can interpolate safely.
        var warnLine =
            "if (-not $global:__BashHookDebugTrapWarned) { " +
            "$global:__BashHookDebugTrapWarned = $true; " +
            "Write-Warning 'ps-bash: trap DEBUG mapped to Register-BashChpwdHook (fires on directory change, not every command)' }";
        var scriptBody = transpiledCmd.Replace("'", "''");
        return
            $"{warnLine}; " +
            $"Register-BashChpwdHook -Name '{hookName}' -ScriptBlock {{ Invoke-Expression '{scriptBody}' }}";
    }

    /// <summary>
    /// Emits an <c>Unregister-BashChpwdHook</c> call for a <c>trap - DEBUG</c> statement.
    /// </summary>
    private static string EmitTrapDebugUnregister(string rawCmd)
    {
        var hookName = ShortHash(rawCmd);
        return $"Unregister-BashChpwdHook -Name '{hookName}'";
    }

    public static bool IsKnownCommand(string name)
    {
        if (s_specialBuiltins.Contains(name))
            return true;

        // Check the mapped command switch — try to match without emitting
        var tempCmd = new Command.Simple(
            [new CompoundWord([new WordPart.Literal(name)])],
            [], []);
        return TryEmitMappedCommand(tempCmd, out _);
    }

    private static readonly HashSet<string> s_specialBuiltins =
    [
        "cd", "export", "local", "return", "true", "false",
        "read", "eval", "readonly", "set", "source", ".",
        "declare", "typeset", "alias", "unalias",
        "if", "then", "else", "elif", "fi", "for", "while", "until",
        "do", "done", "case", "esac", "function", "in", "select",
    ];

    private static bool IsQuotedCommandWord(CompoundWord word)
    {
        if (word.Parts.Length != 1)
            return false;
        return word.Parts[0] is WordPart.SingleQuoted or WordPart.DoubleQuoted;
    }

    /// <summary>
    /// True when the command word is a bare variable expansion (<c>$CMD</c> / <c>${CMD}</c>).
    /// Such a word emits to <c>$env:CMD</c>, which PowerShell reads as a value, not a command —
    /// running it as a command requires the <c>&amp;</c> call operator (<c>&amp; $env:CMD args</c>).
    /// Without this, <c>CMD=echo; $CMD hi</c> transpiled to the unparseable <c>$env:CMD hi</c>.
    /// </summary>
    private static bool IsVariableCommandWord(CompoundWord word)
    {
        if (word.Parts.Length != 1)
            return false;
        return word.Parts[0] is WordPart.SimpleVarSub or WordPart.BracedVarSub;
    }

    /// <summary>
    /// True when the command word is a <em>path-like</em> reference to a shell script — e.g.
    /// <c>./build.sh</c>, <c>../t.sh</c>, <c>/abs/x.sh</c>, <c>dir/x.sh</c>, <c>~/x.sh</c> (and the
    /// <c>.bash</c> variant). Such a command must be run through <c>bash</c>, not invoked bare:
    /// PowerShell treats a <c>.sh</c> file as a "document" — it ShellExecutes one standalone (which
    /// does not actually run the script's contents) but <em>refuses it inside a pipeline</em>
    /// ("Cannot run a document in the middle of a pipeline"). Routing through bash runs the script
    /// correctly and composes in a pipeline, matching bash (which execs the file via its shebang).
    /// Only path-like references are matched: a bare <c>x.sh</c> stays a PATH/command lookup, as in
    /// bash (where the current dir is not on PATH). Words with variable/command expansion return
    /// false (handled conservatively — only plain literal / quoted-literal paths are rewritten).
    /// </summary>
    private static bool IsLocalShellScriptCommand(CompoundWord word)
    {
        var value = ExtractSingleQuotedOrLiteralValue(word);
        if (string.IsNullOrEmpty(value))
            return false;
        bool isPathLike = value.Contains('/') || value.Contains('\\')
            || value.StartsWith("./", StringComparison.Ordinal)
            || value.StartsWith("../", StringComparison.Ordinal)
            || value.StartsWith("~", StringComparison.Ordinal);
        if (!isPathLike)
            return false;
        return value.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".bash", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Emit PowerShell for <c>eval ARG...</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Static eval bodies are reconstructed, joined, and transpiled at parse
    /// time. Dynamic bodies are emitted as an inline runtime block that
    /// evaluates argument expressions, calls
    /// <c>BashTranspiler.Transpile(..., TranspileContext.Eval)</c>, and runs
    /// the result with <c>Invoke-Expression</c>.
    /// </para>
    /// <para>
    /// The runtime block intentionally avoids <c>Invoke-BashEval</c>; invoking
    /// a script block from inside that cmdlet wedges the SDK host pipeline on
    /// inputs like <c>eval "$(printf 'x=5')"</c>.
    /// </para>
    /// <para>
    /// Empty <c>eval</c> with no args is a no-op (bash exits 0).
    /// </para>
    /// </remarks>
    private static string EmitEval(ImmutableArray<CompoundWord> args)
    {
        if (args.IsEmpty)
            return "$global:LASTEXITCODE = 0";

        // Inline-transpile static eval bodies at parse time. This avoids
        // runtime eval entirely for the common case where the eval
        // body is a literal string or a sequence of literal/quoted args (e.g.
        // `eval "export A=1"`, or `ps-bash -c "eval $(fnm env --shell bash)"`
        // where PowerShell already substituted the cmdsub before ps-bash saw
        // it).
        //
        // Reconstruction restores enough of the original bash quoting that the
        // joined string re-parses to the same effective tokens. If any arg
        // contains a runtime expansion (variable, command sub, arithmetic,
        // process sub, glob, brace expansion) we cannot resolve it at parse
        // time and emit the inline runtime block below.
        var staticParts = new List<string>(args.Length);
        bool allStatic = true;
        foreach (var arg in args)
        {
            var s = TryReconstructBashSource(arg);
            if (s is null) { allStatic = false; break; }
            staticParts.Add(s);
        }

        if (allStatic)
        {
            var joined = string.Join(' ', staticParts);
            try
            {
                var inner = Transpile(joined, TranspileContext.Eval);
                if (inner is not null)
                    return inner;
            }
            catch (ParseException)
            {
                // Eval body was parseable as bash tokens at the outer level
                // but malformed when treated as a complete bash script — fall
                // through to the runtime block, which produces a runtime error
                // mirroring bash's own behavior.
            }
        }

        var emittedArgs = string.Join(", ", args.Select(EmitWord));
        return
            // Use Get-Variable -ErrorAction SilentlyContinue so the depth probe is
            // safe under Set-StrictMode -Version Latest (set -u). Plain $global:X
            // throws on first read when the variable has never been initialized.
            "$__psbash_eval_src = @(" + emittedArgs + ") -join ' '; " +
            "if (-not [string]::IsNullOrWhiteSpace($__psbash_eval_src)) { " +
            "$__psbash_eval_depth = [int](Get-Variable -Name '__BashEvalDepth' -Scope Global -ValueOnly -ErrorAction SilentlyContinue); " +
            "if ($__psbash_eval_depth -ge 5) { throw 'eval: nesting depth limit (5) exceeded; possible infinite recursion' }; " +
            "$global:__BashEvalDepth = $__psbash_eval_depth + 1; " +
            "try { " +
            // Assembly-qualified type names: BashTranspiler/TranspileContext live
            // in PsBash.Transpiler.dll (assembly name differs from the namespace
            // since REFACTOR-5a's project split, commit 1f23039). This runtime
            // string is evaluated via Invoke-Expression inside the ps-bash-host
            // SDK runspace. PowerShell's [Type] literal resolver only finds types
            // in already-loaded runspace assemblies UNLESS the name is
            // assembly-qualified — given the assembly name it loads the assembly
            // on demand. A bare [PsBash.Core.Transpiler.BashTranspiler] silently
            // failed to resolve there, so eval emitted empty output.
            "$__psbash_eval_pwsh = [PsBash.Core.Transpiler.BashTranspiler, PsBash.Transpiler]::Transpile($__psbash_eval_src, [PsBash.Core.Transpiler.TranspileContext, PsBash.Transpiler]::Eval); " +
            "Invoke-Expression $__psbash_eval_pwsh " +
            "} finally { $global:__BashEvalDepth = $__psbash_eval_depth } " +
            "}";
    }

    /// <summary>
    /// Recover the post-quote-removal string value of a <see cref="CompoundWord"/>
    /// composed only of literal and quoted parts. Returns <c>null</c> when any
    /// part requires runtime expansion (variable, command sub, arithmetic,
    /// process sub, glob, brace expansion).
    /// </summary>
    /// <remarks>
    /// Mirrors bash's argument processing: by the time <c>eval</c> sees its
    /// args, quotes have already been stripped, so <c>eval "export A=1"</c>
    /// and <c>eval 'export A=1'</c> both produce the eval body
    /// <c>export A=1</c> (no quotes). The recovered string is then re-parsed
    /// as a bash source line.
    /// </remarks>
    private static string? TryReconstructBashSource(CompoundWord word)
    {
        var sb = new StringBuilder();
        foreach (var part in word.Parts)
        {
            switch (part)
            {
                case WordPart.Literal lit:
                    sb.Append(lit.Value);
                    break;
                case WordPart.EscapedLiteral esc:
                    sb.Append(esc.Value);
                    break;
                case WordPart.SingleQuoted sq:
                    sb.Append(sq.Value);
                    break;
                case WordPart.DoubleQuoted dq:
                    foreach (var inner in dq.Parts)
                    {
                        switch (inner)
                        {
                            case WordPart.Literal il:
                                sb.Append(il.Value);
                                break;
                            case WordPart.EscapedLiteral ie:
                                sb.Append(ie.Value);
                                break;
                            default:
                                // Variable/command/arithmetic substitution
                                // inside double quotes — defer to runtime.
                                return null;
                        }
                    }
                    break;
                default:
                    // Variable/command/arithmetic/process subs, globs, and
                    // brace expansions — defer to runtime.
                    return null;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Bare short flags that must be quoted when emitted for <c>find</c> because
    /// the PowerShell binder would otherwise consume them — destroying the
    /// operator's position in find's expression grammar. <c>-o</c> (OR)
    /// prefix-collides with the <c>-OutVariable</c>/<c>-OutBuffer</c> common
    /// parameters; <c>-a</c> (AND) prefix-collides with the find cmdlet's own
    /// <c>-Arguments</c> (<c>ValueFromRemainingArguments</c>) parameter, which
    /// would bind <c>-a</c> as named and swallow the following token as its
    /// value. <c>-and</c>/<c>-or</c>/<c>-not</c> and <c>!</c>/<c>(</c>/<c>)</c>
    /// share no parameter prefix and reach Arguments unaided.
    /// </summary>
    private static readonly IReadOnlySet<string> FindForceQuoteFlags =
        new HashSet<string>(StringComparer.Ordinal) { "-o", "-a" };

    /// <summary>
    /// echo's <c>-e</c> / <c>-E</c> collide with the <c>-Error*</c> common
    /// parameters and are case-indistinguishable to the binder; quoting routes
    /// them to <c>Invoke-BashEcho</c>'s Arguments with case intact.
    /// </summary>
    private static readonly IReadOnlySet<string> EchoForceQuoteFlags =
        new HashSet<string>(StringComparer.Ordinal) { "-e", "-E", "--" };

    private static string EmitPassthrough(
        string cmdlet,
        ImmutableArray<CompoundWord> args,
        IReadOnlySet<string>? forceQuoteFlags = null)
    {
        if (args.IsEmpty)
            return cmdlet;

        var pipelineProcessSubIndex = GetPipelineProcessSubArgIndex(cmdlet, args);

        // Renders operand i as a plain (non-splat) argument: the pipeline
        // process-sub operand goes through EmitProcessSubPipeline, everything
        // else through EmitWord with passthrough quoting applied.
        string EmitPlainArg(int i)
        {
            var emitted = i == pipelineProcessSubIndex
                ? EmitProcessSubPipeline((WordPart.ProcessSub)args[i].Parts[0])
                : EmitWord(args[i]);
            // forceQuoteFlags carries bare short flags that prefix-collide with a
            // PowerShell common parameter (e.g. find's infix `-o`, ambiguous with
            // -OutVariable/-OutBuffer). Quoting routes the literal token to the
            // cmdlet's ValueFromRemainingArguments in its original position — a
            // switch decoy on the cmdlet would resolve the crash but lose the
            // operator's position. See docs/solutions on common-param collisions.
            bool force = forceQuoteFlags is not null && forceQuoteFlags.Contains(emitted);
            if (NeedsPassthroughQuoting(emitted) || force)
            {
                // If the emitted text ALREADY contains a quote char, wrapping it in
                // another pair of double quotes corrupts it: `-F","` (EmitWord text
                // `-F","`) becomes `"-F","`, which PowerShell parses as the two-element
                // array @('-F',''), not the string `-F,`. Emit the literal value as ONE
                // single-quoted string instead. When there are no embedded quotes the
                // historical "..." wrap is unambiguous, so keep it (no needless churn).
                if ((emitted.IndexOf('"') >= 0 || emitted.IndexOf('\'') >= 0)
                    && TryGetStaticArgValue(args[i]) is { } staticValue)
                {
                    return PsBuild.SingleQuote(staticValue);
                }
                return $"\"{emitted}\"";
            }
            return emitted;
        }

        // RC-7: when any operand is a pure unquoted ordinary variable, hoist it
        // to a temp variable and PowerShell-splat it so word-splitting and
        // empty-word elision match bash. The pipeline process-sub operand is
        // never a pure variable word, so it is unaffected.
        if (HasUnquotedVarSplatArg(args, pipelineProcessSubIndex))
            return EmitCommandWithSplatArgs(cmdlet, args, EmitPlainArg);

        var sb = new StringBuilder(cmdlet);
        for (int i = 0; i < args.Length; i++)
        {
            sb.Append(' ');
            sb.Append(EmitPlainArg(i));
        }
        return sb.ToString();
    }

    private static int GetPipelineProcessSubArgIndex(string cmdlet, ImmutableArray<CompoundWord> args)
    {
        if (!IsStdinSubstitutableProcessSubConsumer(cmdlet))
            return -1;

        int processSubIndex = -1;
        for (int i = 0; i < args.Length; i++)
        {
            if (IsPureProcessSubWord(args[i]))
            {
                if (processSubIndex >= 0)
                    return -1;
                processSubIndex = i;
            }
        }

        return processSubIndex;
    }

    private static bool IsPureProcessSubWord(CompoundWord word) =>
        word.Parts.Length == 1 && word.Parts[0] is WordPart.ProcessSub;

    private static bool IsStdinSubstitutableProcessSubConsumer(string cmdlet) => cmdlet is
        "Invoke-BashHead" or
        "Invoke-BashTail" or
        "Invoke-BashSort" or
        "Invoke-BashUniq";
        // RC-8b: Invoke-BashWc intentionally NOT on this allowlist. wc's
        // output format depends on whether input came from a named file
        // (echoes the filename: "100 file.txt") versus stdin (count only:
        // "100"). The "stdin-substitutable" assumption that justifies the
        // Tier-2 pipeline-object route does not hold for wc — routing it
        // through the pipeline would silently change its output shape
        // relative to bash's "<count> /dev/fd/N". Keep wc on the Tier-1
        // temp-file route so a filename is present (even if path differs).

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

    // Double ' for safe embedding inside a PowerShell single-quoted string literal.
    private static string SqEsc(string s) => s.Replace("'", "''");

    /// <summary>
    /// Translates a bash glob pattern to a .NET regex pattern suitable for use in
    /// PowerShell's <c>-replace</c> operator.
    /// <para>
    /// Bash glob special characters:
    /// <list type="bullet">
    ///   <item><c>*</c> → <c>.*</c> (greedy) or <c>.*?</c> (lazy)</item>
    ///   <item><c>?</c> → <c>.</c></item>
    ///   <item><c>[...]</c> → passed through unchanged (already valid regex)</item>
    ///   <item>All other regex metacharacters are escaped.</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="glob">Bash glob pattern (e.g. <c>.*</c>, <c>*.c</c>, <c>?x</c>).</param>
    /// <param name="lazy">
    /// When <c>true</c>, <c>*</c> translates to <c>.*?</c> (lazy/shortest match).
    /// When <c>false</c>, <c>*</c> translates to <c>.*</c> (greedy/longest match).
    /// </param>
    private static string GlobToRegex(string glob, bool lazy)
    {
        var sb = new StringBuilder(glob.Length * 2);
        int i = 0;
        while (i < glob.Length)
        {
            char c = glob[i];
            if (c == '*')
            {
                sb.Append(lazy ? ".*?" : ".*");
                i++;
            }
            else if (c == '?')
            {
                sb.Append('.');
                i++;
            }
            else if (c == '[')
            {
                // Pass character class through unchanged — already valid regex syntax.
                int close = glob.IndexOf(']', i + 1);
                if (close >= 0)
                {
                    sb.Append(glob, i, close - i + 1);
                    i = close + 1;
                }
                else
                {
                    // Unclosed bracket — escape the [ and continue.
                    sb.Append("\\[");
                    i++;
                }
            }
            else if ("\\^$.|+(){}".IndexOf(c) >= 0)
            {
                // Regex metacharacter — escape it.
                sb.Append('\\');
                sb.Append(c);
                i++;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return sb.ToString();
    }
}
