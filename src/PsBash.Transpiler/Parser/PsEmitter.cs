using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using PsBash.Core.Parser.Ast;
using PsBash.Core.Transpiler;

namespace PsBash.Core.Parser;

/// <summary>
/// Walks parsed AST nodes and emits equivalent PowerShell strings.
/// </summary>
public static partial class PsEmitter
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

    // Names of a `VAR=val cmd` prefix being emitted: the command's own words expand BEFORE the prefix
    // takes effect (`x=old; x=new echo "$x"` prints old), so a reference to such a name reads the
    // pre-assignment copy `$env:__psbash_ea_NAME`, which WrapEnvPairs fills.
    [ThreadStatic]
    private static HashSet<string>? _envPrefixShadow;

    // Scalar `local` names of the function being emitted (see EmitFunction); _collectScalarLocals is the
    // sink CollectLocalVars fills while scanning the body.
    [ThreadStatic]
    private static HashSet<string>? _dynLocals;
    // Names the enclosing function declared `local`; every other array it writes is the shell's (a global), not a copy.
    [ThreadStatic]
    private static HashSet<string>? _functionLocalNames;

    // The script being emitted mentions `pipefail`: every multi-stage pipeline records per-stage status.
    [ThreadStatic]
    private static bool _pipefailMode;
    /// <summary>The script reads PIPESTATUS, so a plain command records its own status there.</summary>
    [ThreadStatic]
    private static bool _pipeStatusMode;
    /// <summary>The script can turn xtrace on: a statement-level command is preceded by a runtime-checked trace line.</summary>
    [ThreadStatic]
    private static bool _xtraceMode;
    /// <summary>The script mentions <c>trap</c>: a subshell or <c>$( )</c> may run a function that sets an EXIT trap.</summary>
    [ThreadStatic]
    private static bool _trapMode;
    // A multi-statement script that sets a DEBUG trap: every top-level simple command first runs it (bash semantics).
    // A lone `trap 'cmd' DEBUG` line (an interactive prompt hook) keeps its directory-change mapping.
    [ThreadStatic]
    private static bool _debugTrapMode;
    /// <summary>The script prints variables with <c>declare -p</c>: <c>export</c> and <c>declare -i</c> record their attribute for it.</summary>
    [ThreadStatic]
    private static bool _declarePrintMode;
    // The script can turn nounset on: simple `$x` / `${x}` reads then check the flag at runtime.
    [ThreadStatic]
    private static bool _nounsetMode;

    /// <summary>
    /// Set by <c>source</c> / <c>eval</c> from the caller's LIVE option state: the sourced text may never mention
    /// <c>pipefail</c> or <c>set -u</c> itself, yet bash applies the options it inherits.
    /// </summary>
    [ThreadStatic]
    public static bool InheritedPipefail;
    [ThreadStatic]
    public static bool InheritedNounset;
    [ThreadStatic]
    private static HashSet<string>? _collectScalarLocals;

    private static bool IsDynamicLocalName(string name)
        => name != "IFS" && !_psBuiltins.Contains(name) && PsBuild.TryMapSpecialVar(name, braced: false, inDoubleQuote: false) is null
           && !(_loopVars?.Contains(name) ?? false);
    private const string EnvShadowPrefix = "__psbash_ea_";

    private static string? EnvShadowName(string name)
        => _envPrefixShadow is not null && _envPrefixShadow.Contains(name) ? EnvShadowPrefix + name : null;

    private static T WithEnvShadow<T>(ImmutableArray<EnvPair> pairs, Func<T> emit)
    {
        var prior = _envPrefixShadow;
        var set = prior is null ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(prior, StringComparer.Ordinal);
        foreach (var p in pairs) set.Add(p.Name);
        _envPrefixShadow = set;
        try { return emit(); }
        finally { _envPrefixShadow = prior; }
    }

    /// <summary>
    /// Tracks nesting depth for generating unique loop iteration counter variable names.
    /// </summary>
    [ThreadStatic]
    private static int _loopDepth;

    /// <summary>
    /// True while emitting inside a COMPOUND command's stdin scope — a compound used as a pipe stage
    /// or given <c>&lt; file</c> / <c>&lt;&lt;&lt;</c>, whose incoming data sits in
    /// <c>$global:__BashStdIn</c> (<see cref="PsBuild.StdinScope"/>). A stdin-reading simple command
    /// emitted here is fed from that queue (<see cref="EmitSimpleFed"/>). Pipe-target stages (i &gt; 0)
    /// and the body of a <c>while read</c> loop that streams <c>$input</c> reset it: their stdin is the
    /// pipe, not the shared queue. See emitter-strategy.md "Compound-command stdin".
    /// </summary>
    [ThreadStatic]
    private static bool _inStdinScope;

    /// <summary>Names declared <c>declare -i</c> in the script being emitted: later plain assignments evaluate arithmetically.</summary>
    [ThreadStatic]
    private static HashSet<string>? _intVars;

    /// <summary>Names declared <c>declare -A</c> in the script being emitted: a later <c>a=(k v ...)</c> is key/value pairs (bash 5.1+).</summary>
    [ThreadStatic]
    private static HashSet<string>? _assocVars;
    // Names assigned an array/assoc literal earlier in this unit: a bare `$a` / `${a}` then reads element 0, as in bash.
    private static HashSet<string>? _arrayVars;

    /// <summary>Names declared <c>declare -l</c> (false) / <c>declare -u</c> (true): later assignments are case-converted.</summary>
    [ThreadStatic]
    private static Dictionary<string, bool>? _caseVars;

    /// <summary>A <c>declare -l/-u</c> variable converts the value it is assigned.</summary>
    private static string CaseAttr(string name, string valueExpr) =>
        _caseVars is not null && _caseVars.TryGetValue(name, out bool upper)
            ? "([string](" + valueExpr + "))." + (upper ? "ToUpperInvariant()" : "ToLowerInvariant()")
            : valueExpr;
    // Names a literal `readonly NAME` made read-only earlier in this script: a later plain assignment is refused.
    [ThreadStatic]
    private static HashSet<string>? _readonlyVars;
    /// <summary>True when the script text reads `$?` somewhere: only then does a bare assignment need to reset the status to 0.</summary>
    [ThreadStatic] private static bool _scriptReadsStatus;
    [ThreadStatic] private static bool _scriptReadsLastArg;
    /// <summary>Set when an assignment to a readonly variable was emitted: bash drops the rest of that input line.</summary>
    [ThreadStatic] private static bool _readonlyViolation;
    [ThreadStatic] private static bool _scriptPosix;
    [ThreadStatic] private static bool _scriptLastpipe;

    /// <summary><c>declare -n ref=target</c> in the script being emitted: <c>ref</c> reads and writes <c>target</c>.
    /// Static only (a literal target, resolved at emit time); a nameref set at run time is not modelled.</summary>
    [ThreadStatic]
    private static Dictionary<string, string>? _namerefs;

    /// <summary>Names assigned a literal string so far (`ref=x`): `typeset -n ref` later turns that string into the target.</summary>
    [ThreadStatic]
    private static Dictionary<string, string>? _litAssigned;

    /// <summary>Namerefs declared without a target (`typeset -n ref`): the next literal assignment names it.</summary>
    [ThreadStatic]
    private static HashSet<string>? _pendingNamerefs;

    /// <summary>`a[2]` / `a[i+1]`: a nameref target that names one array element.</summary>
    private static bool IsNamerefElementTarget(string target) =>
        target.Length > 3 && target[^1] == ']' && target.IndexOf('[') is var lb and > 0 && IsValidVarName(target[..lb]);

    private static string ResolveNameref(string name)
    {
        for (int hops = 0; hops < 8 && _namerefs is not null && _namerefs.TryGetValue(name, out var target); hops++)
            name = target;
        return name;
    }

    /// <summary>Lexical nesting of <c>&lt; /dev/null</c> scopes (names <see cref="PsBuild.EmptyStdinScope"/>'s save variable).</summary>
    [ThreadStatic]
    private static int _emptyStdinDepth;

    /// <summary>Run <paramref name="emit"/> with <see cref="_inStdinScope"/> set to <paramref name="value"/>.</summary>
    private static string WithStdinScope(bool value, Func<string> emit)
    {
        var prior = _inStdinScope;
        _inStdinScope = value;
        try { return emit(); }
        finally { _inStdinScope = prior; }
    }

    private static string WithPipeStageFeed(bool value, Func<string> emit)
    {
        var prior = _pipeStageFeedsInput;
        _pipeStageFeedsInput = value;
        try { return emit(); }
        finally { _pipeStageFeedsInput = prior; }
    }

    private static bool HasInputRedirect(ImmutableArray<Redirect> redirects) =>
        !redirects.IsDefaultOrEmpty && redirects.Any(r => r.Fd == 0 && r.Op is "<" or "<<" or "<<<");

    /// <summary>
    /// A compound command that is given its own stdin (<c>&lt; file</c>, <c>&lt;&lt;&lt;</c>) emits its
    /// body INSIDE a stdin scope; one without redirects inherits the surrounding scope unchanged.
    /// </summary>
    private static string EmitCompoundBody(ImmutableArray<Redirect> redirects, Func<string> emitBody) =>
        HasInputRedirect(redirects) ? WithStdinScope(true, emitBody) : emitBody();

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
    // Detection logic lives in FusedLane.cs; what stays here is the emitter's own
    // thread-affine state the lane's decision reads (the capture depth below, and
    // the FusionEnabledOverride test seam), passed in explicitly at the call site.

    /// <summary>
    /// Depth of the current command / process substitution nesting. Fusion only
    /// applies to a terminal-bound pipeline (<c>_captureDepth == 0</c>): inside
    /// <c>$(…)</c> / <c>&lt;(…)</c> the output never crosses the IPC return path,
    /// so batching buys nothing and would change the captured object shape.
    /// </summary>
    [ThreadStatic]
    private static int _captureDepth;

    /// <summary>
    /// How many enclosing SUBSHELLS the current text is being emitted inside. In
    /// bash, <c>exit</c> inside <c>( … )</c> terminates only the subshell and sets
    /// <c>$?</c> in the parent, so at depth &gt; 0 <c>exit</c> must emit a SCOPED
    /// <c>$LASTEXITCODE = N; return</c> instead of a real process exit.
    /// </summary>
    [ThreadStatic]
    private static int _subshellDepth;

    /// <summary>
    /// Non-zero while emitting an operand of an <c>&amp;&amp;</c>/<c>||</c> list.
    /// bash exempts every command in such a list from errexit (except the last),
    /// so a standalone <c>false</c> aborts under <c>set -e</c> but <c>false || true</c>
    /// must not — the emitter reads this to decide whether to throw the errexit
    /// terminating error.
    /// </summary>
    [ThreadStatic]
    private static int _andOrChainDepth;

    /// <summary>
    /// True while transpiling a script that turns errexit on (<c>set -e</c> / <c>set -o errexit</c>,
    /// <see cref="EnablesErrexit"/>). Only then does <see cref="EmitStatement"/> add the errexit checks and exempt
    /// contexts their runtime suppression — a script that never uses <c>set -e</c> emits exactly
    /// what it always did.
    /// </summary>
    [ThreadStatic]
    private static bool _errexitMode;

    /// <summary>
    /// Non-zero while emitting text that is errexit-EXEMPT in bash: a condition, a non-final
    /// <c>&amp;&amp;</c>/<c>||</c> operand, a negated or multi-stage pipeline, a command or process
    /// substitution, a background job. No errexit check is emitted inside one.
    /// </summary>
    [ThreadStatic]
    private static int _errexitExemptDepth;

    private static readonly System.Text.RegularExpressions.Regex SetErrexitPattern = new(
        // `set`, then any words of the SAME command (no ; & | ( ) newline), then -…e… or -o errexit.
        @"\bset[ \t]+(?:[^\s;&|()]+[ \t]+)*?(?:-[A-Za-z]*e[A-Za-z]*\b|-o[ \t]+errexit\b)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="bash"/> can turn errexit on (a <c>set -e</c>-style word).</summary>
    internal static bool EnablesErrexit(string bash) =>
        // This is a conservative emission prefilter, not a parser. Quote/escape
        // spelling must not hide static flags such as set '-e' or set -o 'errexit'.
        // Extra checks are inert until the parsed set command enables the runtime flag.
        (bash.Contains("set", StringComparison.Ordinal) &&
        SetErrexitPattern.IsMatch(bash.Replace("'", "").Replace("\"", "").Replace("\\", "")))
        // `trap ... ERR` needs the same statement-position checks (and exempt contexts) to know when a command failed.
        || (bash.Contains("trap", StringComparison.Ordinal) && bash.Contains("ERR", StringComparison.Ordinal));

    private static T WithErrexitExempt<T>(Func<T> emit)
    {
        _errexitExemptDepth++;
        try { return emit(); }
        finally { _errexitExemptDepth--; }
    }

    /// <summary>How to leave on an errexit failure from here: the script, or only the enclosing subshell.</summary>
    /// <summary>Under an armed set -e, what a failed compound input redirect runs to end the script (null otherwise).</summary>
    private static string? RedirectFailExit() =>
        _errexitMode && _errexitExemptDepth == 0 ? PsBuild.ErrexitCheck(ErrexitExitStatement()) : null;

    private static string ErrexitExitStatement() =>
        _subshellDepth > 0 ? "return" : "exit $global:LASTEXITCODE";

    /// <summary>Builtins whose status is not an errexit failure (bash ignores <c>export x=$(false)</c>) or that leave themselves.</summary>
    private static readonly HashSet<string> ErrexitNeutralBuiltins = new(StringComparer.Ordinal)
    {
        "export", "local", "declare", "typeset", "readonly", "set", "shift", "unset", "alias", "unalias",
        "shopt", "return", "exit", "break", "continue", ":", "true",
    };

    /// <summary>
    /// A body (if/loop/case arm/function/group/subshell) or the whole script: each command in
    /// it is at statement position.
    /// </summary>
    private static string EmitBody(Command body) =>
        body is Command.CommandList list ? EmitCommandList(list) : EmitStatement(body);

    /// <summary>
    /// A command at STATEMENT position — where bash's errexit applies. Under
    /// <see cref="_errexitMode"/>, a command whose failure ends a <c>set -e</c> script is followed by
    /// <see cref="PsBuild.ErrexitCheck"/>; exempt constructs run under runtime suppression so a
    /// function they call keeps going. Compound commands other than a subshell are not checked
    /// themselves (bash: their status never triggers errexit) — their bodies are.
    /// </summary>
    private static string EmitStatement(Command cmd)
    {
        string text = EmitStatementCore(cmd);
        if (_xtraceMode && TryEmitXtrace(cmd) is { } trace)
            text = trace + "; " + text;
        // A plain command is a one-stage pipeline: `false; echo ${PIPESTATUS[*]}` is `1`, not the last real pipeline's.
        return _pipeStatusMode && cmd is Command.Simple { Words.Length: > 0 }
            ? text + "; $global:PIPESTATUS = @([int]$global:LASTEXITCODE)"
            : text;
    }

    /// <summary>
    /// The <c>+ cmd args</c> line <c>set -x</c> prints before a command, as a call that checks the flag at run time.
    /// Only words whose value can be rendered without running anything (literals, quoted text, variables) are traced:
    /// a command substitution or glob would run twice, so such a command is left untraced.
    /// </summary>
    private static string? TryEmitXtrace(Command cmd)
    {
        var parts = new List<string>();
        switch (cmd)
        {
            case Command.Simple { Words.Length: > 0, EnvPairs.IsEmpty: true } simple:
                foreach (var w in simple.Words)
                    if (XtraceWord(w) is { } e) parts.Add(e); else return null;
                break;
            case Command.ShAssignment { IsLocal: false, IsExport: false } assign:
                foreach (var pair in assign.Pairs)
                {
                    if (pair.Op != AssignOp.Equal || pair.ArrayValue is not null) return null;
                    if (pair.Value is null) { parts.Add(PsBuild.SingleQuote(pair.Name + "=")); continue; }
                    if (XtraceWord(pair.Value) is not { } v) return null;
                    parts.Add("(\"" + pair.Name + "=\" + " + v + ")");
                }
                break;
            default:
                return null;
        }
        return "Write-BashXtrace " + string.Join(" ", parts);
    }

    private static string? XtraceWord(CompoundWord word)
    {
        if (TryGetStaticArgValue(word) is { } literal) return PsBuild.SingleQuote(literal);
        foreach (var part in word.Parts)
        {
            var p = part is WordPart.DoubleQuoted dq ? dq.Parts : System.Collections.Immutable.ImmutableArray.Create(part);
            foreach (var q in p)
                if (q is not (WordPart.Literal or WordPart.SimpleVarSub or WordPart.SingleQuoted or WordPart.EscapedLiteral)) return null;
        }
        var emitted = EmitWord(word);
        return emitted.Length > 0 && (emitted[0] == (char)34 || emitted[0] == (char)36) ? "([string](" + emitted + "))" : null;
    }

    private static string EmitStatementCore(Command cmd)
    {
        if (cmd is Command.Simple { Line: > 0 } lined) PsBuild.TrapLine = lined.Line;
        else if (LastSimpleLine(cmd) is > 0 and var lastLine) PsBuild.TrapLine = lastLine;
        if (DebugActive)
            return cmd is Command.AndOrList andOr ? EmitDebugAndOr(andOr) : WithDebugLead(cmd, () => EmitStatementBody(cmd));
        return EmitStatementBody(cmd);
    }

    // ── trap DEBUG ───────────────────────────────────────────────────────────────────────
    // bash runs the DEBUG action before every simple command (assignments, `[[ ]]`, `(( ))` included), before each
    // `for` iteration and arithmetic-for clause, and before `case`, with $LINENO = that command's line. A function
    // (no functrace), a subshell and `$( )` do not inherit it; a pipeline runs it in the parent for each simple stage,
    // in order, and not inside a compound stage. Everything here is gated on DebugActive: other scripts emit as before.

    /// <summary>Nesting of pipeline stages being emitted while DEBUG is on: their bodies do not run the action.</summary>
    [ThreadStatic]
    private static int _debugSuppress;

    private static bool DebugActive => _debugTrapMode && _functionDepth == 0 && _subshellDepth == 0 && _debugSuppress == 0;

    /// <summary>The DEBUG run for a command on <paramref name="line"/>, or "" (no line, or DEBUG not active here).</summary>
    private static string DebugAt(int line) =>
        line > 0 && DebugActive ? PsBuild.DebugTrapRun(line, _errexitMode ? ErrexitExitStatement() : null) : "";

    /// <summary>The DEBUG runs bash makes as <paramref name="cmd"/> starts: one per simple command it runs first.</summary>
    private static string DebugLead(Command cmd) => cmd switch
    {
        Command.Simple s => DebugAt(s.Line),
        Command.ShAssignment a => DebugAt(a.Line),
        Command.BoolExpr b => DebugAt(b.Line),
        Command.ArithCommand a => DebugAt(a.Line),
        Command.Case c => DebugAt(c.Line),
        Command.Pipeline p => string.Concat(p.Commands.Select(stage =>
            stage is Command.Simple or Command.ShAssignment or Command.BoolExpr or Command.ArithCommand ? DebugLead(stage) : "")),
        _ => "",
    };

    /// <summary><paramref name="emit"/> with <paramref name="cmd"/>'s DEBUG lead in front; a pipeline's stages are emitted
    /// with the action off (each simple stage already ran it in the lead).</summary>
    private static string WithDebugLead(Command cmd, Func<string> emit)
    {
        string lead = DebugLead(cmd);
        if (cmd is not Command.Pipeline { Commands.Length: > 1 }) return lead + emit();
        _debugSuppress++;
        try { return lead + emit(); }
        finally { _debugSuppress--; }
    }

    /// <summary>
    /// <c>a &amp;&amp; b || c</c> while DEBUG is on, as statements: PowerShell's chain operands cannot hold the action's
    /// statement, so each operand runs as a statement (with its own lead) when the status so far allows it. Left to
    /// right, as bash groups it: <c>||</c> runs on a non-zero status, <c>&amp;&amp;</c> on zero; only the final operand is
    /// subject to errexit.
    /// </summary>
    private static string EmitDebugAndOr(Command.AndOrList andOr)
    {
        andOr = andOr with { Commands = ResolveLastArgRefs(andOr.Commands) };
        var sb = new StringBuilder();
        for (int i = 0; i < andOr.Commands.Length; i++)
        {
            var op = andOr.Commands[i];
            string text = i == andOr.Commands.Length - 1 ? EmitStatementCore(op) : WithErrexitExempt(() => EmitStatementCore(op));
            // A plain assignment leaves the earlier status in LASTEXITCODE; its own status is 0.
            if (op is Command.ShAssignment asg && !ContainsCommandSub(asg)) text += "; " + PsBuild.SetStatus(0);
            if (i == 0) sb.Append(text);
            else sb.Append("; if (").Append(PsBuild.LastStatusTest(negate: andOr.Ops[i - 1] == "||")).Append(") { ").Append(text).Append(" }");
        }
        return sb.ToString();
    }

    /// <summary>Line of the last simple command a list or pipeline runs (bash reports that line for <c>trap ERR</c>'s <c>$LINENO</c>); 0 when unknown.</summary>
    private static int LastSimpleLine(Command cmd) => cmd switch
    {
        Command.Simple s => s.Line,
        Command.AndOrList a when a.Commands.Length > 0 => LastSimpleLine(a.Commands[^1]),
        Command.Pipeline p when p.Commands.Length > 0 => LastSimpleLine(p.Commands[^1]),
        _ => 0,
    };

    private static bool IsTrapCommand(Command.Simple simple)
        => simple.Words.Length > 0 && TryGetStaticArgValue(simple.Words[0]) == "trap";

    private static string EmitStatementBody(Command cmd)
    {
        if (!_errexitMode || _errexitExemptDepth > 0)
            return Emit(cmd);

        switch (cmd)
        {
            case Command.AndOrList andOr:
                return EmitAndOrStatement(andOr);
            case Command.Pipeline { Negated: true }:
                return PsBuild.ErrexitSuppressed(WithErrexitExempt(() => Emit(cmd)));
            case Command.Pipeline pipeline:
            {
                // Every stage is a subshell in bash: a failure INSIDE a stage never ends the
                // script; only the last stage's status counts.
                var text = PsBuild.ErrexitSuppressed(WithErrexitExempt(() => Emit(cmd)));
                return text + "; " + PsBuild.ErrexitCheck(ErrexitExitStatement());
            }
            case Command.Background:
                return WithErrexitExempt(() => Emit(cmd));
            case Command.Simple simple when IsErrexitCheckedSimple(simple):
            // `x=$(false)` fails with the substitution; `local`/`export x=$(false)` has the builtin's status (0).
            case Command.ShAssignment assign when !assign.IsLocal && !assign.IsExport && ContainsCommandSub(assign):
            case Command.BoolExpr:
            case Command.ArithCommand:
            case Command.Subshell:
                return Emit(cmd) + "; " + PsBuild.ErrexitCheck(ErrexitExitStatement());
            default:
                return Emit(cmd);
        }
    }

    private static bool IsErrexitCheckedSimple(Command.Simple simple) =>
        !simple.Words.IsDefaultOrEmpty
        && !(GetLiteralValue(simple.Words[0]) is { } name && ErrexitNeutralBuiltins.Contains(name));

    private static bool ContainsCommandSub(Command.ShAssignment assign)
    {
        foreach (var pair in assign.Pairs)
        {
            if (pair.Value is null) continue;
            foreach (var part in pair.Value.Parts)
            {
                if (part is WordPart.CommandSub)
                    return true;
                if (part is WordPart.DoubleQuoted dq && dq.Parts.Any(q => q is WordPart.CommandSub))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// <c>a &amp;&amp; b || c</c> at statement position under errexit: the non-final operands are
    /// exempt; the script leaves only when the FINAL command ran and failed.
    /// </summary>
    private static string EmitAndOrStatement(Command.AndOrList andOr)
    {
        // `cmd || exit 1` / `cmd && break`: the condition part is exempt, the keyword leaves by itself.
        if (WithErrexitExempt(() => TryEmitAndOrEndingInStatementKeyword(andOr)) is { } keywordForm)
            return PsBuild.ErrexitSuppressed(keywordForm);

        var chain = EmitAndOrList(andOr, errexitFinalOperand: true);
        return PsBuild.ErrexitAndOrList(chain, ErrexitExitStatement());
    }

    /// <summary>
    /// True when <paramref name="command"/> runs a <c>set</c> that turns errexit on (<c>-e</c> in a flag
    /// word, or <c>-o errexit</c>) at its own level — not inside a nested subshell, which has its own scope.
    /// </summary>
    private static bool BodySetsErrexit(BashNode? command) => command switch
    {
        Command.Simple { Words.Length: > 1 } s when GetLiteralValue(s.Words[0]) == "set" =>
            s.Words.Skip(1).Select(GetLiteralValue).ToList() is var a
            && (a.Any(w => w is { Length: > 1 } && w[0] == '-' && w[1] != '-' && w.Contains('e'))
                || a.SkipWhile(w => w != "-o").Skip(1).FirstOrDefault() == "errexit"),
        Command.Pipeline p => p.Commands.Any(BodySetsErrexit),
        Command.AndOrList a => a.Commands.Any(BodySetsErrexit),
        Command.CommandList l => l.Commands.Any(BodySetsErrexit),
        Command.If i => i.Arms.Any(arm => BodySetsErrexit(arm.Cond) || BodySetsErrexit(arm.Body))
                        || BodySetsErrexit(i.ElseBody),
        Command.ForIn f => BodySetsErrexit(f.Body),
        Command.ForArith f => BodySetsErrexit(f.Body),
        Command.While w => BodySetsErrexit(w.Cond) || BodySetsErrexit(w.Body),
        Command.Case c => c.Arms.Any(arm => BodySetsErrexit(arm.Body)),
        Command.BraceGroup b => BodySetsErrexit(b.Body),
        _ => false,
    };

    /// <summary>
    /// How many enclosing PowerShell DOUBLE-QUOTED strings the current text is being
    /// emitted inside — incremented for the body of a command substitution that lands
    /// in a <c>"…"</c> (see <see cref="EmitCommandSubString"/>).
    /// <para>
    /// PowerShell resolves backtick escapes at EVERY enclosing string level, so an
    /// escape must be doubled once per level: verified directly against the parser,
    /// <c>"$(cmd "a`"b")"</c> is an error ("The string is missing the terminator")
    /// while <c>"$(cmd "a``"b")"</c> parses. Literal text emitted into a double-quoted
    /// string therefore goes through <see cref="EscapeForDoubleQuoteNested"/>, not
    /// <c>PsBuild.EscapeForDoubleQuote</c> directly.
    /// </para>
    /// </summary>
    [ThreadStatic]
    private static int _dqNestDepth;

    /// <summary>
    /// Escape literal text for a PowerShell double-quoted string, doubling the escape
    /// character once per enclosing string level (see <see cref="_dqNestDepth"/>).
    /// </summary>
    private static string EscapeForDoubleQuoteNested(string value)
    {
        var escaped = PsBuild.EscapeForDoubleQuote(value);
        for (int i = 0; i < _dqNestDepth; i++)
            escaped = escaped.Replace("`", "``", StringComparison.Ordinal);
        return escaped;
    }

    /// <summary>
    /// Emit a command whose output is CAPTURED (command / process substitution),
    /// not written to the terminal — suppresses fused-pipeline wrapping for the
    /// duration so nested pipelines keep today's per-object shape.
    /// </summary>
    /// <summary>The body of the innermost <c>$( )</c> being emitted; a bare <c>&lt; file</c> prints the file only when it IS that body.</summary>
    [ThreadStatic] private static Command? _captureBody;

    private static string EmitCaptured(Command body)
    {
        var savedCaptureBody = _captureBody;
        _captureBody = body;
        try { return EmitCapturedCore(body); }
        finally { _captureBody = savedCaptureBody; }
    }

    private static string EmitCapturedCore(Command body)
    {
        _captureDepth++;
        // A substitution body is its own pipeline: the enclosing stage's `$input` is not its stdin.
        bool savedFeed = _pipeStageFeedsInput;
        _pipeStageFeedsInput = false;
        // bash does not inherit errexit into `$( … )` (no inherit_errexit): `x=$(f)` runs all of f.
        // Only the substitution's final status matters, checked after the assignment.
        // But a `set -e` INSIDE the body does apply there: `v=$(set -e; false; echo x)` leaves v empty
        // and $? = 1. Such a body starts with errexit OFF (not inherited) and gets its own checks; the
        // subshell scope (PipelineHead → PsBuild.ShellStateScope) restores the parent's flags after.
        bool ownErrexit = BodySetsErrexit(body);
        bool savedMode = _errexitMode;
        int savedExempt = _errexitExemptDepth;
        if (ownErrexit) { _errexitMode = true; _errexitExemptDepth = 0; }
        else _errexitExemptDepth++;
        // A substitution body is a SUBSHELL: `exit N` inside `$( … )` / `<( … )` leaves only it
        // (`v=$(exit 3); echo $?` prints 3). Raise the depth so `exit` emits the scoped `return`;
        // PipelineHead / Invoke-ProcessSub supply the script block it returns from.
        _subshellDepth++;
        bool savedErrTrapSub = PsBuild.ErrTrapNeedsErrtrace; PsBuild.ErrTrapNeedsErrtrace = true;
        try
        {
            var text = Emit(body);
            if (ownErrexit)
                return "$global:__BashErrexit = $false; " + PsBuild.ErrexitSuppressVar + " = 0; " + text;
            return _errexitMode && ConditionRunsCommand(body) ? PsBuild.ErrexitSuppressed(text) : text;
        }
        finally
        {
            _captureDepth--; _pipeStageFeedsInput = savedFeed; _subshellDepth--; PsBuild.ErrTrapNeedsErrtrace = savedErrTrapSub;
            _errexitMode = savedMode; _errexitExemptDepth = savedExempt;
        }
    }

    /// <summary>
    /// True while the simple command being emitted is a PIPE-TARGET stage (index &gt; 0). A stage that the
    /// emitter has to wrap in a script block (<c>&amp; { … }</c>: RC-7 splat hoisting, an env prefix) must
    /// forward the stage's pipeline input explicitly — PowerShell hands it to the block's <c>$input</c> only,
    /// never to the commands inside — so such wrappers emit <c>$input | cmd …</c> when this is set. Cleared
    /// for the body of a command / process substitution (<see cref="EmitCaptured"/>).
    /// </summary>
    [ThreadStatic]
    private static bool _pipeStageFeedsInput;

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
    /// Test seam forwarder for the fused lane's pure kill-switch parse
    /// (<see cref="FusedLane.IsFusionDisabledByEnvValue"/>). Kept on
    /// <see cref="PsEmitter"/> because the transpiler test suite addresses it here.
    /// </summary>
    internal static bool IsFusionDisabledByEnvValue(string? value)
        => FusedLane.IsFusionDisabledByEnvValue(value);

    // StackGuarded: every command-level recursion of the emitter passes here; the stack probe keeps a
    // deep (but depth-cap-accepted) AST from overflowing a small caller stack (NestingGuard).
    public static string Emit(Command cmd) => TryEmitCompoundOutputProcessSubs(StackGuarded(cmd), out var outputProcSubs) ? outputProcSubs : cmd switch
    {
        Command.If ifCmd => EmitCompoundBody(ifCmd.Redirects, () => EmitIf(ifCmd)),
        Command.BoolExpr boolExpr => EmitBoolExpr(boolExpr),
        Command.ForIn forIn => EmitCompoundBody(forIn.Redirects, () => EmitForIn(forIn)),
        Command.ForArith forArith => EmitCompoundBody(forArith.Redirects, () => EmitForArith(forArith)),
        Command.Select sel => EmitSelect(sel),
        Command.While whileCmd => EmitWhile(whileCmd),
        Command.Case caseCmd => EmitCompoundBody(caseCmd.Redirects, () => EmitCase(caseCmd)),
        Command.ArithCommand arith => EmitArithCommand(arith),
        Command.Subshell subshell => EmitSubshell(subshell),
        Command.BraceGroup braceGroup => EmitCompoundBody(braceGroup.Redirects, () => EmitBraceGroup(braceGroup)),
        Command.ShFunction func => EmitFunction(func),
        // `nohup CMD &` detaches itself and returns at once: no host job, so `&` adds nothing (and would hide the real $!).
        Command.Background { Inner: Command.Simple nohupBg } when IsNohup(nohupBg) => EmitSimpleFed(RewriteNohup(nohupBg)),
        Command.Background bg => EmitBackground(bg),
        Command.Simple simple => EmitSimpleFed(IsNohup(simple) ? RewriteNohup(simple) : simple),
        Command.Pipeline pipeline => EmitPipeline(pipeline),
        Command.AndOrList andOr => EmitAndOrList(andOr),
        Command.ShAssignment assign => EmitShAssignmentStatement(assign),
        Command.CommandList list => EmitCommandList(list),
        _ => throw new NotSupportedException($"Unknown command type: {cmd.GetType().Name}"),
    };

    private static T StackGuarded<T>(T node)
    {
        NestingGuard.EnsureStack();
        return node;
    }

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
        var priorErrexit = _errexitMode;
        var priorExempt = _errexitExemptDepth;
        var priorIntVars = _intVars;
        var priorAssocVars = _assocVars;
        _assocVars = new HashSet<string>(StringComparer.Ordinal);
        var priorArrayVars = _arrayVars;
        // BASH_REMATCH is an array `=~` fills: a bare `$BASH_REMATCH` is its element 0.
        _arrayVars = new HashSet<string>(StringComparer.Ordinal) { "BASH_REMATCH" };
        var priorCaseVars = _caseVars;
        _caseVars = new Dictionary<string, bool>(StringComparer.Ordinal);
        _intVars = new HashSet<string>(StringComparer.Ordinal);
        var priorReadsStatus = _scriptReadsStatus;
        var priorReadsLastArg = _scriptReadsLastArg;
        var priorPosix = _scriptPosix;
        var priorLastpipe = _scriptLastpipe;
        _scriptLastpipe = priorLastpipe || bash.Contains("lastpipe");
        _scriptPosix = priorPosix || bash.Contains("set -o posix");
        _scriptReadsLastArg = priorReadsLastArg || System.Text.RegularExpressions.Regex.IsMatch(bash, @"\$(_(?![A-Za-z0-9_])|\{_[^A-Za-z0-9_])");
        _scriptReadsStatus = priorReadsStatus || bash.Contains("$?") || bash.Contains("${?");
        var priorReadonly = _readonlyVars;
        _readonlyVars = new HashSet<string>(StringComparer.Ordinal);
        _readonlyVars.Add("SHELLOPTS"); _readonlyVars.Add("BASHOPTS");
        var priorNamerefs = _namerefs;
        _namerefs = new Dictionary<string, string>(StringComparer.Ordinal);
        var priorLitAssigned = _litAssigned; var priorPendingNamerefs = _pendingNamerefs;
        _litAssigned = new Dictionary<string, string>(StringComparer.Ordinal); _pendingNamerefs = new HashSet<string>(StringComparer.Ordinal);
        _context = context;
        // Only a script that itself turns errexit on gets the checks (a sourced file running under
        // its caller's `set -e` does not — see intentional-differences.md).
        _errexitMode = EnablesErrexit(bash);
        _errexitExemptDepth = 0;
        // Only a script that mentions pipefail pays for per-stage status capture (it also leaves the fused lane).
        var priorDefined = _definedFunctions;
        var priorAliasDefs = _aliasDefs; var priorAliasLine = _expandAliasesLine; var priorAliasStatic = _aliasStaticOnly; _aliasDefs = null; _aliasStaticOnly = false;
        var priorUnitFunctions = _unitFunctions;
        _unitFunctions = null;
        var priorPipefail = _pipefailMode;
        var priorNounset = _nounsetMode;
        _nounsetMode = priorNounset || InheritedNounset || bash.Contains("nounset", StringComparison.Ordinal)
            || System.Text.RegularExpressions.Regex.IsMatch(bash, @"\bset\s+(?:-[a-zA-Z]+\s+)*-[a-zA-Z]*u");
        _pipeStatusMode = bash.Contains("PIPESTATUS", StringComparison.Ordinal);
        var priorXtrace = _xtraceMode;
        var priorTrap = _trapMode;
        var priorDebugTrap = _debugTrapMode;
        _debugTrapMode = System.Text.RegularExpressions.Regex.IsMatch(bash, @"\btrap\s+(?:'[^'\n]*'|\x22[^\x22\n]*\x22)\s+DEBUG\b")
            && bash.Split('\n').Count(l => l.Trim() is { Length: > 0 } t && t[0] != '#') >= 3;
        var priorDeclarePrint = _declarePrintMode;
        _declarePrintMode = System.Text.RegularExpressions.Regex.IsMatch(bash, @"\b(?:declare|typeset)\s+(?:-[a-zA-Z]+\s+)*-[a-zA-Z]*p")
            || System.Text.RegularExpressions.Regex.IsMatch(bash, @"\bexport\s*(?:-p\s*)?(?:[|;&)}]|\r?\n|$)");
        _trapMode = System.Text.RegularExpressions.Regex.IsMatch(bash, @"\btrap\b");
        _xtraceMode = System.Text.RegularExpressions.Regex.IsMatch(bash, @"\bset\s+(?:-[a-zA-Z]+\s+)*(?:-[a-zA-Z]*x|-o\s+xtrace)");
        _pipefailMode = priorPipefail || InheritedPipefail || bash.Contains("pipefail", StringComparison.Ordinal) || bash.Contains("PIPESTATUS", StringComparison.Ordinal);
        try
        {
            Command? cmd;
            bool aliasesSpliced = false;
            // An alias holding part of a compound command (`alias LEFT={`): bash reads the alias text in its place.
            string? spliced = SpliceAliases(bash, out bool partialAlias);
            if (spliced is not null && partialAlias)
            {
                try { cmd = BashParser.Parse(spliced); aliasesSpliced = true; }
                catch (ParseException) { cmd = BashParser.Parse(bash); }
            }
            else
            {
                try { cmd = BashParser.Parse(bash); }
                catch (ParseException original) when (spliced is not null)
                {
                    try { cmd = BashParser.Parse(spliced); }
                    catch (ParseException) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(original).Throw(); throw; }
                    aliasesSpliced = true;
                }
            }
            if (cmd is null)
                return null;
            CollectCallSites(cmd);
            CollectAliasDefs(cmd, bash);
            if (aliasesSpliced) { _aliasDefs = null; _aliasStaticOnly = false; } // already expanded in the text
            return EmitBody(cmd);
        }
        finally
        {
            _context = prior;
            _intVars = priorIntVars;
            _assocVars = priorAssocVars;
            _arrayVars = priorArrayVars;
            _caseVars = priorCaseVars;
            _scriptReadsStatus = priorReadsStatus;
            _scriptReadsLastArg = priorReadsLastArg;
            _scriptPosix = priorPosix;
            _scriptLastpipe = priorLastpipe;
            _readonlyVars = priorReadonly;
            _namerefs = priorNamerefs;
            _litAssigned = priorLitAssigned; _pendingNamerefs = priorPendingNamerefs;
            _errexitMode = priorErrexit;
            _errexitExemptDepth = priorExempt;
            _pipefailMode = priorPipefail;
            _nounsetMode = priorNounset;
            _unitFunctions = priorUnitFunctions;
            _definedFunctions = priorDefined;
            _aliasDefs = priorAliasDefs; _expandAliasesLine = priorAliasLine; _aliasStaticOnly = priorAliasStatic;
            _xtraceMode = priorXtrace;
            _trapMode = priorTrap;
            _debugTrapMode = priorDebugTrap;
            _declarePrintMode = priorDeclarePrint;
        }
    }

    /// <summary>
    /// True while emitting a script whose stdin is the LAUNCHER's own stdin
    /// (<see cref="TranspileWithLauncherStdin"/>): the whole script is a stdin scope whose cursor the host
    /// installs in <c>$global:__BashStdIn</c> before it runs. Function bodies opt out (see <see cref="EmitFunction"/>).
    /// </summary>
    [ThreadStatic]
    private static bool _launcherStdin;

    /// <summary>
    /// Like <see cref="Transpile(string, TranspileContext)"/>, but the WHOLE script is emitted as a stdin
    /// scope: every stdin-reading command (<see cref="StdinReaders"/>, <c>read</c>, <c>while read</c>) is fed
    /// from <c>$global:__BashStdIn</c>, which the host fills lazily from the launcher's forwarded stdin. The
    /// result mentions <c>__BashStdIn</c> iff some command can read stdin — a caller that sees no mention
    /// should run the plain <see cref="Transpile(string, TranspileContext)"/> text and forward nothing.
    /// </summary>
    public static string? TranspileWithLauncherStdin(string bash, TranspileContext context)
    {
        var priorScope = _inStdinScope;
        var priorLauncher = _launcherStdin;
        _inStdinScope = true;
        _launcherStdin = true;
        try { return Transpile(bash, context); }
        finally
        {
            _inStdinScope = priorScope;
            _launcherStdin = priorLauncher;
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

    /// <summary>
    /// An <c>if</c>/<c>elif</c>/<c>while</c>/<c>until</c> condition: errexit-exempt in bash, so nothing
    /// emitted inside it is checked, and under errexit a condition that runs a command runs it
    /// suppressed — a function called there must keep going past its own failures.
    /// </summary>
    private static string EmitGuardedCondition(Command cond, Func<Command, string> emit)
    {
        var text = WithErrexitExempt(() => emit(cond));
        return _errexitMode && ConditionRunsCommand(cond) ? PsBuild.ErrexitSuppressedExpr(text) : text;
    }

    private static bool ConditionRunsCommand(Command cond) => cond switch
    {
        Command.BoolExpr or Command.ArithCommand => false,
        Command.Simple s when s.Words.Length == 1 && s.EnvPairs.IsEmpty && s.Redirects.IsEmpty
            && GetLiteralValue(s.Words[0]) is "true" or ":" or "false" => false,
        Command.AndOrList a => a.Commands.Any(ConditionRunsCommand),
        _ => true,
    };

    private static string EmitIf(Command.If ifCmd)
        => ApplyCompoundRedirects(EmitIfFromArm(ifCmd, 0), ifCmd.Redirects);

    /// <summary>
    /// The <c>if</c> from arm <paramref name="start"/> on. A pure-test arm (<c>[ ]</c>, <c>(( ))</c>,
    /// <c>true</c>) keeps its boolean-expression form and chains with <c>elseif</c>; an arm whose
    /// condition RUNS a command is hoisted (<see cref="IsHoistedCondition"/>): the condition runs
    /// as a statement — so its output streams, as bash prints it — and the arm tests the status
    /// it left. An <c>elif</c> arm that is hoisted nests in the previous arm's <c>else</c>, since
    /// its condition must run only when every earlier one failed.
    /// </summary>
    private static string EmitIfFromArm(Command.If ifCmd, int start)
    {
        var sb = new StringBuilder();
        bool anyHoisted = false;
        for (int i = start; i < ifCmd.Arms.Length; i++)
        {
            var arm = ifCmd.Arms[i];
            bool hoist = IsHoistedCondition(arm.Cond);
            if (i > start && hoist)
            {
                sb.Append(" else { ").Append(EmitIfFromArm(ifCmd, i)).Append(" }");
                return sb.ToString();
            }
            anyHoisted |= hoist;
            if (hoist)
                sb.Append(EmitHoistedCondition(arm.Cond)).Append("if (").Append(PsBuild.LastStatusTest()).Append(')');
            else
                sb.Append(i == start ? "if (" : " elseif (").Append(EmitGuardedCondition(arm.Cond, EmitCondition)).Append(')');
            sb.Append(" { ").Append(EmitBody(arm.Body)).Append(" }");
        }

        if (ifCmd.ElseBody is not null)
        {
            sb.Append(" else { ");
            sb.Append(EmitBody(ifCmd.ElseBody));
            sb.Append(" }");
        }
        else if (_errexitMode || anyHoisted)
        {
            // bash: an `if` whose branch is not taken has status 0. The failed condition's code
            // must not leak out — `f() { if false; then :; fi; }; set -e; f` keeps going, and
            // `if grep -q x f; then …; fi; echo $?` prints 0.
            sb.Append(" else { $global:LASTEXITCODE = 0 }");
        }

        return sb.ToString();
    }

    /// <summary>
    /// A condition is hoisted to statement position when it runs a command (anything but a pure
    /// <c>[ ]</c>/<c>[[ ]]</c>/<c>(( ))</c>/<c>true</c>/<c>false</c> test): in expression position
    /// (<see cref="PsBuild.ExitCodeTest"/>) its output must be <c>[void]</c>-ed, and bash prints it.
    /// </summary>
    private static bool IsHoistedCondition(Command cond) => ConditionRunsCommand(cond) || DebugActive;

    /// <summary>The hoisted condition statement(s), errexit-exempt (and suppressed under
    /// errexit) like <see cref="EmitGuardedCondition"/>, followed by "; ".</summary>
    private static string EmitHoistedCondition(Command cond)
    {
        // While DEBUG is on every condition is hoisted, so the action can run before it (each time it is tested).
        var text = WithErrexitExempt(() => !DebugActive ? Emit(cond)
            : cond is Command.AndOrList andOr ? EmitDebugAndOr(andOr) : WithDebugLead(cond, () => Emit(cond)));
        if (_errexitMode) text = PsBuild.ErrexitSuppressed(text);
        return PsBuild.HoistedCondition(text);
    }

    // ── Loop control: labels for break N / continue N ─────────────────────────────────────
    //
    // A PowerShell `break` / `continue` takes a LABEL, not a count, and is dynamically scoped:
    // `break 2` meant "the loop labelled 2", found none, and unwound the WHOLE script (bash: leave
    // two loops). Every emitted loop is labelled `:__psbash_l<depth>`, and `break N` / `continue N`
    // name the N-th enclosing one (clamped to the outermost, as bash does). The lexical stack is
    // reset for a function body, where a bare keyword keeps bash's dynamic behaviour (`break` in a
    // function called from a loop leaves the caller's loop). The `while read` loop is two PS loops
    // (records, then the lines of a record), so its entry has distinct break / continue labels.
    [ThreadStatic]
    private static List<(string Break, string Continue)>? _loopLabels;

    /// <summary>
    /// True inside a subshell's body (outside any loop or function within it): bash starts a subshell with no loop,
    /// so `( continue )` inside a loop warns and the subshell carries on. A bare PowerShell `continue` there reached
    /// the enclosing loop through the script block and skipped the rest of the iteration.
    /// </summary>
    [ThreadStatic]
    private static bool _loopBarrier;

    /// <summary>How many function bodies enclose the code being emitted; a definition inside one is global (bash has no function scope).</summary>
    [ThreadStatic]
    private static int _functionDepth;

    /// <summary>Push the label pair for a loop at <paramref name="depth"/>; returns the
    /// <c>:label </c> prefix to put directly before the (outer) loop keyword.</summary>
    private static string PushLoopLabel(int depth, string? continueLabel = null)
    {
        string label = "__psbash_l" + depth;
        (_loopLabels ??= new()).Add((label, continueLabel ?? label));
        return ":" + label + " ";
    }

    private static void PopLoopLabel() => _loopLabels!.RemoveAt(_loopLabels.Count - 1);

    /// <summary>A non-numeric loop-control count ends the script with status 128, as in bash.</summary>
    private static string NumericArgExit => _subshellDepth > 0 ? "$global:LASTEXITCODE = 128; return" : "exit 128";

    /// <summary><c>break [N]</c> / <c>continue [N]</c> → the labelled PowerShell keyword.</summary>
    private static string EmitLoopControl(string keyword, Command.Simple cmd)
    {
        if (_loopLabels is not { Count: > 0 } labels)
        {
            // At the top of a script (no loop, no function body, not eval/source text that runs inside its
            // caller's loop) bash warns and carries on with status 0. A bare PowerShell `break`/`continue`
            // there ends the whole pipeline instead, which left the client waiting on a dead command.
            if (_loopBarrier || (_functionDepth == 0 && _context == TranspileContext.Default))
                return $"Write-Error {PsBuild.SingleQuote(keyword + ": only meaningful in a `for', `while', or `until' loop")} -ErrorAction Continue; {PsBuild.SetStatus(0)}";
            return keyword; // no lexical loop (a function body): bash's dynamic scope, as before
        }
        int n = 1;
        string? check = null;
        if (cmd.Words.Length >= 2)
        {
            // A literal count is resolved here. A non-literal one (`break $n`) is checked at run time:
            // anything but a positive number is bash's "numeric argument required" (status 128) and still
            // leaves the innermost loop.
            if (TryGetPureLiteralText(cmd.Words[1].Parts, out var countText))
            {
                if (int.TryParse(countText, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
                    n = parsed;
                else
                    check = $"Write-Error {PsBuild.SingleQuote($"{keyword}: {countText}: numeric argument required")} -ErrorAction Continue; {NumericArgExit}; ";
            }
            else
            {
                string argText = EmitWord(cmd.Words[1]);
                check = $"$__psb_n = [string]({argText}); if ($__psb_n -notmatch '^[1-9][0-9]*$') {{ Write-Error ({PsBuild.SingleQuote(keyword + ": ")} + $__psb_n + ': numeric argument required') -ErrorAction Continue; {NumericArgExit} }}; ";
            }
        }
        // `break 1 2 3`: bash reports "too many arguments", sets status 1 and leaves the innermost loop
        // (a `continue` with too many arguments breaks too).
        if (cmd.Words.Length > 2)
        {
            check = $"Write-Error {PsBuild.SingleQuote($"{keyword}: too many arguments")} -ErrorAction Continue; {PsBuild.SetStatus(1)}; ";
            keyword = "break";
        }
        var target = labels[Math.Max(0, labels.Count - n)];
        return check + keyword + " " + (keyword == "break" ? target.Break : target.Continue);
    }

    /// <summary>
    /// Loop variables whose value may hold IFS whitespace or glob characters, so an unquoted <c>$v</c> must split
    /// like any other variable. Unquoted list words are already split, so only quoted items can introduce them
    /// (<c>for c in "a b" "c d"; do set -- $c</c>).
    /// </summary>
    [ThreadStatic]
    private static HashSet<string>? _splitLoopVars;

    private static bool ForListMaySplit(ImmutableArray<CompoundWord> list)
    {
        static bool Risky(string s) => s.AsSpan().IndexOfAny(" \t\n*?[\\") >= 0;
        static bool PartRisky(WordPart p) => p switch
        {
            WordPart.SingleQuoted sq => Risky(sq.Value),
            WordPart.AnsiCQuoted => true,
            WordPart.EscapedLiteral => true,
            WordPart.DoubleQuoted dq => dq.Parts.Any(i => i is not WordPart.Literal l || Risky(l.Value)),
            _ => false,
        };
        return list.Any(w => w.Parts.Any(PartRisky));
    }

    private static string EmitForIn(Command.ForIn forIn)
    {
        int depth = _loopDepth++;
        string label = PushLoopLabel(depth);
        try
        {
            var sb = new StringBuilder();
            sb.Append(IterGuardPrefix(depth));
            sb.Append(label);

            // `for i; do` (no `in`) iterates $@; `for i in ; do` has an explicit, empty list and runs zero times.
            if (forIn.List.IsEmpty && forIn.ExplicitIn)
            {
                sb.Append($"foreach (${forIn.Var} in @()) {{ ");
            }
            else if (forIn.List.IsEmpty)
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
            sb.Append(DebugAt(forIn.Line)); // trap DEBUG: before each iteration, with the `for` line
            // The loop variable is an ordinary shell variable: the body's functions see it and it keeps its last value after the loop.
            sb.Append($"$env:{forIn.Var} = [string]${forIn.Var}; ");

            var vars = _loopVars ??= new HashSet<string>();
            bool added = vars.Add(forIn.Var);
            // `for i; do` (no list) iterates "$@": arbitrary strings.
            bool addedSplit = (forIn.List.IsEmpty || ForListMaySplit(forIn.List)) && (_splitLoopVars ??= new HashSet<string>()).Add(forIn.Var);
            try
            {
                sb.Append(EmitBody(forIn.Body));
            }
            finally
            {
                if (addedSplit) _splitLoopVars!.Remove(forIn.Var);
                if (added) vars.Remove(forIn.Var);
            }

            sb.Append(" }");
            string loop = ApplyCompoundRedirects(sb.ToString(), forIn.Redirects);
            // The PowerShell loop variable outlives the loop with its last value, and arithmetic reads a PowerShell
            // variable before the shell variable: `for i in 1 2 3; do :; done; i=0; ...$((i+1))` then counted from 3.
            // The shell variable (`$env:`) already holds the last value, so drop the lexical one. A name the
            // enclosing function declared `local` (already a lexical variable) is left alone.
            return added ? loop + "; Remove-Variable -Name " + forIn.Var + " -ErrorAction SilentlyContinue" : loop;
        }
        finally
        {
            PopLoopLabel();
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

    private static readonly System.Text.RegularExpressions.Regex BarePrefixDoubleQuotedItem = new(@"^([A-Za-z0-9_.+/:@%^~=-]+)(""(?:[^""`]|`.)*"")$", System.Text.RegularExpressions.RegexOptions.Compiled);

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
            arrayName = ResolveNameref(b.Name);
            return true;
        }
        if (w.Parts[0] is WordPart.DoubleQuoted dq && dq.Parts.Length == 1
            && dq.Parts[0] is WordPart.BracedVarSub qb && qb.Suffix == "[@]")
        {
            arrayName = ResolveNameref(qb.Name);
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
            // Unquoted $@ / $* splits each parameter again on IFS, so it takes the split path below.
            if (IsPositionalAllWord(list[0]) && !IsUnquotedPositionalListWord(list[0]))
                return PsBuild.BuildPositionalExpansion("@");

            // `for x in "${arr[@]}"` (and bare ${arr[@]}/${arr[*]}) iterates per
            // element. Quoted "${arr[@]}" would otherwise stringify the array into
            // one space-joined word and run once. Emit the array variable directly.
            if (list[0].Parts is [WordPart.DoubleQuoted { Parts: [WordPart.BracedVarSub { Suffix: { } opSuffix } opVar] }]
                && (IsArrayElementOperator(opSuffix) || IsArraySliceSuffix(opSuffix) || (opVar.Name == "@" && IsPositionalSliceSuffix(opSuffix))))
                return EmitBracedVar(opVar);
            if (IsArrayAllWord(list[0], out var arrName))
                return $"$(if (${arrName} -is [System.Collections.IDictionary]) {{ ${arrName}.Values }} else {{ ${arrName} }})";
            // `for k in "${!m[@]}"`: the KEYS (or indices), one iteration each.
            var keysWord = list[0].Parts switch
            {
                [WordPart.BracedVarSub { Suffix: "![@]" or "![*]" } k] => k,
                [WordPart.DoubleQuoted { Parts: [WordPart.BracedVarSub { Suffix: "![@]" } qk] }] => qk,
                _ => null,
            };
            if (keysWord is not null)
                return EmitBracedVar(keysWord);

            // `for w in $x` — an UNQUOTED ordinary variable is word-split on IFS,
            // so `x="a b c"` runs the loop THREE times. RC-7 applied this splitting
            // to command arguments but not to a for-in list, so the loop ran ONCE
            // over the whole string — a silent wrong result, not a parse error.
            // (The quoted form `for w in "$x"` correctly stays one iteration.)
            if (IsPureUnquotedVarWord(list[0]))
                return EmitUnquotedVarSplitArray(list[0]);

            // `for f in $(cmd)` / `` `cmd` ``: the substitution's TEXT is word-split on IFS and each
            // word glob-expanded (the capture array only splits on lines, so `$(echo a b c)` ran once).
            if (IsPureUnquotedCommandSubWord(list[0]))
                return EmitUnquotedSplitWordArray(list[0]);

            // `for f in *.txt`: pathname expansion by the shell (relative names, no hidden files, sorted;
            // an unmatched pattern is the one literal item, nullglob off) -- see EmitGlobPatternExpr.
            if (IsGlobWord(list[0]) || IsUnquotedDefaultAltWord(list[0]) || IsUnquotedPositionalListWord(list[0]))
                return EmitUnquotedSplitWordArray(list[0]);

            // `for i in a$x` / `=$*=`: split like bash; it used to iterate once over the literal text `a$env:x`.
            if (UnquotedAffixExpansionIndex(list[0], ordinaryVar: true) is var affixAt and >= 0)
                return EmitAffixWordArray(list[0], affixAt);

            var single = EmitWord(list[0]);
            if (WordIsGlob(list[0], single))
                // nullglob is OFF by default in bash: an unmatched glob iterates
                // ONCE with the literal pattern word, never zero times. Resolve-Path
                // errors + yields nothing on no-match (=> zero iterations), diverging.
                // Resolve-BashGlob expands matches but falls back to the literal word
                // when there is no match, matching bash.
                return $"(Resolve-BashGlob {single})";
            // Quote a BARE literal exactly like the multi-item path does. Without this
            // `for x in one` emitted `foreach ($x in one)`, where PowerShell treats the
            // bare word as a COMMAND to invoke — "one: command not found" — while the
            // two-item form `for x in one two` was correctly quoted. FormatForItem
            // leaves anything EmitWord already rendered as a PS value ($x, "a", (…),
            // @(…), a number) untouched.
            return ForListItem(list[0], single);
        }

        // Multiple items. An unquoted ordinary variable or command substitution word-splits
        // (`for w in $a $(cmd) z`), and each split array must FLATTEN into the list. PowerShell's comma
        // operator does not splice (`'a',@('b','c')` is two elements, the second an array), so a list with
        // any split word is emitted as `@( item; item; … )`, where every statement's output is enumerated
        // into one flat array. A glob word in such a list expands on its own (`Resolve-BashGlob`).
        bool anySplit = false;
        foreach (var word in list)
            anySplit |= IsUnquotedSplitWord(word) || UnquotedAffixExpansionIndex(word, ordinaryVar: true) >= 0;

        if (anySplit)
        {
            var flat = new List<string>();
            foreach (var word in list)
            {
                if (UnquotedAffixExpansionIndex(word, ordinaryVar: true) is var wordAffixAt and >= 0)
                {
                    flat.Add(EmitAffixWordArray(word, wordAffixAt));
                    continue;
                }
                if (IsUnquotedSplitWord(word))
                {
                    flat.Add(EmitUnquotedSplitWordArray(word));
                    continue;
                }
                string emitted = EmitWord(word);
                flat.Add(WordIsGlob(word, emitted)
                    ? "Resolve-BashGlob " + emitted
                    : ForListItem(word, emitted));
            }
            return "@(" + string.Join("; ", flat) + ")";
        }

        // Check for glob patterns
        bool hasGlob = false;
        foreach (var word in list)
        {
            if (WordIsGlob(word, EmitWord(word)))
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

        // Plain items: join with commas, quoting bare literals.
        var items = new List<string>();
        foreach (var word in list)
            items.Add(ForListItem(word, EmitWord(word)));
        return string.Join(",", items);
    }

    private static readonly System.Text.RegularExpressions.Regex TildeBarewordItem =
        new(@"^(\$(?:HOME|PWD|env:OLDPWD))([/\\][^$`""'()@{}]*)$");

    /// <summary>
    /// One <c>for</c>-list item. A word that glues text, quotes, variables and command substitutions together
    /// (<c>a"$y"b</c>) is one string value: flatten it. EmitWord renders it as command-mode text (<c>a"$env:y"b</c>),
    /// which <see cref="FormatForItem"/> single-quoted, so the loop saw the PowerShell code instead of the value.
    /// </summary>
    private static string ForListItem(CompoundWord word, string emitted)
    {
        if (word.Parts.Length > 1 && word.Parts.All(p => p is WordPart.Literal or WordPart.EscapedLiteral or WordPart.SingleQuoted
                or WordPart.AnsiCQuoted or WordPart.DoubleQuoted or WordPart.SimpleVarSub or WordPart.BracedVarSub or WordPart.CommandSub)
            && word.Parts.Any(p => p is not (WordPart.Literal or WordPart.EscapedLiteral or WordPart.SingleQuoted or WordPart.AnsiCQuoted)))
            return FlattenPartsToDoubleQuotedString(word.Parts);
        return FormatForItem(emitted);
    }

    private static string FormatForItem(string item)
    {
        if (item.Length == 0) return "''";
        // Only a BARE literal needs quoting. An item EmitWord already rendered as a PS value —
        // a single/double-quoted string ('a\t1'), a variable ($x), a subexpression ($(…)/(…)),
        // or a brace-expansion array (@('a','')) — must pass through untouched; re-wrapping it
        // in '…' produced ''a\t1'' and '@('a','')', neither of which parses (grammar-fuzz find).
        char c = item[0];
        // A tilde word (`~/src`) renders as the bareword `$HOME/src`, valid as a command argument but not as a
        // list element (`$HOME/src,$HOME/git` parses as a division): make it a string concatenation.
        var tildeWord = TildeBarewordItem.Match(item);
        if (tildeWord.Success)
            return "(" + tildeWord.Groups[1].Value + " + '" + tildeWord.Groups[2].Value.Replace("'", "''") + "')";
        // A bare prefix glued to a double-quoted part (`y="$x"`, `--opt="a b"`) is command-mode text: the quoted tail must still expand.
        var gluedQuote = BarePrefixDoubleQuotedItem.Match(item);
        if (gluedQuote.Success)
            return "('" + gluedQuote.Groups[1].Value.Replace("'", "''") + "' + " + gluedQuote.Groups[2].Value + ")";
        if (c is '$' or '"' or '\'' or '(' or '@') return item;
        // Bare command-mode text with an expansion (`a$(...)`, `x$env:y`): single-quoted, the code was the value.
        if (item.IndexOf('$') >= 0 && item.IndexOfAny(['\'', '"']) < 0)
            return "\"" + item + "\"";
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
        string label = PushLoopLabel(depth);
        try
        {
            // Register loop var before emitting header so init/cond/step all use $var
            string? loopVar = ExtractArithVar(forArith.Init);
            var vars = _loopVars ??= new HashSet<string>();
            bool added = loopVar is not null && vars.Add(loopVar);
            try
            {
                if (forArith.Line > 0 && DebugActive)
                    return ApplyCompoundRedirects(EmitDebugForArith(forArith, depth, label, loopVar), forArith.Redirects);
                var sb = new StringBuilder();
                sb.Append(IterGuardPrefix(depth));
                sb.Append(label).Append("for (");
                sb.Append(EmitForArithClause(forArith.Init, initializeLoopVar: loopVar));
                sb.Append("; ");
                sb.Append(EmitForArithCondition(forArith.Cond));
                sb.Append("; ");
                sb.Append(EmitForArithClause(forArith.Step, initializeLoopVar: null));
                sb.Append(") { ");
                sb.Append(IterGuardCheck(depth));
                sb.Append(EmitBody(forArith.Body));
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
            PopLoopLabel();
            _loopDepth--;
        }
    }

    /// <summary>
    /// <c>for ((init; cond; step))</c> while DEBUG is on: bash runs the action before each clause it evaluates (init
    /// once; cond before every pass, the failing one included; step after every pass), and a PowerShell <c>for</c>
    /// header cannot hold it. A <c>while</c> whose first pass skips the step; <c>continue</c> still reaches the step.
    /// </summary>
    private static string EmitDebugForArith(Command.ForArith forArith, int depth, string label, string? loopVar)
    {
        int line = forArith.Line;
        string first = "$__psbash_af" + depth;
        var sb = new StringBuilder();
        string init = EmitForArithClause(forArith.Init, initializeLoopVar: loopVar);
        if (forArith.Init is not null) sb.Append(DebugAt(line));
        if (init.Length > 0) sb.Append(init).Append("; ");
        sb.Append(first).Append(" = $true; ");
        sb.Append(IterGuardPrefix(depth)).Append(label).Append("while ($true) { ");
        sb.Append(IterGuardCheck(depth));
        sb.Append("if (-not ").Append(first).Append(") { ");
        if (forArith.Step is not null)
            sb.Append(DebugAt(line)).Append(EmitForArithClause(forArith.Step, initializeLoopVar: null)).Append("; ");
        sb.Append("}; ").Append(first).Append(" = $false; ");
        if (forArith.Cond is not null) sb.Append(DebugAt(line));
        sb.Append("if (-not (").Append(EmitForArithCondition(forArith.Cond)).Append(")) { break }; ");
        sb.Append(EmitBody(forArith.Body));
        sb.Append(" }");
        return sb.ToString();
    }

    private static string EmitWhile(Command.While whileCmd)
    {
        // Special case: while read VAR -> ForEach-Object pipeline (no infinite loop risk).
        // The classic `while read line; do ...; done < input.txt` idiom attaches its input
        // redirect here; ApplyCompoundRedirects feeds it in via Get-Content | & { $input | ... }.
        // The loop streams its input itself (`$input |`, or the shared stdin queue when it
        // inherits a compound's stdin), so it never opens a stdin scope of its own: with its OWN
        // `< file` the body runs outside any scope and the redirect feeds `$input`.
        if (IsWhileRead(whileCmd.Cond, out var readVar, out var readIfs))
        {
            // Its ONLY redirect is its own input (`done < f`, `<<< "$s"`, a heredoc): read the source
            // straight into the loop header. bash runs this loop in the CURRENT shell (only a pipe makes
            // a subshell), but the general `Get-Content f | & { … }` form put it in a script block, so a
            // `return` in a function's read loop left only that block (`echo never` still ran).
            if (!whileCmd.Redirects.IsDefaultOrEmpty
                && whileCmd.Redirects is [{ Fd: 0, Op: "<" or "<<" or "<<<" } only])
            {
                string readLoop = WithStdinScope(false, () => EmitWhileRead(readVar, readIfs, whileCmd.Body, ReadLoopSource(only)));
                // `done < missing`: the redirect fails the loop (status 1, body never runs, and under set -e the script ends).
                if (only is { Op: "<" } && EmitInputRedirectTarget(only.Target) is var readTarget && readTarget != "$null")
                    readLoop = PsBuild.InputRedirectGuardInline(readTarget, readLoop, RedirectFailExit());
                return readLoop;
            }

            bool readsQueue = _inStdinScope && !HasInputRedirect(whileCmd.Redirects);
            return ApplyCompoundRedirects(
                WithStdinScope(readsQueue, () => EmitWhileRead(readVar, readIfs, whileCmd.Body)),
                whileCmd.Redirects, stdinScope: false);
        }

        return EmitCompoundBody(whileCmd.Redirects, () => EmitWhileLoop(whileCmd));
    }

    private static string EmitWhileLoop(Command.While whileCmd)
    {
        int depth = _loopDepth++;
        string label = PushLoopLabel(depth);
        try
        {
            var sb = new StringBuilder();
            sb.Append(IterGuardPrefix(depth));

            if (IsHoistedCondition(whileCmd.Cond))
            {
                // The condition runs as a statement at the top of each pass (its output streams;
                // `continue` re-runs it, as bash does). bash's loop status is the last body
                // command's, or 0 when the body never ran — never the failed condition's, so the
                // body's status is saved per pass and restored on the way out.
                string st = "$__psbash_wst" + depth;
                sb.Append(st).Append(" = 0; ").Append(label).Append("while ($true) { ");
                sb.Append(IterGuardCheck(depth));
                sb.Append(EmitHoistedCondition(whileCmd.Cond));
                sb.Append("if (").Append(PsBuild.LastStatusTest(negate: !whileCmd.IsUntil)).Append(") { ");
                sb.Append("$global:LASTEXITCODE = ").Append(st).Append("; break }; ");
                sb.Append(EmitBody(whileCmd.Body));
                sb.Append("; ").Append(st).Append(" = $global:LASTEXITCODE }");
                return ApplyCompoundRedirects(sb.ToString(), whileCmd.Redirects);
            }

            sb.Append(label).Append("while (");
            var condText = EmitGuardedCondition(whileCmd.Cond, EmitWhileCondition);

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
            sb.Append(EmitBody(whileCmd.Body));
            sb.Append(" }");
            // bash: a loop ended by its condition has the last body command's status (0 here,
            // or errexit would have left) — not the failed condition's, which must not leak out
            // of a function ending in `while [ $i -lt 3 ]; do …; done`.
            if (_errexitMode && whileCmd.Redirects.IsDefaultOrEmpty)
                sb.Append("; $global:LASTEXITCODE = 0");
            return ApplyCompoundRedirects(sb.ToString(), whileCmd.Redirects);
        }
        finally
        {
            PopLoopLabel();
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
    /// contexts. (A pattern holding an expansion — <c>$pat</c> — goes through
    /// <see cref="EmitCasePatternTest"/> instead.)
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

    /// <summary>
    /// The bash PATTERN text of a word whose value is static (literals, quotes, glob parts), for
    /// <see cref="BashPattern"/>: unquoted literal text is backslash-escaped char by char, quoted
    /// text likewise (both are literal in a pattern), glob/extglob parts keep their raw spelling.
    /// False when the word holds an expansion — its pattern is only known at run time.
    /// </summary>
    private static bool TryGetStaticPatternText(CompoundWord word, out string raw)
    {
        var sb = new StringBuilder();
        raw = "";
        foreach (var part in word.Parts)
        {
            switch (part)
            {
                case WordPart.GlobPart g: sb.Append(g.Pattern); break;
                case WordPart.Literal or WordPart.EscapedLiteral or WordPart.SingleQuoted
                    or WordPart.AnsiCQuoted or WordPart.DoubleQuoted:
                    if (!TryGetPureLiteralText(ImmutableArray.Create(part), out var lit)) return false;
                    foreach (char c in lit)
                    {
                        if (!char.IsLetterOrDigit(c)) sb.Append('\\');
                        sb.Append(c);
                    }
                    break;
                default: return false;
            }
        }
        raw = sb.ToString();
        return true;
    }

    private static void AppendLiteralWildcardChar(StringBuilder sb, char c)
    {
        // Backtick is the PowerShell -Wildcard escape; it survives verbatim inside the
        // single-quoted clause string the caller wraps this in.
        if (c is '*' or '?' or '[' or ']') sb.Append('`');
        sb.Append(c);
    }

    /// <summary>
    /// The test of one case pattern against <c>$_</c>. A pattern holding an expansion (<c>$i</c>,
    /// <c>"$i"</c>, <c>$(cmd)</c>) is only known at run time: an unquoted expansion's value is pattern
    /// text, quoted parts are literal — the same rule as the right-hand side of <c>[[ x == $p ]]</c>.
    /// </summary>
    private static string EmitCasePatternTest(string raw)
    {
        // A leading ~ expands in a case pattern too (`case /home/u in ~)`).
        if (raw.IndexOfAny(['$', '`']) >= 0 || raw.StartsWith('~'))
        {
            var word = new CompoundWord(BashParser.DecomposeWord(raw));
            if (!TryGetStaticPatternText(word, out _))
                return PsBuild.DynamicPatternTest("$_", EmitGlobPatternExpr(word, mapPaths: false));
        }
        return PsBuild.PatternTest("$_", BashPattern.ToAnchoredRegex(raw));
    }

    private static string EmitCase(Command.Case caseCmd)
    {
        // Every arm matches through BashPattern (bash glob/extglob → anchored regex) tested by the
        // runtime's BashPatternMatch: CASE-SENSITIVE, as bash's case is, unless `shopt -s
        // nocasematch` is on at run time. PowerShell's own `switch` (-eq) and `switch -Wildcard`
        // are case-insensitive, so `case abc in A*)` and `case ABC in abc)` both matched. Each
        // clause is a script-block test in the arms' own order, so a mid-list `*)` wins where bash
        // says it does (a PS `default` only ran when nothing else matched).
        // `case x in esac` is valid and does nothing (status 0); a `switch` with no clause is a PowerShell error.
        if (caseCmd.Arms.IsDefaultOrEmpty)
            return $"[void]({EmitCaseExpr(caseCmd.Expr)}); {PsBuild.SetStatus(0)}";
        var sb = new StringBuilder("switch (");
        sb.Append(EmitCaseExpr(caseCmd.Expr));
        sb.Append(") { ");

        for (int i = 0; i < caseCmd.Arms.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            var arm = caseCmd.Arms[i];
            var bodyText = EmitCaseArmBody(caseCmd.Arms, i);
            for (int p = 0; p < arm.Patterns.Length; p++)
            {
                if (p > 0) sb.Append(' ');
                sb.Append(arm.Patterns[p] == "*"
                    ? "{ $true }"
                    : "{ " + EmitCasePatternTest(arm.Patterns[p]) + " }");
                sb.Append(" { ").Append(bodyText).Append(" }");
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
            sb.Append(EmitBody(arms[j].Body));
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
        // An empty `$(cmd)` yields NO value and a `switch` over nothing runs no arm (bash: the word is '' and `*)` matches).
        if (emitted.StartsWith("$(", StringComparison.Ordinal))
            return "[string]" + emitted;
        if (emitted.Length > 0 && (emitted[0] == '$' || emitted[0] == '"' || emitted[0] == '\'' || emitted[0] == '('))
            return emitted;
        // Bare literal — wrap in single quotes so PowerShell treats it as a string.
        return $"'{SqEsc(emitted)}'";
    }

    /// <summary>PowerShell keywords bash does not know: as a bare command word each would begin a PowerShell statement
    /// (`var x` is a parse error, `switch x` a statement), so the command is called by its quoted name instead.</summary>
    private static readonly HashSet<string> PsKeywordCommandNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "begin", "catch", "class", "clean", "configuration", "data", "define", "dynamicparam", "elseif", "end", "enum",
        "filter", "finally", "foreach", "from", "hidden", "inlinescript", "param", "parallel", "process", "sequence",
        "static", "switch", "throw", "try", "using", "var", "workflow",
    };

    /// <summary>The non-constant aliases PowerShell 7 defines on Windows; each shadows a same-named function.</summary>
    private static readonly HashSet<string> PsDefaultAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "ac", "clc", "clhy", "cli", "clp", "cls", "clv", "cnsn", "compare", "copy",
        "cpi", "cpp", "cvpa", "dbp", "del", "dnsn", "ebp", "epal", "epcsv", "erase", "etsn", "exsn",
        "fc", "fhx", "fl", "foreach", "ft", "fw", "gal", "gbp", "gc", "gci", "gcm", "gcs", "gdr", "gerr", "ghy", "gi", "gjb",
        "gl", "gm", "gmo", "gp", "gps", "gpv", "group", "gsn", "gsv", "gu", "gv", "h", "icm", "iex", "ihy", "ii",
        "ipal", "ipcsv", "ipmo", "irm", "iwr", "measure", "mi", "move", "mp", "nal",
        "ndr", "ni", "nmo", "nsn", "nv", "ogv", "oh", "r", "rbp", "rcjb", "rcsn", "rd", "rdr",
        "ren", "ri", "rjb", "rmo", "rni", "rnp", "rp", "rsn", "rv", "rvpa", "sajb", "sal", "saps", "sasv", "sbp",
        "shcm", "si", "sl", "sls", "sp", "spjb", "spps", "spsv", "start", "sv",
        "where", "wjb", "write",
    };

    /// <summary>`$name` for an array write: inside a function (outside any subshell) an array the function did not declare
    /// `local` is the shell's own, so `stack+=(x)` / `a=(...)` / `a[i]=v` must reach the caller, not a function-local copy.</summary>
    /// <summary>Names the function that runs an <c>eval</c> declared <c>local</c>: set around the run-time transpile of the eval text.</summary>
    [ThreadStatic]
    internal static HashSet<string>? EvalLocalNames;

    /// <summary>Whether the code being emitted reads a stdin scope (BashTranspiler's eval entry sets it).</summary>
    internal static bool InStdinScope { get => _inStdinScope; set => _inStdinScope = value; }

    /// <summary>Numbers the save variable of each dot-sourced eval stdin scope (see <see cref="EmitEvalWithStdin"/>).</summary>
    [ThreadStatic] private static int _evalStdinSeq;

    /// <summary>
    /// An <c>eval</c> that is handed stdin (<c>... | eval "$c"</c>, <c>eval "$c" &lt;&lt;&lt; text</c>). Its emission is a
    /// statement list (the runtime block, or a static text transpiled inline), which is no pipeline element - the whole
    /// script failed to parse - and the commands in the eval text must read the piped records. Emit the eval inside a
    /// stdin scope (a static text is transpiled in it; a runtime text sees <c>$global:__BashStdIn</c> and is transpiled
    /// in it at run time) and run it in a block that turns <c>$input</c> into that scope. A pipe stage is a subshell
    /// (<c>&amp;</c>); a here-string keeps the current shell (dot-sourced, so <c>eval 'x=1' &lt;&lt;&lt; q</c> sets x).
    /// </summary>
    private static string EmitEvalWithStdin(Func<string> emitEval, bool dotSource)
    {
        var body = WithStdinScope(true, emitEval);
        return dotSource
            ? ". { " + PsBuild.StdinScope(body, "__psbash_evin_prev" + (++_evalStdinSeq)) + " }"
            : "& { " + PsBuild.StdinScope(body) + " }";
    }

    private static string ArrayWriteTarget(string name) =>
        ResolveNameref(name) is var resolved && resolved != name ? ArrayWriteTarget(resolved) :
        // eval'd text runs in the calling function's scope: an array it assigns is the shell's own unless that function declared it `local`.
        _context == TranspileContext.Eval && _functionDepth == 0 && _subshellDepth == 0
            ? (EvalLocalNames?.Contains(name) == true ? "$" + name : "$global:" + name) :
        _functionDepth > 0 && _subshellDepth == 0 && _functionLocalNames is { } locals && !locals.Contains(name)
            ? "$global:" + name
            : "$" + name;

    private static string EmitFunction(Command.ShFunction func)
    {
        // `$foo-bar() { :; }` / `foo-$(x)() { :; }`: the name is not expanded, so it is not a valid identifier (status 1).
        if (func.Name.AsSpan().IndexOfAny('$', '`') >= 0)
            return $"Write-Error {PsBuild.SingleQuote("`" + func.Name + "': not a valid identifier")} -ErrorAction Continue; {PsBuild.SetStatus(1)}";
        (_unitFunctions ??= new HashSet<string>(StringComparer.Ordinal)).Add(func.Name);
        // A scalar `local` is dynamically scoped (bash): a function this one calls sees the local value.
        // So it lives in the shell variable itself (`$env:x`), saved on first `local` and restored when the
        // function exits — not in a lexical PowerShell variable the callee could never read.
        var dynLocals = new HashSet<string>(StringComparer.Ordinal);
        var priorCollect = _collectScalarLocals;
        _collectScalarLocals = dynLocals;
        List<string> localVars;
        try { localVars = CollectLocalVars(func.Body); }
        finally { _collectScalarLocals = priorCollect; }
        var priorDyn = _dynLocals;
        _dynLocals = dynLocals;
        var priorFunctionLocals = _functionLocalNames;
        var priorFunctionReadonly = _readonlyVars is null ? null : new HashSet<string>(_readonlyVars, StringComparer.Ordinal);
        _functionLocalNames = new HashSet<string>(localVars.Concat(dynLocals), StringComparer.Ordinal);
        var vars = _loopVars ??= new HashSet<string>();
        var added = new List<string>();
        foreach (var v in localVars)
            if (vars.Add(v)) added.Add(v);
        // A function body starts with no lexical loop: its `break` stays bare (dynamic, as bash).
        var outerLabels = _loopLabels;
        _loopLabels = null;
        var outerBarrier = _loopBarrier;
        _loopBarrier = false;

        try
        {
            // A function is typically a FILTER (`echo x | f`): its stdin is whatever it is called with, not the
            // launcher's. Under the launcher-stdin scope its body therefore reads `$input`/the pipe as before.
            _functionDepth++;
            bool savedErrTrap = PsBuild.ErrTrapNeedsErrtrace; PsBuild.ErrTrapNeedsErrtrace = true;
            string body;
            // The body is emitted as inside a stdin scope; one that reads no stdin comes out unchanged. One that does
            // (`indent() { sed 's/^/  /'; }`, `f() { read -r x; }`) takes the pipe it is called with as its stdin
            // (PsBuild.FunctionStdinScope) and otherwise the stdin of whatever called it.
            string ComputeBody()
            {
                string text = WithStdinScope(true, () => EmitBody(func.Body));
                if (text.Contains("__BashStdIn", StringComparison.Ordinal) || text.Contains("Invoke-BashRead", StringComparison.Ordinal)
                    || text.Contains("Invoke-BashMapfile", StringComparison.Ordinal))
                    return PsBuild.FunctionStdinScope(text);
                return _launcherStdin ? WithStdinScope(false, () => EmitBody(func.Body)) : EmitBody(func.Body);
            }
            try
            {
                body = FindDynamicNameref(func.Body) is { } dynamicNameref
                    ? EmitNamerefVariants(func.Name, dynamicNameref, ComputeBody)
                    : ComputeBody();
            }
            finally { _functionDepth--; PsBuild.ErrTrapNeedsErrtrace = savedErrTrap; }
            // `trap '...' RETURN` inside the function runs when the function returns (then is spent, so a
            // later call does not re-fire it). Only a body that calls `trap` pays for the wrapper.
            if (body.Contains("Invoke-BashTrap", StringComparison.Ordinal))
                body = "$global:__BashTrapRETURN = $null; try { " + body + " } finally { "
                     + "$__psbash_rs = $global:LASTEXITCODE; $__psbash_rt = $global:__BashTrapRETURN; $global:__BashTrapRETURN = $null; "
                     + "try { if ($__psbash_rt) { & $__psbash_rt } } catch { }; $global:LASTEXITCODE = $__psbash_rs }";
            // Wrap the function body with save/restore of $global:BashPositional so
            // that recursive calls each see their own positional args ($1, $2, $@, $#)
            // rather than the top-level caller's args. Without this, a recursive call
            // overwrites the global and the outer frame sees the inner frame's values.
            // A run-time eval (`eval "local $n"`, `local "$1"`) can declare locals whose names were unknown here: restore
            // every `$__lsd_*` slot it parked in this call. Only a body with such an eval pays for the scan.
            if (body.Contains("$__psbash_eval_src", StringComparison.Ordinal))
                body = "try { " + body + " } finally { foreach ($__psbLsdV in @(Get-Variable -Name '__lsd_*' -Scope 0 -ErrorAction SilentlyContinue)) "
                     + "{ if ($null -ne $__psbLsdV.Value) { [System.Environment]::SetEnvironmentVariable($__psbLsdV.Name.Substring(6), $__psbLsdV.Value.v) } } }";
            var sb = new StringBuilder();
            // A PowerShell alias outranks a function of the same name (`r`, `h`, `diff`), so a bash function
            // that shares its name would never run; drop the alias first.
            if (PsDefaultAliases.Contains(func.Name))
                sb.Append("Remove-Item -LiteralPath ").Append(PsBuild.SingleQuote("Alias:" + func.Name)).Append(" -Force -ErrorAction SilentlyContinue; ");
            // `outer() { inner() { :; }; }` defines `inner` for the whole shell when `outer` runs; a PowerShell
            // function declared in a function body would die with it.
            sb.Append("function ").Append(_functionDepth > 0 ? "global:" : "").Append(func.Name);
            sb.Append(" {").Append(PsBuild.FunctionPrologue).Append(PsBuild.FunctionSourceMark(func.Source));
            if (dynLocals.Count > 0)
            {
                // Each name's saved slot starts $null in THIS call (a bare read would see the caller's).
                foreach (var v in dynLocals) sb.Append($"$__lsd_{v} = $null; ");
                sb.Append("try { ").Append(body).Append(" } finally { ");
                foreach (var v in dynLocals)
                    sb.Append($"if ($null -ne $__lsd_{v}) {{ $env:{v} = $__lsd_{v}.v }}; ");
                sb.Append('}');
            }
            else
                sb.Append(body);
            sb.Append(PsBuild.FunctionEpilogue).Append('}');
            return sb.ToString();
        }
        finally
        {
            _dynLocals = priorDyn;
            _functionLocalNames = priorFunctionLocals;
            if (priorFunctionReadonly is not null) _readonlyVars = priorFunctionReadonly;
            _loopLabels = outerLabels;
            _loopBarrier = outerBarrier;
            foreach (var v in added) vars.Remove(v);
        }
    }

    /// <summary>
    /// Recursively collect variable names declared with 'local' in a command tree.
    /// </summary>
    /// <summary>The source text of a static <c>eval</c> / <c>builtin eval</c> body (every argument a literal), else null.</summary>
    private static string? EvalLiteralBody(Command.Simple cmd)
    {
        int at = GetLiteralValue(cmd.Words[0]) switch { "eval" => 1, "builtin" when cmd.Words.Length > 2 && GetLiteralValue(cmd.Words[1]) == "eval" => 2, _ => -1 };
        if (at < 0) return null;
        var parts = new List<string>();
        for (int i = at; i < cmd.Words.Length; i++)
        {
            if (i == at && GetLiteralValue(cmd.Words[i]) == "--") continue;
            if (TryReconstructBashSource(cmd.Words[i]) is not { } s) return null;
            parts.Add(s);
        }
        return parts.Count == 0 ? null : string.Join(' ', parts);
    }
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
                    if (bracketIdx < 0 && pair.ArrayValue is null && _collectScalarLocals is not null
                        && IsDynamicLocalName(pair.Name))
                    {
                        _collectScalarLocals.Add(pair.Name);
                        continue;
                    }
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
            // `eval 'local x=1; .'`: a literal eval body is inlined into the function, so its `local`s are the function's.
            case Command.Simple { Words.Length: > 1 } ev when EvalLiteralBody(ev) is { } evalBody:
                try { if (BashParser.Parse(evalBody) is { } parsedEval) CollectLocalVars(parsedEval, result); }
                catch (ParseException) { }
                break;            case Command.ShFunction fn:
                CollectLocalVars(fn.Body, result);
                break;
        }
    }

    /// <summary>
    /// A subshell (also <c>$( )</c>) starts with no EXIT trap and fires the one it sets when it ends. Only a body that
    /// calls <c>trap</c> — or can call a function that does, in a script that mentions <c>trap</c> — pays for it.
    /// </summary>
    private static string WithSubshellExitTrap(string body) =>
        _trapMode || body.Contains("Invoke-BashTrap", StringComparison.Ordinal)
            ? "$global:__BashTrapEXIT = $null; try { " + body + " } finally { "
              + "$__psbash_xs = $global:LASTEXITCODE; try { if ($global:__BashTrapEXIT) { & $global:__BashTrapEXIT } } catch { }; $global:LASTEXITCODE = $__psbash_xs }"
            : body;

    private static string EmitSubshell(Command.Subshell subshell)
    {
        // `exit` inside a subshell exits only the SUBSHELL and sets $? in the
        // parent — `(exit 7); echo $?` prints 7 and keeps going. Emitted as a bare
        // PowerShell `exit`, it terminated the WHOLE shell instead: the script
        // stopped and the `echo $?` never ran. Emit the body with the subshell
        // depth raised so `exit N` becomes a SCOPED `$LASTEXITCODE = N; return`,
        // and give that `return` a script block to return from.
        _subshellDepth++;
        var outerLabels = _loopLabels; var outerBarrier = _loopBarrier;
        _loopLabels = null; _loopBarrier = true;
        string body; bool savedErrTrapSh = PsBuild.ErrTrapNeedsErrtrace; PsBuild.ErrTrapNeedsErrtrace = true;
        // `( … ) < file` gives the body its own stdin (a scope); otherwise it inherits the enclosing one.
        try { body = EmitCompoundBody(subshell.Redirects, () => EmitBody(subshell.Body)); }
        finally { _subshellDepth--; PsBuild.ErrTrapNeedsErrtrace = savedErrTrapSh; _loopLabels = outerLabels; _loopBarrier = outerBarrier; }

        // One subshell scope (PsBuild.ShellStateScope): a child PS scope plus save/restore of the
        // shell state a scope does not isolate, so nothing the body changes — `set -e`, variables,
        // `shopt`, traps, positionals — leaks into the parent. It is always a script block, which also
        // gives `exit` / an errexit failure their scoped `return` (ErrexitExitStatement). The `finally`
        // then restores BOTH halves of the working directory (PowerShell location +
        // [System.Environment]::CurrentDirectory) — see PsBuild.PopLocationRestoringProcessCwd for why
        // a bare `Pop-Location` silently made `(cd sub); cat data.txt | …` stream the wrong file. It
        // runs on the scoped-exit path (`$LASTEXITCODE = N; return`) and on any exception too.
        // A subshell starts with no EXIT trap (bash resets traps) and fires the one it sets when it ends:
        // `( trap 'echo bye' EXIT; echo body )` prints body, then bye. Only a body that calls `trap` pays for it.
        body = WithSubshellExitTrap(body);
        var sb = new StringBuilder(PsBuild.ShellStateScope(
            "Push-Location; " + body, PsBuild.PopLocationRestoringProcessCwd));

        // Partition out the input redirect: `(cmd) < file` feeds the file to the
        // subshell's stdin and wraps the WHOLE result (`Get-Content file | & { … }`),
        // so it can't go through the shared stdout/other tail — without this it fell to
        // the raw `0<file` fallback ("'<' is reserved for future use"), common as
        // `(while read l; ...) < file`. Last input redirect wins (bash). Everything
        // else (stdout file + `2>&1` etc.) uses the shared AppendRedirectTail.
        Redirect? inputRedirect = null;
        var tailRedirects = new List<Redirect>(subshell.Redirects.Length);
        var highFds = new List<Redirect>();
        foreach (var redirect in subshell.Redirects)
        {
            if (redirect.Fd >= 3)
                highFds.Add(redirect);
            else if (redirect.Fd == 0 && redirect.Op is "<" or "<<" or "<<<")
                inputRedirect = redirect;
            else
                tailRedirects.Add(redirect);
        }
        // The scope is `& { … }`, an expression that can head a pipeline, so `( … ) > file` pipes
        // straight into the redirect tail (a bare `try`/`finally` statement could not — "An empty
        // pipe element is not allowed", Go's mkerrors.sh).
        AppendRedirectTail(sb, tailRedirects);

        string result = sb.ToString();
        if (inputRedirect is { Here: { } subshellHere })
        {
            result = $"{EmitHereDocLiteral(subshellHere)} | Emit-BashLine | & {{ {PsBuild.StdinScope(result)} }}";
        }
        else if (inputRedirect is not null)
        {
            var inTarget = EmitInputRedirectTarget(inputRedirect.Target);
            // /dev/null maps to $null; `Get-Content $null` errors. Its stdin is EMPTY (EOF), so feed
            // the scope nothing explicitly (it must not see an outer pipe's or queue's data).
            result = inTarget != "$null"
                ? PsBuild.InputRedirectGuard(inTarget, $"Invoke-BashCat {inTarget} -RawStdinBytes | & {{ {PsBuild.StdinScope(result)} }}", RedirectFailExit())
                : $"@() | & {{ {PsBuild.StdinScope(result)} }}";
        }

        return WithScopedHighFds(result, highFds);
    }

    /// <summary>
    /// <c>( ... ) 7&gt;&amp;1</c>, <c>{ ...; } 3&gt;log</c>: a numbered descriptor opened for one compound command only. Left in
    /// the redirect tail it came out raw (<c>} 7&gt;&amp;1</c>), and PowerShell read the <c>&amp;</c> as its background
    /// operator: the command ran as a job, whose <c>$using:</c> rewrite broke every <c>$global:</c> assignment.
    /// Opened before the command and closed after it, as <c>exec N...</c> would (an earlier <c>exec</c> of the same
    /// descriptor is not restored).
    /// </summary>
    private static string WithScopedHighFds(string command, List<Redirect> highFds)
    {
        if (highFds.Count == 0) return command;
        var open = string.Join("; ", highFds.Select(r => $"Invoke-BashExecFd {r.Fd} {PsBuild.SingleQuote(r.Op)} {EmitArgWord(r.Target)}"));
        var close = string.Join("; ", highFds.Select(r => r.Fd).Distinct().Select(fd => $"Invoke-BashExecFd {fd} '>&' '-'"));
        return "& { " + open + "; try { " + command + " } finally { $__psbash_fdRc = $global:LASTEXITCODE; " + close
             + "; $global:LASTEXITCODE = $__psbash_fdRc } }";
    }

    private static string EmitBraceGroup(Command.BraceGroup braceGroup)
    {
        return ApplyCompoundRedirects(EmitBody(braceGroup.Body), braceGroup.Redirects);
    }

    /// <summary>
    /// Feed <paramref name="simple"/> from the enclosing compound's shared stdin when it is inside a
    /// stdin scope and is a command that reads stdin (<see cref="StdinReaders"/>): the emitted text
    /// becomes <c>feed | cmd</c>. A command that carries its own input (<c>&lt; file</c>, heredoc,
    /// env-pair prefix, or any statement-list emission) is left alone — its text is not a single
    /// pipeable element. A non-ps-bash program gets the shared stdin as its real process stdin
    /// (<see cref="PsBuild.NativeStdinScope"/>) — only when it resolves to an application, and what it
    /// does not read is put back — so a user function or a program that ignores stdin is never starved
    /// of what it would not have read, nor blocked on a stdin that never ends.
    /// </summary>
    private static string EmitSimpleFed(Command.Simple simple)
    {
        var text = EmitSimple(simple);
        // A run-time command name (`$c`, `"$tool"`) may read stdin. Whether it does is known only once it resolves:
        // `-Fed` has the runtime feed the scope's queue to a reader and leave it for anything else. Piped
        // unconditionally, `printf`/`test` never ran on an empty queue and failed to bind a queued line.
        const string dynPrefix = "& (Resolve-BashCommand ";
        if (_inStdinScope && simple.EnvPairs.IsEmpty && simple.HereDocs.IsDefaultOrEmpty && !HasInputRedirect(simple.Redirects)
            && text.StartsWith(dynPrefix, StringComparison.Ordinal) && !PsBuild.IsStatementList(text))
            return dynPrefix + "-Fed " + text.Substring(dynPrefix.Length);
        if (!_inStdinScope
            || !simple.EnvPairs.IsEmpty
            || !simple.HereDocs.IsDefaultOrEmpty
            || HasInputRedirect(simple.Redirects)
            || simple.Words.IsDefaultOrEmpty
            || GetLiteralValue(simple.Words[0]) is not { } name
            || PsBuild.IsStatementList(text)
            || text.StartsWith("$(", StringComparison.Ordinal)
            || text.StartsWith("& {", StringComparison.Ordinal))
            return text;

        var args = new List<string?>(simple.Words.Length - 1);
        for (int i = 1; i < simple.Words.Length; i++)
            args.Add(GetLiteralValue(simple.Words[i]));
        if (!StdinReaders.ShouldFeed(name, args))
            return text;

        if (StdinReaders.Classify(name) != StdinReaders.Kind.Native)
            return PsBuild.StdinFeed + " | " + text;

        // Not `feed | prog`: probing the shared stdin before the program starts BLOCKED until a record
        // arrived or the stdin ended, so under a launcher whose stdin is an open, silent pipe (an agent's
        // shell tool) every native command hung — `git --version` included. The program gets the stdin as
        // its real process stdin instead, and the command ends when the program does.
        return PsBuild.NativeStdinScope(name, text);
    }

    private static bool IsNohup(Command.Simple s) =>
        !s.Words.IsDefaultOrEmpty && s.EnvPairs.IsEmpty && GetLiteralValue(s.Words[0]) == "nohup";

    private static CompoundWord LiteralWord(string text) =>
        new(ImmutableArray.Create<WordPart>(new WordPart.SingleQuoted(text)));

    /// <summary>
    /// A redirect on <c>nohup CMD</c> is meant for CMD, but CMD runs detached in another process, where a host-side
    /// redirect never reaches it. The stdout/stderr file redirects move into leading private flags of
    /// <c>Invoke-BashNohup</c> (<c>--psbash-out FILE [--psbash-append] [--psbash-err FILE|&amp;1]</c>), which opens them for the child.
    /// </summary>
    private static Command.Simple RewriteNohup(Command.Simple s)
    {
        var keep = ImmutableArray.CreateBuilder<Redirect>();
        var lead = new List<CompoundWord>();
        CompoundWord? outTarget = null; bool append = false; CompoundWord? errTarget = null; bool errToOut = false;
        foreach (var r in s.Redirects)
        {
            if (r.Op is ">" or ">>" or ">|" && r.Fd == 1) { outTarget = r.Target; append = r.Op == ">>"; }
            else if (r.Op is "&>" or "&>>") { outTarget = r.Target; append = r.Op == "&>>"; errToOut = true; }
            else if (r.Op == ">&" && r.Fd == 2 && GetLiteralValue(r.Target) == "1") errToOut = true;
            else if (r.Op is ">" or ">>" && r.Fd == 2) errTarget = r.Target;
            else keep.Add(r);
        }
        if (outTarget is not null)
        {
            lead.Add(LiteralWord("--psbash-out")); lead.Add(outTarget);
            if (append) lead.Add(LiteralWord("--psbash-append"));
            if (errToOut) { lead.Add(LiteralWord("--psbash-err")); lead.Add(LiteralWord("&1")); }
            else if (errTarget is not null) { lead.Add(LiteralWord("--psbash-err")); lead.Add(errTarget); }
        }
        else keep.AddRange(s.Redirects.Where(r => r.Fd == 1 || r.Fd == 2 || r.Op is "&>" or "&>>")); // stderr-only redirects keep the old behaviour
        if (outTarget is null) return s;
        var words = ImmutableArray.CreateBuilder<CompoundWord>();
        words.Add(s.Words[0]); words.AddRange(lead); for (int i = 1; i < s.Words.Length; i++) words.Add(s.Words[i]);
        return s with { Words = words.ToImmutable(), Redirects = keep.ToImmutable() };
    }
    private static string EmitBackground(Command.Background bg)
    {
        string inner = Emit(bg.Inner);
        // A job that can change shell state (variables, cwd, options, functions) must not share this
        // process's environment and cwd: it runs in a host of its own (-Isolated). A job that only runs
        // programs keeps the cheap in-process path, whose text is unchanged.
        // A for-loop variable is a PowerShell variable the job (another runspace or process) cannot see, and the
        // loop moves on before the job reads it: hand over its value at launch.
        string vars = "";
        if (_loopVars is { Count: > 0 })
        {
            var used = _loopVars.Where(v => inner.Contains("$" + v, StringComparison.Ordinal)).OrderBy(v => v, StringComparer.Ordinal).ToList();
            if (used.Count > 0)
                vars = " -Vars @{ " + string.Join("; ", used.Select(v => $"{PsBuild.SingleQuote(v)} = ${v}")) + " }";
        }
        return MayMutateShellState(bg.Inner)
            ? $"Invoke-BashBackground -Isolated{vars} {{ {inner} }}"
            : $"Invoke-BashBackground{vars} {{ {inner} }}";
    }

    private static readonly HashSet<string> _stateBuiltins = new(StringComparer.Ordinal)
    {
        "cd", "pushd", "popd", "export", "unset", "declare", "typeset", "local", "readonly", "set", "shopt",
        "read", "source", ".", "eval", "exec", "alias", "unalias", "trap", "umask", "mapfile", "readarray",
        "getopts", "let", "shift", "enable", "hash", "builtin", "command", "ulimit", "wait", "fg", "bg",
    };

    // Function names defined so far in the unit being emitted: calling one can do anything.
    [ThreadStatic] private static HashSet<string>? _unitFunctions;

    /// <summary>True when running <paramref name="cmd"/> could change variables, cwd, options or functions of the shell.</summary>
    private static bool MayMutateShellState(Command cmd) => cmd switch
    {
        Command.Simple s => !s.EnvPairs.IsEmpty
            || s.Words.IsDefaultOrEmpty
            || GetLiteralValue(s.Words[0]) is not { } name
            || _stateBuiltins.Contains(name)
            || (_unitFunctions?.Contains(name) ?? false),
        Command.Pipeline p => p.Commands.Any(MayMutateShellState),
        Command.AndOrList a => a.Commands.Any(MayMutateShellState),
        Command.CommandList l => l.Commands.Any(MayMutateShellState),
        Command.If i => i.Arms.Any(arm => MayMutateShellState(arm.Cond) || MayMutateShellState(arm.Body))
            || (i.ElseBody is not null && MayMutateShellState(i.ElseBody)),
        Command.While w => MayMutateShellState(w.Cond) || MayMutateShellState(w.Body),
        Command.Case c => c.Arms.Any(arm => MayMutateShellState(arm.Body)),
        Command.Subshell sub => MayMutateShellState(sub.Body),
        Command.BraceGroup bg => MayMutateShellState(bg.Body),
        Command.BoolExpr => false,
        // Assignments, loops (they set their variable), (( )), functions, select, anything new: assume it can.
        _ => true,
    };

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
        => PsBuild.SilentExitFromBool($"@({EmitArithmeticInvocation(arith.Expr)}) -ne 0");

    /// <summary>
    /// Boolean expression for <c>(( expr ))</c> used as an <c>if</c>/<c>while</c>
    /// condition: true iff the evaluated result is non-zero.
    /// </summary>
    private static string EmitArithConditionExpr(ArithmeticSyntax syntax)
        => $"(@({EmitArithmeticInvocation(syntax)}) -ne 0)"; // a failed evaluation yields no value: false, status 1

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
            if (word is "true" or ":") return "$true";
            if (word is "false") return "$false";
        }

        return EmitCondition(cond);
    }

    private static bool IsWhileRead(Command cond, out List<string> varNames, out string? ifs)
    {
        varNames = new List<string>();
        ifs = null;
        if (cond is not Command.Simple simple)
            return false;
        if (simple.Words.Length < 2)
            return false;
        if (GetLiteralValue(simple.Words[0]) != "read")
            return false;
        // The `while IFS=: read ...` idiom carries its field separator as an
        // EnvPair prefix on the read command, not as a word. Capture it so the
        // loop's field splitter uses the caller's IFS instead of whitespace.
        foreach (var pair in simple.EnvPairs)
        {
            if (pair.Name == "IFS")
                ifs = pair.Value is null ? "" : GetLiteralValue(pair.Value) ?? (pair.Value.Parts.IsDefaultOrEmpty ? "" : null);   // `IFS= read`: an empty IFS
        }
        // Accept: read VAR  or  read -r VAR  or  read -r a b c.
        // EVERY non-flag word is a target variable — `read a b` splits the line
        // across both. Keeping only the last one (the old behavior) left `$a`
        // unset and dumped the whole line into `$b`.
        for (int i = 1; i < simple.Words.Length; i++)
        {
            var val = GetLiteralValue(simple.Words[i]);
            // `read -d DELIM` reads records, not lines: the generic loop (Invoke-BashRead) handles it.
            if (val is not null && val.Length > 1 && val[0] == '-' && val[1] != '-' && val.Contains('d'))
                return false;
            if (val is not null && !val.StartsWith('-'))
                varNames.Add(val);
        }
        return varNames.Count > 0;
    }

    /// <summary>The records of a read loop's own input redirect, as an array expression: the file's
    /// lines (`/dev/null` = none) or the here-string / heredoc body — the same feeds
    /// <see cref="ApplyCompoundRedirects"/> pipes in.</summary>
    /// <summary>
    /// Stage that turns each input record into its plain text BEFORE the loop collects them with <c>@(.)</c>. A record is
    /// a PSObject carrying BashText/NoTrailingNewline note properties (several KB each on a PowerShell object); holding
    /// 100k of them for a <c>while read</c> over a file cost ~520 MB for 5 MB of text. The loop reads each record as
    /// <c>BashText</c> else its string, so a plain string is the same input at ~100 bytes per line.
    /// </summary>
    private const string RecordText =
        "ForEach-Object { if ($null -ne $_ -and $_.PSObject.Properties['BashText']) { \"$($_.BashText)\" } else { \"$_\" } }";
    private static string ReadLoopSource(Redirect input)
    {
        if (input.Here is { } here)
            return $"@({EmitHereDocLiteral(here)} | Emit-BashLine | {RecordText})";
        var target = EmitInputRedirectTarget(input.Target);
        return target == "$null" ? "@()" : $"@(Invoke-BashCat {target} -RawStdinBytes | {RecordText})";
    }

    /// <param name="varNames">The <c>read</c> variables, bound per line as loop variables.</param>
    /// <param name="ifs">The loop's <c>IFS=</c> prefix value, or null for the default field split.</param>
    /// <param name="body">The loop body.</param>
    /// <param name="sourceExpr">The records to iterate when the loop has its own input redirect
    /// (<see cref="ReadLoopSource"/>); null = the pipeline (<c>$input</c>) or the shared stdin queue.</param>
    private static string EmitWhileRead(List<string> varNames, string? ifs, Command body, string? sourceExpr = null)
    {
        // The `read` variables are the loop bindings for this construct: register
        // them as loop vars so the body emits them bare ($line, not $env:line) and
        // RC-7 word-splitting does not splat them (a `read` var holds a single
        // bound field, exactly like a for-loop variable).
        var vars = _loopVars ??= new HashSet<string>();
        var added = new List<string>(varNames.Count);
        foreach (var name in varNames)
        {
            if (vars.Add(name)) added.Add(name);
        }

        // A real (labelled) loop, not a `ForEach-Object` pipeline: inside a ForEach-Object script
        // block `break` does not stop the pipeline — it unwinds to the nearest ENCLOSING loop, and
        // with none it ended the whole script (`while read …; do break; done < f; echo after`
        // printed nothing after the loop); `continue` acted like break and `return` only ended the
        // current line. Two PS loops — records, then the lines of a record (one record can carry
        // several) — so `break` names the outer label and `continue` the inner one.
        int depth = _loopDepth++;
        string recLabel = "__psbash_l" + depth, lineLabel = "__psbash_lc" + depth;
        (_loopLabels ??= new()).Add((recLabel, lineLabel));
        string bodyText;
        try
        {
            bodyText = Emit(body);
        }
        finally
        {
            PopLoopLabel();
            _loopDepth--;
            foreach (var name in added)
                vars.Remove(name);
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
        // A lone variable still has the IFS whitespace (space, tab) trimmed from both ends; `IFS= read` keeps the line.
        var trimChars = new List<string>();
        if (ifs is null || ifs.Contains(' ')) trimChars.Add("32");
        if (ifs is null || ifs.Contains('\t')) trimChars.Add("9");
        string bind = varNames.Count == 1
            ? trimChars.Count == 0
                ? $"${{{varNames[0]}}} = $_; "
                : $"${{{varNames[0]}}} = $_.Trim([char[]]@({string.Join(",", trimChars)})); "
            : BuildReadFieldBindings(varNames, ifs);

        // The lines of one record: its BashText (null-safe), the trailing newline dropped, split.
        string rec = "$__psbash_rec" + depth;
        string lines = $"@(($(if ($null -ne {rec} -and {rec}.PSObject.Properties['BashText']) {{ {rec}.BashText }} else {{ \"{rec}\" }}) -replace \"`n$\",\"\") -split \"`n\")";
        string inner = $":{lineLabel} foreach ($_ in {lines}) {{ {bind}{bodyText} }}";

        // Inheriting a compound's stdin, the lines come from the shared queue (a `read` or `cat`
        // before the loop already advanced it), not from `$input` — dequeued one record per pass,
        // so a `read` in the body still takes the NEXT record (a `foreach` over the feed would
        // drain the queue up front). `$input` is an enumerator: `foreach` over it streams.
        if (sourceExpr is not null)
            return $":{recLabel} foreach ({rec} in {sourceExpr}) {{ {inner} }}";
        return _inStdinScope
            ? $":{recLabel} while ($global:__BashStdIn -and $global:__BashStdIn.Count -gt 0) {{ {rec} = $global:__BashStdIn.Dequeue(); {inner} }}"
            : $":{recLabel} foreach ({rec} in $input) {{ {inner} }}";
    }

    /// <summary>
    /// Bind one input line across the several variables of `read a b c`. Bash splits
    /// the line on IFS after stripping leading/trailing whitespace, gives one field to
    /// each variable in turn, and the LAST variable absorbs everything remaining
    /// (separators included). Variables past the end of the field list get "".
    /// `-split &lt;ifs&gt;, N` reproduces exactly that remainder rule with its limit
    /// argument; the pre-trim is what stops a leading space from producing a spurious
    /// empty first field.
    ///
    /// <paramref name="ifs"/> is the literal separator from the `IFS=` prefix on the
    /// read command (`null` when absent → default whitespace splitting). A
    /// non-whitespace IFS is compiled into the same delimiter regex the runtime read
    /// cmdlet builds (<c>InvokeBashReadCommand.SplitByIfsForRead</c>), so the fast
    /// path and the `read` builtin cannot drift.
    /// </summary>
    private static string BuildReadFieldBindings(List<string> varNames, string? ifs)
    {
        var sb = new StringBuilder();
        sb.Append("$__psbash_readline = ($_ -replace '^\\s+|\\s+$',''); ");
        if (string.IsNullOrEmpty(ifs) || IsAllWhitespaceIfs(ifs))
        {
            // Default (or all-whitespace) IFS: fold runs of whitespace, trim ends.
            // `@(...)`, never `$(...)`: a subexpression collapses a ONE-element result
            // to a scalar string, and `$str[0]` is then its first CHARACTER — a line
            // with a single field bound `a` to "s" instead of "solo". The array
            // subexpression also keeps the empty branch an empty array rather than $null.
            sb.Append("$__psbash_readf = @(if ($__psbash_readline -eq '') { @() } else { ");
            sb.Append($"$__psbash_readline -split '\\s+', {varNames.Count} }}); ");
        }
        else
        {
            // Custom (non-whitespace) IFS: build the same delimiter regex the
            // runtime read cmdlet uses (`SplitByIfsForRead`) and split with a count
            // limit so the LAST variable keeps every remaining character,
            // separators included — bash `IFS=, read a b` on `1,2,3` gives b="2,3".
            // A whitespace IFS char folds runs; a non-whitespace IFS char is its own
            // delimiter (empty fields preserved between adjacent ones).
            sb.Append("$__psbash_readf = @(if ($__psbash_readline -eq '') { @() } else { ");
            sb.Append($"$__psbash_readline -split {PsBuild.SingleQuote(BuildIfsReadDelimiterRegex(ifs))}, {varNames.Count} }}); ");
        }
        for (int i = 0; i < varNames.Count; i++)
        {
            sb.Append($"${{{varNames[i]}}} = $(if ($__psbash_readf.Count -gt {i}) ");
            sb.Append($"{{ $__psbash_readf[{i}] }} else {{ '' }}); ");
        }
        return sb.ToString();
    }

    /// <summary>
    /// The PowerShell regex that splits a line on a custom (non-whitespace) IFS,
    /// mirroring the runtime <c>InvokeBashReadCommand.SplitByIfsForRead</c>
    /// delimiter construction. Whitespace IFS chars come through as a character
    /// class with run-folding (<c>[ \t]+</c>); each non-whitespace IFS char is a
    /// single-character delimiter that may be padded by whitespace IFS.
    /// </summary>
    private static string BuildIfsReadDelimiterRegex(string ifs)
    {
        var ws = new StringBuilder();
        var nonWs = new StringBuilder();
        foreach (var c in ifs)
        {
            if (char.IsWhiteSpace(c)) ws.Append(System.Text.RegularExpressions.Regex.Escape(c.ToString()));
            else nonWs.Append(System.Text.RegularExpressions.Regex.Escape(c.ToString()));
        }
        string? wsCls = ws.Length > 0 ? "[" + ws + "]" : null;
        string nonWsCls = "[" + nonWs + "]";
        if (wsCls is null)
            return nonWsCls;
        if (nonWs.Length == 0)
            return wsCls + "+";
        return $"{wsCls}*{nonWsCls}{wsCls}*|{wsCls}+";
    }

    /// <summary>True when every char of <paramref name="ifs"/> is whitespace.</summary>
    private static bool IsAllWhitespaceIfs(string ifs)
    {
        foreach (var c in ifs)
            if (!char.IsWhiteSpace(c)) return false;
        return ifs.Length > 0;
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
        return "$env:" + (EnvShadowName(name) ?? name);
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
            if (word is "true" or ":") return "$true";
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
            if (word is "true" or ":") return "$true";
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
        string inner = EmitBoolExprInner(expr);
        // A bad integer operand (`[ a -eq 1 ]`) is status 2, not just false.
        if (inner.Contains("Invoke-BashTestInt") || inner.Contains("__BashTestBad"))
            return "$($global:__BashTestBad = $false; $__psbTestR = (" + inner + "); $global:LASTEXITCODE = $(if ($global:__BashTestBad) { 2 } elseif ($__psbTestR) { 0 } else { 1 }))";
        return PsBuild.SilentExitFromBool("(" + inner + ")");
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

        // && binds tighter than || in [[ ]], but PowerShell's -and / -or have EQUAL precedence:
        // group each run of && operands in its own parens, then join the groups with -or.
        var groups = new List<string>();
        var group = new StringBuilder();
        bool mixed = false;
        for (int i = 0; i < segments.Count; i++)
        {
            // Wrap each segment in parens so that -and / -or are not parsed as
            // additional flags to the preceding cmdlet (e.g. Test-Path ... -and ...).
            if (group.Length > 0) group.Append(" -and ");
            group.Append('(').Append(TranslateTestCondition(segments[i].Words, extended: true)).Append(')');
            if (segments[i].TrailingOp != "-and")
            {
                groups.Add(group.ToString());
                mixed |= group.ToString().Contains(" -and ");
                group.Clear();
            }
        }
        // Parenthesize an && run only when it sits beside a ||.
        if (mixed && groups.Count > 1)
            for (int g = 0; g < groups.Count; g++)
                if (groups[g].Contains(" -and ")) groups[g] = "(" + groups[g] + ")";
        return string.Join(" -or ", groups);
    }

    // In `[ ]` the grouping parens arrive escaped or quoted (`\(`, '('): bash groups on the word text.
    private static ImmutableArray<CompoundWord> WithLiteralParens(ImmutableArray<CompoundWord> words)
    {
        var b = ImmutableArray.CreateBuilder<CompoundWord>(words.Length);
        foreach (var w in words)
        {
            string? text = w.Parts is [WordPart.EscapedLiteral e] ? e.Value
                : w.Parts is [WordPart.SingleQuoted q] ? q.Value : null;
            b.Add(text is "(" or ")" ? new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal(text))) : w);
        }
        return b.MoveToImmutable();
    }

    private const int MaxStaticTestGroupDepth = 32;
    [ThreadStatic] private static int t_testGroupDepth;

    // Groups unwrapped by WithLiteralParens carry bare `(`/`)` literals; handed to the runtime evaluator they
    // must reach it as the words `(`/`)`, not as PowerShell grouping.
    private static ImmutableArray<CompoundWord> QuoteBareTestParens(ImmutableArray<CompoundWord> words)
    {
        var b = ImmutableArray.CreateBuilder<CompoundWord>(words.Length);
        foreach (var w in words)
            b.Add(w.Parts is [WordPart.Literal { Value: "(" or ")" } l]
                ? new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.SingleQuoted(l.Value))) : w);
        return b.MoveToImmutable();
    }

    private static string TranslateTestCondition(ImmutableArray<CompoundWord> words, bool extended)
    {
        // Grouping: `[[ ( -f $f && ! -L $f ) ]]`. Unwrap one enclosing pair and
        // re-enter the full extended-test path so the &&/|| inside the group are
        // translated, then re-parenthesize for PowerShell precedence.
        if (extended && TryUnwrapTestGroup(words, out var grouped))
            return $"({EmitExtendedTest(grouped)})";

        // `[ \( A \) ]`: bash's 3-arg form `( X )` is just X (a lone word is then a non-empty test), and the
        // 4-arg form groups a unary test. Arity 3 is checked first so `[ ( = ) ]`-style operators still apply.
        // Real scripts group a level or two. Past MaxStaticTestGroupDepth the group is left to the runtime
        // `test` evaluator (which bounds its own nesting) so a hostile `[ \( \( … \) \) ]` cannot overflow
        // the emitter or hand PowerShell a thousand-deep paren tower.
        if (!extended && t_testGroupDepth < MaxStaticTestGroupDepth
            && TryUnwrapTestGroup(WithLiteralParens(words), out var bracketGrouped)
            && !(words.Length == 3 && GetLiteralValue(words[1]) is { } gm && (IsBinaryTestOp(gm) || gm is "-a" or "-o")))
        {
            t_testGroupDepth++;
            try { return $"({TranslateTestCondition(bracketGrouped, extended: false)})"; }
            finally { t_testGroupDepth--; }
        }

        // `[ a $op a ]`: the operator is only known at run time (and the expansion may split into several
        // words), so hand the IFS-split word list to the runtime `test` evaluator.
        if (!extended && words.Length == 3 && GetLiteralValue(words[1]) is null && IsUnquotedSplitWord(words[1])
            && !words.Any(w => w.Parts.Any(p => p is WordPart.GlobPart)))
        {
            string a0 = IsUnquotedSplitWord(words[0]) ? EmitUnquotedSplitWordArray(words[0]) : EmitTestOperand(words[0]);
            string a2 = IsUnquotedSplitWord(words[2]) ? EmitUnquotedSplitWordArray(words[2]) : EmitTestOperand(words[2]);
            return $"$(& {{ $__psbT = @({a0}; {EmitUnquotedSplitWordArray(words[1])}; {a2}); Invoke-BashTest @__psbT; if ($global:LASTEXITCODE -eq 2) {{ $global:__BashTestBad = $true }}; $global:LASTEXITCODE -eq 0 }})";
        }

        // `[ A op B ]`: bash picks the arity-3 shape from the OPERATOR in the middle, before it
        // looks at A — so `[ -f = -f ]` compares two strings, `[ ! = ! ]` is true, and
        // `[ -z -a -z ]` is the AND of two non-empty strings. None of the word-0 heuristics
        // below (negation, unary file test) may fire for such a list.
        bool arity3 = !extended && words.Length == 3 && GetLiteralValue(words[1]) is { } mid
            && (IsBinaryTestOp(mid) || mid == "-a" || mid == "-o");
        if (arity3 && GetLiteralValue(words[1]) is "-a" or "-o")
        {
            string l = $"(-not [string]::IsNullOrEmpty({EmitTestOperand(words[0])}))";
            string r = $"(-not [string]::IsNullOrEmpty({EmitTestOperand(words[2])}))";
            return $"{l} {(GetLiteralValue(words[1]) == "-a" ? "-and" : "-or")} {r}";
        }

        // Handle leading ! negation: [ ! -f x ] → !(Test-Path ...)
        if (!arity3 && words.Length >= 1 && GetLiteralValue(words[0]) == "!" && (extended || words.Length <= 3 || TestCombinatorIndices(words).Count == 0))
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

        if (!arity3 && words.Length >= 2)
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
                // A non-literal operand (`[ -e "$d/f" ]`, d=/tmp) is only known at run time: map it
                // through the runtime path policy, as the cmdlets do. A literal was already mapped
                // here (TransformWordPath), so its emission stays unchanged.
                // `path` is ARGUMENT-mode text (`$env:d/$env:f`), so it rides as a script-block
                // argument — spliced into an expression its `/` would divide.
                if (!TryGetPureLiteralText(words[1].Parts, out var fileTestLiteral) || fileTestLiteral == "/tmp")
                    path = PsBuild.RuntimeWindowsPath(path);
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
                string patExpr = IsSoleExpansionWord(words[2])
                    ? EmitWord(words[2])
                    : TryBuildRegexPattern(words[2], out var builtPattern)
                        ? builtPattern
                        : $"'{SqEsc(StripQuotes(EmitWord(words[2])))}'";
                // =~ is case-sensitive (PowerShell's -match is not) unless `shopt -s nocasematch`.
                string matchExpr = $"$(if ([PsBash.Cmdlets.BashPatternMatch]::NoCase) {{ {lhs} -match {patExpr} }} else {{ {lhs} -cmatch {patExpr} }})";
                // Bash's `=~` also fills BASH_REMATCH with the whole match and each
                // capture group. `-match` fills the automatic $Matches hashtable, so
                // copy it across ($Matches[0] is the whole match, same as bash).
                // The subexpression keeps the result ONE expression usable in an
                // `if ( ( … ) )` condition while the assignment is a side effect.
                // A malformed regex is status 2 (the standalone-statement wrapper turns __BashTestBad into it).
                return "$( try { $__psbash_m = " + matchExpr + " } catch [System.ArgumentException] { $__psbash_m = $false; $global:__BashTestBad = $true; Write-Error 'syntax error in regular expression' -ErrorAction Continue }" +
                       "; if ($__psbash_m) { $global:BASH_REMATCH = [PsBash.Cmdlets.BashPatternMatch]::Rematch($Matches, " + patExpr + ") } " +
                       "else { $global:BASH_REMATCH = $null }; $__psbash_m )";
            }

            // `==` / `=` / `!=`: bash PATTERN-matches the right-hand word, case-sensitively unless
            // `shopt -s nocasematch` (PowerShell's -eq / -like are case-insensitive: `[[ abc == A* ]]`
            // and `[[ abc == ABC ]]` were both true). `!=` is the exact mirror (oracle:
            // `[[ https://a != "http"* ]]` is FALSE).
            if (op is "==" or "=" or "!=")
            {
                bool negate = op == "!=";
                // `[ a = b ]` is plain string equality — no pattern, and nocasematch does not
                // apply (`[ abc = a* ]` is false; `[ abc = ABC ]` is false). It was -like / -eq.
                if (!extended)
                {
                    // An empty `$( )` yields nothing and `[string]` of that is not '', so `[ "$(true)" = "" ]` was false: join the lines.
                    string left = lhs.StartsWith("$(", StringComparison.Ordinal) ? $"(@({lhs}) -join \"`n\")" : lhs;
                    return $"{left} {(negate ? "-cne" : "-ceq")} {EmitTestOperand(words[2])}";
                }
                // A static RHS — literal, quoted segments (`"http"*` is the pattern http* with the
                // quoted chars literal), glob, extglob — is one BashPattern regex.
                if (TryGetStaticPatternText(words[2], out var patText))
                {
                    string test = PsBuild.PatternTest(lhs, BashPattern.ToAnchoredRegex(patText));
                    return negate ? $"(-not {test})" : test;
                }
                // A wholly double-quoted run-time RHS ("$B") is literal text: plain string equality.
                if (words[2].Parts is [WordPart.DoubleQuoted])
                {
                    string eq = PsBuild.StringEqualsTest(lhs, EmitTestOperand(words[2]));
                    return negate ? $"(-not {eq})" : eq;
                }
                // Any other run-time RHS is a pattern assembled at run time: an UNQUOTED expansion's
                // value is pattern text (oracle: B='a*'; [[ abc == $B ]] is true), quoted parts are
                // escaped. It compared literally (StringEquals), or through case-insensitive -like.
                string dyn = PsBuild.DynamicPatternTest(lhs, EmitGlobPatternExpr(words[2], mapPaths: false));
                return negate ? $"(-not {dyn})" : dyn;
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
            {
                // Inside [[ ]] each operand is an ARITHMETIC expression (`[[ a -eq a ]]` reads
                // variables a, `017` is octal, an unset name is 0); `[ ]` takes plain integers.
                if (extended)
                {
                    // A bare integer literal would lose its leading zero (`017` is octal 15).
                    static string ArithOperand(string e) =>
                        e.Length > 0 && (char.IsDigit(e[0]) || e[0] == '-' || e[0] == '+') && !e.Contains('$')
                            ? "'" + e + "'" : e;
                    return $"[long](Invoke-BashArith ([string]({ArithOperand(lhs)}))) {op} [long](Invoke-BashArith ([string]({ArithOperand(EmitTestOperand(words[2]))})))";
                }
                return $"(Invoke-BashTestInt ({lhs})) {op} (Invoke-BashTestInt -Second ({EmitTestOperand(words[2])}))";
            }

            // File-comparison operators: mtime ordering (-nt/-ot) and same-file
            // (-ef). Unimplemented, these fell through to the bare-operand join
            // and emitted the unparseable `$env:a -ef $env:b`.
            if (op is "-nt" or "-ot" or "-ef")
                return PsBuild.FileComparisonTest(lhs, EmitTestOperand(words[2]), op);

            var psOp = op switch
            {
                "!=" => "-ne",
                "<" => "-lt",
                ">" => "-gt",
                _ => op,
            };
            return $"{lhs} {psOp} {EmitTestOperand(words[2])}";
        }

        // No operands at all — bash's `[ ]` is false. This also catches a clause the
        // splitter left empty, e.g. the unsupported `[[ ( … ) ]]` grouping form; the
        // old loop returned "" for it, which emitted the unparseable `if (())`.
        if (words.Length == 0)
            return "$false";

        // Fallback. `[ str ]` (one operand) is the non-empty test: emit it quoted so
        // a bare literal is a PowerShell string, not a command invocation.
        if (words.Length == 1)
            return EmitTestOperand(words[0]);

        // More than one operand with no recognized operator means an operator this
        // emitter does not implement — `[ -o PROMPT_SUBST ]` (shell-option test) is
        // the real-world case. Joining the operands with spaces emitted adjacent
        // values (`'-o' 'PROMPT_SUBST'`), which is NEVER valid PowerShell, so one
        // such line broke the parse of the entire file. Degrade to a false result
        // plus a diagnostic on stderr: wrong-but-visible beats unparseable.
        // `[ ]` (not `[[ ]]`) runs bash's own argument-count grammar in the `test` cmdlet, which
        // knows `( )`, `-o NAME`, `-v`, `-R`, usage errors (exit 2) and every shape above
        // verbatim. `[[ ]]` keeps the diagnostic: the cmdlet has no `[[` grammar.
        // `[[ -v name ]]` / `[[ -R name ]]` (is-set / is-nameref) are the same unary operators; the cmdlet owns them.
        // The operand of -v is a variable NAME (`cfg[host]`, `a[1]`), never a glob: pass it as a literal.
        if (words.Length == 2 && GetLiteralValue(words[0]) == "-v" && TryGetVarOperandName(words[1]) is { } varOperand)
            return PsBuild.ExitCodeTest($"Invoke-BashTest '-v' {PsBuild.SingleQuote(varOperand)}", negate: false);
        if (!extended || (words.Length == 2 && GetLiteralValue(words[0]) is "-v" or "-R"))
            return PsBuild.ExitCodeTestFlaggingBad(EmitPassthrough("Invoke-BashTest", QuoteBareTestParens(words)));

        var opName = GetLiteralValue(words[0]) ?? "";
        var message = PsBuild.SingleQuote(
            $"ps-bash: [: {opName}: unsupported test operator");
        return PsBuild.Subexpr($"Write-BashHostStderr {message}; $false");
    }

    private static bool IsBinaryTestOp(string op) => op is "=" or "==" or "!=" or "-eq" or "-ne" or "-lt"
        or "-le" or "-gt" or "-ge" or "-nt" or "-ot" or "-ef";

    // Split a `[ ]` clause list on a top-level POSIX combinator (`-a` / `-o`) and
    // recurse on each clause, joining with the PowerShell logical op (-and / -or).
    // Only separators at index 1..len-2 count (operands on both sides); an operator
    // at the edge is a unary file test, not a combinator. Returns false if the token
    // does not appear in a separator position, so the caller falls through to the
    // single-clause translation.
    // Indices of the `-a` / `-o` words that really JOIN clauses. A clause is parsed the way bash's test does it
    // (binary operator in the middle wins, then a unary operator, else a lone word), so an `-o` that is an OPERAND
    // (`test 1 -ne 0 -a -o != --`) or the operand of a comparison is not a combinator.
    private static HashSet<int> TestCombinatorIndices(ImmutableArray<CompoundWord> words)
    {
        var found = new HashSet<int>();
        words = WithLiteralParens(words);
        int n = words.Length, p = 0;
        while (p < n)
        {
            while (p < n && GetLiteralValue(words[p]) == "!" && !(p + 2 < n && IsBinaryTestOp(GetLiteralValue(words[p + 1]) ?? "")))
                p++;
            if (p >= n) break;
            string? w0 = GetLiteralValue(words[p]);
            if (w0 == "(")
            {
                int depth = 0;
                for (; p < n; p++)
                {
                    string? t = GetLiteralValue(words[p]);
                    if (t == "(") depth++;
                    else if (t == ")" && --depth == 0) { p++; break; }
                }
            }
            else if (p + 2 < n && IsBinaryTestOp(GetLiteralValue(words[p + 1]) ?? "")) p += 3;
            else if (w0 is { Length: 2 } && w0[0] == '-' && p + 1 < n) p += 2;
            else p += 1;
            if (p < n && GetLiteralValue(words[p]) is "-a" or "-o") { found.Add(p); p++; }
            else break;
        }
        return found;
    }

    private static bool TrySplitTestCombinator(
        ImmutableArray<CompoundWord> words, string token, string psOp, out string result)
    {
        result = string.Empty;
        var clauses = new List<ImmutableArray<CompoundWord>>();
        int start = 0;
        var combinators = TestCombinatorIndices(words);
        for (int i = 1; i < words.Length - 1; i++)
        {
            if (GetLiteralValue(words[i]) == token && combinators.Contains(i))
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
    {
        var emitted = EmitTestArg(word);
        // A literal word that starts with `$` (`[[ $ =~ "$" ]]`, `[[ "$" == $ ]]`) is text, not a variable reference:
        // left bare, PowerShell read `$` as an unknown token ("$: command not found", or a parse error before `&&`).
        return emitted.StartsWith('$') && ExtractSingleQuotedOrLiteralValue(word) is { } literal
            ? PsBuild.SingleQuote(literal)
            : QuoteIfBareLiteral(emitted);
    }

    /// <summary>
    /// True when the word is exactly ONE parameter/command expansion and nothing else
    /// (<c>$re</c>, <c>${re}</c>, <c>$(cmd)</c>) — so its emitted text is already a
    /// PowerShell expression and must not be wrapped in a single-quoted literal.
    /// A mixed word (<c>^${p}[0-9]+$</c>) is not sole and keeps the literal path.
    /// </summary>
    /// <summary>
    /// The regex of <c>[[ x =~ WORD ]]</c> assembled part by part, as bash does: UNQUOTED text (and an unquoted
    /// <c>$var</c>) is regex syntax, while every QUOTED part (<c>'a.c'</c>, <c>"$v"</c>, <c>\.</c>) matches
    /// itself — so <c>a"."c</c> is a, a literal dot, c. Returns false for a word with no quoted/escaped/dynamic
    /// part (the plain text path handles it) or with a part this builder does not model.
    /// </summary>
    private static bool TryBuildRegexPattern(CompoundWord word, out string patExpr)
    {
        patExpr = "";
        bool needsBuilder = false;
        foreach (var part in word.Parts)
        {
            switch (part)
            {
                case WordPart.Literal or WordPart.GlobPart:
                    break;
                case WordPart.SingleQuoted or WordPart.EscapedLiteral or WordPart.DoubleQuoted
                    or WordPart.SimpleVarSub or WordPart.BracedVarSub:
                    needsBuilder = true;
                    break;
                default:
                    return false;
            }
        }
        if (!needsBuilder) return false;

        var pieces = new List<string>(word.Parts.Length);
        foreach (var part in word.Parts)
        {
            switch (part)
            {
                case WordPart.Literal l:
                    pieces.Add(PsBuild.SingleQuote(l.Value));
                    break;
                case WordPart.GlobPart g:
                    pieces.Add(PsBuild.SingleQuote(g.Pattern));
                    break;
                case WordPart.SingleQuoted sq:
                    pieces.Add(PsBuild.SingleQuote(System.Text.RegularExpressions.Regex.Escape(sq.Value)));
                    break;
                case WordPart.EscapedLiteral el:
                    pieces.Add(PsBuild.SingleQuote(System.Text.RegularExpressions.Regex.Escape(el.Value)));
                    break;
                case WordPart.DoubleQuoted dq:
                    if (dq.Parts.IsDefaultOrEmpty) break;
                    pieces.Add("[regex]::Escape([string](" + EmitWord(new CompoundWord(ImmutableArray.Create<WordPart>(dq))) + "))");
                    break;
                default: // an unquoted expansion: its VALUE is regex text
                    pieces.Add("[string](" + EmitWord(new CompoundWord(ImmutableArray.Create(part))) + ")");
                    break;
            }
        }
        if (pieces.Count == 0) { patExpr = "''"; return true; }
        patExpr = "(" + string.Join(" + ", pieces) + ")";
        return true;
    }

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
            // A lone expansion is unwrapped so it lands as a bare PowerShell value:
            // "$HOME" -> $HOME, "${V:-}" -> ($env:V ?? "").
            //
            // The old rule was "keep it only if the emitted text starts with $",
            // which single-quoted every OTHER expansion shape as a LITERAL. The very
            // common `[ -z "${V:-}" ]` became
            //     [string]::IsNullOrEmpty('($env:V ?? "")')
            // — a test against that 17-character source text, never empty, so `-z` was
            // always false and `-n` always true. Silent wrong answer, no error.
            if (dq.Parts.Length == 1 && IsExpansionPart(dq.Parts[0]))
                return EmitWordPart(dq.Parts[0]);

            // Mixed literal + expansion ("prefix$V") must stay INTERPOLATING: the
            // single-quoted form froze `$env:V` as literal text. EmitWord emits the
            // proper PowerShell double-quoted string.
            foreach (var part in dq.Parts)
            {
                if (IsExpansionPart(part))
                    return EmitWord(word);
            }

            // Pure literal — single-quote it, or PowerShell treats it as a command.
            var sb = new StringBuilder();
            foreach (var part in dq.Parts)
                sb.Append(EmitWordPart(part));
            return "'" + sb.ToString().Replace("'", "''") + "'";
        }
        return EmitWord(word);
    }

    /// <summary>A word part whose emitted text is a PowerShell expression (it reads a
    /// variable, runs a command, or evaluates arithmetic) rather than literal text.</summary>
    private static bool IsExpansionPart(WordPart part)
        => part is WordPart.SimpleVarSub
            or WordPart.BracedVarSub
            or WordPart.CommandSub
            or WordPart.ArithSub;

    /// <summary>
    /// A for-list word to expand against the filesystem: its EMITTED text carries a glob character AND the
    /// word itself has an unquoted glob part. The text test alone matched the regex inside a quoted
    /// <c>"$(cmd)"</c>'s emitted <c>-replace '(\r?\n)+$'</c>, so a quoted substitution was "globbed".
    /// </summary>
    private static bool WordIsGlob(CompoundWord word, string emitted)
        => HasGlobChars(emitted) && word.Parts.Any(p => p is WordPart.GlobPart);

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

        // Only split at paren depth 0. `[[ a || ( b && c ) ]]` must yield the two
        // top-level operands `a` and `( b && c )`, not three flat ones — splitting
        // inside the group would re-associate the expression and change its meaning.
        int depth = 0;
        foreach (var word in inner)
        {
            var lit = GetLiteralValue(word);
            if (lit == "(") depth++;
            else if (lit == ")") depth--;

            if (depth == 0 && lit is "&&" or "||")
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

    /// <summary>
    /// Strip one fully-enclosing pair of grouping parens from a <c>[[ ]]</c> clause,
    /// e.g. <c>( -f $f &amp;&amp; ! -L $f )</c>. Returns false when the clause is not
    /// wrapped, or when the leading <c>(</c> closes before the end (<c>( a ) || ( b )</c>
    /// — already handled by the depth-aware split above).
    /// </summary>
    private static bool TryUnwrapTestGroup(
        ImmutableArray<CompoundWord> words, out ImmutableArray<CompoundWord> innerWords)
    {
        innerWords = words;
        if (words.Length < 2
            || GetLiteralValue(words[0]) != "("
            || GetLiteralValue(words[^1]) != ")")
            return false;

        int depth = 0;
        for (int i = 0; i < words.Length; i++)
        {
            var lit = GetLiteralValue(words[i]);
            if (lit == "(") depth++;
            else if (lit == ")") depth--;
            // The opening paren closes before the last word — not one enclosing group.
            if (depth == 0 && i < words.Length - 1) return false;
        }
        if (depth != 0) return false;

        innerWords = words[1..^1];
        return true;
    }


    /// <summary>
    /// A bare assignment statement ends with status 0 (`false; x=1; echo $?` prints 0), unless a command substitution
    /// supplies the status or the target is readonly (the refusal sets 1).
    /// </summary>
    private static string NamerefMark(string name, string target) =>
        "[PsBash.Cmdlets.BashShellState]::SetNameref(" + PsBuild.SingleQuote(name) + ", " + PsBuild.SingleQuote(target) + ")";

    private static string EmitShAssignmentStatement(Command.ShAssignment cmd)
    {
        string text = EmitShAssignment(cmd);
        // bash: a bare assignment resets $_ to empty.
        if (_scriptReadsLastArg && !cmd.IsLocal && !cmd.IsExport)
        {
            if (!cmd.IsDeclare) text += "; $global:BashLastArg = ''";
            else if (cmd.Pairs.Length > 0 && cmd.Pairs[^1].ArrayValue is not null) text += "; $global:BashLastArg = " + PsBuild.SingleQuote(cmd.Pairs[^1].Name);
        }
        if (text.Contains("circular name reference", StringComparison.Ordinal)) return text; // the refusal set status 1
        // `x=$((1/0))`: the failed expansion keeps status 1, and its mark must not abandon a LATER command.
        if (text.Contains("Invoke-BashArith", StringComparison.Ordinal)) return text + "; " + PsBuild.ClearExpansionFailed;
        if (!_scriptReadsStatus || ContainsCommandSub(cmd)) return text;
        if (cmd.Pairs.Any(p => _readonlyVars?.Contains(p.Name.Contains('[') ? p.Name[..p.Name.IndexOf('[')] : p.Name) == true || ResolveNameref(p.Name) != p.Name)) return text;
        // An array literal whose glob fails under failglob ends the statement with status 1; keep it.
        if (cmd.Pairs.Any(p => p.ArrayValue is not null)) return text + "; if ($?) { $global:LASTEXITCODE = 0 }";
        return text + "; $global:LASTEXITCODE = 0";
    }

    private static string EmitShAssignment(Command.ShAssignment cmd)
    {
        return EmitShAssignmentCore(cmd);
    }

    private static string EmitShAssignmentCore(Command.ShAssignment cmd)
    {
        var sb = new StringBuilder();
        bool badSubscript = false;
        for (int i = 0; i < cmd.Pairs.Length; i++)
        {
            if (i > 0)
                sb.Append("; ");

            var pair = cmd.Pairs[i];

            // `export a[7]=8` / `readonly a[7]=8`: these take no subscript (`local`/`declare` do).
            if ((cmd.IsExport || cmd.IsReadonly) && !cmd.IsLocal && !cmd.IsDeclare && pair.Name.Contains('['))
            {
                sb.Append("Write-Error ").Append(PsBuild.SingleQuote((cmd.IsExport ? "export" : "readonly") + ": `" + pair.Name + "': not a valid identifier"))
                  .Append(" -ErrorAction Continue");
                badSubscript = true;
                continue;
            }

            // `typeset -n ref` came first: this literal assignment names its target (and is not a value write).
            if (!cmd.IsLocal && !cmd.IsExport && pair.ArrayValue is null && pair.Op == AssignOp.Equal && _pendingNamerefs is { Count: > 0 } && _pendingNamerefs.Contains(pair.Name)
                && pair.Value is not null && ExtractSingleQuotedOrLiteralValue(pair.Value) is { } refTarget && IsValidVarName(refTarget) && _namerefs is not null)
            {
                _pendingNamerefs.Remove(pair.Name);
                _namerefs[pair.Name] = refTarget;
                sb.Append(NamerefMark(pair.Name, refTarget)).Append("; ").Append(PsBuild.SetStatus(0));
                continue;
            }
            if (!cmd.IsLocal && !cmd.IsExport && _litAssigned is not null && !pair.Name.Contains('['))
            {
                if (pair.ArrayValue is null && pair.Op == AssignOp.Equal && pair.Value is not null && ExtractSingleQuotedOrLiteralValue(pair.Value) is { } litVal)
                    _litAssigned[pair.Name] = litVal;
                else _litAssigned.Remove(pair.Name);
            }

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

            // `local -n r=$1` in a variant where r is already resolved to its target: nothing to declare.
            if (cmd.IsLocal && cmd.IsNameref && _namerefs is not null && _namerefs.ContainsKey(pair.Name))
            {
                sb.Append(PsBuild.SetStatus(0));
                continue;
            }
            // `local -r x=1`: later writes to x in this function are refused (the set is restored when the function ends).
            if (cmd.IsLocal && cmd.IsReadonly) _readonlyVars?.Add(pair.Name);
            // Mutually recursive namerefs (`ref1 -> ref2 -> ref1`) are only detected when written through.
            if (!cmd.IsLocal && !cmd.IsExport && _namerefs is { Count: > 0 } && _namerefs.ContainsKey(pair.Name) && _namerefs.ContainsKey(ResolveNameref(pair.Name)))
            {
                sb.Append("Write-Error ").Append(PsBuild.SingleQuote($"ps-bash: {pair.Name}: circular name reference"))
                  .Append(" -ErrorAction Continue; ").Append(PsBuild.SetStatus(1));
                continue;
            }
            string roName = ResolveNameref(pair.Name); // a write through a nameref is a write to its target
            string roBase = roName.Contains('[') ? roName[..roName.IndexOf('[')] : roName;
            if (!cmd.IsLocal && !cmd.IsExport && _readonlyVars?.Contains(roBase) == true)
            {
                _readonlyViolation = true;
                sb.Append("Write-Error ").Append(PsBuild.SingleQuote($"ps-bash: {roBase}: readonly variable"))
                  .Append(" -ErrorAction Continue; ").Append(PsBuild.SetStatus(1));
                continue;
            }

            if (cmd.IsLocal && pair.ArrayValue is null && !pair.Name.Contains('[')
                && (_dynLocals?.Contains(pair.Name) == true
                    // eval text built at run time declares `local`s of the CALLING function, which cannot know them: they live in the shell variable.
                    || (_context == TranspileContext.Eval && _functionDepth == 0 && _dynLocals is null && IsDynamicLocalName(pair.Name))))
            {
                // Park the caller's value once per call (restored by EmitFunction's finally), then assign.
                // `local x` alone leaves x unset, as in bash.
                // `local s+=x`: the first declaration in this call starts from empty (not the caller's s); later ones append.
                if (pair.Op == AssignOp.PlusEqual && pair.Value is not null)
                    sb.Append($"$__psbashLocBase = if ($null -eq $__lsd_{pair.Name}) {{ '' }} else {{ $env:{pair.Name} }}; ");
                sb.Append($"if ($null -eq $__lsd_{pair.Name}) {{ $__lsd_{pair.Name} = @{{ v = $env:{pair.Name} }} }}; $env:{pair.Name} = ");
                if (pair.Value is null) sb.Append("$null");
                else
                {
                    string localAssigned = EmitAssignmentValue(pair.Value);
                    // `local s+=x` appends to the value already there (the caller's, or this function's earlier one).
                    sb.Append(pair.Op == AssignOp.Equal && _intVars?.Contains(pair.Name) == true
                        ? "[string](Invoke-BashArith ([string](" + localAssigned + ")))"
                        : pair.Op == AssignOp.PlusEqual
                            ? "$__psbashLocBase + (" + localAssigned + ")"
                            : CaseAttr(pair.Name, localAssigned));
                }
                continue;
            }

            string varPrefix = cmd.IsLocal ? "$" : "$env:";

            // Array assignment: arr=(a b c) -> $arr = @('a','b','c')
            // Array append:     arr+=('x')  -> $arr += @('x')
            if (pair.ArrayValue is not null)
            {
                if (cmd.IsAssoc) _assocVars?.Add(pair.Name);
                if (IsValidVarName(pair.Name)) _arrayVars?.Add(pair.Name);
                if (pair.Op is AssignOp.Equal or AssignOp.PlusEqual && TryEmitAssocLiteral(pair.ArrayValue, out var assoc, cmd.IsAssoc || _assocVars?.Contains(pair.Name) == true))
                {
                    // `a+=([k]=v)` merges into the existing table (a changed value keeps its place, a new key joins).
                    sb.Append(ArrayWriteTarget(pair.Name)).Append(" = ");
                    sb.Append(pair.Op == AssignOp.PlusEqual
                        ? "[PsBash.Cmdlets.BashAssoc]::Merge(" + ArrayWriteTarget(pair.Name) + ", " + assoc + ")"
                        : assoc);
                    continue;
                }
                // `a=([2]=p [5]=q)`: explicit numeric subscripts; the gaps stay UNSET (a sparse array), as in bash.
                if (pair.Op == AssignOp.Equal && !cmd.IsAssoc && _assocVars?.Contains(pair.Name) != true && TryEmitIndexedLiteral(pair.ArrayValue, out var indexed))
                {
                    sb.Append(ArrayWriteTarget(pair.Name)).Append(" = ").Append(indexed);
                    continue;
                }
                // `declare -A m=()` is an EMPTY ASSOCIATIVE array, not an empty indexed one.
                if (cmd.IsAssoc && pair.Op == AssignOp.Equal && pair.ArrayValue.Elements.IsEmpty)
                {
                    sb.Append(ArrayWriteTarget(pair.Name)).Append(" = [PsBash.Cmdlets.BashAssoc]::new()");
                    continue;
                }
                // `declare -A a; a=(k v k2 v2)` (bash 5.1+): bare words are key/value pairs, a lone last key gets "".
                bool wordsArePairs = pair.Op == AssignOp.Equal && _assocVars?.Contains(pair.Name) == true;
                sb.Append(ArrayWriteTarget(pair.Name));
                // `a+=(x)` goes through Add-BashArray: a sparse array appends after its highest subscript,
                // which PowerShell's own `+=` cannot do on a dictionary.
                sb.Append(pair.Op == AssignOp.PlusEqual ? " = Add-BashArray " + ArrayWriteTarget(pair.Name) + " @(" : wordsArePairs ? " = [PsBash.Cmdlets.BashAssoc]::FromWords(@(" : " = @(");
                // A glob element (`arr=(*.txt)`) expands to ZERO or more elements, which the comma
                // form cannot splice (`'a',@('b','c')` is two elements). Such a list is emitted as
                // `@( item; item; ... )`, whose statements each enumerate into one flat array. An unquoted `$x` /
                // `$(cmd)` / `${x#p}` element is word-split the same way (`a=($(sort f))` is one element PER LINE/word).
                bool anyGlobElement = pair.ArrayValue.Elements.Any(IsUnquotedSplitWord);
                for (int j = 0; j < pair.ArrayValue.Elements.Length; j++)
                {
                    if (j > 0)
                        sb.Append(anyGlobElement ? "; " : ",");
                    var elem = pair.ArrayValue.Elements[j];
                    if (anyGlobElement && IsUnquotedSplitWord(elem))
                    {
                        sb.Append(EmitUnquotedSplitWordArray(elem));
                        continue;
                    }
                    // Emit each element as a properly-quoted PS value expression. Naively
                    // wrapping EmitWord() in single quotes stored the literal string "$env:x"
                    // for a "$x" element and produced unbalanced quotes for $'a\'b' elements.
                    sb.Append(EmitAssignmentValue(elem));
                }
                sb.Append(wordsArePairs ? "))" : ")");
                continue;
            }

            // A nameref to an array element (`declare -n ref='a[2]'; ref=X`) writes that element.
            if (!pair.Name.Contains('[') && ResolveNameref(pair.Name) is var elemTarget && IsNamerefElementTarget(elemTarget))
                pair = pair with { Name = elemTarget };
            // Subscript assignment: map[key]=val -> $map['key'] = 'val'
            if (pair.Name.Contains('['))
            {
                int bracketIdx = pair.Name.IndexOf('[');
                string baseName = pair.Name[..bracketIdx];
                string subscript = pair.Name[(bracketIdx + 1)..^1]; // strip [ and ]
                // `a[$i]=v` / `a[i+1]+=v` / `m[key]=v`: the subscript is a WORD (expanded here) and, for an indexed
                // array, then evaluated as arithmetic at run time; Set-BashElement pads/keys and returns the container.
                // A leading `~` is not a tilde prefix in a subscript (`m[~]=v` keys `~`, not $HOME).
                string target = ArrayWriteTarget(ResolveNameref(baseName));
                sb.Append(target).Append(" = Set-BashElement ").Append(target).Append(' ')
                  .Append(FlattenPartsToDoubleQuotedString(BashParser.DecomposeWord(subscript.StartsWith('~') ? "\\" + subscript : subscript))).Append(' ')
                  .Append(EmitAssignmentValue(pair.Value));
                if (pair.Op == AssignOp.PlusEqual) sb.Append(" -Append");
                continue;
            }

            sb.Append(varPrefix);
            sb.Append(ResolveNameref(pair.Name));
            if (pair.Value is null)
            {
                // Bare export/local VAR with no value: ensure variable exists
                sb.Append(" = ");
                sb.Append(varPrefix);
                sb.Append(pair.Name);
            }
            else
            {
                bool intPlus = pair.Op == AssignOp.PlusEqual && _intVars?.Contains(pair.Name) == true;
                sb.Append(pair.Op == AssignOp.PlusEqual && !intPlus ? " += " : " = ");
                string assigned = EmitAssignmentValue(pair.Value);
                // A `declare -i` variable evaluates its value as arithmetic: `n=5; n+=2` adds (7), not appends (52).
                sb.Append(intPlus
                    ? "[string](Invoke-BashArith (" + PsBuild.SingleQuote(pair.Name + "+(") + " + [string](" + assigned + ") + ')'))"
                    : pair.Op == AssignOp.Equal && _intVars?.Contains(pair.Name) == true
                        ? "[string](Invoke-BashArith ([string](" + assigned + ")))"
                        : pair.Op == AssignOp.Equal ? CaseAttr(pair.Name, assigned) : assigned);
            }
        }
        // `local`/`export x=$(false)`: the status is the BUILTIN's (0), not the substitution's —
        // the substitution left its own code in LASTEXITCODE. (A plain `x=$(false)` keeps it.)
        if ((cmd.IsLocal || cmd.IsExport) && ContainsCommandSub(cmd))
            sb.Append("; " + PsBuild.SetStatus(0));
        // `~` and `$HOME` read PowerShell's own read-only $HOME (the launcher's home), which `HOME=/x` never touched;
        // re-point it so a script that assigns HOME sees it (`HOME=/home/bob; echo ~` prints /home/bob).
        if (!cmd.IsLocal && cmd.Pairs.Any(p => p.Name == "HOME" && p.ArrayValue is null && p.Op == AssignOp.Equal))
            sb.Append("; Microsoft.PowerShell.Utility\\Set-Variable -Name HOME -Value $env:HOME -Option ReadOnly,AllScope -Scope Global -Force");
        // A script writes PATH with `:` separators (`PATH=_tmp:$PATH`); Windows lookup needs `;`.
        if (cmd.Pairs.Any(p => p.Name == "PATH" && p.ArrayValue is null))
            sb.Append("; ").Append(PsBuild.NormalizePathVariable);
        // `export X=1` / `export X`: `declare -p X` shows the x attribute.
        if (cmd.IsExport && !cmd.IsLocal && _declarePrintMode)
            foreach (var pair in cmd.Pairs)
                if (pair.ArrayValue is null && !pair.Name.Contains('['))
                    sb.Append("; [PsBash.Cmdlets.BashShellState]::MarkAttribute(").Append(PsBuild.SingleQuote(pair.Name)).Append(", 'x')");
        // `declare -r a=(x y)` / `readonly a=(...)`: the array is read-only too (`${a@a}` says `ar`).
        if (cmd.IsReadonly)
            foreach (var pair in cmd.Pairs)
                if (pair.ArrayValue is not null)
                {
                    sb.Append("; [PsBash.Cmdlets.BashShellState]::MarkReadonly(").Append(PsBuild.SingleQuote(pair.Name)).Append(")");
                    if (!cmd.IsLocal) _readonlyVars?.Add(pair.Name);
                }
        if (badSubscript) sb.Append("; ").Append(PsBuild.SetStatus(1));
        return sb.ToString();
    }

    // `local`, `local -p`, `local -p a b`: a listing (no assignments, no other flags).
    private static bool IsLocalListing(Command.Simple cmd)
    {
        for (int k = 1; k < cmd.Words.Length; k++)
            if (GetLiteralValue(cmd.Words[k]) is not { } s || s.Contains('=') || (s.StartsWith('-') && s != "-p") || s.Length == 0) return false;
        return true;
    }

    // `declare $var="x"`, `export "$k=$v"`, `readonly $n=1`: the variable NAME is only known at run time, so the
    // static declaration machinery cannot see it. Assign the named scalar directly (the value is data, never re-parsed).
    /// <summary>
    /// `local "$1"` / `local $1` / `readonly $two $one`: the names (and any `=value`) exist only at run time, so the
    /// declaration runs through the run-time eval, which declares a calling function's locals. A quoted word stays one
    /// word (`printf %q`: `local "$1"` with `x=y a=b` makes x='y a=b'); an unquoted one is split by the eval, as bash
    /// splits it. Without this the builtin fell to command lookup: "local: command not found".
    /// </summary>
    private static bool TryEmitRuntimeNamedDeclaration(string name, Command.Simple cmd, out string result)
    {
        result = "";
        static bool IsExpansion(WordPart p) => p is WordPart.SimpleVarSub or WordPart.BracedVarSub or WordPart.CommandSub;
        bool anyDynamic = false;
        var evalWords = ImmutableArray.CreateBuilder<CompoundWord>();
        evalWords.Add(new CompoundWord([new WordPart.Literal(name)]));
        foreach (var w in cmd.Words.Skip(1))
        {
            if (w.Parts.IsDefaultOrEmpty) return false;
            if (GetLiteralValue(w) is { Length: > 1 } opt && opt[0] is '-' or '+' && opt.Skip(1).All(char.IsAsciiLetter))
            {
                evalWords.Add(w);
                continue;
            }
            if (w.Parts.All(IsExpansion))
            {
                anyDynamic = true;
                evalWords.Add(w);
                continue;
            }
            if (w.Parts.Length == 1 && w.Parts[0] is WordPart.DoubleQuoted dq && !dq.Parts.IsDefaultOrEmpty && dq.Parts.All(IsExpansion))
            {
                anyDynamic = true;
                var printf = new Command.Simple(
                    [new CompoundWord([new WordPart.Literal("printf")]), new CompoundWord([new WordPart.SingleQuoted("%q")]), w],
                    ImmutableArray<EnvPair>.Empty, ImmutableArray<Redirect>.Empty);
                evalWords.Add(new CompoundWord([new WordPart.DoubleQuoted([new WordPart.CommandSub(printf)])]));
                continue;
            }
            return false;
        }
        if (!anyDynamic) return false;
        result = EmitEval(evalWords.ToImmutable());
        return true;
    }

    private static bool TryEmitDynamicAssignmentBuiltin(Command.Simple cmd, out string result)
    {
        result = "";
        var name = cmd.Words.Length < 2 ? null : GetLiteralValue(cmd.Words[0]);
        if (name is not ("declare" or "typeset" or "local" or "export" or "readonly"))
            return false;
        static bool IsDynamicAssignment(CompoundWord w)
        {
            if (w.Parts.IsDefaultOrEmpty) return false;
            bool dynamicFirst = w.Parts[0] is WordPart.SimpleVarSub or WordPart.BracedVarSub or WordPart.DoubleQuoted or WordPart.SingleQuoted;
            if (!dynamicFirst) return false;
            static bool HasEquals(ImmutableArray<WordPart> ps) => ps.Any(p => p is WordPart.Literal l && l.Value.Contains('=')
                || p is WordPart.SingleQuoted sq && sq.Value.Contains('=')
                || p is WordPart.DoubleQuoted d && HasEquals(d.Parts));
            return HasEquals(w.Parts);
        }
        // `declare`/`typeset` with run-time words already go to Invoke-BashDeclare; `local` and `readonly` did not.
        if (name is "local" or "readonly" && TryEmitRuntimeNamedDeclaration(name, cmd, out result)) return true;
        if (!cmd.Words.Skip(1).Any(IsDynamicAssignment)) return false;

        // Everything that is not a dynamic assignment keeps its normal path; each dynamic one assigns the scalar
        // named at run time (all scalars live in the environment store).
        static bool IsAssignment(CompoundWord w) => IsDynamicAssignment(w) || TrySplitInitializer(w, out _, out _);
        var plain = cmd.Words.Where((w, i) => i == 0 || !IsAssignment(w)).ToImmutableArray();
        var sbOut = new StringBuilder();
        if (plain.Skip(1).Any(w => !(GetLiteralValue(w) is { Length: > 1 } f && f[0] is '-' or '+')))
            sbOut.Append(EmitSimple(cmd with { Words = plain })).Append("; ");
        foreach (var w in cmd.Words.Skip(1).Where(IsAssignment))
        {
            if (!IsDynamicAssignment(w) && TrySplitInitializer(w, out var staticName, out var staticValue))
            {
                sbOut.Append("$env:").Append(staticName).Append(" = ").Append(EmitAssignmentValue(staticValue)).Append("; ");
                if (staticName == "PATH") sbOut.Append(PsBuild.NormalizePathVariable).Append("; ");
                continue;
            }
            string expr = "[string](" + EmitWord(w) + ")";
            sbOut.Append($"$__psbD = {expr}; $__psbI = $__psbD.IndexOf('='); ")
                 .Append("if ($__psbI -gt 0) { Set-Item -LiteralPath ('Env:' + $__psbD.Substring(0, $__psbI)) -Value $__psbD.Substring($__psbI + 1) }; ");
        }
        sbOut.Append(PsBuild.SetStatus(0));
        result = sbOut.ToString();
        return true;
    }

    /// <summary>`declare foo/bar=1`, `export 1x=2`, `local a-b`: a literal operand whose NAME is not an identifier is
    /// reported (``NAME=v': not a valid identifier`, status 1) and dropped, instead of emitting `$env:foo/bar` (a PowerShell
    /// parse error that kills the whole script). Valid operands still run.</summary>
    private static bool TryEmitInvalidIdentifiers(ref Command.Simple cmd, out string result)
    {
        result = "";
        var cname = cmd.Words.Length < 2 ? null : GetLiteralValue(cmd.Words[0]);
        if (cname is not ("declare" or "typeset" or "local" or "export" or "readonly")) return false;
        // Function/print forms take names that are not variables.
        foreach (var w in cmd.Words.Skip(1))
            if (GetLiteralValue(w) is { Length: > 1 } f && f[0] == '-' && f.AsSpan(1).IndexOfAny('f', 'F', 'p') >= 0) return false;
        var keep = new List<CompoundWord> { cmd.Words[0] };
        var errs = new StringBuilder();
        foreach (var w in cmd.Words.Skip(1))
        {
            string? text = w.Parts.IsDefaultOrEmpty ? null : w.Parts[0] is WordPart.Literal l0 && w.Parts.All(p => p is WordPart.Literal) ? string.Concat(w.Parts.Select(p => ((WordPart.Literal)p).Value)) : null;
            if (text is null && !w.Parts.IsDefaultOrEmpty && w.Parts[0] is WordPart.Literal first && first.Value.Contains('='))
                text = first.Value;
            if (text is null || text.Length == 0 || text[0] is '-' or '+') { keep.Add(w); continue; }
            int eq = text.IndexOf('=');
            string nm = eq < 0 ? text : text[..eq];
            if (nm.EndsWith('+')) nm = nm[..^1];
            int br = nm.IndexOf('[');
            if (br > 0 && nm.EndsWith(']'))
            {
                // `export a[7]=8` / `readonly a[7]=8`: these take no subscript (`local`/`declare` do).
                if (cname is "export" or "readonly")
                {
                    errs.Append("Write-Error ").Append(PsBuild.SingleQuote(cname + ": `" + nm + "': not a valid identifier")).Append(" -ErrorAction Continue; ");
                    continue;
                }
                nm = nm[..br];
            }
            if (IsValidVarName(nm) || nm.IndexOfAny(['$', '`', '\'', '"']) >= 0) { keep.Add(w); continue; }
            errs.Append("Write-Error ").Append(PsBuild.SingleQuote(cname + ": `" + text + "': not a valid identifier")).Append(" -ErrorAction Continue; ");
        }
        if (errs.Length == 0) return false;
        if (keep.Count == 1)
        {
            result = errs + PsBuild.SetStatus(1);
            return true;
        }
        var rest = EmitSimple(cmd with { Words = [.. keep] });
        result = errs + rest + "; " + PsBuild.SetStatus(1);
        return true;
    }
    [ThreadStatic]
    private static int _outputProcSubCounter;

    /// <summary>
    /// <c>cmd &gt; &gt;(body)</c> / <c>tee &gt;(body)</c>: bash connects a pipe to body's stdin and runs it beside cmd.
    /// Here cmd writes a temp file (its path replaces the <c>&gt;(...)</c> word), then body reads that file as
    /// its stdin once cmd has ended, and cmd's exit status is kept. Running body at once instead (the
    /// <c>&lt;(...)</c> path) left it reading the shell's own stdin: <c>seq 3 &gt; &gt;(tac)</c> hung.
    /// <c>exec &gt; &gt;(...)</c> is not covered (its writes go on after this command).
    /// </summary>
    private static bool TryEmitOutputProcessSubs(Command.Simple cmd, out string result)
    {
        result = "";
        if (!cmd.Words.IsDefaultOrEmpty && GetLiteralValue(cmd.Words[0]) == "exec") return false;
        var bodies = new List<(string Var, Command Body)>();
        var words = cmd.Words.IsDefaultOrEmpty ? cmd.Words : [.. cmd.Words.Select(w => RewriteOutputProcessSubWord(w, bodies))];
        var redirects = cmd.Redirects.IsDefaultOrEmpty ? cmd.Redirects : [.. cmd.Redirects.Select(r => r.Target is null ? r : r with { Target = RewriteOutputProcessSubWord(r.Target, bodies) })];
        if (bodies.Count == 0) return false;
        result = WrapOutputProcessSubs(bodies, EmitSimple(cmd with { Words = words, Redirects = redirects }));
        return true;
    }

    /// <summary>
    /// <c>{ ...; } &gt; &gt;(tac)</c>, <c>while ...; done &gt; &gt;(body)</c>: the same file hand-off as
    /// <see cref="TryEmitOutputProcessSubs"/>, for a compound command's own redirects.
    /// </summary>
    private static bool TryEmitCompoundOutputProcessSubs(Command cmd, out string result)
    {
        result = "";
        var redirects = cmd switch
        {
            Command.BraceGroup b => b.Redirects, Command.Subshell s => s.Redirects, Command.If i => i.Redirects,
            Command.ForIn f => f.Redirects, Command.ForArith fa => fa.Redirects, Command.While w => w.Redirects,
            Command.Case c => c.Redirects, _ => default,
        };
        if (redirects.IsDefaultOrEmpty || !redirects.Any(r => HasOutputProcessSub(r.Target))) return false;
        var bodies = new List<(string Var, Command Body)>();
        ImmutableArray<Redirect> rewritten = [.. redirects.Select(r => r.Target is null ? r : r with { Target = RewriteOutputProcessSubWord(r.Target, bodies) })];
        Command inner = cmd switch
        {
            Command.BraceGroup b => b with { Redirects = rewritten }, Command.Subshell s => s with { Redirects = rewritten },
            Command.If i => i with { Redirects = rewritten }, Command.ForIn f => f with { Redirects = rewritten },
            Command.ForArith fa => fa with { Redirects = rewritten }, Command.While w => w with { Redirects = rewritten },
            Command.Case c => c with { Redirects = rewritten }, _ => cmd,
        };
        result = WrapOutputProcessSubs(bodies, Emit(inner));
        return true;
    }

    private static bool HasOutputProcessSub(CompoundWord? w)
        => w is not null && !w.Parts.IsDefaultOrEmpty && w.Parts.Any(p => p is WordPart.ProcessSub { IsInput: false });

    /// <summary>Each <c>&gt;(body)</c> part becomes <c>"$__psbash_psoN"</c> (a temp file path); its body is queued in <paramref name="bodies"/>.</summary>
    private static CompoundWord RewriteOutputProcessSubWord(CompoundWord w, List<(string Var, Command Body)> bodies)
    {
        if (!HasOutputProcessSub(w)) return w;
        var parts = new List<WordPart>(w.Parts.Length);
        foreach (var p in w.Parts)
        {
            if (p is not WordPart.ProcessSub { IsInput: false } ps) { parts.Add(p); continue; }
            var name = "__psbash_pso" + (++_outputProcSubCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);
            bodies.Add((name, (Command)ps.Body));
            parts.Add(new WordPart.DoubleQuoted([new WordPart.SimpleVarSub(name)]));
        }
        return new CompoundWord([.. parts]);
    }

    /// <summary>Creates each temp file, runs <paramref name="command"/>, then feeds each file to its body; the command's status is kept.</summary>
    private static string WrapOutputProcessSubs(List<(string Var, Command Body)> bodies, string command)
    {
        var rc = "$__psbash_psoRc" + _outputProcSubCounter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        foreach (var (v, _) in bodies)
            sb.Append("$env:").Append(v).Append(" = New-BashProcessSubPath; ");
        sb.Append("try { ").Append(command).Append(" } finally { ").Append(rc).Append(" = $global:LASTEXITCODE; ");
        foreach (var (v, body) in bodies)
        {
            var path = new CompoundWord([new WordPart.DoubleQuoted([new WordPart.SimpleVarSub(v)])]);
            sb.Append(Emit(new Command.BraceGroup(body, [new Redirect("<", 0, path)]))).Append("; ")
              .Append("Remove-Item -LiteralPath $env:").Append(v).Append(" -Force -ErrorAction SilentlyContinue; ")
              .Append("Remove-Item Env:").Append(v).Append(" -ErrorAction SilentlyContinue; ");
        }
        sb.Append("$global:LASTEXITCODE = ").Append(rc).Append(" }");
        return sb.ToString();
    }

    private static string EmitSimple(Command.Simple cmd)
    {
        cmd = ExpandAliases(cmd);
        if (TryEmitOutputProcessSubs(cmd, out var outputProcSubResult))
            return outputProcSubResult;
        if (TryEmitEnvPairsWrap(cmd, out var envWrapResult))
            return envWrapResult;

        if (TryEmitHereDoc(cmd, out var hereDocResult))
            return hereDocResult;

        if (TryEmitFdRedirect(cmd, out var fdResult))
            return fdResult;

        if (TryEmitStderrRedirect(cmd, out var stderrResult))
            return stderrResult;

        if (TryEmitInputRedirect(cmd, out var inputResult))
            return inputResult;

        if (TryEmitInvalidIdentifiers(ref cmd, out var invalidIdResult))
            return invalidIdResult;

        if (TryEmitDynamicAssignmentBuiltin(cmd, out var dynamicAssignResult))
            return dynamicAssignResult;

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

        // [1]=v cmd: an element is not an environment binding (it cannot be exported), so it is not wrapped around the command.
        if (cmd.EnvPairs.Any(p => p.Name.Contains('[')))
        {
            var kept = cmd.EnvPairs.RemoveAll(p => p.Name.Contains('['));
            result = EmitSimple(cmd with { EnvPairs = kept });
            return true;
        }

        // `x=v <<EOF`: with no command word the assignments are the shell's own (they persist) and nothing reads the
        // here-document, exactly as without it.
        if (cmd.Words.IsEmpty && !cmd.HereDocs.IsDefaultOrEmpty && cmd.HereDocs.All(h => h.Fd < 3))
        {
            result = EmitShAssignment(new Command.ShAssignment(
                cmd.EnvPairs.Select(p => new Assignment(p.Name, AssignOp.Equal, p.Value)).ToImmutableArray()));
            return true;
        }

        var bare = cmd with { EnvPairs = ImmutableArray<EnvPair>.Empty };
        result = WrapEnvPairs(cmd.EnvPairs, WithEnvShadow(cmd.EnvPairs, () => EmitSimple(bare)));
        // `foo=bar $unset`: a command word that expands to NOTHING leaves no command, so the assignment persists
        // (only a command that actually runs gets the temporary binding).
        if (cmd.Words.Length > 0 && IsPureUnquotedVarWord(cmd.Words[0]))
        {
            string nameWords = EmitUnquotedSplitWordArray(cmd.Words[0]);
            string assignOnly = EmitShAssignment(new Command.ShAssignment(
                cmd.EnvPairs.Select(p => new Assignment(p.Name, AssignOp.Equal, p.Value)).ToImmutableArray()));
            result = $"if (@({nameWords}).Count -eq 0) {{ {assignOnly} }} else {{ {result} }}";
        }
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

        // `cmd 3<<EOF` / `exec 3<<EOF`: the body is descriptor 3's input (`read -u 3`), not stdin.
        if (cmd.HereDocs.Any(h => h.Fd >= 3))
        {
            var fdDocs = cmd.HereDocs.Where(h => h.Fd >= 3).ToList();
            var opens = string.Join("; ", fdDocs.Select(h =>
                $"Invoke-BashExecFd {h.Fd} '<' (Invoke-ProcessSub {{ {EmitHereDocLiteral(h)} | Emit-BashLine }})"));
            bool isExec = cmd.Words.Length == 1 && GetLiteralValue(cmd.Words[0]) == "exec";
            if (isExec && cmd.HereDocs.Length == fdDocs.Count)
            {
                result = opens;
                return true;
            }
            var others = cmd.HereDocs.RemoveRange(fdDocs);
            string inner = EmitSimple(new Command.Simple(cmd.Words, cmd.EnvPairs, cmd.Redirects, others));
            var closes = string.Join("; ", fdDocs.Select(h => $"Invoke-BashExecFd {h.Fd} '<&' '-'"));
            // Closing resets the status, so keep the command's own.
            result = $". {{ {opens}; {inner}; $__psbashFdRc = $global:LASTEXITCODE; {closes}; $global:LASTEXITCODE = $__psbashFdRc }}";
            return true;
        }

        // `exec 4<<<"text"`: the parser leaves the fd as a lone numeric word beside the here-document.
        if (cmd.Words.Length == 2 && cmd.HereDocs.Length == 1 && cmd.Redirects.IsDefaultOrEmpty && cmd.EnvPairs.IsEmpty
            && GetLiteralValue(cmd.Words[0]) == "exec"
            && GetLiteralValue(cmd.Words[1]) is { Length: > 0 } fdText && fdText.All(char.IsAsciiDigit))
        {
            result = $"Invoke-BashExecFd {fdText} '<' (Invoke-ProcessSub {{ {EmitHereDocLiteral(cmd.HereDocs[0])} | Emit-BashLine }})";
            return true;
        }

        // When multiple heredocs exist, use the last one for stdin (bash behavior).
        var innerCmd = new Command.Simple(cmd.Words, cmd.EnvPairs, cmd.Redirects);
        // `x=$(cat) <<EOF`: there is no command to read the here-document (a command substitution in the value has the
        // shell's own stdin), so the body is dropped rather than piped into nothing.
        if (cmd.Words.IsEmpty)
        {
            result = EmitSimple(innerCmd);
            return true;
        }
        if (cmd.EnvPairs.IsEmpty && GetLiteralValue(cmd.Words[0]) == "eval")
        {
            result = $"{EmitHereDocLiteral(cmd.HereDocs[^1])} | Emit-BashLine | {EmitEvalWithStdin(() => EmitSimple(innerCmd), dotSource: true)}";
            return true;
        }
        string cmdText = EmitSimple(innerCmd);
        // A real program must read the heredoc's bytes, not PowerShell's per-record `\r\n` rendering of them;
        // every other consumer keeps receiving the records as before.
        result = $"{EmitHereDocLiteral(cmd.HereDocs[^1])} | Emit-BashLine | {NativeStdinFeedOrNull(innerCmd)}{cmdText}";
        return true;
    }

    /// <summary>
    /// Render a here-doc / here-string body as a PowerShell string literal, ready to be
    /// piped through <c>Emit-BashLine</c>. Shared by the simple-command heredoc path and
    /// the compound-command <c>done &lt;&lt;&lt; "$x"</c> path so both agree on
    /// variable translation and terminator-collision escaping (<c>&lt;&lt;-</c> tab
    /// stripping is done by the parser, not here).
    /// </summary>
    private static string EmitHereDocLiteral(HereDoc hereDoc)
    {
        // <<-EOF tab stripping already happened in the parser
        // (BashParser.CollectHereDocBodies), so Body is exactly what bash stores.
        string body = hereDoc.Body;
        if (hereDoc.Expand)
            body = TranslateHereDocVars(body);

        // A PowerShell here-string ends at the first line that begins with its
        // terminator (`"@` for @"..."@, `'@` for @'...'@). A heredoc body containing
        // such a line would close the string early — bash prints the line literally,
        // ps-bash threw "missing terminator". When that collision is present fall back
        // to an ordinary quoted string (whose value is identical to the here-string
        // content); otherwise keep the here-string, the readable common-case form.
        return HereStringWouldBreak(body, hereDoc.Expand)
            ? (hereDoc.Expand
                ? "\"" + EscapeForDoubleQuotedHereDocBody(body) + "\""
                : PsBuild.SingleQuote(body))
            : (hereDoc.Expand
                ? $"@\"\n{body}\n\"@"
                : $"@'\n{body}\n'@");
    }

    // Stdout-to-stderr redirects (>&2 or 1>&2). The command's stdout/stderr redirects are
    // applied by the fd model (TryAppendModelledRedirectTail) in bash's left-to-right dup
    // order — `>&2 2>/dev/null` still shows the output, `2>/dev/null >&2` discards it, `>f >&2`
    // truncates f — and stdout bound for stderr goes through PsBuild.StdoutToStderrStage, an
    // error-stream record, so redirects of an ENCLOSING command (`{ echo e >&2; } 2>/dev/null`)
    // and `2>&1` apply to it. Every other redirect (`< f`, …) stays on the command.
    private static bool TryEmitStderrRedirect(Command.Simple cmd, out string result)
    {
        result = "";
        if (!cmd.Redirects.Any(IsStdoutToStderrDup))
            return false;

        static bool IsOutputRedirect(Redirect r) =>
            r.Fd is 1 or 2 && r.Op is ">" or ">|" or ">>" or "&>" or "&>>" or ">&";
        var output = cmd.Redirects.Where(IsOutputRedirect).ToList();
        var innerCmd = new Command.Simple(cmd.Words, cmd.EnvPairs,
            cmd.Redirects.Where(r => !IsOutputRedirect(r)).ToImmutableArray());

        var tail = new StringBuilder();
        if (TryAppendModelledRedirectTail(tail, output))
        {
            result = EmitSimple(innerCmd) + tail;
            return true;
        }

        // A list the model does not cover (user fds): the first `>&2` wins, as before.
        var dup = cmd.Redirects.First(IsStdoutToStderrDup);
        var rest = new Command.Simple(cmd.Words, cmd.EnvPairs, cmd.Redirects.Remove(dup));
        result = EmitSimple(rest) + PsBuild.StdoutToStderrStage;
        return true;
    }

    private static bool IsStdoutToStderrDup(Redirect r) =>
        r.Op == ">&" && r.Fd == 1 && GetLiteralValue(r.Target) == "2";

    /// <summary>
    /// The file of a <c>&lt; file</c> redirect, as an argument for the native <c>Get-Content</c> that
    /// reads it. A literal was mapped by <see cref="TransformRedirectTarget"/>; a target known only at
    /// run time (<c>&lt; $d/f</c>, d=/tmp) goes through the runtime path policy, or Get-Content —
    /// which no ps-bash resolver sees — read <c>C:\tmp\f</c> where every cmdlet reads <c>$env:TEMP\f</c>.
    /// </summary>
    private static string EmitInputRedirectTarget(CompoundWord word)
    {
        var target = TransformRedirectTarget(EmitArgWord(word));
        return target == "$null" || TryGetPureLiteralText(word.Parts, out _)
            ? target
            : PsBuild.RuntimeWindowsPath(target);
    }

    // Input redirects (< file) become "Get-Content file | cmd".
    // Special case: `< /dev/null` means "no input" — `Get-Content $null`
    // throws in PowerShell, so the command runs inside an EMPTY stdin scope
    // (PsBuild.EmptyStdinScope) where every stdin reader sees end of input.
    private static bool TryEmitInputRedirect(Command.Simple cmd, out string result)
    {
        // Only fd 0 is stdin: `exec 7<file` / `cmd 3<file` open ANOTHER descriptor and must not feed the command.
        var inputRedirect = cmd.Redirects.FirstOrDefault(r => r.Op == "<" && r.Fd == 0);
        if (inputRedirect is null)
        {
            result = "";
            return false;
        }

        var remaining = cmd.Redirects.Remove(inputRedirect);
        var innerCmd = new Command.Simple(cmd.Words, cmd.EnvPairs, remaining);
        var target = EmitInputRedirectTarget(inputRedirect.Target);

        if (target == "$null")
        {
            // Dropping the redirect used to mean "no stdin"; since the launcher forwards its own stdin it
            // meant the LIVE stdin (`read x </dev/null` read a line, `eval 'prog' </dev/null` fed prog).
            // Inside the empty scope every reader is fed as in any stdin scope — natives get an OS pipe at
            // EOF — so the result is end of input, whatever surrounds the command.
            _emptyStdinDepth++;
            try { result = PsBuild.EmptyStdinScope(WithStdinScope(true, () => EmitSimpleFed(innerCmd)), _emptyStdinDepth); }
            finally { _emptyStdinDepth--; }
            return true;
        }

        // `$(<file)` — bash's read-a-file shorthand: a redirect with NO command, whose
        // value is the file's contents. Emitting the usual `Get-Content f | <cmd>` with
        // an empty <cmd> produced a dangling `|` ("An empty pipe element is not
        // allowed"), which failed the parse of the whole file. With no command the
        // redirect IS the command.
        var inner = EmitSimple(innerCmd);
        if (inner.Length == 0)
        {
            // Only a lone `$(< file)` reads the file out; any other bare redirect just opens it.
            result = ReferenceEquals(cmd, _captureBody) ? $"Get-Content {target}" : $"Get-Content {target} | Out-Null";
            return true;
        }

        // The file is the command's STDIN, which is a byte stream: `Invoke-BashCat` feeds it as exact
        // records (no CRLF/BOM rewriting, a missing final newline kept, invalid UTF-8 bytes carried as
        // escaped-byte markers — see RawBytes), where Get-Content would decode lossily and always
        // terminate the last line (`wc -c < f` was one byte high). A mapped consumer takes those records
        // as-is; a native / unmapped consumer needs their TEXT (the binder would format a record object).
        bool mappedConsumer = TryEmitMappedCommand(innerCmd, out _);
        result = mappedConsumer
            ? $"Invoke-BashCat {target} -RawStdinBytes | {inner}"
            : $"Invoke-BashCat {target} -RawStdinBytes | {NativeStdinFeedStage(innerCmd)}{inner}";
        return true;
    }

    // declare/typeset -A map -> $map = @{} (associative array / hashtable declaration)
    // declare/typeset -a arr -> $arr = @() (indexed array declaration)
    private static bool TryEmitDeclare(Command.Simple cmd, out string result)
    {
        result = "";
        if (cmd.Words.Length < 2 || GetLiteralValue(cmd.Words[0]) is not ("declare" or "typeset"))
            return TryEmitDeclareCore(cmd, out result);

        // `declare -pr` / `-pa` / `-px`: a print form whose other letters SELECT variables; nothing is declared.
        if (cmd.Words.Skip(1).Any(w => GetLiteralValue(w) is { Length: > 1 } pf && pf[0] == '-' && pf[1] != '-' && pf.Contains('p')))
            return TryEmitDeclareCore(cmd, out result);

        // `declare -r X=1` is `readonly X=1`: emit the declaration without the r attribute, then mark each name.
        bool hasR = false;
        var attrs = new HashSet<char>();
        var kept = new List<CompoundWord> { cmd.Words[0] };
        var names = new List<string>();
        foreach (var w in cmd.Words.Skip(1))
        {
            var lit = GetLiteralValue(w);
            if (lit is { Length: > 1 } && lit[0] == '-' && lit.AsSpan(1).IndexOfAnyExcept("pfF") >= 0)
            {
                if (lit.Contains('x')) attrs.Add('x');
                if (lit.Contains('i') && _declarePrintMode) attrs.Add('i');
            }
            if (lit is { Length: > 1 } && lit[0] == '-' && lit.AsSpan(1).IndexOfAnyExcept("pfF") >= 0 && lit.Contains('r'))
            {
                hasR = true;
                var rest = lit.Replace("r", "");
                if (rest.Length > 1) kept.Add(new CompoundWord([new WordPart.Literal(rest)]));
                continue;
            }
            kept.Add(w);
            if (lit is null) { if (TrySplitInitializer(w, out var n0, out _)) names.Add(n0); }
            else if (!lit.StartsWith('-')) names.Add(lit.Split('=')[0]);
        }
        // bash ignores -r on a nameref (`typeset -n -r ref=x` still lets `ref=v` assign through).
        bool isNameref = cmd.Words.Skip(1).Any(w => GetLiteralValue(w) is { Length: > 1 } f && f[0] == '-' && f.AsSpan(1).IndexOfAnyExcept("pfF") >= 0 && f.Contains('n'));
        if (!(hasR || attrs.Count > 0) || isNameref || names.Count == 0)
            return TryEmitDeclareCore(cmd, out result);
        if (!TryEmitDeclareCore(cmd with { Words = [.. kept] }, out var core))
            return false;
        var mark = new StringBuilder();
        foreach (var n in names)
        {
            foreach (var a in attrs)
                mark.Append("; [PsBash.Cmdlets.BashShellState]::MarkAttribute(").Append(PsBuild.SingleQuote(n)).Append(", '").Append(a).Append("')");
            if (!hasR) continue;
            mark.Append("; [PsBash.Cmdlets.BashShellState]::MarkReadonly(").Append(PsBuild.SingleQuote(n)).Append(")");
            _readonlyVars?.Add(n);
        }
        result = core + mark;
        return true;
    }

    private static bool TryEmitDeclareCore(Command.Simple cmd, out string result)
    {
        result = "";
        if (cmd.Words.Length < 2)
            return false;

        var name = GetLiteralValue(cmd.Words[0]);
        if (name is not ("declare" or "typeset"))
            return false;

        // `declare a=1 b=2`: one declaration at a time (the single-name machinery below keeps only the last).
        // Not for the print forms, which take several names in one runtime call.
        {
            var flagWords = new List<CompoundWord>();
            var nameWords = new List<CompoundWord>();
            foreach (var w in cmd.Words.Skip(1))
            {
                var lit = GetLiteralValue(w);
                (lit is { Length: > 1 } && (lit[0] == '-' || lit.Contains('n')) ? flagWords : nameWords).Add(w);
            }
            bool printForm = flagWords.Any(w => GetLiteralValue(w) is { } f && f[0] == '-' && f.AsSpan(1).IndexOfAnyExcept("pfF") < 0);
            if (nameWords.Count > 1 && !printForm)
            {
                var parts = new List<string>();
                foreach (var nw in nameWords)
                {
                    var single = cmd with { Words = [cmd.Words[0], .. flagWords, nw] };
                    if (!TryEmitDeclare(single, out var one)) return false;
                    parts.Add(one);
                }
                result = string.Join("; ", parts);
                return true;
            }
        }

        bool isAssoc = false;
        bool isPrint = false;
        bool isNameref = false;
        string? varName = null;
        // `declare x=$(cmd)` / `declare x="$y"`: an initializer with expansions (not one literal).
        string? exprName = null;
        CompoundWord? exprValue = null;
        foreach (var word in cmd.Words.Skip(1))
        {
            var val = GetLiteralValue(word);
            if (val == "-A") isAssoc = true;
            else if (val == "-n") isNameref = true;
            else if (val == "-i") { /* integer — handled below */ }
            else if (val is { Length: > 1 } && val[0] == '-' && (val.AsSpan(1).IndexOfAnyExcept("pfF") < 0 || (val[1] != '-' && val.Contains('p')))) isPrint = true;
            else if (val is { Length: > 1 } && val[0] == '+' && val.AsSpan(1).IndexOfAnyExcept("aAilnrtux") < 0) { /* `declare +a x`: drops an attribute; nothing to record */ }
            else if (val is not null && !val.StartsWith('-')) varName = val;
            else if (val is null && TrySplitInitializer(word, out var n, out var v)) { exprName = n; exprValue = v; }
        }
        // `declare -p ref` for a nameref prints the attribute line; the reference itself is resolved at transpile time, so the
        // runtime has no variable to show.
        if (isPrint && varName is not null && IsValidVarName(varName) && _namerefs is not null && _namerefs.TryGetValue(varName, out var refTargetName)
            && cmd.Words.Skip(1).Count(w => GetLiteralValue(w) is { } x && !x.StartsWith('-')) == 1)
        {
            result = "Invoke-BashEcho " + PsBuild.SingleQuote("declare -n " + varName + "=\"" + refTargetName + "\"") + "; " + PsBuild.SetStatus(0);
            return true;
        }
        if (isAssoc && !isPrint && _assocVars is not null)
        {
            string? assocName = exprName ?? (varName is null ? null : varName.Split('=')[0]);
            if (assocName is not null && IsValidVarName(assocName)) _assocVars.Add(assocName);
        }
        if (!isPrint && !isAssoc && _intVars is not null && cmd.Words.Skip(1).Any(w => GetLiteralValue(w) == "-i"))
        {
            string? intName = exprName ?? (varName is null ? null : varName.Split('=')[0]);
            if (intName is not null && IsValidVarName(intName)) _intVars.Add(intName);
        }
        if (!isPrint && !isAssoc && _caseVars is not null
            && (cmd.Words.Skip(1).Any(w => GetLiteralValue(w) == "-l") || cmd.Words.Skip(1).Any(w => GetLiteralValue(w) == "-u")))
        {
            string? caseName = exprName ?? (varName is null ? null : varName.Split('=')[0]);
            if (caseName is not null && IsValidVarName(caseName))
                _caseVars[caseName] = cmd.Words.Skip(1).Any(w => GetLiteralValue(w) == "-u") && !cmd.Words.Skip(1).Any(w => GetLiteralValue(w) == "-l");
        }
        // `typeset +n ref` drops the nameref attribute: `ref` is the plain string again.
        if (!isPrint && varName is not null && IsValidVarName(varName) && _namerefs is not null
            && cmd.Words.Skip(1).Any(w => GetLiteralValue(w) is { Length: > 1 } f && f[0] == '+' && f.Contains('n')))
        {
            _namerefs.Remove(varName); _pendingNamerefs?.Remove(varName);
            result = "[PsBash.Cmdlets.BashShellState]::ClearNameref(" + PsBuild.SingleQuote(varName) + "); " + PsBuild.SetStatus(0);
            return true;
        }
        // `typeset -n ref` with no target: a string assigned earlier (`ref=x`) becomes the target; otherwise the next literal assignment names it.
        if (isNameref && !isPrint && varName is not null && IsValidVarName(varName) && _namerefs is not null && !_namerefs.ContainsKey(varName))
        {
            string nrMark = "";
            if (_litAssigned is not null && _litAssigned.TryGetValue(varName, out var known) && IsValidVarName(known) && known != varName)
            { _namerefs[varName] = known; nrMark = NamerefMark(varName, known) + "; "; }
            else if (_litAssigned is null || !_litAssigned.ContainsKey(varName)) _pendingNamerefs?.Add(varName);
            result = nrMark + PsBuild.SetStatus(0);
            return true;
        }
        // `declare -n ref='a[2]'` (a quoted element target makes the word non-literal, so it arrives as name + value).
        if (isNameref && !isPrint && exprName is not null && IsValidVarName(exprName) && exprValue is not null && _namerefs is not null
            && ExtractSingleQuotedOrLiteralValue(exprValue) is { } elemRefTarget && IsNamerefElementTarget(elemRefTarget))
        {
            _namerefs[exprName] = elemRefTarget;
            result = NamerefMark(exprName, elemRefTarget) + "; " + PsBuild.SetStatus(0);
            return true;
        }
        if (isNameref && varName is not null && varName.IndexOf('=') is var neq and > 0
            && IsValidVarName(varName[..neq]) && (IsValidVarName(varName[(neq + 1)..]) || IsNamerefElementTarget(varName[(neq + 1)..])) && _namerefs is not null)
        {
            _namerefs[varName[..neq]] = varName[(neq + 1)..];
            result = NamerefMark(varName[..neq], varName[(neq + 1)..]) + "; " + PsBuild.SetStatus(0);
            return true;
        }
        if (!isPrint && exprName is not null && !isAssoc)
        {
            // Value through the assignment machinery (TryEmitJoinedValue for a $( )); the status
            // is declare's own (0), not the substitution's.
            string value = EmitAssignmentValue(exprValue);
            bool intAttr = cmd.Words.Skip(1).Any(w => GetLiteralValue(w) == "-i");
            result = "$env:" + exprName + " = "
                + (intAttr ? "[string](Invoke-BashArith ([string](" + value + ")))" : CaseAttr(exprName, value))
                + (exprName == "PATH" ? "; " + PsBuild.NormalizePathVariable : "")
                + "; " + PsBuild.SetStatus(0);
            return true;
        }
        if (isPrint)
        {
            // The print forms (`declare -f fn`, `-F`, `-p var`) are a runtime lookup. Through the mapped
            // path so the redirects stay attached — `declare -f fn >/dev/null 2>&1` is the stock
            // "is fn defined?" probe, and routing it to `type` both dropped them and rejected `-f`.
            result = EmitMappedStandalone(cmd, EmitPassthrough("Invoke-BashDeclare", cmd.Words.Skip(1).ToImmutableArray()));
            return true;
        }
        if (varName is null)
            return false;

        // An initializer (`declare -i n=5`, `declare x=hello`) arrives as a
        // single literal `name=value`; split it so we never emit the broken
        // `[int]$global:n=5 = 0`. No `=` → the bare-declaration defaults below.
        int eq = varName.IndexOf('=');
        bool isInt = cmd.Words.Skip(1).Any(w => GetLiteralValue(w) == "-i");
        // A scalar lives where every bash variable read looks: `$env:NAME` (`$x`, `[int]$env:x` in
        // arithmetic). `$global:NAME` was never read back — `declare x=hello; echo $x` printed "".
        if (eq >= 0)
        {
            string declName = varName[..eq];
            string rawVal = varName[(eq + 1)..];
            if (isAssoc)
            {
                result = "$global:" + declName + " = [PsBash.Cmdlets.BashAssoc]::new()";
                return true;
            }
            if (isInt)
            {
                // Integer attribute: bash evaluates the RHS arithmetically
                // (`declare -i n=2+3` -> 5, `declare -i n=x*2` reads $x). A plain
                // integer literal is emitted directly; any expression routes through
                // the shared arithmetic evaluator instead of silently collapsing to 0.
                string intVal = long.TryParse(rawVal, out _)
                    ? PsBuild.SingleQuote(rawVal)
                    : $"[string](Invoke-BashArith {PsBuild.SingleQuote(rawVal)})";
                result = "$env:" + declName + " = " + intVal;
                return true;
            }
            result = "$env:" + declName + " = " + CaseAttr(declName, PsBuild.SingleQuote(rawVal));
            return true;
        }
        if (isAssoc)
        {
            // Re-declaring an existing associative array keeps its entries (bash: `declare -A d` twice).
            result = "if ($global:" + varName + " -isnot [System.Collections.IDictionary]) { $global:" + varName + " = [PsBash.Cmdlets.BashAssoc]::new() }";
            return true;
        }
        result = isInt ? "$env:" + varName + " = '0'"
            // `declare x` on an existing scalar leaves its value alone; only a name with nothing yet becomes the empty array.
            : "if ($null -eq $env:" + varName + " -and $null -eq (Get-Variable -Name " + PsBuild.SingleQuote(varName) + " -Scope Global -ValueOnly -ErrorAction Ignore)) { $global:" + varName + " = @()" + "; [PsBash.Cmdlets.BashShellState]::MarkAttribute(" + PsBuild.SingleQuote(varName) + ", '" + (cmd.Words.Skip(1).Any(w => GetLiteralValue(w) is { Length: > 1 } fl && fl[0] == '-' && fl.Contains('a')) ? "~" : "-") + "')" + " }";
        return true;
    }

    /// <summary>
    /// Split a <c>NAME=value</c> word whose value has expansions (<c>x=$(cmd)</c>, <c>x="$y"</c>):
    /// its first part is a literal starting with a valid name and <c>=</c>. False otherwise.
    /// </summary>
    private static bool TrySplitInitializer(CompoundWord word, out string name, out CompoundWord value)
    {
        name = "";
        value = word;
        if (word.Parts.IsDefaultOrEmpty || word.Parts[0] is not WordPart.Literal first) return false;
        int eq = first.Value.IndexOf('=');
        if (eq <= 0 || !IsValidVarName(first.Value[..eq])) return false;
        name = first.Value[..eq];
        var rest = first.Value[(eq + 1)..];
        var parts = word.Parts.RemoveAt(0);
        if (rest.Length > 0) parts = parts.Insert(0, new WordPart.Literal(rest));
        value = new CompoundWord(parts);
        return true;
    }

    /// <summary><c>NAME+=value</c> as one word whose first part carries the name: splits off the name and the value.</summary>
    private static bool TrySplitAppendInitializer(CompoundWord word, out string name, out CompoundWord value)
    {
        name = "";
        value = word;
        if (word.Parts.IsDefaultOrEmpty || word.Parts[0] is not WordPart.Literal first) return false;
        int eq = first.Value.IndexOf("+=", StringComparison.Ordinal);
        if (eq <= 0 || !IsValidVarName(first.Value[..eq])) return false;
        name = first.Value[..eq];
        var rest = first.Value[(eq + 2)..];
        var parts = word.Parts.RemoveAt(0);
        if (rest.Length > 0) parts = parts.Insert(0, new WordPart.Literal(rest));
        value = new CompoundWord(parts);
        return true;
    }

    private static bool IsValidVarName(string s) =>
        s.Length > 0 && (char.IsAsciiLetter(s[0]) || s[0] == '_')
        && s.AsSpan(1).IndexOfAnyExcept(VarNameChars) < 0;

    private static readonly System.Buffers.SearchValues<char> VarNameChars =
        System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_");

    // Arguments of `:` / `true` / `false` are ignored but still expanded (side effects
    // of `${x:=v}`, `$(cmd)`, `$((i++))`). Plain literals have no effect and are skipped
    // (so a `--help` word can never be parsed as a PowerShell operator).
    private static string EmitNoOpArgPrelude(Command.Simple cmd)
    {
        var sb = new StringBuilder();
        for (int i = 1; i < cmd.Words.Length; i++)
        {
            var lit = GetLiteralValue(cmd.Words[i]);
            if (lit is not null) continue; // a plain literal has no side effects
            bool isLast = i == cmd.Words.Length - 1;
            if (IsUnquotedSplitWord(cmd.Words[i]))
            {
                // `: $x` / `: *.txt`: the word splits / globs, and `$_` becomes the LAST resulting word.
                sb.Append("$__psbash_nv = @(").Append(EmitUnquotedSplitWordArray(cmd.Words[i])).Append("); ");
                if (isLast)
                    sb.Append("$global:BashLastArg = if ($__psbash_nv.Count) { [string]$__psbash_nv[-1] } else { ':' }; ");
                continue;
            }
            if (isLast)
            {
                sb.Append("$global:BashLastArg = [string](").Append(EmitWord(cmd.Words[i])).Append("); ");
                continue;
            }
            sb.Append("[void](").Append(EmitWord(cmd.Words[i])).Append("); ");
        }
        return sb.ToString();
    }

    /// <summary>
    /// bash performs a command's redirections even when the command writes nothing: <c>: &gt; f</c> creates
    /// or truncates f, <c>true &gt;&gt; f</c> creates it, and a target in a missing directory fails the
    /// command (<c>bash: d/f: No such file or directory</c>, status 1) before it runs. Words expand first
    /// (<see cref="EmitNoOpArgPrelude"/>), then the redirections open their targets, left to right.
    /// A target that cannot be opened leaves <c>$global:LASTEXITCODE</c> at 1 (set by the cmdlet), so the
    /// caller sets its own status to 0 BEFORE these statements and keeps the failure after them.
    /// </summary>
    private static string EmitNoOpRedirectTouches(Command.Simple cmd)
    {
        var sb = new StringBuilder();
        // Redirections apply left to right, so `2>/dev/null` silences the failure of the targets AFTER it
        // (`: 2>/dev/null > nodir/f` is quiet; `: > nodir/f 2>/dev/null` reports before the silencing).
        bool stderrSilenced = false;
        foreach (var redirect in cmd.Redirects)
        {
            bool append = redirect.Op is ">>" or "&>>";
            bool opensFile = redirect.Op is ">" or ">>" or "&>" or "&>>"
                || (redirect.Op == ">&" && redirect.Fd == 1);
            if (!opensFile) continue;

            var target = EmitFileRedirectTarget(redirect.Target);
            // `/dev/null` maps to $null; `>&2` / `>&-` / `2>&1` duplicate or close a descriptor, not a file.
            if (target == "$null" || (redirect.Op == ">&" && (IsAllDigits(target) || target == "-")))
            {
                stderrSilenced |= redirect.Fd == 2 && target == "$null";
                continue;
            }
            sb.Append(PsBuild.TouchRedirectTarget(target, append, redirect.Force));
            if (stderrSilenced) sb.Append(" 2>$null");
            sb.Append("; ");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Body of the `$( … )` that stands for <c>:</c> / <c>true</c> / a bare redirection: expand the
    /// arguments, set status 0, open the redirect targets, and — only when a target failed to open —
    /// flip <c>$?</c> so a following <c>||</c> / <c>if</c> sees the failure (the cmdlet already set
    /// <c>$global:LASTEXITCODE</c> to 1).
    /// </summary>
    private static string EmitNoOpSuccessBody(Command.Simple cmd)
    {
        var touches = EmitNoOpRedirectTouches(cmd);
        var sb = new StringBuilder(EmitNoOpArgPrelude(cmd));
        sb.Append("$global:LASTEXITCODE = 0; ").Append(touches);
        // The failure flip must be the LAST statement: any later successful statement would reset `$?`.
        sb.Append(touches.Length > 0
            ? "if ($global:LASTEXITCODE -ne 0) { Write-Error '' -ErrorAction SilentlyContinue } else { [void]$true }"
            : "[void]$true");
        return sb.ToString();
    }

    // Dispatch ladder for the bash-keyword / builtin branches that used to live
    // in one ~200-line else-if chain inside EmitSimple. Order is preserved
    // exactly (it is behavior): `unset`/`trap DEBUG` return immediately (outside
    // the else-if chain, same as before) so other `unset`/`trap` forms still fall
    // through to TryEmitMappedCommand; everything else sets specialResult.
    /// <summary>
    /// <c>cmd &gt;&amp;N</c> / <c>cmd &lt;&amp;N</c> with N &gt;= 3: a descriptor opened by <c>exec</c>. The command's output
    /// is piped to <c>Write-BashFd N</c>, or its stdin is the rest of <c>Read-BashFd N</c>. N of 0-2 keeps the
    /// ordinary redirect handling.
    /// </summary>
    private static bool TryEmitFdRedirect(Command.Simple cmd, out string result)
    {
        result = "";
        if (cmd.Redirects.IsDefaultOrEmpty || cmd.Words.Length < 1 || !cmd.HereDocs.IsDefaultOrEmpty)
            return false;

        int? outFd = null, inFd = null;
        var rest = ImmutableArray.CreateBuilder<Redirect>();
        foreach (var r in cmd.Redirects)
        {
            var target = GetLiteralValue(r.Target);
            if (target is not null && IsAllDigits(target) && int.TryParse(target, out var n) && n >= 3)
            {
                if (r.Op == ">&" && r.Fd == 1) { outFd = n; continue; }
                if (r.Op == "<&" && r.Fd == 0) { inFd = n; continue; }
            }
            rest.Add(r);
        }
        if (outFd is null && inFd is null)
            return false;

        string body = EmitSimple(new Command.Simple(cmd.Words, cmd.EnvPairs, rest.ToImmutable()));
        if (outFd is { } o)
            body = (PsBuild.IsStatementList(body) ? "& { " + body + " }" : body) + $" | Write-BashFd {o}";
        if (inFd is { } i)
            body = $"Read-BashFd {i} | " + body;
        result = body;
        return true;
    }

    /// <summary>
    /// <c>exec</c>. With only redirections (<c>exec 7&lt;f</c>, <c>exec 8&gt;&amp;-</c>) each one opens, duplicates or
    /// closes a numbered descriptor (<c>Invoke-BashExecFd</c>); with a command (<c>exec cmd args</c>) the command
    /// runs and the shell then leaves with its status (a subshell, only itself) — there is no process image to replace.
    /// </summary>
    private static bool TryEmitExec(Command.Simple cmd, out string result)
    {
        // `exec -- cmd` : the `--` ends option parsing, it is not the command.
        if (cmd.Words.Length > 1 && GetLiteralValue(cmd.Words[1]) == "--")
            cmd = new Command.Simple(cmd.Words.RemoveAt(1), cmd.EnvPairs, cmd.Redirects);
        if (cmd.Words.Length > 1)
        {
            var inner = new Command.Simple(cmd.Words.RemoveAt(0), cmd.EnvPairs, cmd.Redirects);
            result = EmitSimple(inner) + "; " + ErrexitExitStatement();
            return true;
        }

        if (cmd.Redirects.IsDefaultOrEmpty)
        {
            result = "$global:LASTEXITCODE = 0";
            return true;
        }

        var steps = new List<string>();
        foreach (var r in cmd.Redirects)
        {
            // `exec N<&-` / `exec N>&-` close; `exec N>&M` duplicates; the rest open a file.
            steps.Add($"Invoke-BashExecFd {r.Fd} {PsBuild.SingleQuote(r.Op)} {EmitArgWord(r.Target)}");
        }
        result = string.Join("; ", steps);
        return true;
    }

    private static bool TryEmitSpecialBuiltins(Command.Simple cmd, out string result)
    {
        result = "";
        if (cmd.Words.Length < 1)
        {
            // A command made only of redirections (`> f`, `>> f`) still opens its targets: it is `:`
            // with redirects. (Before this it emitted ` | Invoke-BashRedirect …` — an empty pipe element,
            // a parse error that failed the whole script.)
            if (cmd.Redirects.IsDefaultOrEmpty || !cmd.HereDocs.IsDefaultOrEmpty)
                return false;
            result = "$(" + EmitNoOpSuccessBody(cmd) + ")";
            return true;
        }

        var cmd0 = GetLiteralValue(cmd.Words[0]);
        // A script-defined function of the same name wins over the special-cased builtin (`cd() { �; }`).
        if (cmd0 is not null && _definedFunctions is { Count: > 0 } && _definedFunctions.Contains(cmd0) && cmd0 is not ("command" or "builtin"))
            return false;

        if (cmd0 == "exec" && cmd.HereDocs.IsDefaultOrEmpty && cmd.EnvPairs.IsEmpty)
            return TryEmitExec(cmd, out result);

        // `unset -n ref` removes the reference itself, so later reads of `ref` are plain again.
        if (cmd0 == "unset" && _namerefs is { Count: > 0 } && cmd.Words.Length >= 3
            && GetLiteralValue(cmd.Words[1]) == "-n")
            foreach (var w in cmd.Words.Skip(2))
                if (GetLiteralValue(w) is { } dropped) { _namerefs.Remove(dropped); _pendingNamerefs?.Remove(dropped); }

        // `unset ref` on a nameref unsets the variable it names (bash); `unset -n ref` removes the reference itself.
        if (cmd0 == "unset" && _namerefs is { Count: > 0 } && cmd.Words.Length >= 2
            && !cmd.Words.Skip(1).Any(w => GetLiteralValue(w) is { } fl && fl.StartsWith('-') && fl.Contains('n'))
            && cmd.Words.Skip(1).Any(w => GetLiteralValue(w) is { } nm && _namerefs.ContainsKey(nm)))
        {
            var words = cmd.Words.Select((w, i) => i > 0 && GetLiteralValue(w) is { } nm && _namerefs.ContainsKey(nm)
                ? new CompoundWord([new WordPart.Literal(ResolveNameref(nm))]) : w).ToImmutableArray();
            cmd = new Command.Simple(words, cmd.EnvPairs, cmd.Redirects);
        }

        // unset PROMPT_COMMAND -> Unregister-BashPromptHook -Name 'prompt-command'
        // Checked early (outside the else-if chain) so that `unset OTHER_VAR` still
        // falls through to TryEmitMappedCommand -> Invoke-BashUnset.
        if (cmd0 == "unset" && cmd.Words.Length >= 2
            && GetLiteralValue(cmd.Words[1]) == "PROMPT_COMMAND")
        {
            result = "Unregister-BashPromptHook -Name 'prompt-command'";
            return true;
        }

        // Bare `readonly` / `export` (optionally `-p`): list the read-only / exported variables as `declare -r` / `declare -x`.
        if (cmd0 is "readonly" or "export" && cmd.EnvPairs.IsEmpty && cmd.HereDocs.IsDefaultOrEmpty
            && cmd.Words.Skip(1).All(w => GetLiteralValue(w) == "-p"))
        {
            var selector = new CompoundWord([new WordPart.Literal(cmd0 == "readonly" ? "-pr" : "-px")]);
            result = EmitMappedStandalone(cmd, EmitPassthrough("Invoke-BashDeclare", ImmutableArray.Create(selector)));
            return true;
        }

        // trap 'CMD' DEBUG  -> Register-BashChpwdHook with first-use warning
        // trap - DEBUG      -> Unregister-BashChpwdHook for the previously registered hook
        // Checked early (outside the else-if chain) so that `trap 'CMD' EXIT` and
        // other non-DEBUG signals still fall through to TryEmitMappedCommand -> Invoke-BashTrap.
        if (cmd0 == "trap" && cmd.Words.Length >= 3 && !_debugTrapMode
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

        // break [N] / continue [N] -> the labelled keyword for the N-th enclosing loop.
        if (cmd0 is "break" or "continue")
            specialResult = EmitLoopControl(cmd0, cmd);

        // return N -> capture exit code for $?
        else if (cmd0 == "return")
        {
            if (cmd.Words.Length >= 2)
            {
                if (TryGetPureLiteralText(cmd.Words[1].Parts, out var retLiteral)
                    && long.TryParse(retLiteral, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var retNum))
                {
                    // The status is the low 8 bits: `return 256` is 0, `return 257` is 1.
                    specialResult = $"$global:LASTEXITCODE = {retNum % 256}; return";
                }
                else
                {
                    // A run-time status: an unquoted variable that expands to nothing is no argument at all
                    // (`return $empty` keeps $?), while anything else must be a number or it is bash's status-2
                    // "numeric argument required"; a number wraps modulo 256.
                    string retValue = IsPureUnquotedVarWord(cmd.Words[1])
                        ? "$__psb_rs = @(" + EmitUnquotedSplitWordArray(cmd.Words[1]) + "); $__psb_r = if ($__psb_rs.Count) { [string]$__psb_rs[0] } else { $null }"
                        : TryGetPureLiteralText(cmd.Words[1].Parts, out var retText)
                            ? "$__psb_r = " + PsBuild.SingleQuote(retText)
                            : "$__psb_r = [string](" + EmitWord(cmd.Words[1]) + ")";
                    specialResult = retValue + "; if ($null -ne $__psb_r) { if ($__psb_r -match '^-?[0-9]+$') { $global:LASTEXITCODE = (([long]$__psb_r % 256) + 256) % 256 } "
                        + "else { Write-Error ('return: ' + $__psb_r + ': numeric argument required') -ErrorAction Continue; $global:LASTEXITCODE = 2 } }; return";
                }
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
        // `:` is the same builtin as `true`; all three ignore their arguments but
        // bash still EXPANDS them first (`: ${x:=5}` assigns, `: $(cmd)` runs cmd),
        // so the expansions run as `[void](word)` statements ahead of the status.
        else if (cmd0 is "true" or ":")
            specialResult = "$(" + EmitNoOpSuccessBody(cmd) + ")";
        else if (cmd0 == "false")
        {
            // Redirect targets are opened (created / truncated) even though `false` then fails.
            var argPrelude = EmitNoOpArgPrelude(cmd) + EmitNoOpRedirectTouches(cmd);
            if (_context == TranspileContext.Eval)
                // try/catch on (1/0) is the mechanism that flips $? to $false so bash `&&` short-circuits;
                // Write-Error can't be used here because it propagates as a terminating error in eval scope.
                // No errexit throw: eval/source'd code runs without per-statement checks (intentional-differences.md),
                // and the throw escaped the eval cmdlet as "ps-bash: PsBash.FalseErrexit", killing the shell, even
                // for a member of an && / || list that bash exempts.
                specialResult = "$(" + argPrelude + "$global:LASTEXITCODE = 1; try { [void](1/0) } catch { })";
            else if (_andOrChainDepth > 0 || _errexitMode)
                // Inside an && / || list: bash exempts every list member from
                // errexit, so `set -e; false || true` must survive. Non-terminating
                // Write-Error still flips $? for the chain operator. Under _errexitMode the
                // statement's own errexit check (EmitStatement) decides whether to leave —
                // it honours runtime suppression and subshell scope, which a bare `exit` here
                // did not (`f() { false; }; set -e; f || echo caught` aborted).
                specialResult = "$(" + argPrelude + "$global:LASTEXITCODE = 1; Write-Error '' -ErrorAction SilentlyContinue)";
            else
                // A standalone `false` under errexit must ABORT the script (bash
                // `set -e; false; echo after` prints nothing) AND leave no trace on
                // stderr. A `throw` would be caught by the worker and printed as
                // "ps-bash: PsBash.FalseErrexit" (bash prints nothing), so abort
                // with `exit $?` instead — outside a subshell this ends the script
                // with the failing status, and the EXIT trap's try/finally still
                // runs. Without errexit, keep the non-terminating Write-Error that
                // flips $? for a following && / || use.
                specialResult = "$(" + argPrelude + "$global:LASTEXITCODE = 1; if ($global:__BashErrexit) { exit $global:LASTEXITCODE } else { Write-Error '' -ErrorAction SilentlyContinue })";
        }

        // Inside a subshell, `exit` leaves only the subshell and sets $? in the
        // parent (`(exit 7); echo $?` prints 7 and continues). A bare PowerShell
        // `exit` terminated the whole shell, so the rest of the script never ran.
        else if (cmd0 == "exit" && _subshellDepth > 0)
        {
            specialResult = cmd.Words.Length >= 2
                ? $"$global:LASTEXITCODE = {EmitExitCodeValue(cmd.Words[1])}; return"
                : "return";
        }

        // A bare `exit` leaves with the status of the last command; inside an EXIT trap handler that is the status
        // from before the handler ran (see BashTranspiler.TrapEpilogueClose).
        // `exit 7 8 9`: "too many arguments", status 1, and the shell stays (bash 5.2).
        else if (cmd0 == "exit" && cmd.Words.Length > 2 && _context != TranspileContext.Eval)
            specialResult = $"Write-Error 'exit: too many arguments' -ErrorAction Continue; {PsBuild.SetStatus(1)}";

        // `exit word` with a literal non-numeric word: "numeric argument required", and the shell leaves with 2.
        else if (cmd0 == "exit" && cmd.Words.Length == 2 && GetLiteralValue(cmd.Words[1]) is { } exitLit
                 && !System.Text.RegularExpressions.Regex.IsMatch(exitLit, @"^[+-]?[0-9]+$"))
            specialResult = $"Write-Error {PsBuild.SingleQuote("exit: " + exitLit + ": numeric argument required")} -ErrorAction Continue; $global:LASTEXITCODE = 2; exit 2";

        else if (cmd0 == "exit" && cmd.Words.Length == 1)
            specialResult = "exit $(if ($null -ne $global:__BashPreTrapStatus) { $global:__BashPreTrapStatus } else { $global:LASTEXITCODE })";

        else if (cmd0 == "exit" && cmd.Words.Length == 2
                 && IsPureUnquotedVarWord(cmd.Words[1]))
            specialResult = EmitExitWithUnquotedVar(cmd.Words[1]);

        // `exit N` also records N as the status, so an EXIT trap (`trap 'echo rc=$?' EXIT`) sees it: PowerShell's
        // `exit` leaves $global:LASTEXITCODE as it was.
        else if (cmd0 == "exit" && cmd.Words.Length == 2 && _context != TranspileContext.Eval)
            specialResult = $"$global:LASTEXITCODE = {EmitExitCodeValue(cmd.Words[1])}; exit $global:LASTEXITCODE";

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

        // `local` / `local -p [NAME...]` inside a function: list the function's locals as `declare -- n="v"` lines.
        else if (cmd0 == "local" && _functionDepth > 0 && _functionLocalNames is { Count: > 0 } fnLocals && IsLocalListing(cmd))
            specialResult = EmitMappedStandalone(cmd, EmitPassthrough("Invoke-BashDeclare",
                ImmutableArray.Create(new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal("-p")))).AddRange(
                    (cmd.Words.Length > 1 && cmd.Words.Skip(1).Any(w => GetLiteralValue(w) is { } s && s != "-p")
                        ? cmd.Words.Skip(1).Where(w => GetLiteralValue(w) != "-p")
                        : fnLocals.Select(n => new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal(n))))))));

        // Bare `set` / `declare` / `typeset`: list the variables as `name=value` lines.
        else if (cmd0 is "set" or "declare" or "typeset" && cmd.Words.Length == 1)
            specialResult = EmitMappedStandalone(cmd, EmitPassthrough("Invoke-BashDeclare",
                ImmutableArray.Create(new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal("--bare-list"))))));

        else if (cmd0 == "set" && cmd.Words.Length >= 2)
            specialResult = EmitSet(cmd);

        else if ((cmd0 == "source" || cmd0 == ".") && cmd.Words.Length == 1)
            specialResult = "Write-Error '" + cmd0 + ": filename argument required' -ErrorAction Continue; $global:LASTEXITCODE = 2";

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
        {
            // EmitMappedStandalone attaches the command's redirects itself.
            result = EmitMappedStandalone(cmd, mapped);
            return true;
        }

        if (specialResult is not null)
        {
            // cd / read / eval / readonly / set / source emit their own statement text, so the redirect
            // tail (`eval '…' >f`, `source rc 2>/dev/null`, `cd d >/dev/null`) was silently dropped.
            // Dot-source keeps their side effects (variables, functions, cwd) in the caller's scope.
            result = cmd0 is "cd" or "read" or "eval" or "readonly" or "set" or "source" or "."
                && !cmd.Redirects.IsDefaultOrEmpty
                    ? PsBuild.WithRedirectTail(specialResult, tail => AppendRedirectTail(tail, cmd.Redirects))
                    : specialResult;
            return true;
        }
        return false;
    }

    // readonly VAR=val -> Set-Variable -Name VAR -Value val -Option Constant
    /// <summary>
    /// <c>readonly NAME=value …</c>: assigns like any scalar (<c>$env:NAME</c>, where every read looks),
    /// then sets readonly's own status 0. It used to create a CONSTANT PowerShell global that no read
    /// ever reached (<c>readonly r=5; echo $r</c> printed "") and skipped any value with an expansion.
    /// Read-only-ness itself is not enforced (an env var cannot be a constant) — see
    /// intentional-differences.md.
    /// </summary>
    private static string EmitReadonly(Command.Simple cmd)
    {
        var roSb = new StringBuilder();
        bool roBad = false;
        for (int i = 1; i < cmd.Words.Length; i++)
        {
            var word = cmd.Words[i];
            var val = GetLiteralValue(word);
            if (val is not null && val.StartsWith('-')) continue; // flags like -p, -r
            // `readonly b[7]=8`: readonly takes no subscript. (`[7]` lexes as a glob part.)
            string? roRaw = val ?? (word.Parts.All(p => p is WordPart.Literal or WordPart.GlobPart)
                ? string.Concat(word.Parts.Select(p => p is WordPart.Literal l ? l.Value : ((WordPart.GlobPart)p).Pattern)) : null);
            if (roRaw is not null && (roRaw.IndexOf('=') is var eqAt && eqAt < 0 ? roRaw : roRaw[..eqAt]) is var roNm && roNm.Contains('['))
            {
                roSb.Append("Write-Error ").Append(PsBuild.SingleQuote("readonly: `" + roNm.TrimEnd('+') + "': not a valid identifier")).Append(" -ErrorAction Continue; ");
                roBad = true;
                continue;
            }
            if (val is not null)
            {
                int eq = val.IndexOf('=');
                if (eq <= 0)
                {
                    roSb.Append("[PsBash.Cmdlets.BashShellState]::MarkReadonly(").Append(PsBuild.SingleQuote(val)).Append("); ");
                    _readonlyVars?.Add(val);
                    continue;
                }
                // `readonly NAME+=text` appends to the current value.
                bool append = val[eq - 1] == '+';
                string roName = append ? val[..(eq - 1)] : val[..eq];
                roSb.Append("[PsBash.Cmdlets.BashShellState]::MarkReadonly(").Append(PsBuild.SingleQuote(roName)).Append("); ");
                _readonlyVars?.Add(roName);
                roSb.Append("$env:").Append(roName).Append(" = ").Append(append ? "$env:" + roName + " + " : "")
                    .Append(PsBuild.SingleQuote(val[(eq + 1)..])).Append("; ");
            }
            else if (TrySplitAppendInitializer(word, out var appName, out var appValue))
            {
                roSb.Append("$env:").Append(appName).Append(" = $env:").Append(appName).Append(" + ")
                    .Append(EmitAssignmentValue(appValue)).Append("; ");
                roSb.Append("[PsBash.Cmdlets.BashShellState]::MarkReadonly(").Append(PsBuild.SingleQuote(appName)).Append("); ");
            }
            else if (TrySplitInitializer(word, out var name, out var value))
            {
                roSb.Append("$env:").Append(name).Append(" = ").Append(EmitAssignmentValue(value)).Append("; ");
                roSb.Append("[PsBash.Cmdlets.BashShellState]::MarkReadonly(").Append(PsBuild.SingleQuote(name)).Append("); ");
            }
        }
        return roSb.Append(PsBuild.SetStatus(roBad ? 1 : 0)).ToString();
    }

    // set -- a b c -> reset positional parameters
    // set -e / set -o errexit -> $ErrorActionPreference = 'Stop'
    // set -x / set -o xtrace -> Set-PSDebug -Trace 1
    // set -u / set -o nounset -> Set-StrictMode -Version Latest
    /// <summary>
    /// Turn errexit on. The suppression counter (<see cref="PsBuild.ErrexitSuppressVar"/>) is created
    /// only when absent: <c>set -e</c> run inside an exempt context must not reset it, and reading an
    /// unset variable throws under <c>set -u</c> (Set-StrictMode).
    /// </summary>
    private const string SetErrexitOn =
        "$global:__BashErrexit = $true; if (-not (Test-Path variable:global:__BashErrexitSuppress)) { "
        + PsBuild.ErrexitSuppressVar + " = 0 }";

    /// <summary>
    /// Turn xtrace on. <c>Set-PSDebug</c> is session-wide and has no query, so the flag records it for
    /// the subshell scope (<c>BashShellState</c>), which re-applies the parent's level on the way out.
    /// </summary>
    private const string SetXtraceOn = "Set-PSDebug -Trace 1; $global:__BashXtrace = $true";

    /// <summary>
    /// Pulls the options ps-bash tracks at runtime (<c>pipefail</c>, <c>nounset</c>/<c>-u</c>) out of a
    /// <c>set</c> argument list, in any bundling (<c>-eu</c>, <c>-euo pipefail</c>, <c>-o nounset</c>);
    /// the remaining options go back through the normal <c>set</c> handling.
    /// </summary>
    private static bool TrySplitRuntimeOptions(List<string?> args, out List<(string Flag, bool On)> effects, out List<string?> rest)
    {
        effects = new List<(string, bool)>();
        rest = new List<string?>();
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a is null || a == "--")
            {
                for (; i < args.Count; i++) rest.Add(args[i]);
                break;
            }
            if (a.Length < 2 || (a[0] != '-' && a[0] != '+') || a[1] == '-' || !a.Skip(1).All(char.IsAsciiLetter))
            {
                rest.Add(a);
                continue;
            }
            bool on = a[0] == '-';
            var letters = new StringBuilder();
            for (int k = 1; k < a.Length; k++)
            {
                char c = a[k];
                if (c == 'u') { effects.Add(("__BashNounset", on)); continue; }
                if (c == 'C') { effects.Add(("__BashNoclobber", on)); continue; }
                if (c == 'o' && k == a.Length - 1 && i + 1 < args.Count && args[i + 1] is "pipefail" or "nounset" or "noclobber")
                {
                    effects.Add((args[i + 1] switch { "pipefail" => "__BashPipefail", "noclobber" => "__BashNoclobber", _ => "__BashNounset" }, on));
                    i++;
                    continue;
                }
                letters.Append(c);
            }
            if (letters.Length > 0) rest.Add(a[0] + letters.ToString());
        }
        return effects.Count > 0;
    }
    /// <summary>
    /// Options that change nothing ps-bash models (errtrace, functrace, histexpand, braceexpand, hashall, monitor, allexport -- every\n    /// variable already lives in the exported environment, so set -a; . .env; set +a just works):
    /// `set -eEuo pipefail` is the standard script preamble, so they are accepted and dropped rather than refused.
    /// </summary>
    private static List<string?> StripInertSetOptions(List<string?> args)
    {
        var kept = new List<string?>();
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a is null || a == "--") { for (; i < args.Count; i++) kept.Add(args[i]); break; }
            if (a is "-o" or "+o" && i + 1 < args.Count && args[i + 1] is "errtrace" or "functrace" or "histexpand" or "braceexpand" or "hashall" or "monitor" or "allexport")
            { i++; continue; }
            if (a.Length >= 2 && (a[0] == '-' || a[0] == '+') && a[1] != '-' && a.Skip(1).All(char.IsAsciiLetter))
            {
                var letters = new string(a.Skip(1).Where(c => c is not ('E' or 'T' or 'H' or 'B' or 'h' or 'm' or 'a')).ToArray());
                if (letters.Length == 0) continue;
                kept.Add(a[0] + letters);
                continue;
            }
            kept.Add(a);
        }
        return kept;
    }

    // Leading options followed by positional parameters (`set -o errexit a b c`, `set -u -- x y z`):
    // optEnd = words that are options, posStart = first positional word (after any `--`).
    private static bool TrySplitOptionsFromPositionals(List<string?> args, out int optEnd, out int posStart)
    {
        optEnd = posStart = 0;
        int i = 0;
        while (i < args.Count)
        {
            var a = args[i];
            if (a is null) return false;
            if (a == "--") { optEnd = i; posStart = i + 1; return optEnd > 0; }
            if (a.Length > 1 && (a[0] == '-' || a[0] == '+'))
            {
                i += a.EndsWith('o') ? 2 : 1;
                continue;
            }
            optEnd = posStart = i;
            return optEnd > 0;
        }
        return false;
    }

    private static string EmitSetPositionals(List<CompoundWord> positionalWords)
    {
        if (positionalWords.Count == 0)
            return "$global:BashPositional = @()";
        else
        {
            // Quote each element so that bare literals like `a`, `b`, `c`
            // are treated as strings, not variable/command references in PS.
            // `set -- *.txt`: a glob word expands into zero or more parameters, which the comma
            // form cannot splice -- such a list is built as `@( item; item; ... )` instead.
            bool anyGlob = positionalWords.Any(IsUnquotedSplitWord);
            var items = string.Join(anyGlob ? "; " : ", ", positionalWords.Select(w =>
            {
                if (anyGlob && IsUnquotedSplitWord(w))
                    return EmitUnquotedSplitWordArray(w);
                var emitted = EmitWord(w);
                // Already quoted (starts with ' or "), a variable ($), subexpr ((), or array (@): pass through.
                if (BarePrefixDoubleQuotedItem.IsMatch(emitted)) return FormatForItem(emitted);
                if (emitted.Length > 0 && emitted[0] is '\'' or '"' or '$' or '(' or '@')
                    return emitted;
                // Bare literal — wrap in single quotes.
                return PsBuild.SingleQuote(emitted);
            }));
            return $"$global:BashPositional = @({items})";
    }
    }

    // `set -o NAME` names bash knows (the rest are "invalid option name").
    private static readonly HashSet<string> BashOptionNames = new(StringComparer.Ordinal)
    {
        "allexport", "braceexpand", "emacs", "errexit", "errtrace", "functrace", "hashall", "histexpand", "history",
        "ignoreeof", "interactive-comments", "keyword", "monitor", "noclobber", "noexec", "noglob", "nolog", "notify",
        "nounset", "onecmd", "physical", "pipefail", "posix", "privileged", "verbose", "vi", "xtrace",
    };

    /// <summary>
    /// A lone <c>+</c> or <c>-</c> among <c>set</c>'s options (bash's set builtin): <c>+</c> is an ignored flag; <c>-</c>
    /// ends the options, turns off -x / -v, and replaces the positional parameters only when words follow it.
    /// <c>set + -</c> keeps $@, <c>set -x - -v</c> leaves xtrace off with $1 = -v, <c>set + a b</c> is <c>set a b</c>.
    /// Null when the option words hold neither.
    /// </summary>
    private static string? TryEmitSetLoneDashOrPlus(Command.Simple cmd, List<string?> literalArgs)
    {
        int k = 0;
        bool sawLone = false;
        for (; k < literalArgs.Count; k++)
        {
            var a = literalArgs[k];
            if (a is null || a == "--") break;
            if (a == "+") { sawLone = true; continue; }
            if (a == "-") { sawLone = true; break; }
            if (a.Length >= 2 && (a[0] == '-' || a[0] == '+')) { if (a is "-o" or "+o") k++; continue; }
            break;
        }
        if (!sawLone) return null;
        var optWords = cmd.Words.Skip(1).Take(Math.Min(k, literalArgs.Count)).Where((w, j) => literalArgs[j] != "+").ToList();
        string? optText = optWords.Count == 0 ? null : EmitSet(cmd with { Words = optWords.Prepend(cmd.Words[0]).ToImmutableArray() });
        string tail;
        if (k < literalArgs.Count && literalArgs[k] == "-")
        {
            var rest = cmd.Words.Skip(2 + k).ToList();
            tail = "$global:__BashXtrace = $false; Set-PSDebug -Off; " + (rest.Count == 0 ? PsBuild.SetStatus(0) : EmitSetPositionals(rest));
        }
        else if (k < literalArgs.Count)
            tail = EmitSet(cmd with { Words = cmd.Words.Skip(1 + k).Prepend(cmd.Words[0]).ToImmutableArray() }) ?? PsBuild.SetStatus(0);
        else
            tail = PsBuild.SetStatus(0);
        return (optText is null ? "" : optText + "; ") + tail;
    }

    private static string? EmitSet(Command.Simple cmd, List<string?>? argsOverride = null)
    {
        string? specialResult = null;
        var literalArgs = argsOverride ?? cmd.Words.Skip(1).Select(TryGetStaticArgValue).ToList();
        if (argsOverride is null && TryEmitSetLoneDashOrPlus(cmd, literalArgs) is { } lone) return lone;
        // `set -o errexit a b c` / `set -u -- x`: leading options, then positional parameters. Apply the
        // options first, then assign the rest exactly as `set -- rest...` would.
        if (argsOverride is null && TrySplitOptionsFromPositionals(literalArgs, out int optEnd, out int posStart))
        {
            var optText = EmitSet(cmd with { Words = cmd.Words.Take(1 + optEnd).ToImmutableArray() });
            var positionalText = EmitSetPositionals(cmd.Words.Skip(1 + posStart).ToList());
            return (optText is null ? "" : optText + "; ") + positionalText;
        }
        // `set -o vi` / `set +o emacs`: option names ps-bash only remembers (for `shopt -po`) go to the shopt table.
        if (argsOverride is null && literalArgs.Count == 2 && literalArgs[0] is "-o" or "+o" && literalArgs[1] is { } remembered
            && BashOptionNames.Contains(remembered) && remembered is not ("errexit" or "xtrace" or "pipefail" or "noglob" or "nounset" or "noclobber" or "noexec" or "onecmd"))
            return "Invoke-BashShopt '-o' " + (literalArgs[0] == "-o" ? "'-s'" : "'-u'") + " " + PsBuild.SingleQuote(remembered);
        // errtrace (`set -E` / `set -o errtrace`): the ERR trap also fires inside functions and subshells.
        if (argsOverride is null && (literalArgs.Count == 1 && literalArgs[0] is "-E" or "+E"
            || literalArgs.Count == 2 && literalArgs[0] is "-o" or "+o" && literalArgs[1] == "errtrace"))
            return "$global:__BashErrtrace = " + (literalArgs[0]![0] == '-' ? "$true" : "$false") + "; " + PsBuild.SetStatus(0);
        // `set -o` / `set +o` alone list the options (the shopt -o table, same two formats).
        if (argsOverride is null && literalArgs.Count == 1 && literalArgs[0] is "-o" or "+o")
            return literalArgs[0] == "-o" ? "Invoke-BashShopt '-o'" : "Invoke-BashShopt '-po'";
        if (literalArgs.Count > 0)
        {
            var stripped = StripInertSetOptions(literalArgs);
            if (stripped.Count == 0) return PsBuild.SetStatus(0);
            literalArgs = stripped;
        }
        if (argsOverride is null && TrySplitRuntimeOptions(literalArgs, out var runtimeEffects, out var otherOptions))
        {
            var setFlag = string.Join("; ", runtimeEffects.Select(e => "$global:" + e.Flag + " = " + (e.On ? "$true" : "$false")));
            if (otherOptions.Count == 0) return setFlag + "; " + PsBuild.SetStatus(0);
            var otherText = EmitSet(cmd, otherOptions);
            return otherText is null ? setFlag + "; " + PsBuild.SetStatus(0) : setFlag + "; " + otherText;
        }
        // `set +efuC` / `set -e -f`: all short options, several letters in all. Each letter is its own `set`.
        if (literalArgs.Count > 0
            && literalArgs.All(a => a is { Length: >= 2 } w && (w[0] == '-' || w[0] == '+') && w[1] != '-' && w.Skip(1).All(c => char.IsAsciiLetter(c) && c != 'o'))
            && literalArgs.Sum(a => a!.Length - 1) > 1)
        {
            var single = new List<string>();
            foreach (var a in literalArgs)
                foreach (var c in a!.Skip(1))
                {
                    var one = EmitSet(cmd, new List<string?> { a![0].ToString() + c });
                    if (one is not null) single.Add(one);
                }
            return single.Count == 0 ? PsBuild.SetStatus(0) : string.Join("; ", single) + "; " + PsBuild.SetStatus(0);
        }
        // `set a b c` / `set $@ $res`: a first word that is not an option starts the positional
        // parameters, exactly like `set -- a b c`.
        bool implicitDashDash = argsOverride is null && literalArgs.Count > 0
            && ((literalArgs[0] is null && IsUnquotedPositionalListWord(cmd.Words[1])) || (literalArgs[0] is not null &&!literalArgs[0]!.StartsWith('-') && !literalArgs[0]!.StartsWith('+')));
        if (implicitDashDash) literalArgs = new List<string?>(literalArgs.Prepend("--"));
        int dashDashIdx = literalArgs.IndexOf("--");
        var optionWords = literalArgs.TakeWhile(a => a != "--" &&
            (a is null || a.StartsWith('-') || a.StartsWith('+'))).ToList();
        // These combined forms used to silently drop one of their requested effects.
        // Keep the supported standalone forms explicit until option composition is implemented.
        if (dashDashIdx > 0 ||
            (optionWords.Any(a => a is "-o" or "+o") && literalArgs.Count != 2) ||
            (optionWords.Any(a => a?.StartsWith('+') == true) && optionWords.Count > 1))
            throw new ParseException("set: combined shell option forms are not supported; use separate set commands", 1, 1, "shell-option");
        // `set -o NOSUCH` is a run-time error in bash (status 2, the script goes on), not a refusal.
        for (int i = 0; i + 1 < literalArgs.Count && literalArgs[i] != "--"; i++)
            if (literalArgs[i] is "-o" or "+o" && literalArgs[i + 1] is { } optName && !BashOptionNames.Contains(optName))
                return $"Write-Error {PsBuild.SingleQuote($"set: {optName}: invalid option name")} -ErrorAction Continue; {PsBuild.SetStatus(2)}";
        // Refuse every unsupported option before any part of the script runs.
        for (int i = 0; i < literalArgs.Count && literalArgs[i] != "--"; i++)
        {
            var arg = literalArgs[i];
            if (arg is null)
                throw new ParseException("set: dynamic shell options are not supported by ps-bash; use set -- for positional parameters", 1, 1, "shell-option");
            if (!arg.StartsWith('-') && !arg.StartsWith('+')) break;
            if (arg is "-o" or "+o")
            {
                var name = literalArgs[++i];
                if (name is not ("errexit" or "xtrace" or "pipefail" or "noglob"))
                    throw new ParseException($"set: option '{name ?? "dynamic name"}' is not supported by ps-bash", 1, 1, "shell-option");
                continue;
            }
            if (arg is "+e" or "+x" or "+f") continue;
            if (arg.StartsWith('-') && arg.Length > 1 && arg.Skip(1).All(c => c is 'e' or 'x' or 'f')) continue;
            throw new ParseException($"set: option '{arg}' is not supported by ps-bash", 1, 1, "shell-option");
        }
        if (dashDashIdx >= 0)
        {
            // The `--` position comes from the ORIGINAL words: an inert/runtime option may have been dropped from literalArgs.
            int dashWord = implicitDashDash ? -1 : cmd.Words.Select((w, k) => (w, k)).Skip(1).First(t => GetLiteralValue(t.w) == "--").k;
            specialResult = EmitSetPositionals(cmd.Words.Skip(implicitDashDash ? 1 : dashWord + 1).ToList());
        }
        else
        {
            var args = literalArgs;
            // noglob (`set -f`, `set -o noglob`): the glob cmdlets keep a pattern literal while the flag is set.
            if (args.Count == 1 && args[0] == "-f" || args.SequenceEqual(new[] { "-o", "noglob" }))
                return "$global:__BashNoglob = $true; " + PsBuild.SetStatus(0);
            if (args.Count == 1 && args[0] == "+f" || args.SequenceEqual(new[] { "+o", "noglob" }))
                return "$global:__BashNoglob = $false; " + PsBuild.SetStatus(0);
            bool longOpt = args.Any(a => a == "-o");
            // `set +e` / `set +o errexit` turn errexit back off (the runtime checks read the flag).
            bool errexitOff = args.Any(a => a is not null && a.StartsWith('+') && a.Contains('e'))
                || args.SkipWhile(a => a != "+o").Skip(1).FirstOrDefault() == "errexit";
            if (errexitOff && !longOpt && !args.Any(a => a is not null && a.StartsWith('-')))
                specialResult = "$ErrorActionPreference = 'Continue'; $global:__BashErrexit = $false";
            else if (args.Contains("+x"))
                specialResult = "Set-PSDebug -Off; $global:__BashXtrace = $false";
            // `set +o NAME` (other than errexit, above): xtrace off, anything else a no-op — never the
            // bare `set` fallthrough (PowerShell's Set-Variable alias).
            else if (!longOpt && args.Contains("+o"))
                specialResult = args.SkipWhile(a => a != "+o").Skip(1).FirstOrDefault() == "xtrace"
                    ? "Set-PSDebug -Off; $global:__BashXtrace = $false"
                    : PsBuild.SetStatus(0);
            else if (longOpt)
            {
                var optVal = args.SkipWhile(a => a != "-o").Skip(1).FirstOrDefault();
                if (optVal == "errexit") specialResult = "$ErrorActionPreference = 'Stop'; " + SetErrexitOn;
                else if (optVal == "xtrace") specialResult = SetXtraceOn;
                // pipefail is the explicit, documented architecture exclusion.
                else specialResult = PsBuild.SetStatus(0);
            }
            else
            {
                var flags = args.Where(a => a is not null && a.StartsWith('-') && !a.StartsWith("--")).ToList();
                bool e = flags.Any(f => f!.Contains('e'));
                bool x = flags.Any(f => f!.Contains('x'));
                var parts = new List<string>();
                if (e) parts.AddRange(new[]{"$ErrorActionPreference = 'Stop'", SetErrexitOn});
                if (x) parts.Add(SetXtraceOn);
                if (flags.Any(f => f!.Contains('f'))) parts.Add("$global:__BashNoglob = $true");
                if (parts.Count > 0) specialResult = string.Join("; ", parts);
            }
        }
        return specialResult;
    }

    // source FILE / . FILE -> . $(Invoke-BashSource -AsScriptBlock FILE args): the cmdlet resolves,
    // transpiles and sets $1.. (restored by the block's own finally); the CALLER dot-sources the
    // block, so its output streams (run inside the cmdlet it arrived only once the file finished).
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
        // The switch goes right after the cmdlet name: a word-split operand wraps the call in
        // `& { $__bashsplat0 = …; Invoke-BashSource @__bashsplat0 }`, where a trailing switch would be
        // an argument of the script block. The operands are ordered-arg quoted, so none binds to it.
        const string Cmdlet = "Invoke-BashSource";
        // (Spliced by EmitPassthrough, not searched for in the text: a file named Invoke-BashSource.sh.)
        return ". $(" + EmitPassthrough(Cmdlet, cmd.Words.RemoveAt(0), cmdletSwitch: " -AsScriptBlock") + ")";
    }

    private static string EmitMappedStandalone(Command.Simple cmd, string mapped)
    {
        if (cmd.Redirects.IsEmpty)
            return mapped;

        // bash expands the words BEFORE it opens (and truncates) the redirect target: `echo $(cat f) x > f` reads the old f.
        // The redirect stage opens its file as the pipeline starts, so run the command to completion first.
        bool wordsRunCommands = false;
        foreach (var w in cmd.Words) wordsRunCommands |= ContainsCommandSub(w.Parts);
        bool onlyStdoutFiles = true;
        foreach (var r in cmd.Redirects) onlyStdoutFiles &= r.Fd == 1 && r.Op is ">" or ">>";
        if (wordsRunCommands && onlyStdoutFiles) mapped = "@(" + mapped + ")";
        var mappedSb = new StringBuilder(mapped);
        EmitPipeTargetRedirects(cmd, mappedSb);
        return mappedSb.ToString();
    }

    /// <summary>The text of a command word that is all literal/glob/quoted text and starts with an unquoted `[` (other than
    /// `[` and `[[` themselves): bash looks that up as a command name, PowerShell would read it as a type literal.</summary>
    private static string? StaticBracketCommandName(CompoundWord word)
    {
        if (word.Parts.IsEmpty) return null;
        var sb = new StringBuilder();
        foreach (var part in word.Parts)
        {
            switch (part)
            {
                case WordPart.Literal l: sb.Append(l.Value); break;
                case WordPart.GlobPart g: sb.Append(g.Pattern); break;
                case WordPart.EscapedLiteral e: sb.Append(e.Value); break;
                case WordPart.SingleQuoted q: sb.Append(q.Value); break;
                default: return null;
            }
        }
        string name = sb.ToString();
        bool unquotedLead = word.Parts[0] is WordPart.Literal or WordPart.GlobPart;
        return unquotedLead && name.Length >= 1 && name[0] == '[' && name != "[[" ? name : null;
    }

    // The general fallback: no special form matched, emit the command word(s),
    // arguments (with env-prefix save/restore and unquoted-var splat handling),
    // and any trailing redirects.
    private static bool IsEmptyQuotedWord(CompoundWord w)
    {
        if (w.Parts.IsEmpty) return false;
        foreach (var part in w.Parts)
        {
            if (part is WordPart.SingleQuoted { Value.Length: 0 }) continue;
            if (part is WordPart.DoubleQuoted { Parts.IsEmpty: true }) continue;
            return false;
        }
        return true;
    }
    private static string EmitGeneralCommand(Command.Simple cmd)
    {
        var sb = new StringBuilder();
        // An empty command word ('' , "" ) names no command: bash reports ": command not found" (127).
        if (cmd.EnvPairs.IsEmpty && cmd.Words.Length > 0 && IsEmptyQuotedWord(cmd.Words[0]))
            return PsBuild.StderrLine("ps-bash: : command not found") + "; $global:LASTEXITCODE = 127";
        // A command word that is only a command substitution: the substitution runs, and when it expands to
        // nothing no command runs and the status is the substitution's (+""+"true"+""+, $(false), $(exit 42)).
        if (cmd.EnvPairs.IsEmpty && cmd.Words.Length == 1 && cmd.Words[0].Parts.Length == 1
            && cmd.Words[0].Parts[0] is WordPart.CommandSub)
            return "$__psbash_cs = " + EmitAssignmentValue(cmd.Words[0]) + "; if ($__psbash_cs) { $__psbash_cw = @(ConvertTo-BashWords $__psbash_cs); & (Resolve-BashCommand $__psbash_cw[0]) @($__psbash_cw | Select-Object -Skip 1) }";

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
            if (cmd.EnvPairs.Any(p => p.Name == "PATH"))
                sb.Append(PsBuild.NormalizePathVariable).Append("; ");
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
        bool isScriptCommand =
            cmd.Words.Length > 0 && IsLocalShellScriptCommand(cmd.Words[0]);
        // A literal command name starting with - or + (`--`, `-x`) would read as a PowerShell operator or parameter.
        // A command name starting with `[` (but not `[` / `[[` themselves) would read as a PowerShell type literal.
        string? dashName = cmd.Words.Length > 0 && GetLiteralValue(cmd.Words[0]) is { Length: > 0 } dn && dn[0] is '-' or '+'
            ? GetLiteralValue(cmd.Words[0]) : null;
        if (dashName is null && cmd.Words.Length > 0 && StaticBracketCommandName(cmd.Words[0]) is { } bracketName)
            dashName = bracketName;
        // A command word that is a PowerShell number literal (`127`, `0`, `1e3`) would evaluate to itself, not be looked up.
        if (dashName is null && cmd.Words.Length > 0)
        {
            string? numName = GetLiteralValue(cmd.Words[0]);
            if (numName is not null && System.Text.RegularExpressions.Regex.IsMatch(numName, @"^[0-9][0-9A-Za-z_.]*$")) dashName = numName;
        }
        // A command word that is a PowerShell keyword (`var`, `try`, `class`, `parallel`) would start a PowerShell statement.
        // A bash reserved word or `[[` that the parser left as a command word (after an environment prefix: `FOO=bar for`) is a name too.
        if (dashName is null && cmd.Words.Length > 0 && GetLiteralValue(cmd.Words[0]) is { Length: > 0 } kwName
            && (PsKeywordCommandNames.Contains(kwName)
                || kwName is "[[" or "for" or "while" or "until" or "if" or "case" or "select" or "do" or "done" or "then" or "fi" or "esac" or "elif" or "else" or "in"))
            dashName = kwName;
        // Any other bare name with a character PowerShell reads as syntax (`{ls` printed itself, `a,b` built an array,
        // `%x` ran ForEach-Object) is a name too: bash looks it up and says `{ls: command not found` (127).
        if (dashName is null && cmd.Words.Length > 0 && cmd.Words[0].Parts is [WordPart.Literal { Value: { Length: > 0 } oddName }]
            && !System.Text.RegularExpressions.Regex.IsMatch(oddName, @"^[\w./\\:~=+-]+$"))
            dashName = oddName;
        bool needsCallOperator =
            !isScriptCommand && cmd.Words.Length > 0
            && (IsExpandedCommandWord(cmd.Words[0]) || dashName is not null
                // `/tmp/x` is emitted as the quoted TempPath string; unquoted-by-`&` it would print itself.
                || GetLiteralValue(cmd.Words[0]) is { } tmpCmd && tmpCmd.StartsWith("/tmp/", StringComparison.Ordinal));
        // A run-time command name (`"$@"`, `$cmd`, `"$tool"`) is looked up as bash does (function, then builtin such
        // as `false` / `echo` / `test`, then PATH): Resolve-BashCommand returns what `&` can call.
        bool dynamicName = needsCallOperator && dashName is null && IsBareVariableCommandWord(cmd.Words[0]);
        bool arrayName = dynamicName && (IsQuotedArrayAllWord(cmd.Words[0]) || IsUnquotedArrayAllWord(cmd.Words[0]));
        // `{1..10}` / `{a,b} x`: brace expansion makes several words, the first is the command (bash: `1: command not
        // found`). Emitted bare, PowerShell printed the numbers.
        bool braceName = !isScriptCommand && cmd.Words.Length > 0 && !cmd.Words[0].Parts.IsDefaultOrEmpty
            && cmd.Words[0].Parts.Any(p => p is WordPart.BracedTuple or WordPart.BracedRange);
        string leading = cmd.Words.Length == 0 ? ""
            : isScriptCommand ? "bash "
            : needsCallOperator ? "& "
            : "";
        // A command word that needs `& ` must also be ONE PowerShell token. A
        // multi-part word like `$gobin/go`, `~/bin/foo`, `$dir/$name` or `"$d"/go`
        // emits as bare concatenated parts, which PowerShell reads as an
        // EXPRESSION (`$env:gobin/go` → "You must provide a value expression
        // following the '/' operator") — unparseable. Flatten those into a single
        // double-quoted string, the same form the already-working single-part
        // quoted case (`"$gobin/go" help`) produced. Hit Go's clean.bash.
        string commandWordText = cmd.Words.Length == 0 ? ""
            : dashName is not null ? PsBuild.SingleQuote(dashName)
            : needsCallOperator && cmd.Words[0].Parts.Length > 1
                ? TransformWordPath(
                    FlattenPartsToDoubleQuotedString(cmd.Words[0].Parts))
                : EmitWord(cmd.Words[0]);

        if (arrayName || braceName)
        {
            // `"$@" extra`: the first element is the command, the rest of the array and the operands its arguments.
            var extra = new StringBuilder();
            for (int i = 1; i < cmd.Words.Length; i++)
                extra.Append(' ').Append(QuoteGeneralArgIfNeeded(EmitWord(cmd.Words[i])));
            string wordsExpr = braceName ? "@(" + EmitArgWord(cmd.Words[0]) + ")" : EmitUnquotedSplitWordArray(cmd.Words[0]);
            sb.Append("& { $__dyn = " + wordsExpr + "; if ($__dyn.Count -gt 0) { $__dynrest = @($__dyn | Select-Object -Skip 1); & (Resolve-BashCommand $__dyn[0]) @__dynrest" + extra + " } else { $global:LASTEXITCODE = 0 } }");
            AppendRedirectTail(sb, cmd.Redirects);
            if (!cmd.EnvPairs.IsEmpty)
            {
                sb.Append(" } finally { ");
                foreach (var envPair in cmd.EnvPairs)
                    sb.Append($"$env:{envPair.Name} = $__saved_{envPair.Name}; ");
                sb.Append('}');
            }
            return sb.ToString();
        }
        if (dynamicName)
        {
            leading = "& (Resolve-BashCommand ";
            // `$SUDO rm f` / `CC='gcc -O2'; $CC -c x.c`: an unquoted name splits first. No word leaves the next one the
            // command (none at all: nothing runs, status 0); more than one makes the rest leading operands.
            commandWordText = IsPureUnquotedVarWord(cmd.Words[0])
                ? "-Words " + EmitUnquotedVarSplitArray(cmd.Words[0]) + ")"
                : commandWordText + ")";
        }

        // A script-defined function named like a mapped command (`cd() { �; }`, `seq() { �; }`): PowerShell resolves an
        // alias before a function, and the command is aliased � so call the function explicitly (bash: functions first).
        if (!dynamicName && !needsCallOperator && cmd.Words.Length > 0 && GetLiteralValue(cmd.Words[0]) is { Length: > 0 } fnName
            && _definedFunctions is { Count: > 0 } && _definedFunctions.Contains(fnName) && IsMappedCommandName(fnName))
        {
            var q = PsBuild.SingleQuote(fnName);
            leading = "& ((Get-Command -Name " + q + " -CommandType Function -ErrorAction SilentlyContinue) ?? " + q + ") ";
            commandWordText = "";
        }

        // A PowerShell statement keyword (exit/return/break/…) cannot take a
        // splatted argument — `exit @__bashsplat0` is a parse error that poisons
        // the whole emitted file. Fall back to plain emission for those.
        bool splatSafeCommand = cmd.Words.Length == 0
            || GetLiteralValue(cmd.Words[0]) is not string cw
            || !PsStatementKeywordCommands.Contains(cw);

        if (splatSafeCommand && HasUnquotedVarSplatArg(commandArgs, loneArraySplats: true))
        {
            // Hoist each unquoted-variable operand to a temp var and splat it.
            // The command word keeps its existing emission (incl. the `& `
            // call-operator prefix for a quoted command word, or the `bash ` prefix for a script).
            sb.Append(EmitCommandWithSplatArgs(
                leading + commandWordText,
                commandArgs,
                argIndex => dynamicName && OrderedArgDashLiteral(commandArgs[argIndex]) is { } dynDash
                    ? PsBuild.SingleQuote(dynDash)
                    : QuoteCommaLiteralWord(commandArgs[argIndex], EmitWord(commandArgs[argIndex]))));
        }
        else
        {
            for (var i = 0; i < cmd.Words.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                    // `$GREP -e pat` / `$SH -e -c ...`: the name may resolve to a cmdlet, where a bare `-e` is an ambiguous
                    // PowerShell parameter prefix. A quoted flag is an argument for whatever the name turns out to be.
                    if (dynamicName && OrderedArgDashLiteral(cmd.Words[i]) is { } dynDashArg)
                    {
                        sb.Append(PsBuild.SingleQuote(dynDashArg));
                        continue;
                    }
                    var emittedArg = EmitWord(cmd.Words[i]);
                    var quotedArg = QuoteGeneralArgIfNeeded(emittedArg);
                    sb.Append(ReferenceEquals(quotedArg, emittedArg)
                        ? QuoteCommaLiteralWord(cmd.Words[i], emittedArg)
                        : quotedArg);
                    continue;
                }
                sb.Append(leading);
                sb.Append(commandWordText);
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
        // Every value is evaluated against the PRE-assignment environment (`a=1 b=$a cmd` gives b the old a).
        var values = WithEnvShadow(pairs, () => pairs.Select(p => EmitAssignmentValue(p.Value)).ToArray());
        var sb = new StringBuilder();
        foreach (var p in pairs)
            sb.Append($"$__saved_{p.Name} = $env:{p.Name}; ");
        // A word that read a prefixed name (emitted as the shadow) needs the old value parked first.
        var parked = new List<string>();
        foreach (var p in pairs)
        {
            var shadow = EnvShadowPrefix + p.Name;
            if (!inner.Contains(shadow, StringComparison.Ordinal)
                && !values.Any(v => v.Contains(shadow, StringComparison.Ordinal)))
                continue;
            sb.Append($"$env:{shadow} = $env:{p.Name}; ");
            parked.Add(shadow);
        }
        sb.Append("try { ");
        for (int i = 0; i < pairs.Length; i++)
            sb.Append($"$env:{pairs[i].Name} = {values[i]}; ");
        if (pairs.Any(p => p.Name == "PATH"))
            sb.Append(PsBuild.NormalizePathVariable).Append("; ");
        sb.Append(inner);
        sb.Append(" } finally { ");
        foreach (var p in pairs)
            sb.Append($"$env:{p.Name} = $__saved_{p.Name}; ");
        foreach (var shadow in parked)
            sb.Append($"$env:{shadow} = $null; ");
        sb.Append('}');
        return sb.ToString();
    }

    private static string EmitRedirect(Redirect r)
    {
        var target = EmitFileRedirectTarget(r.Target);
        // An expanded file target (`2> $dir/f`, dir=/c/Users/...) is only known at run time,
        // so the literal `/c/…` rewrite above cannot see it: map it when it is evaluated.
        // fd-dup/close ops (`>&`, `<&`) take an fd number, not a path — left alone.
        if (UnixPathTranslationEnabled && r.Op is not (">&" or "<&")
            && TryGetStaticArgValue(r.Target) is null)
            target = PsBuild.RuntimeWindowsPath(target);

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
            // PowerShell's fd-merge operator accepts ONLY `n>&1` with n != 1.
            // Every other numeric merge is a parse error — `1>&3` / `2>&3`
            // ("Missing file specification after redirection operator") and
            // `1>&2` / `2>&2` ("reserved for future use") — which poisons the
            // parse of the whole emitted file. bash's user fds (3, 4, …) have no
            // PowerShell equivalent at all: they are opened by `exec 3>&1`, which
            // we cannot model. Degrade to a visible inline no-op — the stream
            // keeps its default destination — the same way `<&-` already does.
            // (`>&2`, the common stderr idiom, never reaches here: EmitSimple
            // routes it through the Write-BashHostStderr pipe.)
            // Hit dotnet-install.sh and the Visual Studio prereq scripts.
            ">&" when IsAllDigits(target) && (target != "1" || r.Fd == 1) =>
                $"<# ps-bash: fd merge {r.Fd}>&{target} has no PowerShell equivalent #>",
            ">&" => $"{r.Fd}>&{target}",
            // `n<&-` (close stdin) — no PowerShell equivalent; degrade to a
            // documented inline no-op comment rather than the invalid `0<&-`.
            "<&" when target == "-" => "<# ps-bash: stdin fd-close (<&-) has no PowerShell equivalent #>",
            // `&>file` / `&>>file` — redirect (or append) BOTH stdout and stderr.
            // PowerShell: `>file 2>&1` / `>>file 2>&1` (stdout to the file, then
            // stderr to wherever stdout now points).
            "&>" => $">{target} 2>&1",
            "&>>" => $">>{target} 2>&1",
            // `cmd 7<file` opens descriptor 7 FOR the command; nothing here reads it, and the raw `7<file` is a
            // PowerShell parse error ("'<' is reserved"). Stdin is untouched, as in bash.
            "<" when r.Fd > 2 => $"<# ps-bash: fd {r.Fd} input redirect ignored #>",
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

    /// <summary>
    /// The path of a `> word` / `>> word` / `2> word` file redirect. An unquoted glob or variable is expanded like any word: exactly
    /// one result is the file (no match leaves a glob as its own text); several, or none for a variable, is "ambiguous redirect" --
    /// the empty path, which fails the redirect with status 1 as `> ''` does.
    /// </summary>
    private static string EmitFileRedirectTarget(CompoundWord word)
    {
        if (IsGlobWord(word) || IsPureUnquotedVarWord(word))
            return "$(& { $__rt = @(" + EmitUnquotedSplitWordArray(word) + "); if ($__rt.Count -eq 1) { $__rt[0] } else { '' } })";
        // `> a-{one,two}`: brace expansion gives several words, also an ambiguous redirect (the array reached -Path).
        if (!word.Parts.IsDefaultOrEmpty && word.Parts.Any(p => p is WordPart.BracedTuple or WordPart.BracedRange))
            return "$(& { $__rt = @(" + EmitArgWord(word) + "); if ($__rt.Count -eq 1) { $__rt[0] } else { '' } })";
        return TransformRedirectTarget(EmitArgWord(word));
    }

    private static string TransformRedirectTarget(string target)
    {
        if (target == "/dev/null")
            return "$null";
        if (target.StartsWith("/tmp/"))
            return PsBuild.TempPath(target[5..]);
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
    private static bool TryTranslateMsysDrivePath(string path, out string windowsPath, bool allowBareDriveRoot = false)
    {
        // A bare `/x` (one letter, nothing after) is a Windows switch as an operand
        // (`cmd /c`, `robocopy /e`, `taskkill /f`), never a drive. Like MSYS, only `/x/...` is
        // converted; `cd /c` (the one place a bare root is unambiguous) opts in.
        bool bareSwitch = !allowBareDriveRoot && path.Length == 2 && path[0] == '/';
        if (!bareSwitch && !_operandsAreText && UnixPathTranslationEnabled && WindowsPath.TryMapUnixDrivePath(path, out windowsPath))
            return true;
        windowsPath = path;
        return false;
    }

    /// <summary>
    /// Translate bash variable references ($VAR, ${VAR}) in heredoc body text
    /// to PowerShell equivalents ($env:VAR).
    /// </summary>
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
                        sb.Append(EmitSimpleVar(content, strict: true));
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
            else if (body[pos] == '$' && pos + 1 < len && body[pos + 1] is (>= '0' and <= '9') or '#' or '?' or '@' or '*' or '$' or '!')
            {
                // Positional / special parameter ($1 $# $@ $* $? $$ $!): one character, `$10` is `$1` then `0`.
                sb.Append(EmitSimpleVar(body[pos + 1].ToString(), inDoubleQuote: true, strict: true));
                pos += 2;
            }
            else if (body[pos] == '$' && pos + 1 < len && IsHereDocVarStart(body[pos + 1]))
            {
                pos++; // skip $
                int start = pos;
                while (pos < len && IsHereDocVarChar(body[pos]))
                    pos++;
                string name = body[start..pos];
                sb.Append(EmitSimpleVar(name, strict: true));
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
    /// <summary>`a=([2]=p [5]=q)` with every element a numeric `[N]=value`: a bash indexed array with explicit subscripts.
    /// Mixed or non-numeric forms are not handled here (non-numeric keys are the associative form).</summary>
    private static bool TryEmitIndexedLiteral(ArrayWord array, out string literal)
    {
        literal = "";
        if (array.Elements.IsDefaultOrEmpty) return false;
        var entries = new List<string>(array.Elements.Length * 2);
        foreach (var elem in array.Elements)
        {
            string? first = elem.Parts.IsDefaultOrEmpty ? null : elem.Parts[0] switch
            {
                WordPart.Literal l => l.Value,
                WordPart.GlobPart g => g.Pattern,
                _ => null,
            };
            if (first is not { Length: > 2 } || first[0] != '[') return false;
            string key, head;
            ImmutableArray<WordPart> parts;
            if (first[^1] == ']' && elem.Parts.Length > 1 && elem.Parts[1] is WordPart.Literal { Value: var eqTail } && eqTail.StartsWith('='))
            {
                key = first[1..^1]; head = eqTail[1..]; parts = elem.Parts.RemoveRange(0, 2);
            }
            else
            {
                int close = first.IndexOf("]=", StringComparison.Ordinal);
                if (close < 2) return false;
                key = first[1..close]; head = first[(close + 2)..]; parts = elem.Parts.RemoveAt(0);
            }
            if (key.Length == 0 || key.Length > 9 || !key.All(char.IsDigit)) return false;
            if (head.Length > 0) parts = parts.Insert(0, new WordPart.Literal(head));
            entries.Add(PsBuild.SingleQuote(key));
            entries.Add("(" + EmitAssignmentValue(new CompoundWord(parts)) + ")");
        }
        literal = "[PsBash.Cmdlets.BashSparseArray]::FromEntries(" + string.Join(", ", entries) + ")";
        return true;
    }

    /// <summary>
    /// <c>m=([k1]=v1 [k2]=v2)</c> with non-numeric keys: an associative array, a PowerShell hashtable (the shape
    /// <c>declare -A m; m[k]=v</c> already builds). Numeric keys stay on the indexed-array path.
    /// </summary>
    private static bool TryEmitAssocLiteral(ArrayWord array, out string literal, bool numericKeysAllowed = false)
    {
        literal = "";
        if (array.Elements.IsDefaultOrEmpty) return false;
        var entries = new List<string>(array.Elements.Length);
        foreach (var elem in array.Elements)
        {
            // The lexer reads the subscript `[k1]` as a glob part, then `=v1` as a literal.
            string? first = elem.Parts.IsDefaultOrEmpty ? null : elem.Parts[0] switch
            {
                WordPart.Literal l => l.Value,
                WordPart.GlobPart g => g.Pattern,
                _ => null,
            };
            if (first is not { Length: > 2 } || first[0] != '[') return false;
            string key, head;
            ImmutableArray<WordPart> parts;
            if (first[^1] == ']' && elem.Parts.Length > 1 && elem.Parts[1] is WordPart.Literal { Value: var eqTail } && eqTail.StartsWith('='))
            {
                key = first[1..^1];
                head = eqTail[1..];
                parts = elem.Parts.RemoveRange(0, 2);
            }
            else
            {
                int close = first.IndexOf("]=", StringComparison.Ordinal);
                if (close < 2) return false;
                key = first[1..close];
                head = first[(close + 2)..];
                parts = elem.Parts.RemoveAt(0);
            }
            // `['a+1']=c` / `["q r"]=2`: the quotes delimit the key, they are not part of it.
            if (key.Length == 0 || (!numericKeysAllowed && key.All(char.IsDigit))) return false;
            if (key.Length >= 2 && key[0] is '\'' or '"' && key[^1] == key[0]) key = key[1..^1];
            if (head.Length > 0) parts = parts.Insert(0, new WordPart.Literal(head));
            // bash 5.2 leaves a tilde on the right of an associative `[key]=value` literal (an indexed one expands it).
            if (parts.Any(p => p is WordPart.TildeSub))
                parts = parts.Select(p => p is WordPart.TildeSub ts ? (WordPart)new WordPart.Literal("~" + ts.User + (ts.SlashConsumed ? "/" : "")) : p).ToImmutableArray();
            entries.Add(PsBuild.SingleQuote(key) + ", (" + EmitAssignmentValue(new CompoundWord(parts)) + ")");
        }
        literal = "[PsBash.Cmdlets.BashAssoc]::FromPairs(" + string.Join(", ", entries) + ")";   // bash's key order and case-sensitive keys, not a @{} literal
        return true;
    }

    private static string EmitAssignmentValue(CompoundWord? value)
    {
        if (value is null)
            return "\"\"";

        // Brace expansion does not happen on the right of an assignment: `v={X,Y}` stores the text `{X,Y}`.
        if (value.Parts.Any(p => p is WordPart.BracedTuple or WordPart.BracedRange))
            value = new CompoundWord(value.Parts.Select(p => p switch
            {
                WordPart.BracedTuple t => (WordPart)new WordPart.Literal("{" + string.Join(",", t.Items) + "}"),
                WordPart.BracedRange r => new WordPart.Literal("{" + r.Start.ToString().PadLeft(r.ZeroPad, '0') + ".." + r.End.ToString().PadLeft(r.ZeroPad, '0')
                    + (r.Step != 0 ? ".." + r.Step : "") + "}"),
                _ => p,
            }).ToImmutableArray());

        // If the value is a single double-quoted part, emit it directly (already has quotes).
        if (value.Parts.Length == 1 && value.Parts[0] is WordPart.DoubleQuoted)
            return EmitWordPart(value.Parts[0]);

        // If the value is a single single-quoted part, emit it directly.
        if (value.Parts.Length == 1 && value.Parts[0] is WordPart.SingleQuoted)
            return EmitWordPart(value.Parts[0]);

        // A command substitution anywhere in the value (`y=$(cmd)`, `y=a"$(cmd)"`): one join
        // expression, never `$( )` text inside the flattened "…" (see TryEmitJoinedWord).
        if (TryEmitJoinedValue(value.Parts, out var joined))
            return joined;

        // Multi-part value: flatten into one PS double-quoted string.
        // EmitWord wraps each DoubleQuoted part in its own "...", which produces
        // ""segment1":"segment2"" when combined — invalid PowerShell.
        // Instead open one outer double-quote and inline the content of each part.
        return FlattenPartsToDoubleQuotedString(value.Parts);
    }

    private const string TmpRoot = "/tmp/";

    /// <summary>
    /// If the word's FIRST part begins with the literal text <c>/tmp/</c> (bare, single-quoted or
    /// inside a double-quoted part), returns the word's parts with that prefix removed. This is how
    /// the <c>/tmp</c> rewrite sees a word: as PARTS it can re-render, never as already-emitted
    /// PowerShell text (which still carries bash's quote characters).
    /// </summary>
    private static bool TryStripTmpPrefix(ImmutableArray<WordPart> parts, out ImmutableArray<WordPart> rest)
    {
        rest = default;
        if (parts.IsDefaultOrEmpty)
            return false;

        WordPart? replacement;
        switch (parts[0])
        {
            case WordPart.Literal l when l.Value.StartsWith(TmpRoot, StringComparison.Ordinal):
                replacement = l.Value.Length == TmpRoot.Length
                    ? null : new WordPart.Literal(l.Value[TmpRoot.Length..]);
                break;
            case WordPart.SingleQuoted s when s.Value.StartsWith(TmpRoot, StringComparison.Ordinal):
                replacement = s.Value.Length == TmpRoot.Length
                    ? null : new WordPart.SingleQuoted(s.Value[TmpRoot.Length..]);
                break;
            case WordPart.DoubleQuoted { Parts: { Length: > 0 } inner }
                when inner[0] is WordPart.Literal dl && dl.Value.StartsWith(TmpRoot, StringComparison.Ordinal):
                var innerRest = dl.Value.Length == TmpRoot.Length
                    ? inner.RemoveAt(0)
                    : inner.SetItem(0, new WordPart.Literal(dl.Value[TmpRoot.Length..]));
                replacement = innerRest.IsEmpty ? null : new WordPart.DoubleQuoted(innerRest);
                break;
            default:
                return false;
        }

        var builder = ImmutableArray.CreateBuilder<WordPart>(parts.Length);
        if (replacement is not null)
            builder.Add(replacement);
        for (int i = 1; i < parts.Length; i++)
            builder.Add(parts[i]);
        rest = builder.ToImmutable();
        return true;
    }

    /// <summary>
    /// <c>/tmp/REST</c> as ONE PowerShell double-quoted string: the runtime temp-dir expression,
    /// then <c>/</c>, then REST's parts rendered through the normal part emitters (so quote
    /// characters are consumed, expansions stay live, globs stay in the literal for the cmdlet's
    /// own glob expansion). Declines (false) for shapes it cannot render faithfully — brace,
    /// tilde and process-substitution parts — which keep their dedicated emitters.
    /// </summary>
    private static bool TryEmitTmpRootedWord(ImmutableArray<WordPart> parts, out string emitted)
    {
        emitted = "";
        if (_operandsAreText || !TryStripTmpPrefix(parts, out var rest))
            return false;
        foreach (var p in rest)
            if (p is WordPart.ProcessSub or WordPart.TildeSub or WordPart.BracedTuple or WordPart.BracedRange)
                return false;

        var sb = new StringBuilder();
        sb.Append('"').Append(PsBuild.TempDirExpr).Append('/');
        AppendFlattenedParts(sb, rest);
        sb.Append('"');
        emitted = sb.ToString();
        return true;
    }

    /// <summary>
    /// <c>/tmp/{a,b}</c>: one temp-dir-rooted string per brace item. Only the plain shape (one
    /// brace part, every other part literal text) is handled; anything richer keeps the generic
    /// brace emitter.
    /// </summary>
    private static bool TryEmitTmpBraceWord(ImmutableArray<WordPart> parts, out string emitted)
    {
        emitted = "";
        if (_operandsAreText || !TryStripTmpPrefix(parts, out var rest))
            return false;
        // The stripped prefix must have been BARE literal text (a quoted `/tmp/` brace word is
        // not brace-expanded in bash).
        if (parts[0] is not WordPart.Literal)
            return false;

        var pre = new StringBuilder();
        var suf = new StringBuilder();
        WordPart? brace = null;
        foreach (var p in rest)
        {
            if (p is WordPart.BracedTuple or WordPart.BracedRange)
            {
                if (brace is not null)
                    return false;
                brace = p;
            }
            else if (p is WordPart.Literal l)
                (brace is null ? pre : suf).Append(l.Value);
            else
                return false;
        }
        if (brace is null)
            return false;

        var items = new List<string>();
        foreach (var item in ExpandBrace(brace))
            items.Add("\"" + PsBuild.TempDirExpr + "/"
                + PsBuild.EscapeForDoubleQuote(pre + item + suf) + "\"");
        emitted = $"@({string.Join(',', items)})";
        return true;
    }

    private static string EmitWord(CompoundWord word)
    {
        // `/tmp/…` words are rewritten from their PARTS (see TryEmitTmpRootedWord), before any
        // emitted text exists to splice into.
        if (TryEmitTmpRootedWord(word.Parts, out var tmpWord))
            return tmpWord;

        // Check for brace expansion parts that need prefix/suffix combination.
        if (HasBraceExpansion(word.Parts))
            return TryEmitTmpBraceWord(word.Parts, out var tmpBrace)
                ? tmpBrace
                : EmitBraceExpandedWord(word.Parts);

        // ~/ alone: the parser consumed the '/', so only the string form keeps it.
        if (word.Parts is [WordPart.TildeSub { User: null or "+" or "-", SlashConsumed: true }])
            return TransformWordPath(FlattenPartsToDoubleQuotedString(word.Parts));

        if (word.Parts.Length == 1)
            return EscapeLeadingSplatSigil(
                word, TransformWordPath(EmitBarewordPart(word.Parts[0], wholeWord: true)));

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

        // A tilde word (~/x, a=~:y, ~/) renders as ONE string: the bareword $HOME/x is only valid as a command
        // argument (in an array element or a test operand it parses as a division), loses a trailing '/', and
        // $HOME:y reads $HOME: as a drive reference.
        if (NeedsTildeFlatten(word.Parts))
            return TransformWordPath(FlattenPartsToDoubleQuotedString(word.Parts));

        // `pre"$(cmd)"`: a bareword-led word would glue the join expression onto the bareword,
        // which PowerShell splits into two arguments — emit the WHOLE word as one join.
        if (TryEmitJoinedWord(word.Parts, out var joinedWord))
            return joinedWord;

        var sb = new StringBuilder();
        for (int i = 0; i < word.Parts.Length; i++)
        {
            var part = word.Parts[i];
            string bare = EmitBarewordPart(part);
            // `p${a[1]}` / `last=${a[-1]}`: glued to a literal, a bare `$a['1']` would expand only `$a` and keep `['1']` as text.
            if (word.Parts.Length > 1 && part is WordPart.BracedVarSub && System.Text.RegularExpressions.Regex.IsMatch(bare, @"^\$\w+\[.*\]$"))
                bare = "$(" + bare + ")";
            sb.Append(bare);

            // After TildeSub, restore the '/' ParseTilde consumed if more parts follow. bash
            // appends the rest of the word verbatim; a '\' here made `~/x` a literal
            // `/home/u\x` on Linux, so `cd ~/x` failed. Windows accepts '/' too.
            if (part is WordPart.TildeSub { SlashConsumed: true })
                sb.Append('/');
        }
        return EscapeLeadingSplatSigil(word, TransformWordPath(sb.ToString()));
    }

    /// <summary>
    /// Backtick-escapes a leading <c>@</c> that came from a bare bash LITERAL.
    /// <c>@</c> is an ordinary character in bash but PowerShell's splat / array /
    /// hashtable sigil, so an unquoted literal word starting with it is either a
    /// parse error (<c>echo @</c> → "Unrecognized token", which poisons the whole
    /// file) or — worse — silently WRONG: <c>cmd @arg</c> parsed fine but splatted
    /// the PowerShell variable <c>$arg</c> instead of passing the literal text
    /// <c>@arg</c>. Found in npm's completion.sh (<c>… -n = -n @ -n : …</c>).
    /// <para>
    /// Gated on the word's FIRST part being a <see cref="WordPart.Literal"/>: an
    /// <c>@(…)</c> the emitter itself produced (brace expansion, splat hoist) is
    /// real PowerShell syntax and must not be escaped. Bash's own escaped/quoted
    /// forms (<c>\@</c>, <c>'@'</c>) already emit correctly and never reach here.
    /// </para>
    /// </summary>
    private static string EscapeLeadingSplatSigil(CompoundWord word, string emitted) =>
        emitted.StartsWith('@') && word.Parts is [WordPart.Literal, ..]
            ? "`" + emitted
            : emitted;

    /// <summary>True for a word holding an expandable tilde (~, ~+, ~-) that can render as one string: no glob, brace or process-substitution part (those keep their dedicated emitters).</summary>
    private static bool NeedsTildeFlatten(ImmutableArray<WordPart> parts)
    {
        bool tilde = false;
        foreach (var p in parts)
        {
            if (p is WordPart.GlobPart or WordPart.ProcessSub or WordPart.BracedTuple or WordPart.BracedRange) return false;
            if (p is WordPart.TildeSub { User: null or "+" or "-" }) tilde = true;
        }
        return tilde;
    }

    /// <summary>
    /// True when naive part concatenation of a multi-part word would split it into
    /// multiple arguments or corrupt content: the leading part is a self-delimiting
    /// PowerShell token ('...', "...", or $(...)), or an expansion is followed by
    /// more text (member access / indexing — see below). Words containing glob,
    /// process-substitution, tilde, or brace parts are excluded — they require
    /// their own emission and do not lead with a self-delimiting quote in
    /// practice.
    /// </summary>
    private static bool NeedsAdjacencyFlatten(ImmutableArray<WordPart> parts)
    {
        if (parts.Length < 2)
            return false;
        foreach (var p in parts)
        {
            if (p is WordPart.GlobPart or WordPart.ProcessSub or WordPart.TildeSub
                  or WordPart.BracedTuple or WordPart.BracedRange)
                return false;
        }
        if (parts[0] is WordPart.SingleQuoted or WordPart.DoubleQuoted
                     or WordPart.AnsiCQuoted or WordPart.CommandSub)
            return true;
        // An expansion followed by more text (`$f.txt`, `$f[0]`, `${f}:x`, `$f,x`, `$((n)).x`).
        // Glued in a PowerShell argument-mode bareword, the text folds into the variable token:
        // `.txt` is MEMBER ACCESS (even on `${env:f}`), `[0]` indexing, `,` an array — so
        // `echo $f.txt` printed "" and `touch $f.bak` got an empty operand. Inside one "…"
        // string none of these apply (FlattenPartsToDoubleQuotedString braces `${…}` itself).
        for (int i = 0; i < parts.Length - 1; i++)
        {
            // `$@x` / `${a[@]}x` expand to several words: not a single-string word.
            if (parts[i] is WordPart.SimpleVarSub { Name: not ("@" or "*") }
                or WordPart.BracedVarSub { Name: not ("@" or "*"), Suffix: not ("[@]" or "[*]") }
                or WordPart.ArithSub)
                return true;
        }
        // A trailing `${v:-x}` / `${v:+x}` (…) emits as a parenthesised expression, which PowerShell reads as a
        // SEPARATE argument after a bareword (`echo a${v:+foo}` printed "a foo").
        if (parts[^1] is WordPart.BracedVarSub { Name: not ("@" or "*"), Suffix: { Length: > 0 } sfx }
            && sfx[0] is ':' or '-' or '+' or '=' or '?' or '^' or ',' or '#' or '%' or '/' or '@')
            return true;
        return false;
    }

    /// <summary>
    /// Flatten a word's parts into one PowerShell double-quoted string. Mirrors
    /// the multi-part branch of <see cref="EmitAssignmentValue"/>: single-quoted
    /// and literal content is escaped so `$` / backtick / `"` stay literal, while
    /// double-quoted parts and command substitutions keep their expansion.
    /// </summary>
    private static string FlattenPartsToDoubleQuotedString(ImmutableArray<WordPart> parts)
    {
        if (TryEmitJoinedWord(parts, out var joined))
            return joined;

        // Same rule as EmitDoubleQuoted: a word whose value is fully known at
        // transpile time emits as a SINGLE-quoted PowerShell string when the
        // double-quoted form would need a backtick escape. The escape does not
        // survive nesting inside another double-quoted string — this is the path the
        // `'"'"'` single-quote idiom takes (`grep 'a'"'"'b'`), and it is what left
        // resolve-port.sh / backup.sh / stop-hook.sh unparseable.
        if (TryGetPureLiteralText(parts, out var literal)
            && NeedsSingleQuotedLiteral(literal))
        {
            return PsBuild.SingleQuote(literal);
        }

        var sb = new StringBuilder();
        sb.Append('"');
        AppendFlattenedParts(sb, parts);
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// A word whose emitted PowerShell would put a command substitution INSIDE a double-quoted
    /// string (<c>"$(cmd)"</c>, <c>"a$(cmd)b"</c>, <c>"${x:+$(cmd)}"</c>) is emitted instead as one
    /// expression, <c>(-join @(seg, seg, …))</c>, so no command text ever sits in a PS string.
    /// <para>
    /// Why: PowerShell finds the end of a <c>$( … )</c> inside an expandable string by counting
    /// parens WITHOUT honouring the quotes or here-strings within it. <c>"$('a ) b')"</c> and
    /// <c>"$('a ( b')"</c> are parse errors, so bash's <c>echo "$(echo ')')"</c>, a <c>:)</c> in a
    /// <c>"$(cat &lt;&lt;'EOF' … EOF)"</c> commit message, or any odd paren in quoted command text
    /// made the WHOLE script fail to parse. Earlier fixes (<see cref="_dqNestDepth"/>, the
    /// <c>[char]10</c> join, single-quoting nested literals) each dodged one shape of this
    /// in-string scan; leaving the string removes the class. Segments: quoted/literal text →
    /// single-quoted; a nested run without command text → its ordinary <c>"…"</c> (variables
    /// are safe in a string); each command substitution → <see cref="EmitCommandSubString"/>
    /// as a bare value (<c>nested: false</c> — it is no longer inside a string).
    /// </para>
    /// Returns false (caller keeps the string form) when no command text would be embedded, or
    /// the word has a part with its own emission (glob, tilde, brace, process substitution).
    /// </summary>
    private static bool TryEmitJoinedWord(ImmutableArray<WordPart> parts, out string expr)
    {
        expr = "";
        return EmbedsCommandSubInString(parts, inDq: false) && TryJoin(parts, inDq: false, out expr);
    }

    /// <summary>
    /// <see cref="TryEmitJoinedWord"/> for a word that is string context as a whole even when
    /// unquoted: an assignment value (<c>y=$(cmd)</c> — no word splitting) or a
    /// <c>${x:-W}</c> argument W. Any command substitution in it would otherwise be flattened
    /// into the value's <c>"…"</c>.
    /// </summary>
    private static bool TryEmitJoinedValue(ImmutableArray<WordPart> parts, out string expr)
    {
        expr = "";
        return ContainsCommandSub(parts) && TryJoin(parts, inDq: true, out expr);
    }

    private static bool TryJoin(ImmutableArray<WordPart> parts, bool inDq, out string expr)
    {
        expr = "";
        var segs = new List<string>();
        if (!AppendJoinSegments(segs, parts, inDq)) return false;
        // A lone quoted $( ) is already a string-valued expression (`x="$(cmd)"`, `y=$(cmd)`):
        // emit it bare. Any other lone segment keeps the join, which also turns a $null
        // (`${x:+…}` unset) into "" rather than unsetting an assigned variable.
        expr = segs.Count == 1 && segs[0].StartsWith(CommandSubStringPrefix, StringComparison.Ordinal)
            ? segs[0]
            : "(-join @(" + string.Join(", ", segs) + "))";
        return true;
    }

    /// <summary>How every <see cref="EmitCommandSubString"/> value starts.</summary>
    private const string CommandSubStringPrefix = "$((@(";

    /// <summary>True when emitting <paramref name="parts"/> as a PS string would embed a command
    /// substitution's text in it: a <c>$( )</c> inside double quotes, or a <c>${x:-W}</c> in
    /// double quotes whose argument word holds one.</summary>
    private static bool EmbedsCommandSubInString(ImmutableArray<WordPart> parts, bool inDq)
    {
        foreach (var p in parts)
        {
            switch (p)
            {
                case WordPart.CommandSub when inDq: return true;
                // Glued to other text (`[$(cmd)]`), an unquoted $( ) is emitted inside a PS
                // argument-mode bareword, whose $( ) end is found by the same naive paren count.
                case WordPart.CommandSub when parts.Length > 1: return true;
                case WordPart.DoubleQuoted dq when EmbedsCommandSubInString(dq.Parts, inDq: true): return true;
                case WordPart.BracedVarSub { ArgWord: { } arg } when inDq && ContainsCommandSub(arg): return true;
            }
        }
        return false;
    }

    private static bool ContainsCommandSub(ImmutableArray<WordPart> parts)
    {
        foreach (var p in parts)
        {
            if (p is WordPart.CommandSub) return true;
            if (p is WordPart.DoubleQuoted dq && ContainsCommandSub(dq.Parts)) return true;
            if (p is WordPart.BracedVarSub { ArgWord: { } arg } && ContainsCommandSub(arg)) return true;
        }
        return false;
    }

    /// <summary>Emit <paramref name="parts"/> as join segments (see <see cref="TryEmitJoinedWord"/>);
    /// false for a part this form does not model.</summary>
    private static bool AppendJoinSegments(List<string> segs, ImmutableArray<WordPart> parts, bool inDq)
    {
        // Consecutive string-safe parts inside double quotes collect into one "…" run.
        var run = ImmutableArray.CreateBuilder<WordPart>();
        void FlushRun()
        {
            if (run.Count == 0) return;
            segs.Add(EmitDoubleQuoted(new WordPart.DoubleQuoted(run.ToImmutable())));
            run.Clear();
        }
        foreach (var p in parts)
        {
            switch (p)
            {
                case WordPart.Literal or WordPart.EscapedLiteral when inDq:
                    run.Add(p);
                    break;
                case WordPart.Literal or WordPart.EscapedLiteral or WordPart.SingleQuoted or WordPart.AnsiCQuoted:
                    FlushRun();
                    TryGetPureLiteralText(ImmutableArray.Create(p), out var lit);
                    segs.Add(PsBuild.SingleQuote(lit));
                    break;
                case WordPart.DoubleQuoted dq:
                    FlushRun();
                    if (!AppendJoinSegments(segs, dq.Parts, inDq: true)) return false;
                    break;
                case WordPart.CommandSub cs:
                    FlushRun();
                    // Quoted: newline-preserving string value. Unquoted inside a mixed word: the
                    // lines space-joined, as the glued bareword's $OFS join gave before (its bash
                    // word-splitting is not modelled here).
                    segs.Add(inDq ? EmitCommandSubString(cs, nested: false) : $"(@{EmitCommandSub(cs)[1..]} -join ' ')");
                    break;
                case WordPart.BracedVarSub { ArgWord: { } arg } bvs when inDq && ContainsCommandSub(arg):
                    FlushRun();
                    segs.Add(EmitBracedVar(bvs, inDoubleQuote: false));
                    break;
                case WordPart.SimpleVarSub or WordPart.BracedVarSub or WordPart.ArithSub:
                    if (inDq) { run.Add(p); break; }
                    segs.Add(EmitWordPart(p));
                    break;
                default:
                    return false;
            }
        }
        FlushRun();
        return true;
    }

    /// <summary>
    /// The INNER text (no surrounding quotes) of <see cref="FlattenPartsToDoubleQuotedString"/>:
    /// each part rendered for placement inside one PowerShell double-quoted string. Shared with
    /// the <c>/tmp</c> word rewrite so it renders the rest of the word through these same part
    /// emitters instead of splicing already-emitted text.
    /// </summary>
    private static void AppendFlattenedParts(StringBuilder sb, ImmutableArray<WordPart> parts)
    {
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            // The part after the closing quote decides whether a variable that ENDS the quotes needs braces:
            // `"$y"x` emitted "$env:yx" (another, unset variable) and printed nothing.
            if (part is WordPart.DoubleQuoted dq)
                AppendDoubleQuotedInner(sb, dq.Parts, i + 1 < parts.Length ? parts[i + 1] : null);
            else if (part is WordPart.SingleQuoted sq)
                sb.Append(EscapeForDoubleQuoteNested(sq.Value));
            else if (part is WordPart.AnsiCQuoted aq)
                sb.Append(EscapeForDoubleQuoteNested(ExpandAnsiCEscapes(aq.Value)));
            else if (part is WordPart.Literal lit)
                sb.Append(EscapeForDoubleQuoteNested(lit.Value));
            // `\x` in bash is the literal char x. A bare backtick+x is NOT that inside a PS
            // double-quoted string for every x (`` `n `` is a newline, `` `' `` a quote pair).
            else if (part is WordPart.EscapedLiteral esc)
                sb.Append(EscapeForDoubleQuoteNested(esc.Value));
            // A NESTED expansion (e.g. ${x:-${y:-z}}) must be emitted in double-quote
            // context: its bare form `($env:y ?? "z")` carries raw `"`, which would
            // break out of this surrounding "...". The inDoubleQuote form uses `$(...)`
            // with single-quoted inner literals, which is safe nested here.
            // Same suffix-less-${name} drive-reference guard as AppendDoubleQuotedInner.
            else if (part is WordPart.BracedVarSub { Suffix: null } plainNbvs
                && i + 1 < parts.Length && NextPartNeedsBracing(parts[i + 1]))
                sb.Append(EmitSimpleVarBraced(plainNbvs.Name, strict: true));
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
                    ? EmitSimpleVarBraced(nvs.Name, strict: true)
                    : EmitSimpleVar(nvs.Name, inDoubleQuote: true, strict: true));
            }
            else if (part is WordPart.CommandSub ncs)
                // Assignment/flatten context (x=$(cmd)) preserves internal newlines and
                // strips trailing ones, like bash — not the array $OFS-join a space.
                sb.Append(EmitCommandSubString(ncs));
            else if (part is WordPart.TildeSub { User: null, SlashConsumed: false }
                     && i + 1 < parts.Length && parts[i + 1] is WordPart.Literal { Value: [':' or '_' or (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z'), ..] })
                sb.Append("${HOME}"); // braced: "$HOME:x" would read `$HOME:` as a drive reference
            else
                sb.Append(EmitWordPart(part));

            // ParseTilde consumes the '/' after `~`, so the emitter owes the
            // separator back. EmitWord does this; this flatten path did not, so
            // `PATH=~/bin:~/x` emitted "$HOMEbin:$HOMEx" — and `$HOMEbin` is not a
            // valid PowerShell variable reference, so the whole file failed to parse.
            if (part is WordPart.TildeSub { SlashConsumed: true })
                sb.Append('/');
        }
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
        // A command substitution in W must not land inside the flattened "…" string either (see
        // TryEmitJoinedWord); W's value is the string form, so its $( ) is quoted-context.
        if (TryEmitJoinedValue(parts, out var joined))
            return joined;

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

    /// <summary>The text a prefix/suffix part contributes to every brace item: a quoted or escaped literal is
    /// its value (`\{`, `'x'` have no quote/backtick left to print), anything else is emitted as before.</summary>
    private static string BraceAffixText(WordPart part)
        => part is WordPart.Literal or WordPart.EscapedLiteral or WordPart.SingleQuoted
           && TryGetPureLiteralText(ImmutableArray.Create(part), out var text)
            ? text
            : EmitWordPart(part);

    // A brace item with an expansion (`{$a,b}`) is word text: decompose it; a plain item stays one literal.
    // A LEADING item that starts with ~ (`{~,~/x}`) is tilde-expanded once the braces are gone, as in bash.
    private static ImmutableArray<WordPart> BraceItemParts(string item, bool leading = false)
        => item.Contains('$') || item.Contains('`') || (leading && item.StartsWith('~'))
            ? BashParser.DecomposeWord(item)
            : ImmutableArray.Create<WordPart>(new WordPart.Literal(item));

    // Joining text after an item that ends in a variable name continues that NAME, as in bash's textual
    // expansion (`{$a,b}_c` reads `$a_c`).
    private static ImmutableArray<WordPart> AppendBraceParts(ImmutableArray<WordPart> head, ImmutableArray<WordPart> tail)
    {
        if (head.Length > 0 && head[^1] is WordPart.SimpleVarSub v && tail.Length > 0 && tail[0] is WordPart.Literal lit)
        {
            int n = 0;
            while (n < lit.Value.Length && (char.IsAsciiLetterOrDigit(lit.Value[n]) || lit.Value[n] == '_')) n++;
            if (n > 0)
            {
                var merged = head.RemoveAt(head.Length - 1).Add(new WordPart.SimpleVarSub(v.Name + lit.Value[..n]));
                if (n < lit.Value.Length) merged = merged.Add(new WordPart.Literal(lit.Value[n..]));
                return merged.AddRange(tail.RemoveAt(0));
            }
        }
        return head.AddRange(tail);
    }

    private static string EmitBraceExpandedWord(ImmutableArray<WordPart> parts)
    {
        // Adjacent brace expansions cross-multiply: bash `{a..c}{1..2}` -> a1 a2 b1 b2 c1 c2.
        // With 2+ brace parts, compute the full Cartesian product across all segments
        // (each brace contributes its item list; every other part is one literal segment).
        int braceCount = 0;
        bool dynamicPart = false;
        bool dynamicItems = false;
        foreach (var p in parts)
        {
            if (p is WordPart.BracedTuple or WordPart.BracedRange)
            {
                braceCount++;
                // `{$a,b}`: an item with an expansion is text that must be expanded, not printed.
                if (p is WordPart.BracedTuple t && t.Items.Any(i => i.Contains('$') || i.Contains('`')))
                    dynamicPart = dynamicItems = true;
                else if (braceCount == 1 && ReferenceEquals(p, parts[0]) && p is WordPart.BracedTuple lead && lead.Items.Any(i => i.StartsWith('~')))
                    dynamicPart = true;
            }
            else if (p is not (WordPart.Literal or WordPart.EscapedLiteral or WordPart.SingleQuoted or WordPart.GlobPart))
                dynamicPart = true;
        }
        // `{a,b}$x`, `{1..3}"$y"`, `{a,b}$(cmd)`: a non-literal part cannot be spliced into a single-quoted
        // item (it emitted `a$env:x`). Build each combination as a part list and emit it as one expansion-aware word.
        if (dynamicPart)
        {
            var combos = new List<ImmutableArray<WordPart>> { ImmutableArray<WordPart>.Empty };
            foreach (var part in parts)
            {
                var alts = part is WordPart.BracedTuple or WordPart.BracedRange
                    ? ExpandBrace(part).ConvertAll(it => BraceItemParts(it, leading: combos.Count == 1 && combos[0].IsEmpty))
                    : new List<ImmutableArray<WordPart>> { ImmutableArray.Create(part) };
                CheckBraceWords((long)combos.Count * alts.Count);
                var next = new List<ImmutableArray<WordPart>>(combos.Count * alts.Count);
                foreach (var head in combos)
                    foreach (var alt in alts)
                        next.Add(AppendBraceParts(head, alt));
                combos = next;
            }
            var combined = $"@({string.Join(',', combos.ConvertAll(FlattenPartsToDoubleQuotedString))})";
            // An unquoted expansion that comes out empty is dropped from the word list (`{$a,b}_c` with `$a_c` unset).
            return dynamicItems ? $"@({combined} | Where-Object {{ $_ -ne '' }})" : combined;
        }
        if (braceCount >= 2)
        {
            var combos = new List<string> { "" };
            foreach (var part in parts)
            {
                List<string> segment = part is WordPart.BracedTuple or WordPart.BracedRange
                    ? ExpandBrace(part)
                    : new List<string> { BraceAffixText(part) };
                CheckBraceWords((long)combos.Count * segment.Count); // bound the product before building it
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
                prefix.Append(BraceAffixText(part));
            }
            else
            {
                suffix.Append(BraceAffixText(part));
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
        // long arithmetic: with an int counter `{2147483600..2147483647}` overflowed `v += step`
        // past End and looped forever, allocating until the process died. Counted (and bounded,
        // see MaxBraceWords) BEFORE anything is materialized.
        long step = range.Step != 0 ? Math.Abs((long)range.Step) * (range.Start <= range.End ? 1 : -1)
                                    : (range.Start <= range.End ? 1 : -1);
        long count = (Math.Abs((long)range.End - range.Start) / Math.Abs(step)) + 1;
        CheckBraceWords(count);
        var items = new List<string>((int)count);
        for (long v = range.Start;
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

    /// <summary>
    /// Most words one brace-expanded word may produce. Expansion happens at TRANSPILE time — in the
    /// launcher, or inside the shared host for eval/source — so an unbounded product
    /// (<c>{a,b}</c>×30 = 2^30 words) exhausted memory and killed the process. Past the cap the word is
    /// a clean parse error (bash would grind through it until malloc fails).
    /// </summary>
    internal const long MaxBraceWords = 1_000_000;

    internal static void CheckBraceWords(long count)
    {
        if (count > MaxBraceWords)
            throw new ParseException(
                $"brace expansion too large ({count} words; the limit is {MaxBraceWords})", 1, 1, "brace");
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

    // internal, not private: FusedLane's stage-list builder must apply the SAME
    // operand path rewrite the fallback path applies, or the two lanes diverge.
    internal static string TransformWordPath(string value)
    {
        // NOTE: do NOT map /dev/null -> $null here. As a command OPERAND (vs a redirect target,
        // which TransformRedirectTarget still maps to $null), bash's /dev/null is an empty FILE —
        // `grep x /dev/null` reads an empty file (no match, exit 1), not the $null discard sink
        // (which crashed cmdlets with "Value cannot be null"). The literal /dev/null flows to the
        // runtime, where BashFileSystem.OpenRead (via FileSystemHelpers.IsNullDevice) serves it
        // from the OS-native null device as empty.
        if (value.StartsWith("/tmp/") && !_operandsAreText)
            return PsBuild.TempPath(value[5..]);
        if (TryTranslateMsysDrivePath(value, out var translated))
            return translated;
        return value;
    }

    /// <summary>A part spliced into an unquoted PowerShell word: a literal `{` / `}` would start a scriptblock
    /// (`echo a{b}c` split into `a`, a block and `c`), so it is backtick-escaped.</summary>
    private static string EmitBarewordPart(WordPart part, bool wholeWord = false)
        => part is WordPart.Literal { Value: var v } && (v.Contains('{') || v.Contains('}'))
            ? wholeWord ? PsBuild.DoubleQuote(v) : v.Replace("{", "`{").Replace("}", "`}")
            : EmitWordPart(part);

    private static string EmitWordPart(WordPart part) => part switch
    {
        WordPart.Literal lit => lit.Value,
        // `\b`, `\e`, `\n`... are plain letters in bash; a backtick before one would be a PowerShell escape (bell, ESC, LF).
        WordPart.EscapedLiteral el => el.Value.Length == 1 && char.IsAsciiLetterOrDigit(el.Value[0]) ? el.Value : $"`{el.Value}",
        WordPart.SingleQuoted sq => $"'{sq.Value}'",
        WordPart.AnsiCQuoted aq => EmitAnsiCQuoted(aq),
        WordPart.DoubleQuoted dq => EmitDoubleQuoted(dq),
        WordPart.SimpleVarSub vs => EmitSimpleVar(vs.Name, strict: true),
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
    internal static string ExpandAnsiCEscapes(string s)
    {
        // \xHH / \nnn name BYTES: runs of them are UTF-8 decoded (EscapedTextBuilder), so
        // $'\xe2\x82\xac' is the euro sign, not three Latin-1 characters.
        var sb = new EscapedTextBuilder(s.Length);
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
                    else { sb.AppendByte(val); i = j; }
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
                        sb.Append(x == '?' ? (char)0x7f : (char)(char.ToUpperInvariant(x) & 0x1f));   // bash TOCTRL: \c0 is 0x10, \c? is DEL
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
                    sb.AppendByte(val);
                    i = j;
                    break;
                }

                default:
                    sb.Append('\\').Append(n);
                    i += 2;
                    break;
            }
        }
        // bash words are NUL-terminated C strings: $'a\0b' is just "a" (oracle-verified).
        // Truncate instead of smuggling a NUL into an argv/PowerShell literal.
        string expanded = sb.ToString();
        int nul = expanded.IndexOf('\0');
        return nul >= 0 ? expanded.Substring(0, nul) : expanded;
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
    /// <summary>
    /// Returns <paramref name="inner"/> in a form that can HEAD a PowerShell
    /// pipeline, wrapping it in <c>&amp; { … }</c> when it cannot.
    /// <para>
    /// The AST node type alone is not enough. A compound body (if/for/case/…)
    /// obviously emits a statement and needs wrapping — but so does a
    /// <see cref="Command.Simple"/> whose emission is a statement LIST: an env
    /// prefix (<c>$(LC_TIME=C date)</c> → <c>$__saved_X = …; try { … } finally
    /// { … }</c>) or <c>cd</c> (an if/else block). Those used to be classified as
    /// pipeable and emitted "An empty pipe element is not allowed", breaking the
    /// parse of the whole file. Classify by the EMITTED TEXT via
    /// <see cref="PsBuild.IsStatementList"/> so any current or future
    /// statement-list emitter is covered without re-enumerating builtins.
    /// </para>
    /// The wrap is the full SUBSHELL scope (<see cref="PsBuild.ShellStateScope"/>): bash runs
    /// <c>$( … )</c> in a subshell, so <c>v=$(x=1; set -e; echo)</c> must leave <c>x</c> and errexit
    /// unchanged in the parent — a bare child scope isolated neither (both are process/global state).
    /// The cheap unwrapped form is kept only for a pipeline that provably cannot change shell state
    /// (<see cref="CannotChangeShellState"/>): every stage a mapped <c>Invoke-Bash*</c> command — not a
    /// function, not a state builtin. A native (<c>$(git …)</c>) takes the scope too; its process spawn
    /// dwarfs the snapshot.
    /// </summary>
    private static string PipelineHead(Command body, string inner)
    {
        bool pipeable = body is Command.Simple or Command.Pipeline
                        && !PsBuild.IsStatementList(inner);
        // `${x:?}` inside ends only the substitution (bash: a subshell), so it needs the scope that catches it.
        return pipeable && CannotChangeShellState(body) && !inner.Contains("BashParamErrorException", StringComparison.Ordinal)
            ? inner : PsBuild.ShellStateScope(WithSubshellExitTrap(inner));
    }

    /// <summary>Builtins that change shell state even though they map to a runtime command.</summary>
    private static readonly HashSet<string> StateChangingBuiltins = new(StringComparer.Ordinal)
    {
        "set", "shopt", "trap", "alias", "unalias", "export", "unset", "declare", "typeset", "local",
        "readonly", "cd", "pushd", "popd", "source", ".", "eval", "shift", "getopts", "read", "mapfile",
        "readarray", "let", "exec", "umask", "hash", "builtin", "command", "enable", "exit",
        "return", "wait",
    };

    /// <summary>
    /// True when <paramref name="body"/> is a simple command or pipeline whose every stage is a literal,
    /// MAPPED command (so it cannot be a user function) that is not a state-changing builtin and carries
    /// no assignment. Anything else might change shell state and needs the subshell scope.
    /// </summary>
    private static bool CannotChangeShellState(Command body)
    {
        static bool StageIsInert(Command c) =>
            c is Command.Simple { Words.Length: > 0, EnvPairs.Length: 0 } s
            && GetLiteralValue(s.Words[0]) is { } name
            && !StateChangingBuiltins.Contains(name)
            // `printf -v VAR` assigns; plain `$(printf …)` is common and inert.
            && !(name == "printf" && s.Words.Skip(1).Any(w => GetLiteralValue(w) is { } a && a.StartsWith("-v", StringComparison.Ordinal)))
            && TryEmitMappedCommand(new Command.Simple([s.Words[0]], [], []), out _);
        return body switch
        {
            Command.Simple s => StageIsInert(s),
            Command.Pipeline p => p.Commands.All(StageIsInert),
            _ => false,
        };
    }

    private static string EmitCommandSub(WordPart.CommandSub cs)
    {
        var body = (Command)cs.Body;
        string inner = EmitCaptured(body);
        return $"$({PipelineHead(body, inner)} | ConvertTo-BashCapture)";
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
    private static string EmitCommandSubString(WordPart.CommandSub cs, bool nested = true)
    {
        var body = (Command)cs.Body;
        // This `$( … )` is emitted INTO a double-quoted string, so everything inside
        // it sits one string level deeper (see _dqNestDepth). `nested: false` is the
        // same value used as a bare argument expression (no enclosing string).
        if (nested) _dqNestDepth++;
        string inner;
        try { inner = EmitCaptured(body); }
        finally { if (nested) _dqNestDepth--; }
        // The newline join uses [char]10, NOT a "`n" literal — the SAME reason
        // EmitArithCommandSubValue does. This fragment can land inside an
        // arbitrarily nested "$( … )", where the OUTER string scanner consumes
        // the backtick escape and the inner string ends early ("The string is
        // missing the terminator"). Hit git-completion.bash, where a command sub
        // nested two double-quote levels deep broke the whole file's parse.
        return $"{CommandSubStringPrefix}{PipelineHead(body, inner)} | ConvertTo-BashCapture -Join)))";
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
        var fragments = BuildArithFragments(expr, textual: syntax.Root is ArithmeticExpr.Deferred);
        string argument = fragments is null
            ? PsBuild.SingleQuote(expr)
            : $"('' + {string.Join(" + ", fragments)})";
        var invocation = $"Invoke-BashArith {argument}";
        return _nounsetMode ? PsBuild.ArithNounsetGuard(invocation) : invocation;
    }

    /// <summary>
    /// Split an arithmetic expression into PowerShell concatenation fragments, replacing
    /// every span the evaluator cannot resolve itself with a subexpression yielding its
    /// runtime value. Two kinds of span qualify:
    /// <list type="bullet">
    /// <item>positional / special parameters (<c>$1</c>, <c>$#</c>, <c>${?}</c>, …) — see
    /// <see cref="PositionalRefExpr"/>;</item>
    /// <item>command substitutions (<c>$(date +%s)</c>, <c>`date +%s`</c>) — bash expands
    /// them to text and then evaluates, so the command must run BEFORE
    /// <c>Invoke-BashArith</c> sees the string.</item>
    /// </list>
    /// Everything else (ordinary variables, numbers, operators) is preserved verbatim as a
    /// single-quoted literal for the evaluator to resolve. Returns <c>null</c> when no span
    /// needs substituting, so the common case still emits one plain quoted literal.
    /// </summary>
    private static List<string>? BuildArithFragments(string expr, bool textual = false)
    {
        List<string>? parts = null;
        int last = 0;
        for (int i = 0; i < expr.Length; i++)
        {
            int spanEnd;
            string replacement;
            char c = expr[i];

            if (c == '`')
            {
                int close = expr.IndexOf('`', i + 1);
                if (close < 0) continue;
                spanEnd = close + 1;
                replacement = EmitArithCommandSubValue(expr[(i + 1)..close]);
            }
            else if (c == '$' && i + 1 < expr.Length && expr[i + 1] == '('
                     && !(i + 2 < expr.Length && expr[i + 2] == '('))
            {
                // `$((…))` is a nested arithmetic expansion, not a command substitution;
                // the typed parser strips the `$` and treats it as paren grouping.
                int close = BashArithmeticParser.FindMatchingParen(expr, i + 1);
                if (close < 0) continue;
                spanEnd = close + 1;
                replacement = EmitArithCommandSubValue(expr[(i + 2)..close]);
            }
            else if (c == '$' && TryMatchArithPositional(expr, i, out int length, out string key))
            {
                spanEnd = i + length;
                replacement = PositionalRefExpr(key);
            }
            else if (c == '$' && i + 1 < expr.Length && expr[i + 1] == '{' && FindBraceEnd(expr, i + 1) is var braceEnd and > 0
                     && (textual || !expr[(i + 2)..braceEnd].All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_'))) // ${x}: the evaluator resolves a plain name, unless the text is expanded first
            {
                // `${a[i]}` / `${m[$k]:-0}` / `${x:-7}`: a full parameter expansion, expanded by the shell
                // BEFORE the arithmetic is evaluated (bash expands, then evaluates). An empty result is 0.
                spanEnd = braceEnd + 1;
                var expansion = BashParser.Parse("echo \"" + expr[i..spanEnd] + "\"") is Command.Simple { Words.Length: 2 } echoCmd
                    ? EmitWord(echoCmd.Words[1])
                    : null;
                if (expansion is null) continue;
                replacement = textual ? expansion : ZeroDefault(expansion);
            }
            else if (textual && c == '$' && i + 1 < expr.Length && (char.IsAsciiLetter(expr[i + 1]) || expr[i + 1] == '_'))
            {
                // `f$x`: in a deferred expression every `$name` is replaced by its text before evaluation.
                int end = i + 1;
                while (end < expr.Length && (char.IsAsciiLetterOrDigit(expr[end]) || expr[end] == '_')) end++;
                spanEnd = end;
                replacement = EmitSimpleVar(expr[(i + 1)..end]);
            }
            else if (char.IsAsciiLetter(c) && (i == 0 || !(char.IsAsciiLetterOrDigit(expr[i - 1]) || expr[i - 1] is '_' or '$' or '{'))
                     && TryMatchArithDynamicSpecial(expr, i, out int nameLen, out string dynamicExpr))
            {
                // `$(( RANDOM % 6 ))`: the evaluator reads variables, not the shell's computed ones.
                spanEnd = i + nameLen;
                replacement = ZeroDefault(dynamicExpr);
            }
            else continue;

            parts ??= [];
            if (i > last) parts.Add(PsBuild.SingleQuote(expr[last..i]));
            parts.Add(replacement);
            last = spanEnd;
            i = spanEnd - 1;
        }

        if (parts is null) return null;
        if (last < expr.Length) parts.Add(PsBuild.SingleQuote(expr[last..]));
        return parts;
    }

    /// <summary>A bare RANDOM / SECONDS / EPOCHSECONDS / BASHPID / PPID / UID / EUID in an arithmetic expression that
    /// is READ (not assigned): its computed value, from the same mapping the plain `$NAME` expansion uses.</summary>
    private static bool TryMatchArithDynamicSpecial(string expr, int start, out int length, out string valueExpr)
    {
        length = 0; valueExpr = "";
        int end = start;
        while (end < expr.Length && (char.IsAsciiLetterOrDigit(expr[end]) || expr[end] == '_')) end++;
        string name = expr[start..end];
        if (name is not ("RANDOM" or "SECONDS" or "EPOCHSECONDS" or "BASHPID" or "PPID" or "UID" or "EUID")) return false;
        int k = end;
        while (k < expr.Length && expr[k] == ' ') k++;
        if (k < expr.Length && expr[k] == '=' && !(k + 1 < expr.Length && expr[k + 1] == '=')) return false; // assignment
        if (k + 1 < expr.Length && expr[k + 1] == '=' && expr[k] is '+' or '-' or '*' or '/' or '%') return false;
        if (PsBuild.TryMapSpecialVar(name, braced: false, inDoubleQuote: false) is not { } mapped) return false;
        length = end - start;
        valueExpr = mapped;
        return true;
    }

    /// <summary>Index of the `}` closing the `{` at <paramref name="open"/>, counting nested braces; -1 when unclosed.</summary>
    private static int FindBraceEnd(string s, int open)
    {
        for (int depth = 0, i = open; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>
    /// Match a positional or special parameter reference (<c>$1</c>, <c>$#</c>,
    /// <c>${12}</c>, <c>${?}</c>) at <paramref name="start"/>. Ordinary named variables
    /// are deliberately NOT matched — the evaluator resolves those itself.
    /// <para>
    /// Bash's unbraced rule is preserved: <c>$10</c> is <c>$1</c> followed by the literal
    /// <c>0</c>, so only ONE digit is consumed unbraced; <c>${10}</c> addresses positional
    /// parameter 10.
    /// </para>
    /// </summary>
    private static bool TryMatchArithPositional(string expr, int start, out int length, out string key)
    {
        length = 0;
        key = "";
        int i = start + 1;
        if (i >= expr.Length) return false;
        bool braced = expr[i] == '{';
        if (braced) i++;
        int keyStart = i;
        if (i < expr.Length && char.IsAsciiDigit(expr[i]))
        {
            i++;
            if (braced) while (i < expr.Length && char.IsAsciiDigit(expr[i])) i++;
        }
        else if (i < expr.Length && expr[i] is '@' or '#' or '*' or '?' or '$' or '!')
            i++;
        else return false;
        if (i == keyStart) return false;
        key = expr[keyStart..i];
        // `${#@}` / `${#*}` count the positionals, like `$#`.
        if (braced && key == "#" && i + 1 < expr.Length && expr[i] is '@' or '*' && expr[i + 1] == '}')
            i++;
        if (braced)
        {
            if (i >= expr.Length || expr[i] != '}') return false;
            i++;
        }
        length = i - start;
        return true;
    }

    /// <summary>
    /// A PowerShell subexpression yielding a command substitution's output as trimmed text,
    /// for splicing into an arithmetic string. Deliberately built without a PowerShell
    /// double-quoted string (the newline join uses <c>[char]10</c>): the fragment can land
    /// inside an arbitrarily nested <c>"$( … )"</c>, where a literal <c>"</c> would need
    /// per-level escaping (see <see cref="_dqNestDepth"/>). Trimming matches bash, which
    /// strips surrounding whitespace before evaluating the expanded text.
    /// </summary>
    private static string EmitArithCommandSubValue(string commandText)
    {
        var body = BashParser.Parse(commandText);
        // An empty substitution (`$(( $() + 1 ))`) expands to nothing, which bash then
        // evaluates as the empty operand of a unary `+` — i.e. zero.
        if (body is null) return "'0'";
        string inner = EmitCaptured(body);
        return $"$((@({PipelineHead(body, inner)} | ConvertTo-BashCapture) -join [string][char]10).Trim())";
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
                "@" => "if ($global:BashPositional) { $global:BashPositional -join ' ' } else { $args -join ' ' }",
                "*" => "if ($global:BashPositional) { $global:BashPositional -join " + PsBuild.IfsJoinSeparator + " } else { $args -join " + PsBuild.IfsJoinSeparator + " }",
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

    private static string EmitSimpleVar(string name, bool inDoubleQuote = false, bool strict = false, bool arrayElem0 = true)
    {
        // An EMPTY name is never a valid bash variable. It arises from a
        // substitution this parser does not model — in practice ZSH-only syntax
        // sitting in a `[[ -n ${ZSH_VERSION-} ]]` branch of a dual-shell script,
        // e.g. git-completion.bash's `unset ${(M)${(k)parameters[@]}:#pat}`.
        //
        // bash PARSES such a file fine (it only errors as "bad substitution" if the
        // dead branch ever runs), so rejecting the file would be stricter than the
        // oracle. But emitting the bare `$env:` is not valid PowerShell ("':' was
        // not followed by a valid variable name character") and killed the parse of
        // the entire file. Degrade to the empty string: the expansion contributes
        // nothing, which is what the unreachable branch would have wanted anyway.
        if (name.Length == 0)
            return "''";

        name = ResolveNameref(name);
        // `declare -n ref='a[2]'`: the nameref names an array element.
        if (IsNamerefElementTarget(name))
        {
            int lb = name.IndexOf('[');
            string elem = EmitBracedVar(new WordPart.BracedVarSub(name[..lb], name[lb..]), inDoubleQuote);
            // Glued to a literal (`ref=$ref`) a bare `$a['2']` would expand only `$a`.
            return !inDoubleQuote && elem.StartsWith('$') && !elem.StartsWith("$(") ? "$(" + elem + ")" : elem;
        }
        // Loop variables emit as $var, not $env:var
        if (_loopVars is not null && _loopVars.Contains(name))
            return $"${name}";

        if (arrayElem0 && _arrayVars is not null && _arrayVars.Contains(name))
            // $Matches (BASH_REMATCH) is keyed by int; every other array reads by the string key.
            return "$(if ($null -eq $" + name + ") { $env:" + name + " } else { $" + name
                + (name == "BASH_REMATCH" ? "[0]" : "['0']") + " })";

        var special = PsBuild.TryMapSpecialVar(name, braced: false, inDoubleQuote);
        if (special is not null) return strict && _nounsetMode ? StrictPositional(name, special) : special;
        var plain = $"$env:{EnvShadowName(name) ?? name}";
        return strict && _nounsetMode && EnvShadowName(name) is null ? StrictRead(name, plain) : plain;
    }

    /// <summary>`$1`..`$N` past the last positional parameter is unbound under nounset (`$@` `$*` `$#` `$0` never are).</summary>
    private static string StrictPositional(string name, string special)
        => name.Length > 0 && name[0] != '0' && name.All(char.IsAsciiDigit)
            ? $"$(if ($global:__BashNounset -and @(if ($global:BashPositional) {{ $global:BashPositional }} else {{ $args }}).Count -lt {name}) {{ {PsBuild.ParamErrorThrowStatements(PsBuild.SingleQuote(name + ": "), "'unbound variable'")} }} else {{ {special} }})"
            : special;

    /// <summary>A read of <paramref name="name"/> that ends the script ("unbound variable") when nounset is on and it is unset.</summary>
    private static string StrictRead(string name, string plain)
        => $"$(if ($global:__BashNounset -and $null -eq {plain}) {{ {PsBuild.ParamErrorThrowStatements(PsBuild.SingleQuote(name + ": "), "'unbound variable'")} }} else {{ {plain} }})";

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

        // A loop variable bound from quoted items holds an arbitrary string: it splits like any variable
        // (EmitSimpleVar renders it bare, `$cfg`, which the split array takes just as well as `$env:cfg`).
        if (_splitLoopVars is not null && _splitLoopVars.Contains(name))
            return true;

        // The single source of truth: a name is RC-7-splat-eligible iff
        // EmitSimpleVar would render it as `$env:NAME`. Loop vars, special
        // params, positional digits, and PS-builtin-backed names all take a
        // different branch in EmitSimpleVar and must not be word-split.
        return EmitSimpleVar(name, arrayElem0: false).StartsWith("$env:", StringComparison.Ordinal);
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
            WordPart.SimpleVarSub vs => EmitSimpleVar(vs.Name, strict: true),
            WordPart.BracedVarSub bvs => EmitSimpleVar(bvs.Name, strict: true),
            _ => throw new InvalidOperationException(
                "EmitUnquotedVarSplitArray requires a pure unquoted variable " +
                "word; guard with IsPureUnquotedVarWord."),
        };

        return PsBuild.WordSplitArray(varRef);
    }

    /// <summary>
    /// True when <paramref name="word"/> is a single bare (unquoted) command substitution
    /// (<c>$(cmd)</c> or <c>`cmd`</c>): bash word-splits its output on IFS and glob-expands each word.
    /// A substitution mixed into a longer word (<c>x$(cmd)y</c>) or inside quotes is not "pure" here.
    /// </summary>
    private static bool IsPureUnquotedCommandSubWord(CompoundWord word)
        => word.Parts.Length == 1 && word.Parts[0] is WordPart.CommandSub;

    /// <summary>A word whose expansion is word-split (and elided when empty): see RC-7 and
    /// <see cref="IsPureUnquotedCommandSubWord"/>.</summary>
    private static bool IsUnquotedSplitWord(CompoundWord word)
        => IsPureUnquotedVarWord(word) || IsUnquotedPositionalListWord(word) || IsPureUnquotedCommandSubWord(word) || IsGlobWord(word)
           || IsBraceGlobWord(word) || IsArrayValuedWord(word) || IsUnquotedDefaultAltWord(word) || IsUnquotedElementWord(word) || IsUnquotedPositionalOpWord(word)
           || IsUnquotedPositionalAffixWord(word);

    /// <summary>Bare <c>${@#a}</c> / <c>${*/x/y}</c> / <c>${@%d}</c>: the operator applies to each parameter, so the result is a list of words.</summary>
    private static bool IsUnquotedPositionalOpWord(CompoundWord word)
        => word.Parts is [WordPart.BracedVarSub { Name: "@" or "*", Suffix: { Length: > 0 } s }] && (s[0] is '^' or ',' || s.Length > 1 && s[0] is '/' or '%' or '#');

    /// <summary>Bare <c>$@</c> / <c>$*</c> (also <c>${@}</c>): every parameter, each split again on IFS and globbed.</summary>
    private static bool IsUnquotedPositionalListWord(CompoundWord word)
        => word.Parts is [WordPart.SimpleVarSub { Name: "@" or "*" }]
           or [WordPart.BracedVarSub { Name: "@" or "*", Suffix: null }];

    /// <summary>
    /// The index of the one unquoted expansion in a word that glues literal or quoted text to it: <c>=$*=</c>, <c>a$@</c>,
    /// and with <paramref name="ordinaryVar"/> also <c>a$x</c> / <c>$x.bak</c>. -1 when the word is not that shape (no text,
    /// two expansions, a glob, a substitution, an operator). bash splits such a word: see BashWordSplitter.AffixFields.
    /// </summary>
    private static int UnquotedAffixExpansionIndex(CompoundWord word, bool ordinaryVar)
    {
        if (word.Parts.Length < 2) return -1;
        int found = -1;
        for (int i = 0; i < word.Parts.Length; i++)
        {
            var p = word.Parts[i];
            string? name = p switch { WordPart.SimpleVarSub s => s.Name, WordPart.BracedVarSub { Suffix: null } b => b.Name, _ => null };
            if (name is not null && (name is "@" or "*" || ordinaryVar && (IsOrdinaryVarName(name) || name.Length > 0 && name.All(char.IsAsciiDigit))))
            {
                if (found >= 0) return -1;
                found = i;
                continue;
            }
            if (p is not (WordPart.Literal or WordPart.EscapedLiteral or WordPart.SingleQuoted or WordPart.DoubleQuoted)) return -1;
        }
        return found;
    }

    /// <summary><c>=$*=</c> / <c>a$@</c> unquoted: one field per parameter (each split on IFS), the text glued to the first and last.</summary>
    private static bool IsUnquotedPositionalAffixWord(CompoundWord word) => UnquotedAffixExpansionIndex(word, ordinaryVar: false) >= 0;

    /// <summary>The fields of an affixed expansion word (see <see cref="UnquotedAffixExpansionIndex"/>), as an array expression.</summary>
    private static string EmitAffixWordArray(CompoundWord word, int idx)
    {
        var parts = word.Parts;
        string pre = idx == 0 ? "''" : FlattenPartsToDoubleQuotedString(ImmutableArray.Create(parts, 0, idx));
        string suf = idx == parts.Length - 1 ? "''" : FlattenPartsToDoubleQuotedString(ImmutableArray.Create(parts, idx + 1, parts.Length - idx - 1));
        string values = parts[idx] is WordPart.SimpleVarSub { Name: "@" or "*" } or WordPart.BracedVarSub { Name: "@" or "*" }
            ? "@(" + PsBuild.BuildPositionalExpansion("@") + ")"
            : "@(" + EmitWord(new CompoundWord(ImmutableArray.Create(parts[idx]))) + ")";
        return "@(ConvertTo-BashWords -Prefix " + pre + " -Suffix " + suf + " -Fields " + values + ")";
    }

    /// <summary>
    /// Bare <c>${a[i]}</c> (one subscript, not <c>@</c>/<c>*</c>): the element is word-split like any unquoted expansion, so an
    /// unset or empty element is no argument at all (<c>echo x ${a[7]} y</c> prints <c>x y</c>).
    /// </summary>
    private static bool IsUnquotedElementWord(CompoundWord word)
        => word.Parts is [WordPart.BracedVarSub { Name: var n, Suffix: { Length: > 2 } s }]
           && IsOrdinaryVarName(n) && s[0] == '[' && s[^1] == ']' && s.IndexOf(']') == s.Length - 1
           && s is not ("[@]" or "[*]") && s[1] != '@' && s[1] != '*';
    /// <summary>The suffix of <c>${#v}</c> / <c>${#a[@]}</c> / <c>${#a[0]}</c>: a number, never split.</summary>
    private static bool IsLengthSuffix(string s)
        => s == "#L" || (s.Length > 3 && s[0] == '#' && s[1] == 'L' && s[2] == '[' && s[^1] == ']' && s.IndexOf(']') == s.Length - 1);

    /// <summary>
    /// Bare <c>${v:-x}</c> / <c>${v+x}</c> / <c>${v#p}</c> / <c>${v/a/b}</c> / <c>${v:1}</c> / <c>${v^^}</c>: the result is word-split like any unquoted
    /// expansion, so an empty result is no argument at all (<c>echo ${empty:+x} y</c> prints <c>y</c>, not <c> y</c>).
    /// Positional-parameter names (<c>$@</c>, <c>$*</c>) are arrays with their own rules and are left alone.
    /// </summary>
    private static bool IsUnquotedDefaultAltWord(CompoundWord word)
        => word.Parts is [WordPart.BracedVarSub { Suffix: { } s, Name: var n }]
           && (IsOrdinaryVarName(n) && n is not ("@" or "*") || IsPositionalListDefaultAlt(n, s))
           // `${v:-"A B"}`: the quoted default is one word and is not split again.
           && !(s.Length > 1 && s[0] is ':' or '-' or '+' && s.IndexOf('"') > 0)
           && (s.StartsWith(":-") || s.StartsWith(":+") || (s.Length > 0 && s[0] is '-' or '+')
               // Trim / replace / slice / case-change results are split like any other unquoted expansion:
               // `${s#*o}` of "Hello World" is the two words " World" -> World, not one argument with its space.
               || (s.Length > 0 && s[0] is '#' or '%' or '/' or '^' or ',' && !IsLengthSuffix(s))
               || (s.Length == 2 && s[0] == '@' && s[1] is 'Q' or 'K' or 'k' or 'A' or 'a')
               || (s.Length > 1 && s[0] == ':' && s[1] is not ('-' or '+' or '=' or '?')));

    /// <summary>`${@-w}` `${*:+w}`: the default / alternative operators on the positional LIST (see <see cref="EmitPositionalWordOp"/>).</summary>
    private static bool IsPositionalListDefaultAlt(string name, string suffix)
        => name is "@" or "*" && (suffix.StartsWith(":-") || suffix.StartsWith(":+") || suffix.StartsWith('-') || suffix.StartsWith('+'));

    /// <summary>
    /// Bare <c>${a[@]}</c> / <c>${a[*]}</c>: every element, each split again on IFS. Left as a bare array it is
    /// ONE argument, and an array beside any other argument cannot bind (<c>echo ${a[@]} x</c> failed with
    /// "Cannot convert System.Object[] to String").
    /// </summary>
    private static bool IsArrayValuedWord(CompoundWord word)
        => IsUnquotedArrayAllWord(word) || IsQuotedArrayAllWord(word) || IsPlainBraceWord(word);

    /// <summary>
    /// Exactly <c>"${a[@]}"</c> or <c>"$@"</c>: each element is its OWN argument, never split again. Joined into one
    /// string it made <c>printf '[%s]' "${a[@]}"</c> print <c>[a b c]</c> instead of <c>[a][b][c]</c>.
    /// </summary>
    private static bool IsQuotedArrayAllWord(CompoundWord word)
        => word.Parts is [WordPart.DoubleQuoted { Parts: [var only] }]
           && (only is WordPart.SimpleVarSub { Name: "@" }
               or WordPart.BracedVarSub { Name: "@", Suffix: null }
               or WordPart.BracedVarSub { Suffix: "[@]" or "![@]" }
               || only is WordPart.BracedVarSub { Suffix: { } opSfx } && (IsArrayElementOperator(opSfx) || IsArraySliceSuffix(opSfx))
               || only is WordPart.BracedVarSub { Name: "@", Suffix: { } posSlice } && IsPositionalSliceSuffix(posSlice));

    /// <summary>`[@]:off` / `[@]:off:len`: a slice of the array (`:-` `:=` `:+` `:?` are the default operators, not slices).</summary>
    /// <summary>:off / :off:len on $@ (a slice; :- := :+ :? are the default operators).</summary>
    private static bool IsPositionalSliceSuffix(string suffix)
        => suffix.Length > 1 && suffix[0] == ':' && (char.IsAsciiDigit(suffix[1]) || suffix[1] is ' ' or '$' or '(');

    private static bool IsArraySliceSuffix(string suffix)
        => suffix.Length > 4 && suffix.StartsWith("[@]:", StringComparison.Ordinal) && (char.IsAsciiDigit(suffix[4]) || suffix[4] is ' ' or '$' or '(');
    /// <summary>`[@]` followed by a per-element operator (`/ # % ^ ,`): `${a[@]/o/0}`, `${a[@]%.*}`, `${a[@]^^}`.</summary>
    private static bool IsArrayElementOperator(string suffix)
        => suffix.Length > 3 && suffix.StartsWith("[@]", StringComparison.Ordinal) && suffix[3] is '/' or '#' or '%' or '^' or ',';

    /// <summary>A brace expansion (<c>{1..5}</c>, <c>x{a,b}</c>) with no glob part: an array of words at run time.</summary>
    private static bool IsPlainBraceWord(CompoundWord word)
    {
        foreach (var part in word.Parts)
            if (part is WordPart.BracedTuple or WordPart.BracedRange)
                return !IsBraceGlobWord(word);
        return false;
    }

    private static bool IsUnquotedArrayAllWord(CompoundWord word)
        => word.Parts is [WordPart.BracedVarSub { Suffix: { } s }]
           && (s.StartsWith("[@]", StringComparison.Ordinal) || s.StartsWith("[*]", StringComparison.Ordinal)
               || s.StartsWith('!'));

    /// <summary>
    /// The array an unquoted split word expands to, for PowerShell <c>@</c>-splatting or flattening into a
    /// list: a variable or a command substitution goes through <c>ConvertTo-BashWords</c> (IFS split, then
    /// pathname expansion of each word), fed the value / the substitution's whole text; a glob word goes
    /// through <c>ConvertTo-BashGlob</c> (see <see cref="EmitGlobPatternExpr"/>).
    /// </summary>
    private static string EmitQuotedArrayElements(WordPart part) => part switch
    {
        WordPart.BracedVarSub { Name: "@", Suffix: null } or WordPart.SimpleVarSub { Name: "@" }
            => "@($(if ($global:BashPositional) { $global:BashPositional } else { $args }))",
        _ => "@(" + EmitBracedVar((WordPart.BracedVarSub)part) + ")",
    };

    private static string EmitUnquotedSplitWordArray(CompoundWord word)
        => IsUnquotedPositionalAffixWord(word)
            ? EmitAffixWordArray(word, UnquotedAffixExpansionIndex(word, ordinaryVar: false))
            : IsUnquotedPositionalOpWord(word)
            ? "@(" + EmitBracedVar((WordPart.BracedVarSub)word.Parts[0]) + " | ConvertTo-BashWords)"
            : IsQuotedArrayAllWord(word)
            ? EmitQuotedArrayElements(((WordPart.DoubleQuoted)word.Parts[0]).Parts[0])
            : IsPlainBraceWord(word)
            ? "@(" + EmitBraceExpandedWord(word.Parts) + ")"
            : IsUnquotedArrayAllWord(word)
            // ConvertTo-BashWords takes ONE string: the elements joined by a space split back apart on IFS.
            ? PsBuild.WordSplitArray("(@(" + EmitBracedVar((WordPart.BracedVarSub)word.Parts[0]) + ") -join ' ')")
            : IsUnquotedPositionalListWord(word)
            // Joined with IFS[0] and split again (IFS=x, `"" ""` is one empty field); IFS='' keeps each parameter whole.
            ? "@(ConvertTo-BashWords -Prefix '' -Suffix '' -Fields @(" + PsBuild.BuildPositionalExpansion("@") + "))"
            : IsBraceGlobWord(word)
            // `*.{c,h}` / `sub/{a,b}*`: brace expansion comes FIRST, then every resulting word globs.
            ? "@(" + EmitBraceExpandedWord(word.Parts) + " | ConvertTo-BashGlob)"
            : IsGlobWord(word)
            ? PsBuild.GlobWordArray(EmitGlobPatternExpr(word))
            : IsUnquotedDefaultAltWord(word) && word.Parts[0] is WordPart.BracedVarSub { Name: "@" or "*" }
                ? "@(" + EmitBracedVar((WordPart.BracedVarSub)word.Parts[0]) + ")"
            : IsUnquotedDefaultAltWord(word) || IsUnquotedElementWord(word)
                ? PsBuild.WordSplitArray("(" + EmitBracedVar((WordPart.BracedVarSub)word.Parts[0]) + ")")
            : word.Parts[0] is WordPart.CommandSub cs
                ? "@(ConvertTo-BashWords " + EmitCommandSubString(cs, nested: false) + ")"
                : EmitUnquotedVarSplitArray(word);

    // ───────────────────────────── Pathname expansion (globbing) ─────────────────────────────
    //
    // bash's expansion order ends  … word splitting -> PATHNAME EXPANSION -> quote removal, and the
    // SHELL does it: `echo *` prints file names because echo is handed names. The mapped cmdlets used to
    // get the literal pattern (and some expanded it themselves, with their own dialect: hidden files
    // included, absolute paths), while echo / printf / functions / natives / for lists / arrays never
    // expanded at all. A glob word is therefore expanded HERE, by ConvertTo-BashGlob, and the command
    // receives the names. See docs/specs/emitter-strategy.md "Pathname expansion".

    /// <summary>
    /// A word with an UNQUOTED glob part (<c>*</c>, <c>?</c>, <c>[set]</c>) the shell expands: every other
    /// part must be one the pattern builder can render (literals, quotes, expansions, tilde). Words with an
    /// extglob (<c>+(a|b)</c>, not implemented), process substitution or brace expansion (<c>*.{c,h}</c>: its
    /// own array form) keep their old emission.
    /// </summary>
    private static bool IsGlobWord(CompoundWord word)
    {
        bool glob = false;
        foreach (var part in word.Parts)
        {
            switch (part)
            {
                case WordPart.GlobPart gp:
                    glob = true;
                    break;
                case WordPart.Literal or WordPart.EscapedLiteral or WordPart.SingleQuoted or WordPart.AnsiCQuoted
                    or WordPart.DoubleQuoted or WordPart.SimpleVarSub or WordPart.BracedVarSub
                    or WordPart.CommandSub or WordPart.ArithSub or WordPart.TildeSub:
                    break;
                default:
                    return false;
            }
        }
        return glob;
    }

    /// <summary>Backslash-escapes <c>* ? [ \</c> so quoted / escaped text matches itself in a glob pattern.</summary>
    private static string EscapeGlobLiteral(string text)
    {
        if (text.AsSpan().IndexOfAny("*?[\\") < 0) return text;
        var sb = new StringBuilder(text.Length + 4);
        foreach (char c in text)
        {
            if (c is '*' or '?' or '[' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// A word with a brace expansion AND a glob (<c>*.{c,h}</c>, <c>sub/{a,b}*</c>) made only of plain literals,
    /// brace parts and glob parts. bash expands the braces first; the emitter's brace array carries each
    /// resulting word's text with the glob characters intact, so piping it through <c>ConvertTo-BashGlob</c>
    /// finishes the job. Quoted or dynamic parts beside a brace keep the existing emission.
    /// </summary>
    private static bool IsBraceGlobWord(CompoundWord word)
    {
        bool brace = false, glob = false;
        foreach (var part in word.Parts)
        {
            switch (part)
            {
                case WordPart.BracedTuple or WordPart.BracedRange:
                    brace = true;
                    break;
                case WordPart.GlobPart gp:
                    glob = true;
                    break;
                case WordPart.Literal:
                    break;
                default:
                    return false;
            }
        }
        return brace && glob;
    }

    /// <summary>
    /// A PowerShell expression for the glob pattern TEXT of <paramref name="word"/> (guard with
    /// <see cref="IsGlobWord"/>), in bash's dialect: glob parts verbatim, everything quoted or escaped
    /// backslash-escaped, unquoted expansions spliced in active (their glob characters match, as in bash),
    /// quoted expansions escaped at run time. A drive-style prefix (<c>/c/Users/</c>) and <c>/tmp/</c> are
    /// mapped like any other path word, with forward slashes (the pattern's only separator).
    /// </summary>
    /// <param name="word">The glob word.</param>
    /// <param name="mapPaths">False for a pattern that is not a path (<c>[[ $p == /tmp/$x ]]</c> compares
    /// text): no <c>/tmp/</c> or drive-prefix mapping.</param>
    private static string EmitGlobPatternExpr(CompoundWord word, bool mapPaths = true)
    {
        const string Escape = "[PsBash.Cmdlets.BashGlobText]::Escape(";
        var pieces = new List<string>();
        var lit = new StringBuilder();

        void Flush()
        {
            if (lit.Length == 0) return;
            pieces.Add(PsBuild.SingleQuote(lit.ToString()));
            lit.Clear();
        }
        void Dynamic(string expr)
        {
            Flush();
            pieces.Add("(" + expr + ")");
        }

        for (int i = 0; i < word.Parts.Length; i++)
        {
            switch (word.Parts[i])
            {
                case WordPart.Literal l:
                {
                    string text = l.Value;
                    if (mapPaths && i == 0 && text.StartsWith("/tmp/", StringComparison.Ordinal))
                    {
                        Dynamic(PsBuild.TempDirExpr + ".Replace('\\','/')");
                        text = text[4..];
                    }
                    else if (mapPaths && i == 0 && TryTranslateMsysDrivePath(text, out var drive))
                        text = drive.Replace('\\', '/');
                    lit.Append(EscapeGlobLiteral(text));
                    break;
                }
                case WordPart.EscapedLiteral e:
                    lit.Append(EscapeGlobLiteral(e.Value));
                    break;
                case WordPart.SingleQuoted sq:
                    lit.Append(EscapeGlobLiteral(sq.Value));
                    break;
                case WordPart.AnsiCQuoted aq:
                    lit.Append(EscapeGlobLiteral(ExpandAnsiCEscapes(aq.Value)));
                    break;
                case WordPart.GlobPart gp:
                    lit.Append(gp.Pattern);
                    break;
                case WordPart.DoubleQuoted dq:
                    if (dq.Parts.All(p => p is WordPart.Literal or WordPart.EscapedLiteral)
                        && TryGetStaticArgValue(new CompoundWord(ImmutableArray.Create<WordPart>(dq))) is { } dqText)
                        lit.Append(EscapeGlobLiteral(dqText));
                    else
                        Dynamic(Escape + EmitDoubleQuoted(dq) + ")");
                    break;
                // The parser consumes the `/` after a tilde; EmitWord re-adds it when parts follow.
                case WordPart.TildeSub { User: null or "+" or "-" } ts:
                    Dynamic(Escape + "([string]" + EmitWordPart(ts) + ").Replace('\\','/'))");
                    if (ts.SlashConsumed) lit.Append('/');
                    break;
                case WordPart.TildeSub ts:
                    lit.Append(EscapeGlobLiteral("~" + ts.User));
                    if (ts.SlashConsumed) lit.Append('/');
                    break;
                case WordPart.CommandSub cs:
                    Dynamic(EmitCommandSubString(cs, nested: false));
                    break;
                default: // SimpleVarSub, BracedVarSub, ArithSub: unquoted, so glob-active
                    Dynamic(EmitWordPart(word.Parts[i]));
                    break;
            }
        }
        Flush();

        if (pieces.Count == 1)
            return pieces[0];
        // A leading '' forces string concatenation when the first piece is an expression.
        return "('' + " + string.Join(" + ", pieces) + ")";
    }

    /// <summary>
    /// PowerShell statement keywords that the emitter can produce as a "command
    /// word". They are parsed as STATEMENTS, not commands, so a splatted
    /// argument (<c>@var</c>) after one is a hard PowerShell parse error
    /// ("the splatting operator '@' … can be used only as an argument to a
    /// command"). The RC-7 splat hoist must therefore never fire for them —
    /// <c>exit $code</c> used to emit <c>exit @__bashsplat0</c> and broke the
    /// whole file's parse.
    /// </summary>
    private static readonly HashSet<string> PsStatementKeywordCommands =
        new(StringComparer.Ordinal) { "exit", "return", "break", "continue", "throw" };

    /// <summary>
    /// Emits <c>exit $var</c> for a pure unquoted ordinary variable while keeping
    /// bash's word-splitting semantics WITHOUT the illegal splat (see
    /// <see cref="PsStatementKeywordCommands"/>). In bash an empty unquoted
    /// operand is elided entirely, so <c>x=; exit $x</c> is a bare <c>exit</c> —
    /// which exits with the previous command's status. The value expression is a
    /// <c>$( … )</c> subexpression so the whole thing stays ONE statement and
    /// <c>exit</c> itself is never wrapped in a script block (a wrapped
    /// <c>exit</c> would only leave the block when inside a function).
    /// </summary>
    /// <summary>
    /// The exit-status VALUE of an <c>exit N</c> operand, for the scoped
    /// subshell form. An unquoted variable keeps bash's elision rule (an empty
    /// operand leaves <c>$?</c> alone), mirroring
    /// <see cref="EmitExitWithUnquotedVar"/>.
    /// </summary>
    private static string EmitExitCodeValue(CompoundWord word)
    {
        if (!IsPureUnquotedVarWord(word))
            return EmitWord(word);

        return "$(& { $__bashexit = " + EmitUnquotedVarSplitArray(word) +
               "; if ($__bashexit.Count) { $__bashexit[0] } " +
               "else { $global:LASTEXITCODE } })";
    }

    private static string EmitExitWithUnquotedVar(CompoundWord word)
    {
        string split = EmitUnquotedVarSplitArray(word);
        return "exit $(& { $__bashexit = " + split +
               "; if ($__bashexit.Count) { $__bashexit[0] } " +
               "else { $global:LASTEXITCODE } })";
    }

    /// <summary>
    /// True when any operand in <paramref name="args"/> (optionally skipping the
    /// pipeline-process-sub operand at <paramref name="skipIndex"/>) is a pure
    /// unquoted ordinary variable that needs RC-7 word-splitting. A lone array word binds as the array itself for a
    /// mapped cmdlet, but a function / native command receives it as ONE argument unless splatted
    /// (<paramref name="loneArraySplats"/>).
    /// </summary>
    private static bool HasUnquotedVarSplatArg(
        ImmutableArray<CompoundWord> args, int skipIndex = -1, bool loneArraySplats = false)
    {
        for (int i = 0; i < args.Length; i++)
        {
            // A LONE array word binds as the array itself; only beside another argument must it splat. An UNQUOTED `${a[@]}` is the\r\n            // exception: its elements are word-split and globbed again (`a=("x y"); printf "%s\n" ${a[@]}` is two arguments).
            if (i != skipIndex && IsUnquotedSplitWord(args[i])
                && !(args.Length == 1 && !loneArraySplats && IsArrayValuedWord(args[i]) && !IsUnquotedArrayAllWord(args[i])))
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
        // The block below is a script block: the stage's pipeline input reaches its `$input`, not the
        // command inside. Forward it (see _pipeStageFeedsInput). Read before the args are emitted, which
        // may recurse (a nested substitution clears the flag for its own body).
        if (_pipeStageFeedsInput) call.Append("$input | ");
        call.Append(leadingTokens);
        call.Append(commandName);

        int splatCounter = 0;
        // A word that assigns (`$((n+=2))`, `${w=q}`) must run BEFORE a later word reads the variable, but the
        // hoisted splat temps all run first. Such a word is evaluated in order into the prelude as well, so
        // `echo $((n+=2)) $n` prints `5 5`.
        for (int i = 0; i < args.Length; i++)
        {
            call.Append(' ');
            if (!IsUnquotedSplitWord(args[i]) && MutatesShellState(args[i]))
            {
                string v = $"$__bashsplat{splatCounter++}";
                prelude.Append(v).Append(" = ").Append(emitPlainArg(i)).Append("; ");
                call.Append(v);
            }
            else if (IsUnquotedSplitWord(args[i]))
            {
                string tempVar = $"$__bashsplat{splatCounter++}";
                string emitted = EmitUnquotedSplitWordArray(args[i]);
                prelude.Append(tempVar)
                       .Append(" = ")
                       .Append(emitted)
                       .Append("; ");
                // failglob: the expansion error ends only its own statement, so stop before the command runs.
                // An invalid indirect reference (`${!a}`, a=/) abandons the command too.
                if (IsGlobWord(args[i]) || IsBraceGlobWord(args[i])) prelude.Append("if (-not $?) { return }; ");
                else if (emitted.Contains("Get-BashIndirectValue")) prelude.Append(PsBuild.AbandonIfExpansionFailed);
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

    /// <summary>True when expanding the word assigns a shell variable: `$((n+=2))`, `$((i++))`, `${w=q}`, `${w:=q}`.</summary>
    private static bool MutatesShellState(CompoundWord word)
    {
        foreach (var part in word.Parts)
        {
            if (part is WordPart.BracedVarSub { Suffix: { } s } && (s.StartsWith("=") || s.StartsWith(":=")))
                return true;
            if (part is WordPart.ArithSub a && System.Text.RegularExpressions.Regex.IsMatch(a.Expr.Source, @"\+\+|--|(?<![=!<>])=(?!=)|[-+*/%&|^]=|<<=|>>="))
                return true;
        }
        return false;
    }


    /// <summary>
    /// Split the text after <c>${v/</c> (or <c>${v//</c>) into find and replacement at the first UNQUOTED, UNESCAPED
    /// <c>/</c>. The first character is always part of the pattern, so <c>${p////\\/}</c> (<c>//</c>, pattern <c>/</c>,
    /// replacement <c>\\/</c>) and <c>${x///}</c> (delete every slash) read as bash reads them.
    /// </summary>
    private static (string Find, string Replace) SplitPatsubOperands(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\') { i++; continue; }
            if (c == '\'' || c == '"')
            {
                for (i++; i < s.Length && s[i] != c; i++)
                    if (c == '"' && s[i] == '\\') i++;
                continue;
            }
            if (c == '$' && i + 1 < s.Length && s[i + 1] == '{') { int e = FindBraceEnd(s, i + 1); if (e < 0) break; i = e; continue; }
            if (c == '$' && i + 1 < s.Length && s[i + 1] == '(') { i = BashLexer.ScanBalancedParens(s, i + 2, 1) - 1; continue; }
            if (c == '/' && i > 0) return (s[..i], s[(i + 1)..]);
        }
        return (s, "");
    }

    /// <summary>
    /// A glob pattern with its quotes removed and every QUOTED character backslash-escaped so it stays literal
    /// (<c>[^'&gt;&lt;+-.,[]']</c>). Null when there is nothing to do: no quotes, a <c>$</c> expansion, or no unquoted glob character.
    /// </summary>
    private static string? UnquoteGlobPattern(string raw)
    {
        if (raw.IndexOfAny(['\'', '"']) < 0 || raw.IndexOf('$') >= 0) return null;
        var sb = new StringBuilder(raw.Length + 8);
        bool anyGlob = false;
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '\\' && i + 1 < raw.Length) { sb.Append(c).Append(raw[++i]); continue; }
            if (c is '\'' or '"')
            {
                int end = raw.IndexOf(c, i + 1);
                if (end < 0) return null;
                for (int j = i + 1; j < end; j++)
                {
                    if (!char.IsAsciiLetterOrDigit(raw[j])) sb.Append('\\');
                    sb.Append(raw[j]);
                }
                i = end;
                continue;
            }
            if (c is '*' or '?' or '[') anyGlob = true;
            sb.Append(c);
        }
        return anyGlob ? sb.ToString() : null;
    }

    /// <summary>
    /// The text of a <c>${v/find/rep}</c> operand that carries quoting or a variable (<c>$'\n'</c>, <c>'x'</c>,
    /// <c>"x"</c>, <c>$nl</c>, <c>${nl}</c>) as one PowerShell string expression, so the match is the quote-removed
    /// value and not the raw source text. Null when the operand has none of these (the plain literal path applies)
    /// or contains an unquoted glob character (kept on the pattern path).
    /// </summary>
    /// <summary>
    /// A glob pattern with its quoting removed: <c>$'b'*</c> / <c>'a'*</c> / <c>"x"?</c> become the pattern text
    /// where the QUOTED characters are backslash-escaped (literal) and the bare <c>* ? [</c> keep their meaning.
    /// A pattern with an expansion in it (or no quote at all) is returned unchanged.
    /// </summary>
    private static string UnquoteForGlob(string raw)
    {
        if (raw.IndexOfAny(['\'', '"']) < 0) return raw;
        var sb = new StringBuilder(raw.Length + 4);
        void Lit(string s) { foreach (char ch in s) { if (ch is '*' or '?' or '[' or ']' or '\\') sb.Append('\\'); sb.Append(ch); } }
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '$' && i + 1 < raw.Length && raw[i + 1] == '\'')
            {
                int end = raw.IndexOf('\'', i + 2);
                while (end > 0 && raw[end - 1] == '\\' && (end < 2 || raw[end - 2] != '\\')) end = raw.IndexOf('\'', end + 1);
                if (end < 0) return raw;
                Lit(ExpandAnsiCEscapes(raw[(i + 2)..end]));
                i = end;
            }
            else if (c == '\'')
            {
                int end = raw.IndexOf('\'', i + 1);
                if (end < 0) return raw;
                Lit(raw[(i + 1)..end]);
                i = end;
            }
            else if (c == '"')
            {
                int end = raw.IndexOf('"', i + 1);
                if (end < 0 || raw.AsSpan(i + 1, end - i - 1).IndexOf('$') >= 0) return raw;
                Lit(raw[(i + 1)..end]);
                i = end;
            }
            else if (c == '\\' && i + 1 < raw.Length) { sb.Append(c).Append(raw[++i]); }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string? TryEmitSubstitutionOperand(string raw, bool backslashEscapes = false)
    {
        if (raw.IndexOfAny(backslashEscapes ? ['$', '\'', '"', '\\'] : ['$', '\'', '"']) < 0) return null;
        var segs = new List<string>();
        var lit = new StringBuilder();
        void Flush() { if (lit.Length > 0) { segs.Add(PsBuild.SingleQuote(lit.ToString())); lit.Clear(); } }
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '$' && i + 1 < raw.Length && raw[i + 1] == '\'')
            {
                int end = raw.IndexOf('\'', i + 2);
                while (end > 0 && raw[end - 1] == '\\' && (end < 2 || raw[end - 2] != '\\')) end = raw.IndexOf('\'', end + 1);
                if (end < 0) return null;
                lit.Append(ExpandAnsiCEscapes(raw[(i + 2)..end]));
                i = end;
            }
            else if (c == '\'')
            {
                int end = raw.IndexOf('\'', i + 1);
                if (end < 0) return null;
                lit.Append(raw, i + 1, end - i - 1);
                i = end;
            }
            else if (c == '"')
            {
                int end = -1;
                for (int j = i + 1; j < raw.Length && end < 0; j++)
                {
                    if (raw[j] == '"') end = j;
                    else if (raw[j] == '$' && j + 1 < raw.Length && raw[j + 1] == '{')
                    {
                        j = FindBraceEnd(raw, j + 1);
                        if (j < 0) return null;
                    }
                }
                if (end < 0) return null;
                string inner = raw[(i + 1)..end];
                if (inner.IndexOf('$') >= 0)
                {
                    // "${s%%[![:space:]]*}" / "$x-$y": expanded by the shell, then matched as literal text.
                    if (BashParser.Parse("echo \"" + inner + "\"") is not Command.Simple { Words.Length: 2 } echoCmd) return null;
                    Flush(); segs.Add("[string](" + EmitWord(echoCmd.Words[1]) + ")");
                }
                else lit.Append(inner);
                i = end;
            }
            else if (c == '$' && i + 1 < raw.Length && (char.IsAsciiLetter(raw[i + 1]) || raw[i + 1] == '_' || raw[i + 1] == '{'))
            {
                int s = i + 1, e;
                if (raw[s] == '{')
                {
                    e = raw.IndexOf('}', s);
                    if (e < 0) return null;
                    string name = raw[(s + 1)..e];
                    if (name.Length == 0 || !name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_')) return null;
                    Flush(); segs.Add("[string](" + EmitSimpleVar(name, strict: true) + ")");
                    i = e;
                }
                else
                {
                    e = s;
                    while (e < raw.Length && (char.IsAsciiLetterOrDigit(raw[e]) || raw[e] == '_')) e++;
                    Flush(); segs.Add("[string](" + EmitSimpleVar(raw[s..e], strict: true) + ")");
                    i = e - 1;
                }
            }
            else if (c == '\\' && backslashEscapes)
            {
                // Quote removal: `\\` is one backslash, `\"` a quote, `\*` a literal star, `\n` the letter n.
                if (i + 1 < raw.Length) lit.Append(raw[++i]); else lit.Append(c);
            }
            else if (c is '*' or '?' or '[' or '\\' or '$') return null;
            else lit.Append(c);
        }
        Flush();
        return segs.Count == 0 ? "''" : "(-join @(" + string.Join(", ", segs) + "))";
    }
    // `${!a[1]}`: the indirection's reference is the value of ONE array element (the parser keeps `a[1]` as the name).
    private static string IndirectRef(string name, bool strict)
    {
        var m = System.Text.RegularExpressions.Regex.Match(name, @"^([A-Za-z_]\w*)\[(.+)\]$");
        return m.Success
            ? EmitBracedVar(new WordPart.BracedVarSub(m.Groups[1].Value, "[" + m.Groups[2].Value + "]"))
            : EmitSimpleVar(name, strict: strict);
    }
    private static string EmitBracedVar(WordPart.BracedVarSub bvs, bool inDoubleQuote = false)
    {
        NestingGuard.EnsureStack(); // ${x:-${x:-…}} recurses here without passing Emit(Command)
        // `${!ref}` of a nameref is the NAME it refers to (bash inverts the usual meaning).
        if (bvs.Suffix == "!indirect" && _namerefs is { Count: > 0 } && _namerefs.TryGetValue(bvs.Name, out var namerefTargetName))
            return inDoubleQuote ? "$(" + PsBuild.SingleQuote(namerefTargetName) + ")" : PsBuild.SingleQuote(namerefTargetName);
        if (_namerefs is { Count: > 0 } && _namerefs.ContainsKey(bvs.Name))
            bvs = bvs with { Name = ResolveNameref(bvs.Name) };
        if (bvs.Suffix is null)
            return EmitSimpleVar(bvs.Name, strict: true);

        if (bvs.Suffix.StartsWith("#BAD:", StringComparison.Ordinal))
            return "$(" + PsBuild.ParamErrorThrowStatements(PsBuild.SingleQuote(bvs.Suffix[5..] + ": "), "'bad substitution'") + ")";

        // Positional-parameter SLICE: ${@:offset[:length]} / ${*:…}. Unhandled, this
        // fell through to the ordinary-variable path and emitted the bare `$env:`
        // (empty name) — "':' was not followed by a valid variable name character",
        // which broke the whole file. `${@: -1}` (the last argument) is the common
        // real-world spelling; it is how zoxide's shell hook reads its target dir.
        if (bvs.Name is "@" or "*" && bvs.Suffix.StartsWith(':')
            && EmitPositionalSlice(bvs.Suffix, inDoubleQuote) is { } posSlice)
        {
            return posSlice;
        }

        // `${v@a}` / `${a[0]@a}`: the attribute letters of the variable (array kind, then i/r/x).
        if (System.Text.RegularExpressions.Regex.IsMatch(bvs.Name, @"^[A-Za-z_]\w*$")
            && System.Text.RegularExpressions.Regex.IsMatch(bvs.Suffix, @"^@[AKk]$"))
        {
            string q = PsBuild.SingleQuote(bvs.Name);
            return "$([PsBash.Cmdlets.BashQuoteText]::Transform('" + bvs.Suffix[1] + "', " + q + ", (Get-Variable -Name " + q + " -ValueOnly -ErrorAction Ignore)))";
        }
        if (System.Text.RegularExpressions.Regex.IsMatch(bvs.Name, @"^[A-Za-z_]\w*$")
            && System.Text.RegularExpressions.Regex.IsMatch(bvs.Suffix, @"^(\[[^@*\]]*\])?@a$"))
        {
            string attrs = $"[PsBash.Cmdlets.BashQuoteText]::Attributes({PsBuild.SingleQuote(bvs.Name)}, (Get-Variable -Name {PsBuild.SingleQuote(bvs.Name)} -ValueOnly -ErrorAction Ignore))";
            return "$(" + attrs + ")";
        }
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
                    "BASH_SOURCE" => "$global:BASH_SOURCE",
                    "FUNCNAME" => "@(Get-BashFuncNames)",
                    var n => $"${n}"
                };

                if (subOp.Length == 0)
                {
                    // ${arr[@]} -> $arr (whole array); an associative array (hashtable) yields its VALUES.
                    // "${a[*]}" joins with the first character of IFS ("${a[@]}" with a space).
                    // A SCALAR (`s=abc`) lives in the environment, not in a PowerShell variable: `${s[@]}` / `${s[0]}` are
                    // the scalar itself (bash treats it as a one-element array).
                    string whole = bvs.Name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && !char.IsAsciiDigit(bvs.Name[0])
                        && bvs.Name is not ("BASH_VERSINFO" or "BASH_SOURCE" or "FUNCNAME")
                        ? $"$(if ($null -ne {arrayVar}) {{ {arrayVar} }} else {{ {EmitSimpleVar(bvs.Name)} }})"
                        : arrayVar;
                    if (subscript == "0" && !ReferenceEquals(whole, arrayVar))
                    {
                        // $Matches (BASH_REMATCH) is keyed by int; every other array reads by the string key.
                        string zero = bvs.Name == "BASH_REMATCH" ? "0" : "'0'";
                        return $"$(if ($null -eq {arrayVar}) {{ {EmitSimpleVar(bvs.Name)} }} else {{ {arrayVar}[{zero}] }})";
                    }
                    if (subscript == "*" && inDoubleQuote)
                        return $"$(@(if ({arrayVar} -is [System.Collections.IDictionary]) {{ {arrayVar}.Values }} else {{ {whole} }}) -join {PsBuild.IfsJoinSeparator})";
                    if (subscript is "@" or "*")
                        return $"$(if ({arrayVar} -is [System.Collections.IDictionary]) {{ {arrayVar}.Values }} else {{ {whole} }})";
                    // ${m[$k]} / ${a[$i]}: the subscript is a variable's value.
                    if (System.Text.RegularExpressions.Regex.Match(subscript, @"^\$\{?(\w+)\}?$") is { Success: true } sv)
                    {
                        string subRef = $"{arrayVar}[{EmitVarSubscript(arrayVar, EmitSimpleVar(sv.Groups[1].Value))}]";
                        return inDoubleQuote ? $"$({subRef})" : subRef;
                    }
                    if (int.TryParse(subscript, out _))
                    {
                        // $Matches (BASH_REMATCH) is keyed by int; every other array reads by the string key.
                        string key = bvs.Name == "BASH_REMATCH" ? subscript : $"'{subscript}'";
                        return inDoubleQuote ? $"$({arrayVar}[{key}])" : $"{arrayVar}[{key}]";
                    }
                    string keyRef = $"{arrayVar}[{EmitWordSubscript(arrayVar, subscript)}]";
                    return inDoubleQuote ? $"$({keyRef})" : keyRef;
                }

                // Whole-array operator: only slicing (${arr[@]:off:len}) maps cleanly to
                // a PowerShell range index; other ops degrade to the whole array.
                if (subscript is "@" or "*")
                {
                    if (EmitArraySlice(arrayVar, subOp, inDoubleQuote) is { } slice) return slice;
                    // `${a[@]@Q}` / `${a[@]@a}`: quote-for-reuse and the attribute letters apply to EACH element.
                    if (subOp is "@Q" or "@a")
                    {
                        string srcv = $"$(if ({arrayVar} -is [System.Collections.IDictionary]) {{ {arrayVar}.Values }} else {{ {arrayVar} }})";
                        string each = subOp == "@Q"
                            ? "[PsBash.Cmdlets.BashQuoteText]::ForReuse([string]$_)"
                            : $"$(if ({arrayVar} -is [System.Collections.IDictionary]) {{ 'A' }} else {{ 'a' }})";
                        string mappedQ = $"@({srcv}) | ForEach-Object {{ {each} }}";
                        return subscript == "*" && inDoubleQuote
                            ? $"$(@({mappedQ}) -join {PsBuild.IfsJoinSeparator})"
                            : $"$({mappedQ})";
                    }
                    // `${a[@]/o/0}` `${a[@]%o}` `${a[@]#?}` `${a[@]^^}`: the operator applies to EACH element.
                    if (subOp[0] is '/' or '#' or '%' or '^' or ',')
                    {
                        const string elem = "$__ae";
                        string source = $"$(if ({arrayVar} -is [System.Collections.IDictionary]) {{ {arrayVar}.Values }} else {{ {arrayVar} }})";
                        if (EmitScalarSuffix(elem, subOp, false, bvs.ArgWord) is { } perElement)
                        {
                            string mapped = $"@({source}) | ForEach-Object {{ {elem} = [string]$_; {perElement} }}";
                            return subscript == "*" && inDoubleQuote
                                ? $"$(@({mapped}) -join {PsBuild.IfsJoinSeparator})"
                                : $"$({mapped})";
                        }
                    }
                    // `${a[@]:-word}` / `${a[@]:+word}`: "the array has elements" is the test (an associative array: its values).
                    if (subOp.Length > 1 && (subOp.StartsWith(":-") || subOp.StartsWith(":+") || subOp[0] is '-' or '+'))
                    {
                        bool alt = subOp.Contains('+');
                        string word = subOp.StartsWith(':') ? subOp[2..] : subOp[1..];
                        // The scalar emitter renders `word` (with its own quoting / expansions) as the default / alternative arm.
                        string arm = EmitScalarSuffix("$null", ":-" + word, false, bvs.ArgWord) ?? "''";
                        // Read through Get-Variable: an unset array must be "empty", not an unbound-variable error under `set -u`.
                        string av = arrayVar.StartsWith("$") && arrayVar.AsSpan(1).IndexOfAnyExcept(VarNameChars) < 0
                            ? $"(Get-Variable -Name {arrayVar[1..]} -ValueOnly -ErrorAction SilentlyContinue)"
                            : arrayVar;
                        string src = $"@(if ({av} -is [System.Collections.IDictionary]) {{ {av}.Values }} else {{ {av} }})";
                        // The colon forms also treat a lone empty element as null (`a=(""); ${a[@]:-w}` is w); two empties are not.
                        string has = subOp.StartsWith(':')
                            ? $"({src}.Count -gt 1 -or ({src}.Count -eq 1 -and {src}[0]))"
                            : $"{src}.Count -gt 0";
                        return alt
                            ? $"$(if ({has}) {{ {arm} }} else {{ '' }})"
                            : $"$(if ({has}) {{ {src} }} else {{ {arm} }})";
                    }
                    return arrayVar;
                }

                // Single element: the indexed value is a scalar string — apply the same
                // scalar suffix operator the plain-variable path uses.
                string elemRef = int.TryParse(subscript, out _)
                    ? $"{arrayVar}[{subscript}]"
                    // ${m[$k]:-0}: a variable subscript is the variable's value, as in the operator-less form above.
                    : System.Text.RegularExpressions.Regex.Match(subscript, @"^\$\{?(\w+)\}?$") is { Success: true } subVar
                        ? $"{arrayVar}[{EmitVarSubscript(arrayVar, EmitSimpleVar(subVar.Groups[1].Value))}]"
                        : $"{arrayVar}[{EmitWordSubscript(arrayVar, subscript)}]";
                // A SCALAR (`s=pq`) has no PowerShell variable, and indexing $null throws ("Cannot index into a null
                // array"). bash treats it as a one-element array: ${s[0]-w} is pq, ${s[1]-w} is w. The assigning forms
                // keep the plain element reference AssignDefault parses.
                if (int.TryParse(subscript, out int scalarIdx) && arrayVar == "$" + bvs.Name && subOp.Length > 0
                    && subOp[0] != '=' && !subOp.StartsWith(":=", StringComparison.Ordinal))
                    elemRef = $"$(if ($null -eq {arrayVar}) {{ {(scalarIdx == 0 ? EmitSimpleVar(bvs.Name) : "$null")} }} else {{ {elemRef} }})";
                return EmitScalarSuffix(elemRef, subOp, inDoubleQuote, bvs.ArgWord)
                    ?? (inDoubleQuote ? $"$({elemRef})" : elemRef);
            }
        }

        // Array length: ${#arr[@]} or ${#arr[*]} -> $arr.Count
        if (bvs.Suffix.StartsWith("#L[", StringComparison.Ordinal) && bvs.Suffix.EndsWith(']'))
        {
            string subscript = bvs.Suffix[3..^1]; // strip #L[ and ]
            if (subscript is "@" or "*")
            {
                string lenArr = bvs.Name switch { "FUNCNAME" => "@(Get-BashFuncNames)", "BASH_VERSINFO" => "$global:BashVersionInfo", _ => $"${bvs.Name}" };
                // Always a subexpression: bare `$a.Count` glued to a literal (`len=${#a[@]}`) expands only `$a`.
                // A scalar (`s=abc`, an env var) is a one-element array: `${#s[@]}` is 1.
                if (lenArr.AsSpan(1).IndexOfAnyExcept(VarNameChars) < 0 && bvs.Name.Length > 0)
                    return $"$(if ({lenArr} -is [System.Collections.IDictionary]) {{ {lenArr}.Count }} elseif ($null -ne {lenArr}) {{ @({lenArr}).Count }} elseif ($null -ne {EmitSimpleVar(bvs.Name)}) {{ 1 }} else {{ 0 }})";
                return $"$({lenArr}.Count)";
            }
            // ${#a[N]}: the length of ONE element (a string), not of the array.
            // (`${s#[0-9]}` is a strip pattern that merely starts with `[`: the subscript must be arithmetic text.)
            if (System.Text.RegularExpressions.Regex.IsMatch(subscript, @"^[\w+\-*/% ]+$"))
            {
                string elemLen = EmitBracedVar(bvs with { Suffix = "[" + subscript + "]" });
                return $"$(([string]({elemLen})).Length)";
            }
        }

        // `${@@Q}` / `${*@Q}`: quote-for-reuse applies to EACH positional parameter.
        if (bvs.Name is "@" or "*" && bvs.Suffix == "@Q")
        {
            string posQ = $"@({PsBuild.BuildPositionalExpansion("@")}) | ForEach-Object {{ [PsBash.Cmdlets.BashQuoteText]::ForReuse([string]$_) }}";
            return bvs.Name == "*" && inDoubleQuote
                ? $"$(@({posQ}) -join {PsBuild.IfsJoinSeparator})"
                : $"$({posQ})";
        }
        string varRef = EmitSimpleVar(bvs.Name, strict: bvs.Suffix.StartsWith("/", StringComparison.Ordinal) || bvs.Suffix == "#L" || IsSliceSuffix(bvs.Suffix));

        // Name-prefix expansion: ${!prefix*} / ${!prefix@} -> the NAMES of
        // variables starting with `prefix`. ps-bash models bash variables as
        // env vars, so match over env:. (bash * joins on IFS, @ keeps words
        // separate; both yield the same name set here.)
        // Indirect expansion ${!a}: the value of the variable whose NAME is $a's value.
        if (bvs.Suffix is { } indSfx && indSfx.StartsWith("!indirect", StringComparison.Ordinal) && indSfx.Length > "!indirect".Length)
        {
            // ${!ref OP word}: the operator applies to the value of the variable NAMED by $ref.
            string op = indSfx["!indirect".Length..];
            string IndLookup(bool nameOnly) =>
                "Get-BashIndirectValue -Ref (" + IndirectRef(bvs.Name, false) + ") -RefName "
                + PsBuild.SingleQuote(System.Text.RegularExpressions.Regex.IsMatch(bvs.Name, @"^[A-Za-z_]\w*$") ? bvs.Name : "")
                + " -Pos @(" + PsBuild.BuildPositionalExpansion("@") + ")" + (nameOnly ? " -NameOnly" : "");
            // `${!ref@a}`: the attribute letters of the variable NAMED by $ref.
            if (op == "@a")
                return "$(& { $__in = [string]$(" + IndLookup(true) + "); if ($__in) { $__iv = Get-Variable -Name $__in -ValueOnly -ErrorAction Ignore; "
                    + "if ($null -eq $__iv) { $__iv = [System.Environment]::GetEnvironmentVariable($__in) }; "
                    + "[PsBash.Cmdlets.BashQuoteText]::Attributes($__in, $__iv) } })";
            if (op.StartsWith(":=", StringComparison.Ordinal) || op.StartsWith('='))
            {
                string assigned = EmitScalarSuffix("$__iv", op, false, bvs.ArgWord) ?? "$__iv";
                // The name first: an unset or empty ref reports its error once and assigns nothing.
                return "$(& { $__ivn = [string]$(" + IndLookup(true) + "); if ($__ivn -eq '') { return }; "
                    + "$__iv0 = $(" + IndLookup(false) + "); $__iv = $__iv0; $__ir = " + assigned + "; "
                    + "if ($__iv -cne $__iv0) { [System.Environment]::SetEnvironmentVariable($__ivn, [string]$__iv) }; $__ir })";
            }
            string indRef = "$(" + IndLookup(false) + ")";
            // Always the `$( … )` subexpression form: it stays one word beside a literal prefix (`x=${!ref-d}`).
            return EmitScalarSuffix(indRef, op, true, bvs.ArgWord) ?? indRef;
        }
        if (bvs.Suffix == "!indirect")
        {
            // The name's value is a variable name, or a positional index (`n=2; ${!n}`, `${!#}` = the last argument).
            // An unset name ref is bash's "invalid indirect expansion", which Get-BashIndirectValue reports, even under
            // set -u; only special parameters (`${!#}`) keep the strict read.
            bool namedRef = System.Text.RegularExpressions.Regex.IsMatch(bvs.Name, @"^[A-Za-z_]\w*$");
            string lookup = "& { $__indv = (" + IndirectRef(bvs.Name, !namedRef) + "); $__ind = [string]$__indv; "
                + "if ($__ind -match '^[0-9]+$') { $__pp = @(" + PsBuild.BuildPositionalExpansion("@") + "); "
                + "if ([int]$__ind -eq 0) { " + EmitSimpleVar("0", strict: true) + " } elseif ([int]$__ind -le $__pp.Count) { $__pp[[int]$__ind - 1] } } "
                + "else { Get-BashIndirectValue -Ref $__indv -RefName "
                + PsBuild.SingleQuote(System.Text.RegularExpressions.Regex.IsMatch(bvs.Name, @"^[A-Za-z_]\w*$") ? bvs.Name : "")
                + " -Pos @(" + PsBuild.BuildPositionalExpansion("@") + ") } }";
            return inDoubleQuote ? $"$({lookup})" : $"$({lookup})";
        }

        if (bvs.Suffix == "!names")
        {
            string expr = $"@(@(Get-ChildItem env: | Where-Object {{ $_.Name -clike '{bvs.Name}*' }} | ForEach-Object {{ $_.Name }}) + @(Get-Variable -Name '{bvs.Name}*' -ErrorAction SilentlyContinue | Where-Object {{ $_.Name -clike '{bvs.Name}*' -and $_.Options -eq 'None' -and ($_.Value -is [array] -or $_.Value -is [System.Collections.IDictionary]) -and $_.Name -cnotin 'args','input','Error','Matches','PSBoundParameters' }} | ForEach-Object {{ $_.Name }}) | Sort-Object -Unique -CaseSensitive)";
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
        if (bvs.Name is "@" or "*" && EmitPositionalWordOp(bvs.Name, bvs.Suffix, inDoubleQuote, bvs.ArgWord) is { } posOp)
            return posOp;
        // ${@#a} ${*/x/y} ${@%d} ${*^}: the operator applies to EACH parameter, then the words are joined (* quoted) or kept apart.
        if (bvs.Name is "@" or "*" && bvs.Suffix.Length > 0 && (bvs.Suffix[0] is '^' or ',' || bvs.Suffix.Length > 1 && bvs.Suffix[0] is '/' or '%' or '#'))
        {
            string elem = EmitScalarSuffix("$__psbpe", bvs.Suffix, false, bvs.ArgWord) ?? "$__psbpe";
            string list = "$(if ($null -ne $global:BashPositional -and $global:BashPositional.Count) { $global:BashPositional } else { $args })";
            string each = $"@(@({list}) | ForEach-Object {{ $__psbpe = [string]$_; {elem} }})";
            return !inDoubleQuote ? each : bvs.Name == "*" ? $"$((@({list}) | ForEach-Object {{ $__psbpe = [string]$_; {elem} }}) -join {PsBuild.IfsJoinSeparator})" : "$" + each[1..];
        }
        return EmitScalarSuffix(varRef, bvs.Suffix, inDoubleQuote, bvs.ArgWord) ?? varRef;
    }

    // `${@-w}` `${@:+w}` `${*:-w}` …: the operand is the positional LIST, not a string. `-` / `+` test only whether
    // any parameter is set; `:-` / `:+` also treat the expansion as null: no words, one empty word, or (`"$*"`) a
    // join that is empty. Two empty parameters are two words, so not null.
    private static string? EmitPositionalWordOp(string name, string suffix, bool inDoubleQuote, ImmutableArray<WordPart>? argWord)
    {
        bool colon = suffix.StartsWith(':');
        string op = colon ? suffix[1..] : suffix;
        if (op.Length == 0 || op[0] is not ('-' or '+')) return null;
        // `set -- ""` is a ONE-element list holding "", which a plain `if ($list)` takes for empty.
        string pos = "$(if ($null -ne $global:BashPositional -and $global:BashPositional.Count) { $global:BashPositional } else { $args })";
        bool joined = name == "*" && inDoubleQuote;
        string value = joined ? $"(@({pos}) -join {PsBuild.IfsJoinSeparator})"
            : inDoubleQuote ? pos
            : $"@(@({pos}) | Where-Object {{ $_ -ne '' }})"; // unquoted: empty words vanish
        string isNull = !colon
            ? $"@({pos}).Count -eq 0"
            : joined
                ? $"-not {value}"
                : $"@({pos}).Count -eq 0 -or (@({pos}).Count -eq 1 -and -not [string]@({pos})[0])";
        string arm = EmitScalarSuffix("$null", ":-" + op[1..], inDoubleQuote, argWord) ?? "''";
        return op[0] == '-'
            ? $"$(if ({isNull}) {{ {arm} }} else {{ {value} }})"
            : $"$(if ({isNull}) {{ '' }} else {{ {arm} }})";
    }

    // Emit a scalar parameter-expansion suffix operator applied to an arbitrary base
    // reference (a plain variable, or an array element like `$arr[0]`). Returns null if
    // `suffix` is not a scalar operator handled here — the caller falls back to the bare
    // reference. Keeping this independent of the variable name lets ${arr[0]##*/},
    // ${arr[i]:-x}, etc. reuse the exact same operator semantics as ${VAR##*/}.
    /// <summary>`${x:?msg}` / `${x?msg}`: throw the typed error a subshell scope catches (bash ends only the subshell).</summary>
    /// <summary><c>${x:1}</c> / <c>${x: -1:2}</c> (a substring), as opposed to <c>${x:-d}</c> <c>${x:=d}</c> <c>${x:+d}</c> <c>${x:?m}</c>.</summary>
    private static bool IsSliceSuffix(string suffix)
        => suffix.Length > 1 && suffix[0] == ':' && suffix[1] is not ('-' or '=' or '+' or '?');

    /// <summary>The assigning half of <c>${x=d}</c> / <c>${x:=d}</c>. An array element (<c>${arr[0]=d}</c>) goes through
    /// <c>Set-BashElement</c>, which grows the array; a plain PowerShell index assignment throws past the end.</summary>
    private static string AssignDefault(string varRef, string value)
    {
        var m = System.Text.RegularExpressions.Regex.Match(varRef, @"^\$([A-Za-z_]\w*)\[(.+)\]$");
        if (!m.Success) return $"({varRef} = {value})";
        string arr = "$" + m.Groups[1].Value;
        return $"$($__psbV = {value}; {arr} = Set-BashElement {arr} {m.Groups[2].Value} $__psbV; $__psbV)";
    }

    private static string ParamErrorThrow(string varRef, string argVal)
    {
        var name = varRef.StartsWith("$env:", StringComparison.Ordinal) ? varRef[5..].Trim('{', '}', '(', ')') : "";
        var prefix = name.Length > 0 ? PsBuild.SingleQuote(name + ": ") : "''";
        return "$(" + PsBuild.ParamErrorThrowStatements(prefix, argVal) + ")";
    }

    private static string? EmitScalarSuffix(string varRef, string suffix, bool inDoubleQuote,
        ImmutableArray<WordPart>? argWord = null)
    {
        string open = inDoubleQuote ? "$(" : "(";

        // Quote safety is NOT the same question as `inDoubleQuote`.
        //
        // `open` asks "am I being emitted directly into a PowerShell double-quoted
        // string right now?" — that decides `$(` vs `(`.
        //
        // The QUOTE CHARACTER asks a wider question: "will this text end up inside
        // SOME enclosing double-quoted string?" A command substitution resets
        // inDoubleQuote to false for its body (a `$( … )` is a fresh expression
        // scope) but does NOT leave the enclosing string — `_dqNestDepth` tracks
        // that. Keying the quote on inDoubleQuote alone emitted
        //   "$(git ($env:x ? "--dir=$env:x" : "") rev-parse)"
        // whose inner `"` closed the outer string ("The string is missing the
        // terminator"), breaking git-completion.bash. Single quotes are inert at
        // any nesting depth — the same rule NeedsSingleQuotedLiteral already uses.
        bool nested = inDoubleQuote || _dqNestDepth > 0;
        char q = nested ? '\'' : '"';

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
        // A $( ) in W: one join expression in both contexts, never command text in "…".
        string ArgVal(string rawSlice) => inDoubleQuote ? ParenSafeLiteral(ArgValRaw(rawSlice)) : ArgValRaw(rawSlice);

        // PowerShell finds the end of "$( … )" by counting parens WITHOUT honouring the quotes inside, so a
        // literal operand holding an unbalanced ( or ) (`"${u-\(}"`) must not carry a raw paren there.
        static string ParenSafeLiteral(string expr)
        {
            if (expr.Length < 2 || expr[0] != '\'' || expr[^1] != '\'' || (expr.IndexOf('(') < 0 && expr.IndexOf(')') < 0))
                return expr;
            for (int i = 1; i < expr.Length - 1; i++)
                if (expr[i] == '\'' && expr[i + 1] != '\'' ) return expr;
                else if (expr[i] == '\'') i++;
            return "(" + expr.Replace("(", "' + [char]40 + '").Replace(")", "' + [char]41 + '") + ")";
        }

        string ArgValRaw(string rawSlice) =>
            !argWord.HasValue
                ? (nested ? PsBuild.SingleQuote(rawSlice) : $"\"{rawSlice}\"")
            : TryEmitJoinedValue(argWord.Value, out var joinedArg) ? joinedArg
            : nested ? EmitBracedArgWordValue(argWord.Value)
            : FlattenPartsToDoubleQuotedString(argWord.Value);

        // Length: ${#VAR}
        if (suffix == "#L")
        {
            // `${##}`: $# is a number, so its length is that of its text.
            if (varRef.Contains("BashPositional") && varRef.Contains(".Count")) varRef = $"([string]{varRef})";
            return inDoubleQuote ? $"$({varRef}.Length)" : $"{varRef}.Length";
        }

        // The `:`-prefixed forms act when the variable is unset OR EMPTY.
        //
        // These used to emit `??`, which only tests for $null — so a variable SET
        // TO EMPTY skipped the operator entirely and `x=; echo ${x:-d}` printed
        // nothing instead of `d`. That is the single most common parameter
        // expansion in shell scripts, and the failure was silent (found by the
        // oracle differential sweep, invisible to every parse check).
        //
        // The PowerShell ternary is exactly bash's test: `""` and `$null` are both
        // falsy, while `"0"` — which bash also treats as set-and-non-empty — is
        // TRUTHY in PowerShell (unlike in C). Verified directly against pwsh.
        //
        // Default value: ${VAR:-default}
        if (suffix.StartsWith(":-"))
            return $"{open}{varRef} ? {varRef} : {ArgVal(suffix[2..])})";
        // Assign default: ${VAR:=default}
        if (suffix.StartsWith(":="))
            return $"{open}{varRef} ? {varRef} : {AssignDefault(varRef, ArgVal(suffix[2..]))})";
        // Use alternative: ${VAR:+alt}
        if (suffix.StartsWith(":+"))
            return $"{open}{varRef} ? {ArgVal(suffix[2..])} : {q}{q})";
        // Error if unset: ${VAR:?message}
        if (suffix.StartsWith(":?"))
            return $"{open}{varRef} ? {varRef} : {ParamErrorThrow(varRef, ArgVal(suffix[2..]))})";

        // Colon-less unset-only variants: ${VAR-w} ${VAR=w} ${VAR+w} ${VAR?msg}.
        // Bash distinguishes these (act only when UNSET) from the `:`-prefixed forms
        // (act when unset OR empty). `$env:X` is $null when unset but "" when set to
        // empty, so `??` is an EXACT match for the unset-only test — which is also
        // precisely why it was WRONG for the `:` forms above.
        if (suffix.StartsWith("-"))
            return $"{open}{varRef} ?? {ArgVal(suffix[1..])})";
        if (suffix.StartsWith("="))
            return $"{open}{varRef} ?? {AssignDefault(varRef, ArgVal(suffix[1..]))})";
        if (suffix.StartsWith("+"))
            // Set-but-EMPTY counts as set here (an array element can hold ""), so test for $null, not truthiness.
            return $"{open}$null -ne {varRef} ? {ArgVal(suffix[1..])} : {q}{q})";
        if (suffix.StartsWith("?"))
            return $"{open}{varRef} ?? {ParamErrorThrow(varRef, ArgVal(suffix[1..]))})";

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
        // A quoted / expanded pattern (`${s#"${s%%[![:space:]]*}"}`, `${f%"$ext"}`) is the quote-removed VALUE, matched
        // literally (its glob characters are quoted); an empty one removes nothing.
        // A leading unquoted `~` / `~/` of a removal pattern is the home directory, matched literally (``).
        if (suffix.Length > 1 && suffix[0] is '%' or '#')
        {
            int skip = suffix[1] == suffix[0] ? 2 : 1;
            var removalPat = suffix[skip..];
            if (removalPat.IndexOfAny(['*', '?', '[', '\\', '\'', '"']) < 0 && TildeFind(removalPat) is var homePat && !ReferenceEquals(homePat, removalPat))
                suffix = suffix[..skip] + homePat;
        }
        if ((suffix.StartsWith("%%") || suffix.StartsWith("##") ? suffix[2..] : suffix.StartsWith("%") || suffix.StartsWith("#") ? suffix[1..] : null)
                is { } stripPat && suffix[0] is '%' or '#' && TryEmitSubstitutionOperand(stripPat) is { } stripExpr)
        {
            string esc = $"[regex]::Escape({stripExpr})";
            string guard = $"$(if ({stripExpr}) {{ {esc} }} else {{ '(?!)' }})";
            return suffix[0] == '#'
                ? $"{open}{varRef} -creplace ('^' + {guard}), '')"
                : suffix.StartsWith("%%")
                    ? $"{open}{varRef} -creplace ({guard} + '$'), '')"
                    : $"{open}{varRef} -creplace ('^(.*)' + {guard} + '$'), '$1')";
        }
        if (suffix.StartsWith("%%"))
            return $"{open}{varRef} -creplace '{SqEsc(GlobToRegex(UnquoteForGlob(suffix[2..]), lazy: false))}$','')";
        if (suffix.StartsWith("%"))
            return $"{open}{varRef} -creplace '^(.*){SqEsc(GlobToRegex(UnquoteForGlob(suffix[1..]), lazy: false))}$','$1')";

        // Remove prefix: ${VAR##pattern} (greedy) / ${VAR#pattern} (lazy).
        if (suffix.StartsWith("##"))
            return $"{open}{varRef} -creplace '^{SqEsc(GlobToRegex(UnquoteForGlob(suffix[2..]), lazy: false))}','')";
        if (suffix.StartsWith("#"))
            return $"{open}{varRef} -creplace '^{SqEsc(GlobToRegex(UnquoteForGlob(suffix[1..]), lazy: true))}','')";

        // Replace first: ${VAR/find/replace} — instance overload .Replace(str, rep, 1).
        if (suffix.StartsWith("/") && !suffix.StartsWith("//"))
        {
            var (find, replace) = SplitPatsubOperands(suffix[1..]);
            return SubstituteExpr(open, varRef, TildeFind(find), replace, first: true);
        }
        // Replace all: ${VAR//find/replace} — escape find literally; 2-arg overload = all.
        if (suffix.StartsWith("//"))
        {
            var (find, replace) = SplitPatsubOperands(suffix[2..]);
            return SubstituteExpr(open, varRef, TildeFind(find), replace, first: false);
        }

        // A leading unquoted `~` / `~/` of the pattern is the home directory (matched literally).
        static string TildeFind(string find)
            => find == "~" || find.StartsWith("~/", StringComparison.Ordinal) ? "\"$HOME\"" + find[1..] : find;

        // A quoted / variable operand (`$'\n'`, `$nl`, `'x'`) is the quote-removed VALUE, matched literally; `$` in the
        // replacement is doubled because .NET reads `$1` / `$&` there.
        static string SubstituteExpr(string open, string varRef, string find, string replace, bool first)
        {
            string tail = first ? ", 1" : "";
            // A quoted glob (`[^'><+-.,[]']`): quotes come off, the quoted characters stay literal.
            if (UnquoteGlobPattern(find) is { } unquotedFind) find = unquotedFind;
            string? findExpr = TryEmitSubstitutionOperand(find, backslashEscapes: true);
            string? repExpr = TryEmitSubstitutionOperand(replace, backslashEscapes: true);
            if (findExpr is null && repExpr is null)
                return $"{open}([regex]{ReplaceFindRegex(find)}).Replace({varRef}, '{SqEsc(replace)}'{tail}))";
            // An unquoted `$g` is a PATTERN (`g='*'; ${v//$g/-}` replaces the whole value); "$g" is the literal text.
            string regex = findExpr is null ? ReplaceFindRegex(find)
                : System.Text.RegularExpressions.Regex.IsMatch(find, @"^\$(\w+|\{\w+\})$")
                    ? $"$(if ({findExpr}) {{ '(?:(?=[\\s\\S])|\\A)(?:' + [PsBash.Core.Parser.BashPattern]::ToRegex([string]({findExpr}), $false, $false) + ')' }} else {{ '(?!)' }})"
                    : $"$(if ({findExpr}) {{ [regex]::Escape({findExpr}) }} else {{ '(?!)' }})";
            string rep = repExpr is null ? $"'{SqEsc(replace)}'" : $"({repExpr}).Replace('$', '$$')";
            return $"{open}([regex]{regex}).Replace({varRef}, {rep}{tail}))";
        }
        // The `find` of ${VAR/find/rep}: an extglob pattern is matched as one (`${v//@(a|z)/Y}`);
        // anything else stays the historical LITERAL match (plain-glob find is not modelled).
        // A leading `#` / `%` anchors the match to the start / end of the value (`${s/#xx/yy}`, `${s/%xx/yy}`); a
        // plain-glob `*` `?` `[` is matched as a glob (`${v/c*/XX}`), longest-first like bash.
        static string ReplaceFindRegex(string find)
        {
            string pre = "", post = "";
            if (find.Length > 0 && find[0] == '#') { pre = "^"; find = find[1..]; }
            else if (find.Length > 0 && find[0] == '%') { post = "$"; find = find[1..]; }
            bool isGlob = BashPattern.HasExtGlob(find) || find.IndexOfAny(['*', '?', '[']) >= 0;
            if (!isGlob && pre.Length == 0 && post.Length == 0)
                return $"[regex]::Escape('{SqEsc(find)}')";
            string body = isGlob ? SqEsc(BashPattern.ToRegex(find)) : SqEsc(System.Text.RegularExpressions.Regex.Escape(find));
            // `${v//*/-}`: no empty match at the end of the value (bash replaces once, .NET would twice).
            string noEmptyTail = isGlob && pre.Length == 0 && post.Length == 0 && find.Trim('*').Length == 0 ? "(?:(?=[\\s\\S])|\\A)" : "";
            return $"'(?s){noEmptyTail}{pre}{body}{post}'";
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
                    string sv = "([string]" + varRef + ")";   // an unset variable slices as the empty string
                    string len = $"{sv}.Length";
                    string start = offset >= 0
                        ? $"[Math]::Min({offset}, {len})"
                        : $"[Math]::Max(0, {len} - {-offset})";
                    string? expr = null;
                    if (sliceParts.Length == 1)
                        expr = $"{sv}.Substring({start})";
                    else if (int.TryParse(sliceParts[1].Trim(), out int length) && length >= 0)
                        expr = $"{sv}.Substring({start}, [Math]::Min({length}, {len} - ({start})))";
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
        // An optional glob PATTERN (`${x,A}`, `${x^^[a-c]}`) limits the conversion to characters it matches;
        // an unset variable converts to the empty string.
        if (suffix.Length > 0 && suffix[0] is '^' or ',')
        {
            bool all = suffix.Length > 1 && suffix[1] == suffix[0];
            string pat = suffix[(all ? 2 : 1)..];
            string re = "(?s)" + (pat.Length == 0 ? "." : "(?:" + GlobToRegex(UnquoteForGlob(pat), lazy: false) + ")");
            string conv = suffix[0] == '^' ? "ToUpper" : "ToLower";
            return $"{open}([regex]'{SqEsc(re)}').Replace([string]({varRef}), {{ $args[0].Value.{conv}() }}{(all ? "" : ", 1")}))";
        }

        // Transform operators ${VAR@op} (bash 4.4+/5.1). @Q (quote-for-reuse) and the case
        // transforms map cleanly; @E/@P/@A/@a have no PowerShell equivalent and degrade to
        // the bare value (documented gap, emitter-strategy.md §6) — never silently dropped.
        if (suffix.StartsWith("@") && suffix.Length >= 2)
        {
            switch (suffix[1])
            {
                case 'Q':
                    return $"$(if ($null -eq {varRef}) {{ '' }} else {{ [PsBash.Cmdlets.BashQuoteText]::ForReuse({varRef}) }})";
                case 'P':
                    return $"{open}[PsBash.Cmdlets.BashPromptText]::Expand({varRef}))";
                case 'E':
                    return $"{open}[PsBash.Cmdlets.BashPromptText]::Unescape({varRef}))";
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

    /// <summary>
    /// Emit <c>${@:offset[:length]}</c> / <c>${*:…}</c> — a slice of the positional
    /// parameters — by reusing the runtime-clamped <see cref="EmitArraySlice"/> over
    /// the positional array. Returns null when the suffix is not a numeric slice, so
    /// the caller degrades instead of emitting something broken.
    /// <para>
    /// Oracle-verified indexing: bash slices the list <c>$0 $1 $2 ...</c>, so <c>${@:1}</c> is the whole
    /// positional list and <c>${@:0}</c> starts with <c>$0</c>. <see cref="EmitArraySlice"/> puts <c>$0</c>
    /// in front when <c>positional</c> is set; a negative offset counts from the end (<c>${@: -1}</c> = last).
    /// </para>
    /// </summary>
    private static string? EmitPositionalSlice(string suffix, bool inDoubleQuote)
        => EmitArraySlice(PsBuild.BuildPositionalExpansion("@"), suffix, inDoubleQuote, positional: true);

    // `${a[$i]}`: an associative array uses the value as the key; an indexed array evaluates it as an arithmetic
    // expression (`code='1+2'; ${a[$code]}` is a[3]). A plain integer skips the evaluator.
    private static string EmitVarSubscript(string arrayVar, string valueRef)
        => $"$(if ({arrayVar} -is [System.Collections.IDictionary]) {{ {valueRef} }} elseif ([string]{valueRef} -match '^-?[0-9]+$') {{ [long]{valueRef} }} else {{ [long](Invoke-BashArith ([string]{valueRef})) }})";

    // `${a[i]}` / `${a[i+1]}`: on an indexed array the subscript is an arithmetic expression (bare names are
    // variables); on an associative array it is the literal key. The array kind is known only at run time.
    private static string EmitWordSubscript(string arrayVar, string subscript)
    {
        // `${m["k"]}` / `${m['k']}`: a quoted literal subscript is its quote-removed text.
        if (subscript.Length >= 2 && subscript[0] is '"' or '\'' && subscript[^1] == subscript[0]
            && subscript.AsSpan(1, subscript.Length - 2).IndexOfAny("$`\\\"'") < 0)
            subscript = subscript[1..^1];
        // `${a[$(cmd) + 1]}`: a command substitution in the subscript is expanded first, then the result evaluated.
        if (subscript.Contains("$(") || subscript.Contains('`'))
        {
            string dyn = FlattenPartsToDoubleQuotedString(BashParser.DecomposeWord(subscript));
            return $"$(if ({arrayVar} -is [System.Collections.IDictionary]) {{ {dyn} }} else {{ [long](Invoke-BashArith {dyn}) }})";
        }
        string key = PsBuild.SingleQuote(subscript);
        if (!System.Text.RegularExpressions.Regex.IsMatch(subscript, @"^[\w+\-*/% ()]+$"))
            return key;
        return $"$(if ({arrayVar} -is [System.Collections.IDictionary]) {{ {key} }} else {{ [long](Invoke-BashArith {key}) }})";
    }

    // Emit a bash array slice ${arr[@]:offset[:length]} as a PowerShell range index.
    // Returns null when `op` is not a `:`-prefixed numeric slice (caller degrades to the
    // whole array). bash element indices map directly: offset..(offset+length-1), and
    // negative offsets index from the end (PowerShell supports negative range bounds).
    private static string? EmitArraySlice(string arrayVar, string op, bool inDoubleQuote, bool positional = false)
    {
        if (!op.StartsWith(":"))
            return null;
        var body = op[1..].Trim();
        var parts = body.Split(':', 2);
        // A bound is an integer or a variable (`${a[@]:$i}`, `${@:$#}`, `${a[@]:$i:$n}`): its value is read at run time.
        static string? Bound(string text)
        {
            text = text.Trim();
            if (int.TryParse(text, out int n)) return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var m = System.Text.RegularExpressions.Regex.Match(text, @"^\$(?:\{(\w+|#)\}|(\w+|#))$");
            return m.Success ? "[int](" + ZeroDefault(EmitSimpleVar(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, strict: true)) + ")" : null;
        }
        if (Bound(parts[0]) is not { } offset)
            return null;

        string? length = null;
        if (parts.Length == 2)
        {
            if (Bound(parts[1]) is not { } len)
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
        // A sparse or associative table is a dictionary: slice its VALUES, not the one-element wrapper `@($table)` makes.
        // A SPARSE array's offset is a subscript (the elements at index >= offset), not a position among the values.
        var sb = new StringBuilder("& { $__psbS = ");
        sb.Append(arrayVar);
        sb.Append("; if ($__psbS -is [PsBash.Cmdlets.BashSparseArray]) { $__psbS.Slice(").Append(offset).Append(", ")
          .Append(length is null ? "-1" : "[Math]::Max(0, " + length + ")").Append(") } else { ");
        sb.Append("$__psbA = @(if ($__psbS -is [System.Collections.IDictionary]) { $__psbS.Values } else { $__psbS }); $__psbO = ").Append(offset).Append("; ");
        // ${@:0} starts at $0 and ${@:1} at $1: slice the list with $0 in front.
        if (positional) sb.Append("$__psbA = @(").Append(PsBuild.TryMapSpecialVar("0", braced: false, inDoubleQuote: false)).Append(") + $__psbA; ");
        // A negative offset reaching before the first element selects nothing (`${a[@]: -5}` of 4 elements is empty).
        sb.Append("if ($__psbO -lt 0) { $__psbO = $__psbA.Count + $__psbO; if ($__psbO -lt 0) { $__psbO = $__psbA.Count } }; ");
        sb.Append("$__psbO = [Math]::Max(0, [Math]::Min($__psbO, $__psbA.Count)); ");
        if (length is not null)
            sb.Append("$__psbN = ").Append(length).Append("; ");
        else
            sb.Append("$__psbN = $__psbA.Count - $__psbO; ");
        sb.Append("if ($__psbN -lt 0) { $__psbN = 0 }; ");
        sb.Append("$__psbN = [Math]::Min($__psbN, $__psbA.Count - $__psbO); ");
        sb.Append("if ($__psbN -le 0) { @() } else { $__psbA[$__psbO..($__psbO + $__psbN - 1)] } } }");
        // Always wrap the scriptblock invocation in a subexpression: a bare `& { … }`
        // is NOT a valid command argument (`cmd & {…}` parses `&` as the PS background
        // operator). `$(& { … })` is a valid argument in every context and splats the
        // array like the old bare `$a[1..2]` did.
        return $"$({sb})";
    }

    /// <param name="following">The word part after the closing quote (flattening <c>"$y"x</c> into one string): it
    /// follows the LAST inner part, so a variable there is braced when the text would fold into its name.</param>
    private static void AppendDoubleQuotedInner(StringBuilder sb, ImmutableArray<WordPart> parts, WordPart? following = null)
    {
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            WordPart? next = i + 1 < parts.Length ? parts[i + 1] : following;
            // A SUFFIX-LESS ${name} is just a variable read — EmitBracedVar returns the
            // same bare `$name` a SimpleVarSub would, so it needs the same
            // drive-reference guard. Without this `grep "^${field}:"` emitted
            // `"^$field:"`, and PowerShell reads `$field:` as a provider-qualified
            // path ("':' was not followed by a valid variable name character"), so the
            // whole file failed to parse. The guard was on `$field:` but not `${field}:`.
            if (part is WordPart.BracedVarSub { Suffix: null } plainBvs
                && next is not null && NextPartNeedsBracing(next))
            {
                sb.Append(EmitSimpleVarBraced(plainBvs.Name, strict: true));
            }
            else if (part is WordPart.BracedVarSub bvs)
                sb.Append(EmitBracedVar(bvs, inDoubleQuote: true));
            else if (part is WordPart.SimpleVarSub vs)
            {
                // Check if next part starts with ':' — PowerShell would misparse as a drive reference.
                // Use ${name} bracing to prevent this.
                bool needsBracing = next is not null && NextPartNeedsBracing(next);
                if (needsBracing)
                    sb.Append(EmitSimpleVarBraced(vs.Name, strict: true));
                else
                    sb.Append(EmitSimpleVar(vs.Name, inDoubleQuote: true, strict: true));
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
                sb.Append(EscapeForDoubleQuoteNested(lit.Value));
            }
            else
                sb.Append(EmitWordPart(part));
        }
    }

    private static string EmitDoubleQuoted(WordPart.DoubleQuoted dq)
    {
        // A word with NO expansion is a plain literal, so a PowerShell SINGLE-quoted
        // string carries it exactly — and, unlike the double-quoted form, survives
        // being nested inside another double-quoted string.
        //
        // `X="$(grep "a\"b" f)"` emitted ... "a`"b" ... inside the outer "$( … )"
        // string. PowerShell's OUTER string scanner consumes the backtick escape, so
        // the inner string ended early and the file failed to parse ("The string is
        // missing the terminator"). The same happens to an escaped `$` or backtick.
        // Only take this path when an escape would actually be emitted, so the common
        // shape keeps its existing (more readable) double-quoted form.
        if (TryGetPureLiteralText(dq.Parts, out var literal)
            && NeedsSingleQuotedLiteral(literal))
        {
            return PsBuild.SingleQuote(literal);
        }

        if (TryEmitJoinedWord(ImmutableArray.Create<WordPart>(dq), out var joined))
            return joined;

        var sb = new StringBuilder();
        sb.Append('"');
        AppendDoubleQuotedInner(sb, dq.Parts);
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Should a known-literal word be emitted as a SINGLE-quoted PowerShell string
    /// rather than a double-quoted one? Yes when the double-quoted form would need a
    /// backtick escape (the enclosing string would eat it), and yes for ANY literal
    /// once nested inside another double-quoted string — notably the EMPTY one:
    /// <c>X="$(echo "")"</c> is a parse error ("The string is missing the terminator")
    /// because the inner <c>""</c> closes the outer string, while <c>''</c> is inert.
    /// Un-nested, only a literal that would actually be escaped switches, so the
    /// ordinary double-quoted shape stays readable.
    /// </summary>
    private static bool NeedsSingleQuotedLiteral(string literal)
        => _dqNestDepth > 0 || literal.AsSpan().IndexOfAny('"', '$', '`') >= 0;

    /// <summary>
    /// Concatenate the parts when EVERY one is literal text (no parameter, command, or
    /// arithmetic expansion), so the word's value is known at transpile time.
    /// </summary>
    private static bool TryGetPureLiteralText(
        ImmutableArray<WordPart> parts, out string text)
    {
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            switch (part)
            {
                case WordPart.Literal lit: sb.Append(lit.Value); break;
                case WordPart.EscapedLiteral el: sb.Append(el.Value); break;
                case WordPart.SingleQuoted sq: sb.Append(sq.Value); break;
                case WordPart.AnsiCQuoted aq: sb.Append(ExpandAnsiCEscapes(aq.Value)); break;
                // A double-quoted part is literal only if ITS parts are.
                case WordPart.DoubleQuoted dq when TryGetPureLiteralText(dq.Parts, out var inner):
                    sb.Append(inner);
                    break;
                default:
                    text = "";
                    return false;
            }
        }
        text = sb.ToString();
        return true;
    }

    private static bool NextPartNeedsBracing(WordPart part)
    {
        // Text that would FOLD INTO the preceding PowerShell variable token inside a "…" string:
        // ':' → drive/scope reference ($env:PATH: misparse), '.' → property access, and every char
        // PowerShell reads as part of a variable NAME — letters, digits, '_', '?'. bash ends the name
        // at the closing brace, so `"${f}x"` / `"${f}_bak"` / `x="${f}1"` emitted `$env:fx` (another,
        // unset variable) and printed "" silently. A `$f` (no braces) is never followed by a name
        // char — the parser takes it into the name — but `$f?` is.
        static bool Folds(char c) => c is ':' or '.' or '_' or '?' || char.IsAsciiLetterOrDigit(c);
        // Quoted text glued after the variable folds the same way once flattened into one string:
        // `$y'x'`, `$y"x"`, `"$y""x"`.
        return part switch
        {
            WordPart.Literal lit => lit.Value.Length > 0 && Folds(lit.Value[0]),
            WordPart.EscapedLiteral el => el.Value.Length > 0 && Folds(el.Value[0]),
            WordPart.SingleQuoted sq => sq.Value.Length > 0 && Folds(sq.Value[0]),
            WordPart.AnsiCQuoted aq => ExpandAnsiCEscapes(aq.Value) is { Length: > 0 } s && Folds(s[0]),
            WordPart.DoubleQuoted dq => dq.Parts.Length > 0 && NextPartNeedsBracing(dq.Parts[0]),
            _ => false,
        };
    }

    private static string EmitSimpleVarBraced(string name, bool strict = false)
    {
        // Emit ${name} or ${env:name} with braces to prevent PS drive-reference misparse.
        name = ResolveNameref(name);
        if (_loopVars is not null && _loopVars.Contains(name))
            return $"${{{name}}}";

        var special = PsBuild.TryMapSpecialVar(name, braced: true, inDoubleQuote: true);
        if (special is not null) return strict && _nounsetMode ? StrictPositional(name, special) : special;
        var plain = $"${{env:{EnvShadowName(name) ?? name}}}";
        return strict && _nounsetMode && EnvShadowName(name) is null ? StrictRead(name, plain) : plain;
    }

    /// <summary>
    /// <c>$_</c> is the last argument of the previous command. Inside one <c>;</c> / <c>&amp;&amp;</c> / <c>||</c> list the
    /// previous simple command is known when emitting (<c>mkdir -p a/b &amp;&amp; cd $_</c>), so a whole-word <c>$_</c> /
    /// <c>"$_"</c> is replaced by that command's last word when it is a plain literal (no expansion to run twice).
    /// Anything else keeps reading <c>$global:BashLastArg</c>.
    /// </summary>
    private static ImmutableArray<Command> ResolveLastArgRefs(ImmutableArray<Command> cmds)
    {
        CompoundWord? prevLast = null;
        ImmutableArray<Command>.Builder? rewritten = null;
        for (int i = 0; i < cmds.Length; i++)
        {
            var cmd = cmds[i];
            if (cmd is Command.Simple y && y.Words.Length > 0)
            {
                if (prevLast is not null && y.Words.Skip(1).Any(HasLastArgRef))
                {
                    var words = y.Words.Select((w, k) => k > 0 && HasLastArgRef(w) ? ReplaceLastArgRef(w, prevLast) : w).ToImmutableArray();
                    cmd = y with { Words = words };
                    rewritten ??= cmds.ToBuilder();
                    rewritten[i] = cmd;
                }
                var last = ((Command.Simple)cmd).Words[^1];
                prevLast = PlainLastArgValue(last) is not null ? last : null;
            }
            else prevLast = null;
        }
        return rewritten?.ToImmutable() ?? cmds;
    }

    private static bool IsLastArgPart(WordPart p)
        => p is WordPart.SimpleVarSub { Name: "_" } or WordPart.BracedVarSub { Name: "_", Suffix: null or "" };

    private static bool HasLastArgRef(CompoundWord w)
        => w.Parts.Any(p => IsLastArgPart(p) || p is WordPart.DoubleQuoted dq && dq.Parts.Any(IsLastArgPart));

    /// <summary>The text of a word made only of literal/quoted-literal parts (nothing to expand), else null.</summary>
    private static string? PlainLastArgValue(CompoundWord w)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var p in w.Parts)
        {
            switch (p)
            {
                case WordPart.Literal l: sb.Append(l.Value); break;
                case WordPart.EscapedLiteral e: sb.Append(e.Value); break;
                case WordPart.SingleQuoted q: sb.Append(q.Value); break;
                case WordPart.DoubleQuoted d when d.Parts.All(x => x is WordPart.Literal or WordPart.EscapedLiteral):
                    foreach (var x in d.Parts) sb.Append(x is WordPart.Literal l2 ? l2.Value : ((WordPart.EscapedLiteral)x).Value);
                    break;
                default: return null;
            }
        }
        return w.Parts.Length > 0 ? sb.ToString() : null;
    }

    private static CompoundWord ReplaceLastArgRef(CompoundWord w, CompoundWord prev)
    {
        string value = PlainLastArgValue(prev)!;
        if (value.IndexOfAny(['$', '`', '"', '\\']) >= 0) return w;
        // Unquoted, `$_` is word-split and globbed: only a value with nothing to split or glob is safe to inline as a literal.
        bool safeUnquoted = value.Length > 0 && value.IndexOfAny([' ', '\t', '\n', '*', '?', '[', '\\', '"', '\'', '$', '`', '~', '{', '}', '(', ')', '<', '>', '|', '&', ';', '#', '!']) < 0;
        var parts = ImmutableArray.CreateBuilder<WordPart>();
        foreach (var p in w.Parts)
        {
            if (IsLastArgPart(p)) { if (safeUnquoted) parts.Add(new WordPart.Literal(value)); else return w; }
            else if (p is WordPart.DoubleQuoted dq && dq.Parts.Any(IsLastArgPart))
                parts.Add(new WordPart.DoubleQuoted(dq.Parts.Select(x => IsLastArgPart(x) ? new WordPart.Literal(value) : x).ToImmutableArray()));
            else parts.Add(p);
        }
        return new CompoundWord(parts.ToImmutable());
    }

    private static int SourceLine(Command c) => c switch { Command.Simple s => s.Line, Command.ShAssignment a => a.Line, _ => 0 };


    private static string EmitCommandList(Command.CommandList list)
    {
        list = list with { Commands = ResolveLastArgRefs(list.Commands) };
        var sb = new StringBuilder();
        for (int i = 0; i < list.Commands.Length; i++)
        {
            if (i > 0)
                sb.Append("; ");
            _readonlyViolation = false;
            sb.Append(EmitStatement(list.Commands[i]));
            if (_readonlyViolation)
            {
                if (_scriptPosix && _functionDepth == 0) break;
                int failedLine = SourceLine(list.Commands[i]);
                while (failedLine > 0 && i + 1 < list.Commands.Length && SourceLine(list.Commands[i + 1]) == failedLine) i++;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Rewrites <c>CMD &amp;&amp; break</c> / <c>CMD || exit 1</c> and friends into an
    /// <c>if</c> statement, because PowerShell's <c>&amp;&amp;</c>/<c>||</c> chain
    /// operators require a COMMAND on the right — a statement keyword there is
    /// parsed as a command NAME.
    /// <para>
    /// Both failure modes are silent-ish and both are everyday bash: <c>&amp;&amp;
    /// break</c> printed "The term 'break' is not recognized" and the loop ran on,
    /// while <c>|| exit 1</c> simply DID NOT EXIT — the script kept going past the
    /// guard it was written to enforce. Found by the oracle differential sweep.
    /// </para>
    /// Returns null when the chain does not end in a statement keyword.
    /// </summary>
    private static string? TryEmitAndOrEndingInStatementKeyword(Command.AndOrList andOr)
    {
        var last = andOr.Commands[^1];
        if (last is not Command.Simple lastSimple || lastSimple.Words.Length == 0)
            return null;
        if (GetLiteralValue(lastSimple.Words[0]) is not { } name
            || !PsStatementKeywordCommands.Contains(name))
        {
            return null;
        }

        // Everything before the final operator is the CONDITION. Re-emit it through
        // the ordinary path so nested chains, pipelines and tests keep their
        // existing exit-code bridging.
        var prefixCommands = andOr.Commands.RemoveAt(andOr.Commands.Length - 1);
        string condition = prefixCommands.Length == 1
            ? Emit(prefixCommands[0])
            : EmitAndOrList(andOr with
            {
                Commands = prefixCommands,
                Ops = andOr.Ops.RemoveAt(andOr.Ops.Length - 1),
            });

        // `&&` runs the keyword when the condition SUCCEEDED, `||` when it failed. The condition
        // runs as a statement so its output streams (`grep pat f || exit 1` prints the matches);
        // ExitCodeTest's expression position would [void] it.
        bool negate = andOr.Ops[^1] == "||";
        return $"{PsBuild.HoistedCondition(condition)}if ({PsBuild.LastStatusTest(negate)}) {{ {Emit(last)} }}";
    }

    /// <param name="andOr">The <c>&amp;&amp;</c>/<c>||</c> chain to emit.</param>
    /// <param name="errexitFinalOperand">
    /// At statement position under errexit (<see cref="EmitAndOrStatement"/>): the final operand is
    /// emitted as errexit-active and records its status (<see cref="PsBuild.ErrexitFinalOperand"/>).
    /// </param>
    private static string EmitAndOrList(Command.AndOrList andOr, bool errexitFinalOperand = false)
    {
        andOr = andOr with { Commands = ResolveLastArgRefs(andOr.Commands) };
        if (TryEmitAndOrEndingInStatementKeyword(andOr) is { } keywordForm)
            return keywordForm;

        var sb = new StringBuilder();
        for (int i = 0; i < andOr.Commands.Length; i++)
        {
            bool isFinal = i == andOr.Commands.Length - 1;
            // bash exempts every operand but the last from errexit.
            if (!isFinal) _errexitExemptDepth++;
            int operandStart = sb.Length;
            _andOrChainDepth++;
            try
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
                // A bad integer operand / malformed regex is status 2, not just false.
                if (boolText.Contains("Invoke-BashTestInt") || boolText.Contains("__BashTestBad"))
                    sb.Append("$($global:__BashTestBad = $false; $__psbTestR = " + boolText + "; if ($__psbTestR -and -not $global:__BashTestBad) { $global:LASTEXITCODE = 0 } "
                        + "else { $global:LASTEXITCODE = $(if ($global:__BashTestBad) { 2 } else { 1 }); Write-Error '' -ErrorAction SilentlyContinue })");
                else
                    sb.Append(PsBuild.SetExitFromBool(boolText));
            }
            // ShAssignment in && / || chains: wrap so PS doesn't output the value.
            // A multi-pair assignment (e.g. `TEMP=x TMP=y`, as Claude Code's Bash
            // tool emits in its env-setup preamble) emits two statements joined by
            // "; ". PowerShell's grouping `(...)` cannot hold a statement list
            // ("Missing closing ')'"), only the subexpression `$(...)` can — so use
            // `[void]$(...)` for the multi-statement case. A single pair stays in the
            // cheaper `[void](...)` form (bit-identical to prior output).
            else if (cmd is Command.ShAssignment shAssign)
            {
                var assignText = Emit(cmd);
                sb.Append(PsBuild.VoidStatement(assignText));
                // `r=$(false) || echo fb`: the assignment's status is its command substitution's, which `||` only sees
                // once bridged from LASTEXITCODE to $? (a plain `x=1` is always 0).
                if (!isFinal && !shAssign.IsLocal && !shAssign.IsExport && ContainsCommandSub(shAssign))
                    sb.Append("; " + PsBuild.SignalFailIfFailed());
            }
            else if (cmd is Command.Pipeline { Negated: true } negPipelineInChain)
            {
                // Negated pipeline (! cmd) in && / || chain: mirror the BoolExpr-in-chain
                // pattern. PS pipeline-chain operators check $?, not $LASTEXITCODE.
                // Use Write-Error to set $?=false when negation yields failure so the
                // chain operators observe the correct success/failure state.
                var unnegated = negPipelineInChain with { Negated = false };
                // The command's own output must still reach stdout (`! echo hi || .` prints hi): run it as a statement, then invert its status.
                sb.Append(EmitPipeline(unnegated) + "; " + PsBuild.SetExitFromBool("$global:LASTEXITCODE -ne 0"));
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
                var simpleText = EmitSimpleFed(simple);
                sb.Append(NeedsChainOperandSubexpression(simpleText) ? $"$({simpleText})" : simpleText);
                // `&&`/`||` test PowerShell's $?, which a bash function's `return 3` (or a cmdlet
                // that sets only the exit code) leaves true — `f || echo fb` never fell back.
                // Bridge the exit code exactly as the pipeline operand does; the final operand's
                // status is read from LASTEXITCODE by whatever follows, so it needs no bridge.
                if (!isFinal) sb.Append("; " + PsBuild.SignalFailIfFailed());
            }
            else
            {
                // A COMPOUND command (while / for / case / subshell / brace group …)
                // emits a PowerShell *statement*, and `&&` / `||` are pipeline-chain
                // operators that require a pipeline operand — `cmd || switch (…) {…}`
                // and `cmd && while (…) {…}` are both parse errors. Wrapping in the
                // subexpression `$( … )` makes it a valid operand; unlike `( … )` it
                // can hold a statement list (the loop-guard prologue), and unlike
                // `& { … }` it runs in the CURRENT scope, so assignments inside the
                // body still persist the way bash's do.
                var compoundText = Emit(cmd);
                sb.Append(NeedsChainOperandSubexpression(compoundText)
                    ? PsBuild.Subexpr(compoundText)
                    : compoundText);
                // A `$( … )` operand never propagates $? (see the pipeline case), so `(exit 4) ||
                // echo fb` never fell back: bridge the exit code.
                if (!isFinal) sb.Append("; " + PsBuild.SignalFailIfFailed());
            }
            }
            finally
            {
                _andOrChainDepth--;
                if (!isFinal) _errexitExemptDepth--;
            }

            if (isFinal && errexitFinalOperand)
            {
                // Re-wrap the final operand (everything after its leading " op ").
                int textStart = i > 0 ? operandStart + andOr.Ops[i - 1].Length + 2 : operandStart;
                var operand = sb.ToString(textStart, sb.Length - textStart);
                sb.Length = textStart;
                sb.Append(PsBuild.ErrexitFinalOperand(operand));
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

    /// <summary>
    /// A SIMPLE command can still emit a PowerShell statement LIST — the runtime-`eval`
    /// block (`$__psbash_eval_src = …; …; Invoke-Expression …`), an env-pair prefix
    /// (`$env:X = "1"; cmd`), a `cd` rewrite. None of those is a valid bare pipeline
    /// segment, so `eval "find …" | sort` failed with "An empty pipe element is not
    /// allowed". <see cref="IsCompoundPipelineStage"/> already handles loops / groups /
    /// conditionals; this covers the statement-list SIMPLE commands it does not see.
    /// `&amp; { … }` (not `$( … )`) keeps the stage streaming, and its child scope
    /// matches bash, which runs every pipe stage in its own subshell.
    /// </summary>
    private static string WrapPipelineStageIfStatementList(string emitted)
    {
        if (emitted.StartsWith("& {", StringComparison.Ordinal)
            || emitted.StartsWith("$(", StringComparison.Ordinal)
            || !emitted.Contains("; ", StringComparison.Ordinal))
            return emitted;

        return $"& {{ {emitted} }}";
    }

    [ThreadStatic] private static int _timeDepth;

    private static string EmitPipeline(Command.Pipeline pipeline)
    {
        // `time [-p] pipeline`: emit the pipeline itself, then wrap it in the timing report.
        if (pipeline.Time != PipelineTime.None)
        {
            int depth = _timeDepth++;
            try
            {
                var untimed = pipeline with { Time = PipelineTime.None };
                string text = untimed.Commands.Length == 1 && !untimed.Negated ? Emit(untimed.Commands[0]) : EmitPipeline(untimed);
                return PsBuild.Timed(text, pipeline.Time == PipelineTime.Posix, "$__psbash_time" + depth);
            }
            finally { _timeDepth--; }
        }
        var entryScope = _inStdinScope;
        // Each stage of a real pipeline is a subshell in bash: errexit inside one never ends the script.
        bool exempt = pipeline.Commands.Length > 1 || pipeline.Negated;
        if (exempt) _errexitExemptDepth++;
        try { return EmitPipelineStages(pipeline, entryScope); }
        finally
        {
            _inStdinScope = entryScope;
            if (exempt) _errexitExemptDepth--;
        }
    }

    /// <summary>
    /// <c>yes | { …; }</c> / <c>seq … | { …; }</c>: a literal-argument producer that can be unbounded, piped into a
    /// compound that opens a stdin scope. The scope reads the producer lazily from a background runspace
    /// (<see cref="PsBuild.StdinScopeLazy"/>) so a reader that stops early (<c>head -n1</c>, a lone <c>read</c>)
    /// ends the producer — a script block after a pipe would only start once the producer finished (never).
    /// Other producers keep the eager scope (<see cref="PsBuild.StdinScope"/>).
    /// </summary>
    private static bool TryEmitLazyProducerStage(Command.Pipeline pipeline, out string emitted)
    {
        emitted = "";
        if (pipeline.Commands.Length != 2 || pipeline.Negated || pipeline.Ops[0] != "|"
            || pipeline.Commands[0] is not Command.Simple producer
            || !producer.EnvPairs.IsEmpty || !producer.HereDocs.IsDefaultOrEmpty
            || !producer.Redirects.IsDefaultOrEmpty || producer.Words.IsDefaultOrEmpty
            || GetLiteralValue(producer.Words[0]) is not ("yes" or "seq"))
            return false;
        foreach (var w in producer.Words)
            if (GetLiteralValue(w) is null) return false;

        var cmd = pipeline.Commands[1];
        bool opensScope = cmd is Command.Subshell or Command.BraceGroup or Command.ForIn
                or Command.ForArith or Command.If or Command.Case
            || (cmd is Command.While loop && !IsWhileRead(loop.Cond, out _, out _));
        if (!opensScope || !TryEmitMappedCommand(producer, out var producerText))
            return false;

        var body = WithStdinScope(true, () => Emit(cmd));
        emitted = $"& {{ {PsBuild.StdinScopeLazy(producerText, body)} }}";
        return true;
    }

    // Mapped commands whose cmdlet has no pipeline-input parameter (bash ignores stdin for them too).
    private static readonly HashSet<string> StdinIgnoringCommands = new(StringComparer.Ordinal)
    {
        "seq", "sleep", "pwd", "date", "basename", "dirname", "uname", "whoami", "hostname", "id", "echo",
        "printf", "expr", "mktemp", "readlink", "realpath", "which", "ls", "ps", "stat", "du", "touch", "mkdir", "chmod",
        "rmdir", "tree", "kill", "yes", "tput", "ping", "traceroute", "umask", "ulimit",
    };

    private static string EmitPipelineStages(Command.Pipeline pipeline, bool entryScope)
    {
        // Without shopt -s lastpipe the last stage is a subshell: cho hi | read x leaves x unset.
        if (!_scriptLastpipe && pipeline.Commands.Length > 1 && pipeline.Commands[^1] is Command.Simple { Words.Length: > 0 } lastRead
            && GetLiteralValue(lastRead.Words[0]) is "read" or "mapfile" or "readarray")
            pipeline = pipeline with { Commands = pipeline.Commands.SetItem(pipeline.Commands.Length - 1, new Command.Subshell(lastRead, ImmutableArray<Redirect>.Empty)) };
        if (TryEmitLazyProducerStage(pipeline, out var lazy)) return lazy;
        var sb = new StringBuilder();
        // pipefail script: each stage reports its own status (Invoke-BashPipeStage) into $__pf.
        bool perStage = _pipefailMode && pipeline.Commands.Length > 1;
        int stageStart = 0;
        for (int i = 0; i < pipeline.Commands.Length; i++)
        {
            // Only the FIRST stage reads the enclosing compound's stdin; every later stage reads
            // the pipe. (A compound stage re-enters a scope of its own below.)
            _inStdinScope = i == 0 && entryScope;
            if (i > 0)
            {
                var op = pipeline.Ops[i - 1];
                if (op == "|&")
                    sb.Append(" 2>&1 | ");
                else
                    sb.Append(" | ");
            }
            stageStart = sb.Length;

            var cmd = pipeline.Commands[i];
            // `... | echo "[$(cat)]"`: echo ignores the pipe, but the command substitution inherits it as stdin.
            // Run the stage as `{ echo ...; }`, whose stdin scope hands the pipe to the substitution.
            if (i > 0 && cmd is Command.Simple { EnvPairs.IsEmpty: true, Words.Length: >= 1 } substStage
                && GetLiteralValue(substStage.Words[0]) is { } substName && StdinIgnoringCommands.Contains(substName)
                && !HasInputRedirect(substStage.Redirects) && substStage.HereDocs.IsDefaultOrEmpty
                && substStage.Words.Any(w => ContainsCommandSub(w.Parts)))
                cmd = new Command.BraceGroup(substStage, ImmutableArray<Redirect>.Empty);
            if (i > 0 && cmd is Command.Simple simple)
            {
                bool isLastStage = i == pipeline.Commands.Length - 1;
                // A command that never reads stdin has no pipeline-input parameter, so PowerShell rejects the upstream
                // records (`echo a | seq 2`), and a ProcessRecord cmdlet with an empty upstream never runs (`sleep 0.1 | seq 3`).
                // Drain the input inside a block and run the command once, as bash does.
                bool ignoresStdin = simple.Words.Length >= 1 && simple.EnvPairs.IsEmpty && GetLiteralValue(simple.Words[0]) is { } stdinName
                    && StdinIgnoringCommands.Contains(stdinName);
                // `... | cmd < file`, `... | cmd <<< text`, `... | cmd <<EOF`: the redirect replaces the pipe as the stage's stdin, so the
                // upstream records are drained and the command runs exactly as it would on its own.
                bool ownStdin = HasInputRedirect(simple.Redirects)
                    || !simple.HereDocs.IsDefaultOrEmpty && simple.HereDocs.Any(h => h.Fd < 3);
                if (ownStdin)
                    sb.Append("& { $null = @($input); ").Append(WithPipeStageFeed(false, () => Emit(simple))).Append(" }");
                else {
                if (ignoresStdin) sb.Append("& { $null = @($input); ");
                if (simple.EnvPairs.IsEmpty)
                    sb.Append(EmitSimplePipeStage(simple, isLastStage));
                else
                {
                    // `… | VAR=v cmd`: the prefix applies to THAT stage only. Emit the bare stage, then
                    // wrap it in the save/set/restore block (which has to forward the stage's input).
                    var bare = simple with { EnvPairs = ImmutableArray<EnvPair>.Empty };
                    sb.Append(WrapEnvPairsForPipeStage(
                        simple.EnvPairs, WithEnvShadow(simple.EnvPairs, () => EmitSimplePipeStage(bare, isLastStage))));
                }
                if (ignoresStdin) sb.Append(" }");
                }
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
                //
                // A compound after the first stage additionally needs the piped records
                // on its stdin: bash connects the pipe to the group's fd 0, so
                // `echo hi | { read y; ... }` reads "hi" and `{ sort; }` sorts it.
                // PowerShell's `& { }` does not feed `$input` to the commands inside, so
                // EmitCompoundStage drains it into the shared stdin queue (a stdin scope)
                // that `read` and the fed readers consume from. The body still runs
                // ONCE — bash's semantics, unlike a per-line ForEach-Object envelope.
                sb.Append(EmitCompoundStage(cmd, isFirstStage: i == 0, entryScope, lastpipeStage: _scriptLastpipe && i > 0 && i == pipeline.Commands.Length - 1));
            }
            else if (pipeline.Commands.Length > 1)
                sb.Append(WrapPipelineStageIfStatementList(Emit(cmd)));
            else
                sb.Append(Emit(cmd));

            // `echo "${x:?}" | head`: each stage is a subshell, so the parameter error ends only that stage.
            if (pipeline.Commands.Length > 1)
            {
                var rawStage = sb.ToString(stageStart, sb.Length - stageStart);
                if (rawStage.Contains("BashParamErrorException]::Reported", StringComparison.Ordinal)
                    && !rawStage.Contains("catch [PsBash.Cmdlets.BashParamErrorException]", StringComparison.Ordinal))
                {
                    sb.Length = stageStart;
                    sb.Append(i == 0 ? PsBuild.ShellStateScope(rawStage) : "& { " + PsBuild.ShellStateGuard("$input | " + rawStage) + " }");
                }
            }

            if (perStage)
            {
                // The stage as emitted may be a statement list (the first stage is not wrapped above); a
                // steppable pipeline holds ONE pipeline, so such a stage becomes `& { … }` first.
                var stageText = sb.ToString(stageStart, sb.Length - stageStart);
                // The first stage may be an expression (`$(false)`), which no steppable pipeline can hold.
                stageText = i == 0 && !stageText.StartsWith("& {", StringComparison.Ordinal)
                    ? $"& {{ {stageText} }}"
                    : WrapPipelineStageIfStatementList(stageText);
                // A trailing redirection (`cat >/dev/null`) cannot be the root of a steppable pipeline (it throws a
                // NullReferenceException on Begin): run it in a script block that feeds `$input` to the stage.
                if (i > 0 && !stageText.StartsWith("& {", StringComparison.Ordinal)
                    && System.Text.RegularExpressions.Regex.IsMatch(stageText, @"\s\d?>{1,2}[&\s]?\S"))
                    stageText = $"& {{ $input | {stageText} }}";
                sb.Length = stageStart;
                sb.Append($"Invoke-BashPipeStage -Slot $__pf -Index {i} -Script {{ {stageText} }}");
            }
        }

        var body = sb.ToString();
        if (perStage)
            // The pipeline's status under pipefail: the last stage that failed (0 when none did).
            body = $"$__pf = [int[]]::new({pipeline.Commands.Length}); {body}; $global:PIPESTATUS = $__pf; "
                 + "if ($global:__BashPipefail) { $global:LASTEXITCODE = 0; foreach ($__s in $__pf) { if ($__s -ne 0) { $global:LASTEXITCODE = $__s } } }";

        // Fused lane: an all-mapped, terminal-bound, plain-`|` pipeline runs inside
        // Invoke-BashFusedPipeline so its output returns to the launcher in a few
        // large batched frames instead of one IPC frame per line. IsFusablePipeline
        // already excludes negated pipelines, so this never collides with the
        // negation tail below.
        // Inside a compound's stdin scope the first stage reads the shared queue (a feed the
        // compiled cores know nothing about), so the chain stays on the plain pipeline.
        // Eval/source'd code is not terminal-bound: its output feeds the caller's pipes, redirects and
        // substitutions, and a fused batch renders its record boundary as the platform newline (CRLF on
        // Windows) — . f | wc -c counted the extra CR.
        if (_captureDepth == 0
            && !perStage
            && !entryScope
            && _context != TranspileContext.Eval
            && FusedLane.IsFusionEnabled(FusionEnabledOverride)
            && FusedLane.IsFusablePipeline(pipeline))
        {
            // Phase-2b: also emit a structured stage list when every stage's args are
            // plain compile-time literals, so the runtime can run a true streaming
            // line→line chain (no per-line PSObject). Any non-literal arg (variable,
            // command-sub, glob, brace expansion) leaves the stage list off and the
            // cmdlet uses the phase-2a scriptblock (still batched, still correct).
            var stages = FusedLane.TryBuildFusedStages(pipeline);
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
    /// One SIMPLE command as a pipe-target stage (index &gt; 0) of <see cref="EmitPipelineStages"/>, WITHOUT
    /// its env prefix (the caller wraps that). The stage's pipeline input is forwarded by any script-block
    /// wrapper the emission needs (<see cref="_pipeStageFeedsInput"/>).
    /// </summary>
    private static string EmitSimplePipeStage(Command.Simple simple, bool isLastStage)
    {
        bool savedFeed = _pipeStageFeedsInput;
        _pipeStageFeedsInput = true;
        try
        {
            // true/false as pipe targets need a cmdlet form, not a subexpression.
            // $($global:LASTEXITCODE = 0; [void]$true) cannot be a pipeline segment.
            var word0 = simple.Words.Length >= 1 ? GetLiteralValue(simple.Words[0]) : null;
            // Redirects are allowed here (e.g. `… | true 2>&1`): `true`/`false` ignore
            // stdin, so we emit Out-Null and append any redirects. Excluding the redirect
            // case sent it to the Emit fallback, which emitted the bare subexpression
            // `$($global:LASTEXITCODE = 0; [void]$true)` — not a valid pipeline segment.
            if (simple.Words.Length >= 1 && simple.EnvPairs.IsEmpty
                && word0 is "true" or ":" or "false")
            {
                var noop = new StringBuilder("Out-Null");
                EmitPipeTargetRedirects(simple, noop);
                if (isLastStage || _pipefailMode)
                {
                    // Consume all piped input; `true` / `:` succeed (exit 0), `false` fails (exit 1).
                    // Out-Null is a valid pipeline cmdlet; the status is set after the pipeline.
                    noop.Append(word0 == "false"
                        ? "; $($global:LASTEXITCODE = 1; Write-Error '' -ErrorAction SilentlyContinue)"
                        : "; $global:LASTEXITCODE = 0");
                }
                return noop.ToString();
            }

            if (word0 == "eval" && simple.EnvPairs.IsEmpty)
            {
                var evalSb = new StringBuilder(EmitEvalWithStdin(
                    () => TryEmitMappedCommand(simple, out var evalText) ? evalText : Emit(simple), dotSource: false));
                EmitPipeTargetRedirects(simple, evalSb);
                return evalSb.ToString();
            }

            if (TryEmitMappedCommand(simple, out var mapped))
            {
                var mappedSb = new StringBuilder(mapped);
                // Append any redirects from the pipe target (TryEmitMappedCommand only handles args)
                EmitPipeTargetRedirects(simple, mappedSb);
                return mappedSb.ToString();
            }

            // A non-mapped pipe target (native/external program, or a
            // PowerShell command ps-bash does not wrap) receives the
            // pipeline through PowerShell's native binder, which FORMATS
            // each object — a BashObject renders as its property table
            // ("BashText NoTrai…"), not its text. bash pipes bytes, so
            // convert every upstream object to its bash text first. This
            // is the ONE shared rule for cmdlet-output → native stdin;
            // Get-BashText is identity on a string and on any object with
            // no BashText, so it is safe for every pipe target.
            return NativeStdinFeedStage(simple)
                + WrapPipelineStageIfStatementList(Emit(simple));
        }
        finally { _pipeStageFeedsInput = savedFeed; }
    }

    /// <summary>
    /// The stage that sits in front of a NON-MAPPED command to turn the upstream records into its stdin. A
    /// command word that could be a real program (<see cref="StdinReaders.Kind.Native"/>) gets
    /// <see cref="PsBuild.NativeStdinFeed"/> — the exact bytes bash would write, never PowerShell's per-record
    /// <c>\r\n</c> — which falls back to the record's text when the name turns out to be a function or alias.
    /// Anything else (a builtin, a computed command word) keeps the plain text conversion.
    /// The result ends in <c>| </c>, ready to be followed by the command.
    /// </summary>
    private static string NativeStdinFeedStage(Command.Simple simple) =>
        NativeStdinFeedOrNull(simple) ?? "ForEach-Object { Get-BashText $_ } | ";

    /// <summary><see cref="NativeStdinFeedStage"/>'s converter stage, or null when the command is not a native candidate.</summary>
    private static string? NativeStdinFeedOrNull(Command.Simple simple) =>
        !simple.Words.IsDefaultOrEmpty
        && GetLiteralValue(simple.Words[0]) is { Length: > 0 } name
        && StdinReaders.Classify(name) == StdinReaders.Kind.Native
        && !TryEmitMappedCommand(simple, out _)
            ? PsBuild.NativeStdinFeed(name) + " | "
            : null;

    /// <summary>
    /// <c>VAR=v</c> prefix on a pipe-target stage. The save/set/restore block is a statement list, so it
    /// needs a script block to sit in a pipeline — and a script block does not hand the stage's pipeline
    /// input to the command inside, so the input is forwarded explicitly (<c>$input | stage</c>). The
    /// assignment is visible to that stage only and restored afterwards (no leak).
    /// </summary>
    private static string WrapEnvPairsForPipeStage(ImmutableArray<EnvPair> pairs, string stage)
        => "& { " + WrapEnvPairs(pairs, "$input | " + stage) + " }";

    /// <summary>
    /// A compound command as a stage of a multi-stage pipeline: <c>&amp; { … }</c>. A stage AFTER the
    /// first receives the pipe as its stdin, so it opens a stdin scope (<see cref="PsBuild.StdinScope"/>):
    /// every stdin-reading command inside — <c>sort</c>, <c>cat</c>, <c>read</c>, a native — consumes the
    /// same queue, in order. The FIRST stage keeps whatever stdin its surroundings have. A
    /// <c>while read</c> loop streams <c>$input</c> itself and an arithmetic command / function
    /// definition reads nothing, so those open no scope (the body sees no shared stdin).
    /// </summary>
    private static string EmitCompoundStage(Command cmd, bool isFirstStage, bool entryScope, bool lastpipeStage = false)
    {
        // bash runs every pipe stage in a subshell: `{ x=2; } | cat` must leave x alone. A Subshell stage already
        // carries its own scope; a function definition changes the function table by design here.
        // The guard, not a nested scope: the stage's own `& { }` is the scope, and it must keep its `$input`.
        string Scoped(string inner) => cmd is Command.Subshell or Command.ShFunction || lastpipeStage ? inner : PsBuild.ShellStateGuard(inner);

        if (isFirstStage)
            return $"& {{ {Scoped(Emit(cmd))} }}";

        bool opensScope = cmd is Command.Subshell or Command.BraceGroup or Command.ForIn
                or Command.ForArith or Command.If or Command.Case
            || (cmd is Command.While loop && !IsWhileRead(loop.Cond, out _, out _));
        if (!opensScope)
            return $"& {{ {Scoped(Emit(cmd))} }}";

        return $"& {{ {PsBuild.StdinScope(WithStdinScope(true, () => Scoped(Emit(cmd))))} }}";
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
            or Command.ShFunction or Command.Select
            // `[[ ]]` and a bare assignment emit an expression (`$(if ...)`), which PowerShell allows only as
            // the FIRST pipeline element: `[[ a = b ]] | [[ a = b ]]` was a parse error.
            or Command.BoolExpr or Command.ShAssignment;

    private static void EmitPipeTargetRedirects(Command.Simple cmd, StringBuilder sb)
    {
        if (cmd.Redirects.IsEmpty) return;
        AppendRedirectTail(sb, cmd.Redirects);
    }

    /// <summary>
    /// Append a command's redirect tail to <paramref name="sb"/>. Stdout/stderr redirects are
    /// modelled as bash applies them — left to right, per fd, LAST one wins — by
    /// <see cref="TryAppendModelledRedirectTail"/>: every FILE target goes through one
    /// <c>Invoke-BashRedirect</c> stage (stderr via a <c>2&gt;&amp;1</c> merge, so it is written
    /// byte-faithfully, not by PowerShell's CRLF/cmdlet-prefixed native <c>2&gt; f</c>), and only
    /// <c>$null</c> / the <c>2&gt;&amp;1</c> merge stay native, each at most once (PowerShell
    /// rejects redirecting a stream twice). A tail with a redirect the model does not cover
    /// (user fds, <c>&lt;&amp;</c>, …) keeps the legacy emission below.
    /// <para>Single source for EmitSimple, EmitSubshell, and
    /// <see cref="EmitPipeTargetRedirects"/>: the subshell <c>&lt; file</c> bug came
    /// from this selection being copy-pasted three times and a fix landing in only one.
    /// Input redirects (<c>&lt; file</c>) are the caller's responsibility — they wrap
    /// the whole command (<c>Get-Content | …</c>), so partition them out first.</para>
    /// </summary>
    private static void AppendRedirectTail(StringBuilder sb, IEnumerable<Redirect> redirects)
    {
        var list = redirects as IReadOnlyList<Redirect> ?? redirects.ToList();
        if (TryAppendModelledRedirectTail(sb, list))
            return;
        AppendLegacyRedirectTail(sb, list);
    }

    // One fd's destination while a redirect list is applied: the terminal default, the null
    // device, an opened file (index into the open list), or a copy of the OTHER fd's default
    // taken by a dup before that fd was redirected — fd 2: `2>&1 >f` (StdoutDefault); fd 1:
    // `>&2 2>/dev/null` (StderrDefault — stdout still reaches the terminal's stderr).
    private enum FdDestKind { Default, Null, File, StdoutDefault, StderrDefault }

    private readonly record struct FdDest(FdDestKind Kind, int File = -1);

    /// <summary>
    /// Emit a stdout/stderr redirect list as bash's fd model (see <see cref="AppendRedirectTail"/>).
    /// False — nothing appended — when a redirect is outside the model.
    /// </summary>
    private static bool TryAppendModelledRedirectTail(StringBuilder sb, IReadOnlyList<Redirect> redirects)
    {
        if (redirects.Count == 0)
            return true;

        var opened = new List<(string Target, bool Append, bool Force)>();
        var fd1 = new FdDest(FdDestKind.Default);
        var fd2 = new FdDest(FdDestKind.Default);
        FdDest Open(string target, bool append, bool force = false)
        {
            if (target == "$null") return new FdDest(FdDestKind.Null);
            opened.Add((target, append, force));
            return new FdDest(FdDestKind.File, opened.Count - 1);
        }

        foreach (var r in redirects)
        {
            if (r.Fd is not (1 or 2))
                return false;
            var target = EmitFileRedirectTarget(r.Target);
            switch (r.Op)
            {
                case ">" or ">|" or ">>":
                {
                    var dest = Open(target, r.Op == ">>", r.Force);
                    if (r.Fd == 1) fd1 = dest; else fd2 = dest;
                    break;
                }
                case "&>" or "&>>":
                    fd1 = fd2 = Open(target, r.Op == "&>>");
                    break;
                case ">&" when target == "-":
                    if (r.Fd == 1) fd1 = new FdDest(FdDestKind.Null); else fd2 = new FdDest(FdDestKind.Null);
                    break;
                case ">&" when target == r.Fd.ToString(System.Globalization.CultureInfo.InvariantCulture):
                    break; // `1>&1` / `2>&2`: a dup onto itself changes nothing
                case ">&" when r.Fd == 2 && target == "1":
                    // fd 2 becomes a copy of fd 1 AS IT IS NOW (bash dup2 semantics).
                    fd2 = fd1.Kind switch
                    {
                        FdDestKind.Default => new FdDest(FdDestKind.StdoutDefault),
                        FdDestKind.StderrDefault => new FdDest(FdDestKind.Default), // `>&2 2>&1`
                        _ => fd1,
                    };
                    break;
                case ">&" when r.Fd == 1 && target == "2":
                    // `>&2`: fd 1 becomes a copy of fd 2 AS IT IS NOW — `2>/dev/null >&2` discards
                    // stdout, `>&2 2>/dev/null` still shows it, `2>f >&2` writes both to f.
                    fd1 = fd2.Kind switch
                    {
                        FdDestKind.Default => new FdDest(FdDestKind.StderrDefault),
                        FdDestKind.StdoutDefault => new FdDest(FdDestKind.Default), // `2>&1 >&2`
                        _ => fd2,
                    };
                    break;
                case ">&" when r.Fd == 1 && !IsAllDigits(target):
                    // `>&file` is bash's synonym for `&>file`.
                    fd1 = fd2 = Open(target, append: false);
                    break;
                default:
                    return false;
            }
        }

        // The open files the final fds no longer point at were still opened (created / truncated)
        // in order by bash: `echo x >a >b` leaves an empty `a`.
        var truncate = new List<string>();
        var touch = new List<string>();
        for (int i = 0; i < opened.Count; i++)
        {
            if ((fd1.Kind == FdDestKind.File && fd1.File == i) || (fd2.Kind == FdDestKind.File && fd2.File == i))
                continue;
            (opened[i].Append ? touch : truncate).Add(opened[i].Target);
        }

        bool needsStage = fd1.Kind == FdDestKind.File || fd2.Kind == FdDestKind.File
            || truncate.Count > 0 || touch.Count > 0
            || (fd2.Kind == FdDestKind.StdoutDefault && fd1.Kind != FdDestKind.Default);

        if (!needsStage)
        {
            if (fd1.Kind == FdDestKind.Null) sb.Append(" >$null");
            // Both discarded: the familiar `>$null 2>&1` (stderr follows stdout into $null).
            if (fd2.Kind == FdDestKind.Null) sb.Append(fd1.Kind == FdDestKind.Null ? " 2>&1" : " 2>$null");
            else if (fd2.Kind == FdDestKind.StdoutDefault) sb.Append(PsBuild.MergeStderrIntoStdout);
            // After the command's own stderr handling: its stdout records become stderr records,
            // which that `2>…` (on the command, not on this stage) no longer touches.
            if (fd1.Kind == FdDestKind.StderrDefault) sb.Append(PsBuild.StdoutToStderrStage);
            return true;
        }

        // Error records enter the stage only when stderr is redirected somewhere other than its default.
        if (fd2.Kind == FdDestKind.Null) sb.Append(" 2>$null");
        else if (fd2.Kind != FdDestKind.Default) sb.Append(" 2>&1");

        string? stdoutPath = fd1.Kind switch
        {
            FdDestKind.File => opened[fd1.File].Target,
            FdDestKind.Null => "$null",
            _ => null,
        };
        string? errorPath = null;
        bool errorAppend = false;
        if (fd2.Kind == FdDestKind.File && !(fd1.Kind == FdDestKind.File && fd1.File == fd2.File))
        {
            errorPath = opened[fd2.File].Target;
            errorAppend = opened[fd2.File].Append;
        }
        sb.Append(PsBuild.RedirectStage(
            stdoutPath, fd1.Kind == FdDestKind.File && opened[fd1.File].Append,
            errorPath, errorAppend,
            passErrors: fd2.Kind == FdDestKind.StdoutDefault,
            truncate, touch,
            force: fd1.Kind == FdDestKind.File && opened[fd1.File].Force,
            errorForce: errorPath is not null && opened[fd2.File].Force));
        // `>f >&2`, `2>f >&2`-style lists: the stage (no -Path) passes stdout on to stderr.
        if (fd1.Kind == FdDestKind.StderrDefault) sb.Append(PsBuild.StdoutToStderrStage);
        return true;
    }

    /// <summary>
    /// The pre-model tail: every NON-stdout-file redirect inline via <see cref="EmitRedirect"/>, the
    /// FIRST stdout file redirect piped through <c>Invoke-BashRedirect</c>. Kept for the redirect
    /// lists <see cref="TryAppendModelledRedirectTail"/> does not cover.
    /// </summary>
    private static void AppendLegacyRedirectTail(StringBuilder sb, IReadOnlyList<Redirect> redirects)
    {
        Redirect? fileRedirect = null;
        // PowerShell rejects redirecting the same stream twice ("The error stream
        // for this command is already redirected"). bash tolerates it — the
        // redundant-but-legal `cmd &>/dev/null 2>&1` appears in real scripts, and
        // `&>` ALREADY emits the `2>&1` merge, so the explicit one made the whole
        // file unparseable. Emit the stderr merge at most once.
        bool stderrMerged = false;
        foreach (var redirect in redirects)
        {
            var target = EmitFileRedirectTarget(redirect.Target);
            bool isStdoutFile = redirect.Fd == 1
                && (redirect.Op == ">" || redirect.Op == ">>")
                && target != "$null";

            if (isStdoutFile && fileRedirect is null)
            {
                fileRedirect = redirect;
                continue;
            }

            var emitted = EmitRedirect(redirect);
            bool mergesStderr = emitted.Contains("2>&1", StringComparison.Ordinal);
            if (mergesStderr && stderrMerged)
            {
                // Drop ONLY a redirect that is nothing but the duplicate merge; a
                // fragment that also redirects stdout (`>file 2>&1`) still carries
                // information and must not be lost.
                if (emitted == "2>&1")
                    continue;
            }
            stderrMerged |= mergesStderr;
            sb.Append(' ');
            sb.Append(emitted);
        }

        if (fileRedirect is not null)
        {
            var target = EmitFileRedirectTarget(fileRedirect.Target);
            sb.Append(" | Invoke-BashRedirect -Path ");
            sb.Append(target);
            if (fileRedirect.Op == ">>")
                sb.Append(" -Append");
            if (fileRedirect.Force)
                sb.Append(" -Force");
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
    /// STDIN: the input redirect (<c>&lt; file</c>, <c>&lt;&lt;&lt;</c>, <c>&lt;&lt;</c>) becomes the
    /// body's stdin SCOPE (<see cref="PsBuild.StdinScope"/>), so every stdin-reading command inside
    /// consumes it, not just <c>while read</c>. <paramref name="stdinScope"/> is false for that loop,
    /// which streams <c>$input</c> itself. See emitter-strategy.md "Compound-command stdin".
    /// </para>
    /// </summary>
    private static string ApplyCompoundRedirects(
        string body, ImmutableArray<Redirect> redirects, bool stdinScope = true)
    {
        if (redirects.IsDefaultOrEmpty)
            return body;

        Redirect? inputRedirect = null;
        var tailRedirects = new List<Redirect>(redirects.Length);
        var highFds = new List<Redirect>();
        foreach (var redirect in redirects)
        {
            // `<` file and `<<<` here-string both supply the group's stdin; the last
            // one wins (bash).
            if (redirect.Fd >= 3)
                highFds.Add(redirect);
            else if (redirect.Fd == 0 && redirect.Op is "<" or "<<" or "<<<")
                inputRedirect = redirect;
            else
                tailRedirects.Add(redirect);
        }

        // With an input redirect the body is a stdin scope: the redirected data is drained into the
        // shared queue the body's stdin-reading commands consume (the body was emitted with
        // _inStdinScope set — see EmitCompoundBody). A `while read` loop streams `$input` itself.
        var sb = new StringBuilder("& { ");
        sb.Append(inputRedirect is not null && stdinScope ? PsBuild.StdinScope(body) : body);
        sb.Append(" }");
        AppendRedirectTail(sb, tailRedirects);
        string result = sb.ToString();

        if (inputRedirect is { Here: { } here })
        {
            result = $"{EmitHereDocLiteral(here)} | Emit-BashLine | {result}";
        }
        else if (inputRedirect is not null)
        {
            var inTarget = EmitInputRedirectTarget(inputRedirect.Target);
            // /dev/null maps to $null; `Get-Content $null` errors. Its stdin is EMPTY (EOF), which
            // for a stdin scope must still replace any outer pipe, so feed it nothing explicitly.
            if (inTarget != "$null")
                result = PsBuild.InputRedirectGuard(inTarget, $"Invoke-BashCat {inTarget} -RawStdinBytes | {result}", RedirectFailExit());
            else if (stdinScope)
                result = $"@() | {result}";
        }

        return WithScopedHighFds(result, highFds);
    }

    private static string EmitCd(ImmutableArray<CompoundWord> args)
    {
        // Leading options: `--` ends them (`cd -- "$dir"` is the safe spelling for a dir that starts with `-`);
        // -L / -P pick logical / physical symlink handling, which this cd does not distinguish, so they are accepted.
        while (!args.IsEmpty && GetLiteralValue(args[0]) is "--" or "-L" or "-P")
        {
            bool endOfOptions = GetLiteralValue(args[0]) == "--";
            args = args.RemoveAt(0);
            if (endOfOptions) break;
        }
        // bash: `cd a b` is "too many arguments", status 1, and the directory does not change.
        if (args.Length > 1)
            return $"Write-Error 'cd: too many arguments' -ErrorAction Continue; {PsBuild.SetStatus(1)}";

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
            else if (TryTranslateMsysDrivePath(literal, out var winPath, allowBareDriveRoot: true))
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
            if (emitted.StartsWith("$HOME/", StringComparison.Ordinal))
                targetExpr = "$HOME + " + QuotePsString(emitted["$HOME".Length..]);
            else if (emitted.StartsWith('~'))
                // Unresolved ~user / ~N (dirstack) has no PowerShell equivalent and degrades
                // to the literal bareword text (WordPart.TildeSub's `_ => $"~{ts.User}"`
                // branch) — quote it through PsBuild so the assignment is a valid PS string
                // literal, not an invalid bareword (`$__psbash_cd_target = ~user`).
                targetExpr = QuotePsString(emitted);
            else if (args[0].Parts.Length > 1 && !TryGetPureLiteralText(args[0].Parts, out _))
                // A glued multi-part word is ARGUMENT-mode text (`$env:d/sub`): an assignment RHS is
                // expression mode, where that `/` DIVIDES — `cd $d/sub` failed outright. Evaluate it
                // as an argument. A single part (`$HOME`, `$env:x`, `"…"`) is already a value.
                targetExpr = "$(& { [string]$args[0] } " + emitted + ")";
            else
                targetExpr = emitted;
        }

        // CDPATH (`CDPATH=/srv; cd app` lands in /srv/app and echoes it) only matters for a target that may be relative
        // and does not start with `.`: the runtime (BashRuntime.CdPathLookup) decides; a literal that cannot use it skips it.
        bool useCdPath = !isDash && !args.IsEmpty && !targetExpr.StartsWith("$HOME", StringComparison.Ordinal)
            && args[0].Parts is not [WordPart.TildeSub, ..]
            && !(GetLiteralValue(args[0]) is { } cdLiteral
                 && (cdLiteral.Length == 0 || cdLiteral[0] is '/' or '\\' or '.' or '~' || cdLiteral.Contains(':')));

        // The success branch captures the dir we are leaving into $OLDPWD BEFORE
        // overwriting CurrentDirectory, so the next `cd -` can return to it.
        // The target goes through the runtime path policy (BashRuntime.MapPath: /tmp → $env:TEMP,
        // unix drive paths) — an expanded `cd $d` (d=/tmp) must reach the directory `cd /tmp` and
        // every cmdlet resolve, not GetFullPath's `C:\tmp`.
        var resolveAndAct =
            "$__psbash_cd_resolved = [System.IO.Path]::GetFullPath(" + PsBuild.RuntimeMapPath("[string]$__psbash_cd_target") + ", [System.Environment]::CurrentDirectory); " +
            "if ([System.IO.Directory]::Exists($__psbash_cd_resolved) -and [PsBash.Cmdlets.BashRuntime]::CdPathWalkExists(" + PsBuild.RuntimeMapPath("[string]$__psbash_cd_target") + ", [System.Environment]::CurrentDirectory)) { " +
            "$env:OLDPWD = [System.Environment]::CurrentDirectory; " +
            "$global:__PsBashCwd = $__psbash_cd_resolved; " +
            "[System.Environment]::CurrentDirectory = $__psbash_cd_resolved; " +
            "$env:PWD = $__psbash_cd_resolved; " +
            "Set-Location -LiteralPath $__psbash_cd_resolved -ErrorAction SilentlyContinue; " +
            (isDash ? "Write-Output $__psbash_cd_resolved; " : useCdPath ? "if ($__psbash_cd_via) { Write-Output $__psbash_cd_resolved }; " : "") +
            "$global:LASTEXITCODE = 0 " +
            "} else { Write-Error -Message (\"cd: \" + $__psbash_cd_target + \": No such file or directory\") -ErrorAction Continue; $global:LASTEXITCODE = 1 }";

        var prefix = "$__psbash_cd_target = " + targetExpr + "; " + (useCdPath
            ? "$__psbash_cd_via = [PsBash.Cmdlets.BashRuntime]::CdPathLookup([string]$__psbash_cd_target, [System.Environment]::CurrentDirectory); "
              + "if ($__psbash_cd_via) { $__psbash_cd_target = $__psbash_cd_via }; "
            : "");
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

    /// <summary>A let operand's text: literals, quotes and the unquoted * of x=3*4 (an arithmetic operator here).</summary>
    private static bool TryGetLetOperandText(CompoundWord word, out string text)
    {
        var sb = new StringBuilder();
        foreach (var part in word.Parts)
        {
            if (part is WordPart.GlobPart g) { sb.Append(g.Pattern); continue; }
            if (!TryGetPureLiteralText(ImmutableArray.Create(part), out var piece)) { text = ""; return false; }
            sb.Append(piece);
        }
        text = sb.ToString();
        return text.Length > 0;
    }

    private static string? TryEmitLetAsArithmetic(ImmutableArray<CompoundWord> args)
    {
        if (args.IsEmpty) return null;
        var statements = new List<string>(args.Length);
        foreach (var word in args)
        {
            if (!TryGetLetOperandText(word, out var text)) return null;
            try { statements.Add(EmitArithCommand(new Command.ArithCommand(BashArithmeticParser.Parse(text)))); }
            catch (ParseException) { return null; }
        }
        return string.Join("; ", statements);
    }

    /// <summary>
    /// Tries to emit a mapped PowerShell equivalent for known bash pipe-target commands.
    /// Returns false if the command is not a recognized pipe target.
    /// </summary>
    /// <summary>PowerShell default aliases that would otherwise beat a same-named shell function.</summary>
    private static readonly HashSet<string> PsAliasNames = new(StringComparer.Ordinal)
    {
        "type", "ls", "cat", "cp", "mv", "rm", "pwd", "echo", "sleep", "sort", "kill", "ps", "diff", "man", "mount", "rmdir", "clear", "history", "tee", "write", "where", "sc", "gc", "gm", "gl", "gi", "sl", "md",
    };

    /// <summary>True when <paramref name="name"/> is a command the emitter maps to an <c>Invoke-Bash*</c> cmdlet (ignoring script-defined functions).</summary>
    private static bool IsMappedCommandName(string name)
    {
        if (_stateBuiltins.Contains(name) || PsAliasNames.Contains(name)) return true;
        var prior = _definedFunctions;
        _definedFunctions = null;
        try { return TryEmitMappedCommand(new Command.Simple([new CompoundWord([new WordPart.Literal(name)])], [], []), out _); }
        finally { _definedFunctions = prior; }
    }

    private static bool TryEmitMappedCommand(Command.Simple cmd, out string result)
    {
        result = "";
        if (cmd.Words.IsEmpty)
            return false;

        var name = GetLiteralValue(cmd.Words[0]);
        if (name is null)
            return false;

        // A function the script defines under a mapped command's name wins over it (bash: functions precede builtins and PATH).
        if (_definedFunctions is { Count: > 0 } && _definedFunctions.Contains(name) && name is not ("command" or "builtin"))
            return false;

        var args = cmd.Words.RemoveAt(0);

        switch (name)
        {
            case "grep":
                result = EmitPassthrough("Invoke-BashGrep", args);
                return true;
            case "egrep":
            case "fgrep":
                // GNU wrappers: `egrep` is `grep -E`, `fgrep` is `grep -F`.
                result = EmitPassthrough("Invoke-BashGrep",
                    args.Insert(0, new CompoundWord([new WordPart.Literal(name == "egrep" ? "-E" : "-F")])));
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
            case "gawk":
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
                // find is an expression language, not getopt: `-o` (OR) and `-a` (AND) are
                // position-critical infix operators that collide with -OutVariable / -Arguments,
                // and the flags of a `-exec CMD ...` argv belong to CMD. find is on
                // OrderedArgCommands, so EVERY dash word is single-quoted and the cmdlet gets the
                // whole expression verbatim and in order (the old per-flag force-quote sets are gone).
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
            case "chmod":
                result = EmitPassthrough("Invoke-BashChmod", args);
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
            case "nohup":
                result = EmitPassthrough("Invoke-BashNohup", args);
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
            case "od":
                result = EmitPassthrough("Invoke-BashOd", args);
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
            case "sh":
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
            case "alias" when _aliasStaticOnly:
                // Expanded at transpile time (PsEmitter.Aliases.cs): record the alias for `alias` listings, define no function.
                result = EmitPassthrough("Invoke-BashAlias", args, cmdletSwitch: " -NoFunction");
                return true;
            case "unalias":
                // The cmdlet is shared with `alias`; `-u` selects removal (a bare PS alias could not tell them apart).
                result = EmitPassthrough("Invoke-BashAlias", args, cmdletSwitch: " -u");
                return true;
            case "eval":
                result = EmitEval(args);
                return true;
            case "shift":
                result = EmitPassthrough("Invoke-BashShift", args);
                return true;
            case "getopts":
                result = EmitPassthrough("Invoke-BashGetopts", args);
                return true;
            case "flock":
                result = EmitPassthrough("Invoke-BashFlock", args);
                return true;
            case "timeout":
                result = EmitPassthrough("Invoke-BashTimeout", args);
                return true;
            case "times":
                result = EmitPassthrough("Invoke-BashTimes", args);
                return true;
            case "history":
                result = EmitPassthrough("Invoke-BashHistory", args);
                return true;
            case "hash":
                result = EmitPassthrough("Invoke-BashHash", args);
                return true;
            case "umask":
                result = EmitPassthrough("Invoke-BashUmask", args);
                return true;
            case "ulimit":
                result = EmitPassthrough("Invoke-BashUlimit", args);
                return true;
            case "compgen":
                result = EmitPassthrough("Invoke-BashCompgen", args);
                return true;
            case "complete":
                result = EmitPassthrough("Invoke-BashComplete", args);
                return true;
            case "compopt":
                result = EmitPassthrough("Invoke-BashCompopt", args);
                return true;
            case "realpath":
                result = EmitPassthrough("Invoke-BashRealpath", args);
                return true;
            case "command":
                result = EmitPassthrough("Invoke-BashCommand", args);
                return true;
            case "declare":
            case "typeset":
                // Reached only when no static declaration path took it (e.g. `typeset -a "$code"`): the runtime parses the words.
                result = EmitPassthrough("Invoke-BashDeclare", args);
                return true;
            case "builtin":
            {
                // Runs the named builtin, skipping any shell function of that name: the rest is emitted as
                // an ordinary command with that one function hidden.
                var rest = args;
                if (!rest.IsEmpty && GetLiteralValue(rest[0]) == "--") rest = rest.RemoveAt(0);
                if (rest.IsEmpty) { result = PsBuild.SetStatus(0); return true; }
                var priorFns = _definedFunctions;
                if (priorFns is not null && GetLiteralValue(rest[0]) is { } bn && priorFns.Contains(bn))
                {
                    _definedFunctions = new HashSet<string>(priorFns, StringComparer.Ordinal);
                    _definedFunctions.Remove(bn);
                }
                try { result = EmitSimple(cmd with { Words = rest }); }
                finally { _definedFunctions = priorFns; }
                return true;
            }
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
                // `let "a = 3 * 4" b++` is `(( a = 3 * 4 )); (( b++ ))`: arithmetic evaluation with the full
                // grammar, assigning the shell's own variables. The cmdlet wrote PowerShell variables the
                // emitted `$env:a` never read. Dynamic or unparseable operands keep the cmdlet.
                result = TryEmitLetAsArithmetic(args) ?? EmitPassthrough("Invoke-BashLet", args);
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

    /// <summary>The name operand of `[[ -v name ]]`: literal text, where `[..]` (a subscript) arrives as a glob part.</summary>
    private static string? TryGetVarOperandName(CompoundWord word)
    {
        var sb = new StringBuilder();
        foreach (var part in word.Parts)
        {
            if (part is WordPart.GlobPart g) sb.Append(g.Pattern);
            else if (TryGetStaticArgValue(new CompoundWord(ImmutableArray.Create(part))) is { } s) sb.Append(s);
            else return null;
        }
        return sb.ToString();
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
    // internal, not private: also the fused lane's compile-time-static arg test.
    internal static string? TryGetStaticArgValue(CompoundWord word)
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

    /// <summary>A command word that is just a variable expansion (`$c`, `${c}`, `"$c"`, `"$@"`, `"${a[@]}"`): the name is
    /// only known at run time. A composed word (`$dir/$name`, `"$HOME/bin/x"`) is a path and stays a plain call.</summary>
    private static bool IsBareVariableCommandWord(CompoundWord word)
    {
        if (word.Parts.Length != 1) return false;
        var part = word.Parts[0] is WordPart.DoubleQuoted { Parts.Length: 1 } dq ? dq.Parts[0] : word.Parts[0];
        return part is WordPart.SimpleVarSub or WordPart.BracedVarSub;
    }

    /// <summary>
    /// True when the command word does not emit as a bare PowerShell command NAME
    /// and therefore needs the <c>&amp;</c> call operator: it is quoted, or it
    /// contains any expansion (variable, command/arith substitution, tilde).
    /// <para>
    /// Generalizes <see cref="IsQuotedCommandWord"/> / <see cref="IsVariableCommandWord"/>,
    /// which both required a SINGLE part and so missed every composed word —
    /// <c>$gobin/go</c>, <c>~/bin/foo</c>, <c>$dir/$name</c>, <c>"$d"/go</c> all
    /// emitted unparseable PowerShell expressions. A word made only of literals /
    /// escaped literals / globs stays a bare name, as in bash.
    /// </para>
    /// </summary>
    private static bool IsExpandedCommandWord(CompoundWord word)
    {
        foreach (var part in word.Parts)
        {
            if (part is WordPart.SimpleVarSub or WordPart.BracedVarSub
                     or WordPart.CommandSub or WordPart.ArithSub
                     or WordPart.TildeSub
                     or WordPart.SingleQuoted or WordPart.DoubleQuoted)
                return true;
        }
        return false;
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
        // `eval -- body`: `--` ends eval's own options and is not part of the evaluated text.
        if (!args.IsEmpty && GetLiteralValue(args[0]) == "--") args = args.RemoveAt(0);
        else if (!args.IsEmpty && GetLiteralValue(args[0]) is { Length: > 1 } badOpt && badOpt[0] == '-')
            return "Write-Error " + PsBuild.SingleQuote("eval: " + badOpt + ": invalid option") + " -ErrorAction Continue; $global:LASTEXITCODE = 2";
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

        // The args sit in a PowerShell array literal (expression mode); a word with an escaped character
        // (`\$$a`) is command-mode text there, and so is a bare `echo`: each is rendered as one string.
        var emittedArgs = string.Join(", ", args.Select(a =>
            a.Parts.Any(p => p is WordPart.EscapedLiteral)
                ? EmitWord(new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.DoubleQuoted(a.Parts))))
                : FormatForItem(EmitWord(a))));
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
            "$__psbash_eval_pwsh = $null; try { $__psbash_eval_pwsh = [PsBash.Core.Transpiler.BashTranspiler, PsBash.Transpiler]::Transpile($__psbash_eval_src, [PsBash.Core.Transpiler.TranspileContext, PsBash.Transpiler]::Eval"
            + (EvalLocalNamesArg() is { Length: > 0 } evalLocals ? evalLocals : ", $null")
            + ", ($null -ne (Get-Variable -Name __BashStdIn -Scope Global -ValueOnly -ErrorAction SilentlyContinue))) } " +
            // A syntax error in the eval text is eval's own failure (status 2), not an exception out of the surrounding script.
            "catch { Write-Error ('ps-bash: eval: ' + $(if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message })) -ErrorAction Continue; $global:LASTEXITCODE = 2 }; " +
            "if ($null -ne $__psbash_eval_pwsh) { Invoke-Expression $__psbash_eval_pwsh } " +
            "} finally { $global:__BashEvalDepth = $__psbash_eval_depth } " +
            "}";
    }

    /// <summary>`, [string[]]@('a','b')`: the locals of the function this eval runs in (empty at top level).</summary>
    private static string EvalLocalNamesArg() =>
        _functionDepth > 0 && _functionLocalNames is { Count: > 0 } locals
            ? ", [string[]]@(" + string.Join(",", locals.Where(IsValidVarName).Select(PsBuild.SingleQuote)) + ")"
            : "";

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
    /// Bash command names whose cmdlet parses its argv with the shared ORDERED parser
    /// (<c>PsBash.Cmdlets.Args.ArgParser</c>). For these, <see cref="EmitPassthrough"/> quotes
    /// EVERY dash-leading literal word (and <c>--</c>) so the PowerShell binder never sees a
    /// parameter-shaped token: each flag reaches <c>[ValueFromRemainingArguments] Arguments</c>
    /// verbatim and in its original position, with no decoy switch losing the order and no
    /// prefix collision (<c>-i</c>/<c>-e</c>/<c>-p</c>...) crashing or being swallowed.
    /// This generalizes the per-flag sets above (find/echo/xargs), which quote only the
    /// specific collisions. Opt a command in HERE only after its cmdlet is migrated — the
    /// cmdlet must be prepared to receive every flag as a plain string.
    /// </summary>
    internal static readonly IReadOnlySet<string> OrderedArgCommands =
        new HashSet<string>(StringComparer.Ordinal) { "source", "getopts", "flock", "timeout", "times", "history", "hash", "umask", "ulimit", "compgen", "shopt", "complete", "compopt", "tee", "cp", "mv", "rm", "mkdir", "chmod", "rmdir", "ln", "touch", "xargs", "time", "env", "command", "declare", "type", "bash", "sh", "awk", "head", "tail", "wc", "cat", "tac", "nl", "uniq", "fold", "expand", "unexpand", "paste", "join", "comm", "split", "strings", "base64", "stat", "file", "cut", "sort", "grep", "sed", "rg", "find", "echo", "printf", "test", "ls", "du", "tree", "column", "gzip", "tar", "md5sum", "sha1sum", "sha256sum", "diff", "jq", "od" };

    /// <summary><c>Invoke-BashTee</c> -&gt; is <c>tee</c> in <see cref="OrderedArgCommands"/>?</summary>
    private static bool IsOrderedArgCmdlet(string cmdlet) =>
        cmdlet.StartsWith("Invoke-Bash", StringComparison.Ordinal)
        && OrderedArgCommands.Contains(cmdlet.Substring("Invoke-Bash".Length).ToLowerInvariant());

    /// <summary>
    /// The literal text of a dash-leading word that is fully static (bare, quoted or mixed:
    /// <c>-r</c>, <c>--file="a b"</c>), or null when the word is dynamic or not dash-leading.
    /// A dynamic word (<c>-$x</c>) keeps the normal path; an already-quoted whole word
    /// (<c>'-r'</c>) is also returned — re-emitting it single-quoted is equivalent and keeps
    /// one rule for "every dash-leading word".
    /// </summary>
    private static string? OrderedArgDashLiteral(CompoundWord word)
    {
        if (word.Parts.IsEmpty || TryGetStaticArgValue(word) is not { Length: > 0 } value || value[0] != '-')
            return null;
        return value;
    }

    /// <summary>Set while the operands of a pure string-manipulation command are emitted: they are text, not filesystem paths.</summary>
    [ThreadStatic]
    private static bool _operandsAreText;

    private static string EmitPassthrough(
        string cmdlet,
        ImmutableArray<CompoundWord> args,
        IReadOnlySet<string>? forceQuoteFlags = null,
        string cmdletSwitch = "")
    {
        // `dirname /x/y/z` and `basename /a/b` only cut a string: rewriting `/x/y` to `X:\y` (PSBASH_UNIX_PATHS) changed the answer.
        if (cmdlet is not ("Invoke-BashDirname" or "Invoke-BashBasename"))
            return EmitPassthroughCore(cmdlet, args, forceQuoteFlags, cmdletSwitch);
        bool prior = _operandsAreText;
        _operandsAreText = true;
        try { return EmitPassthroughCore(cmdlet, args, forceQuoteFlags, cmdletSwitch); }
        finally { _operandsAreText = prior; }
    }

    private static string EmitPassthroughCore(
        string cmdlet,
        ImmutableArray<CompoundWord> args,
        IReadOnlySet<string>? forceQuoteFlags,
        string cmdletSwitch)
    {
        if (args.IsEmpty)
            return cmdlet + cmdletSwitch;

        var pipelineProcessSubIndex = GetPipelineProcessSubArgIndex(cmdlet, args);

        // Renders operand i as a plain (non-splat) argument: the pipeline
        // process-sub operand goes through EmitProcessSubPipeline, everything
        // else through EmitWord with passthrough quoting applied.
        bool orderedArgs = IsOrderedArgCmdlet(cmdlet);

        string EmitPlainArg(int i)
        {
            // Ordered-parser commands: EVERY dash-leading literal (and `--`) is a single-quoted
            // string, so no flag is ever a PowerShell parameter token. See OrderedArgCommands.
            if (orderedArgs && OrderedArgDashLiteral(args[i]) is { } dashLiteral)
                return PsBuild.SingleQuote(dashLiteral);

            var emitted = i == pipelineProcessSubIndex
                ? EmitProcessSubPipeline((WordPart.ProcessSub)args[i].Parts[0])
                : EmitWord(args[i]);
            // A bare literal with a comma is a PowerShell ARRAY (`xargs -d , echo` -> the
            // parse error "Missing argument"; `a,b` -> @('a','b')). Ordered-parser commands
            // take option VALUES as separate words, so quote those literals as well.
            if (orderedArgs && emitted.Contains(',')
                && emitted.IndexOfAny(['"', '\'', '$', '`', '(', '@']) < 0
                && TryGetStaticArgValue(args[i]) is { } commaLiteral && commaLiteral == emitted)
                return PsBuild.SingleQuote(commaLiteral);
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
                // A dynamic word with quotes (`-"$v"-`): wrapping the emitted text in more quotes splits it
                // into several arguments; build ONE double-quoted string from the parts instead.
                if (emitted.IndexOf('"') >= 0 && args[i].Parts.Length > 1
                    && !args[i].Parts.Any(p => p is WordPart.GlobPart or WordPart.ProcessSub or WordPart.TildeSub
                        or WordPart.BracedTuple or WordPart.BracedRange))
                    return FlattenPartsToDoubleQuotedString(args[i].Parts);
                return $"\"{emitted}\"";
            }
            // A bare `,` literal (sed -n 725,750p) is a PowerShell array separator.
            return QuoteCommaLiteralWord(args[i], emitted);
        }

        // RC-7: when any operand is a pure unquoted ordinary variable, hoist it
        // to a temp variable and PowerShell-splat it so word-splitting and
        // empty-word elision match bash. The pipeline process-sub operand is
        // never a pure variable word, so it is unaffected.
        if (HasUnquotedVarSplatArg(args, pipelineProcessSubIndex))
            return EmitCommandWithSplatArgs(cmdlet + cmdletSwitch, args, EmitPlainArg);

        var sb = new StringBuilder(cmdlet).Append(cmdletSwitch);
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

    /// <summary>
    /// Quote a flag-shaped operand on the GENERAL (unmapped / external command)
    /// emission path when PowerShell would otherwise fail to parse it. Only the
    /// characters that actually break the parse are covered: <c>,</c> (the
    /// PowerShell array separator — <c>cc -Wp,-v</c> emitted "Missing argument in
    /// parameter list") and <c>{</c>/<c>}</c> (script-block delimiters).
    /// <para>
    /// Deliberately NARROWER than <see cref="NeedsPassthroughQuoting"/>, which also
    /// quotes <c>:</c> for mapped <c>Invoke-Bash*</c> cmdlets. <c>-Foo:bar</c> PARSES
    /// fine; quoting it here would change PowerShell's named-parameter binding for a
    /// real cmdlet, which is a behavior change rather than a parse fix.
    /// </para>
    /// </summary>
    private static string QuoteGeneralArgIfNeeded(string emitted)
    {
        if (emitted.Length < 2 || emitted[0] != '-')
            return emitted;
        // An already-quoted or expansion-bearing word is left alone: re-wrapping
        // corrupts it (the `-F","` -> `"-F","` array-split trap).
        if (emitted.IndexOfAny(['"', '\'', '$', '`']) >= 0)
            return emitted;
        return emitted.IndexOfAny([',', '{', '}']) >= 0 ? $"\"{emitted}\"" : emitted;
    }

    /// <summary>An argument word, emitted and comma-quoted (<see cref="QuoteCommaLiteralWord"/>).</summary>
    private static string EmitArgWord(CompoundWord word)
        => QuoteCommaLiteralWord(word, EmitWord(word));

    /// <summary>
    /// Quote a command ARGUMENT word that carries a bare, unquoted <c>,</c> literal.
    /// In PowerShell argument mode <c>a,b</c> is an ARRAY (<c>sed -n 725,750p f</c>
    /// reached <c>Invoke-BashSed</c> as <c>@(725,'750p')</c> — "Cannot convert
    /// 'System.Object[]' to the type 'System.String'"; a native command got the
    /// array flattened wrongly). Bash treats the comma as an ordinary character
    /// (brace expansion <c>{a,b}</c> is a distinct <see cref="WordPart.BracedTuple"/>
    /// and never reaches here).
    /// <para>
    /// A pure-literal word becomes ONE single-quoted string (path transforms
    /// applied first). A word mixing the literal with expansions/quotes
    /// (<c>$x,y</c>, <c>a,"b"</c>) is flattened to one double-quoted string.
    /// Words with a glob / process-sub / brace part are left alone (quoting would
    /// disable globbing). Returns <paramref name="emitted"/> unchanged when no
    /// quoting is needed or it is already a single quoted token.
    /// </para>
    /// </summary>
    private static string QuoteCommaLiteralWord(CompoundWord word, string emitted)
    {
        if (emitted.Length == 0 || emitted[0] is '"' or '\'' or '(' or '@')
            return emitted;

        bool hasCommaLiteral = false;
        bool hasGlob = false;
        foreach (var part in word.Parts)
        {
            switch (part)
            {
                case WordPart.Literal lit:
                    if (lit.Value.Contains(','))
                        hasCommaLiteral = true;
                    break;
                case WordPart.GlobPart:
                    hasGlob = true;
                    break;
                case WordPart.ProcessSub or WordPart.BracedTuple or WordPart.BracedRange:
                    return emitted;
            }
        }
        if (!hasCommaLiteral)
            return emitted;

        if (hasGlob)
        {
            // `ls *.c,x`: the comma is literal inside the glob pattern (bash), but bare it is a
            // PowerShell array. The mapped cmdlets expand glob operands themselves from the
            // literal string, so ONE single-quoted pattern keeps globbing AND the comma. Only
            // the plain literal+glob shape is handled; anything richer (variables, escapes,
            // quotes mixed into a glob) keeps its bare emission.
            if (word.Parts.All(p => p is WordPart.Literal or WordPart.GlobPart))
            {
                var globText = TransformWordPath(emitted);
                if (!globText.StartsWith("$env:TEMP\\", StringComparison.Ordinal))
                    return PsBuild.SingleQuote(globText);
            }
            return emitted;
        }

        if (word.Parts.All(p => p is WordPart.Literal or WordPart.EscapedLiteral)
            && TryGetPureLiteralText(word.Parts, out var text))
        {
            if (text.StartsWith("/tmp/"))
                return PsBuild.TempPath(PsBuild.EscapeForDoubleQuote(text[5..]));
            return PsBuild.SingleQuote(TransformWordPath(text));
        }
        return TransformWordPath(FlattenPartsToDoubleQuotedString(word.Parts));
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
    /// <summary>
    /// The regex for a glob, with every literal parenthesis written as <c>\x28</c> / <c>\x29</c>: the pattern is emitted inside
    /// <c>"$( … )"</c>, whose closing paren PowerShell finds by counting parens even inside quotes (see the naive-scan memory).
    /// </summary>
    private static string GlobToRegex(string glob, bool lazy)
    {
        string re = GlobToRegexCore(glob, lazy);
        if (re.IndexOf("\\(", StringComparison.Ordinal) < 0 && re.IndexOf("\\)", StringComparison.Ordinal) < 0) return re;
        var sb = new StringBuilder(re.Length + 8);
        for (int i = 0; i < re.Length; i++)
        {
            if (re[i] == '\\' && i + 1 < re.Length)
            {
                char n = re[++i];
                sb.Append(n == '(' ? "\\x28" : n == ')' ? "\\x29" : "\\" + n);
            }
            else sb.Append(re[i]);
        }
        return sb.ToString();
    }

    private static string GlobToRegexCore(string glob, bool lazy)
    {
        // Extglob forms (`${v%.@(gz|bz2)}`, `${v##+(a)}`) — the shared converter. Plain globs keep
        // the historical translation below.
        if (BashPattern.HasExtGlob(glob))
            return BashPattern.ToRegex(glob, lazy);

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
                // A bracket expression is nearly valid regex: `[!x]` negates (regex `[^x]`), a leading `]` is
                // literal, and `[:space:]` inside it is a POSIX class (so the first `]` is not always the close).
                int close = -1, j = i + 1;
                if (j < glob.Length && glob[j] is '!' or '^') j++;
                if (j < glob.Length && glob[j] == ']') j++;
                for (; j < glob.Length && close < 0; j++)
                {
                    if (glob[j] == '\\' && j + 1 < glob.Length) j++; // an escaped ] [ - stays inside the bracket
                    else if (glob[j] == '[' && j + 1 < glob.Length && glob[j + 1] == ':' && glob.IndexOf(":]", j + 2, StringComparison.Ordinal) is > 0 and var pe) j = pe + 1;
                    else if (glob[j] == ']') close = j;
                }
                if (close >= 0)
                {
                    string body = glob[(i + 1)..close];
                    if (body.Length > 0 && body[0] is '!' or '^') body = "^" + body[1..];
                    int lead = body.StartsWith('^') ? 1 : 0;
                    if (body.Length > lead && body[lead] == ']') body = body[..lead] + "\\]" + body[(lead + 1)..];
                    sb.Append('[').Append(BashParser.TranslatePosixClassesForRegex(body)).Append(']');
                    i = close + 1;
                }
                else
                {
                    // Unclosed bracket — escape the [ and continue.
                    sb.Append("\\[");
                    i++;
                }
            }
            else if (c == '\\')
            {
                // In a bash pattern `\X` means the LITERAL character X (it strips X's
                // glob meaning) — it does not mean "a backslash then X". Emitting `\\`
                // here made `${value%\'}` compile to the regex `^(.*)\\'$`, which
                // requires an actual backslash before the quote and so never matched:
                // the quote-stripping idiom silently did nothing. Oracle-verified:
                // `value="'hi'"; value="${value%\'}"` yields `'hi`.
                if (i + 1 < glob.Length)
                {
                    sb.Append(System.Text.RegularExpressions.Regex.Escape(
                        glob[i + 1].ToString()));
                    i += 2;
                }
                else
                {
                    // Trailing lone backslash — the literal character itself.
                    sb.Append("\\\\");
                    i++;
                }
            }
            else if ("^$.|+(){}".IndexOf(c) >= 0)
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
