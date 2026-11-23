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
    /// A function's LEXICAL locals in scope: its local arrays (an array lives only as a PowerShell variable) and the
    /// scalar names a dynamic local cannot hold (<see cref="IsDynamicLocalName"/>). They emit as <c>$var</c>. Every
    /// other shell variable, a loop variable included, is read from and written to the shell variable store
    /// (<c>$env:var</c>): one storage per variable (environment-model.md section 8).
    /// </summary>
    [ThreadStatic]
    private static HashSet<string>? _lexicalLocals;

    /// <summary>
    /// Names an enclosing <c>for</c> / <c>for ((;;))</c> / <c>while read</c> binds. Their storage is the shell variable
    /// store like any other variable's (a function called from the body reads its own <c>local</c> or the loop's value
    /// there, never a PowerShell variable of the loop's). The set only keeps two emission choices: an unquoted
    /// <c>$v</c> is not word-split unless the list may hold IFS characters (<see cref="_splitLoopVars"/>). A background
    /// job started in the body gets the value the loop had at launch from its own scalar store, copied at the <c>&amp;</c>.
    /// </summary>
    [ThreadStatic]
    private static HashSet<string>? _storeLoopVars;

    /// <summary>
    /// While a background job's body is emitted: the function's lexical locals it may read, each with the
    /// PowerShell expression of its value at launch. The job (another runspace, or another process) reads its own
    /// copy, <c>$name</c>, which <c>Invoke-BashBackground -Vars</c> sets; <see cref="_jobCaptureUsed"/> collects the
    /// names the body read.
    /// </summary>
    [ThreadStatic]
    private static Dictionary<string, string>? _jobCapture;
    [ThreadStatic]
    private static HashSet<string>? _jobCaptureUsed;

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
    /// <summary>The script mentions <c>failglob</c> or holds a bad substitution: either error abandons the rest of its
    /// input line (<c>echo *.ZZ; echo x</c> prints nothing), so commands that share a line are guarded (see EmitCommandList).</summary>
    [ThreadStatic]
    private static bool _lineAbandonMode;
    /// <summary>The root command of the text being transpiled: its top-level commands are the input lines bash reads,
    /// which a discarding expansion error ends one at a time (PsBuild.DiscardLineScope, EmitCommandList).</summary>
    [ThreadStatic]
    private static Command? _rootCommand;

    /// <summary>The statements after a top-level <c>exec &gt;f</c> (EmitCommandList): top-level commands too, each in its line scope.</summary>
    [ThreadStatic] private static Command.CommandList? _execRestRoot;
    /// <summary><c>bash -i -c</c> (the launcher's psbash_interactive prelude) under <c>set -u</c>: an unbound variable
    /// discards its input line instead of ending the shell, so every top-level command gets the line scope.</summary>
    [ThreadStatic]
    private static bool _discardInteractive;
    /// <summary>The script reads PIPESTATUS, so a plain command records its own status there.</summary>
    [ThreadStatic]
    private static bool _pipeStatusMode;
    /// <summary>The script can turn xtrace on: a statement-level command is preceded by a runtime-checked trace line.</summary>
    [ThreadStatic]
    private static bool _xtraceMode;
    /// <summary>The script mentions <c>trap</c>: a subshell or <c>$( )</c> may run a function that sets an EXIT trap.</summary>
    [ThreadStatic]
    private static bool _trapMode;
    // Text that sets a DEBUG trap and runs other commands: every top-level simple command first runs it (bash semantics).
    // A lone `trap 'cmd' DEBUG` (an interactive prompt hook) keeps its directory-change mapping (SetsDebugTrapForOtherCommands).
    [ThreadStatic]
    private static bool _debugTrapMode;
    /// <summary>
    /// The script reads attributes at run time: <c>declare -p</c>, or <c>${v@a}</c> / <c>${v@A}</c> (whose v may be a nameref
    /// the run time resolves). Then <c>export</c>, <c>declare -i</c> and <c>-l</c>/<c>-u</c> record their letter in the one
    /// run-time table (<c>BashShellState.MarkAttribute</c>); otherwise only the emitter's own tables hold them, which keeps
    /// the common script's declarations free of the call. REGRESSION: <c>declare -i ti=3; declare -n ri; ri=$w; ${ri@a}</c>
    /// printed nothing, as the -i lived only in the compile-time table.
    /// </summary>
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

    /// <summary>
    /// Set by <c>trap</c> while it transpiles an action for this signal (ERR, DEBUG). bash runs the action's commands
    /// like any others: a failing one runs the ERR trap and errexit (except inside the ERR action itself), and the DEBUG
    /// trap runs before each of them (except inside the DEBUG action). So the action text gets the statement checks and,
    /// for a signal other than DEBUG, the DEBUG leads, whether or not it mentions <c>set -e</c> or <c>trap</c>.
    /// </summary>
    [ThreadStatic]
    public static string? TrapActionSignal;

    /// <summary><see cref="TrapActionSignal"/> for a real signal's action (USR1, TERM, ...): ERR checks and DEBUG leads as in a
    /// script, but its commands keep their own line numbers (bash reports line 1 of the action).</summary>
    public const string RealSignalTrapAction = "SIGNAL";
    [ThreadStatic]
    private static HashSet<string>? _collectScalarLocals;

    private static bool IsDynamicLocalName(string name)
        => name != "IFS" && PsBuild.TryMapSpecialVar(name, braced: false, inDoubleQuote: false) is null
           && !(_lexicalLocals?.Contains(name) ?? false);
    private const string EnvShadowPrefix = "__psbash_ea_";

    private static string? EnvShadowName(string name)
        => _envPrefixShadow is not null && _envPrefixShadow.Contains(name) ? EnvShadowPrefix + name : null;

    private static T WithEnvShadow<T>(ImmutableArray<EnvPair> pairs, Func<T> emit)
    {
        var prior = _envPrefixShadow;
        var set = prior is null ? new HashSet<string>(VarNameKeys) : new HashSet<string>(prior, VarNameKeys);
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

    /// <summary>
    /// Nonzero while emitting the body of a compound command whose stderr bash merges into its stdout
    /// (<c>{ ...; } 2&gt;&amp;1</c>, <c>( ... ) &amp;&gt;f</c>, a compound stage before <c>|&amp;</c>): a program inside runs through
    /// <c>Invoke-BashNative</c> (<see cref="NativeMergeRunner"/>), which gives it one pipe for stdout and stderr when the
    /// merge holds at run time.
    /// </summary>
    [ThreadStatic]
    private static int _stderrMergeDepth;

    /// <summary>Names declared <c>declare -i</c> in the script being emitted: later plain assignments evaluate arithmetically.</summary>
    [ThreadStatic]
    private static HashSet<string>? _intVars;

    /// <summary>Names declared <c>declare -A</c> in the script being emitted: a later <c>a=(k v ...)</c> is key/value pairs (bash 5.1+).</summary>
    [ThreadStatic]
    private static HashSet<string>? _assocVars;
    // Names assigned an array/assoc literal earlier in this unit: a bare `$a` / `${a}` then reads element 0, as in bash.
    // Per thread like the rest of the emitter state: concurrent transpiles (the test host) saw each other's names.
    [ThreadStatic]
    private static HashSet<string>? _arrayVars;

    /// <summary>Names declared <c>declare -l</c> (false) / <c>declare -u</c> (true): later assignments are case-converted.</summary>
    [ThreadStatic]
    private static Dictionary<string, bool>? _caseVars;

    /// <summary>The names the function being emitted has declared local so far: a later <c>local n</c> keeps their attributes.</summary>
    [ThreadStatic]
    private static HashSet<string>? _localAttrNames;

    /// <summary>
    /// Attributes a <c>declare -g</c> in a function set (letter i, l or u): they outlive the function, whose own local
    /// attribute changes are undone when its body ends (EmitFunction).
    /// </summary>
    [ThreadStatic]
    private static List<(string Name, char Letter)>? _globalAttrsInFunctions;

    /// <summary>
    /// A local declaration of <paramref name="name"/> in a function: the compile-time attribute sets follow it for the rest
    /// of the body. Its own <paramref name="attrs"/> (letters of "ilu") apply; the first declaration in this function
    /// without them hides the caller's (bash: <c>declare -i n; f() { local n; n=2+2; }</c> stores "2+2"), and a later one
    /// keeps what the local has. EmitFunction puts the sets back when the body ends.
    /// </summary>
    private static void ScopeLocalAttributes(string name, string? attrs)
    {
        bool first = (_localAttrNames ??= new HashSet<string>(VarNameKeys)).Add(name);
        if (_intVars is not null)
        {
            if (attrs?.Contains('i') == true) _intVars.Add(name);
            else if (first) _intVars.Remove(name);
        }
        if (_caseVars is not null)
        {
            if (attrs?.Contains('l') == true) _caseVars[name] = false;
            else if (attrs?.Contains('u') == true) _caseVars[name] = true;
            else if (first) _caseVars.Remove(name);
        }
    }

    /// <summary>Records an attribute a declaration outside the local path set while a function is being emitted (see <see cref="_globalAttrsInFunctions"/>).</summary>
    private static void NoteGlobalAttr(string name, char letter)
    {
        if (_functionDepth > 0) (_globalAttrsInFunctions ??= new()).Add((name, letter));
    }

    /// <summary>A <c>declare -l/-u</c> variable converts the value it is assigned.</summary>
    private static string CaseAttr(string name, string valueExpr) =>
        _caseVars is not null && _caseVars.TryGetValue(name, out bool upper)
            ? "([string](" + valueExpr + "))." + (upper ? "ToUpperInvariant()" : "ToLowerInvariant()")
            : valueExpr;
    // Names a literal `readonly NAME` made read-only earlier in this script: a later plain assignment is refused.
    [ThreadStatic]
    private static HashSet<string>? _readonlyVars;

    /// <summary>The emitter's per-shell attribute tables as a subshell body found them (<see cref="EnterSubshellTables"/>).</summary>
    private readonly record struct SubshellTables(HashSet<string>? Readonly, Dictionary<string, string>? Namerefs,
        HashSet<string>? PendingNamerefs, HashSet<string>? RuntimeNamerefs, Dictionary<string, string>? LitAssigned,
        HashSet<string>? IntVars, Dictionary<string, bool>? CaseVars, HashSet<string>? AssocVars, int FunctionDepth);

    /// <summary>
    /// A subshell body starts (<c>( )</c>, <c>$( )</c>, <c>&lt;( )</c>, <c>cmd &amp;</c>, a pipe stage): it gets a copy of
    /// every compile-time per-shell table (readonly names, namerefs and pending ones, literal values a <c>typeset -n</c>
    /// reads, <c>-i</c>, <c>-l</c>/<c>-u</c>, <c>-A</c>), and <see cref="LeaveSubshellTables"/> puts back what this returns
    /// when it ends: what a subshell declares ends with it (bash forks; at run time BashShellState.ShellTables does the
    /// same). A new such table goes here. REGRESSION: <c>( readonly ro=1 ); ro=2</c> refused the assignment, and
    /// <c>( declare -n r=y ); r=3</c> wrote y.
    /// </summary>
    private static SubshellTables EnterSubshellTables()
    {
        var outer = new SubshellTables(_readonlyVars, _namerefs, _pendingNamerefs, _runtimeNamerefs, _litAssigned, _intVars, _caseVars, _assocVars, _subshellFunctionDepth);
        _subshellFunctionDepth = _functionDepth;
        if (_readonlyVars is { } ro) _readonlyVars = new HashSet<string>(ro, VarNameKeys);
        if (_namerefs is { } refs) _namerefs = new Dictionary<string, string>(refs, VarNameKeys);
        if (_pendingNamerefs is { } pending) _pendingNamerefs = new HashSet<string>(pending, VarNameKeys);
        if (_runtimeNamerefs is { } runtimeRefs) _runtimeNamerefs = new HashSet<string>(runtimeRefs, VarNameKeys);
        if (_litAssigned is { } lit) _litAssigned = new Dictionary<string, string>(lit, VarNameKeys);
        if (_intVars is { } ints) _intVars = new HashSet<string>(ints, VarNameKeys);
        if (_caseVars is { } cases) _caseVars = new Dictionary<string, bool>(cases, VarNameKeys);
        if (_assocVars is { } assoc) _assocVars = new HashSet<string>(assoc, VarNameKeys);
        return outer;
    }

    /// <summary>
    /// <see cref="_functionDepth"/> when the innermost subshell body started (0 outside one): a function body deeper than
    /// that was defined inside the subshell, so its array writes go to the subshell's array, not to one of its own.
    /// </summary>
    [ThreadStatic] private static int _subshellFunctionDepth;

    /// <summary>The subshell body ended: the tables are the ones <see cref="EnterSubshellTables"/> found.</summary>
    private static void LeaveSubshellTables(SubshellTables outer)
    {
        _readonlyVars = outer.Readonly; _namerefs = outer.Namerefs; _pendingNamerefs = outer.PendingNamerefs; _runtimeNamerefs = outer.RuntimeNamerefs;
        _litAssigned = outer.LitAssigned; _intVars = outer.IntVars; _caseVars = outer.CaseVars; _assocVars = outer.AssocVars; _subshellFunctionDepth = outer.FunctionDepth;
    }
    /// <summary>True when the script text reads `$?` somewhere: only then does a bare assignment need to reset the status to 0.</summary>
    [ThreadStatic] private static bool _scriptReadsStatus;
    [ThreadStatic] private static bool _scriptReadsLastArg;
    /// <summary>Set when an assignment to a readonly variable was emitted: bash drops the rest of that input line.</summary>
    [ThreadStatic] private static bool _readonlyViolation;
    /// <summary>
    /// Set once the script makes a name read-only that <see cref="_readonlyVars"/> cannot follow: from a run-time word
    /// (`readonly $names`) or inside a function (whose static set ends with it). Later plain assignments then also ask
    /// the run-time set (<see cref="PsBuild.RuntimeReadonlyGuard"/>).
    /// </summary>
    [ThreadStatic] private static bool _runtimeReadonly;
    [ThreadStatic] private static bool _scriptPosix;
    // The command or function definition EmitPosixSpecialBuiltin / EmitFunction is emitting both forms of (no re-split).
    [ThreadStatic] private static object? _posixSplitNode;
    /// <summary>The script mentions <c>inherit_errexit</c>: command substitutions under errexit get checks (EmitCapturedCore).</summary>
    [ThreadStatic] private static bool _inheritErrexitMode;
    [ThreadStatic] private static bool _scriptLastpipe;

    /// <summary><c>declare -n ref=target</c> in the script being emitted: <c>ref</c> reads and writes <c>target</c>.
    /// Static only (a literal target, resolved at emit time); a nameref set at run time is not modelled.</summary>
    [ThreadStatic]
    private static Dictionary<string, string>? _namerefs;

    /// <summary>
    /// The comparer of EVERY table keyed by variable name (<see cref="_readonlyVars"/>, <see cref="_namerefs"/>, a
    /// function's locals, attributes, ...): equal by store name (<see cref="BashNameCase.StoreNameComparer"/>). A name from
    /// an AST record is already a store name (<c>__psbash_cv_0_TMP</c>), one from word text (<c>unset tmp</c>,
    /// <c>local -p path</c>, a nameref target) is not; both find the same entry. Guard: <c>BashNameCaseTests</c>.
    /// </summary>
    private static readonly IEqualityComparer<string> VarNameKeys = BashNameCase.StoreNameComparer;

    /// <summary>Names assigned a literal string so far (`ref=x`): `typeset -n ref` later turns that string into the target.</summary>
    [ThreadStatic]
    private static Dictionary<string, string>? _litAssigned;

    /// <summary>Namerefs declared without a target (`typeset -n ref`): the next literal assignment names it.</summary>
    [ThreadStatic]
    private static HashSet<string>? _pendingNamerefs;

    /// <summary>
    /// Namerefs whose target is known only at run time: one declared with no target, then written with a value the
    /// transpiler cannot see (`r=$1`, `r=$(cmd)`), or a `for` variable that is a nameref (each word re-targets it). Their
    /// reads and writes go through the run-time table (PsBuild.ReadThroughNameref / AssignThroughNameref), where
    /// BashShellState.WriteTarget names the target on the first write, for every writer alike.
    /// </summary>
    [ThreadStatic]
    private static HashSet<string>? _runtimeNamerefs;

    /// <summary>A nameref the run time resolves: target known only then, or none yet (pending).</summary>
    private static bool IsRuntimeNameref(string name) =>
        (_runtimeNamerefs?.Contains(name) == true || _pendingNamerefs?.Contains(name) == true) && _namerefs?.ContainsKey(name) != true;

    /// <summary>A nameref of any kind: target known now, known only at run time, or none yet.</summary>
    private static bool IsAnyNameref(string name) =>
        _namerefs?.ContainsKey(name) == true || _pendingNamerefs?.Contains(name) == true || _runtimeNamerefs?.Contains(name) == true;

    /// <summary>`a[2]` / `a[i+1]`: a nameref target that names one array element.</summary>
    private static bool IsNamerefElementTarget(string target) =>
        target.Length > 3 && target[^1] == ']' && target.IndexOf('[') is var lb and > 0 && IsValidVarName(target[..lb]);

    private static string ResolveNameref(string name)
    {
        // A target taken from word text (`declare -n ref=path`) is a bash name: the variable is its store name (BashNameCase).
        for (int hops = 0; hops < 8 && _namerefs is not null && _namerefs.TryGetValue(name, out var target); hops++)
            name = BashNameCase.ToStore(target);
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
    /// A SIMPLE command whose stdin is not the scope's or the pipe's: an input redirection (<see cref="HasInputRedirect"/>)
    /// or <c>&lt;&amp;N</c> on a descriptor opened by <c>exec</c> (N &gt;= 3, <see cref="TryEmitFdRedirect"/>: its stdin is
    /// <c>Read-BashFd N</c>). Fed the scope's queue or the stage's pipe as well, <c>Read-BashFd</c> could not bind it
    /// (<c>{ exec 6&lt;&amp;0; cat &lt;&amp;6; }</c>, <c>echo x | cat &lt;&amp;6</c>), and a feed would have drained the stdin a later
    /// command reads. Every "does this simple command read the inherited stdin" site asks this.
    /// </summary>
    private static bool HasOwnStdin(ImmutableArray<Redirect> redirects) =>
        HasInputRedirect(redirects)
        || !redirects.IsDefaultOrEmpty && redirects.Any(r => r.Fd == 0 && r.Op == "<&" && r.FdVar is null
            && GetLiteralValue(r.Target) is { } t && IsAllDigits(t) && int.TryParse(t, out int n) && n >= 3);

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

    private static string IterVar(int depth) => depth == 0 ? "$__psbash_iter" : $"$__psbash_iter{depth}";

    private static string IterGuardPrefix(int depth) => IterVar(depth) + " = 0; ";

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

    /// <summary>The script being transpiled has a <c>set -e</c> word (<see cref="SetsErrexit"/>): with only an ERR trap,
    /// <see cref="_errexitMode"/> is on but a pipe stage never inherits an errexit to end it.</summary>
    [ThreadStatic]
    private static bool _errexitSetMode;

    /// <summary>
    /// Non-zero while emitting text that is errexit-EXEMPT in bash: a condition, a non-final
    /// <c>&amp;&amp;</c>/<c>||</c> operand, a negated or multi-stage pipeline, a command or process
    /// substitution, a background job. No errexit check is emitted inside one.
    /// </summary>
    [ThreadStatic]
    private static int _errexitExemptDepth;

    /// <summary>Nesting of negated statements being emitted: names each one's run-time suppression mark.</summary>
    [ThreadStatic]
    private static int _negatedDepth;

    /// <summary>Set while a negated statement emits its pipeline: that pipeline is suppressed at run time, not here.</summary>
    [ThreadStatic]
    private static bool _negationGuardedAtRuntime;

    /// <summary>The script sets an ERR trap and errtrace: the trap runs inside `` too (EmitCapturedCore).</summary>
    [ThreadStatic]
    private static bool _errtraceMode;

    /// <summary>
    /// What a node's line is relative to: a `` body is parsed from its own text (line 1 = the line it starts on), so
    /// inside one this is that line minus 1. The ERR trap's `` is `_lineBase + Line`.
    /// </summary>
    [ThreadStatic]
    private static int _lineBase;

    internal static bool EnablesErrtrace(string bash) =>
        bash.Contains("trap", StringComparison.Ordinal) && bash.Contains("ERR", StringComparison.Ordinal)
        && (bash.Contains("errtrace", StringComparison.Ordinal) || ErrtraceFlagPattern.IsMatch(bash));

    private static readonly System.Text.RegularExpressions.Regex ErrtraceFlagPattern = new(@"(?<![\w-])[-]\w*E",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex SetErrexitPattern = new(
        // `set`, then any words of the SAME command (no ; & | ( ) newline), then -…e… or -o errexit.
        @"\bset[ \t]+(?:[^\s;&|()]+[ \t]+)*?(?:-[A-Za-z]*e[A-Za-z]*\b|-o[ \t]+errexit\b)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="bash"/> can turn errexit on (a <c>set -e</c>-style word).</summary>
    internal static bool EnablesErrexit(string bash) =>
        SetsErrexit(bash)
        // `trap ... ERR` needs the same statement-position checks (and exempt contexts) to know when a command failed.
        || (bash.Contains("trap", StringComparison.Ordinal) && bash.Contains("ERR", StringComparison.Ordinal));

    /// <summary>True when <paramref name="bash"/> has a <c>set -e</c>-style word (an ERR trap alone does not count).</summary>
    private static bool SetsErrexit(string bash) =>
        // This is a conservative emission prefilter, not a parser. Quote/escape
        // spelling must not hide static flags such as set '-e' or set -o 'errexit'.
        // Extra checks are inert until the parsed set command enables the runtime flag.
        bash.Contains("set", StringComparison.Ordinal) &&
        SetErrexitPattern.IsMatch(bash.Replace("'", "").Replace("\"", "").Replace("\\", ""));

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
        _subshellDepth > 0 ? LeavingReturn("return") : ShellExit("$global:LASTEXITCODE");

    /// <summary>
    /// Code that may run inside a subshell the emitter cannot see: a function body is emitted once and called from
    /// anywhere, including <c>f | cat</c>, <c>(f)</c> and <c>$(f)</c>, where bash's <c>exit</c> ends only that subshell.
    /// So is eval/source/trap text (<see cref="TranspileContext.Eval"/>): <c>( source ./s.sh )</c>,
    /// <c>eval 'exit 3' | cat</c> and <c>( trap 'exit 3' EXIT; ... )</c> end only their subshell, where a plain
    /// <c>exit</c> in the text ended the whole script.
    /// </summary>
    private static bool ExitDecidedAtRunTime => _subshellDepth == 0 && (_functionDepth > 0 || _context == TranspileContext.Eval);

    /// <summary>The statement that ends the shell with <paramref name="status"/> (<see cref="PsBuild.DynamicExit"/> in a function body).</summary>
    private static string ShellExit(string status) =>
        ExitDecidedAtRunTime ? PsBuild.DynamicExit(status) : "exit " + status;

    /// <summary>
    /// A fatal shell error ends the shell with <paramref name="status"/>: inside <c>( )</c> only that subshell, as
    /// <c>exit</c> does there. REGRESSION: a POSIX-mode `export() { :; }` in a subshell ran a bare <c>exit 2</c> and ended
    /// the whole script.
    /// </summary>
    private static string EndShell(string status) =>
        "$global:LASTEXITCODE = " + status + "; " + (_subshellDepth > 0 ? LeavingReturn("return") : ShellExit("$global:LASTEXITCODE"));

    /// <summary>`readonly r=1; r=2`: the assignment is refused with status 1, which errexit acts on.</summary>
    private static bool WritesReadonly(Command.ShAssignment assign)
    {
        if (assign.IsReadonly || assign.IsDeclare || _readonlyVars is null) return false;
        foreach (var p in assign.Pairs)
        {
            string n = ResolveNameref(p.Name);
            if (_readonlyVars.Contains(n.Contains('[') ? n[..n.IndexOf('[')] : n)) return true;
        }
        return false;
    }

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
        // `r=return; $r 99`: a command named at run time may turn out to be `return` (Resolve-BashCommand), which can
        // only raise the eval-return flag; the function (or eval text) it ran in returns here, as after an eval.
        if (cmd is Command.Simple && _subshellDepth == 0 && (_functionDepth > 0 || _context == TranspileContext.Eval)
            && text.Contains("Resolve-BashCommand", StringComparison.Ordinal))
            text += "; " + LeavingReturn(PsBuild.EvalReturnCheck(inFunction: _functionDepth > 0));
        // A script that can set a trap runs a signal sent to the shell (ShellSignals) between statements, as bash runs a
        // trap between commands. A subshell is no target: bash's subshell is another process with its traps reset.
        if (_canSetTrap && _subshellDepth == 0)
            text += "; " + PsBuild.SignalTrapPoll;
        return text;
    }

    /// <summary>
    /// <paramref name="cmd"/> emitted at statement position. A plain command is a one-stage pipeline:
    /// `false; echo ${PIPESTATUS[*]}` is `1`, not the last real pipeline's. The status is recorded before
    /// the ERR trap / errexit check that follows, so the trap sees it.
    /// </summary>
    private static string EmitRecordingStatus(Command cmd)
    {
        string text = Emit(cmd);
        // An assignment statement is a command too: `x=1; echo ${PIPESTATUS[*]}` is `0` (and `x=$(false)` is 1).
        if (_pipeStatusMode && cmd is Command.ShAssignment { IsLocal: false, IsExport: false, IsDeclare: false } assign)
            return text + (ContainsCommandSub(assign) ? "; $global:PIPESTATUS = @([int]$global:LASTEXITCODE)" : "; $global:PIPESTATUS = @(0)");
        return _pipeStatusMode && cmd is Command.Simple { Words.Length: > 0 }
            ? text + "; $global:PIPESTATUS = @([int]$global:LASTEXITCODE)"
            : text;
    }

    /// <summary>
    /// The <c>+ cmd args</c> line <c>set -x</c> prints before a command, as a call that checks the flag at run time.
    /// bash traces the words AFTER expansion -- the words the command runs with -- so each word is passed as its fields
    /// (<see cref="XtraceFields"/>): <c>$unset -c x</c> traces <c>+ -c x</c>, a split <c>$x</c> each field, an empty
    /// <c>"$@"</c> nothing. Only words that expand without running anything are traced (<see cref="XtracePure"/>): a
    /// command substitution, an assigning or failing expansion or a glob would run twice, so such a command is untraced.
    /// </summary>
    private static string? TryEmitXtrace(Command cmd)
    {
        var parts = new List<string>();
        switch (cmd)
        {
            case Command.Simple { Words.Length: > 0, EnvPairs.IsEmpty: true } simple:
                foreach (var w in simple.Words)
                    if (XtraceFields(w) is { } e) parts.Add(e); else return null;
                break;
            case Command.ShAssignment { IsLocal: false, IsExport: false } assign:
                foreach (var pair in assign.Pairs)
                {
                    if (pair.Op != AssignOp.Equal || pair.ArrayValue is not null) return null;
                    if (pair.Value is null) { parts.Add(PsBuild.XtraceAssignment(pair.Name, "''")); continue; }
                    // An assignment's value is never split: one field.
                    if (!XtracePure(pair.Value.Parts) || XtraceValue(pair.Value) is not { } v) return null;
                    parts.Add(PsBuild.XtraceAssignment(pair.Name, v));
                }
                break;
            default:
                return null;
        }
        return "Write-BashXtrace " + string.Join(" ", parts);
    }

    /// <summary>
    /// A word's FIELDS for the trace, from the same expansion the command runs with: a word the command splits
    /// (<see cref="IsUnquotedSplitWord"/>) is the array <see cref="EmitUnquotedSplitWordArray"/> builds -- the one
    /// <see cref="EmitCommandWithSplatArgs"/> splats -- so a word that expands to nothing traces as nothing; any other
    /// word is one field. Null when the word cannot be expanded twice safely.
    /// </summary>
    private static string? XtraceFields(CompoundWord word)
    {
        if (!XtracePure(word.Parts)) return null;
        if (TryGetStaticArgValue(word) is { } literal) return PsBuild.SingleQuote(literal);
        return IsUnquotedSplitWord(word) ? "(" + EmitUnquotedSplitWordArray(word) + ")" : XtraceValue(word);
    }

    /// <summary>A word as ONE field (its emitted string value), or null when it has no plain value expression.</summary>
    private static string? XtraceValue(CompoundWord word)
    {
        if (TryGetStaticArgValue(word) is { } literal) return PsBuild.SingleQuote(literal);
        var emitted = EmitWord(word);
        return emitted.Length > 0 && (emitted[0] == (char)34 || emitted[0] == (char)36) ? "([string](" + emitted + "))" : null;
    }

    /// <summary>
    /// Word parts that expand the same every time and run nothing: text, quotes, tilde, brace expansion, plain variables
    /// (not <c>$RANDOM</c>, which changes per read) and <c>${name}</c> / <c>${name[@]}</c> / <c>${name[N]}</c>. Not a
    /// command or arithmetic substitution, an operator expansion (<c>${x:=v}</c> assigns, <c>${x:?}</c> fails) or a glob.
    /// </summary>
    private static bool XtracePure(System.Collections.Immutable.ImmutableArray<WordPart> parts)
    {
        foreach (var part in parts)
        {
            switch (part)
            {
                case WordPart.Literal or WordPart.EscapedLiteral or WordPart.SingleQuoted or WordPart.AnsiCQuoted
                    or WordPart.TildeSub or WordPart.BracedTuple or WordPart.BracedRange:
                    break;
                case WordPart.SimpleVarSub { Name: not ("RANDOM" or "SRANDOM") }:
                    break;
                case WordPart.DoubleQuoted dq when XtracePure(dq.Parts):
                    break;
                case WordPart.BracedVarSub { Name: not ("RANDOM" or "SRANDOM"), Suffix: var s, ArgWord: null }
                    when s is null or "[@]" or "[*]" || s.Length > 2 && s[0] == '[' && s[^1] == ']' && s[1..^1].All(char.IsAsciiDigit):
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    /// <summary>The ERR trap line of the statement before the one being emitted (the last command it ran).</summary>
    [ThreadStatic]
    private static int _priorTrapLine;

    private static string EmitStatementCore(Command cmd)
    {
        int outerRedirectLine = _redirectLine;
        // The outermost statement of the script sets the line (its end line); one inside it keeps it.
        if (outerRedirectLine == 0 && _functionDepth == 0) _redirectLine = StatementEndLine(cmd);
        try
        {
            string record = RecordLineFor(cmd, statement: true, out int lateLine);
            // `for` and `case` set bash's line to their own as they run their body (after their own redirects opened).
            if (cmd is Command.ForIn { Line: > 0 } or Command.Case { Line: > 0 })
                _redirectLine = cmd is Command.ForIn f ? f.Line : ((Command.Case)cmd).Line;
            string body = EmitStatementCoreBody(cmd);
            return LateLineRecord(record, lateLine, body) + body;
        }
        finally { _redirectLine = outerRedirectLine; }
    }

    /// <summary>
    /// The line bash names in a redirect error of a compound command that sets no line of its own (<c>{ }</c>,
    /// <c>while</c>, <c>if</c>, <c>( )</c>, <c>for (( ))</c>, a <c>for</c>'s own redirect): the line the outermost
    /// statement being run ends on, or the line of the <c>for</c> / <c>case</c> whose body is running. 0 = unknown (a
    /// function body, a substitution, eval text), where the compound's first line stands in (<see cref="CompoundStartLine"/>).
    /// Relative to <see cref="_lineBase"/>. docs/specs/error-handling.md "Redirect open errors".
    /// </summary>
    [ThreadStatic]
    private static int _redirectLine;

    /// <summary>The line <paramref name="cmd"/> ends on (its redirects and here-document bodies included); 0 when unknown.</summary>
    private static int StatementEndLine(Command cmd) => cmd switch
    {
        Command.If c => c.EndLine,
        Command.ForIn c => c.EndLine,
        Command.Select c => c.EndLine,
        Command.ForArith c => c.EndLine,
        Command.While c => c.EndLine,
        Command.Case c => c.EndLine,
        Command.Subshell c => c.EndLine,
        Command.BraceGroup c => c.EndLine,
        Command.AndOrList a when a.Commands.Length > 0 => StatementEndLine(a.Commands[^1]),
        Command.Pipeline p when p.Commands.Length > 0 => StatementEndLine(p.Commands[^1]),
        _ => LastSimpleLine(cmd),
    };

    /// <summary>
    /// The <see cref="PsBuild.SetCallLine"/> that goes before <paramref name="cmd"/> at statement position -- a statement,
    /// or a hoisted <c>if</c>/<c>elif</c>/<c>while</c>/<c>until</c> condition (<see cref="EmitHoistedCondition"/>) -- or "".
    /// A script that reads BASH_LINENO records each statement's line: a function it calls reports it. A command that may
    /// name a missing command records it too: bash's <c>script.sh: line N: x: command not found</c> shows it
    /// (<see cref="PsBuild.DiagnosticPrefix"/>). A trap action keeps the line of the command the trap fired for. Sets
    /// <see cref="_statementLine"/> (a static <c>eval</c>'s text counts its lines from it).
    /// </summary>
    private static string RecordLineFor(Command cmd, bool statement) => RecordLineFor(cmd, statement, out _);

    /// <summary>
    /// <see cref="RecordLineFor(Command, bool)"/>, and when it records nothing though the line could be recorded,
    /// <paramref name="lateLine"/> = that line, for <see cref="LateLineRecord"/> to decide from the emitted text.
    /// </summary>
    private static string RecordLineFor(Command cmd, bool statement, out int lateLine)
    {
        _statementLine = (cmd is Command.Simple { Line: > 0 } ls ? SimpleCommandLine(ls) : FirstSimpleLine(cmd)) is > 0 and var line
            ? _lineBase + line
            // `{ ...; } > nodir/f`, `done < missing`: the redirect's open error names a line too.
            : HasOpenableRedirect(CompoundRedirects(cmd)) && (_redirectLine > 0 ? _redirectLine : CompoundStartLine(cmd)) is > 0 and var start ? _lineBase + start : 0;
        // A loop that reaches past line 1 runs its line-1 statements again after later lines recorded theirs.
        if (statement && _functionDepth == 0 && _loopDepth == 0) _loopReachesPastLine1 = RunsPastLine1(cmd);
        bool lineAlreadyRecorded = _statementLine == 1 && _scriptRoot && _functionDepth == 0 && (_loopDepth == 0 || !_loopReachesPastLine1);
        bool eligible = !PsBuild.InTrapAction && !lineAlreadyRecorded && _statementLine > 0;
        bool now = (_bashLinenoMode || (eligible && (MayRaiseShellDiagnostic(cmd) || CallsScriptFunction(cmd)))) && _statementLine > 0;
        lateLine = !now && eligible ? _statementLine : 0;
        return now ? PsBuild.SetCallLine(_statementLine) : "";
    }

    /// <summary>
    /// The line record for a statement whose up-front test (<see cref="MayRaiseShellDiagnostic"/>) recorded nothing:
    /// recorded after all when its emitted text can report a diagnostic (<see cref="PsBuild.MayReportDiagnostic"/>).
    /// The emitted text is the one source: every site that reports builds the prefix, discards the line or expands a
    /// glob, so a new diagnostic needs no entry in a list here. REGRESSION: <c>ro=2</c>, <c>${x?m}</c>, <c>${!r}</c>,
    /// <c>${x!}</c>, a failglob word and a posix special-builtin function on line 2 reported line 1.
    /// </summary>
    private static string LateLineRecord(string record, int lateLine, string emitted)
        => record.Length == 0 && lateLine > 0 && PsBuild.MayReportDiagnostic(emitted) ? PsBuild.SetCallLine(lateLine) : record;

    /// <summary>
    /// The statement calls a function this text defines: the function's BASH_LINENO[0] (and <c>caller</c>) is the line it
    /// was called from, read by code this text cannot see (a sourced library's <c>caller</c>, a run-time eval), so the line
    /// is recorded even when this text reads no BASH_LINENO. REGRESSION: <c>f</c> on line 6 of a script reported the line
    /// of the last statement that recorded one (the <c>. ./lib.sh</c> on line 3).
    /// </summary>
    private static bool CallsScriptFunction(Command cmd) => cmd switch
    {
        Command.Simple { Words.IsDefaultOrEmpty: false } s => _definedFunctions is { } fns && GetLiteralValue(s.Words[0]) is { } name && fns.Contains(name),
        Command.Pipeline p => p.Commands.Any(CallsScriptFunction),
        Command.AndOrList a => a.Commands.Any(CallsScriptFunction),
        _ => false,
    };

    /// <summary>
    /// A builtin that looks a name up and runs it: <c>command x</c> / <c>builtin x</c> (not <c>command -v x</c>, a query),
    /// <c>exec x</c> (not <c>exec &gt;f</c>), and an <c>eval</c> of text known only at run time (a static eval's text is
    /// emitted in place, its statements record their own lines).
    /// </summary>
    private static bool LooksUpACommand(Command.Simple s, string name) => name switch
    {
        "command" => s.Words.Length > 1 && !s.Words.Skip(1).TakeWhile(w => GetLiteralValue(w) is ['-', _, ..]).Any(w => GetLiteralValue(w)!.AsSpan().IndexOfAny('v', 'V') >= 0)
                     && s.Words.Skip(1).Any(w => GetLiteralValue(w) is not ['-', ..]),
        "builtin" or "exec" => s.Words.Length > 1,
        "eval" => s.Words.Skip(1).Any(w => TryReconstructBashSource(w) is null),
        _ => false,
    };

    /// <summary>Depth of nested <see cref="Transpile(string, TranspileContext)"/> calls (an inline eval's text is one).</summary>
    [ThreadStatic] private static int _transpileNesting;

    /// <summary>
    /// The outermost text being emitted is a script (`-c` text or a script file), not text transpiled at run time
    /// (a sourced file, a run-time eval): its first statements run when nothing has recorded a line yet, and the host
    /// starts every command on line 1 (VarFrameStack.ResetFor), so a statement on line 1 outside a function or loop
    /// need not record it.
    /// </summary>
    [ThreadStatic] private static bool _scriptRoot;

    /// <summary>
    /// <paramref name="cmd"/> runs a command on a line past 1, so a loop around it runs its line-1 commands again after
    /// a later line was recorded (they must record line 1 again). A function definition runs nothing; an unknown
    /// node is assumed to.
    /// </summary>
    private static bool RunsPastLine1(Command? cmd)
    {
        switch (cmd)
        {
            case null: return false;
            case Command.Simple s: return SimpleCommandLine(s) > 1;
            case Command.ShAssignment a: return a.Line > 1;
            case Command.BoolExpr b: return b.Line > 1;
            case Command.ArithCommand a: return a.Line > 1;
            case Command.ShFunction: return false;
            case Command.Pipeline p: foreach (var c in p.Commands) if (RunsPastLine1(c)) return true; return false;
            case Command.AndOrList l: foreach (var c in l.Commands) if (RunsPastLine1(c)) return true; return false;
            case Command.CommandList l: foreach (var c in l.Commands) if (RunsPastLine1(c)) return true; return false;
            case Command.If i:
                foreach (var arm in i.Arms) if (RunsPastLine1(arm.Cond) || RunsPastLine1(arm.Body)) return true;
                return RunsPastLine1(i.ElseBody);
            case Command.While w: return RunsPastLine1(w.Cond) || RunsPastLine1(w.Body);
            case Command.ForIn f: return f.Line > 1 || RunsPastLine1(f.Body);
            case Command.ForArith f: return f.Line > 1 || RunsPastLine1(f.Body);
            case Command.Select s: return RunsPastLine1(s.Body);
            case Command.Case c:
                if (c.Line > 1) return true;
                foreach (var arm in c.Arms) if (RunsPastLine1(arm.Body)) return true;
                return false;
            case Command.Subshell s: return RunsPastLine1(s.Body);
            case Command.BraceGroup g: return RunsPastLine1(g.Body);
            case Command.Background b: return RunsPastLine1(b.Inner);
            default: return true;
        }
    }
    /// <summary>The top-level statement being emitted has a simple command past line 1 (see <see cref="_scriptRoot"/>).</summary>
    [ThreadStatic] private static bool _loopReachesPastLine1;

    /// <summary>The script line of the statement being emitted (bash's, <see cref="SimpleCommandLine"/>); 0 when unknown.</summary>
    [ThreadStatic] private static int _statementLine;

    /// <summary>
    /// The line bash gives a simple command (<see cref="Command.Simple.ReportLine"/>: where the token after its first word
    /// ends; `x \&lt;newline&gt; arg` on 2 is line 3, `eval 'a&lt;newline&gt;b'` on 5 is 6), else the line it starts on.
    /// </summary>
    private static int SimpleCommandLine(Command.Simple s) => s.ReportLine > 0 ? s.ReportLine : s.Line;

    /// <summary>
    /// The statement may raise a shell diagnostic, which carries the statement's line (<see cref="PsBuild.DiagnosticPrefix"/>):
    /// <list type="bullet">
    /// <item>a command that is not found: a simple command (alone, in a pipeline or an and-or list) whose name is not a
    /// literal, or a literal that is no builtin, ps-bash command or script function -- or <c>command</c> / <c>exec</c> /
    /// <c>builtin</c>, which look a name up themselves;</item>
    /// <item>a redirect that may fail to open (<see cref="MayFailToOpen"/>: <c>&gt; nodir/f</c>, <c>&lt; missing</c>), on a
    /// simple command, an assignment or a compound command (whose redirects are applied before its body records
    /// anything);</item>
    /// <item>a builtin given an option word (it may be invalid: <c>pwd -Q</c>);</item>
    /// <item>a builtin that reports its own errors with the prefix (<see cref="s_selfReportingBuiltins"/>: <c>let</c>,
    /// <c>.</c> / <c>source</c>, <c>cd</c>, <c>type</c>, ...; <see cref="NumericBuiltinMayReport"/>: <c>shift x</c>, <c>break</c>
    /// with no loop);</item>
    /// <item>arithmetic, which may fail as it is evaluated (<c>((: 1/0 : division by 0</c>): <c>(( ... ))</c>, a word with
    /// <c>$(( ... ))</c>, a <c>[[ ... ]]</c> with a numeric operator.</item>
    /// </list>
    /// Otherwise a compound command is not: its body's statements record their own lines.
    /// </summary>
    private static bool MayRaiseShellDiagnostic(Command cmd) => cmd switch
    {
        _ when HasOpenableRedirect(CompoundRedirects(cmd)) => true,
        Command.Simple s when HasOpenableRedirect(s.Redirects) => true,
        Command.ArithCommand => true,
        // `[[ 1/0 -eq 1 ]]` (`[[: 1/0: division by 0`), `[ a -eq 1 ]` (`[: a: integer expression expected`).
        // `[ -v 'a[1/0]' ]`: an element operand's subscript is evaluated (`1/0: division by 0`, BashArrayElement.FailSubscript).
        Command.BoolExpr b => b.Inner.Any(w => GetLiteralValue(w) is "-eq" or "-ne" or "-lt" or "-le" or "-gt" or "-ge" or "-v"
            || ContainsCommandSub(w.Parts) || ContainsArithSub(w.Parts)),
        Command.Simple { Words.IsDefaultOrEmpty: false } s => GetLiteralValue(s.Words[0]) is not { Length: > 0 } name
            || s_selfReportingBuiltins.Contains(name)
            || NumericBuiltinMayReport(s, name)
            || LooksUpACommand(s, name)
            || (s_elementOperandBuiltins.Contains(name) && s.Words.Skip(1).Any(IsElementOperand))
            || s.Words.Any(w => ContainsCommandSub(w.Parts) || ContainsArithSub(w.Parts))
            // A builtin given an option word may reject it (`pwd -Q`: `script.sh: line N: pwd: -Q: invalid option`).
            || (BuiltinOptions.EmittedBuiltins.TryGetValue(name, out var emittedSpec) && BuiltinOptions.FindInvalid(s.Words.Skip(1).Select(TryGetStaticArgValue).ToList(), emittedSpec.Letters, emittedSpec.ValueLetters) is not null)
            || (s_optionDiagnosticBuiltins.Contains(name) && s.Words.Skip(1).Any(w => GetLiteralValue(w) is not { } v || v is ['-', not '-', ..] or ['-', '-', _, ..]))
            || (name == "set" && SetInvalidOption(s.Words.Skip(1).Select(TryGetStaticArgValue).ToList()) is not null)
            || !(s_posixSpecialBuiltins.Contains(name) || s_specialBuiltins.Contains(name)
                 || _definedFunctions is { } fns && fns.Contains(name) || IsMappedCommandName(name)),
        // `x=$(missing)`, `x=$(< missing)`, `x=$(( 2 + ))`: the substitution's diagnostic reports the assignment's line.
        Command.ShAssignment assign => assign.IsLocal || ContainsCommandSub(assign) || HasOpenableRedirect(assign.Redirects)
            || assign.Pairs.Any(p => (p.Value is { } v && ContainsArithSub(v.Parts))
                || (p.Name.IndexOf('[') is > 0 and var open && IsArithmeticSubscript(p.Name[open..]))),
        Command.Pipeline p => p.Commands.Any(MayRaiseShellDiagnostic),
        Command.AndOrList a => a.Commands.Any(MayRaiseShellDiagnostic),
        _ => false,
    };

    /// <summary>
    /// Builtins that evaluate an element operand's subscript at run time (<c>unset 'a[i]'</c>, <c>read "m[$k]"</c>,
    /// <c>printf -v 'a[i]'</c>, <c>test -v 'a[i]'</c>) and report one that is not an expression as the shell's diagnostic
    /// (<c>s.sh: line 3: 1/0: division by 0</c>, BashArrayElement.FailSubscript).
    /// </summary>
    private static readonly IReadOnlySet<string> s_elementOperandBuiltins = new HashSet<string>(StringComparer.Ordinal)
    {
        "unset", "read", "printf", "test", "[",
    };

    /// <summary>A word whose static start is <c>NAME[</c>: <c>'a[1/0]'</c>, <c>"m[$k]"</c>, <c>a[i]</c>.</summary>
    private static bool IsElementOperand(CompoundWord word)
    {
        var start = new StringBuilder();
        foreach (var part in word.Parts)
        {
            if (part is WordPart.Literal l) start.Append(l.Value);
            else if (part is WordPart.EscapedLiteral e) start.Append(e.Value);
            else if (part is WordPart.SingleQuoted q) start.Append(q.Value);
            else if (part is WordPart.DoubleQuoted d)
            {
                foreach (var inner in d.Parts)
                {
                    if (inner is WordPart.Literal il) start.Append(il.Value);
                    else break;
                }
                break;
            }
            else break;
        }
        return s_elementOperandStart.IsMatch(start.ToString());
    }

    private static readonly System.Text.RegularExpressions.Regex s_elementOperandStart =
        new(@"^[A-Za-z_][A-Za-z0-9_]*\[", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Builtins that report their own errors as the shell's diagnostic (<c>s.sh: line 3: cd: /x: No such file or
    /// directory</c>, ShellDiagnostic): a statement that runs one records its line. docs/specs/error-handling.md
    /// "Diagnostic prefix".
    /// </summary>
    private static readonly IReadOnlySet<string> s_selfReportingBuiltins = new HashSet<string>(StringComparer.Ordinal)
    {
        "let", ".", "source", "cd", "type", "declare", "typeset", "local", "alias", "unalias", "wait", "command", "builtin", "help",
        "[", "test", "history", "fc",
    };

    /// <summary>
    /// <c>break</c> / <c>continue</c> / <c>return</c> / <c>exit</c> / <c>shift</c> that may report (<c>s.sh: line 3: shift: x:
    /// numeric argument required</c>, <c>break: only meaningful in a loop</c>, <c>exit: too many arguments</c>): an operand
    /// that is not a plain number, more than one operand, or a loop jump with no loop label in reach. A plain
    /// <c>break</c> in a loop or <c>exit 1</c> records nothing, so hot loops stay cheap. REGRESSION: these named the
    /// line of an earlier statement.
    /// </summary>
    private static bool NumericBuiltinMayReport(Command.Simple s, string name) =>
        name is "break" or "continue" or "return" or "exit" or "shift"
        && (s.Words.Length > 2
            || (s.Words.Length == 2 && !(GetLiteralValue(s.Words[1]) is { } count && count.Length > 0 && count.All(char.IsAsciiDigit)))
            || (name is "break" or "continue" && _loopLabels is not { Count: > 0 }));

    /// <summary>
    /// Builtins whose invalid-option diagnostic carries the shell prefix with the line (FileSystemHelpers.
    /// WriteBuiltinInvalidOption, the emitted cd): a statement that gives one an option word records its line.
    /// </summary>
    private static readonly IReadOnlySet<string> s_optionDiagnosticBuiltins = new HashSet<string>(StringComparer.Ordinal)
    {
        "alias", "unalias", "cd", "pwd", "jobs", "wait", "mapfile", "readarray", "compgen", "times", "unset", "type",
        "ulimit", "source", "getopts", "trap", "compopt", "disown", "bind", "caller", "enable", "read",
    };

    private static bool HasOpenableRedirect(ImmutableArray<Redirect> redirects)
    {
        if (redirects.IsDefaultOrEmpty) return false;
        foreach (var r in redirects)
            if (MayFailToOpen(r)) return true;
        return false;
    }

    /// <summary>
    /// A redirect that opens a file, so it can fail with a shell diagnostic (<c>nodir/f: No such file or directory</c>):
    /// <c>&lt; &gt; &gt;&gt; &gt;| &amp;&gt; &amp;&gt;&gt;</c> and <c>&gt;&amp;word</c> (= <c>&amp;&gt;</c>). Not a dup onto a
    /// descriptor (<c>2&gt;&amp;1</c>, <c>&gt;&amp;-</c>), a here-document or here-string, or the literal <c>/dev/null</c>.
    /// </summary>
    private static bool MayFailToOpen(Redirect r)
    {
        if (r.Here is not null) return false;
        string? literal = GetLiteralValue(r.Target);
        if (literal == DevicePath.Null) return false;
        return r.Op switch
        {
            "<" or ">" or ">>" or ">|" or "&>" or "&>>" => true,
            ">&" => literal is null || !(literal == "-" || IsAllDigits(literal)),
            _ => false,
        };
    }

    /// <summary>
    /// The redirect tail the report of an input redirect that cannot be opened goes through
    /// (<see cref="PsBuild.InputRedirectFailure"/>): the stdout/stderr redirects written BEFORE <paramref name="input"/>,
    /// which bash has applied when it reaches the <c>&lt;</c> (left to right), so <c>cmd 2&gt;/dev/null &lt; missing</c>
    /// is silent and <c>cmd &gt;out &lt; missing</c> still creates <c>out</c>. Null when there are none.
    /// </summary>
    private static string? InputFailTail(IEnumerable<Redirect> redirects, Redirect input)
    {
        List<Redirect>? before = null;
        foreach (var r in redirects)
        {
            if (ReferenceEquals(r, input)) break;
            if (r.FdVar is null && r.Here is null && r.Fd is 1 or 2 && r.Op is not ("<" or "<<" or "<<<"))
                (before ??= new List<Redirect>()).Add(r);
        }
        if (before is null) return null;
        var sb = new StringBuilder();
        AppendRedirectTail(sb, before);
        return sb.ToString();
    }

    /// <summary>The script reads BASH_LINENO: statements record their line for the functions they call.</summary>
    [ThreadStatic]
    private static bool _bashLinenoMode;

    private static string EmitStatementCoreBody(Command cmd)
    {
        _priorTrapLine = PsBuild.TrapLine;
        if (cmd is Command.Simple { Line: > 0 } lined) PsBuild.TrapLine = _lineBase + lined.Line;
        else if (LastSimpleLine(cmd) is > 0 and var lastLine) PsBuild.TrapLine = _lineBase + lastLine;
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
        (line > 0 || PsBuild.InTrapAction) && DebugActive ? PsBuild.DebugTrapRun(line, _errexitMode ? ErrexitExitStatement() : null) : "";

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
        Command.ShAssignment s => s.Line,
        Command.BoolExpr b => b.Line,
        Command.ArithCommand c => c.Line,
        Command.AndOrList a when a.Commands.Length > 0 => LastSimpleLine(a.Commands[^1]),
        Command.Pipeline p when p.Commands.Length > 0 => LastSimpleLine(p.Commands[^1]),
        _ => 0,
    };

    /// <summary>
    /// The line a compound command without a line of its own (<c>while</c>, <c>{ }</c>, <c>( )</c>, <c>if</c>) starts
    /// on: its first command's. The line of a redirect's open error where <see cref="_redirectLine"/> knows none (a
    /// function body, a substitution): bash names the function's line there, which is this for a one-line function
    /// (docs/specs/error-handling.md "Redirect open errors").
    /// </summary>
    private static int CompoundStartLine(Command cmd) => cmd switch
    {
        Command.While w => FirstLineIn(w.Cond),
        Command.BraceGroup b => FirstLineIn(b.Body),
        Command.Subshell s => FirstLineIn(s.Body),
        Command.If i when i.Arms.Length > 0 => FirstLineIn(i.Arms[0].Cond),
        Command.ForIn f => f.Line,
        Command.ForArith f => f.Line,
        Command.Case c => c.Line,
        _ => 0,
    };

    private static int FirstLineIn(Command cmd) => cmd switch
    {
        Command.CommandList l when l.Commands.Length > 0 => FirstLineIn(l.Commands[0]),
        Command.Simple { Line: > 0 } s => SimpleCommandLine(s),
        _ when FirstSimpleLine(cmd) is > 0 and var line => line,
        _ => CompoundStartLine(cmd),
    };

    /// <summary>Line of the first simple command <paramref name="cmd"/> runs; 0 when unknown.</summary>
    private static int FirstSimpleLine(Command cmd) => cmd switch
    {
        Command.AndOrList a when a.Commands.Length > 0 => FirstSimpleLine(a.Commands[0]),
        Command.Pipeline p when p.Commands.Length > 0 => FirstSimpleLine(p.Commands[0]),
        _ => LastSimpleLine(cmd),
    };

    private static bool IsTrapCommand(Command.Simple simple)
        => simple.Words.Length > 0 && TryGetStaticArgValue(simple.Words[0]) == "trap";

    private static string EmitStatementBody(Command cmd)
    {
        if (!_errexitMode || _errexitExemptDepth > 0)
            return EmitRecordingStatus(cmd);

        switch (cmd)
        {
            case Command.AndOrList andOr:
                return EmitAndOrStatement(andOr);
            case Command.Pipeline { Negated: true }:
            {
                // bash ignores errexit under `!` only when it is on as the pipeline starts: a `set -e` run inside a
                // function or group under `!` (`f() { set -e; false; echo x; }; ! f`) takes effect at once. So the
                // body keeps its checks and the suppression is decided at run time.
                int depth = _negatedDepth++;
                bool priorGuard = _negationGuardedAtRuntime;
                _negationGuardedAtRuntime = true;
                try { return PsBuild.ErrexitSuppressedIfOn(Emit(cmd), "$__psbNeg" + depth); }
                finally { _negatedDepth--; _negationGuardedAtRuntime = priorGuard; }
            }
            case Command.Pipeline pipeline:
            {
                // Every stage is a subshell in bash: a failure INSIDE a stage never ends the
                // script; only the last stage's status counts. The stage itself inherits -e, so a compound
                // stage ends at its failing command (EmitCompoundStage).
                _pipelineAtArmedStatement = true;
                string text;
                // A stage that lifts the suppression (EmitCompoundStage) sets it back to its value before this pipeline.
                string? outerBase = _armedBase; bool outerBaseUsed = _armedBaseUsed;
                string mark = "$__psbash_eb" + _armedBaseDepth++;
                _armedBase = mark; _armedBaseUsed = false;
                try
                {
                    text = WithErrexitExempt(() => Emit(cmd));
                    text = _armedBaseUsed ? PsBuild.ErrexitSuppressedFrom(mark, text) : PsBuild.ErrexitSuppressed(text);
                }
                finally { _pipelineAtArmedStatement = false; _armedBaseDepth--; _armedBase = outerBase; _armedBaseUsed = outerBaseUsed; }
                // bash runs the ERR trap twice for a failed pipeline that ends in a subshell (`true | (false)`,
                // also `(false) | (true)` under pipefail); a group or plain last stage runs it once.
                string twice = pipeline.Commands.Length > 1 && pipeline.Commands[^1] is Command.Subshell
                    ? PsBuild.ErrTrapCheck() : "";
                return text + "; " + twice + PsBuild.ErrexitCheck(ErrexitExitStatement());
            }
            // The job is a subshell: set -e and (under errtrace) the ERR trap apply inside it (EmitBackground); its own status is 0.
            case Command.Background:
                return EmitRecordingStatus(cmd);
            case Command.Simple simple when IsErrexitCheckedSimple(simple):
            // `x=$(false)` fails with the substitution; `local`/`export x=$(false)` has the builtin's status (0).
            case Command.ShAssignment assign when !assign.IsLocal && !assign.IsExport && (ContainsCommandSub(assign) || WritesReadonly(assign)):
            case Command.BoolExpr:
            case Command.ArithCommand:
            case Command.Subshell:
                return EmitRecordingStatus(cmd) + "; " + PsBuild.ErrexitCheck(ErrexitExitStatement());
            case Command.BraceGroup g when OpensOutputFile(g.Redirects):
            case Command.If i when OpensOutputFile(i.Redirects):
            case Command.While w when OpensOutputFile(w.Redirects):
            case Command.ForIn f when OpensOutputFile(f.Redirects):
            case Command.ForArith a when OpensOutputFile(a.Redirects):
            case Command.Case c when OpensOutputFile(c.Redirects):
                return EmitRedirectedCompound(cmd);
            default:
                return EmitRecordingStatus(cmd);
        }
    }

    /// <summary>
    /// A compound command with redirects, under an ERR trap / errexit: a redirect that cannot be opened fails it
    /// (status 1, the body never runs) and runs the trap and errexit; the body's own status does not. bash leaves
    /// $LINENO at the command before it, which is the last one that ran.
    /// </summary>
    /// <summary>A stdout file redirect (<c>&gt; f</c>, <c>&gt;&gt; f</c>, <c>&gt;| f</c>, <c>&amp;&gt; f</c>) that
    /// Invoke-BashRedirect opens; <c>/dev/null</c> is emitted without it.</summary>
    private static bool OpensOutputFile(ImmutableArray<Redirect> redirects)
    {
        if (redirects.IsDefaultOrEmpty) return false;
        foreach (var r in redirects)
        {
            if (r.Here is not null || r.FdVar is not null) continue;
            bool stdout = r.Op is "&>" or "&>>" || (r.Op is ">" or ">>" or ">|" && r.Fd == 1);
            if (stdout && GetLiteralValue(r.Target) != DevicePath.Null) return true;
        }
        return false;
    }

    private static string EmitRedirectedCompound(Command cmd)
    {
        int prior = _priorTrapLine;
        string text = Emit(cmd);
        PsBuild.TrapLine = prior;
        return PsBuild.RedirectFailureChecked(text, PsBuild.ErrexitCheck(ErrexitExitStatement()));
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
        // `$( ( set -e; false; echo in ); echo "inner $?" )`: the nested subshell's own -e applies inside it.
        Command.Subshell s => BodySetsErrexit(s.Body),
        _ => false,
    };

    /// <summary>A command in <paramref name="command"/> runs a function of this script or eval/source text.</summary>
    private static bool BodyMayRunSetE(BashNode? command) => command switch
    {
        Command.Simple { Words.Length: > 0 } s => GetLiteralValue(s.Words[0]) is { } n
            && (n is "eval" or "fc" or "source" or "." || _definedFunctions?.Contains(n) == true || _unitFunctions?.Contains(n) == true),
        Command.Pipeline p => p.Commands.Any(BodyMayRunSetE),
        Command.AndOrList a => a.Commands.Any(BodyMayRunSetE),
        Command.CommandList l => l.Commands.Any(BodyMayRunSetE),
        Command.BraceGroup b => BodyMayRunSetE(b.Body),
        Command.Subshell s => BodyMayRunSetE(s.Body),
        Command.If i => i.Arms.Any(arm => BodyMayRunSetE(arm.Cond) || BodyMayRunSetE(arm.Body)) || BodyMayRunSetE(i.ElseBody),
        Command.ForIn f => BodyMayRunSetE(f.Body),
        Command.While w => BodyMayRunSetE(w.Cond) || BodyMayRunSetE(w.Body),
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

    private static string EmitCaptured(Command body, bool scoped = false)
    {
        var savedCaptureBody = _captureBody;
        _captureBody = body;
        // A refused assignment in `$( ... )` / `<( ... )` drops the rest of the substitution's line, not the line the
        // substitution is on (`u=$(r=2; echo x); echo "$?"` prints 1). REGRESSION: the outer `echo` was dropped too.
        bool savedReadonlyViolation = _readonlyViolation;
        try { return EmitCapturedCore(body, scoped); }
        finally { _captureBody = savedCaptureBody; _readonlyViolation = savedReadonlyViolation; }
    }

    /// <param name="body">The command substitution\'s body.</param>
    /// <param name="scoped">The text runs through <see cref="PipelineHead"/>, whose subshell scope puts set -e back after.</param>
    private static string EmitCapturedCore(Command body, bool scoped)
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
        // A body that runs code which may turn -e on where this text cannot see it (a function of this script, eval or
        // source text) is treated the same: `v=$(f)` with `f() { set -e; false; echo x; }` leaves v empty, status 1.
        // (Not under inherit_errexit / errtrace, which keep their own handling below.)
        bool ownErrexit = BodySetsErrexit(body)
            || (_errexitMode && !_inheritErrexitMode && !_errtraceMode && BodyMayRunSetE(body));
        bool savedMode = _errexitMode;
        int savedExempt = _errexitExemptDepth;
        // Otherwise set -e is not inherited, but under errtrace the ERR trap runs inside (`x=$( false; echo y )` traps):
        // a scoped body turns errexit off and keeps its statements' checks, so only the trap half can fire.
        // So does a body that sets its own ERR trap (`x=$(trap 'echo E' ERR; false)` captures E): BodySetsErrTrap.
        bool trapInside = !ownErrexit && scoped && (_errtraceMode || BodySetsErrTrap(body));
        // `shopt -s inherit_errexit`: the substitution keeps -e. The body gets its checks; at run time they may fire only
        // while the option is on (PsBuild.InheritErrexitCapture). A scoped body returns from its own scope when one does.
        bool inheritErrexit = !ownErrexit && !trapInside && scoped && _errexitMode && _inheritErrexitMode;
        int savedLineBase = _lineBase, savedRedirectLine = _redirectLine;
        if (PsBuild.TrapLine > 0) _lineBase = PsBuild.TrapLine - 1;
        _redirectLine = 0;
        if (ownErrexit || trapInside || inheritErrexit) { _errexitMode = true; _errexitExemptDepth = 0; }
        else _errexitExemptDepth++;
        // A substitution body is a SUBSHELL: `exit N` inside `$( … )` / `<( … )` leaves only it
        // (`v=$(exit 3); echo $?` prints 3). Raise the depth so `exit` emits the scoped `return`;
        // PipelineHead / Invoke-ProcessSub supply the script block it returns from.
        _subshellDepth++; var subRoOuter = EnterSubshellTables();
        // A body that is sure to get the subshell scope (PipelineHead) has the ERR trap put aside at run time
        // (ErrTrapInheritance), so `x=$(trap 'echo E' ERR; false)` fires its own trap; one that may run unscoped keeps
        // the static errtrace test.
        bool savedErrTrapSub = PsBuild.ErrTrapNeedsErrtrace;
        PsBuild.ErrTrapNeedsErrtrace = !(scoped && !CannotChangeShellState(body));
        // No lexical loop reaches into the substitution: with one around it a `break` ends the substitution, without one
        // it warns as it would here (LoopBarrierKind).
        var capOuterLabels = _loopLabels; var capOuterBarrier = _loopBarrier;
        _loopBarrier = LoopControlWarnsHere ? LoopBarrierKind.Warn : LoopBarrierKind.EndsSubshell;
        _loopLabels = null;
        try
        {
            // A lone command is at statement position too: `x=$(false)` runs the trap inside.
            var text = trapInside ? EmitBody(body) : Emit(body);
            if (ownErrexit)
                return "$global:__BashErrexit = $false; " + PsBuild.ErrexitSuppressVar + " = 0; " + text;
            if (trapInside)
                return "$global:__BashErrexit = $false; " + text;
            if (inheritErrexit)
                return PsBuild.InheritErrexitCapture(text);
            return _errexitMode && ConditionRunsCommand(body) ? PsBuild.ErrexitSuppressed(text) : text;
        }
        finally
        {
            _captureDepth--; _pipeStageFeedsInput = savedFeed; _subshellDepth--; LeaveSubshellTables(subRoOuter); PsBuild.ErrTrapNeedsErrtrace = savedErrTrapSub;
            _errexitMode = savedMode; _errexitExemptDepth = savedExempt; _lineBase = savedLineBase; _redirectLine = savedRedirectLine;
            _loopLabels = capOuterLabels; _loopBarrier = capOuterBarrier;
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
    public static string Emit(Command cmd)
    {
        if (cmd is Command.Simple || !RedirectsMergeStderr(CompoundRedirects(cmd))) return EmitCommand(cmd);
        _stderrMergeDepth++;
        try { return EmitCommand(cmd); }
        finally { _stderrMergeDepth--; }
    }

    /// <summary>
    /// One command: its frame records <see cref="_leavingReturns"/> as it begins, so <see cref="RedirectGroup"/> can tell
    /// whether the command's own text (its body) has a <c>return</c> in it.
    /// </summary>
    private static string EmitCommand(Command cmd)
    {
        int priorMark = _groupReturnMark;
        _groupReturnMark = _leavingReturns;
        try { return EmitCommandCore(cmd); }
        finally { _groupReturnMark = priorMark; }
    }

    private static string EmitCommandCore(Command cmd) => TryEmitCompoundOutputProcessSubs(StackGuarded(cmd), out var outputProcSubs) ? outputProcSubs : cmd switch
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
        Command.Background { Inner: Command.Simple nohupBg } when IsNohup(nohupBg) => EmitSimpleFed(RewriteNohup(nohupBg, background: true)),
        Command.Background bg => EmitBackground(bg),
        Command.Simple simple => EmitSimpleFed(IsNohup(simple) ? RewriteNohup(simple, background: false) : simple),
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
        var priorErrexit = _errexitMode; var priorErrtrace = _errtraceMode; var priorErrexitSet = _errexitSetMode;
        var priorExempt = _errexitExemptDepth;
        var priorIntVars = _intVars;
        var priorAssocVars = _assocVars;
        _assocVars = new HashSet<string>(StringComparer.Ordinal);
        var priorArrayVars = _arrayVars;
        // BASH_REMATCH is an array `=~` fills: a bare `$BASH_REMATCH` is its element 0.
        _arrayVars = new HashSet<string>(StringComparer.Ordinal) { "BASH_REMATCH" };
        var priorCaseVars = _caseVars;
        _caseVars = new Dictionary<string, bool>(VarNameKeys);
        _intVars = new HashSet<string>(VarNameKeys);
        _globalAttrsInFunctions = null;
        var priorReadsStatus = _scriptReadsStatus;
        var priorFinalAssignment = _finalAssignment;
        var priorReadsLastArg = _scriptReadsLastArg;
        var priorPosix = _scriptPosix;
        var priorLastpipe = _scriptLastpipe;
        _scriptLastpipe = priorLastpipe || bash.Contains("lastpipe");
        _scriptPosix = priorPosix || bash.Contains("set -o posix");
        var priorInheritErrexit = _inheritErrexitMode;
        _inheritErrexitMode = priorInheritErrexit || bash.Contains("inherit_errexit", StringComparison.Ordinal);
        _scriptReadsLastArg = priorReadsLastArg || System.Text.RegularExpressions.Regex.IsMatch(bash, @"\$(_(?![A-Za-z0-9_])|\{_[^A-Za-z0-9_])");
        _scriptReadsStatus = priorReadsStatus || bash.Contains("$?") || bash.Contains("${?");
        var priorReadonly = _readonlyVars;
        var priorRuntimeReadonly = _runtimeReadonly;
        _readonlyVars = new HashSet<string>(VarNameKeys);
        _readonlyVars.Add("SHELLOPTS"); _readonlyVars.Add("BASHOPTS");
        var priorNamerefs = _namerefs;
        _namerefs = new Dictionary<string, string>(VarNameKeys);
        var priorLitAssigned = _litAssigned; var priorPendingNamerefs = _pendingNamerefs; var priorRuntimeNamerefs = _runtimeNamerefs;
        _litAssigned = new Dictionary<string, string>(VarNameKeys); _pendingNamerefs = new HashSet<string>(VarNameKeys);
        _runtimeNamerefs = new HashSet<string>(VarNameKeys);
        _context = context;
        // Only a script that itself turns errexit on gets the checks (a sourced file running under
        // its caller's `set -e` does not — see intentional-differences.md).
        _errexitMode = EnablesErrexit(bash);
        _errexitSetMode = _errexitMode && SetsErrexit(bash);
        _errtraceMode = _errexitMode && EnablesErrtrace(bash);
        _errexitExemptDepth = 0;
        var priorDefined = _definedFunctions;
        var priorAliasDefs = _aliasDefs; var priorAliasLine = _expandAliasesLine; var priorAliasStatic = _aliasStaticOnly; _aliasDefs = null; _aliasStaticOnly = false;
        var priorUnitFunctions = _unitFunctions;
        _unitFunctions = null;
        var priorStderrMergeDepth = _stderrMergeDepth;
        _stderrMergeDepth = 0;
        var priorPipefail = _pipefailMode;
        var priorFailglob = _lineAbandonMode;
        var priorRoot = _rootCommand; var priorDiscardInteractive = _discardInteractive; var priorLiveJobs = _liveJobs;
        _lineAbandonMode = priorFailglob || bash.Contains("failglob", StringComparison.Ordinal);
        var priorNounset = _nounsetMode;
        _nounsetMode = priorNounset || InheritedNounset;
        var priorPipeStatus = _pipeStatusMode;
        var priorNamesLoopControl = _namesLoopControl;
        var priorDynamicCode = _runsDynamicCode;
        _runsDynamicCode = priorDynamicCode || System.Text.RegularExpressions.Regex.IsMatch(bash, @"\b(?:eval|source)\b|(?:^|[\s;&|(])\.\s+\S");
        var priorXtrace = _xtraceMode;
        var priorTrap = _trapMode;
        var priorCanSetTrap = _canSetTrap;
        var priorDebugTrap = _debugTrapMode;
        _debugTrapMode = SetsDebugTrapForOtherCommands(bash);
        var priorDeclarePrint = _declarePrintMode;
        // Eval and sourced text cannot see the `declare -p` that will print what they declare (it is in the caller), so
        // they always record the attributes.
        _declarePrintMode = _context == TranspileContext.Eval
            || System.Text.RegularExpressions.Regex.IsMatch(bash, @"\b(?:declare|typeset)\s+(?:-[a-zA-Z]+\s+)*-[a-zA-Z]*p")
            || System.Text.RegularExpressions.Regex.IsMatch(bash, @"\bexport\s*(?:-p\s*)?(?:[|;&)}]|\r?\n|$)")
            || bash.Contains("@a}", StringComparison.Ordinal) || bash.Contains("@A}", StringComparison.Ordinal);
        _trapMode = System.Text.RegularExpressions.Regex.IsMatch(bash, @"\btrap\b");
        _xtraceMode = false;
        _pipefailMode = priorPipefail || InheritedPipefail;
        var priorBashLineno = _bashLinenoMode;
        _bashLinenoMode = priorBashLineno || bash.Contains("BASH_LINENO", StringComparison.Ordinal);
        var trapSignal = TrapActionSignal;
        TrapActionSignal = null;
        var priorInTrapAction = PsBuild.InTrapAction;
        PsBuild.InTrapAction = trapSignal is "ERR" or "DEBUG";
        if (trapSignal is not null)
        {
            _errexitMode = true;
            if (trapSignal != "DEBUG") _debugTrapMode = true;
        }
        var priorScriptRoot = _scriptRoot;
        if (_transpileNesting++ == 0) _scriptRoot = context == TranspileContext.Default;
        // Nested text (eval, source, a trap action) counts its own statements.
        int priorRedirectLine = _redirectLine;
        _redirectLine = 0;
        // Only the root script's input lines are read by the shell's reader; eval, sourced and inline text are not.
        var priorReaderMode = _readerMode;
        _readerMode = _transpileNesting == 1 && context == TranspileContext.Default ? ReaderModeFor(bash) : ReaderMode.Off;
        try
        {
            Command? cmd;
            bool aliasesSpliced = false;
            BashParser.SawBadSubstitution = false;
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
            // A lone statement starts from status 0 anyway.
            _finalAssignment = (cmd is Command.CommandList { Commands.Length: > 1 } top ? top.Commands[^1] : null) as Command.ShAssignment;
            CollectCallSites(cmd);
            CollectArrayAssignments(cmd);
            CollectAliasDefs(cmd, bash);
            if (aliasesSpliced) { _aliasDefs = null; _aliasStaticOnly = false; } // already expanded in the text
            if (BashParser.SawBadSubstitution) _lineAbandonMode = true;
            _rootCommand = cmd;
            _canSetTrap = priorCanSetTrap || CanSetTrap(cmd);
            // Only a unit that can turn pipefail on or reads PIPESTATUS pays for per-stage status capture (it also leaves the
            // fused lane). Decided on the parsed commands: a quoted `grep pipefail f` turns nothing on (CanSetPipefail).
            _pipeStatusMode = ReadsPipestatus(cmd);
            // Static eval text is emitted inline under its caller's modes: `c=break; while :; do eval '$c'; done`.
            _namesLoopControl = _namesLoopControl || NamesLoopControl(cmd);
            _pipefailMode = _pipefailMode || _pipeStatusMode || CanSetPipefail(cmd);
            // Likewise set -u and set -x, also through a word known only at run time (set -o "$o", c=set; $c -x).
            _nounsetMode = _nounsetMode || CanSetNounset(cmd);
            _xtraceMode = CanSetXtrace(cmd);
            _liveJobs = _context == TranspileContext.Eval ? null : CollectLiveJobs(cmd);
            _discardInteractive = _nounsetMode && bash.Contains("shopt -s psbash_interactive", StringComparison.Ordinal);
            string body = EmitBody(cmd);
            // A lone top-level command is the reader's one input line (a list reports each line in EmitCommandList).
            if (cmd is not Command.CommandList) body = ReaderCommandFor(cmd, 0) + body;
            // A lone top-level command is its own input line (a list gets the scope per command in EmitCommandList).
            if (cmd is not Command.CommandList && (_discardInteractive || PsBuild.CanDiscardLine(body)))
                body = PsBuild.DiscardLineScope(body);
            // eval text (also source, trap, an interactive line): a failed glob or a bad substitution in it abandons the
            // rest of ITS line only; the line that ran the eval goes on (`eval 'echo *.ZZ'; echo $?` prints 1).
            return _context == TranspileContext.Eval && PsBuild.CanAbandonLine(body) ? PsBuild.EndOfEvalText(body) : body;
        }
        finally
        {
            _transpileNesting--; _scriptRoot = priorScriptRoot; _readerMode = priorReaderMode; _redirectLine = priorRedirectLine;
            _context = prior;
            _intVars = priorIntVars;
            _assocVars = priorAssocVars;
            _arrayVars = priorArrayVars;
            _caseVars = priorCaseVars;
            _scriptReadsStatus = priorReadsStatus;
            _finalAssignment = priorFinalAssignment;
            _scriptReadsLastArg = priorReadsLastArg;
            _scriptPosix = priorPosix;
            _inheritErrexitMode = priorInheritErrexit;
            _scriptLastpipe = priorLastpipe;
            _readonlyVars = priorReadonly;
            _runtimeReadonly = priorRuntimeReadonly;
            _namerefs = priorNamerefs;
            _litAssigned = priorLitAssigned; _pendingNamerefs = priorPendingNamerefs; _runtimeNamerefs = priorRuntimeNamerefs;
            _errexitMode = priorErrexit; _errtraceMode = priorErrtrace; _errexitSetMode = priorErrexitSet;
            _errexitExemptDepth = priorExempt;
            _pipefailMode = priorPipefail;
            _lineAbandonMode = priorFailglob;
            _rootCommand = priorRoot; _discardInteractive = priorDiscardInteractive; _liveJobs = priorLiveJobs;
            _nounsetMode = priorNounset;
            _unitFunctions = priorUnitFunctions;
            _stderrMergeDepth = priorStderrMergeDepth;
            _definedFunctions = priorDefined;
            _runsDynamicCode = priorDynamicCode;
            _aliasDefs = priorAliasDefs; _expandAliasesLine = priorAliasLine; _aliasStaticOnly = priorAliasStatic;
            _xtraceMode = priorXtrace;
            _pipeStatusMode = priorPipeStatus;
            _namesLoopControl = priorNamesLoopControl;
            _trapMode = priorTrap;
            _canSetTrap = priorCanSetTrap;
            _debugTrapMode = priorDebugTrap;
            _declarePrintMode = priorDeclarePrint;
            PsBuild.InTrapAction = priorInTrapAction;
            _bashLinenoMode = priorBashLineno;
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
    /// An <c>if</c>/<c>elif</c>/<c>while</c>/<c>until</c> condition in expression position: a test that runs no command
    /// (<c>[ ]</c>, <c>[[ ]]</c>, <c>(( ))</c>, <c>true</c>), errexit-exempt as every condition is in bash. A condition that
    /// runs a command is hoisted to statement position instead (<see cref="IsHoistedCondition"/>,
    /// <see cref="EmitHoistedCondition"/>), so a <c>return</c> in it (<c>if return 6; then</c>, <c>while ! return 6</c>,
    /// <c>if [[ 1 ]] &amp;&amp; return 6</c>) is a statement of the function and leaves it, under set -e and an ERR
    /// trap too — in the expression's script block (<see cref="PsBuild.ExitCodeTest"/>) it would leave only the block.
    /// One is refused here, so no new caller can put a command in that block (guard <c>RedirectGroupGuardTests</c>).
    /// </summary>
    private static string EmitExpressionCondition(Command cond)
    {
        if (IsHoistedCondition(cond))
            throw new InvalidOperationException("a condition that runs a command is hoisted (EmitHoistedCondition), never an expression");
        return WithErrexitExempt(() => EmitCondition(cond));
    }
    private static bool ConditionRunsCommand(Command cond) => cond switch
    {
        Command.BoolExpr or Command.ArithCommand => false,
        Command.Simple s when s.Words.Length == 1 && s.EnvPairs.IsEmpty && s.Redirects.IsEmpty
            && IsConstantStatusBuiltin(s, out _) => false,
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
        for (int i = start; i < ifCmd.Arms.Length; i++)
        {
            var arm = ifCmd.Arms[i];
            bool hoist = IsHoistedCondition(arm.Cond);
            if (i > start && hoist)
            {
                sb.Append(" else { ").Append(EmitIfFromArm(ifCmd, i)).Append(" }");
                return sb.ToString();
            }
            if (hoist)
                sb.Append(EmitHoistedCondition(arm.Cond)).Append("if (").Append(PsBuild.LastStatusTest()).Append(')');
            else
                sb.Append(i == start ? "if (" : " elseif (").Append(EmitExpressionCondition(arm.Cond)).Append(')');
            sb.Append(" { ").Append(EmitBody(arm.Body)).Append(" }");
        }

        if (ifCmd.ElseBody is not null)
        {
            sb.Append(" else { ");
            sb.Append(EmitBody(ifCmd.ElseBody));
            sb.Append(" }");
        }
        else
        {
            // Every condition sets the status (a test through PsBuild.ConditionStatus).
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
    /// errexit: <see cref="PsBuild.ErrexitSuppressed"/>, a try/finally, so a return in it leaves the function), followed by "; ".</summary>
    private static string EmitHoistedCondition(Command cond)
    {
        // A condition names its command like a statement does: `if nosuch; then` on line 3 reports line 3, and a loop's
        // condition is tested again after its body recorded later lines. Recorded first: a static eval in it counts from it.
        string record = RecordLineFor(cond, statement: false, out int lateLine);
        // While DEBUG is on every condition is hoisted, so the action can run before it (each time it is tested).
        var text = WithErrexitExempt(() => !DebugActive ? Emit(cond)
            : cond is Command.AndOrList andOr ? EmitDebugAndOr(andOr) : WithDebugLead(cond, () => Emit(cond)));
        record = LateLineRecord(record, lateLine, text);
        if (_errexitMode) text = PsBuild.ErrexitSuppressed(text);
        return record + PsBuild.HoistedCondition(text);
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
    /// Set inside a subshell's body (outside any loop or function within it). A PowerShell `break`/`continue` is
    /// dynamically scoped, so without it the keyword reached the enclosing loop through the subshell's script block.
    /// <see cref="LoopBarrierKind.Warn"/>: bash starts `( ... )`, a background job and a compound pipe stage with no loop,
    /// so `( continue )` inside a loop warns and the subshell carries on. <see cref="LoopBarrierKind.EndsSubshell"/>:
    /// `$( ... )`, `&lt;( ... )` and a simple first pipe stage keep the loop level, so `v=$( break )` ends the substitution
    /// (status 0) and the loop goes on.
    /// </summary>
    [ThreadStatic]
    private static LoopBarrierKind _loopBarrier;

    private enum LoopBarrierKind : byte { None, Warn, EndsSubshell }

    /// <summary>Emitting the text of an `eval` at the top of a script (outside any function): its `return` is refused.</summary>
    [ThreadStatic]
    private static bool _evalAtScriptTop;

    /// <summary>The script runs eval or source text, which can define functions the emitter never sees.</summary>
    [ThreadStatic]
    private static bool _runsDynamicCode;

    /// <summary>
    /// The unit can give a real signal a trap (<see cref="CanSetTrap"/>, decided on the parsed commands): each top-level
    /// statement then ends with the signal poll (<see cref="PsBuild.SignalTrapPoll"/>, background-jobs.md section 13).
    /// </summary>
    [ThreadStatic]
    private static bool _canSetTrap;

    /// <summary>Emit with no lexical loop in scope and <paramref name="kind"/> as the barrier (when <paramref name="apply"/>).</summary>
    private static T WithLoopBarrier<T>(bool apply, LoopBarrierKind kind, Func<T> emit)
    {
        if (!apply) return emit();
        var outerLabels = _loopLabels; var outerBarrier = _loopBarrier;
        _loopLabels = null; _loopBarrier = kind;
        try { return emit(); }
        finally { _loopLabels = outerLabels; _loopBarrier = outerBarrier; }
    }

    /// <summary>True when a `break` emitted here, with no loop of its own, would warn (bash: "only meaningful in a loop").</summary>
    private static bool LoopControlWarnsHere =>
        _loopLabels is not { Count: > 0 } && _loopBarrier != LoopBarrierKind.EndsSubshell
        && (_loopBarrier == LoopBarrierKind.Warn || _functionDepth > 0 || _context == TranspileContext.Default);

    /// <summary>
    /// The <see cref="PsBuild.LoopModeVar"/> assignment for a call site that runs eval/source text: what a `break` at the
    /// top of that text does here. Empty at the top of eval/source text itself, which passes its caller's mode on.
    /// </summary>
    private static string LoopModeForCallSite() => LoopModeAtCallSite() is var m and >= 0 ? PsBuild.LoopModeSet(m) : "";

    /// <summary>What a <c>break</c> run from here does (<see cref="PsBuild.RunTimeLoopControl(string, string)"/>): 0 the keyword, 1 warn, 2 end the subshell; -1 the caller's mode.</summary>
    private static int LoopModeAtCallSite() =>
        _loopLabels is { Count: > 0 } ? 0
        : _loopBarrier == LoopBarrierKind.EndsSubshell ? 2
        : LoopControlWarnsHere ? 1
        : -1;

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
    private static string NumericArgExit => _subshellDepth > 0 ? LeavingReturn("$global:LASTEXITCODE = 128; return") : ShellExit("128");

    /// <summary><c>break [N]</c> / <c>continue [N]</c> → the labelled PowerShell keyword.</summary>
    private static string EmitLoopControl(string keyword, Command.Simple cmd)
    {
        if (_loopLabels is not { Count: > 0 } labels)
        {
            // At the top of a script or in a function body with no loop of its own (bash 5.2 resets the loop level
            // on a function call, so a caller's loop is not reached) bash warns and carries on with status 0. A bare
            // PowerShell `break`/`continue` there ends the whole pipeline instead, or left the caller's loop.
            // `$( break )` / `<( break )` / a simple pipe stage inside a loop: bash's subshell keeps the loop level, so the
            // keyword ends that subshell with status 0 (the caught throw: the text need not be lexically in its block).
            string warning = PsBuild.LoopControlWarning(keyword);
            if (_loopBarrier == LoopBarrierKind.EndsSubshell)
                return PsBuild.SubshellExitThrow("0");
            if (LoopControlWarnsHere)
                return warning;
            // Eval/source/trap text at its top: the loop is its caller's, and whether there is one in reach is known
            // only at the call site, which sets the mode (LoopModeForCallSite).
            return PsBuild.RunTimeLoopControl(keyword, warning);
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
                    check = PsBuild.ShellDiagnosticLine(PsBuild.SingleQuote($"{keyword}: {countText}: numeric argument required")) + "; " + NumericArgExit + "; ";
            }
            else
            {
                string argText = EmitWord(cmd.Words[1]);
                check = $"$__psb_n = [string]({argText}); if ($__psb_n -notmatch '^[1-9][0-9]*$') {{ {PsBuild.ShellDiagnosticLine(PsBuild.SingleQuote(keyword + ": ") + " + $__psb_n + ': numeric argument required'")}; {NumericArgExit} }}; ";
            }
        }
        // `break 1 2 3`: bash reports "too many arguments", status 1, and drops the rest of the line, the loop with it
        // (PsBuild.UsageDiscard). Outside a line scope the innermost loop is left (a `continue` breaks too).
        if (cmd.Words.Length > 2)
        {
            check = PsBuild.UsageDiscard($"{keyword}: too many arguments") + "; ";
            keyword = "break";
        }
        var target = labels[Math.Max(0, labels.Count - n)];
        // bash: a `break`/`continue` that leaves is status 0 (`while true; do false; break; done` is 0); a bad count
        // set its own above.
        return (check ?? PsBuild.SetStatus(0) + "; ") + keyword + " " + (keyword == "break" ? target.Break : target.Continue);
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

    /// <summary>
    /// One pass of a loop binds its variable: the shell variable, or for a name the enclosing function holds as a
    /// lexical local, that PowerShell variable. Never a second, PowerShell copy beside the shell variable.
    /// </summary>
    private static string BindLoopVariable(string name, string valueExpr)
        => _lexicalLocals is not null && _lexicalLocals.Contains(name)
            ? $"${{{name}}} = [string]({valueExpr}); "
            : PsBuild.SetShellScalar(name, valueExpr) + "; ";

    /// <summary>Notes <paramref name="name"/> as bound by the loop being emitted; <see cref="LeaveLoopVariable"/> undoes it.</summary>
    private static (bool Added, string? Captured) EnterLoopVariable(string name)
    {
        bool added = (_storeLoopVars ??= new HashSet<string>(StringComparer.Ordinal)).Add(name);
        // In a background job's body, a loop that binds a captured name binds the job's own variable from then on.
        string? captured = _jobCapture is not null && _jobCapture.Remove(name, out var expr) ? expr : null;
        return (added, captured);
    }

    private static void LeaveLoopVariable(string name, (bool Added, string? Captured) entered)
    {
        if (entered.Added) _storeLoopVars!.Remove(name);
        if (entered.Captured is not null && _jobCapture is not null) _jobCapture[name] = entered.Captured;
    }

    /// <summary>
    /// In a background job's body: true when <paramref name="name"/> is read from the job's own copy (<c>$name</c>),
    /// noting that the job must be handed it.
    /// </summary>
    private static bool IsJobCaptured(string name)
    {
        if (_jobCapture is null || !_jobCapture.ContainsKey(name)) return false;
        _jobCaptureUsed!.Add(name);
        return true;
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

            // The iteration variable is private to the loop. The loop variable's one storage is the shell variable,
            // which each pass sets (BindLoopVariable): a PowerShell variable of the same name was a second storage that
            // a function called from the body read in place of its own `local` (environment-model.md section 8).
            string psVar = "__psbash_lv" + depth;
            string listPrelude = "";

            // `for i; do` (no `in`) iterates $@; `for i in ; do` has an explicit, empty list and runs zero times.
            if (forIn.List.IsEmpty && forIn.ExplicitIn)
            {
                sb.Append($"foreach (${psVar} in @()) {{ ");
            }
            else if (forIn.List.IsEmpty)
            {
                sb.Append($"foreach (${psVar} in {PsBuild.BuildPositionalExpansion("@")}) {{ ");
            }
            else
            {
                string list = FormatForInList(forIn.List);
                // `for k in "${(@k)a}"`: a word that fails to expand abandons the whole loop (status 1, no
                // iteration); the loop ran once over the empty word. Expand the list first, then test the mark.
                if (list.Contains(PsBuild.ExpansionErrorFunction, StringComparison.Ordinal)
                    || list.Contains("Invoke-BashArith", StringComparison.Ordinal)
                    || list.Contains("Get-BashIndirectValue", StringComparison.Ordinal))
                {
                    // Emptied first: a failglob error ends the assignment and would leave an earlier loop's list.
                    listPrelude = "$__psbash_fl = $null; " + PsBuild.ClearExpansionFailed + "; $__psbash_fl = " + list + "; ";
                    list = "$__psbash_fl";
                }
                sb.Append($"foreach (${psVar} in ");
                sb.Append(list);
                sb.Append(") { ");
            }

            sb.Append(IterGuardCheck(depth));
            sb.Append(DebugAt(forIn.Line)); // trap DEBUG: before each iteration, with the `for` line
            // The loop variable is an ordinary shell variable: the body's functions see it and it keeps its last value after the loop.
            // `for r in a b` where r is a nameref: each word becomes its target (bash), so from here on the body and what
            // follows read and write r through the run-time table. REGRESSION: the loop stored each word in r.
            if (IsAnyNameref(forIn.Var))
            {
                _namerefs?.Remove(forIn.Var); _pendingNamerefs?.Remove(forIn.Var);
                (_runtimeNamerefs ??= new HashSet<string>(VarNameKeys)).Add(forIn.Var);
                sb.Append(PsBuild.BindLoopNameref(forIn.Var, "$" + psVar));
            }
            else sb.Append(BindLoopVariable(forIn.Var, "$" + psVar));

            var entered = EnterLoopVariable(forIn.Var);
            // `for i; do` (no list) iterates "$@": arbitrary strings.
            bool addedSplit = (forIn.List.IsEmpty || ForListMaySplit(forIn.List)) && (_splitLoopVars ??= new HashSet<string>()).Add(forIn.Var);
            try
            {
                sb.Append(EmitBody(forIn.Body));
            }
            finally
            {
                if (addedSplit) _splitLoopVars!.Remove(forIn.Var);
                LeaveLoopVariable(forIn.Var, entered);
            }

            sb.Append(" }");
            string body = sb.ToString();
            // A list that globs can fail (failglob): then the loop's status is 1, not the zero-iteration 0.
            bool globs = (listPrelude + body).Contains("ConvertTo-BashGlob", StringComparison.Ordinal) || (listPrelude + body).Contains("ConvertTo-BashWords", StringComparison.Ordinal);
            body = body + "; " + (globs ? PsBuild.ZeroIterationStatusAfterGlob(IterVar(depth)) : PsBuild.ZeroIterationStatus(IterVar(depth)));
            // A list expanded ahead of the loop (it can fail) runs the loop only when it did not.
            if (listPrelude.Length > 0) body = listPrelude + PsBuild.UnlessExpansionFailed(body);
            return ApplyCompoundRedirects((globs ? PsBuild.FailGlobReset : "") + body, forIn.Redirects);
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
            if (IsGlobWord(list[0]) || IsUnquotedDefaultAltWord(list[0]) || IsUnquotedPositionalListWord(list[0]) || IsUnquotedQuotedAltWord(list[0]))
                return EmitUnquotedSplitWordArray(list[0]);

            // `for i in a$x` / `=$*=`: split like bash; it used to iterate once over the literal text `a$env:x`.
            if (UnquotedAffixExpansionIndex(list[0], ordinaryVar: true) is var affixAt and >= 0)
                return EmitAffixWordArray(list[0], affixAt);
            // `for w in -"${a[@]}"`: one item per element, the text glued to the first and last.
            if (IsQuotedArrayAffixWord(list[0]))
                return EmitUnquotedSplitWordArray(list[0]);

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
        new(@"^(\$(?:HOME|env:PWD|env:OLDPWD))([/\\][^$`""'()@{}]*)$");

    /// <summary>
    /// One <c>for</c>-list item. A word that glues text, quotes, variables and command substitutions together
    /// (<c>a"$y"b</c>) is one string value: flatten it. EmitWord renders it as command-mode text (<c>a"$env:y"b</c>),
    /// which <see cref="FormatForItem"/> single-quoted, so the loop saw the PowerShell code instead of the value.
    /// </summary>
    private static string ForListItem(CompoundWord word, string emitted)
    {
        // A static word (literals, `\x` escapes, '.', "." without expansions) is its VALUE, single-quoted. Never
        // re-quote EmitWord's command-mode text: `a\ b` renders as the bareword a` b, and '.' around it kept the
        // backtick (`for c in grep\ x` ran `grep` x`; `e\"f` became e`"f). Oracle: PsQuotingDifferentialTests.
        if (TryGetStaticArgValue(word) is { } value)
            return PsBuild.SingleQuote(value);
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
            return "(" + tildeWord.Groups[1].Value + " + " + PsBuild.SingleQuote(tildeWord.Groups[2].Value) + ")";
        // A bare prefix glued to a double-quoted part (`y="$x"`, `--opt="a b"`) is command-mode text: the quoted tail must still expand.
        var gluedQuote = BarePrefixDoubleQuotedItem.Match(item);
        if (gluedQuote.Success)
            return "(" + PsBuild.SingleQuote(gluedQuote.Groups[1].Value) + " + " + gluedQuote.Groups[2].Value + ")";
        if (c is '$' or '"' or '\'' or '(' or '@') return item;
        // Bare command-mode text with an expansion (`a$(...)`, `x$env:y`): single-quoted, the code was the value.
        if (item.IndexOf('$') >= 0 && item.IndexOfAny(['\'', '"']) < 0)
            return "\"" + item + "\"";
        // Only a canonical integer keeps its text bare in expression mode (`08`, `+5`, `1e5` are other numbers).
        if (PsBuild.IsBareIntegerValue(item))
            return item;
        return PsBuild.SingleQuote(item);
    }

    /// <summary>
    /// <c>select</c> (the interactive menu loop) is not supported by ps-bash. It used to be dropped silently (a block
    /// comment), so the loop body never ran and nothing said why; it now says so on stderr and the status is 2, as for
    /// any other construct the shell cannot run. The rest of the script still transpiles and runs.
    /// Spec: bash-feature-coverage.md (select, coproc).
    /// </summary>
    private static string EmitSelect(Command.Select sel)
        => PsBuild.NotSupported("select");

    private static string EmitForArith(Command.ForArith forArith)
    {
        int depth = _loopDepth++;
        string label = PushLoopLabel(depth);
        try
        {
            // The loop variable lives in the shell variable store, which Invoke-BashArith reads and writes in the init,
            // the condition and the step alike; the name is only noted as a loop variable (_storeLoopVars).
            string? loopVar = ExtractArithVar(forArith.Init);
            var entered = loopVar is null ? default : EnterLoopVariable(loopVar);
            try
            {
                if (forArith.Line > 0 && DebugActive)
                    return ApplyCompoundRedirects(EmitDebugForArith(forArith, depth, label), forArith.Redirects);
                // bash's loop status is the last body command's (0 when it never ran, or after `continue`/`break`), not
                // the step's or the failed condition's: saved per pass and restored after the loop.
                string st = "$__psbash_fst" + depth;
                // A failed clause (`for ((i=1/0;;))`, `for ((;1/0;))`) ends the loop with status 1.
                string failed = ForArithFailedVar(forArith, depth);
                var sb = new StringBuilder();
                sb.Append(IterGuardPrefix(depth)).Append(st).Append(" = 0; ");
                if (failed.Length > 0) sb.Append(failed).Append(" = $false; ");
                sb.Append(label).Append("for (");
                sb.Append(EmitForArithClause(forArith.Init, forArith.Line, failed));
                sb.Append("; ");
                string cond = EmitForArithCondition(forArith.Cond, forArith.Line, failed);
                sb.Append(failed.Length == 0 ? cond : cond.Length == 0 ? "-not " + failed : "-not " + failed + " -and (" + cond + ")");
                sb.Append("; ");
                sb.Append(EmitForArithClause(forArith.Step, forArith.Line, failed));
                sb.Append(") { ");
                sb.Append(IterGuardCheck(depth));
                sb.Append(st).Append(" = 0; ");
                sb.Append(EmitBody(forArith.Body));
                sb.Append("; ").Append(st).Append(" = $global:LASTEXITCODE }; ").Append(PsBuild.SetStatusTo(st));
                if (failed.Length > 0) sb.Append("; if (").Append(failed).Append(") { $global:LASTEXITCODE = 1 }");
                return ApplyCompoundRedirects(sb.ToString(), forArith.Redirects);
        }
        finally
        {
            if (loopVar is not null) LeaveLoopVariable(loopVar, entered);
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
    private static string EmitDebugForArith(Command.ForArith forArith, int depth, string label)
    {
        int line = forArith.Line;
        string first = "$__psbash_af" + depth;
        string failed = ForArithFailedVar(forArith, depth);
        // The loop's status is the last body command's, as in EmitForArith (the DEBUG form left the step's).
        string st = "$__psbash_fst" + depth;
        var sb = new StringBuilder();
        sb.Append(st).Append(" = 0; ");
        if (failed.Length > 0) sb.Append(failed).Append(" = $false; ");
        string init = EmitForArithClause(forArith.Init, line, failed);
        if (forArith.Init is not null) sb.Append(DebugAt(line));
        if (init.Length > 0) sb.Append(init).Append("; ");
        sb.Append(first).Append(" = $true; ");
        sb.Append(IterGuardPrefix(depth)).Append(label).Append("while ($true) { ");
        sb.Append(IterGuardCheck(depth));
        sb.Append("if (-not ").Append(first).Append(") { ");
        if (forArith.Step is not null)
            sb.Append(DebugAt(line)).Append(EmitForArithClause(forArith.Step, line, failed)).Append("; ");
        sb.Append("}; ").Append(first).Append(" = $false; ");
        if (failed.Length > 0) sb.Append("if (").Append(failed).Append(") { break }; ");
        // An empty condition is true (`for ((;;))`): nothing to test (`if (-not ())` did not parse).
        if (forArith.Cond is not null)
            sb.Append(DebugAt(line)).Append("if (-not (").Append(EmitForArithCondition(forArith.Cond, line, failed)).Append(")) { break }; ");
        sb.Append(st).Append(" = 0; ");
        sb.Append(EmitBody(forArith.Body));
        sb.Append("; ").Append(st).Append(" = $global:LASTEXITCODE }; ").Append(PsBuild.SetStatusTo(st));
        if (failed.Length > 0) sb.Append("; if (").Append(failed).Append(") { $global:LASTEXITCODE = 1 }");
        return sb.ToString();
    }

    private static string EmitWhile(Command.While whileCmd)
    {
        // Special case: while read VAR -> a labelled PowerShell loop over the lines (EmitWhileRead). It reads the shared
        // stdin cursor line by line (emitter-strategy.md "Stage stdin"), the same cursor `read`, `cat` and the other
        // readers in its body consume; only its own `< file` with a body that reads nothing iterates the records directly.
        if (IsWhileRead(whileCmd.Cond, out var readVar, out var readIfs))
        {
            // Its ONLY redirect is its own input (`done < f`, `<<< "$s"`, a heredoc): read the source
            // straight into the loop header. bash runs this loop in the CURRENT shell (only a pipe makes
            // a subshell), but the general `Get-Content f | & { … }` form put it in a script block, so a
            // `return` in a function's read loop left only that block (`echo never` still ran).
            // With its own input, a body that reads stdin too (`while read a; do read b; ...; done < f`, a `cat` or a nested
            // read loop in the body) takes from the SAME input as the loop, line by line: the input becomes a stdin scope
            // and the loop reads its lines through the shared cursor. A body that reads nothing keeps iterating the records.
            if (!whileCmd.Redirects.IsDefaultOrEmpty
                && whileCmd.Redirects is [{ Fd: 0, Op: "<" or "<<" or "<<<" } only])
            {
                string source = ReadLoopSource(only);
                bool bodyReads = false;
                string readLoop = EmitWhileRead(readVar, readIfs,
                    () => { var b = EmitReadLoopBody(whileCmd.Body); bodyReads = b.Reads; return b.Text; },
                    () => bodyReads ? ReadLoopFeed.Shared : new ReadLoopFeed(ReadLoopFeedKind.Records, source));
                // Inline (not a script block), so a `return` in a function's read loop still leaves the function.
                if (bodyReads)
                    readLoop = PsBuild.StdinScope(readLoop, "__psbash_rlin_prev" + _loopDepth, source);
                // `done < missing`: the redirect fails the loop (status 1, body never runs, and under set -e the script ends).
                if (only is { Op: "<" } && EmitInputRedirectTarget(only.Target) is var readTarget && readTarget != "$null")
                    readLoop = PsBuild.InputRedirectGuardInline(readTarget, readLoop, RedirectFailExit());
                return readLoop;
            }

            // Otherwise as any compound: its own input (`done < in > out`) becomes the stdin scope its lines come from.
            return ApplyCompoundRedirects(EmitCompoundBody(whileCmd.Redirects, () => EmitWhileRead(readVar, readIfs, whileCmd.Body)),
                whileCmd.Redirects);
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

            // `while true` / `until false`: the condition never ends the loop; it only sets the status the body starts with.
            if (!IsHoistedCondition(whileCmd.Cond) && whileCmd.Cond is Command.Simple { Words.Length: 1 } constant
                && constant.EnvPairs.IsEmpty && constant.Redirects.IsEmpty
                && IsConstantStatusBuiltin(constant, out var succeeds) && succeeds != whileCmd.IsUntil)
            {
                sb.Append(label).Append("while ($true) { ").Append(IterGuardCheck(depth));
                sb.Append(PsBuild.SetStatus(succeeds ? 0 : 1)).Append("; ").Append(EmitBody(whileCmd.Body)).Append(" }");
                return ApplyCompoundRedirects(sb.ToString(), whileCmd.Redirects);
            }

            // The condition is tested at the top of each pass (a hoisted one runs as a statement, so its output streams;
            // `continue` re-runs it, as bash does) and sets the status the body starts with. bash's loop status is the
            // last body command's, or 0 when the body never ran, never the failed condition's: the body's status is
            // saved per pass (0 before it, so a `continue` leaves 0) and restored when the condition ends the loop.
            string st = "$__psbash_wst" + depth;
            sb.Append(st).Append(" = 0; ").Append(label).Append("while ($true) { ");
            sb.Append(IterGuardCheck(depth));
            if (IsHoistedCondition(whileCmd.Cond))
            {
                sb.Append(EmitHoistedCondition(whileCmd.Cond));
                sb.Append("if (").Append(PsBuild.LastStatusTest(negate: !whileCmd.IsUntil)).Append(") { ");
            }
            else
            {
                string cond = EmitExpressionCondition(whileCmd.Cond);
                sb.Append(whileCmd.IsUntil ? "if (" + cond + ") { " : "if (-not (" + cond + ")) { ");
            }
            sb.Append(PsBuild.SetStatusTo(st)).Append("; break }; ");
            sb.Append(st).Append(" = 0; ");
            sb.Append(EmitBody(whileCmd.Body));
            sb.Append("; ").Append(st).Append(" = $global:LASTEXITCODE }");
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
                case WordPart.GlobPart g:
                    if (IsDynamicExtglob(g.Pattern)) return false;
                    sb.Append(g.Pattern);
                    break;
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

    /// <summary>An extglob (<c>@(a|$b)</c>) whose arms hold an expansion or a backtick substitution.</summary>
    private static bool IsDynamicExtglob(string pattern) =>
        pattern.Length > 1 && pattern[1] == '(' && pattern.AsSpan().IndexOfAny('$', '`') >= 0;

    /// <summary>
    /// An extglob's text cut into its operators (<c>@(</c>, <c>|</c>, <c>)</c>, nested <c>!(</c> ...) and the
    /// arm text between them. Quotes, backslash escapes, <c>$( )</c>, <c>${ }</c>, backticks and bracket
    /// expressions are kept whole inside an arm.
    /// </summary>
    internal static List<(string Text, bool IsOperator)> SplitExtglob(string pattern)
    {
        var result = new List<(string, bool)>();
        var arm = new StringBuilder();
        int depth = 0;
        void FlushArm()
        {
            if (arm.Length == 0) return;
            result.Add((arm.ToString(), false));
            arm.Clear();
        }
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c is '?' or '*' or '+' or '@' or '!' && i + 1 < pattern.Length && pattern[i + 1] == '(')
            {
                FlushArm();
                result.Add((pattern.Substring(i, 2), true));
                depth++;
                i++;
                continue;
            }
            if (depth > 0 && c is '|' or ')')
            {
                FlushArm();
                result.Add((c.ToString(), true));
                if (c == ')') depth--;
                continue;
            }
            int end = i;
            switch (c)
            {
                case '\\':
                    end = Math.Min(i + 1, pattern.Length - 1);
                    break;
                case '\'':
                    end = pattern.IndexOf('\'', i + 1);
                    break;
                case '`':
                    end = pattern.IndexOf('`', i + 1);
                    break;
                case '"':
                    for (end = i + 1; end < pattern.Length && pattern[end] != '"'; end++)
                        if (pattern[end] == '\\') end++;
                    break;
                case '$' when i + 1 < pattern.Length && pattern[i + 1] is '(' or '{':
                {
                    char open = pattern[i + 1], close = open == '(' ? ')' : '}';
                    int nest = 0;
                    for (end = i + 1; end < pattern.Length; end++)
                    {
                        if (pattern[end] == open) nest++;
                        else if (pattern[end] == close && --nest == 0) break;
                    }
                    break;
                }
                case '[':
                {
                    int close = pattern.IndexOf(']', i + 2 < pattern.Length ? i + 2 : i + 1);
                    end = close > 0 ? close : i;
                    break;
                }
            }
            if (end < i || end >= pattern.Length) end = pattern.Length - 1;
            arm.Append(pattern, i, end - i + 1);
            i = end;
        }
        FlushArm();
        return result;
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
        var subject = EmitCaseExpr(caseCmd.Expr);
        // `case $((42/0)) in`: a subject that fails to expand abandons the whole case (status 1, no arm runs); the
        // switch ran its `*)` arm over the empty word. Expand it first, then test the mark the runtime left.
        bool subjectCanFail = subject.Contains("Invoke-BashArith", StringComparison.Ordinal)
            || subject.Contains(PsBuild.ExpansionErrorFunction, StringComparison.Ordinal)
            || subject.Contains("Get-BashIndirectValue", StringComparison.Ordinal);
        string subjectPrelude = "";
        if (subjectCanFail)
        {
            subjectPrelude = PsBuild.ClearExpansionFailed + "; $__psbash_cx = " + subject + "; ";
            subject = "$__psbash_cx";
        }
        var sb = new StringBuilder("switch (");
        sb.Append(subject);
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

        // bash: a case that matches no pattern is status 0, not the status from before it.
        sb.Append(" default { ").Append(PsBuild.SetStatus(0)).Append(" } }");
        var caseText = subjectCanFail ? subjectPrelude + PsBuild.UnlessExpansionFailed(sb.ToString()) : sb.ToString();
        return ApplyCompoundRedirects(caseText, caseCmd.Redirects);
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
        // An arm that runs no command (`x) ;;`) is status 0, not the status from before the case.
        if (sb.ToString().AsSpan().Trim(" ;").IsEmpty)
            sb.Clear().Append(PsBuild.SetStatus(0));
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
        return dotSource ? DotStdinBlock(body) : "& { " + PsBuild.StdinScope(body) + " }";
    }

    /// <summary>A body run in this shell with the block's pipe as its stdin scope (eval / source text fed a file).</summary>
    private static string DotStdinBlock(string body) =>
        ". { " + PsBuild.StdinScope(body, "__psbash_evin_prev" + (++_evalStdinSeq)) + " }";

    /// <summary>
    /// <paramref name="target"/>, or for an <see cref="PsBuild.ArraySlot"/> the slot bound once (<see cref="PsBuild.BindArraySlot"/>,
    /// a statement appended to <paramref name="sb"/>): the write mentions its target up to four times, each a run-time lookup.
    /// </summary>
    private static string BindArrayTarget(StringBuilder sb, string target)
    {
        if (!PsBuild.IsArraySlot(target) || target == PsBuild.BoundArraySlot) return target;
        sb.Append(PsBuild.BindArraySlot(target)).Append("; ");
        return PsBuild.BoundArraySlot;
    }

    private static string ArrayWriteTarget(string name) =>
        ResolveNameref(name) is var resolved && resolved != name ? ArrayWriteTarget(resolved) :
        // eval'd text runs in the calling function's scope: an array it assigns is the shell's own unless that function declared it `local`.
        _context == TranspileContext.Eval && _functionDepth == 0 && _subshellDepth == 0
            ? (EvalLocalNames?.Contains(name) == true ? "$" + name : "$global:" + name) :
        // Not this function's local: a calling function's local array of the name, else the global (bash's dynamic scope);
        // in a function defined inside a subshell, the subshell's own array (a subshell body's `$name` is its copy already).
        _functionDepth > _subshellFunctionDepth && _functionLocalNames is { } locals && !locals.Contains(name)
            ? PsBuild.ArraySlot(name)
        // A lastpipe stage runs in the shell, but inside the stage's `& { }`: `... | while read l; do a+=("$l"); done`
        // must append to the shell's array, not to one local to that block.
        : _lastpipeStageDepth > 0 && _functionDepth == 0 && _subshellDepth == 0
            ? "$global:" + name
            : "$" + name;

    /// <summary>Above 0 while the last stage of a pipeline is emitted under <c>shopt -s lastpipe</c>.</summary>
    [ThreadStatic] private static int _lastpipeStageDepth;

    private static string EmitFunction(Command.ShFunction func)
    {
        // `set -o posix`: a function may not take a special builtin's name (bash ends the shell, status 2).
        if (_scriptPosix && !ReferenceEquals(_posixSplitNode, func) && s_posixSpecialBuiltins.Contains(func.Name))
        {
            var priorNode = _posixSplitNode;
            _posixSplitNode = func;
            // A definition has no line of its own: bash names the line its body starts on (`t.sh: line 2: `export': ...`).
            string defLine = FirstLineIn(func.Body) is > 0 and var bodyLine ? PsBuild.SetCallLine(_lineBase + bodyLine) : "";
            try { return PsBuild.PosixElse(defLine + PsBuild.SpecialBuiltinRedefined(func.Name) + "; " + EndShell("2"), EmitFunction(func)); }
            finally { _posixSplitNode = priorNode; }
        }
        // `$foo-bar() { :; }` / `foo-$(x)() { :; }`: the name is not expanded, so it is not a valid identifier (status 1).
        if (func.Name.AsSpan().IndexOfAny('$', '`') >= 0)
            return $"Write-Error {PsBuild.SingleQuote("`" + func.Name + "': not a valid identifier")} -ErrorAction Continue; {PsBuild.SetStatus(1)}";
        (_unitFunctions ??= new HashSet<string>(StringComparer.Ordinal)).Add(func.Name);
        // A scalar `local` is dynamically scoped (bash): a function this one calls sees the local value.
        // So it lives in the shell variable itself (`$env:x`), saved on first `local` and restored when the
        // function exits — not in a lexical PowerShell variable the callee could never read.
        var dynLocals = new HashSet<string>(VarNameKeys);
        var priorCollect = _collectScalarLocals;
        _collectScalarLocals = dynLocals;
        List<string> localVars;
        try { localVars = CollectLocalVars(func.Body); }
        finally { _collectScalarLocals = priorCollect; }
        var priorDyn = _dynLocals;
        _dynLocals = dynLocals;
        var priorFunctionLocals = _functionLocalNames;
        var priorFunctionReadonly = _readonlyVars is null ? null : new HashSet<string>(_readonlyVars, VarNameKeys);
        // A local's attributes (`local -i n`, or a plain `local n` hiding the caller's -i) hold in this body only.
        var priorIntVars = _intVars is null ? null : new HashSet<string>(_intVars, VarNameKeys);
        var priorCaseVars = _caseVars is null ? null : new Dictionary<string, bool>(_caseVars, VarNameKeys);
        var priorLocalAttrNames = _localAttrNames;
        _localAttrNames = null;
        var priorNamerefs = _namerefs is null ? null : new Dictionary<string, string>(_namerefs, VarNameKeys);
        var priorPendingNamerefs = _pendingNamerefs is null ? null : new HashSet<string>(_pendingNamerefs, VarNameKeys);
        var priorRuntimeNamerefs = _runtimeNamerefs is null ? null : new HashSet<string>(_runtimeNamerefs, VarNameKeys);
        _functionLocalNames = new HashSet<string>(localVars.Concat(dynLocals), VarNameKeys);
        var vars = _lexicalLocals ??= new HashSet<string>(VarNameKeys);
        var added = new List<string>();
        foreach (var v in localVars)
            if (vars.Add(v)) added.Add(v);
        // A function body starts with no lexical loop: its `break` stays bare (dynamic, as bash).
        var outerLabels = _loopLabels;
        _loopLabels = null;
        var outerBarrier = _loopBarrier;
        _loopBarrier = LoopBarrierKind.None;

        try
        {
            // A function is typically a FILTER (`echo x | f`): its stdin is whatever it is called with, not the
            // launcher's. Under the launcher-stdin scope its body therefore reads `$input`/the pipe as before.
            _functionDepth++;
            int fnLeavingReturns = _leavingReturns;   // the body's returns leave the function, not a group around it
            // The body starts without the caller's ERR trap unless errtrace is on: the runtime puts it aside on entry
            // (PsBuild.FunctionPrologue -> ErrTrapInheritance), so a trap the body sets itself fires there.
            bool savedErrTrap = PsBuild.ErrTrapNeedsErrtrace; PsBuild.ErrTrapNeedsErrtrace = false;
            string body;
            // The body is emitted as inside a stdin scope; one PROVEN to read no stdin (ProbeStdinScope) comes out
            // unchanged. Any other (`indent() { sed 's/^/  /'; }`, `f() { read -r x; }`, but also `f() { g; }` or
            // `f() { python3 x.py; }`, whose callee may read) takes the pipe it is called with as its stdin
            // (PsBuild.FunctionStdinScope) and otherwise the stdin of whatever called it.
            string ComputeBody()
            {
                var probe = ProbeStdinScope(EmitFunctionBodyOnce);
                NoteFunctionStdin(func.Name, probe.Reads);
                if (probe.Reads)
                    return PsBuild.FunctionStdinScope(probe.Text);
                return _launcherStdin ? WithStdinScope(false, EmitFunctionBodyOnce) : EmitFunctionBodyOnce();
            }
            // The body is emitted more than once (stdin scope probe, nameref variants): each emission starts from the
            // read-only set the function was entered with. A `readonly x` later in the body is not yet in force at an
            // earlier `local x` / `x=1` (`f() { local x=1; readonly -p x; }` refused the local on the second emission).
            string EmitFunctionBodyOnce()
            {
                if (priorFunctionReadonly is not null) _readonlyVars = new HashSet<string>(priorFunctionReadonly, VarNameKeys);
                return EmitBody(func.Body);
            }
            try
            {
                body = FindDynamicNameref(func.Body) is { } dynamicNameref
                    ? EmitNamerefVariants(func.Name, dynamicNameref, ComputeBody)
                    : ComputeBody();
            }
            finally { _functionDepth--; PsBuild.ErrTrapNeedsErrtrace = savedErrTrap; _leavingReturns = fnLeavingReturns; }
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
            // every `$__lsd_*` slot it parked in this call. Only a body with such an eval pays for the scan. A sourced file
            // runs dot-sourced in this scope too: `main() { . ./lib.sh; }` with `local L=x` in lib.sh (it outlived main).
            if (body.Contains("$__psbash_eval_src", StringComparison.Ordinal) || body.Contains("Invoke-BashSource", StringComparison.Ordinal)
                // `c=local; $c v=1`, `command local v`: a builtin named at run time runs as its static form (BashBuiltins.RunAsSource).
                || body.Contains("Resolve-BashCommand", StringComparison.Ordinal) || body.Contains("Invoke-BashCommand", StringComparison.Ordinal))
                body = "try { " + body + " } finally { foreach ($__psbLsdV in @(Get-Variable -Name '__lsd_*' -Scope 0 -ErrorAction SilentlyContinue)) "
                     // An unset caller value is removed (a $null handed to SetEnvironmentVariable arrived as "": set, empty).
                     + "{ if ($null -ne $__psbLsdV.Value) { $__psbLsdN = $__psbLsdV.Name.Substring(6); "
                     + "$__psbLsdR = [PsBash.Cmdlets.VarFrameStack]::LeaveLocal($__psbLsdN, $__psbLsdV.Value.v, [PsBash.Cmdlets.BashVariableStore]::Get($__psbLsdN)); "
                     + "if ($null -eq $__psbLsdR) { Remove-Item -LiteralPath ('Env:' + $__psbLsdN) -ErrorAction SilentlyContinue } else { [PsBash.Cmdlets.BashVariableStore]::Set($__psbLsdN, $__psbLsdR) } } } }";
            var sb = new StringBuilder();
            // A PowerShell alias outranks a function of the same name (`r`, `h`, `diff`), so a bash function
            // that shares its name would never run; drop the alias first.
            if (PsDefaultAliases.Contains(func.Name))
                sb.Append("Remove-Item -LiteralPath ").Append(PsBuild.SingleQuote("Alias:" + func.Name)).Append(" -Force -ErrorAction SilentlyContinue; ");
            // `outer() { inner() { :; }; }` defines `inner` for the whole shell when `outer` runs; a PowerShell
            // function declared in a function body would die with it.
            // Eval and sourced text run in the scope of whatever ran them, often a function: `main() { . ./lib.sh; }`
            // defines lib.sh's functions for the whole shell too.
            sb.Append("function ").Append(_functionDepth > 0 || _context == TranspileContext.Eval ? "global:" : "").Append(func.Name);
            sb.Append(" {").Append(PsBuild.FunctionPrologue).Append(PsBuild.FunctionSourceMark(func.Source));
            // Its locals that may be arrays (PowerShell variables of this scope): a callee's array write reaches them.
            sb.Append(PsBuild.LocalArrays(localVars.Concat(dynLocals).Where(n => IsValidVarName(n) && !PowerShellOwnedVariables.Contains(n)).Distinct(StringComparer.Ordinal)));
            if (dynLocals.Count > 0)
            {
                // Each name's saved slot starts $null in THIS call (a bare read would see the caller's).
                foreach (var v in dynLocals) sb.Append($"$__lsd_{v} = $null; ");
                sb.Append("try { ").Append(body).Append(" } finally { ");
                foreach (var v in dynLocals)
                    sb.Append($"if ($null -ne $__lsd_{v}) {{ ").Append(PsBuild.RestoreLocal(v)).Append(" }; ");
                sb.Append('}');
            }
            else
                sb.Append(body);
            sb.Append(PsBuild.FunctionEpilogue).Append('}');
            // Where it was defined: its BASH_SOURCE entry and diagnostic name while it runs (FunctionSources).
            sb.Append("; ").Append(PsBuild.FunctionDefined(func.Name));
            return sb.ToString();
        }
        finally
        {
            _dynLocals = priorDyn;
            _functionLocalNames = priorFunctionLocals;
            if (priorFunctionReadonly is not null) _readonlyVars = priorFunctionReadonly;
            if (priorIntVars is not null && _intVars is not null) { _intVars.Clear(); _intVars.UnionWith(priorIntVars); }
            if (priorCaseVars is not null && _caseVars is not null)
            {
                _caseVars.Clear();
                foreach (var kv in priorCaseVars) _caseVars[kv.Key] = kv.Value;
            }
            if (_globalAttrsInFunctions is not null)
                foreach (var (gName, gLetter) in _globalAttrsInFunctions)
                    if (gLetter == 'i') _intVars?.Add(gName);
                    else if (_caseVars is not null) _caseVars[gName] = gLetter == 'u';
            _localAttrNames = priorLocalAttrNames;
            if (priorNamerefs is not null && _namerefs is not null)
            {
                _namerefs.Clear();
                foreach (var kv in priorNamerefs) _namerefs[kv.Key] = kv.Value;
            }
            // A `local -n r` with no target ends with the body like a local with one (`r=x` after the call is plain).
            if (_pendingNamerefs is not null)
            {
                _pendingNamerefs.Clear();
                if (priorPendingNamerefs is not null) _pendingNamerefs.UnionWith(priorPendingNamerefs);
            }
            if (_runtimeNamerefs is not null)
            {
                _runtimeNamerefs.Clear();
                if (priorRuntimeNamerefs is not null) _runtimeNamerefs.UnionWith(priorRuntimeNamerefs);
            }
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
              // An `exit` (or errexit) in the handler sets the subshell's status; a bare `exit` there uses the status from
              // before the handler; a `break` at its top warns (the handler is not in a loop).
              + "$__psbash_xs = $global:LASTEXITCODE; $global:__BashPreTrapStatus = $__psbash_xs; " + PsBuild.LoopModeSet(1)
              + "try { if ($global:__BashTrapEXIT) { & $global:__BashTrapEXIT } } catch [PsBash.Cmdlets.BashSubshellExitException] { $__psbash_xs = $_.Exception.Status } catch { }; "
              + "$global:__BashPreTrapStatus = $null; $global:LASTEXITCODE = $__psbash_xs }"
            : body;

    private static string EmitSubshell(Command.Subshell subshell)
    {
        // `exit` inside a subshell exits only the SUBSHELL and sets $? in the
        // parent — `(exit 7); echo $?` prints 7 and keeps going. Emitted as a bare
        // PowerShell `exit`, it terminated the WHOLE shell instead: the script
        // stopped and the `echo $?` never ran. Emit the body with the subshell
        // depth raised so `exit N` becomes a SCOPED `$LASTEXITCODE = N; return`,
        // and give that `return` a script block to return from.
        _subshellDepth++; var subRoOuter = EnterSubshellTables(); bool subRoViolation = _readonlyViolation; // `( r=2 ); echo` goes on
        int subLeavingReturns = _leavingReturns;   // the body's exits leave the subshell's own block, not a group around it
        var outerLabels = _loopLabels; var outerBarrier = _loopBarrier;
        _loopLabels = null; _loopBarrier = subshell.KeepsLoopLevel ? outerBarrier : LoopBarrierKind.Warn;
        // The ERR trap is put aside at run time (BashShellState.Save -> ErrTrapInheritance): `( trap 'echo err' ERR; false )`
        // fires the subshell's own trap, which a static errtrace test here kept from running.
        string body; bool savedErrTrapSh = PsBuild.ErrTrapNeedsErrtrace; PsBuild.ErrTrapNeedsErrtrace = false;
        // `( … ) < file` gives the body its own stdin (a scope); otherwise it inherits the enclosing one.
        try { body = EmitCompoundBody(subshell.Redirects, () => EmitBody(subshell.Body)); }
        finally { _subshellDepth--; LeaveSubshellTables(subRoOuter); _readonlyViolation = subRoViolation; PsBuild.ErrTrapNeedsErrtrace = savedErrTrapSh; _loopLabels = outerLabels; _loopBarrier = outerBarrier; _leavingReturns = subLeavingReturns; }

        // One subshell scope (PsBuild.ShellStateScope): a child PS scope plus save/restore of the
        // shell state a scope does not isolate, so nothing the body changes — `set -e`, variables,
        // `shopt`, traps, positionals — leaks into the parent. It is always a script block, which also
        // gives `exit` / an errexit failure their scoped `return` (ErrexitExitStatement). The working
        // directory is part of that state (BashShellState.RestoreWorkingDirectory), as it is for every subshell form.
        // A subshell starts with no EXIT trap (bash resets traps) and fires the one it sets when it ends:
        // `( trap 'echo bye' EXIT; echo body )` prints body, then bye. Only a body that calls `trap` pays for it.
        body = WithSubshellExitTrap(body);
        var sb = new StringBuilder(PsBuild.ShellStateScope(body));

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
                ? PsBuild.InputRedirectGuard(inTarget, $"Invoke-BashCat {inTarget} -RawStdinBytes | & {{ {PsBuild.StdinScope(result)} }}", RedirectFailExit(),
                    InputFailTail(subshell.Redirects, inputRedirect))
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
        var open = string.Join("; ", highFds.Select(r => $"Invoke-BashExecFd {r.Fd} {PsBuild.SingleQuote(r.OpenOperator)} {EmitArgWord(r.Target)}"));
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
        if (_inStdinScope) NoteStdinUse(simple);
        // A run-time command name (`$c`, `"$tool"`) may read stdin. Whether it does is known only once it resolves:
        // `-Fed` has the runtime feed the scope's queue to a reader and leave it for anything else. Piped
        // unconditionally, `printf`/`test` never ran on an empty queue and failed to bind a queued line.
        const string dynPrefix = "& (Resolve-BashCommand ";
        if (_inStdinScope && simple.EnvPairs.IsEmpty && simple.HereDocs.IsDefaultOrEmpty && !HasOwnStdin(simple.Redirects)
            && text.StartsWith(dynPrefix, StringComparison.Ordinal) && !PsBuild.IsStatementList(text))
            return dynPrefix + "-Fed " + text.Substring(dynPrefix.Length);
        // The same with a splatted operand (`f() { $SH --norc "$@"; }`): the head is resolved first inside `& { ... }`.
        const string dynHoist = "& { $__bashhead = (Resolve-BashCommand -Words ";
        if (_inStdinScope && simple.EnvPairs.IsEmpty && simple.HereDocs.IsDefaultOrEmpty && !HasOwnStdin(simple.Redirects)
            && text.StartsWith(dynHoist, StringComparison.Ordinal))
            return "& { $__bashhead = (Resolve-BashCommand -Fed -Words " + text.Substring(dynHoist.Length);
        if (!_inStdinScope
            || !simple.EnvPairs.IsEmpty
            || !simple.HereDocs.IsDefaultOrEmpty
            || HasOwnStdin(simple.Redirects)
            || simple.Words.IsDefaultOrEmpty
            || StdinOperands(simple) is not ({ } name, var args)
            || PsBuild.IsStatementList(text)
            || text.StartsWith("$(", StringComparison.Ordinal)
            || text.StartsWith("& {", StringComparison.Ordinal))
            return text;

        // `command cat` / `command -p sort`: the command it runs is the reader, and Invoke-BashCommand hands it the pipeline.
        if (name == "command")
            return StdinReaders.CommandTarget(args) is { } target && StdinReaders.ShouldFeed(target.Name, target.Args)
                ? PsBuild.StdinFeed + " | " + text
                : text;
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
    /// <c>Invoke-BashNohup</c> (<c>--psbash-out FILE [--psbash-append] [--psbash-err FILE|&amp;1 [--psbash-err-append]]</c>), which opens
    /// them for the child: <c>2&gt;</c> truncates its file and <c>2&gt;&gt;</c> appends, as the shell would have opened them.
    /// <c>nohup CMD &amp;</c> leads with <c>--psbash-bg</c>: only then does nohup return before CMD ends (a foreground
    /// <c>nohup CMD</c> waits and takes CMD's status, which is also what an unmarked <c>Invoke-BashNohup</c> does).
    /// </summary>
    private static Command.Simple RewriteNohup(Command.Simple s, bool background)
    {
        var keep = ImmutableArray.CreateBuilder<Redirect>();
        var lead = new List<CompoundWord>();
        if (background) lead.Add(LiteralWord("--psbash-bg"));
        CompoundWord? outTarget = null; bool append = false; CompoundWord? errTarget = null; bool errAppend = false; bool errToOut = false;
        foreach (var r in s.Redirects)
        {
            if (r.Op is ">" or ">>" or ">|" && r.Fd == 1) { outTarget = r.Target; append = r.Op == ">>"; }
            else if (r.Op is "&>" or "&>>") { outTarget = r.Target; append = r.Op == "&>>"; errToOut = true; }
            else if (r.Op == ">&" && r.Fd == 2 && GetLiteralValue(r.Target) == "1") errToOut = true;
            else if (r.Op is ">" or ">>" or ">|" && r.Fd == 2) { errTarget = r.Target; errAppend = r.Op == ">>"; }
            else keep.Add(r);
        }
        if (outTarget is not null)
        {
            lead.Add(LiteralWord("--psbash-out")); lead.Add(outTarget);
            if (append) lead.Add(LiteralWord("--psbash-append"));
            if (errToOut) { lead.Add(LiteralWord("--psbash-err")); lead.Add(LiteralWord("&1")); }
            else if (errTarget is not null)
            {
                lead.Add(LiteralWord("--psbash-err")); lead.Add(errTarget);
                if (errAppend) lead.Add(LiteralWord("--psbash-err-append"));
            }
        }
        else keep.AddRange(s.Redirects.Where(r => r.Fd == 1 || r.Fd == 2 || r.Op is "&>" or "&>>")); // stderr-only redirects keep the old behaviour
        if (outTarget is null && !background) return s;
        if (outTarget is null) keep = s.Redirects.ToBuilder();
        var words = ImmutableArray.CreateBuilder<CompoundWord>();
        words.Add(s.Words[0]); words.AddRange(lead); for (int i = 1; i < s.Words.Length; i++) words.Add(s.Words[i]);
        return s with { Words = words.ToImmutable(), Redirects = keep.ToImmutable() };
    }
    private static string EmitBackground(Command.Background bg)
    {
        // The job is a subshell (as EmitSubshell): `exit` and an errexit failure leave only the job, and the ERR trap
        // fires in it only under errtrace. `{ false; echo x; } &` under set -e printed x and the job's status was 0.
        _subshellDepth++; var subRoOuter = EnterSubshellTables();
        var outerLabels = _loopLabels; var outerBarrier = _loopBarrier;
        _loopLabels = null; _loopBarrier = LoopBarrierKind.Warn;
        bool savedErrTrap = PsBuild.ErrTrapNeedsErrtrace; PsBuild.ErrTrapNeedsErrtrace = true;
        // A function's lexical local the body reads is handed over at launch (the job's runspace has no such PowerShell
        // variable). A shell scalar, a loop variable included, is in the job's own store copied at the `&`.
        var outerCapture = _jobCapture; var outerCaptureUsed = _jobCaptureUsed;
        var capture = JobCaptureCandidates(outerCapture);
        _jobCapture = capture is null ? null : new Dictionary<string, string>(capture, StringComparer.Ordinal);
        _jobCaptureUsed = new HashSet<string>(StringComparer.Ordinal);
        string inner;
        HashSet<string> captured;
        try { inner = Emit(bg.Inner); captured = _jobCaptureUsed; }
        finally { _subshellDepth--; LeaveSubshellTables(subRoOuter); PsBuild.ErrTrapNeedsErrtrace = savedErrTrap; _loopLabels = outerLabels; _loopBarrier = outerBarrier; _jobCapture = outerCapture; _jobCaptureUsed = outerCaptureUsed; }
        // A job inside a job passes on the copy the outer job holds, which the outer job must then be handed too.
        if (outerCapture is not null)
            foreach (var v in captured)
                if (outerCapture.ContainsKey(v)) outerCaptureUsed!.Add(v);
        // A job that can change shell state (variables, cwd, options, functions) must not share this
        // process's environment and cwd: it runs in a host of its own (-Isolated). A job that only runs
        // programs keeps the cheap in-process path, whose text is unchanged.
        string vars = captured.Count == 0 || capture is null
            ? ""
            : " -Vars @{ " + string.Join("; ", captured.Order(StringComparer.Ordinal).Select(v => $"{PsBuild.SingleQuote(v)} = {capture[v]}")) + " }";
        // A job whose stdout/stderr are the shell's own writes them live (CollectLiveJobs).
        string live = _liveJobs is not null && _liveJobs.Contains(bg) ? " -Live" : "";
        // The job's command as written: `fg` prints it when it brings the job back (PsBuild.JobText).
        string text = PsBuild.JobText(bg.Source);
        return MayMutateShellState(bg.Inner)
            ? $"Invoke-BashBackground -Isolated{live}{vars} {{ {inner} }}{text}"
            : $"Invoke-BashBackground{live}{vars} {{ {inner} }}{text}";
    }

    /// <summary>
    /// The names a background job started here reads from a PowerShell copy of its own, each with the PowerShell
    /// expression of its value now: a function's lexical local (a PowerShell variable the job's runspace does not have).
    /// A PowerShell-owned name (<c>$input</c>, <c>$args</c>) cannot be a copy; the job reads the shell variable.
    /// <para>
    /// <b>Invariant</b>: a shell scalar (a loop variable included) is never captured here. The job's own scalar store,
    /// copied at the <c>&amp;</c> (ScalarOverlay in-process, the environment block of a job host), already holds the
    /// loop's value at launch, and it is the one storage the job's assignments write: a second, PowerShell copy was read
    /// in place of the job's own <c>i=9</c> (docs/specs/background-jobs.md section 3). Guard: <c>JobScalarCaptureTests</c>.
    /// </para>
    /// </summary>
    private static Dictionary<string, string>? JobCaptureCandidates(Dictionary<string, string>? outer)
    {
        Dictionary<string, string>? map = null;
        if (_lexicalLocals is not null)
            foreach (var v in _lexicalLocals)
                if (!PowerShellOwnedVariables.Contains(v))
                    (map ??= new(StringComparer.Ordinal))[v] = "$" + v;
        if (outer is not null)
            foreach (var kv in outer)
                (map ??= new(StringComparer.Ordinal)).TryAdd(kv.Key, "$" + kv.Key);
        return map;
    }

    private static readonly HashSet<string> _stateBuiltins = new(StringComparer.Ordinal)
    {
        "cd", "pushd", "popd", "export", "unset", "declare", "typeset", "local", "readonly", "set", "shopt",
        "read", "source", ".", "eval", "exec", "alias", "unalias", "trap", "umask", "mapfile", "readarray",
        "getopts", "let", "shift", "enable", "hash", "builtin", "command", "ulimit", "wait", "fg", "bg", "history", "fc",
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
            || (_unitFunctions?.Contains(name) ?? false)
            // `echo ${bar=2} &`: expanding a word assigns (the job's copy only, in bash).
            || s.Words.Any(MutatesShellState),
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
    /// <param name="arith">The command.</param>
    /// <param name="label">The builtin bash names in an error: <c>((</c> (<c>s.sh: line 3: ((: 1/0 : division by 0 (error
    /// token is "0 ")</c>), or <c>let</c> for the emitter's static <c>let</c>.</param>
    private static string EmitArithCommand(Command.ArithCommand arith, string label = ArithCommandLabel)
        => PsBuild.SilentExitFromBool($"@({EmitArithmeticInvocation(arith.Expr, label)}) -ne 0");

    /// <summary>The name bash gives the arithmetic command (and a <c>for ((...))</c> clause) in an error: <c>((: EXPR: ...</c>.</summary>
    private const string ArithCommandLabel = "((";

    /// <summary>
    /// Boolean expression for <c>(( expr ))</c> used as an <c>if</c>/<c>while</c>
    /// condition: true iff the evaluated result is non-zero.
    /// </summary>
    private static string EmitArithConditionExpr(ArithmeticSyntax syntax)
        => $"(@({EmitArithmeticInvocation(syntax, ArithCommandLabel)}) -ne 0)"; // a failed evaluation yields no value: false, status 1

    /// <summary>
    /// <c>true</c> / <c>:</c> (<paramref name="succeeds"/>) or <c>false</c> as the BUILTIN: the emitter may stand a constant
    /// status in for it (<c>if true</c> -> <c>$true</c>, <c>| true</c> -> Out-Null). Not when the script defines a function
    /// of that name: bash runs the function (<c>true() { return 1; }; if true</c> takes the else branch).
    /// </summary>
    private static bool IsConstantStatusBuiltin(Command.Simple s, out bool succeeds)
    {
        succeeds = false;
        if (s.Words.IsDefaultOrEmpty || GetLiteralValue(s.Words[0]) is not ("true" or ":" or "false") || _definedFunctions?.Contains(GetLiteralValue(s.Words[0])!) == true)
            return false;
        succeeds = GetLiteralValue(s.Words[0]) != "false";
        return true;
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
            // `read -d DELIM` reads records, not lines, and every other flag that takes a value (`-u FD`, `-n N`, `-N N`,
            // `-t SECS`, `-p PROMPT`, `-a ARR`, `-i TEXT`) changes where or what is read: the generic loop
            // (Invoke-BashRead) handles them. Taken for a plain line loop, the flag's VALUE became a variable name
            // (`while read -u 3 a` read stdin into `$3`).
            if (val is not null && val.Length > 1 && val[0] == '-' && val[1] != '-' && val.AsSpan(1).IndexOfAny("adinNptu") >= 0)
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
    /// (<see cref="ReadLoopSource"/>); null = the shared stdin cursor.</param>
    /// <param name="recordTail">With <paramref name="sourceExpr"/>: a statement run after each record's lines are all done
    /// (skipped by a <c>break</c> or <c>return</c> out of the record).</param>
    private static string EmitWhileRead(List<string> varNames, string? ifs, Command body, string? sourceExpr = null, string? recordTail = null) =>
        EmitWhileRead(varNames, ifs, () => EmitBody(body),
            () => sourceExpr is not null ? new ReadLoopFeed(ReadLoopFeedKind.Records, sourceExpr, recordTail) : ReadLoopFeed.Shared);

    /// <summary>Where a read loop takes its input from.</summary>
    private enum ReadLoopFeedKind
    {
        /// <summary>The shared stdin (<c>$global:__BashStdIn</c>), one line per pass (<see cref="PsBuild.SharedStdinNextLine"/>).</summary>
        Shared,
        /// <summary>An array expression of records (<see cref="ReadLoopSource"/>, or the one record of a streamed stage).</summary>
        Records,
    }

    /// <summary>A read loop's input; <paramref name="RecordTail"/> runs after each record of <see cref="ReadLoopFeedKind.Records"/>.</summary>
    private readonly record struct ReadLoopFeed(ReadLoopFeedKind Kind, string? Records = null, string? RecordTail = null)
    {
        public static ReadLoopFeed Shared => new(ReadLoopFeedKind.Shared);
    }

    /// <summary>
    /// A read loop's body, emitted as inside a stdin scope first (as a function body is): unless it is PROVEN to read
    /// nothing (<see cref="ProbeStdinScope"/>: a <c>read</c>, a fed <c>cat</c>/<c>head</c>, a nested read loop, but also
    /// anything the emitter cannot see into -- a function, a program, eval / source text, a background job) it keeps that
    /// text and the loop must read the same stdin, line by line. A body proven to read nothing is emitted again outside
    /// any scope, as it always was (inside a scope no fused pipeline is formed, so the scoped text is not reused).
    /// </summary>
    private static (string Text, bool Reads) EmitReadLoopBody(Command body)
    {
        var probe = ProbeStdinScope(() => EmitBody(body));
        if (probe.Reads) return probe;
        return (WithStdinScope(false, () => EmitBody(body)), false);
    }

    /// <param name="varNames">The <c>read</c> variables, bound per line as loop variables.</param>
    /// <param name="ifs">The loop's <c>IFS=</c> prefix value, or null for the default field split.</param>
    /// <param name="emitBody">Emits the loop body (run inside the loop's labels and bindings).</param>
    /// <param name="feedFor">The loop's input, decided once the body has been emitted.</param>
    private static string EmitWhileRead(List<string> varNames, string? ifs, Func<string> emitBody, Func<ReadLoopFeed> feedFor)
    {
        // The `read` variables are this loop's bindings. Like every shell variable they live in the store, where the
        // body and the functions it calls read them; noted as loop variables, RC-7 word-splitting does not splat them
        // (a `read` var holds a single bound field, exactly like a for-loop variable).
        var entered = new List<(string Name, (bool Added, string? Captured) Entered)>(varNames.Count);
        foreach (var name in varNames)
            entered.Add((name, EnterLoopVariable(name)));

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
            bodyText = emitBody();
        }
        finally
        {
            PopLoopLabel();
            _loopDepth--;
            for (int i = entered.Count - 1; i >= 0; i--)
                LeaveLoopVariable(entered[i].Name, entered[i].Entered);
        }

        // Bind the read variable to the current line at the top of the loop body: the shell variable
        // (BindLoopVariable), which the body reads like any other. The
        // old approach text-rewrote every `$VAR` in the emitted body to `$_`, which
        // clobbered occurrences of the name inside single-quoted PS literals (e.g. a
        // bash `'has $line literal'` that must stay literal), corrupting output.
        // A lone variable still has the IFS whitespace (space, tab) trimmed from both ends; `IFS= read` keeps the line.
        var trimChars = new List<string>();
        if (ifs is null || ifs.Contains(' ')) trimChars.Add("32");
        if (ifs is null || ifs.Contains('\t')) trimChars.Add("9");
        string bind = varNames.Count == 1
            ? trimChars.Count == 0
                ? BindLoopVariable(varNames[0], "$_")
                : BindLoopVariable(varNames[0], $"$_.Trim([char[]]@({string.Join(",", trimChars)}))")
            : BuildReadFieldBindings(varNames, ifs);

        // The lines of one record: its BashText (null-safe), the trailing newline dropped, split.
        string rec = "$__psbash_rec" + depth;
        // `\z`, not `$`: .NET's `$` also matches before a final newline, so `a\n\n` lost its empty last line.
        string lines = $"@(($(if ($null -ne {rec} -and {rec}.PSObject.Properties['BashText']) {{ {rec}.BashText }} else {{ \"{rec}\" }}) -replace \"`n\\z\",\"\") -split \"`n\")";
        // Each pass starts after `read` succeeded: the body's `$?` is 0 (not the previous pass's last status).
        string inner = $":{lineLabel} foreach ($_ in {lines}) {{ {PsBuild.SetStatus(0)}; {bind}{bodyText} }}";

        var feed = feedFor();
        // Reading a shared stdin (a `read` or `cat` before the loop may already have advanced it), each pass takes ONE
        // line through the cursor every reader in the body advances too, so `read a` in the condition and `read b` in the
        // body pair the lines as in bash. A pass that took a whole record (one can carry several lines) left a body
        // reader only the records after it. The cursor is pulled per line: nothing is drained up front.
        // The `read` that finds no more input assigns "" to its variables (`while read q; do :; done <<< x; echo "[$q]"`
        // prints []), and a `break` leaves them as they were: the clearing ends a one-pass block that `break` leaves.
        string atEof = string.Concat(varNames.Select(v => BindLoopVariable(v, "''")));
        string loop;
        if (feed.Kind == ReadLoopFeedKind.Shared)
            loop = $":{recLabel}r while ($null -ne ({rec} = {PsBuild.SharedStdinNextLine})) {{ :{lineLabel} foreach ($_ in @({rec})) {{ {PsBuild.SetStatus(0)}; {bind}{bodyText} }} }}";
        else
            loop = feed.RecordTail is null
                ? $":{recLabel}r foreach ({rec} in {feed.Records}) {{ {inner} }}"
                : $":{recLabel}r foreach ({rec} in {feed.Records}) {{ {inner}; {feed.RecordTail} }}";
        return $":{recLabel} do {{ {loop}; {atEof}}} while ($false)";
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
            sb.Append(BindLoopVariable(varNames[i], $"$(if ($__psbash_readf.Count -gt {i}) {{ $__psbash_readf[{i}] }} else {{ '' }})"));
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

    /// <summary>
    /// The init or step clause of <c>for ((init; cond; step))</c> on line <paramref name="line"/>: a diagnostic in it
    /// names the <c>for</c>'s line (<c>s.sh: line 6: ((: i&lt;1/0: division by 0</c>) -- recorded in the clause, which
    /// runs again after the body recorded later lines.
    /// </summary>
    /// With <paramref name="failed"/>, the clause sets that variable to whether it failed: a failed evaluation yields no
    /// value (the condition's test, <see cref="EmitArithConditionExpr"/>), and bash then ends the loop with status 1.
    private static string EmitForArithClause(ArithmeticSyntax? syntax, int line, string failed = "")
    {
        if (syntax is null) return "";
        string evaluation = WithConditionLine(new Command.ArithCommand(syntax, line), EmitArithmeticInvocation(syntax, ArithCommandLabel));
        return failed.Length == 0 ? "$null = " + evaluation : failed + " = @(" + evaluation + ").Count -eq 0";
    }

    /// <summary>The variable that records a failed clause of a <c>for ((;;))</c> (none when all three are empty).</summary>
    private static string ForArithFailedVar(Command.ForArith forArith, int depth)
        => forArith.Init is null && forArith.Cond is null && forArith.Step is null ? "" : "$__psbash_afe" + depth;

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
    /// With <paramref name="failed"/>, a failed evaluation (no value: `for ((;1/0;))`) also sets that variable, so the
    /// loop ends with status 1 rather than the last body command's.
    private static string EmitForArithCondition(ArithmeticSyntax? syntax, int line, string failed = "")
    {
        if (syntax is null) return "";
        var cmd = new Command.ArithCommand(syntax, line);
        if (failed.Length == 0) return WithConditionLine(cmd, EmitArithConditionExpr(syntax));
        string values = failed + "v";
        return WithConditionLine(cmd, "(@(" + values + " = @(" + EmitArithmeticInvocation(syntax, ArithCommandLabel) + "); "
            + failed + " = " + values + ".Count -eq 0; " + values + ") -ne 0)");
    }

    /// <summary>
    /// Emit a condition expression for use inside <c>if</c>/<c>elif</c>.
    /// BoolExpr nodes are unwrapped (no outer parens); plain commands are emitted as-is.
    /// </summary>
    private static string EmitCondition(Command cond)
    {
        if (cond is Command.BoolExpr boolExpr)
            return WithConditionLine(cond, PsBuild.ConditionStatus(EmitBoolExprStatus(boolExpr)));

        if (cond is Command.Subshell subshell)
            return Emit(subshell.Body);

        // Bare true/:/false: a constant condition that still sets the status (PsBuild.ConstantStatusCondition)
        if (cond is Command.Simple simple && simple.Words.Length == 1
            && simple.EnvPairs.IsEmpty && simple.Redirects.IsEmpty)
        {
            if (IsConstantStatusBuiltin(simple, out var succeeds)) return PsBuild.ConstantStatusCondition(succeeds);
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
            return WithConditionLine(cond, PsBuild.ConditionStatus(PsBuild.BoolStatus(EmitArithConditionExpr(arithCmd.Expr))));

        return Emit(cond);
    }

    /// <summary>
    /// A condition that is an expression, not a hoisted statement (<c>if (( 1/0 ))</c>, <c>while [[ $a -eq 1 ]]</c>,
    /// a <c>for ((;;))</c> clause): its diagnostic names its own line, and a loop tests it again after its body recorded
    /// later lines, so the line is recorded inside the expression (as <see cref="EmitHoistedCondition"/> does before a
    /// hoisted one).
    /// </summary>
    private static string WithConditionLine(Command cond, string expr)
    {
        string record = LateLineRecord(RecordLineFor(cond, statement: false, out int lateLine), lateLine, expr);
        return record.Length == 0 ? expr : PsBuild.Subexpr(record + expr);
    }

    /// <summary>
    /// Converts a Command to a PowerShell expression that evaluates to a boolean,
    /// suitable for use inside if/while condition parentheses.
    /// </summary>
    private static string EmitConditionAsExpr(Command cmd)
    {
        // A test sets the status as it is evaluated (PsBuild.ConditionStatus), so `&&`/`||` leave the last one's.
        if (cmd is Command.BoolExpr boolExpr)
            return PsBuild.ConditionStatus(EmitBoolExprStatus(boolExpr));

        // Bare true/false
        if (cmd is Command.Simple simple && simple.Words.Length == 1
            && simple.EnvPairs.IsEmpty && simple.Redirects.IsEmpty)
        {
            if (IsConstantStatusBuiltin(simple, out var succeeds)) return PsBuild.ConstantStatusCondition(succeeds);
        }

        // Nested AndOrList: recurse
        if (cmd is Command.AndOrList nested)
            return EmitConditionAndOrList(nested);

        // Arithmetic (( expr )): non-zero result is true (see EmitCondition).
        if (cmd is Command.ArithCommand arithCmd)
            return PsBuild.ConditionStatus(PsBuild.BoolStatus(EmitArithConditionExpr(arithCmd.Expr)));

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
        if (HasBadOperandStatus(inner))
            return "$($global:LASTEXITCODE = " + BadOperandStatus(inner) + ")";
        return PsBuild.SilentExitFromBool("(" + inner + ")");
    }

    /// <summary>The status a <c>[ ]</c>/<c>[[ ]]</c> leaves, as an int expression (a condition's, through PsBuild.ConditionStatus).</summary>
    private static string EmitBoolExprStatus(Command.BoolExpr expr)
    {
        string inner = EmitBoolExprInner(expr);
        return HasBadOperandStatus(inner) ? BadOperandStatus(inner) : PsBuild.BoolStatus(inner);
    }

    // A bad integer operand (`[ a -eq 1 ]`) is status 2, not just false.
    private static bool HasBadOperandStatus(string inner) => inner.Contains("Invoke-BashTestInt") || inner.Contains("__BashTestBad");

    private static string BadOperandStatus(string inner) =>
        "$($global:__BashTestBad = $false; $__psbTestR = (" + inner + "); if ($global:__BashTestBad) { 2 } elseif ($__psbTestR) { 0 } else { 1 })";

    private static string EmitBoolExprInner(Command.BoolExpr expr)
    {
        if (expr.Extended)
            return EmitExtendedTest(expr.Inner);
        if (HasRunTimeTestWords(expr.Inner) || HasOperatorShapedConnectorOperand(expr.Inner))
            return EmitRunTimeTest(expr.Inner);
        return TranslateTestCondition(expr.Inner, extended: false);
    }

    /// <summary>`[ -a -a -a -a -a -a ]`: two adjacent `-a` / `-o` words make one of them an operand or a dangling
    /// connector, a shape only bash's own grammar resolves (here `argument expected`, status 2); the static translation
    /// read every `-a` as a connector. The runtime `test` evaluates it.</summary>
    private static bool HasOperatorShapedConnectorOperand(ImmutableArray<CompoundWord> words)
    {
        for (int i = 1; i < words.Length; i++)
            if (GetLiteralValue(words[i - 1]) is "-a" or "-o" && GetLiteralValue(words[i]) is "-a" or "-o")
                return true;
        return false;
    }

    /// <summary>
    /// `[ -n $a ]`, `[ $e = '' ]`: `[` is a command, so an unquoted expansion is split into words (an empty one is
    /// none) before `test` sees them, and the expression's shape is only known at run time. With `a` empty,
    /// `[ -n $a ]` is `[ -n ]` (true) and `[ $e = '' ]` is `[ = '' ]` (status 2); both were compared as one
    /// operand. `$#`, `$?`, `$$` and `$!` never split. A glob word keeps the static path.
    /// A quoted `&lt;` / `&gt;` (ASCII string order) also goes to the runtime: the static path dropped it
    /// (`[ a '&lt;' b ]` emitted `('a'  'b')`), and `[ -z '&gt;' -- ]` is a binary `&gt;`, not `-z`.
    /// </summary>
    private static bool HasRunTimeTestWords(ImmutableArray<CompoundWord> words)
    {
        bool split = false, glob = false;
        foreach (var w in words)
        {
            if (TryGetPureLiteralText(w.Parts, out var lit) && lit is "<" or ">") return true;
            if (w.Parts.Any(p => p is WordPart.GlobPart)) { glob = true; continue; }
            if (w.Parts is [WordPart.SimpleVarSub { Name: "#" or "?" or "$" or "!" }]
                or [WordPart.BracedVarSub { Name: "#" or "?" or "$" or "!", Suffix: null }])
                continue;
            if (IsUnquotedSplitWord(w)) split = true;
        }
        return split && !glob;
    }

    /// <summary>The `[ ]` word list, IFS-split at run time, evaluated by the runtime `test` (status 2 flags a bad expression).</summary>
    private static string EmitRunTimeTest(ImmutableArray<CompoundWord> words)
    {
        var items = new List<string>(words.Length);
        foreach (var w in QuoteBareTestParens(WithLiteralParens(words)))
            items.Add(IsUnquotedSplitWord(w) ? EmitUnquotedSplitWordArray(w) : EmitTestOperand(w));
        return PsBuild.RunTimeTest(string.Join("; ", items));
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
            return PsBuild.RunTimeTest(a0 + "; " + EmitUnquotedSplitWordArray(words[1]) + "; " + a2);
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
                // A device name (`[ -e /dev/stdout ]`, `[ -c /dev/null ]`) is not a path to look up: the runtime answers
                // from the device (DevicePath), where Test-Path would stat C:\dev\stdout.
                if (TryGetPureLiteralText(words[1].Parts, out var deviceOperand) && DevicePath.Classify(deviceOperand, windows: false, out _) != DeviceKind.None)
                    return PsBuild.ExitCodeTest($"Invoke-BashTest {PsBuild.SingleQuote(flag)} {PsBuild.SingleQuote(deviceOperand)}", negate: false);
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
                // POSIX ERE -> .NET: `{,n}`, classes in a run-time value, and a stray `{` (a regcomp error, status 2).
                patExpr = PsBuild.EreRegex(patExpr);
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
                    // An operand's error is bash's `s.sh: line 3: [[: 1/0: division by 0 (error token is "0")`.
                    return PsBuild.ArithCompare($"Invoke-BashArith ([string]({ArithOperand(lhs)})) -Label '[['", op,
                        $"Invoke-BashArith ([string]({ArithOperand(EmitTestOperand(words[2]))})) -Label '[['");
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
        // `[[ -v name ]]` / `[[ -R name ]]` / `[[ -o opt ]]` (is-set / is-nameref / option-on) are the same unary operators; the cmdlet owns them.
        // The operand of -v is a variable NAME (`cfg[host]`, `a[1]`), never a glob: pass it as a literal.
        if (words.Length == 2 && GetLiteralValue(words[0]) == "-v" && TryGetVarOperandName(words[1]) is { } varOperand)
            return PsBuild.ExitCodeTest($"Invoke-BashTest '-v' {PsBuild.SingleQuote(varOperand)}", negate: false);
        if (!extended || (words.Length == 2 && GetLiteralValue(words[0]) is "-v" or "-R" or "-o"))
            return PsBuild.ExitCodeTestFlaggingBad(EmitPassthrough("Invoke-BashTest", QuoteBareTestParens(words)));

        var opName = GetLiteralValue(words[0]) ?? "";
        var message = PsBuild.SingleQuote($"[[: {opName}: unsupported test operator");
        return PsBuild.Subexpr($"Write-BashHostStderr ({PsBuild.DiagnosticPrefix} + {message}); $false");
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
        // `x\{1,2}`, `[[ x{1,2} == ... ]]`: a literal whose argument form backtick-escapes a brace was then
        // single-quoted backtick and all ('x`{1,2`}'), so it never equalled x{1,2}. Quote the literal text itself.
        if (emitted.Contains('`') && TryGetPureLiteralText(word.Parts, out var braceLiteral))
            return PsBuild.SingleQuote(braceLiteral);
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
                case WordPart.SingleQuoted or WordPart.EscapedLiteral or WordPart.DoubleQuoted or WordPart.AnsiCQuoted
                    or WordPart.SimpleVarSub or WordPart.BracedVarSub or WordPart.TildeSub:
                    needsBuilder = true;
                    break;
                default:
                    return false;
            }
        }
        if (!needsBuilder)
        {
            // An unquoted literal regex is its source text. Its emitted argument form is not: a leading @ (PowerShell's
            // splat sigil) is backtick-escaped there, so [[ @fx =~ @f(x) ]] matched the regex "`@f(x)".
            var text = new StringBuilder();
            foreach (var part in word.Parts)
                text.Append(part is WordPart.Literal l ? l.Value : ((WordPart.GlobPart)part).Pattern);
            patExpr = PsBuild.SingleQuote(text.ToString());
            return true;
        }

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
                case WordPart.AnsiCQuoted aq: // `$'|'` matches a literal |
                    pieces.Add(PsBuild.SingleQuote(System.Text.RegularExpressions.Regex.Escape(ExpandAnsiCEscapes(aq.Value))));
                    break;
                case WordPart.DoubleQuoted dq:
                    if (dq.Parts.IsDefaultOrEmpty) break;
                    pieces.Add("[regex]::Escape([string](" + EmitWord(new CompoundWord(ImmutableArray.Create<WordPart>(dq))) + "))");
                    break;
                case WordPart.TildeSub ts:
                    // `[[ $HOME =~ ~ ]]`: the tilde expands to the home directory, matched as text (HOME='^a$' matches itself).
                    pieces.Add(ts.User is null or "+" or "-"
                        ? "[regex]::Escape([string]" + EmitWordPart(ts) + ")"
                        : PsBuild.SingleQuote(System.Text.RegularExpressions.Regex.Escape("~" + ts.User)));
                    if (ts.SlashConsumed) pieces.Add("'/'");
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
        // A canonical integer stays bare so numeric -eq/-ne/-lt/-gt compare as numbers; `08`/`+5` bare would be 8/5.
        if (PsBuild.IsBareIntegerValue(emitted))
            return emitted;
        return PsBuild.SingleQuote(emitted);
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
            return PsBuild.SingleQuote(sb.ToString());
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
    /// <summary>
    /// The scalar value of <paramref name="name"/> an array write builds on (<c>s=x; s+=(y)</c>, <c>s[1]=y</c> keep x as
    /// element 0): the shell variable, or for a nameref whose target is known only at run time the target's
    /// (PsBuild.ReadThroughNameref), never r's own.
    /// </summary>
    private static string ScalarValueOf(string name, bool braced) =>
        IsRuntimeNameref(name) ? PsBuild.ReadThroughNameref(name) : braced ? "${env:" + name + "}" : "$env:" + name;

    private static string NamerefMark(string name, string target) =>
        "[PsBash.Cmdlets.BashShellState]::SetNameref(" + PsBuild.SingleQuote(name) + ", " + PsBuild.SingleQuote(target) + ")"
        // No target: it is named at run time, and the array forms of the name read `$name` (BashVariableLookup.ViewNameref).
        + (target.Length == 0 ? "; " + PsBuild.ViewNameref(name) : "");

    private static string EmitShAssignmentStatement(Command.ShAssignment cmd)
    {
        string text = EmitShAssignmentStatementCore(cmd);
        // `declare -A m=(...) 2>/dev/null`: the builtin's own redirects. A value's `$( )` runs before them (its stderr is
        // not redirected), so such a declaration keeps the old behavior of not applying them.
        // A bare `x=v > f` is no builtin: the assignment is made, then the redirect is opened (bash: a missing directory
        // reports the error and the status is 1, but x is still set).
        bool bare = !cmd.IsLocal && !cmd.IsExport && !cmd.IsDeclare && !cmd.IsReadonly;
        if (bare && !cmd.Redirects.IsDefaultOrEmpty && !ContainsCommandSub(cmd) && cmd.Redirects.All(r => r.Op is ">" or ">>"))
            text = text + "; " + PsBuild.SetStatus(0) + "; " + EmitNoOpRedirectTouches(new Command.Simple([], [], cmd.Redirects));
        else if (!cmd.Redirects.IsDefaultOrEmpty && !ContainsCommandSub(cmd)) text = ApplyCompoundRedirects(text, cmd.Redirects);
        if (!_runtimeReadonly || cmd.IsLocal || cmd.IsExport || cmd.IsDeclare) return text;
        var unknown = cmd.Pairs.Select(p => ResolveNameref(p.Name)).Select(n => n.Contains('[') ? n[..n.IndexOf('[')] : n)
            .Where(n => _readonlyVars?.Contains(n) != true).Distinct(StringComparer.Ordinal).ToList();
        return unknown.Count == 0 ? text : PsBuild.RuntimeReadonlyGuard(unknown, text);
    }

    private static string EmitShAssignmentStatementCore(Command.ShAssignment cmd)
    {
        string text = EmitShAssignment(cmd);
        // bash: a bare assignment resets $_ to empty.
        if (_scriptReadsLastArg && !cmd.IsLocal && !cmd.IsExport)
        {
            if (!cmd.IsDeclare) text += "; $global:BashLastArg = ''";
            else if (cmd.Pairs.Length > 0 && cmd.Pairs[^1].ArrayValue is not null) text += "; $global:BashLastArg = " + PsBuild.SingleQuote(cmd.Pairs[^1].Name);
        }
        if (text.Contains("circular name reference", StringComparison.Ordinal)) return text; // the refusal set status 1
        if (text.Contains(": bad array subscript'", StringComparison.Ordinal)) return text;   // so did this one
        if (text.Contains(": not a valid identifier'", StringComparison.Ordinal)) return text; // and a write through `ref -> a[0]`
        if (text.Contains(PsBuild.BadArithSubscriptMessage + "'", StringComparison.Ordinal)) return text; // the guard sets it either way
        if (NeedsArrayKindGuard(cmd)) return text; // each declaration sets its own status (1 when the kind change is refused)
        if (text.Contains(PsBuild.DeclareLocalArrayMark, StringComparison.Ordinal)) return text; // `local -A m`: so does this
        // `x=$((1/0))`: the failed expansion keeps status 1, and its mark must not abandon a LATER command.
        if (text.Contains("Invoke-BashArith", StringComparison.Ordinal) || text.Contains(PsBuild.ExpansionErrorFunction, StringComparison.Ordinal))
            return text + "; " + PsBuild.ClearExpansionFailed;
        // The script's last statement sets its exit status, which is read even when no `$?` is
        // (`nosuchcmd; len=0` exits 0, not 127).
        if ((!_scriptReadsStatus && !ReferenceEquals(cmd, _finalAssignment)) || ContainsCommandSub(cmd)) return text;
        if (cmd.Pairs.Any(p => _readonlyVars?.Contains(p.Name.Contains('[') ? p.Name[..p.Name.IndexOf('[')] : p.Name) == true || ResolveNameref(p.Name) != p.Name)) return text;
        // `a=(); a[-1]=1`: Set-BashElement reports a bad subscript with status 1, which a status set after it would
        // hide; start from 0 instead (unless the statement reads `$?` itself).
        if (cmd.Pairs.Any(p => p.Name.Contains('[')))
            return text.Contains("LASTEXITCODE", StringComparison.Ordinal) ? text : PsBuild.SetStatus(0) + "; " + text;
        // An array literal whose glob fails under failglob ends the statement with status 1; keep it.
        if (cmd.Pairs.Any(p => p.ArrayValue is not null)) return text + "; if ($?) { $global:LASTEXITCODE = 0 }";
        return text + "; $global:LASTEXITCODE = 0";
    }

    [ThreadStatic] private static bool _inArrayKindGuard;

    /// <summary>The script's last top-level statement when it is an assignment (its status is the script's).</summary>
    [ThreadStatic] private static Command.ShAssignment? _finalAssignment;

    /// <summary>
    /// `declare -A a=(...)` / `declare -a a=(...)` refuses to change the kind of an existing array (run-time check). Not
    /// in a function: there `declare` makes a new local.
    /// </summary>
    private static bool NeedsArrayKindGuard(Command.ShAssignment cmd) =>
        !_inArrayKindGuard && cmd.IsDeclare && !cmd.IsLocal && (cmd.IsAssoc || cmd.IsIndexed) && _functionDepth == 0;

    private static string EmitShAssignment(Command.ShAssignment cmd)
    {
        // `set -o posix; v=t export w=1`: export is a special builtin, so the prefix the parser folded away persists.
        if (_scriptPosix && cmd.IsExport && !cmd.TempPrefix.IsDefaultOrEmpty)
        {
            var plain = cmd with { TempPrefix = default };
            var prefix = cmd.TempPrefix.Select(a => new EnvPair(a.Name, a.Value)).ToImmutableArray();
            return PsBuild.PosixElse(EmitPersistentPrefix(prefix, () => EmitShAssignment(plain)), EmitShAssignment(plain));
        }
        if (!NeedsArrayKindGuard(cmd)) return EmitShAssignmentCore(cmd);
        _inArrayKindGuard = true;
        try
        {
            // The status is 1 when any name was refused (`declare -A a b=(1 2)` still declares b).
            var sb = new StringBuilder(PsBuild.SetStatus(0));
            foreach (var pair in cmd.Pairs)
            {
                sb.Append("; ");
                string one = EmitShAssignmentCore(cmd with { Pairs = [pair] });
                sb.Append(IsValidVarName(pair.Name) && ResolveNameref(pair.Name) == pair.Name
                    ? PsBuild.ArrayKindGuard(cmd.IsLocal ? "local" : "declare", pair.Name, cmd.IsAssoc, one)
                    : one);
            }
            return sb.ToString();
        }
        finally { _inArrayKindGuard = false; }
    }

    [ThreadStatic] private static Assignment? _keyOnlyGuardedPair;

    /// <summary>
    /// The array name of <c>NAME[SUB]</c> when SUB is static text (no <c>$</c> or backtick) that does not parse as an
    /// arithmetic expression (<c>'2'</c>, <c>0+</c>); otherwise null.
    /// </summary>
    private static string? IsNonArithmeticSubscript(string name)
    {
        int open = name.IndexOf('[');
        if (open <= 0 || name[^1] != ']' || !IsValidVarName(name[..open])) return null;
        string sub = name[(open + 1)..^1];
        if (sub.Length == 0 || sub is "@" or "*" || sub.AsSpan().IndexOfAny('$', '`') >= 0) return null;
        try { BashArithmeticParser.Parse(sub); return null; }
        catch (BashArithmeticParseException ex) when (!ex.Message.Contains(NestingGuard.TooDeepMessage, StringComparison.Ordinal)
                                                      && !ex.Message.Contains(NestingGuard.TooLongMessage, StringComparison.Ordinal))
        {
            return name[..open];
        }
    }

    private static string EmitShAssignmentCore(Command.ShAssignment cmd)
    {
        var sb = new StringBuilder();
        bool badSubscript = false;
        bool localRefused = false;
        for (int i = 0; i < cmd.Pairs.Length; i++)
        {
            if (i > 0)
                sb.Append("; ");

            var pair = cmd.Pairs[i];
            if (cmd.IsLocal && _functionDepth > 0 && pair.ArrayValue is null && IsValidVarName(pair.Name))
                ScopeLocalAttributes(pair.Name, cmd.Attrs);
            // `declare -n g=y; f() { local g=9; }`: f's g is a plain variable from here on, not the nameref (EmitFunction puts
            // the outer one back; at run time VarFrameStack.Local takes the nameref aside). REGRESSION: `g=9` wrote y.
            if (cmd.IsLocal && !cmd.IsNameref && _functionDepth > 0 && IsValidVarName(pair.Name) && _namerefs?.ContainsKey(pair.Name) == true)
                _namerefs.Remove(pair.Name);

            // `export a[7]=8` / `readonly a[7]=8`: these take no subscript (`local`/`declare` do).
            if ((cmd.IsExport || cmd.IsReadonly) && !cmd.IsLocal && !cmd.IsDeclare && pair.Name.Contains('['))
            {
                sb.Append("Write-Error ").Append(PsBuild.SingleQuote((cmd.IsExport ? "export" : "readonly") + ": `" + pair.Name + "': not a valid identifier"))
                  .Append(" -ErrorAction Continue");
                badSubscript = true;
                continue;
            }

            // `a['2']=3`, `a=x b[0+]=y c=z`: a subscript that is no arithmetic expression only works as an associative
            // key. On an indexed (or new) array it is an arithmetic syntax error, status 1: that write and the rest of
            // the assignments after it are not made. Decided when it runs (an array declared -A elsewhere takes the key).
            if (!ReferenceEquals(_keyOnlyGuardedPair, pair) && !cmd.IsLocal && !cmd.IsAssoc && !cmd.IsDeclare && !cmd.IsExport && !cmd.IsReadonly
                && pair.ArrayValue is null && IsNonArithmeticSubscript(pair.Name) is { } guardBase
                && ResolveNameref(guardBase) == guardBase && _assocVars?.Contains(guardBase) != true)
            {
                var priorGuarded = _keyOnlyGuardedPair;
                _keyOnlyGuardedPair = pair;
                string rest;
                try { rest = EmitShAssignmentCore(cmd with { Pairs = cmd.Pairs[i..] }); }
                finally { _keyOnlyGuardedPair = priorGuarded; }
                sb.Append(PsBuild.KeyOnlySubscriptGuard(guardBase, pair.Name, rest));
                break;
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
            // Any other write to a nameref with no target (`r=$(echo t)`, `r=$1`), or to one whose target is known only at
            // run time: the run time names the target on the first write and writes it after (BashShellState.WriteTarget,
            // the rule read, printf -v and getopts share). REGRESSION: only a literal `r=name` named it; `r=$v` stored v in r.
            if (!cmd.IsLocal && !cmd.IsExport && pair.ArrayValue is null && pair.Value is not null && !pair.Name.Contains('[')
                && pair.Op is AssignOp.Equal or AssignOp.PlusEqual && IsRuntimeNameref(pair.Name))
            {
                _pendingNamerefs?.Remove(pair.Name);
                (_runtimeNamerefs ??= new HashSet<string>(VarNameKeys)).Add(pair.Name);
                _litAssigned?.Remove(pair.Name);
                sb.Append(PsBuild.AssignThroughNameref(pair.Name, EmitAssignmentValue(pair.Value), pair.Op == AssignOp.PlusEqual));
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
                    var scriptBody = PsBuild.EscapeForSingleQuote(transpiledCmd);
                    // The variable too: $PROMPT_COMMAND reads it, and ps-bash's own prompt loops (REPL, piped -i) run it.
                    sb.Append("$env:PROMPT_COMMAND = ").Append(PsBuild.SingleQuote(rawCmd)).Append("; ");
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

            // `local -n r=t` / `declare -n r='a[2]'` in a function: a nameref local to it (EmitFunction restores the set). In a
            // function bash only warns about `local -n s=s` (status 0); a target that is no name is refused (status 1).
            // REGRESSION: a literal target was never recorded, so `r=6` wrote r, not t.
            if (cmd.IsLocal && cmd.IsNameref && _functionDepth > 0 && _namerefs is not null
                && pair.ArrayValue is null && pair.Op == AssignOp.Equal && IsValidVarName(pair.Name)
                && pair.Value is not null && ExtractSingleQuotedOrLiteralValue(pair.Value) is { } localTarget)
            {
                string builtin = cmd.Builtin ?? "local";
                if (localTarget == pair.Name)
                    sb.Append(PsBuild.ShellDiagnosticLine(PsBuild.SingleQuote($"{builtin}: warning: {pair.Name}: circular name reference")))
                      .Append("; ").Append(PsBuild.SetStatus(0));
                else if (!IsValidVarName(localTarget) && !IsNamerefElementTarget(localTarget))
                {
                    sb.Append(PsBuild.ShellDiagnosticLine(PsBuild.SingleQuote($"{builtin}: `{localTarget}': invalid variable name for name reference")))
                      .Append("; ").Append(PsBuild.SetStatus(1));
                    localRefused = true;
                }
                else
                {
                    _namerefs[pair.Name] = localTarget; _runtimeNamerefs?.Remove(pair.Name);
                    // The name is the frame's own, as for a plain local: the return restores the caller's value, also after
                    // `local +n r` turned r into a plain variable holding the target name.
                    // REGRESSION: `f() { local -n r=x; local +n r; }; f` left a global r=x.
                    if (_dynLocals?.Contains(pair.Name) == true)
                        sb.Append(PsBuild.RunTimeLocal(pair.Name, keepsValue: true, inWrittenFunction: true)).Append("; ");
                    sb.Append("[PsBash.Cmdlets.VarFrameStack]::LocalAttributes(").Append(PsBuild.SingleQuote(pair.Name)).Append("); ")
                      .Append(NamerefMark(pair.Name, localTarget)).Append("; ").Append(PsBuild.SetStatus(0));
                }
                continue;
            }
            // `local -n r` with no target, in a function: a nameref local to it, whose target the next literal assignment
            // names (`r=x` then reads and writes x); until then it hides an outer reference of that name and r is unset.
            // EmitFunction puts the static sets back when the body ends. REGRESSION: it was a plain local, so `r=x` set r.
            if (cmd.IsLocal && cmd.IsNameref && _functionDepth > 0 && _namerefs is not null
                && pair.ArrayValue is null && pair.Value is null && IsValidVarName(pair.Name))
            {
                _namerefs.Remove(pair.Name); _runtimeNamerefs?.Remove(pair.Name);
                (_pendingNamerefs ??= new HashSet<string>(VarNameKeys)).Add(pair.Name);
                if (_dynLocals?.Contains(pair.Name) == true)
                    sb.Append($"if ($null -eq $__lsd_{pair.Name}) {{ $__lsd_{pair.Name} = @{{ v = $env:{pair.Name} }} }}; $env:{pair.Name} = ")
                      .Append(PsBuild.DeclareLocal(pair.Name)).Append("; ");
                sb.Append("[PsBash.Cmdlets.VarFrameStack]::LocalAttributes(").Append(PsBuild.SingleQuote(pair.Name)).Append("); ")
                  .Append(NamerefMark(pair.Name, "")).Append("; ").Append(PsBuild.SetStatus(0));
                continue;
            }
            // `local -n r=$1` in a variant where r is already resolved to its target: nothing to declare.
            if (cmd.IsLocal && cmd.IsNameref && _namerefs is not null && _namerefs.ContainsKey(pair.Name))
            {
                sb.Append(PsBuild.SetStatus(0));
                continue;
            }
            // `local -n r=$1` / `local -n r="$name"`: a reference local to the function whose target is known only at run
            // time (PsBuild.DeclareNameref); every expansion and write of r then goes through the run time. REGRESSION: it
            // was a plain local holding the value, so `r=x`, `${r[@]}` and `r+=(z)` used r, not the variable it names.
            if (cmd.IsLocal && cmd.IsNameref && _functionDepth > 0 && _namerefs is not null
                && pair.ArrayValue is null && pair.Op == AssignOp.Equal && pair.Value is not null && IsValidVarName(pair.Name))
            {
                _namerefs.Remove(pair.Name); _pendingNamerefs?.Remove(pair.Name); _litAssigned?.Remove(pair.Name);
                (_runtimeNamerefs ??= new HashSet<string>(VarNameKeys)).Add(pair.Name);
                if (_dynLocals?.Contains(pair.Name) == true)
                    sb.Append($"if ($null -eq $__lsd_{pair.Name}) {{ $__lsd_{pair.Name} = @{{ v = $env:{pair.Name} }} }}; $env:{pair.Name} = ")
                      .Append(PsBuild.DeclareLocal(pair.Name)).Append("; ");
                sb.Append("[PsBash.Cmdlets.VarFrameStack]::LocalAttributes(").Append(PsBuild.SingleQuote(pair.Name)).Append("); ")
                  .Append(PsBuild.DeclareNameref(cmd.Builtin ?? "local", pair.Name, EmitAssignmentValue(pair.Value), inFunction: true));
                continue;
            }
            // `readonly y; f() { local x=1 y=2; }`: bash refuses the local (`local: y: readonly variable`, status 1).
            if (cmd.IsLocal && !cmd.IsNameref && pair.ArrayValue is null && !pair.Name.Contains('[') && _readonlyVars?.Contains(pair.Name) == true)
            {
                sb.Append(PsBuild.ShellDiagnosticLine(PsBuild.SingleQuote($"local: {pair.Name}: readonly variable")))
                  .Append("; ").Append(PsBuild.SetStatus(1));
                localRefused = true;
                continue;
            }
            // `local -r x=1`: later writes to x in this function are refused (the set is restored when the function ends).
            if (cmd.IsLocal && cmd.IsReadonly) _readonlyVars?.Add(pair.Name);
            // Mutually recursive namerefs (`ref1 -> ref2 -> ref1`) are only detected when written through.
            if (!cmd.IsLocal && !cmd.IsExport && _namerefs is { Count: > 0 } && _namerefs.ContainsKey(pair.Name) && _namerefs.ContainsKey(ResolveNameref(pair.Name)))
            {
                _readonlyViolation = true;   // a failed assignment: bash drops the rest of the line
                sb.Append(PsBuild.AssignmentError($"warning: {pair.Name}: circular name reference"));
                continue;
            }
            // `a[]=42`: an empty subscript is bash's 'bad array subscript' (status 1, and like a readonly write the
            // rest of the input line is dropped); it wrote element 0.
            if (pair.Name.EndsWith("[]", StringComparison.Ordinal) && pair.Name.IndexOf('[') == pair.Name.Length - 2)
            {
                _readonlyViolation = true;
                sb.Append(PsBuild.AssignmentError($"{pair.Name}: bad array subscript"));
                continue;
            }
            // `typeset -n ref='a[0]'` (or 'a[@]'): `ref[1]=x` / `ref=(x y)` would make the ELEMENT an array; bash refuses
            // (`a[0]': not a valid identifier, status 1) and, like any failed assignment, abandons the rest of the line.
            if (!cmd.IsLocal && (pair.ArrayValue is not null || pair.Name.Contains('['))
                && (pair.Name.Contains('[') ? pair.Name[..pair.Name.IndexOf('[')] : pair.Name) is var nrWriteBase
                && ResolveNameref(nrWriteBase) is var nrWriteTarget && nrWriteTarget != nrWriteBase && nrWriteTarget.Contains('['))
            {
                _readonlyViolation = true;
                sb.Append(PsBuild.AssignmentError($"`{nrWriteTarget}': not a valid identifier"));
                continue;
            }
            string roName = ResolveNameref(pair.Name); // a write through a nameref is a write to its target
            string roBase = roName.Contains('[') ? roName[..roName.IndexOf('[')] : roName;
            if (!cmd.IsLocal && !cmd.IsExport && _readonlyVars?.Contains(roBase) == true)
            {
                _readonlyViolation = true;
                sb.Append(PsBuild.AssignmentError($"{roBase}: readonly variable"));
                continue;
            }

            // `local -A m` / `local -a q` with no value, in a function: a local array of that kind (PsBuild.DeclareLocalArray).
            // REGRESSION: it was a scalar local, so `m[x]=1` evaluated x as arithmetic and wrote m[0].
            if (cmd.IsLocal && (cmd.IsAssoc || cmd.IsIndexed) && _functionDepth > 0 && pair.ArrayValue is null && pair.Value is null
                && IsValidVarName(pair.Name) && ResolveNameref(pair.Name) == pair.Name)
            {
                if (cmd.IsAssoc) _assocVars?.Add(pair.Name);
                _arrayVars?.Add(pair.Name);
                sb.Append(PsBuild.DeclareLocalArray(cmd.Builtin ?? "local", pair.Name, cmd.IsAssoc));
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
                bool writtenLocal = _dynLocals?.Contains(pair.Name) == true;
                if (pair.Op == AssignOp.PlusEqual && pair.Value is not null)
                    sb.Append("$__psbashLocBase = if (" + PsBuild.NotParked(pair.Name, writtenLocal) + ") { '' } else { $env:" + pair.Name + " }; ");
                // bash's frame rules (VarFrameStack): `local v` over a temp binding (`v=t f`) keeps its value, a second
                // `local v` in the same call keeps the local's, anything else leaves v unset; `unset` consults the frames.
                sb.Append(PsBuild.RunTimeLocal(pair.Name, keepsValue: pair.Value is not null, inWrittenFunction: writtenLocal));
                if (pair.Value is not null)
                {
                    sb.Append($"; $env:{pair.Name} = ");
                    string localAssigned = EmitAssignmentValue(pair.Value);
                    // `local s+=x` appends to the value already there (the caller's, or this function's earlier one).
                    sb.Append(pair.Op == AssignOp.Equal && _intVars?.Contains(pair.Name) == true
                        ? "[string](Invoke-BashArith ([string](" + localAssigned + ")))"
                        : pair.Op == AssignOp.PlusEqual
                            ? "$__psbashLocBase + (" + localAssigned + ")"
                            : CaseAttr(pair.Name, localAssigned));
                }
                // `local -i n`: declare -p shows the letters (VarFrameStack puts the caller's back at the return).
                if (cmd.Attrs is { } localAttrs)
                    foreach (char letter in localAttrs)
                        sb.Append("; [PsBash.Cmdlets.BashShellState]::MarkAttribute(").Append(PsBuild.SingleQuote(pair.Name)).Append(", '").Append(letter).Append("')");
                continue;
            }

            // A scalar is the shell variable unless the function holds the name as a lexical local (IFS, a local array):
            // a top-level `local` (no function to scope it) is not a PowerShell variable either.
            string varPrefix = cmd.IsLocal && (_lexicalLocals?.Contains(pair.Name) ?? false) ? "$" : "$env:";

            // Array assignment: arr=(a b c) -> $arr = @('a','b','c')
            // Array append:     arr+=('x')  -> $arr += @('x')
            if (pair.ArrayValue is not null)
            {
                // `typeset -n ref; ref=(x y)`: a nameref with no target stops being one (bash warns) and the array is ref's
                // own; one with a target takes the array (PsBuild.ArrayAssignToNameref). Known only at run time, so a pending
                // name is resolved there from now on.
                if (!(cmd.IsLocal || cmd.IsExport || cmd.IsReadonly || cmd.IsNameref || cmd.IsDeclare || cmd.IsAssoc || cmd.IsIndexed)
                    && IsRuntimeNameref(pair.Name))
                {
                    sb.Append(PsBuild.ArrayAssignToNameref(pair.Name)).Append("; ");
                    if (_pendingNamerefs?.Remove(pair.Name) == true) (_runtimeNamerefs ??= new HashSet<string>(VarNameKeys)).Add(pair.Name);
                }
                if (cmd.IsAssoc) _assocVars?.Add(pair.Name);
                if (IsValidVarName(pair.Name)) _arrayVars?.Add(pair.Name);
                bool knownAssoc = cmd.IsAssoc || _assocVars?.Contains(pair.Name) == true;
                // `a=([k]=-{a,b}-)` on an array that is not associative: bash brace-expands the element first, and the
                // words it makes (`[k]=-a-` `[k]=-b-`) are plain values, not subscripted ones.
                if (!knownAssoc && BraceExpandedKeyedElements(pair.ArrayValue) is { } plainElements)
                    pair = pair with { ArrayValue = plainElements };
                // A mixed `b=([n]=x [0]=z)` has a numeric key too: it takes this path (all-numeric ones the sparse literal below).
                bool keyedNumericOk = knownAssoc || pair.Op == AssignOp.Equal && HasNonNumericSubscript(pair.ArrayValue);
                string arrTarget = BindArrayTarget(sb, ArrayWriteTarget(pair.Name));
                if (pair.Op is AssignOp.Equal or AssignOp.PlusEqual && TryEmitAssocLiteral(pair.ArrayValue, out var assoc, out var assocAsIndexed, keyedNumericOk))
                {
                    // `a+=([k]=v)` merges into the existing table (a changed value keeps its place, a new key joins).
                    sb.Append(arrTarget).Append(" = ");
                    sb.Append(pair.Op == AssignOp.PlusEqual
                        ? "[PsBash.Cmdlets.BashAssoc]::Merge(" + PsBuild.ArrayAppendBase(arrTarget, ScalarValueOf(ResolveNameref(pair.Name), braced: true)) + ", " + assoc + ")"
                        // `a=([k1]=v1)` with no `declare -A a` in sight: indexed (arithmetic subscripts) unless a is
                        // associative at run time.
                        : knownAssoc ? assoc : PsBuild.AssocOrIndexed(arrTarget, assoc, assocAsIndexed));
                    continue;
                }
                // `a=([2]=p [5]=q)`: explicit numeric subscripts; the gaps stay UNSET (a sparse array), as in bash.
                if (pair.Op == AssignOp.Equal && !cmd.IsAssoc && _assocVars?.Contains(pair.Name) != true && TryEmitIndexedLiteral(pair.ArrayValue, out var indexed))
                {
                    sb.Append(arrTarget).Append(" = ").Append(indexed);
                    continue;
                }
                // `declare -A m=()` is an EMPTY ASSOCIATIVE array, not an empty indexed one.
                if (cmd.IsAssoc && pair.Op == AssignOp.Equal && pair.ArrayValue.Elements.IsEmpty)
                {
                    sb.Append(arrTarget).Append(" = [PsBash.Cmdlets.BashAssoc]::new()");
                    continue;
                }
                // `declare -A a; a=(k v k2 v2)` (bash 5.1+): bare words are key/value pairs, a lone last key gets "".
                // An explicit `-a` (`local -a m=(1 2)`) is indexed whatever else m is declared as.
                bool wordsArePairs = pair.Op == AssignOp.Equal && !cmd.IsIndexed && _assocVars?.Contains(pair.Name) == true;
                // eval'd text cannot see the caller's `declare -A a`: the run time picks pairs or list by a's kind.
                bool kindAtRunTime = pair.Op == AssignOp.Equal && !wordsArePairs && !cmd.IsAssoc && !cmd.IsDeclare
                    && _context == TranspileContext.Eval;
                sb.Append(arrTarget);
                // `a+=(x)` goes through Add-BashArray: a sparse array appends after its highest subscript,
                // which PowerShell's own `+=` cannot do on a dictionary.
                sb.Append(pair.Op == AssignOp.PlusEqual ? " = Add-BashArray " + PsBuild.ArrayAppendBase(arrTarget, ScalarValueOf(ResolveNameref(pair.Name), braced: true)) + " @("
                    : wordsArePairs ? " = [PsBash.Cmdlets.BashAssoc]::FromWords(@("
                    : kindAtRunTime ? " = [PsBash.Cmdlets.BashAssoc]::ListOrPairs(" + arrTarget + ", @("
                    : " = @(");
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
                sb.Append(wordsArePairs || kindAtRunTime ? "))" : ")");
                continue;
            }

            // The element as written (`b[$i]`, `ref[-9]`): a bad subscript is reported under this name.
            string writtenName = pair.Name;
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
                string target = BindArrayTarget(sb, ArrayWriteTarget(ResolveNameref(baseName)));
                if (IsValidVarName(baseName))
                {
                    _arrayVars?.Add(baseName); // a bare `$a` now reads `${a[0]}`
                    // `c=s; c[1]=t`: a scalar becomes element 0 of the array.
                    if ((target.StartsWith('$') || PsBuild.IsArraySlot(target)) && _assocVars?.Contains(baseName) != true && EnvShadowName(baseName) is null
                        && PsBuild.TryMapSpecialVar(baseName, braced: false, false) is null)
                        sb.Append(PsBuild.SeedArrayFromScalar(target, ScalarValueOf(baseName, braced: false))).Append("; ");
                }
                string key = FlattenPartsToDoubleQuotedString(BashParser.DecomposeWord(subscript.StartsWith('~') ? "\\" + subscript : subscript));
                // `a[a[0]=1]=X`: a subscript that assigns runs first, here, so the array read below already has its
                // write (Set-BashElement evaluated it on a copy of the array and returned that, losing a[0]=1).
                if (ArithAssignRx.IsMatch(subscript))
                {
                    sb.Append("$__psbK = ").Append(key).Append("; if (-not (").Append(target).Append(" -is [System.Collections.IDictionary] -and ")
                      .Append(target).Append(" -isnot [PsBash.Cmdlets.BashSparseArray])) { $__psbK = [string](Invoke-BashArith $__psbK) }; ");
                    key = "$__psbK";
                }
                sb.Append(target).Append(" = Set-BashElement ").Append(PsBuild.TargetArg(target)).Append(' ').Append(key).Append(' ')
                  .Append(EmitAssignmentValue(pair.Value));
                if (pair.Op == AssignOp.PlusEqual) sb.Append(" -Append");
                sb.Append(" -Written ").Append(PsBuild.SingleQuote(writtenName));
                continue;
            }

            // `a=(x y); a=z` / `a+=z`: a scalar write to an array writes element 0 (`${a[@]}` = z y).
            int scalarStart = sb.Length;
            bool toArray0 = !cmd.IsLocal && !cmd.IsExport && pair.Value is not null && _arrayVars?.Contains(pair.Name) == true
                            && _intVars?.Contains(pair.Name) != true && IsValidVarName(pair.Name) && ResolveNameref(pair.Name) == pair.Name;
            sb.Append(varPrefix);
            sb.Append(ResolveNameref(pair.Name));
            if (pair.Value is null)
            {
                // Bare export/local VAR with no value: ensure variable exists
                sb.Append(" = ");
                // `export SHELLOPTS`: the variable is computed (the options now on), so the environment gets that value. A child
                // shell turns each listed option on at startup; `bash -c` refreshes it at each spawn (InvokeBashBashCommand).
                if (cmd.IsExport && !cmd.IsLocal && pair.Name == "SHELLOPTS")
                    sb.Append(PsBuild.TryMapSpecialVar("SHELLOPTS", braced: false, inDoubleQuote: false));
                else
                {
                    sb.Append(varPrefix);
                    sb.Append(pair.Name);
                }
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
                if (toArray0)
                {
                    string scalarWrite = sb.ToString(scalarStart, sb.Length - scalarStart);
                    sb.Length = scalarStart;
                    string arr = ArrayWriteTarget(pair.Name);
                    sb.Append(PsBuild.WriteElementZeroOrScalar(arr, pair.Op == AssignOp.Equal ? CaseAttr(pair.Name, assigned) : assigned, pair.Op == AssignOp.PlusEqual, scalarWrite));
                }
            }
        }
        // `local`/`export x=$(false)`: the status is the BUILTIN's (0), not the substitution's —
        // the substitution left its own code in LASTEXITCODE. (A plain `x=$(false)` keeps it.)
        if ((cmd.IsLocal || cmd.IsExport) && ContainsCommandSub(cmd))
            sb.Append("; " + PsBuild.SetStatus(localRefused ? 1 : 0));
        else if (localRefused && cmd.Pairs.Length > 1)
            sb.Append("; " + PsBuild.SetStatus(1));
        // `~` and `$HOME` read PowerShell's own read-only $HOME (the launcher's home), which `HOME=/x` never touched;
        // re-point it so a script that assigns HOME sees it (`HOME=/home/bob; echo ~` prints /home/bob).
        if (!cmd.IsLocal && cmd.Pairs.Any(p => p.Name == "HOME" && p.ArrayValue is null && p.Op == AssignOp.Equal))
            // By the cmdlet's TYPE, not `Microsoft.PowerShell.Utility\Set-Variable`: a module-qualified name makes the
            // engine import the module when the session did not load it as one, and under load that import failed
            // ("The module 'Microsoft.PowerShell.Utility' could not be loaded", macOS CI) or stalled (Windows CI), leaving
            // $HOME unchanged. A bare `Set-Variable` would be found, but a script's own `Set-Variable` function would win.
            sb.Append("; & $ExecutionContext.InvokeCommand.GetCmdletByTypeName('Microsoft.PowerShell.Commands.SetVariableCommand') -Name HOME -Value $env:HOME -Option ReadOnly,AllScope -Scope Global -Force");
        // A script writes PATH with `:` separators (`PATH=_tmp:$PATH`); Windows lookup needs `;`. Only a WRITE:
        // a bare `export PATH` leaves the value as it was (bash), even when the inherited PATH is not in `;` form.
        if (cmd.Pairs.Any(p => p.Name == "PATH" && p.ArrayValue is null && p.Value is not null))
            sb.Append("; ").Append(PsBuild.NormalizePathVariable);
        // `export X=1` / `export X` / `local -x X`: the names get the export attribute (a child process sees exported
        // variables only); `export -n X` / `typeset +x X` take it away. After the assignment, and after a local's frame.
        int exportAttr = cmd.ExportAttr != 0 ? cmd.ExportAttr : cmd.IsExport && !cmd.IsLocal ? 1 : 0;
        if (exportAttr != 0)
        {
            var exportNames = cmd.Pairs.Where(p => p.ArrayValue is null && !p.Name.Contains('[') && IsValidVarName(p.Name))
                .Select(p => ResolveNameref(p.Name)).Distinct().ToList();
            if (exportNames.Count > 0)
                sb.Append("; ").Append(exportAttr > 0 ? PsBuild.MarkExported(exportNames) : PsBuild.MarkUnexported(exportNames));
        }
        // `declare -r a=(x y)` / `readonly a=(...)`: the array is read-only too (`${a@a}` says `ar`).
        // Every name of the list is read-only, scalars and bare names too (`readonly a=(1 2) no_value c=3`).
        if (cmd.IsReadonly)
            foreach (var pair in cmd.Pairs)
            {
                string roPair = pair.Name.Contains('[') ? pair.Name[..pair.Name.IndexOf('[')] : pair.Name;
                // `local -r x=1`: writes are refused statically in its function; the run-time mark (declare -p: `-r`) is the
                // local's, so its frame takes it back at the return. REGRESSION: a local scalar was never marked, and a local
                // array's mark outlived the function.
                if (cmd.IsLocal && _functionDepth > 0)
                    sb.Append("; [PsBash.Cmdlets.VarFrameStack]::LocalAttributes(").Append(PsBuild.SingleQuote(roPair)).Append(")");
                else if (cmd.IsLocal) continue;
                sb.Append("; [PsBash.Cmdlets.BashShellState]::MarkReadonly(").Append(PsBuild.SingleQuote(roPair)).Append(")");
                if (!cmd.IsLocal) _readonlyVars?.Add(roPair);
                if (!cmd.IsLocal && _functionDepth > 0) _runtimeReadonly = true;
            }
        if (badSubscript) sb.Append("; ").Append(PsBuild.SetStatus(1));
        return sb.ToString();
    }

    /// <summary>
    /// <c>local -p [NAME...]</c> in a function: its locals as <c>declare</c> lines. A named variable that is not one of
    /// the function's locals is "local: NAME: not found", status 1 (it printed the global, as <c>declare -p</c> does).
    /// </summary>
    private static string EmitLocalListing(Command.Simple cmd, HashSet<string> fnLocals)
    {
        static CompoundWord Lit(string s) => new(ImmutableArray.Create<WordPart>(new WordPart.Literal(s)));
        var named = cmd.Words.Skip(1).Select(GetLiteralValue).Where(s => s != "-p").Select(s => s!).ToList();
        var listed = named.Count == 0 ? fnLocals.ToList() : named.Where(fnLocals.Contains).ToList();
        var missing = named.Where(n => !fnLocals.Contains(n)).ToList();
        var parts = new List<string>();
        if (listed.Count > 0)
            parts.Add(EmitPassthrough("Invoke-BashDeclare", ImmutableArray.Create(Lit("-p")).AddRange(listed.Select(Lit))));
        foreach (var n in missing)
            parts.Add(PsBuild.ShellDiagnosticLine(PsBuild.SingleQuote("local: " + n + ": not found")));
        if (missing.Count > 0)
            parts.Add(PsBuild.SetStatus(1));
        // The redirects (`local -p a b 2>/dev/null`) apply to the "not found" lines too, so they wrap the whole list.
        return EmitMappedStandalone(cmd, parts.Count == 1 ? parts[0] : "& { " + string.Join("; ", parts) + " }");
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
    private static bool TryEmitRuntimeNamedDeclaration(string name, Command.Simple cmd, out string result, bool operandsAreOrdinaryWords = false)
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
            // A literal operand (`x`, `k=~/x`, `-a`) is passed on as written; one with other characters is quoted.
            if (w.Parts.All(p => p is WordPart.Literal) && GetLiteralValue(w) is { Length: > 0 } lit)
            {
                evalWords.Add(lit.All(c => char.IsAsciiLetterOrDigit(c) || "_=+-./:,@%~".Contains(c))
                    ? w : new CompoundWord([new WordPart.SingleQuoted(PsBuild.BashSingleQuote(lit))]));
                continue;
            }
            // Behind `builtin`/`command` every operand expands like a command argument (split, globbed). A quoted array
            // (`typeset -"${a[@]}"`, a=(x foo=F)) is one operand per element: `typeset -x foo=F`.
            if (w.Parts.All(IsExpansion) || operandsAreOrdinaryWords || IsQuotedArrayAllWord(w) || IsQuotedArrayAffixWord(w))
            {
                // The FIELDS of `$names` are names, never code: eval gets each one single-quoted
                // ("$(for w in $names; do printf '%s ' "${w@Q}"; done)"), so `a;echo x` stays one word.
                anyDynamic = true;
                evalWords.Add(QuotedFieldsForEval(w, "%s "));
                continue;
            }
            if (w.Parts.Length == 1 && w.Parts[0] is WordPart.DoubleQuoted dq && !dq.Parts.IsDefaultOrEmpty && dq.Parts.All(IsExpansion))
            {
                anyDynamic = true;
                evalWords.Add(QuotedFieldsForEval(w, "%s"));
                continue;
            }
            // `export NIX_LDFLAGS${role_post}+=" -L$1/lib64"`: a name glued to an expansion is not an assignment word, so
            // bash expands it like an ordinary operand (split, globbed) and the builtin reads NAME+=VALUE from the result.
            // Each field reaches the eval quoted (${w@Q}), as above. It fell to command lookup: "export: command not found".
            // `local "m=$1"` / `local m"$s"=1` too: the eval's `local 'm=a b*'` is the declaration with its local frame
            // (TryEmitQuotedStaticDeclaration). It was assigned straight to the variable and outlived the function.
            // (`export "X=$v"` keeps the direct assignment below, which sets the attribute; a local needs its frame.)
            bool local = name == "local";
            if (name is "export" or "local"
                && w.Parts.Any(p => IsExpansion(p) || local && p is WordPart.DoubleQuoted { Parts: var inner } && inner.Any(IsExpansion))
                && w.Parts.Any(p => p is WordPart.Literal { Value: var l } && l.Contains('=')
                    || local && p is WordPart.DoubleQuoted { Parts: var dqParts } && dqParts.Any(q => q is WordPart.Literal { Value: var dl } && dl.Contains('='))))
            {
                anyDynamic = true;
                evalWords.Add(QuotedFieldsForEval(w, "%s "));
                continue;
            }
            return false;
        }
        if (!anyDynamic) return false;
        if (name == "readonly") _runtimeReadonly = true; // `readonly $names`: the names are known only at run time
        result = EmitEval(evalWords.ToImmutable());
        // `readonly $names 2>/dev/null`: the redirect belongs to the declaration the eval runs.
        var redirects = cmd.Redirects;
        if (!redirects.IsDefaultOrEmpty)
            result = RedirectGroup(result, body => PsBuild.WithRedirectTail(body, tail => AppendRedirectTail(tail, redirects)));
        return true;
    }

    /// <summary>
    /// Whether an operand of a wrapped declaration builtin (an ordinary word) expands unlike the declaration's own: an
    /// unquoted expansion is split and globbed, and one before the first <c>=</c> makes the name a run-time one. The
    /// emitter's side of <c>BashParser.OperandExpandsAsAWord</c>.
    /// </summary>
    private static bool ExpandsAsAWord(CompoundWord w)
    {
        bool sawEquals = false;
        foreach (var p in w.Parts)
        {
            switch (p)
            {
                case WordPart.Literal { Value: var l }:
                    sawEquals |= l.Contains('=');
                    break;
                case WordPart.EscapedLiteral or WordPart.SingleQuoted:
                    break;
                case WordPart.DoubleQuoted { Parts: var dq }:
                    if (!sawEquals && dq.Any(q => q is not (WordPart.Literal or WordPart.EscapedLiteral))) return true;
                    break;
                default:
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// <c>local 'm=a b*'</c>: a quoted NAME=VALUE operand with nothing left to expand is the declaration
    /// <c>local m='a b*'</c> (the builtin reads the assignment from the expanded word, as bash does), so it gets the
    /// function's local frame like a written one. Every run-time <c>local</c> arrives in this form: the eval of
    /// <c>local "$x"</c>, <c>local "m=$1"</c>, <c>builtin local m=$1</c>, <c>\local m=$x</c> hands each field over
    /// single-quoted (<see cref="QuotedFieldsForEval"/>). REGRESSION: it was assigned straight to the variable and
    /// outlived the function (<c>f() { \local m="$1"; }; f "a b*"; echo "${m-unset}"</c> printed a b*; bash: unset).
    /// Only a valid NAME (and plain flags); anything else keeps its path and its diagnostics.
    /// <para>The same for <c>declare</c>/<c>typeset</c> in text built at run time (<c>declare "$2"</c>, <c>$dc v=1</c>,
    /// <c>command declare v=$1</c> hand <c>declare 'v=1'</c> over): the written declaration
    /// <c>declare v='1'</c> is the running function's local (<see cref="RunTimeDeclareScope"/>). REGRESSION: it was
    /// assigned straight to the variable, so <c>f(){ dc=declare; $dc wv=1; }; f</c> left wv set (bash: unset).</para>
    /// </summary>
    private static bool TryEmitQuotedStaticDeclaration(string name, Command.Simple cmd, out string result)
    {
        result = "";
        bool declares = name is "declare" or "typeset" && _context == TranspileContext.Eval;
        if (!(name == "local" || declares) || !cmd.EnvPairs.IsDefaultOrEmpty || !cmd.HereDocs.IsDefaultOrEmpty) return false;
        var text = new StringBuilder(name);
        bool anyQuotedPair = false;
        foreach (var w in cmd.Words.Skip(1))
        {
            if (TryGetStaticArgValue(w) is not { } s) return false;
            bool bare = w.Parts.All(p => p is WordPart.Literal);
            if (bare && s.Length > 1 && s[0] is '-' or '+' && s.Skip(1).All(char.IsAsciiLetter))
            {
                text.Append(' ').Append(s);
                continue;
            }
            int eq = s.IndexOf('=');
            string varName = eq < 0 ? s : eq > 0 && s[eq - 1] == '+' ? s[..(eq - 1)] : s[..eq];
            if (!IsValidVarName(varName)) return false;
            text.Append(' ').Append(eq < 0 ? s : s[..(eq + 1)] + PsBuild.BashSingleQuote(s[(eq + 1)..]));
            // The rebuilt `declare v='1'` keeps NAME= bare, so it is not taken for a quoted pair again.
            anyQuotedPair |= eq >= 0 && !bare && !(declares && w.Parts[0] is WordPart.Literal first && first.Value.Contains('='));
        }
        if (!anyQuotedPair) return false;
        var parsed = BashParser.Parse(text.ToString());
        if (parsed is Command.ShAssignment declaration)
            result = EmitCommand(cmd.Redirects.IsDefaultOrEmpty ? declaration : declaration with { Redirects = cmd.Redirects });
        else if (declares && parsed is Command.Simple written)
            result = EmitCommand(cmd.Redirects.IsDefaultOrEmpty ? written : written with { Redirects = cmd.Redirects });
        else return false;
        return true;
    }

    /// <summary>
    /// "$(for __psbash_declw in WORD; do printf FORMAT "${__psbash_declw@Q}"; done)": each field of WORD single-quoted for
    /// the eval. `%q` would leave a `~` after `=` bare, and the eval then tilde-expands the assignment (bash keeps
    /// `const=~/src` from `readonly "$binding"` as written).
    /// </summary>
    private static CompoundWord QuotedFieldsForEval(CompoundWord w, string format)
    {
        const string fieldVar = "__psbash_declw";
        // Only a field that needs it is quoted: the declaration builtins read a plain name or flag (`m`, `-x`, `k=v`) as
        // text, and `'-x'` / `'1a=3'` would reach them as quoted words. Empty, blank, `~`, `;`, `$`, quote and glob
        // characters are quoted, so the eval sees them as data.
        var quoteField = BashParser.Parse(
            "case $" + fieldVar + " in ''|*[!A-Za-z0-9_=+./:,@%-]*) printf '" + format + "' \"${" + fieldVar + "@Q}\";; "
            + "*) printf '" + format + "' \"$" + fieldVar + "\";; esac")
            ?? throw new InvalidOperationException("QuotedFieldsForEval: the field quoting snippet did not parse");
        var fields = new Command.ForIn(fieldVar, [w], quoteField, ExplicitIn: true);
        return new CompoundWord([new WordPart.DoubleQuoted([new WordPart.CommandSub(fields)])]);
    }

    /// <summary>
    /// `declare b[2]=y`, `local -a c[i+1]=$v`, `declare a[a[0]=1]=X`: an element initializer declares NAME (with the
    /// command's flags) and then assigns the element like `b[2]=y`. The word was globbed and handed to declare, which
    /// reported "b[2]=y: not found".
    /// </summary>
    private static bool TryEmitElementDeclaration(Command.Simple cmd, out string result)
    {
        result = "";
        if (cmd.Words.Length < 2 || GetLiteralValue(cmd.Words[0]) is not ("declare" or "typeset" or "local")) return false;
        // `declare a[ a[2]=3 ]=Y`: unlike an assignment statement, a declare operand is split at the blanks first
        // (`a[` and `]=Y` are then invalid identifiers).
        if (cmd.Words.Skip(1).Any(w => TrySplitElementInitializer(w, out var n, out _, out _) && n.Any(char.IsWhiteSpace)))
        {
            var resplit = new List<CompoundWord> { cmd.Words[0] };
            foreach (var w in cmd.Words.Skip(1))
            {
                if (!TrySplitElementInitializer(w, out var n, out _, out _) || !n.Any(char.IsWhiteSpace)) { resplit.Add(w); continue; }
                int run = 0;
                var lead = new StringBuilder();
                while (run < w.Parts.Length && w.Parts[run] is WordPart.Literal or WordPart.GlobPart)
                    lead.Append(w.Parts[run++] switch { WordPart.Literal l => l.Value, WordPart.GlobPart g => g.Pattern, _ => "" });
                var fields = lead.ToString().Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries);
                for (int k = 0; k < fields.Length; k++)
                    resplit.Add(new CompoundWord(k < fields.Length - 1
                        ? [new WordPart.Literal(fields[k])]
                        : [new WordPart.Literal(fields[k]), .. w.Parts.Skip(run)]));
            }
            result = EmitSimple(cmd with { Words = [.. resplit] });
            return true;
        }
        // `declare -r ro[0]=1` makes ro a readonly (empty) array and then refuses the element: an error (status 0), and
        // unlike an assignment statement the rest of the line still runs.
        bool readonlyFlag = cmd.Words.Skip(1).Any(w => GetLiteralValue(w) is { Length: > 1 } f && f[0] == '-' && f.Contains('r'));
        bool hasArrayFlag = cmd.Words.Skip(1).Any(w => GetLiteralValue(w) is { Length: > 1 } f && f[0] == '-' && (f.Contains('a') || f.Contains('A')));
        var flags = new List<CompoundWord>();
        var elements = new List<(string Name, AssignOp Op, CompoundWord Value)>();
        var others = new List<CompoundWord>();
        foreach (var w in cmd.Words.Skip(1))
        {
            if (TrySplitElementInitializer(w, out var elemName, out var op, out var value)) elements.Add((elemName, op, value));
            else if (GetLiteralValue(w) is { Length: > 1 } f && f[0] is '-' or '+') flags.Add(w);
            else others.Add(w);
        }
        if (elements.Count == 0) return false;
        var sbOut = new StringBuilder();
        if (others.Count > 0)
            sbOut.Append(EmitSimple(cmd with { Words = [cmd.Words[0], .. flags, .. others], Redirects = [] })).Append("; ");
        foreach (var (elemName, op, value) in elements)
        {
            string baseName = elemName[..elemName.IndexOf('[')];
            CompoundWord[] declFlags = readonlyFlag && !hasArrayFlag ? [new CompoundWord([new WordPart.Literal("-a")]), .. flags] : [.. flags];
            sbOut.Append(EmitSimple(cmd with { Words = [cmd.Words[0], .. declFlags, new CompoundWord([new WordPart.Literal(baseName)])], Redirects = [] }))
                 .Append("; ");
            if (readonlyFlag)
                sbOut.Append(PsBuild.ShellDiagnosticLine(PsBuild.SingleQuote($"{baseName}: readonly variable"))).Append("; ");
            else
                sbOut.Append(EmitStatement(new Command.ShAssignment([new Assignment(elemName, op, value)]))).Append("; ");
        }
        sbOut.Append(PsBuild.SetStatus(0));
        result = ApplyCompoundRedirects(sbOut.ToString(), cmd.Redirects);
        return true;
    }

    /// <summary>`NAME[subscript]=value` / `NAME[subscript]+=value` as one word (the subscript may nest brackets):
    /// the element name `NAME[subscript]`, the operator, and the value word.</summary>
    private static bool TrySplitElementInitializer(CompoundWord word, out string elemName, out AssignOp op, out CompoundWord value)
    {
        elemName = ""; op = AssignOp.Equal; value = word;
        if (word.Parts.IsDefaultOrEmpty) return false;
        // The parser splits `b[2]=y` into literal and glob parts: join the leading unquoted run, remembering where
        // each part ends, so the value keeps the parts after the `=`.
        var text = new StringBuilder();
        var partEnds = new List<int>();
        foreach (var part in word.Parts)
        {
            if (part is WordPart.Literal lit) text.Append(lit.Value);
            else if (part is WordPart.GlobPart glob) text.Append(glob.Pattern);
            else break;
            partEnds.Add(text.Length);
        }
        string s = text.ToString();
        int i = 0;
        while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] == '_')) i++;
        if (i == 0 || char.IsAsciiDigit(s[0]) || i >= s.Length || s[i] != '[') return false;
        int depth = 0, close = -1;
        for (int j = i; j < s.Length; j++)
        {
            if (s[j] == '[') depth++;
            else if (s[j] == ']' && --depth == 0) { close = j; break; }
        }
        if (close < 0 || close == i + 1) return false;
        int eq = close + 1;
        if (eq + 1 < s.Length && s[eq] == '+' && s[eq + 1] == '=') { op = AssignOp.PlusEqual; eq += 2; }
        else if (eq < s.Length && s[eq] == '=') eq += 1;
        else return false;
        elemName = s[..(close + 1)];
        int consumed = 0;
        while (partEnds[consumed] < eq) consumed++;
        var parts = word.Parts.RemoveRange(0, consumed + 1);
        if (eq < partEnds[consumed]) parts = parts.Insert(0, new WordPart.Literal(s[eq..partEnds[consumed]]));
        value = new CompoundWord(parts);
        return true;
    }

    private static bool TryEmitDynamicAssignmentBuiltin(Command.Simple cmd, out string result)
    {
        result = "";
        // `builtin export z=$x`, `command readonly c=$x`, `\builtin declare d=$x`: behind a wrapper the operands are
        // ORDINARY words (bash 5.2): with x='a b', `z=$x` splits into `z=a` and `b`. The parser leaves such a
        // declaration a plain command when an operand has an expansion; it runs through the run-time eval.
        int wrap = 0;
        while (wrap < cmd.Words.Length - 1 && TryGetStaticArgValue(cmd.Words[wrap]) is "builtin" or "command") wrap++;
        if (wrap > 0 && TryGetStaticArgValue(cmd.Words[wrap]) is { } wrapped && wrapped is "declare" or "typeset" or "local" or "export" or "readonly")
        {
            var inner = cmd with { Words = [NoAliasWord(wrapped), .. cmd.Words.Skip(wrap + 1)] };
            // An operand expanded only inside double quotes after NAME= (`builtin typeset t="$x"`) is neither split nor
            // globbed: the same as the declaration's, which needs no run-time transpile (BashParser.OperandExpandsAsAWord).
            if (inner.Words.Skip(1).Any(ExpandsAsAWord)
                && TryEmitRuntimeNamedDeclaration(wrapped, inner, out result, operandsAreOrdinaryWords: true))
                return true;
            result = EmitSimple(inner);
            return true;
        }
        var name = cmd.Words.Length < 2 ? null : GetLiteralValue(cmd.Words[0]);
        if (name is not ("declare" or "typeset" or "local" or "export" or "readonly"))
            return false;
        if (TryEmitQuotedStaticDeclaration(name, cmd, out result)) return true;
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
        // `declare $y`, `export $y`, `local $y` (y='p=1 q=2'): the run-time eval declares each field. Known gap:
        // `typeset -a "$code"` with code='x=(1 2 3)' makes the scalar "(1 2 3)" (bash parses the array; ps-bash never
        // re-parses a variable's value as code).
        if (TryEmitRuntimeNamedDeclaration(name, cmd, out result)) return true;
        // `readonly 'c=$(echo'`: a quoted operand with no expansion is static text, which EmitReadonly checks as an
        // identifier like the bare word (`readonly '1a=3'` is status 1).
        bool readonlyKw = name == "readonly";
        bool IsRuntimeAssignment(CompoundWord w) => IsDynamicAssignment(w) && !(readonlyKw && TryGetStaticArgValue(w) is not null);
        if (!cmd.Words.Skip(1).Any(IsRuntimeAssignment)) return false;

        // Everything that is not a dynamic assignment keeps its normal path; each dynamic one assigns the scalar
        // named at run time (all scalars live in the environment store).
        bool IsAssignment(CompoundWord w) => IsRuntimeAssignment(w) || TrySplitInitializer(w, out _, out _);
        var plain = cmd.Words.Where((w, i) => i == 0 || !IsAssignment(w)).ToImmutableArray();
        var sbOut = new StringBuilder();
        if (plain.Skip(1).Any(w => !(GetLiteralValue(w) is { Length: > 1 } f && f[0] is '-' or '+')))
            sbOut.Append(EmitSimple(cmd with { Words = plain })).Append("; ");
        foreach (var w in cmd.Words.Skip(1).Where(IsAssignment))
        {
            if (!IsRuntimeAssignment(w) && TrySplitInitializer(w, out var staticName, out var staticValue))
            {
                // A name from word text, not from an AST record: map it here (`export path=x` is not PATH, BashNameCase).
                sbOut.Append("$env:").Append(BashNameCase.ToStore(staticName)).Append(" = ").Append(EmitAssignmentValue(staticValue)).Append("; ");
                if (staticName == "PATH") sbOut.Append(PsBuild.NormalizePathVariable).Append("; ");
                continue;
            }
            string expr = "[string](" + EmitWord(w) + ")";
            // `readonly "$n=1"`: the name is known only at run time, so it is marked there.
            string markReadonly = name == "readonly" ? "; [PsBash.Cmdlets.BashShellState]::MarkReadonly($__psbN)" : "";
            if (name == "readonly") _runtimeReadonly = true;
            // `export 'NAME+=VALUE'` (the run-time eval of `export NAME${x}+=v` quotes each field) appends.
            sbOut.Append($"$__psbD = {expr}; $__psbI = $__psbD.IndexOf('='); ")
                 .Append("if ($__psbI -gt 0) { $__psbN = $__psbD.Substring(0, $__psbI); $__psbV = $__psbD.Substring($__psbI + 1); ")
                 .Append("if ($__psbN.EndsWith('+')) { $__psbN = $__psbN.Substring(0, $__psbN.Length - 1); $__psbV = [string][PsBash.Cmdlets.BashVariableStore]::Get($__psbN) + $__psbV }; ")
                 // The bare name, operator stripped, is what maps (`path+` is no reserved spelling): `path` is not PATH (BashNameCase).
                 .Append("$__psbN = [PsBash.Core.BashNameCase]::ToStore($__psbN); ")
                 .Append("Set-Item -LiteralPath ('Env:' + $__psbN) -Value $__psbV" + markReadonly)
                 // `export "$k=$v"`: `declare -p` shows the x attribute, as for a literal `export X=1`.
                 .Append(name == "export" ? "; " + PsBuild.MarkExportedAtRunTime("$__psbN") : "")
                 .Append(" }; ");
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
        // The reports are the command's own stderr: `readonly 1a=3 2>/dev/null` is silent.
        var redirects = cmd.Redirects;
        string reported = redirects.IsDefaultOrEmpty
            ? errs.ToString()
            : PsBuild.WithRedirectTail(errs.ToString().TrimEnd(' ', ';'), tail => AppendRedirectTail(tail, redirects)) + "; ";
        if (keep.Count == 1)
        {
            result = reported + PsBuild.SetStatus(1);
            return true;
        }
        var rest = EmitSimple(cmd with { Words = [.. keep] });
        result = reported + rest + "; " + PsBuild.SetStatus(1);
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
        cmd = UnquoteBuiltinName(cmd);
        // `set -o posix` (bash): a prefix assignment before a special builtin stays set after it (`foo=bar :`), and the
        // builtin wins over a function of its name. The option is known only at run time, so a script that names it
        // gets both forms.
        if (_scriptPosix && !ReferenceEquals(_posixSplitNode, cmd) && cmd.Words.Length > 0
            && GetLiteralValue(cmd.Words[0]) is { } posixName && s_posixSpecialBuiltins.Contains(posixName)
            && (!cmd.EnvPairs.IsEmpty || _definedFunctions?.Contains(posixName) == true))
            return EmitPosixSpecialBuiltin(cmd, posixName);
        // `local +n ref`: inside a function `local` is `typeset`, whose path reads a `+` flag (the parser takes only `-`).
        if (_functionDepth > 0 && cmd.Words.Length > 1 && GetLiteralValue(cmd.Words[0]) == "local"
            && cmd.Words.Skip(1).Any(w => GetLiteralValue(w) is ['+', _, ..]))
            cmd = cmd with { Words = cmd.Words.SetItem(0, new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal("typeset")))) };
        var braced = ExpandDeclarationBraceWords(UnquotePlainDeclarationOperands(cmd));
        // `export -p a{0..1}` is `export -p a0 a1`, which the parser makes a declaration; do the same here.
        if (!ReferenceEquals(braced, cmd) && AsPlainExport(braced) is { } export)
            return EmitShAssignment(export);
        cmd = braced;
        // `exec >&2` / `FOO=bar exec 1>&3`: the redirections ARE the command, so the generic redirect handlers must not
        // strip them (they piped a `$global:LASTEXITCODE = 0` into the target, a stray "0"). A binding is dropped (bash).
        if (cmd.Words.Length == 1 && GetLiteralValue(cmd.Words[0]) == "exec" && !cmd.Redirects.IsDefaultOrEmpty
            && cmd.HereDocs.IsDefaultOrEmpty && _definedFunctions?.Contains("exec") != true
            && TryEmitExec(cmd with { EnvPairs = [] }, out var execResult))
            return execResult;
        if (TryEmitRunTimeExecRedirect(cmd, out var runTimeExec))
            return runTimeExec;
        // `echo hi {fd}>f`, `cmd {v}<<< w`: bash opens a descriptor of its own (10 up), stores its number in the variable
        // and leaves it open after the command; the command's own stdin and stdout are not touched (as for a group, see
        // ApplyCompoundRedirects). It was taken as a stdout redirect: the output went to f and the variable stayed unset.
        if (cmd.Words.Length > 0 && !cmd.Redirects.IsDefaultOrEmpty && cmd.Redirects.Any(r => r.FdVar is not null)
            && GetLiteralValue(cmd.Words[0]) != "exec")
        {
            var opens = new List<string>();
            foreach (var r in cmd.Redirects)
            {
                if (r.FdVar is not { } fdVar) continue;
                string target = r.Here is { } fdHere ? "(" + EmitHereDocLiteral(fdHere) + ")" : EmitArgWord(r.Target);
                opens.Add($"Invoke-BashExecFd -Variable {PsBuild.SingleQuote(fdVar)} -Operator {PsBuild.SingleQuote(r.OpenOperator)} -Target {target}");
            }
            var rest = cmd with { Redirects = [.. cmd.Redirects.Where(r => r.FdVar is null)] };
            return RedirectGroup(EmitSimple(rest), body => ". { " + string.Join("; ", opens) + "; " + body + " }");
        }
        // `w9 9>&1`, `echo hi 9>f.txt`, `read -u 7 l 7<f`: a numbered descriptor opened for this command only.
        if (ScopedHighFdRedirects(cmd) is { Count: > 0 } highFds)
            return RedirectGroup(EmitSimple(cmd with { Redirects = [.. cmd.Redirects.Where(r => !highFds.Contains(r))] }),
                body => WithScopedHighFds(body, highFds));
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

        if (TryEmitInvalidBuiltinOption(cmd, out var invalidOptionResult))
            return invalidOptionResult;

        if (TryEmitInvalidIdentifiers(ref cmd, out var invalidIdResult))
            return invalidIdResult;

        if (TryEmitElementDeclaration(cmd, out var elementDeclResult))
            return elementDeclResult;

        if (TryEmitDynamicAssignmentBuiltin(cmd, out var dynamicAssignResult))
            return dynamicAssignResult;

        if (TryEmitDeclare(cmd, out var declareResult))
            return RunTimeDeclareScope(cmd, declareResult);

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

        // `a[1]=v cmd`: an element is not an environment binding (it cannot be exported), so it is not wrapped around the command.
        if (cmd.EnvPairs.Any(p => p.Name.Contains('[')))
        {
            var kept = cmd.EnvPairs.RemoveAll(p => p.Name.Contains('['));
            result = EmitSimple(cmd with { EnvPairs = kept });
            return true;
        }

        // `readonly r=1; r=3 cmd`: bash reports the binding and runs the command without it (its status is the command's;
        // the line goes on). REGRESSION: nothing was reported.
        if (!cmd.Words.IsDefaultOrEmpty && _readonlyVars is { Count: > 0 } && cmd.EnvPairs.Any(p => _readonlyVars.Contains(p.Name)))
        {
            var refused = cmd.EnvPairs.Where(p => _readonlyVars.Contains(p.Name)).ToList();
            var sbRo = new StringBuilder();
            foreach (var pair in refused)
                sbRo.Append(PsBuild.ShellDiagnosticLine(PsBuild.SingleQuote($"{pair.Name}: readonly variable"))).Append("; ");
            result = sbRo.Append(EmitSimple(cmd with { EnvPairs = cmd.EnvPairs.RemoveAll(p => refused.Contains(p)) })).ToString();
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

        // `v=t readonly v` / `v=t export v`: a declaration that names its own prefix keeps the binding's value (bash 5.2).
        if (cmd.Words.Length > 1 && GetLiteralValue(cmd.Words[0]) is "readonly" or "export")
        {
            var named = cmd.EnvPairs.Where(p => cmd.Words.Skip(1).Any(w => GetLiteralValue(w) == p.Name)).ToImmutableArray();
            if (!named.IsEmpty)
            {
                // Written as the binding itself would be (no readonly check: the declaration that follows may already
                // be recorded, when the POSIX form of this command was emitted first).
                result = string.Concat(named.Select(p => $"$env:{p.Name} = {EmitAssignmentValue(p.Value)}; "))
                    + EmitSimple(cmd with { EnvPairs = cmd.EnvPairs.RemoveAll(p => named.Contains(p)) });
                return true;
            }
        }

        var bare = cmd with { EnvPairs = ImmutableArray<EnvPair>.Empty };
        result = WrapEnvPairs(cmd.EnvPairs, WithEnvShadow(cmd.EnvPairs, () => EmitSimple(bare)), TempFrameKind(cmd));
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
            result = RedirectGroup(inner, body => $". {{ {opens}; {body}; $__psbashFdRc = $global:LASTEXITCODE; {closes}; $global:LASTEXITCODE = $__psbashFdRc }}");
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
        // eval / source / . run text in this shell whose commands read the here-document: a stdin scope around them
        // (`. ./f <<< "$in"` with `read l` in f; `. /dev/stdin <<< "$code"` runs it). REGRESSION: source was piped
        // into a bare `. $(...)`, so the file's commands read nothing and /dev/stdin was empty.
        if (cmd.EnvPairs.IsEmpty && GetLiteralValue(cmd.Words[0]) is "eval" or "source" or ".")
        {
            string hereText = EmitHereDocLiteral(cmd.HereDocs[^1]);
            result = RedirectGroup(WithStdinScope(true, () => EmitSimple(innerCmd)), body => $"{hereText} | Emit-BashLine | {DotStdinBlock(body)}");
            return true;
        }
        // The command is the pipe stage after the body (`body | Emit-BashLine | cmd`): any script block its emission wraps it in
        // (a splat hoist, an env prefix, the run-time exec branch of `$SH >out <<EOF`) must hand the block's input on, or the
        // command reads nothing (`f=-; cat $f <<EOF`, `$SH >stdout <<EOF` printed nothing).
        string cmdText = WithPipeStageFeed(true, () => EmitSimple(innerCmd));
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
        // A here-string is a word (bash 5.2: tilde, parameter, command and arithmetic expansion, quote removal - no
        // splitting, no globbing), expanded as an assignment value is, plus its newline (HereDoc.Word).
        if (hereDoc.Word is { } word)
            return PsBuild.HereStringValue(EmitAssignmentValue(word));
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
            // Its readers read the EMPTY scope, never the surrounding one (SealedFromStdinProbe): `ssh h cmd < /dev/null`
            // in a read loop's body leaves the loop its lines, and the loop may still stream.
            // A return in it (`return 6 </dev/null`, `eval 'return 7' </dev/null`) leaves the function past the block.
            try { result = SealedFromStdinProbe(() => RedirectGroup(WithStdinScope(true, () => EmitSimpleFed(innerCmd)), b => PsBuild.EmptyStdinScope(b, _emptyStdinDepth))); }
            finally { _emptyStdinDepth--; }
            return true;
        }

        // `$(<file)` — bash's read-a-file shorthand: a redirect with NO command, whose
        // value is the file's contents. Emitting the usual `Get-Content f | <cmd>` with
        // an empty <cmd> produced a dangling `|` ("An empty pipe element is not
        // allowed"), which failed the parse of the whole file. With no command the
        // redirect IS the command.
        // The command is the pipe stage after the file (`Invoke-BashCat f | cmd`): fed like a here-document's command, so a
        // wrapper its emission needs (a splat hoist: `g=-; cat $g < f` read nothing) hands the file on.
        // A target that cannot be opened fails the command before it runs (bash: status 1, the command never starts).
        // Device paths (`/dev/stdin`, `/dev/zero`) are the runtime's own and always open.
        bool guarded = !(TryGetPureLiteralText(inputRedirect.Target.Parts, out var literalTarget)
                         && DevicePath.Classify(literalTarget, windows: false, out _) != DeviceKind.None);
        string? failTail = guarded ? InputFailTail(cmd.Redirects, inputRedirect) : null;
        bool noCommand = innerCmd.Words.IsDefaultOrEmpty && innerCmd.EnvPairs.IsDefaultOrEmpty;
        var inner = noCommand ? "" : WithPipeStageFeed(true, () => EmitSimple(innerCmd));
        if (inner.Length == 0)
        {
            // Only a lone `$(< file)` reads the file out; any other bare redirect just opens it. Its other redirects
            // (`$(< f 2>/dev/null)`) still apply; emitted as a command they were an expression after a pipe.
            var read = new StringBuilder(ReferenceEquals(cmd, _captureBody) && !remaining.Any(r => r.Fd == 1 || r.Op is "&>" or "&>>")
                ? $"Get-Content {target}" : $"Get-Content {target} | Out-Null");
            if (!remaining.IsDefaultOrEmpty) AppendRedirectTail(read, remaining);
            result = read.ToString();
            // `x=$(< missing)`: reported (status 1), as for a command; Get-Content's own error was a PowerShell one, status 0.
            if (guarded) result = InputRedirectGuarded(target, result, static r => r, failTail);
            return true;
        }

        // The file is the command's STDIN, which is a byte stream: `Invoke-BashCat` feeds it as exact
        // records (no CRLF/BOM rewriting, a missing final newline kept, invalid UTF-8 bytes carried as
        // escaped-byte markers — see RawBytes), where Get-Content would decode lossily and always
        // terminate the last line (`wc -c < f` was one byte high). A mapped consumer takes those records
        // as-is; a native / unmapped consumer needs their TEXT (the binder would format a record object).
        bool mappedConsumer = TryEmitMappedCommand(innerCmd, out _);
        // `. ./f < in`: the sourced commands read the file, in this shell - a stdin scope, as for a here-document
        // (EmitEvalWithStdin). REGRESSION: piped into a bare `. $(...)`, `read` in f read nothing.
        // `eval 'return 7' < f`, `return 6 < f`: a statement list too (no pipe stage: the whole script failed to parse); run
        // as source's text is, and a return in it leaves the function (RedirectGroup).
        if (innerCmd.EnvPairs.IsDefaultOrEmpty && GetLiteralValue(innerCmd.Words[0]) is "source" or "." or "eval" or "return" or "exit")
        {
            var fed = WithStdinScope(true, () => EmitSimple(innerCmd));
            string Feed(string body) => $"Invoke-BashCat {target} -RawStdinBytes | {DotStdinBlock(body)}";
            result = guarded ? InputRedirectGuarded(target, fed, Feed, failTail) : RedirectGroup(fed, Feed);
            return true;
        }
        result = mappedConsumer
            ? $"Invoke-BashCat {target} -RawStdinBytes | {inner}"
            : $"Invoke-BashCat {target} -RawStdinBytes | {NativeStdinFeedStage(innerCmd)}{inner}";
        if (guarded)
            result = InputRedirectGuarded(target, result, static r => r, failTail);
        return true;
    }

    /// <summary>
    /// A simple command fed <c>&lt; file</c> (<paramref name="feed"/> builds the feed around its emitted
    /// <paramref name="body"/>) behind the guard that reports a file it cannot open, through <see cref="RedirectGroup"/>:
    /// a return in the body leaves the function (when the file cannot be opened the body never runs, nor returns).
    /// </summary>
    private static string InputRedirectGuarded(string target, string body, Func<string, string> feed, string? failTail)
    {
        return RedirectGroup(body, b =>
            PsBuild.SimpleInputRedirectGuard(target, feed(b), failTail));
    }

    /// <summary>
    /// <c>declare</c>/<c>typeset</c> in eval text (<c>eval "$c"</c>, <c>declare "$2"</c>, <c>builtin declare v=$1</c>, a
    /// sourced file): the text cannot know whether a function runs it, so each scalar it declares becomes the running
    /// function's local at run time (<see cref="PsBuild.LocalWhenInFunction"/>, the frame a run-time <c>local</c> takes),
    /// and the declaration stays global at the top level. <c>-g</c>, the print forms and anything but the attribute
    /// letters a local takes keep it global; arrays and namerefs stay global, as a run-time <c>local</c>'s do.
    /// REGRESSION: <c>f(){ declare "$1"; }; f e=b</c> left e set after f returned (bash: <c>declare: e: not found</c>).
    /// </summary>
    private static string RunTimeDeclareScope(Command.Simple cmd, string declaration)
    {
        if (_context != TranspileContext.Eval || _functionDepth != 0 || _dynLocals is not null) return declaration;
        if (GetLiteralValue(cmd.Words[0]) is not ("declare" or "typeset")) return declaration;
        var scope = new StringBuilder();
        foreach (var w in cmd.Words.Skip(1))
        {
            string? lit = GetLiteralValue(w);
            if (lit == "--") continue;
            if (lit is { Length: > 1 } && lit[0] is '-' or '+')
            {
                if (lit.AsSpan(1).IndexOfAnyExcept(RunTimeLocalDeclareFlags) >= 0) return declaration;
                continue;
            }
            string name;
            bool keepsValue;
            if (lit is not null)
            {
                int eq = lit.IndexOf('=');
                name = eq < 0 ? lit : lit[..eq];
                // `declare s+=x`: a new local starts empty, as `local s+=x` does.
                bool append = name.EndsWith('+');
                if (append) name = name[..^1];
                keepsValue = eq > 0 && !append;
            }
            else if (TrySplitAppendInitializer(w, out var appendName, out _)) { name = appendName; keepsValue = false; }
            else if (TrySplitInitializer(w, out var initName, out _)) { name = initName; keepsValue = true; }
            else return declaration;
            if (!IsValidVarName(name) || !IsDynamicLocalName(name)) continue;
            scope.Append(PsBuild.LocalWhenInFunction(name, keepsValue)).Append("; ");
        }
        return scope.Length == 0 ? declaration : scope + declaration;
    }

    /// <summary>The attribute letters a run-time <c>declare</c> may carry and still declare a scalar local (no -a/-A/-n, no -g).</summary>
    private const string RunTimeLocalDeclareFlags = "ilrux";

    // declare/typeset -A map -> $map = @{} (associative array / hashtable declaration)
    // declare/typeset -a arr -> $arr = @() (indexed array declaration)
    private static bool TryEmitDeclare(Command.Simple cmd, out string result)
    {
        if (cmd.Redirects.IsEmpty) return TryEmitDeclareUnredirected(cmd, out result);
        // `declare -A q 2>/dev/null`: a declaration can report an error (a refused kind change), which goes to its
        // redirects. The print forms (`declare -p x >f`) attach their own.
        if (!TryEmitDeclareUnredirected(cmd with { Redirects = [] }, out result)) return false;
        if (result.StartsWith("Invoke-BashDeclare", StringComparison.Ordinal)) return TryEmitDeclareUnredirected(cmd, out result);
        result = ApplyCompoundRedirects(result, cmd.Redirects);
        return true;
    }

    private static bool TryEmitDeclareUnredirected(Command.Simple cmd, out string result)
    {
        result = "";
        if (cmd.Words.Length < 2 || GetLiteralValue(cmd.Words[0]) is not ("declare" or "typeset"))
            return TryEmitDeclareCore(cmd, out result);

        // `declare -pr` / `-pa` / `-px`: a print form whose other letters SELECT variables; nothing is declared.
        if (cmd.Words.Skip(1).Any(w => GetLiteralValue(w) is { Length: > 1 } pf && pf[0] == '-' && pf[1] != '-' && pf.Contains('p')))
            return TryEmitDeclareCore(cmd, out result);

        // `declare -r X=1` is `readonly X=1`: emit the declaration without the r attribute, then mark each name.
        bool hasR = false;
        bool plusX = false;
        var attrs = new HashSet<char>();
        var kept = new List<CompoundWord> { cmd.Words[0] };
        var names = new List<string>();
        foreach (var w in cmd.Words.Skip(1))
        {
            var lit = GetLiteralValue(w);
            // `declare +x NAME`: the export attribute is taken away (the variable stays).
            if (lit is { Length: > 1 } && lit[0] == '+' && lit.AsSpan(1).IndexOf('x') >= 0) plusX = true;
            if (lit is { Length: > 1 } && lit[0] == '-' && lit.AsSpan(1).IndexOfAnyExcept("pfF") >= 0)
            {
                if (lit.Contains('x')) attrs.Add('x');
                if (lit.Contains('i') && _declarePrintMode) attrs.Add('i');
                // `declare -l s`: declare -p shows the case letter (none when -l and -u cancel).
                if (_declarePrintMode && DeclareCaseFlag(cmd) is not '\0' and var caseLetter) attrs.Add(caseLetter);
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
            else if (!lit.StartsWith('-') && !lit.StartsWith('+')) names.Add(DeclaredName(lit));
        }
        // bash ignores -r on a nameref (`typeset -n -r ref=x` still lets `ref=v` assign through).
        bool isNameref = cmd.Words.Skip(1).Any(w => GetLiteralValue(w) is { Length: > 1 } f && f[0] == '-' && f.AsSpan(1).IndexOfAnyExcept("pfF") >= 0 && f.Contains('n'));
        if (!(hasR || plusX || attrs.Count > 0) || isNameref || names.Count == 0)
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
            _readonlyVars?.Add(BashNameCase.ToStore(n));
        }
        if (plusX) mark.Append("; ").Append(PsBuild.MarkUnexported(names.Where(IsValidVarName)));
        result = core + mark;
        return true;
    }

    /// <summary>The name a literal declaration operand declares: <c>s</c> for <c>s</c>, <c>s=1</c> and <c>s+=1</c>.</summary>
    private static string DeclaredName(string operand)
    {
        string name = operand.Split('=')[0];
        return name.EndsWith('+') ? name[..^1] : name;
    }

    /// <summary>
    /// A <c>declare</c> option word sets attribute <paramref name="letter"/>: alone (<c>-i</c>) or combined (<c>-gi</c>,
    /// <c>-ix</c>). REGRESSION: only the bare word counted, so <c>declare -gi n=2+2</c> stored "2+2".
    /// </summary>
    private static bool HasDeclareFlag(Command.Simple cmd, char letter)
    {
        foreach (var w in cmd.Words.Skip(1))
            if (GetLiteralValue(w) is { Length: > 1 } f && f[0] == '-' && f[1] != '-' && f.AsSpan(1).IndexOfAnyExcept("aAfFgilnprtux") < 0 && f.Contains(letter))
                return true;
        return false;
    }

    /// <summary>The case attribute a <c>declare</c> sets: 'l' or 'u', '\0' for none. Both in one declaration cancel (bash: <c>declare -lu v=Mx</c> keeps "Mx").</summary>
    private static char DeclareCaseFlag(Command.Simple cmd)
    {
        bool lower = false, upper = false;
        foreach (var w in cmd.Words.Skip(1))
            if (GetLiteralValue(w) is { Length: > 1 } f && f[0] == '-' && f[1] != '-' && f.AsSpan(1).IndexOfAnyExcept("aAfFgilnprtux") < 0)
            {
                lower |= f.Contains('l');
                upper |= f.Contains('u');
            }
        return lower == upper ? '\0' : lower ? 'l' : 'u';
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
                // `typeset +n ref`: a + word is an attribute flag too (a name merely containing n, `k=1 pwn`, is not).
                (lit is { Length: > 1 } && lit[0] is '-' or '+' ? flagWords : nameWords).Add(w);
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
            // `declare 'b'` / `declare "-x" b`: quote removal comes first, so a quoted static name or flag is that text (it
            // went to the print cmdlet, which listed `declare -- b=...`; bash prints nothing without -p).
            if (val is null && TryGetStaticArgValue(word) is { Length: > 0 } quotedVal
                && (quotedVal[0] is '-' or '+' ? quotedVal.Length > 1 && quotedVal.AsSpan(1).IndexOfAnyExcept("aAfFgilnprtux") < 0
                    : IsValidVarName(quotedVal.Split('=')[0])))
                val = quotedVal;
            // `declare -gA m` (a function declaring a global associative array): the -g changes nothing here, every array
            // is global. It was a plain `declare m`, so `m[k]=v` wrote an indexed element.
            if (val == "-A" || val is { Length: > 2 } && val[0] == '-' && val.Contains('A') && val.AsSpan(1).IndexOfAnyExcept("gA") < 0) isAssoc = true;
            else if (val == "-n") isNameref = true;
            else if (val == "-i") { /* integer — handled below */ }
            else if (val is { Length: > 1 } && val[0] == '-' && (val.AsSpan(1).IndexOfAnyExcept("pfF") < 0 || (val[1] != '-' && val.Contains('p')))) isPrint = true;
            else if (val is { Length: > 1 } && val[0] == '+' && val.AsSpan(1).IndexOfAnyExcept("aAilnrtux") < 0) { /* `declare +a x`: drops an attribute; nothing to record */ }
            else if (val is not null && !val.StartsWith('-')) varName = val;
            else if (val is null && TrySplitInitializer(word, out var n, out var v)) { exprName = n; exprValue = v; }
        }
        // The names come from word text, not from AST records: the store name the records carry (`declare path=x` is not
        // PATH on Windows; BashNameCase). A message names it by its bash name (PsBuild.ShellDiagnosticLine).
        if (varName is not null) varName = StoreNameWord(varName);
        if (exprName is not null) exprName = BashNameCase.ToStore(exprName);
        // `readonly r=1; typeset +r r=2` / `declare r=2`: assigning a read-only name is refused, the value kept (the direct
        // `$env:r = ...` below overwrote it). Same report as a plain `r=2` (EmitAssignment), with the builtin's name.
        if (!isPrint && _readonlyVars is not null && (exprName ?? (varName is { } rv && rv.IndexOf('=') > 0 ? DeclaredName(rv) : null)) is { } assigned
            && _readonlyVars.Contains(assigned))
        {
            // A builtin's failure: unlike `r=2`, the rest of the line still runs (no _readonlyViolation).
            result = PsBuild.ShellDiagnosticLine(PsBuild.SingleQuote($"{name}: {assigned}: readonly variable")) + "; " + PsBuild.SetStatus(1);
            return true;
        }
        // `declare -n r=r`: a nameref to itself; bash refuses it (status 1) and declares nothing. REGRESSION: it was declared
        // silently, status 0.
        if (isNameref && !isPrint
            && (exprName is not null && exprValue is not null ? (exprName, ExtractSingleQuotedOrLiteralValue(exprValue))
                : varName is not null && varName.IndexOf('=') is var selfEq and > 0 ? (varName[..selfEq], varName[(selfEq + 1)..]) : (null, null))
               is (string selfName, string selfTarget)
            && VarNameKeys.Equals(selfName, selfTarget) && IsValidVarName(selfName))
        {
            result = PsBuild.ShellDiagnosticLine(PsBuild.SingleQuote($"{name}: {selfName}: nameref variable self references not allowed")) + "; " + PsBuild.SetStatus(1);
            return true;
        }
        // `typeset -n ref='@'` / `declare -n r=1`: the target is no variable name; bash refuses it (status 1) and declares nothing.
        if (isNameref && !isPrint
            && (exprName is not null && exprValue is not null ? (IsValidVarName(exprName) ? ExtractSingleQuotedOrLiteralValue(exprValue) : null)
                : varName is not null && varName.IndexOf('=') is var nrEq and > 0 && IsValidVarName(varName[..nrEq]) ? varName[(nrEq + 1)..] : null) is { } nrTarget
            && !IsValidVarName(nrTarget) && !IsNamerefElementTarget(nrTarget))
        {
            result = PsBuild.ShellDiagnosticLine(PsBuild.SingleQuote($"{name}: `{nrTarget}': invalid variable name for name reference")) + "; " + PsBuild.SetStatus(1);
            return true;
        }
        // `declare -p ref` for a nameref prints the attribute line; the reference itself is resolved at transpile time, so the
        // runtime has no variable to show.
        if (isPrint && varName is not null && IsValidVarName(varName) && _namerefs is not null && _namerefs.TryGetValue(varName, out var refTargetName)
            && cmd.Words.Skip(1).Count(w => GetLiteralValue(w) is { } x && !x.StartsWith('-')) == 1)
        {
            result = "Invoke-BashEcho " + PsBuild.SingleQuote("declare -n " + BashNameCase.ToBash(varName) + "=\"" + refTargetName + "\"") + "; " + PsBuild.SetStatus(0);
            return true;
        }
        if (isAssoc && !isPrint && _assocVars is not null)
        {
            string? assocName = exprName ?? (varName is null ? null : DeclaredName(varName));
            // `declare -A m; m[0]=x; echo $m`: a bare `$m` reads the element keyed 0.
            if (assocName is not null && IsValidVarName(assocName)) { _assocVars.Add(assocName); _arrayVars?.Add(assocName); }
        }
        if (!isPrint && !isAssoc && _intVars is not null && HasDeclareFlag(cmd, 'i'))
        {
            string? intName = exprName ?? (varName is null ? null : DeclaredName(varName));
            if (intName is not null && IsValidVarName(intName)) { _intVars.Add(intName); NoteGlobalAttr(intName, 'i'); }
        }
        if (!isPrint && !isAssoc && _caseVars is not null
            && DeclareCaseFlag(cmd) != '\0')
        {
            string? caseName = exprName ?? (varName is null ? null : DeclaredName(varName));
            if (caseName is not null && IsValidVarName(caseName))
            {
                _caseVars[caseName] = DeclareCaseFlag(cmd) == 'u';
                NoteGlobalAttr(caseName, _caseVars[caseName] ? 'u' : 'l');
            }
        }
        // `typeset +n ref` drops the nameref attribute: `ref` is a plain variable again, holding the target name.
        if (!isPrint && varName is not null && IsValidVarName(varName) && _namerefs is not null
            && cmd.Words.Skip(1).Any(w => GetLiteralValue(w) is { Length: > 1 } f && f[0] == '+' && f.Contains('n')))
        {
            _namerefs.Remove(varName); _pendingNamerefs?.Remove(varName); _runtimeNamerefs?.Remove(varName);
            result = PsBuild.ClearNameref(varName) + "; " + PsBuild.SetStatus(0);
            return true;
        }
        // `typeset -n ref` with no target: a string assigned earlier (`ref=x`) becomes the target; otherwise the next literal
        // assignment names it, and until then the run-time table holds it with no target (`declare -p ref` prints
        // `declare -n ref`, as bash does; REGRESSION: it was "not found").
        if (isNameref && !isPrint && varName is not null && IsValidVarName(varName) && _namerefs is not null && !_namerefs.ContainsKey(varName)
            && _runtimeNamerefs?.Contains(varName) != true)   // `declare -n r` again keeps the target r has (bash)
        {
            string nrMark = "";
            if (_litAssigned is not null && _litAssigned.TryGetValue(varName, out var known) && IsValidVarName(known) && !VarNameKeys.Equals(known, varName))
            { _namerefs[varName] = known; nrMark = NamerefMark(varName, known) + "; "; }
            else if (_litAssigned is null || !_litAssigned.ContainsKey(varName))
            { _pendingNamerefs?.Add(varName); nrMark = NamerefMark(varName, "") + "; "; }
            result = nrMark + PsBuild.SetStatus(0);
            return true;
        }
        // `declare -n ref='a[2]'` (a quoted element target makes the word non-literal, so it arrives as name + value).
        if (isNameref && !isPrint && exprName is not null && IsValidVarName(exprName) && exprValue is not null && _namerefs is not null
            && ExtractSingleQuotedOrLiteralValue(exprValue) is { } elemRefTarget && IsNamerefElementTarget(elemRefTarget))
        {
            _namerefs[exprName] = elemRefTarget; _runtimeNamerefs?.Remove(exprName); _pendingNamerefs?.Remove(exprName);
            result = NamerefMark(exprName, elemRefTarget) + "; " + PsBuild.SetStatus(0);
            return true;
        }
        if (isNameref && varName is not null && varName.IndexOf('=') is var neq and > 0
            && IsValidVarName(varName[..neq]) && (IsValidVarName(varName[(neq + 1)..]) || IsNamerefElementTarget(varName[(neq + 1)..])) && _namerefs is not null)
        {
            _namerefs[varName[..neq]] = varName[(neq + 1)..]; _runtimeNamerefs?.Remove(varName[..neq]); _pendingNamerefs?.Remove(varName[..neq]);
            result = NamerefMark(varName[..neq], varName[(neq + 1)..]) + "; " + PsBuild.SetStatus(0);
            return true;
        }
        // `declare -n r=$v`: a target known only at run time (PsBuild.DeclareNameref); every expansion and write of r then
        // goes through the run time. REGRESSION: the attribute was dropped, so r held v's value.
        if (isNameref && !isPrint && exprName is not null && IsValidVarName(exprName) && exprValue is not null && _namerefs is not null
            && ExtractSingleQuotedOrLiteralValue(exprValue) is null)
        {
            _namerefs.Remove(exprName); _pendingNamerefs?.Remove(exprName); _litAssigned?.Remove(exprName);
            (_runtimeNamerefs ??= new HashSet<string>(VarNameKeys)).Add(exprName);
            result = PsBuild.DeclareNameref(name, exprName, EmitAssignmentValue(exprValue), inFunction: _functionDepth > 0);
            return true;
        }
        if (!isPrint && exprName is not null && !isAssoc)
        {
            // Value through the assignment machinery (TryEmitJoinedValue for a $( )); the status
            // is declare's own (0), not the substitution's.
            string value = EmitAssignmentValue(exprValue);
            bool intAttr = HasDeclareFlag(cmd, 'i');
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
        bool isInt = HasDeclareFlag(cmd, 'i');
        // A scalar lives where every bash variable read looks: `$env:NAME` (`$x`, `[int]$env:x` in
        // arithmetic). `$global:NAME` was never read back — `declare x=hello; echo $x` printed "".
        if (eq >= 0)
        {
            string declName = varName[..eq];
            string rawVal = varName[(eq + 1)..];
            // `declare s+=x` / `declare -i n+=2` appends to (adds to) the current value, as `s+=x` does. REGRESSION: it
            // emitted `$env:s+ = 'x'`, which failed the parse of the whole script.
            bool appends = declName.EndsWith('+') && IsValidVarName(declName[..^1]);
            if (appends) declName = declName[..^1];
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
                string intVal = appends ? $"[string](Invoke-BashArith {PsBuild.SingleQuote(declName + "+(" + rawVal + ")")})"
                    : BashArithmeticParser.TryPlainDecimal(rawVal, out _)
                    ? PsBuild.SingleQuote(rawVal)
                    : $"[string](Invoke-BashArith {PsBuild.SingleQuote(rawVal)})";
                result = "$env:" + declName + " = " + intVal;
                return true;
            }
            result = "$env:" + declName + " = " + CaseAttr(declName, (appends ? "$env:" + declName + " + " : "") + PsBuild.SingleQuote(rawVal));
            return true;
        }
        bool indexedFlag = cmd.Words.Skip(1).Any(w => GetLiteralValue(w) is { Length: > 1 } fl && fl[0] == '-' && fl.Contains('a'));
        if (isAssoc)
        {
            // Re-declaring an existing associative array keeps its entries (bash: `declare -A d` twice).
            // `x=1; declare -A x`: the scalar becomes the element keyed 0.
            result = "if ($global:" + varName + " -isnot [System.Collections.IDictionary]) { $global:" + varName + " = [PsBash.Cmdlets.BashAssoc]::Unset()"
                + "; if ($null -ne $env:" + varName + ") { $global:" + varName + "['0'] = $env:" + varName + " } }";
            if (_functionDepth == 0) result = PsBuild.ArrayKindGuard(GetLiteralValue(cmd.Words[0]) ?? "declare", varName, toAssoc: true, result + "; " + PsBuild.SetStatus(0));
            return true;
        }
        // `declare -i x`: the attribute only (recorded above); x stays unset (it was set to 0) or keeps its value. An empty
        // array would shadow the scalar a later `x=3+4` writes (`${x[@]}`, `declare -p x`).
        // `declare -a e`: e is an array from here on, so `e=y` writes element 0.
        if (indexedFlag && !isInt && IsValidVarName(varName)) _arrayVars?.Add(varName);
        result = isInt ? PsBuild.SetStatus(0)
            // `declare x` on an existing variable leaves it alone. A name with nothing yet is declared but unset: `declare -a e`
            // makes the empty array; a plain `declare x` only records the mark (declare -p: `declare -- x`). REGRESSION: it
            // made an empty array too, which then hid the scalar a later `x=v` wrote (`declare -p x` lost the value).
            : "if ($null -eq $env:" + varName + " -and $null -eq (Get-Variable -Name " + PsBuild.SingleQuote(varName) + " -Scope Global -ValueOnly -ErrorAction Ignore)) { "
              + (indexedFlag ? "$global:" + varName + " = @(); " : "")
              + "[PsBash.Cmdlets.BashShellState]::MarkAttribute(" + PsBuild.SingleQuote(varName) + ", '" + (indexedFlag ? "~" : "-") + "')" + " }";
        // `declare -a m` on an associative m is refused.
        if (indexedFlag && !isInt && _functionDepth == 0) result = PsBuild.ArrayKindGuard(GetLiteralValue(cmd.Words[0]) ?? "declare", varName, toAssoc: false, result + "; " + PsBuild.SetStatus(0));
        return true;
    }

    /// <summary>
    /// Split a <c>NAME=value</c> word whose value has expansions (<c>x=$(cmd)</c>, <c>x="$y"</c>):
    /// its first part is a literal starting with a valid name and <c>=</c>. False otherwise.
    /// </summary>
    /// <summary>
    /// A builtin's literal operand (<c>path</c>, <c>path=v</c>, <c>path+=v</c>, <c>path[1]=v</c>) with its NAME part as the
    /// store name (<see cref="BashNameCase.ToStore(string)"/>): what the AST records give a name parsed as an assignment.
    /// </summary>
    private static string StoreNameWord(string word)
    {
        int eq = word.IndexOf('=');
        int end = eq < 0 ? word.Length : eq > 0 && word[eq - 1] == '+' ? eq - 1 : eq;
        string mapped = BashNameCase.ToStore(word[..end]);
        return ReferenceEquals(mapped, word) || mapped == word[..end] ? word : mapped + word[end..];
    }

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

        foreach (var r in cmd.Redirects)
        {
            if (r.FdVar is null && GetLiteralValue(r.Target) is null
                && (IsPlainVarRefWord(r.Target) && (r.Op == ">&" && r.Fd == 1 || r.Op == "<&" && r.Fd == 0)
                    || IsGlobWord(r.Target) && r.Op == ">&" && r.Fd == 1))
            {
                result = EmitRunTimeFdRedirect(cmd, r);
                return true;
            }
        }

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
    /// <c>cmd &gt;&amp;$fd</c> / <c>cmd &lt;&amp;$fd</c>: what the word means is known only when it expands
    /// (<c>exec {fd}&gt;f; echo hi &gt;&amp;$fd</c>). A number duplicates that descriptor (<c>Write-BashFd</c> /
    /// <c>Read-BashFd</c>, which also fail an unopened one); for output anything else is a file for both streams
    /// (<c>&gt;&amp;word</c> is <c>&amp;&gt;word</c>), for input <c>-</c> closes stdin. The word is a plain variable
    /// reference, so evaluating it again in the chosen branch has no side effect.
    /// </summary>
    private static string EmitRunTimeFdRedirect(Command.Simple cmd, Redirect dyn)
    {
        // `>& "$@"`: each element is its own word; anything but exactly one is "ambiguous redirect" (status 1, the
        // command does not run). Joined into one string it named a file `2 3 c d`.
        if (IsQuotedArrayAllWord(dyn.Target))
        {
            string one = EmitRunTimeFdRedirectCore(cmd, dyn, "$__psbFd = [string]$__psbFdW[0]; ");
            var only = ((WordPart.DoubleQuoted)dyn.Target.Parts[0]).Parts[0];
            string shown = only is WordPart.BracedVarSub b ? "\"${" + b.Name + b.Suffix + "}\"" : "\"$@\"";
            return "$__psbFdW = @(" + EmitUnquotedSplitWordArray(dyn.Target) + "); if ($__psbFdW.Count -ne 1) { "
                + PsBuild.ShellDiagnosticLine(PsBuild.SingleQuote(shown + ": ambiguous redirect")) + "; " + PsBuild.SetStatus(1) + " } else { " + one + " }";
        }
        // `>& 1*`: the word is pathname-expanded first; one match (a file named 10) is the descriptor 10. No match keeps the
        // pattern (a file); several leave '' so the file branch reports the ambiguous redirect.
        if (IsGlobWord(dyn.Target))
            return EmitRunTimeFdRedirectCore(cmd, dyn, "$__psbFdW = @(" + EmitUnquotedSplitWordArray(dyn.Target)
                + "); $__psbFd = if ($__psbFdW.Count -eq 1) { [string]$__psbFdW[0] } else { '' }; ");
        return EmitRunTimeFdRedirectCore(cmd, dyn, "$__psbFd = [string](" + EmitWord(dyn.Target) + "); ");
    }

    private static string EmitRunTimeFdRedirectCore(Command.Simple cmd, Redirect dyn, string value)
    {
        string plain = EmitSimple(cmd with { Redirects = cmd.Redirects.Remove(dyn) });
        if (dyn.Op == ">&")
        {
            string file = EmitSimple(cmd with { Redirects = cmd.Redirects.Replace(dyn, dyn with { Op = "&>" }) });
            string sink = PsBuild.IsStatementList(plain) ? "& { " + plain + " }" : plain;
            return value + "if ($__psbFd -match '^[0-9]+$') { " + sink + " | Write-BashFd ([int]$__psbFd) } else { " + file + " }";
        }
        var dash = new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal("-")));
        string closed = EmitSimple(cmd with { Redirects = cmd.Redirects.Replace(dyn, dyn with { Target = dash }) });
        return value + "if ($__psbFd -eq '0') { " + plain + " } elseif ($__psbFd -match '^[0-9]+$') { Read-BashFd ([int]$__psbFd) | "
            + plain + " } else { " + closed + " }";
    }

    /// <summary>A word that is only variable references (<c>$fd</c>, <c>"${fd}"</c>): no operator, nothing to run.</summary>
    private static bool IsPlainVarRefWord(CompoundWord w)
    {
        static bool Ref(WordPart p) => p is WordPart.SimpleVarSub || p is WordPart.BracedVarSub { Suffix: null or "", ArgWord: null };
        return !w.Parts.IsDefaultOrEmpty && w.Parts.All(p => Ref(p) || p is WordPart.DoubleQuoted dq && dq.Parts.Length > 0 && dq.Parts.All(Ref));
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
        // `exec -a NAME cmd`: NAME is the command's argv[0]; `-c` (empty environment) and `-l` (login) are dropped.
        // Only a nested shell shows argv[0] (`exec -a NAME sh -c 'echo $0'`): it becomes the -c script's $0.
        CompoundWord? argv0 = null;
        while (cmd.Words.Length > 1 && GetLiteralValue(cmd.Words[1]) is "-a" or "-c" or "-l" or "-cl" or "-lc" or "--")
        {
            string opt = GetLiteralValue(cmd.Words[1])!;
            if (opt == "--") { cmd = new Command.Simple(cmd.Words.RemoveAt(1), cmd.EnvPairs, cmd.Redirects); break; }
            if (opt == "-a")
            {
                if (cmd.Words.Length < 3) break;
                argv0 = cmd.Words[2];
                cmd = new Command.Simple(cmd.Words.RemoveRange(1, 2), cmd.EnvPairs, cmd.Redirects);
                continue;
            }
            cmd = new Command.Simple(cmd.Words.RemoveAt(1), cmd.EnvPairs, cmd.Redirects);
        }
        if (argv0 is not null && cmd.Words.Length == 4 && GetLiteralValue(cmd.Words[1]) is "sh" or "bash"
            && GetLiteralValue(cmd.Words[2]) == "-c")
            cmd = new Command.Simple(cmd.Words.Add(argv0), cmd.EnvPairs, cmd.Redirects);
        if (cmd.Words.Length > 1)
        {
            var inner = new Command.Simple(cmd.Words.RemoveAt(0), cmd.EnvPairs, cmd.Redirects);
            // The command reads the stdin exec had, the enclosing stdin scope's included (EmitSimpleFed). REGRESSION:
            // `echo q | exec cat` and `{ exec cat; } < f` printed nothing. Not when exec's own here-document or `<` file
            // is already piped in (the caller strips it and sets _pipeStageFeedsInput): the scope's feed displaced it.
            result = (_pipeStageFeedsInput ? EmitSimple(inner) : EmitSimpleFed(inner)) + "; " + ErrexitExitStatement();
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
            // `exec {fd}>f`: the runtime picks the descriptor (10 up) and stores it in fd; `exec {fd}>&-` closes $fd.
            steps.Add(r.FdVar is { } fdVar
                ? $"Invoke-BashExecFd -Variable {PsBuild.SingleQuote(fdVar)} -Operator {PsBuild.SingleQuote(r.OpenOperator)} -Target {EmitArgWord(r.Target)}"
                : $"Invoke-BashExecFd {r.Fd} {PsBuild.SingleQuote(r.OpenOperator)} {EmitArgWord(r.Target)}");
        }
        result = string.Join("; ", steps);
        return true;
    }

    // POSIX special builtins (exec and exit aside: a prefix before them is never seen again).
    private static readonly HashSet<string> s_posixSpecialBuiltins = new(StringComparer.Ordinal)
        { ":", ".", "source", "break", "continue", "eval", "export", "readonly", "return", "set", "shift", "times", "trap", "unset" };

    /// <summary>
    /// A special builtin under a run-time <c>set -o posix</c> check: in POSIX mode its prefix assignments are plain
    /// assignments made first (they persist), and a function of the same name is passed over; otherwise the usual form.
    /// </summary>
    private static string EmitPosixSpecialBuiltin(Command.Simple cmd, string name)
    {
        var priorNode = _posixSplitNode;
        var priorFns = _definedFunctions;
        try
        {
            var bare = cmd with { EnvPairs = [] };
            if (priorFns is not null && priorFns.Contains(name))
                _definedFunctions = new HashSet<string>(priorFns.Where(n => n != name), priorFns.Comparer);
            _posixSplitNode = bare;
            string posix = cmd.EnvPairs.IsEmpty
                ? EmitSimple(bare)
                : EmitPersistentPrefix(cmd.EnvPairs, () => EmitSimple(bare));
            _definedFunctions = priorFns;
            _posixSplitNode = cmd;
            return PsBuild.PosixElse(posix, EmitSimple(cmd));
        }
        finally { _definedFunctions = priorFns; _posixSplitNode = priorNode; }
    }

    /// <summary>
    /// The POSIX-mode prefix assignment (<c>set -o posix; v=t :</c>): the assignments are made first and stay, but while
    /// <paramref name="inner"/> runs each is still a temp binding frame (VarFrameStack), so <c>v=t unset v</c> and
    /// <c>v=t eval 'unset v'</c> take the frame and reveal the value under it, as in bash. Every prefix assignment that
    /// persists goes through here; a plain assignment in its place loses the hidden value.
    /// </summary>
    private static string EmitPersistentPrefix(ImmutableArray<EnvPair> pairs, Func<string> inner)
    {
        var sb = new StringBuilder();
        foreach (var p in pairs)
            if (!p.Name.Contains('[')) sb.Append(PsBuild.BindSameScope(p.Name)).Append("; ");
        sb.Append(EmitShAssignment(new Command.ShAssignment([.. pairs.Select(p => new Assignment(p.Name, AssignOp.Equal, p.Value))])));
        // The assignment is emitted before the command: `v=t readonly v` marks v readonly, and an assignment emitted
        // after it would be reported as a write to a readonly variable.
        sb.Append("; try { ").Append(inner()).Append(" } finally { ");
        foreach (var p in pairs)
            if (!p.Name.Contains('[')) sb.Append(PsBuild.PersistSameScope(p.Name)).Append("; ");
        return sb.Append('}').ToString();
    }

    /// <summary>
    /// A builtin the emitter writes (<see cref="BuiltinOptions.EmittedBuiltins"/>), given an option it does not have
    /// (<c>read -Q</c>, <c>export --bogus</c>): bash's usage error (<see cref="PsBuild.BuiltinInvalidOption"/>, status 2)
    /// in place of the builtin, with the command's redirects. A function of that name is the function's business.
    /// </summary>
    private static bool TryEmitInvalidBuiltinOption(Command.Simple cmd, out string result)
    {
        result = "";
        if (cmd.Words.Length < 2 || GetLiteralValue(cmd.Words[0]) is not { } name
            || !BuiltinOptions.EmittedBuiltins.TryGetValue(name, out var spec)
            || _definedFunctions is { } fns && fns.Contains(name))
            return false;
        if (BuiltinOptions.FindInvalid(cmd.Words.Skip(1).Select(TryGetStaticArgValue).ToList(), spec.Letters, spec.ValueLetters) is not var (word, bad))
            return false;
        var diagnostic = PsBuild.BuiltinInvalidOption(name, BuiltinOptions.Spelling(word, bad));
        if (name == "local") diagnostic = PsBuild.LocalInFunction(diagnostic);
        result = cmd.Redirects.IsDefaultOrEmpty ? diagnostic : PsBuild.WithRedirectTail(diagnostic, tail => AppendRedirectTail(tail, cmd.Redirects));
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
        if (cmd0 is not null && _definedFunctions is { Count: > 0 } && _definedFunctions.Contains(cmd0))
            return false;

        if (cmd0 == "exec" && cmd.HereDocs.IsDefaultOrEmpty && cmd.EnvPairs.IsEmpty)
            return TryEmitExec(cmd, out result);

        // `coproc cmd`: a co-process with pipes both ways is not supported by design; say so (it was "coproc: command
        // not found", which read like a missing program).
        if (cmd0 == "coproc")
        {
            result = PsBuild.NotSupported("coproc");
            return true;
        }

        // `unset -n ref` removes the reference itself, so later reads of `ref` are plain again; `unset ref` on a nameref
        // unsets the variable it names (bash). The options are read exactly as Invoke-BashUnset reads them (UnsetOptions:
        // clusters such as -vn, `--`, the first name ends them; -f overrides -n; -f with -v unsets nothing). A nameref with
        // no target yet names no variable: `unset r` removes the reference itself. One whose target is known only at run
        // time is resolved by the cmdlet. REGRESSION: only the words -f/-v/-n were options, so `unset -vn r` unset r's target.
        if (cmd0 == "unset" && (_namerefs is { Count: > 0 } || _pendingNamerefs is { Count: > 0 } || _runtimeNamerefs is { Count: > 0 })
            && cmd.Words.Length >= 2)
        {
            var lits = new string?[cmd.Words.Length];
            for (int i = 1; i < lits.Length; i++) lits[i] = GetLiteralValue(cmd.Words[i]);
            var unsetOptions = UnsetOptions.Parse(lits, 1);
            if (unsetOptions.Known && !unsetOptions.Conflict && !unsetOptions.Functions)
            {
                bool rewrote = false;
                var words = cmd.Words.ToBuilder();
                for (int i = unsetOptions.FirstOperand; i < words.Count; i++)
                {
                    var lit = lits[i];
                    if (lit is null || lit.StartsWith('-')) continue;
                    if (unsetOptions.ByReference) { _namerefs?.Remove(lit); _pendingNamerefs?.Remove(lit); _runtimeNamerefs?.Remove(lit); }
                    else if (_namerefs?.ContainsKey(lit) == true) { words[i] = new CompoundWord([new WordPart.Literal(ResolveNameref(lit))]); rewrote = true; }
                    else _pendingNamerefs?.Remove(lit);
                }
                if (rewrote) cmd = new Command.Simple(words.ToImmutable(), cmd.EnvPairs, cmd.Redirects);
            }
        }

        // unset PROMPT_COMMAND -> Unregister-BashPromptHook -Name 'prompt-command'
        // Checked early (outside the else-if chain) so that `unset OTHER_VAR` still
        // falls through to TryEmitMappedCommand -> Invoke-BashUnset.
        if (cmd0 == "unset" && cmd.Words.Length >= 2
            && GetLiteralValue(cmd.Words[1]) == "PROMPT_COMMAND")
        {
            result = "Unregister-BashPromptHook -Name 'prompt-command'; $env:PROMPT_COMMAND = $null";
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
        // A form that only reports (top-level `return`, `exit 1 2`) takes the command's redirects too: `return 3 2>/dev/null`
        // printed its message anyway.
        bool reportOnly = false;

        // break [N] / continue [N] -> the labelled keyword for the N-th enclosing loop.
        // A redirect on it still opens its target (`break > f` creates f).
        if (cmd0 is "break" or "continue")
            specialResult = (cmd.Redirects.IsDefaultOrEmpty ? "" : EmitNoOpRedirectTouches(cmd)) + EmitLoopControl(cmd0, cmd);

        // At the top of a script or -c string (not a function, not eval/source text), bash refuses `return` with
        // status 2 and goes on; a PowerShell `return` there ended the whole script. A subshell is no different.
        else if (cmd0 == "return" && _functionDepth == 0 && (_context == TranspileContext.Default || _evalAtScriptTop))
        {
            reportOnly = true;
            specialResult = "$(" + EmitNoOpArgPrelude(cmd) + "Write-Error " + PsBuild.SingleQuote("return: can only `return' from a function or sourced script")
                + " -ErrorAction Continue; " + PsBuild.SetStatus(2) + ")";
        }

        // return N -> capture exit code for $?
        else if (cmd0 == "return")
        {
            string evalSignal = _context == TranspileContext.Eval && _functionDepth == 0 && _subshellDepth == 0
                ? PsBuild.EvalReturnSignal : "";
            _leavingReturns++;   // every form below ends with a PowerShell return (RedirectGroup)
            // `return 1 2`: bash checks the status operand, then reports "too many arguments" and drops the rest of the
            // line (PsBuild.UsageDiscard). REGRESSION: no message, and the line went on.
            if (cmd.Words.Length > 2 && !IsNonNumericLiteral(cmd.Words[1]))
                specialResult = PsBuild.UsageDiscard("return: too many arguments") + "; " + evalSignal + "return";
            else if (cmd.Words.Length >= 2)
            {
                if (TryGetPureLiteralText(cmd.Words[1].Parts, out var retLiteral)
                    && long.TryParse(retLiteral, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var retNum))
                {
                    // The status is the low 8 bits: `return 256` is 0, `return 257` is 1.
                    specialResult = $"$global:LASTEXITCODE = {retNum % 256}; " + evalSignal + "return";
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
                        + "else { " + PsBuild.ShellDiagnosticLine("'return: ' + $__psb_r + ': numeric argument required'") + "; $global:LASTEXITCODE = 2 } }; " + evalSignal + "return";
                }
            }
            else
            {
                specialResult = evalSignal + "return";
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
        // `exit 7 8 9`: "too many arguments", status 1, the rest of the line dropped and the shell stays (bash 5.2), a
        // subshell's too. A non-numeric first operand is checked first (below). REGRESSION: `( exit 1 2 )` said nothing.
        else if (cmd0 == "exit" && cmd.Words.Length > 2 && (_context != TranspileContext.Eval || _subshellDepth > 0)
                 && !IsNonNumericLiteral(cmd.Words[1]))
        {
            reportOnly = true;
            specialResult = PsBuild.UsageDiscard("exit: too many arguments");
        }

        else if (cmd0 == "exit" && _subshellDepth > 0)
        {
            specialResult = LeavingReturn(cmd.Words.Length >= 2
                ? $"$global:LASTEXITCODE = {EmitExitCodeValue(cmd.Words[1])}; return"
                : "return");
        }

        // A bare `exit` leaves with the status of the last command; inside an EXIT trap handler that is the status
        // from before the handler ran (see BashTranspiler.TrapEpilogueClose).
        // `exit word` with a literal non-numeric word: "numeric argument required", and the shell leaves with 2.
        else if (cmd0 == "exit" && cmd.Words.Length >= 2 && IsNonNumericLiteral(cmd.Words[1]))
            specialResult = PsBuild.ShellDiagnosticLine(PsBuild.SingleQuote("exit: " + GetLiteralValue(cmd.Words[1]) + ": numeric argument required"))
                + "; $global:LASTEXITCODE = 2; " + ShellExit("2");

        else if (cmd0 == "exit" && cmd.Words.Length == 1)
            specialResult = ShellExit("$(if ($null -ne $global:__BashPreTrapStatus) { $global:__BashPreTrapStatus } else { $global:LASTEXITCODE })");

        else if (cmd0 == "exit" && cmd.Words.Length == 2
                 && IsPureUnquotedVarWord(cmd.Words[1]))
            specialResult = EmitExitWithUnquotedVar(cmd.Words[1]);

        // `exit N` also records N as the status, so an EXIT trap (`trap 'echo rc=$?' EXIT`) sees it: PowerShell's
        // `exit` leaves $global:LASTEXITCODE as it was. In eval/source text the exit is decided at run time (ShellExit).
        else if (cmd0 == "exit" && cmd.Words.Length == 2 && (_context != TranspileContext.Eval || ExitDecidedAtRunTime))
            specialResult = $"$global:LASTEXITCODE = {EmitExitCodeValue(cmd.Words[1])}; " + ShellExit("$global:LASTEXITCODE");

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

        else if (cmd0 == "fc")
            specialResult = EmitFc(cmd.Words.RemoveAt(0));

        else if (cmd0 == "readonly")
            specialResult = EmitReadonly(cmd);

        // `local` / `local -p [NAME...]` inside a function: list the function's locals as `declare -- n="v"` lines.
        else if (cmd0 == "local" && _functionDepth > 0 && _functionLocalNames is { } fnLocals && IsLocalListing(cmd))
            specialResult = EmitLocalListing(cmd, fnLocals);

        // Bare `set` / `declare` / `typeset`: list the variables as `name=value` lines.
        else if (cmd0 is "set" or "declare" or "typeset" && cmd.Words.Length == 1)
            specialResult = EmitMappedStandalone(cmd, EmitPassthrough("Invoke-BashDeclare",
                ImmutableArray.Create(new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal("--bare-list"))))));

        else if (cmd0 == "set" && cmd.Words.Length >= 2)
            specialResult = WithAllExport(cmd, EmitSet(cmd));

        // A bare `local` reached PowerShell as the unknown command `local` (127), also through `run local`.
        else if (cmd0 == "local" && cmd.Words.Length == 1)
            specialResult = PsBuild.BareLocal;

        else if ((cmd0 == "source" || cmd0 == ".") && cmd.Words.Length == 1)
            // bash: `s.sh: line 3: .: filename argument required`, then the usage line WITHOUT the prefix, status 2.
            specialResult = PsBuild.ShellDiagnosticLine(PsBuild.SingleQuote(cmd0 + ": filename argument required")) + "; "
                + PsBuild.StderrLine(cmd0 + ": usage: " + cmd0 + " filename [arguments]") + "; " + PsBuild.SetStatus(2);

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
            result = (reportOnly || cmd0 is "cd" or "read" or "eval" or "fc" or "readonly" or "set" or "source" or "." or "local")
                && !cmd.Redirects.IsDefaultOrEmpty
                    ? WithRedirectTailOn(specialResult, cmd.Redirects)
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
        if (_functionDepth > 0) _runtimeReadonly = true; // the function's static readonly set ends with it
        var roSb = new StringBuilder();
        bool roBad = false;
        for (int i = 1; i < cmd.Words.Length; i++)
        {
            var word = cmd.Words[i];
            // `readonly 'q'` / `readonly "q=1"`: quote removal comes first (the run-time eval of `readonly $names` hands
            // every field over single-quoted).
            var val = GetLiteralValue(word) ?? TryGetStaticArgValue(word);
            if (val is not null && val.StartsWith('-')) continue; // flags like -p, -r
            // `readonly b[7]=8`: readonly takes no subscript. (`[7]` lexes as a glob part.)
            string? roRaw = val ?? TryGetVarOperandName(word); // static text, quotes and escapes removed (`"a b"`, `a\;b`)
            if (roRaw is not null && (roRaw.IndexOf('=') is var eqAt && eqAt < 0 ? roRaw : roRaw[..eqAt]) is var roNm
                && (roNm.Contains('[') || !IsValidVarName(eqAt >= 0 && roNm.EndsWith('+') ? roNm[..^1] : roNm)))
            {
                // bash names the subscripted NAME (`b[7]`), and otherwise the whole word (`1a=3`, `a;echo`, `=x`).
                string shown = roNm.Contains('[') ? roNm.TrimEnd('+') : roRaw;
                roSb.Append("Write-Error ").Append(PsBuild.SingleQuote("readonly: `" + shown + "': not a valid identifier")).Append(" -ErrorAction Continue; ");
                roBad = true;
                continue;
            }
            if (val is not null)
            {
                int eq = val.IndexOf('=');
                if (eq <= 0)
                {
                    roSb.Append("[PsBash.Cmdlets.BashShellState]::MarkReadonly(").Append(PsBuild.SingleQuote(val)).Append("); ");
                    _readonlyVars?.Add(BashNameCase.ToStore(val));
                    continue;
                }
                // `readonly NAME+=text` appends to the current value.
                bool append = val[eq - 1] == '+';
                string roName = BashNameCase.ToStore(append ? val[..(eq - 1)] : val[..eq]); // word text: the store name (BashNameCase)
                roSb.Append("[PsBash.Cmdlets.BashShellState]::MarkReadonly(").Append(PsBuild.SingleQuote(roName)).Append("); ");
                _readonlyVars?.Add(roName);
                roSb.Append("$env:").Append(roName).Append(" = ").Append(append ? "$env:" + roName + " + " : "")
                    .Append(PsBuild.SingleQuote(val[(eq + 1)..])).Append("; ");
            }
            else if (TrySplitAppendInitializer(word, out var appName, out var appValue))
            {
                appName = BashNameCase.ToStore(appName);
                roSb.Append("$env:").Append(appName).Append(" = $env:").Append(appName).Append(" + ")
                    .Append(EmitAssignmentValue(appValue)).Append("; ");
                roSb.Append("[PsBash.Cmdlets.BashShellState]::MarkReadonly(").Append(PsBuild.SingleQuote(appName)).Append("); ");
            }
            else if (TrySplitInitializer(word, out var name, out var value))
            {
                name = BashNameCase.ToStore(name);
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
                if (c == 'E') { effects.Add(("__BashErrtrace", on)); continue; }
                // verbose: tracked for $- / $SHELLOPTS / test -o; the input lines are not echoed (intentional-differences.md).
                if (c == 'v') { effects.Add(("__BashVerbose", on)); continue; }
                if (c == 'o' && k == a.Length - 1 && i + 1 < args.Count && args[i + 1] is "pipefail" or "nounset" or "noclobber" or "errtrace" or "verbose")
                {
                    effects.Add((args[i + 1] switch { "pipefail" => "__BashPipefail", "noclobber" => "__BashNoclobber", "errtrace" => "__BashErrtrace", "verbose" => "__BashVerbose", _ => "__BashNounset" }, on));
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
    /// Options that change nothing ps-bash models (functrace, histexpand, braceexpand, hashall, monitor; allexport is
    /// dropped here too, after WithAllExport has emitted its switch):
    /// `set -eEuo pipefail` is the standard script preamble, so they are accepted and dropped rather than refused.
    /// </summary>
    private static List<string?> StripInertSetOptions(List<string?> args)
    {
        var kept = new List<string?>();
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a is null || a == "--") { for (; i < args.Count; i++) kept.Add(args[i]); break; }
            if (a is "-o" or "+o" && i + 1 < args.Count && args[i + 1] is "functrace" or "histexpand" or "braceexpand" or "hashall" or "monitor" or "allexport")
            { i++; continue; }
            if (a.Length >= 2 && (a[0] == '-' || a[0] == '+') && a[1] != '-' && a.Skip(1).All(char.IsAsciiLetter))
            {
                var letters = new string(a.Skip(1).Where(c => c is not ('T' or 'H' or 'B' or 'h' or 'm' or 'a')).ToArray());
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
            tail = "$global:__BashXtrace = $false; $global:__BashVerbose = $false; Set-PSDebug -Off; " + (rest.Count == 0 ? PsBuild.SetStatus(0) : EmitSetPositionals(rest));
        }
        else if (k < literalArgs.Count)
            tail = EmitSet(cmd with { Words = cmd.Words.Skip(1 + k).Prepend(cmd.Words[0]).ToImmutableArray() }) ?? PsBuild.SetStatus(0);
        else
            tail = PsBuild.SetStatus(0);
        return (optText is null ? "" : optText + "; ") + tail;
    }

    /// <summary>
    /// <c>set -a</c> / <c>set +a</c> / <c>set -o allexport</c> (alone or among other options): the allexport switch goes in
    /// front of whatever the rest of the command emits (the option parsing below drops the letter).
    /// </summary>
    private static string? WithAllExport(Command.Simple cmd, string? emitted)
    {
        bool? on = null;
        for (int i = 1; i < cmd.Words.Length; i++)
        {
            var a = TryGetStaticArgValue(cmd.Words[i]);
            if (a is null || a.Length < 2 || a == "--" || a[0] is not ('-' or '+')) break;
            if (a is "-o" or "+o")
            {
                if (i + 1 < cmd.Words.Length && TryGetStaticArgValue(cmd.Words[i + 1]) == "allexport") on = a[0] == '-';
                i++;
                continue;
            }
            if (a[1] != '-' && a.AsSpan(1).IndexOf('a') >= 0 && a.Skip(1).All(char.IsAsciiLetter)) on = a[0] == '-';
        }
        if (on is null) return emitted;
        return PsBuild.SetAllExport(on.Value) + "; " + (emitted ?? PsBuild.SetStatus(0));
    }

    /// <summary>
    /// The option bash's <c>set</c> rejects in <paramref name="args"/> (its leading option words), spelled as bash
    /// names it (<c>-Q</c>, <c>+Q</c>, <c>--</c> for any <c>--word</c>), or null when every option is one of its letters.
    /// A word known only at run time ends the scan (that <c>set</c> runs then).
    /// </summary>
    internal static string? SetInvalidOption(IReadOnlyList<string?> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a is null || a is "--" or "-" or "+" || a.Length < 2 || a[0] is not ('-' or '+')) return null;
            if (a[0] == '-' && a[1] == '-') return "--";
            bool takesName = false;
            foreach (var c in a.AsSpan(1))
            {
                if (c == 'o') { takesName = true; continue; }
                if ("abefhkmnptuvxBCEHPT".IndexOf(c) < 0) return a[0].ToString() + c;
            }
            if (takesName) i++;
        }
        return null;
    }

    private static string? EmitSet(Command.Simple cmd, List<string?>? argsOverride = null)
    {
        string? specialResult = null;
        var literalArgs = argsOverride ?? cmd.Words.Skip(1).Select(TryGetStaticArgValue).ToList();
        // An option bash's set does not have is a usage error at run time (status 2, nothing set, the script goes on),
        // not a refusal of the whole script.
        if (argsOverride is null && SetInvalidOption(literalArgs) is { } badSetOption)
            return PsBuild.BuiltinInvalidOption("set", badSetOption);
        if (argsOverride is null && TryEmitSetLoneDashOrPlus(cmd, literalArgs) is { } lone) return lone;
        // noexec: nothing after `set -n` runs (and `set +n` can never be reached by a non-interactive shell).
        if (argsOverride is null && literalArgs is ["-n"] or ["-o", "noexec"]) return PsBuild.NoExecStop;
        if (argsOverride is null && literalArgs is ["+n"] or ["+o", "noexec"]) return PsBuild.SetStatus(0);
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
            && BashOptionNames.Contains(remembered) && remembered is not ("errexit" or "xtrace" or "pipefail" or "noglob" or "nounset" or "noclobber" or "noexec" or "onecmd" or "verbose"))
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
            // `set -o "$opt"`, `set "$flags"`: the option is known only at run time, so `set` runs then, as its static
            // form with the words it got (the same runner as `c=set; $c -o pipefail`, BashBuiltins.RunAsSource).
            if (arg is null && argsOverride is null)
                return EmitPassthrough(PsBuild.BuiltinAtRunTime("set"), cmd.Words.RemoveAt(0));
            if (arg is null)
                throw new ParseException("set: dynamic shell options are not supported by ps-bash; use set -- for positional parameters", 1, 1, "shell-option");
            if (!arg.StartsWith('-') && !arg.StartsWith('+')) break;
            if (arg is "-o" or "+o")
            {
                var name = literalArgs[++i];
                if (name is null && argsOverride is null)
                    return EmitPassthrough(PsBuild.BuiltinAtRunTime("set"), cmd.Words.RemoveAt(0));
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
            int dashWord = implicitDashDash ? -1 : cmd.Words.Select((w, k) => (w, k)).Skip(1).First(t => TryGetStaticArgValue(t.w) == "--").k;   // `set "--" a`: quoted, still the separator
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
        // The file's top-level `break` learns from the call site whether a loop is in reach (PsBuild.LoopModeVar).
        return ". $(" + LoopModeForCallSite() + EmitPassthrough(Cmdlet, cmd.Words.RemoveAt(0), cmdletSwitch: " -AsScriptBlock") + ")";
    }

    /// <summary>
    /// A special form's text with the command's redirect tail. A statement list (`cd`, `eval`) is grouped as `. { ... }`
    /// first, so the tail covers every statement and side effects stay in the caller's scope; a single command (`read`)
    /// takes the tail itself. Grouped, `Invoke-BashCat f | . { Invoke-BashRead w } 2>$null` read nothing: the group does
    /// not hand its pipe to the command inside (`read w &lt; f 2>/dev/null` was status 1 with w empty).
    /// </summary>
    private static string WithRedirectTailOn(string emitted, ImmutableArray<Redirect> redirects)
    {
        if (!IsSingleRuntimeCommand(emitted))
            return RedirectGroup(emitted, body => PsBuild.WithRedirectTail(body, tail => AppendRedirectTail(tail, redirects)));
        var sb = new StringBuilder(emitted);
        AppendRedirectTail(sb, redirects);
        return sb.ToString();
    }

    /// <summary>One `Invoke-Bash*` command (no statement list, no keyword statement), which a redirect tail can follow.</summary>
    private static bool IsSingleRuntimeCommand(string emitted) =>
        emitted.StartsWith("Invoke-Bash", StringComparison.Ordinal) && !PsBuild.HasUnquotedStatementSeparator(emitted);
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
            return PsBuild.ShellDiagnosticLine("': command not found'") + "; $global:LASTEXITCODE = 127";
        // A command word that is only a command substitution: the substitution runs, and when it expands to
        // nothing no command runs and the status is the substitution's (+""+"true"+""+, $(false), $(exit 42)).
        if (cmd.EnvPairs.IsEmpty && cmd.Words.Length == 1 && cmd.Words[0].Parts.Length == 1
            && cmd.Words[0].Parts[0] is WordPart.CommandSub)
            return "$__psbash_cs = " + EmitAssignmentValue(cmd.Words[0]) + "; if ($__psbash_cs) { $__psbash_cw = @(ConvertTo-BashWords $__psbash_cs); & (Resolve-BashCommand $__psbash_cw[0]" + RunTimeNameLoopModeArg() + ") @($__psbash_cw | Select-Object -Skip 1) }";

        // Env-var prefix: VAR=value cmd — set VAR only for the duration of cmd,
        // then restore to its previous value to prevent leaking into the shell.
        if (!cmd.EnvPairs.IsEmpty)
        {
            // Save original values, then open a try block.
            foreach (var envPair in cmd.EnvPairs)
                sb.Append($"$__saved_{envPair.Name} = $env:{envPair.Name}; ");
            sb.Append(PsBuild.TempExportBegin(cmd.EnvPairs.Select(p => p.Name))).Append("; ");
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
        // A quoted command substitution (`"$(cmd)"`) is one word, never elided, like `"$x"`: an empty one names no
        // command (": command not found", 127). Emitted as `& "$(...)"`, an empty name was a PowerShell error.
        bool dynamicName = needsCallOperator && dashName is null
            && (IsBareVariableCommandWord(cmd.Words[0]) || cmd.Words[0].Parts is [WordPart.DoubleQuoted { Parts: [WordPart.CommandSub] }]);
        // `${b}uiltin declare x`, `${z}ommand readonly y`: a name glued to text is split and looked up the same way. A word
        // with a path separator (`$dir/bin/tool`) keeps its path emission.
        bool affixName = needsCallOperator && dashName is null && !dynamicName && IsUnquotedVarAffixWord(cmd.Words[0])
            && cmd.Words[0].Parts.All(p => p is not WordPart.Literal { Value: var lv } || lv.IndexOfAny(['/', '\\']) < 0);
        dynamicName |= affixName;
        // `$(cmd) args`, `` `true` X ``: the substitution's words split, the first is the command; none leaves the next
        // operand the command, and with none at all the status is the last substitution's ($(exit 42) $(exit 43) is 43).
        bool cmdSubName = needsCallOperator && dashName is null && !dynamicName && cmd.Words.Length > 1
            && cmd.Words[0].Parts is [WordPart.CommandSub] && IsUnquotedSplitWord(cmd.Words[0]);
        dynamicName |= cmdSubName;
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
            sb.Append("& { $__dyn = " + wordsExpr + "; if ($__dyn.Count -gt 0) { $__dynrest = @($__dyn | Select-Object -Skip 1); & (Resolve-BashCommand $__dyn[0]" + RunTimeNameLoopModeArg() + ") @__dynrest" + extra + " } else { $global:LASTEXITCODE = 0 } }");
            AppendRedirectTail(sb, cmd.Redirects);
            if (!cmd.EnvPairs.IsEmpty)
            {
                sb.Append(" } finally { ");
                sb.Append(PsBuild.TempExportEnd).Append("; ");
                foreach (var envPair in cmd.EnvPairs)
                    sb.Append($"$env:{envPair.Name} = $__saved_{envPair.Name}; ");
                sb.Append('}');
            }
            return sb.ToString();
        }
        if (dynamicName)
        {
            // A pipe stage (`echo x | $c`): -Piped makes the run-time command take its input only when it reads stdin
            // (StdinReaders.ShouldFeed); piped a record, a cmdlet with no pipeline-input parameter failed to bind it.
            leading = _pipeStageFeedsInput ? "& (Resolve-BashCommand -Piped " : "& (Resolve-BashCommand ";
            // `$SUDO rm f` / `CC='gcc -O2'; $CC -c x.c`: an unquoted name splits first. No word leaves the next one the
            // command (none at all: nothing runs, status 0); more than one makes the rest leading operands.
            string loopArg = RunTimeNameLoopModeArg();
            commandWordText = cmdSubName
                ? "-Words " + EmitUnquotedSplitWordArray(cmd.Words[0]) + " -KeepStatus" + loopArg + ")"
                : affixName
                ? "-Words " + EmitUnquotedSplitWordArray(cmd.Words[0]) + loopArg + ")"
                : IsPureUnquotedVarWord(cmd.Words[0])
                ? "-Words " + EmitUnquotedVarSplitArray(cmd.Words[0]) + loopArg + ")"
                : commandWordText + loopArg + ")";
        }

        // A script-defined function named like a mapped command (`cd() { �; }`, `seq() { �; }`): PowerShell resolves an
        // alias before a function, and the command is aliased � so call the function explicitly (bash: functions first).
        if (!dynamicName && !needsCallOperator && cmd.Words.Length > 0 && GetLiteralValue(cmd.Words[0]) is { Length: > 0 } fnName
            && _definedFunctions is { Count: > 0 } && _definedFunctions.Contains(fnName) && IsAliasedCommandName(fnName))
        {
            var q = PsBuild.SingleQuote(fnName);
            // `builtin` has no alias to fall back to (before the definition runs, or after `unset -f builtin`): the
            // run-time command peels it and runs the rest past functions.
            var fallback = fnName == "builtin" ? "{ " + PsBuild.ArgvLiterals + "; Invoke-BashCommand 'builtin' " + PsBuild.ArgvSplat + " }" : q;
            leading = "& ((Get-Command -Name " + q + " -CommandType Function -ErrorAction SilentlyContinue) ?? " + fallback + ") ";
            commandWordText = "";
        }

        // A PowerShell statement keyword (exit/return/break/…) cannot take a
        // splatted argument — `exit @__bashsplat0` is a parse error that poisons
        // the whole emitted file. Fall back to plain emission for those.
        bool splatSafeCommand = cmd.Words.Length == 0
            || GetLiteralValue(cmd.Words[0]) is not string cw
            || !PsStatementKeywordCommands.Contains(cw);

        // `git x 2>&1`, `make &>log`, `{ cc a.c; } 2>&1`: a program whose stderr bash merges into its stdout runs
        // through Invoke-BashNative, which starts it with ONE pipe for both (PowerShell reads two and interleaves them
        // in whatever order its readers ran). Its dash operands are quoted: `-foo:bar` is one argument.
        bool mergeRunner = !dynamicName && !needsCallOperator && !isScriptCommand && leading.Length == 0 && cmd.Words.Length > 0
            && NativeMergeRunner(cmd);
        if (mergeRunner) leading = "Invoke-BashNative -- ";

        // An operand PowerShell's binder would rewrite: every dash word for a run-time name or a merged-stderr run, else
        // the words no PowerShell callee receives intact (QuotedDashOperand). A Verb-Noun command keeps its syntax.
        // A script file run in place (`./x.sh` -> `bash ./x.sh`) is a call of the bash cmdlet: every dash word is its operand.
        bool quoteEveryDash = dynamicName || mergeRunner || isScriptCommand;
        bool powerShellCommand = !quoteEveryDash && cmd.Words.Length > 0 && GetLiteralValue(cmd.Words[0]) is { } headName
            && IsPowerShellCommandName(headName);
        string? DashOperand(CompoundWord w) => OrderedArgDashLiteral(w) is { } d
            && (quoteEveryDash || (splatSafeCommand && !powerShellCommand && BinderRewritesOperand(d))) ? d : null;

        if (splatSafeCommand && (HasUnquotedVarSplatArg(commandArgs, loneArraySplats: true) || commandArgs.Any(HasIndirectExpansion)))
        {
            // Hoist each unquoted-variable operand to a temp var and splat it.
            // The command word keeps its existing emission (incl. the `& `
            // call-operator prefix for a quoted command word, or the `bash ` prefix for a script).
            // A run-time command name is resolved first, as bash expands words left to right: `$(exit 42) $(exit 43)`
            // leaves 43, and `$empty $(exit 3)` leaves 3 (the hoisted operands ran before the name).
            sb.Append(EmitCommandWithSplatArgs(
                dynamicName ? "& $__bashhead" : leading + commandWordText,
                commandArgs,
                argIndex => DashOperand(commandArgs[argIndex]) is { } dynDash
                    ? PsBuild.SingleQuote(dynDash)
                    : QuoteCommaLiteralWord(commandArgs[argIndex], EmitWord(commandArgs[argIndex])),
                headPrelude: dynamicName ? "$__bashhead = " + leading[2..] + commandWordText + "; " : ""));
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
                    if (DashOperand(cmd.Words[i]) is { } dynDashArg)
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
            sb.Append(PsBuild.TempExportEnd).Append("; ");
            foreach (var envPair in cmd.EnvPairs)
                sb.Append($"$env:{envPair.Name} = $__saved_{envPair.Name}; ");
            sb.Append('}');
        }

        return sb.ToString();
    }

    /// <summary>
    /// <c>v=t f</c> with f a function of this unit: the binding is f's context; <c>v=t eval ...</c> / <c>source</c> /
    /// <c>.</c>: the current one's. Other commands cannot reach <c>local</c>/<c>unset</c> under the binding, so no
    /// frame (a function only a run-time <c>source</c> defines is not known here: no frame either).
    /// </summary>
    private static bool? TempFrameKind(Command.Simple cmd)
    {
        if (cmd.Words.IsEmpty || TryGetStaticArgValue(cmd.Words[0]) is not { } name) return null;
        if (name is "eval" or "source" or ".") return false;
        return _unitFunctions?.Contains(name) == true ? true : null;
    }

    /// <summary>
    /// Wrap an already-emitted command in the <c>VAR=val cmd</c> env-prefix
    /// save/set/restore so the assignment applies for the duration of the command
    /// and is then restored (no leak), regardless of which emission path produced
    /// <paramref name="inner"/>.
    /// </summary>
    /// <param name="pairs">The <c>VAR=val</c> bindings in front of the command.</param>
    /// <param name="inner">The command, already emitted.</param>
    /// <param name="functionCall">
    /// The temp bindings' variable-context frames (VarFrameStack): true for a call of a shell function (the binding
    /// belongs to the called function), false for eval/source/. (it belongs to the current one), null for none.
    /// </param>
    private static string WrapEnvPairs(ImmutableArray<EnvPair> pairs, string inner, bool? functionCall = null)
    {
        // Every value is evaluated against the PRE-assignment environment (`a=1 b=$a cmd` gives b the old a).
        var values = WithEnvShadow(pairs, () => pairs.Select(p => EmitAssignmentValue(p.Value)).ToArray());
        var sb = new StringBuilder();
        // eval/source/. run in this PowerShell scope, where a binding inside them (`v=1 eval 'v=2 eval ...'`) would
        // overwrite `$__saved_v`: their saved values live on the runtime's binding stack instead.
        bool sameScope = functionCall == false;
        foreach (var p in pairs)
            sb.Append(sameScope ? PsBuild.BindSameScope(p.Name) : $"$__saved_{p.Name} = $env:{p.Name}").Append("; ");
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
        // The binding is exported for the command, whatever the name's attribute was.
        sb.Append(PsBuild.TempExportBegin(pairs.Select(p => p.Name))).Append("; ");
        sb.Append("try { ");
        for (int i = 0; i < pairs.Length; i++)
            sb.Append($"$env:{pairs[i].Name} = {values[i]}; ");
        if (pairs.Any(p => p.Name == "PATH"))
            sb.Append(PsBuild.NormalizePathVariable).Append("; ");
        if (functionCall == true)
            foreach (var p in pairs)
                sb.Append(PsBuild.PushTempFrame(p.Name)).Append("; ");
        sb.Append(inner);
        sb.Append(" } finally { ");
        sb.Append(PsBuild.TempExportEnd).Append("; ");
        // A binding whose frame `unset` took is not put back (bash: later writes went to the outer variable).
        foreach (var p in pairs)
            sb.Append(sameScope ? PsBuild.UnbindSameScope(p.Name)
                : functionCall == true ? PsBuild.RestoreTempBinding(p.Name)
                : $"$env:{p.Name} = $__saved_{p.Name}").Append("; ");
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
            // Not a PowerShell stream: `9>f.txt` / `9>$null` were literal ARGUMENTS ("hi 9>f.txt"), and `9>&1` ended in
            // PowerShell's background `&`. (A simple command opens it around itself: ScopedHighFdRedirects.)
            _ when r.Fd > 6 => $"<# ps-bash: fd {r.Fd} {r.Op} redirect ignored #>",
            ">&" or "<&" when r.Fd > 2 && target == "0" => $"<# ps-bash: fd {r.Fd} {r.Op} redirect ignored #>",
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
    /// one result is the file (no match leaves a glob as its own text); several, or none for a variable, is "ambiguous redirect",
    /// naming the word as written (<c>$x: ambiguous redirect</c>): the <see cref="RedirectTarget.Ambiguous"/> stand-in, which
    /// fails the redirect's open (status 1, the command does not run).
    /// </summary>
    private static string EmitFileRedirectTarget(CompoundWord word)
    {
        // `> "$@"`, `> "${a[@]}"`: one word per element, so anything but exactly one is ambiguous too.
        // `> two-$star`: a variable glued to text is split and pathname-expanded too (star='*' writes two-bar).
        if (IsGlobWord(word) || IsPureUnquotedVarWord(word) || IsQuotedArrayAllWord(word) || IsUnquotedVarAffixWord(word))
            return OneWordOrAmbiguous(EmitUnquotedSplitWordArray(word), word);
        // `> a-{one,two}`: brace expansion gives several words, also an ambiguous redirect (the array reached -Path).
        if (!word.Parts.IsDefaultOrEmpty && word.Parts.Any(p => p is WordPart.BracedTuple or WordPart.BracedRange))
            return OneWordOrAmbiguous(EmitArgWord(word), word);
        return TransformRedirectTarget(EmitArgWord(word));
    }

    /// <summary>The single word <paramref name="words"/> expands to, else the ambiguous-redirect stand-in naming <paramref name="word"/>.</summary>
    private static string OneWordOrAmbiguous(string words, CompoundWord word)
        => "$(& { $__rt = @(" + words + "); if ($__rt.Count -eq 1) { $__rt[0] } else { [PsBash.Core.RedirectTarget]::Ambiguous("
           + PsBuild.SingleQuote(WordAsWritten(word)) + ") } })";

    private static string TransformRedirectTarget(string target)
    {
        if (target == DevicePath.Null)
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
        PsBuild.EscapeDoubleQuoteMarks(body);

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

    /// <summary>Index of the <c>}</c> closing the <c>${</c> whose <c>{</c> is at <paramref name="openBrace"/>
    /// (<see cref="BashLexer.DollarBraceClose"/>: `${u:-{h}}` closes at its first `}`); -1 if unclosed.</summary>
    private static int FindMatchingBrace(string s, int openBrace) => BashLexer.DollarBraceClose(s, openBrace - 1);

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
            // `a=([$i]=x)`: an indexed subscript with an expansion is arithmetic on its expanded text (bash).
            if (TrySplitExpandedKey(elem, out var keyExpr, out var keyHead, out var keyParts))
            {
                if (keyHead.Length > 0) keyParts = keyParts.Insert(0, new WordPart.Literal(keyHead));
                entries.Add("[string](Invoke-BashArith ([string]" + keyExpr + "))");
                entries.Add("(" + EmitAssignmentValue(new CompoundWord(keyParts)) + ")");
                continue;
            }
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
        => TryEmitAssocLiteral(array, out literal, out _, numericKeysAllowed);

    /// <summary>
    /// <c>a=([k]=v{1,2})</c>: each <c>[KEY]=</c> element holding an unquoted brace expansion, its subscript text quoted so
    /// the element is a plain word (brace expansion still applies). Null unless there is such an element and no other
    /// <c>[KEY]=</c> element (a mixed list keeps the subscripted path).
    /// </summary>
    private static ArrayWord? BraceExpandedKeyedElements(ArrayWord array)
    {
        bool any = false;
        var elems = ImmutableArray.CreateBuilder<CompoundWord>(array.Elements.Length);
        foreach (var elem in array.Elements)
        {
            string? first = elem.Parts.IsDefaultOrEmpty ? null : elem.Parts[0] switch
            {
                WordPart.Literal l => l.Value,
                WordPart.GlobPart g => g.Pattern,
                _ => null,
            };
            bool keyed = first is { Length: > 2 } && first[0] == '['
                && (first.Contains("]=", StringComparison.Ordinal)
                    || first[^1] == ']' && elem.Parts.Length > 1 && elem.Parts[1] is WordPart.Literal { Value: var eq } && eq.StartsWith('='));
            if (!keyed) { elems.Add(elem); continue; }
            if (!elem.Parts.Any(p => p is WordPart.BracedTuple or WordPart.BracedRange)) return null;
            any = true;
            elems.Add(new CompoundWord(elem.Parts.SetItem(0, new WordPart.SingleQuoted(first!))));
        }
        return any ? new ArrayWord(elems.MoveToImmutable()) : null;
    }

    /// <summary>True when some element is <c>[KEY]=…</c> with a KEY that is not all digits (<c>[n]=x</c>, <c>[k1]=v</c>).</summary>
    private static bool HasNonNumericSubscript(ArrayWord array)
    {
        foreach (var elem in array.Elements)
        {
            string? first = elem.Parts.IsDefaultOrEmpty ? null : elem.Parts[0] switch
            {
                WordPart.Literal l => l.Value,
                WordPart.GlobPart g => g.Pattern,
                _ => null,
            };
            if (first is not { Length: > 2 } || first[0] != '[') continue;
            int close = first.IndexOf(']');
            if (close > 1 && !first.AsSpan(1, close - 1).ContainsAnyExceptInRange('0', '9')) continue;
            if (close > 1) return true;
        }
        return false;
    }

    /// <summary>
    /// <paramref name="indexedLiteral"/>: the same pairs as an INDEXED array, each subscript evaluated as arithmetic
    /// (bash: <c>a=([k1]=v1 [k2]=v2)</c> on a variable that is not associative stores both at <c>a[0]</c>, k1 and k2
    /// being unset).
    /// </summary>
    private static bool TryEmitAssocLiteral(ArrayWord array, out string literal, out string indexedLiteral, bool numericKeysAllowed = false)
    {
        literal = "";
        indexedLiteral = "";
        if (array.Elements.IsDefaultOrEmpty) return false;
        var entries = new List<string>(array.Elements.Length);
        var indexedEntries = new List<string>(array.Elements.Length);
        foreach (var elem in array.Elements)
        {
            string keyExpr, head;
            ImmutableArray<WordPart> parts;
            if (TrySplitStaticKey(elem, numericKeysAllowed, out var staticKey, out head, out parts))
                keyExpr = PsBuild.SingleQuote(staticKey);
            // `[$j-1]=e` / `[${j}2]=f` / `[$k]=v` on an array known to be associative: the key is the expanded subscript.
            else if (!numericKeysAllowed || !TrySplitExpandedKey(elem, out keyExpr, out head, out parts))
                return false;
            if (head.Length > 0) parts = parts.Insert(0, new WordPart.Literal(head));
            // bash 5.2 leaves a tilde on the right of an associative `[key]=value` literal (an indexed one expands it).
            if (parts.Any(p => p is WordPart.TildeSub))
                parts = parts.Select(p => p is WordPart.TildeSub ts ? (WordPart)new WordPart.Literal("~" + ts.User + (ts.SlashConsumed ? "/" : "")) : p).ToImmutableArray();
            string valueExpr = "(" + EmitAssignmentValue(new CompoundWord(parts)) + ")";
            entries.Add(keyExpr + ", " + valueExpr);
            indexedEntries.Add("[string](Invoke-BashArith ([string]" + keyExpr + ")), " + valueExpr);
        }
        literal = "[PsBash.Cmdlets.BashAssoc]::FromPairs(" + string.Join(", ", entries) + ")";   // bash's key order and case-sensitive keys, not a @{} literal
        indexedLiteral = "[PsBash.Cmdlets.BashSparseArray]::FromEntries(" + string.Join(", ", indexedEntries) + ")";
        return true;
    }

    /// <summary>
    /// The <c>[KEY]=</c> head of an array-literal element whose subscript is plain or quoted text: the key after quote
    /// removal, the text after the <c>=</c> and the value's remaining parts.
    /// </summary>
    private static bool TrySplitStaticKey(CompoundWord elem, bool numericKeysAllowed, out string key, out string head, out ImmutableArray<WordPart> parts)
    {
        key = head = "";
        parts = default;
        // The lexer reads the subscript `[k1]` as a glob part, then `=v1` as a literal; a single-quoted key
        // (`['$k']=v`) arrives as `[`, the quoted part, then `]=v`: join the run up to the `]=`.
        var firstText = new StringBuilder();
        int used = 0;
        while (used < elem.Parts.Length)
        {
            string? piece = elem.Parts[used] switch
            {
                WordPart.Literal l => l.Value,
                WordPart.GlobPart g => g.Pattern,
                WordPart.SingleQuoted sq when used > 0 => "'" + sq.Value + "'",
                _ => null,
            };
            if (piece is null) break;
            firstText.Append(piece);
            used++;
            if (piece.Contains("]=", StringComparison.Ordinal)
                || (piece.EndsWith(']') && used < elem.Parts.Length && elem.Parts[used] is WordPart.Literal { Value: var nx } && nx.StartsWith('=')))
                break;
        }
        string first = firstText.ToString();
        if (used == 0 || first.Length <= 2 || first[0] != '[') return false;
        if (first[^1] == ']' && elem.Parts.Length > used && elem.Parts[used] is WordPart.Literal { Value: var eqTail } && eqTail.StartsWith('='))
        {
            key = first[1..^1];
            head = eqTail[1..];
            parts = elem.Parts.RemoveRange(0, used + 1);
        }
        else
        {
            int close = first.IndexOf("]=", StringComparison.Ordinal);
            if (close < 2) return false;
            key = first[1..close];
            head = first[(close + 2)..];
            parts = elem.Parts.RemoveRange(0, used);
        }
        // `['a+1']=c` / `["q r"]=2` / `["k\"j"]=v`: the subscript is raw text, so it gets bash's quote removal
        // (BashQuoting); stripping only the outer quotes kept `k\"j` as the key.
        if (key.Length == 0 || (!numericKeysAllowed && key.All(char.IsDigit))) return false;
        key = PsBash.Core.BashQuoting.RemoveQuotes(key);
        if (key.Length == 0) return false;
        return true;
    }

    /// <summary>
    /// The <c>[KEY]=</c> head of an element whose subscript holds an expansion (<c>[$j-1]=e</c>, <c>[${j}2]=f</c>,
    /// <c>["$k"]=v</c>): the subscript's expanded text as a PowerShell string (no splitting, no globbing), the text after
    /// the <c>=</c> and the value's remaining parts. bash expands an associative subscript like a double-quoted word.
    /// </summary>
    private static bool TrySplitExpandedKey(CompoundWord elem, out string keyExpr, out string head, out ImmutableArray<WordPart> parts)
    {
        keyExpr = head = "";
        parts = default;
        var keyParts = ImmutableArray.CreateBuilder<WordPart>();
        bool expands = false;
        for (int used = 0; used < elem.Parts.Length; used++)
        {
            var p = elem.Parts[used];
            string? text = p switch { WordPart.Literal l => l.Value, WordPart.GlobPart g => g.Pattern, _ => null };
            if (used == 0)
            {
                if (text is not { Length: > 0 } || text[0] != '[') return false;
                text = text[1..];
            }
            if (text is null)
            {
                if (p is not (WordPart.SingleQuoted or WordPart.EscapedLiteral)) expands = true;
                keyParts.Add(p);
                continue;
            }
            int close = text.IndexOf("]=", StringComparison.Ordinal);
            if (close >= 0)
            {
                if (close > 0) keyParts.Add(new WordPart.Literal(text[..close]));
                head = text[(close + 2)..];
                parts = elem.Parts.RemoveRange(0, used + 1);
            }
            else if (text.EndsWith(']') && used + 1 < elem.Parts.Length && elem.Parts[used + 1] is WordPart.Literal { Value: var eq } && eq.StartsWith('='))
            {
                if (text.Length > 1) keyParts.Add(new WordPart.Literal(text[..^1]));
                head = eq[1..];
                parts = elem.Parts.RemoveRange(0, used + 2);
            }
            else
            {
                if (text.Length > 0) keyParts.Add(new WordPart.Literal(text));
                continue;
            }
            if (!expands || keyParts.Count == 0) return false;
            keyExpr = "([string](" + FlattenPartsToDoubleQuotedString(keyParts.ToImmutable()) + "))";
            return true;
        }
        return false;
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

    /// <summary>A word as a command argument. Number-shaped literal text (`1e5`, `0x10`, `08`) is single-quoted:
    /// bare, PowerShell reads it as a number (PsBuild.QuoteArgumentNumber).</summary>
    private static string EmitWord(CompoundWord word)
        => PsBuild.QuoteArgumentNumber(EmitWordText(word));

    private static string EmitWordText(CompoundWord word)
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

        // `\ x`, `\ `: PowerShell drops a backtick-escaped space or tab at the start of an argument (`echo \ ` printed
        // an empty line, `echo \ x` printed "x"), so such a word is one string.
        if (word.Parts is [WordPart.EscapedLiteral { Value: " " or "\t" }, ..])
            return FlattenPartsToDoubleQuotedString(word.Parts);

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
            string bare = EmitBarewordPart(part, first: i == 0);
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
        return (ContainsCommandSub(parts) || HasStringHostileVar(parts)) && TryJoin(parts, inDq: true, out expr);
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
                case WordPart.BracedVarSub bv when inDq && BracedVarLeavesString(bv): return true;
            }
        }
        return false;
    }

    /// <summary>
    /// A <c>${.}</c> that must not be emitted inside a PS string's <c>$( )</c>: its argument word holds a command
    /// substitution, or its operator argument or subscript holds a double-quote mark (<c>"</c>, U+201C-U+201E).
    /// PowerShell rewrites those marks even inside a literal nested in the <c>$( )</c> of an expandable string:
    /// <c>"$('a""b')"</c> is <c>a"b</c> (a pair becomes one, a backtick before one is consumed), so
    /// <c>echo "${u:-a\"\"b}"</c> printed <c>a"b</c> and <c>"${m[kU+201C U+201D]}"</c> read another key. Such a word is a join.
    /// </summary>
    private static bool BracedVarLeavesString(WordPart.BracedVarSub b)
        => (b.ArgWord is { } arg && ContainsCommandSub(arg)) || HasDoubleQuoteMark(b.Suffix) || HasDoubleQuoteMark(b.Name);

    // A ${...} anywhere in the word (also inside "...") that BracedVarLeavesString.
    private static bool HasStringHostileVar(ImmutableArray<WordPart> parts)
    {
        foreach (var p in parts)
        {
            if (p is WordPart.BracedVarSub b && BracedVarLeavesString(b)) return true;
            if (p is WordPart.DoubleQuoted dq && HasStringHostileVar(dq.Parts)) return true;
        }
        return false;
    }
    private static bool HasDoubleQuoteMark(string? text)
    {
        if (text is null) return false;
        foreach (char c in text)
            if (PsBuild.IsDoubleQuoteMark(c)) return true;
        return false;
    }

    /// <summary>A word that expands <c>$(( ... ))</c> (which may fail with the shell's diagnostic).</summary>
    private static bool ContainsArithSub(ImmutableArray<WordPart> parts)
    {
        foreach (var p in parts)
        {
            if (p is WordPart.ArithSub) return true;
            if (p is WordPart.DoubleQuoted dq && ContainsArithSub(dq.Parts)) return true;
            if (p is WordPart.BracedVarSub { ArgWord: { } arg } && ContainsArithSub(arg)) return true;
            // `${a[1/0]}`: an indexed subscript is an arithmetic expression (`s.sh: line 6: 1/0: division by 0`).
            if (p is WordPart.BracedVarSub { Suffix: { } suffix } && IsArithmeticSubscript(suffix.StartsWith("#L[", StringComparison.Ordinal) ? suffix[2..] : suffix))
                return true;
        }
        return false;
    }

    /// <summary>
    /// <paramref name="text"/> starts with an indexed subscript that is evaluated as arithmetic and so can fail
    /// (<c>[1/0]</c>, <c>[i+1]</c>): not <c>[@]</c>, <c>[*]</c> or digits only.
    /// </summary>
    private static bool IsArithmeticSubscript(string text)
        => text is ['[', ..] && BashParser.FindSubscriptClose(text, 0) is > 1 and var close
           && text.AsSpan(1, close - 1) is var sub && sub is not ("@" or "*") && !IsDigitsOnlySubscript(sub);

    /// <summary>A subscript of digits only (<c>a[3]</c>): evaluating it cannot fail, so it needs no diagnostic path. Not a bare-number decision (PsNumberLiteral owns those).</summary>
    private static bool IsDigitsOnlySubscript(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return false;
        foreach (char c in text) if (c is < '0' or > '9') return false;
        return true;
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
                case WordPart.BracedVarSub bvs when inDq && BracedVarLeavesString(bvs):
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
        return PsBuild.SingleQuote(sb.ToString());
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

    // One brace's alternatives as part lists. A quoted empty item (`{"",a}`) becomes an empty single-quoted part, so the
    // word it makes counts as quoted and stays (DropsEmptyBraceWord).
    private static List<ImmutableArray<WordPart>> BraceAlternatives(WordPart part, bool leading)
    {
        var items = ExpandBrace(part);
        var alts = new List<ImmutableArray<WordPart>>(items.Count);
        for (int i = 0; i < items.Count; i++)
            alts.Add(part is WordPart.BracedTuple tuple && tuple.IsQuotedEmpty(i)
                ? ImmutableArray.Create<WordPart>(new WordPart.SingleQuoted(""))
                : BraceItemParts(items[i], leading));
        return alts;
    }

    /// <summary>
    /// True when a word brace expansion produced is dropped if it comes out empty: bash removes an unquoted word that
    /// expands to nothing (<c>{a,}</c> is one word, <c>{,}</c> none, <c>$e{a,}</c> none with <c>e</c> unset), and keeps
    /// one with any quoted part (<c>{"",a}</c>, <c>''{a,}</c>, <c>{"$e",b}</c>). A tilde or glob part is never empty.
    /// </summary>
    internal static bool DropsEmptyBraceWord(ImmutableArray<WordPart> word)
    {
        foreach (var p in word)
            if (p is WordPart.SingleQuoted or WordPart.AnsiCQuoted or WordPart.DoubleQuoted or WordPart.EscapedLiteral
                or WordPart.TildeSub or WordPart.GlobPart)
                return false;
        return true;
    }
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
        foreach (var p in parts)
        {
            if (p is WordPart.BracedTuple or WordPart.BracedRange)
            {
                braceCount++;
                // `{$a,b}`: an item with an expansion is text that must be expanded, not printed.
                if (p is WordPart.BracedTuple t && t.Items.Any(i => i.Contains('$') || i.Contains('`')))
                    dynamicPart = true;
                else if (braceCount == 1 && ReferenceEquals(p, parts[0]) && p is WordPart.BracedTuple lead && lead.Items.Any(i => i.StartsWith('~')))
                    dynamicPart = true;
            }
            else if (p is not (WordPart.Literal or WordPart.EscapedLiteral or WordPart.SingleQuoted or WordPart.GlobPart))
                dynamicPart = true;
        }
        // `~{/src,root}`: the parser left the leading ~ as text (brace expansion comes first); each resulting word's
        // tilde prefix is expanded afterwards (`~/src ~root`).
        bool leadingTilde = parts.Length > 1 && parts[0] is WordPart.Literal { Value: "~" } && parts[1] is WordPart.BracedTuple or WordPart.BracedRange;
        if (leadingTilde) dynamicPart = true;
        // `{a,b}$x`, `{1..3}"$y"`, `{a,b}$(cmd)`: a non-literal part cannot be spliced into a single-quoted
        // item (it emitted `a$env:x`). Build each combination as a part list and emit it as one expansion-aware word.
        if (dynamicPart)
        {
            var combos = new List<ImmutableArray<WordPart>> { ImmutableArray<WordPart>.Empty };
            foreach (var part in parts)
            {
                var alts = part is WordPart.BracedTuple or WordPart.BracedRange
                    ? BraceAlternatives(part, leading: combos.Count == 1 && combos[0].IsEmpty)
                    : new List<ImmutableArray<WordPart>> { ImmutableArray.Create(part) };
                CheckBraceWords((long)combos.Count * alts.Count);
                var next = new List<ImmutableArray<WordPart>>(combos.Count * alts.Count);
                foreach (var head in combos)
                    foreach (var alt in alts)
                        next.Add(AppendBraceParts(head, alt));
                combos = next;
            }
            if (leadingTilde) combos = combos.ConvertAll(RetildeBraceWord);
            // A word with no quoted part that comes out empty is dropped (DropsEmptyBraceWord): known now when its text
            // is literal (`{a,}`), at run time when it holds an expansion (`{$a,b}_c` with `$a_c` unset). A quoted one
            // stays (`{"$a",b}`). A run-time drop needs the `;` form: in a comma list an empty pipeline is a $null item.
            var elements = new List<string>(combos.Count);
            bool runtimeDrop = false;
            foreach (var combo in combos)
            {
                string word = FlattenPartsToDoubleQuotedString(combo);
                if (!DropsEmptyBraceWord(combo)) elements.Add(word);
                else if (TryGetPureLiteralText(combo, out var text)) { if (text.Length > 0) elements.Add(word); }
                else { elements.Add($"({word} | Where-Object {{ $_ -ne '' }})"); runtimeDrop = true; }
            }
            return $"@({string.Join(runtimeDrop ? "; " : ",", elements)})";
        }
        if (braceCount >= 2)
        {
            // Each word carries whether any of its text was quoted: an empty one that was not is dropped (`{,a}{,b}`).
            var combos = new List<(string Text, bool Quoted)> { ("", false) };
            foreach (var part in parts)
            {
                var segment = new List<(string Text, bool Quoted)>();
                if (part is WordPart.BracedTuple or WordPart.BracedRange)
                {
                    var braceItems = ExpandBrace(part);
                    for (int i = 0; i < braceItems.Count; i++)
                        segment.Add((braceItems[i], part is WordPart.BracedTuple tuple && tuple.IsQuotedEmpty(i)));
                }
                else segment.Add((BraceAffixText(part), part is not WordPart.Literal));
                CheckBraceWords((long)combos.Count * segment.Count); // bound the product before building it
                var next = new List<(string Text, bool Quoted)>(combos.Count * segment.Count);
                foreach (var head in combos)
                    foreach (var s in segment)
                        next.Add((head.Text + s.Text, head.Quoted || s.Quoted));
                combos = next;
            }
            var crossItems = new List<string>(combos.Count);
            foreach (var c in combos)
                if (c.Text.Length > 0 || c.Quoted)
                    crossItems.Add(PsBuild.SingleQuote(c.Text));
            return $"@({string.Join(',', crossItems)})";
        }

        // Collect prefix (parts before brace), brace expansion, suffix (parts after brace).
        var prefix = new StringBuilder();
        var suffix = new StringBuilder();
        WordPart? bracePart = null;
        bool foundBrace = false;
        bool affixQuoted = false;   // `''{a,}`: a quoted affix keeps the empty word

        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (!foundBrace && part is WordPart.BracedTuple or WordPart.BracedRange)
            {
                bracePart = part;
                foundBrace = true;
                continue;
            }
            affixQuoted |= part is not WordPart.Literal;
            (foundBrace ? suffix : prefix).Append(BraceAffixText(part));
        }

        string pre = prefix.ToString();
        string suf = suffix.ToString();
        var all = ExpandBrace(bracePart!);
        // `{a,}`, `{,}`: a bare empty item makes an empty unquoted word, which bash drops; `{"",a}` keeps its "".
        var expanded = new List<string>(all.Count);
        for (int i = 0; i < all.Count; i++)
            if (all[i].Length > 0 || pre.Length > 0 || suf.Length > 0 || affixQuoted
                || (bracePart is WordPart.BracedTuple tuple && tuple.IsQuotedEmpty(i)))
                expanded.Add(all[i]);

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

    /// <summary>One word of a brace-expanded <c>~{...}</c>: its leading literal text re-read up to the first <c>/</c> so the
    /// tilde prefix expands (<c>~/src</c> is the home directory's src, <c>~root</c> another user's home).</summary>
    private static ImmutableArray<WordPart> RetildeBraceWord(ImmutableArray<WordPart> word)
    {
        int n = 0;
        var text = new StringBuilder();
        while (n < word.Length && word[n] is WordPart.Literal l) { text.Append(l.Value); n++; }
        string s = text.ToString();
        int slash = s.IndexOf('/');
        string prefix = slash < 0 ? s : s[..(slash + 1)];
        var head = BashParser.DecomposeWord(prefix, braceExpansion: false);
        if (head.IsDefaultOrEmpty || head[0] is not WordPart.TildeSub) return word;
        if (slash >= 0 && slash + 1 < s.Length) head = head.Add(new WordPart.Literal(s[(slash + 1)..]));
        return head.AddRange(word.RemoveRange(0, n));
    }

    private static List<string> ExpandBrace(WordPart part)
    {
        if (part is WordPart.BracedTuple tuple)
            return new List<string>(tuple.Items);

        // The ONE sequence expander (BraceSequence: step, direction, zero padding, the word cap), shared with a
        // nested `{x,{1..10..3}}`.
        var range = (WordPart.BracedRange)part;
        return new BraceSequence(false, range.Start, range.End, range.Step, range.ZeroPad).Words();
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
        // Only a canonical integer (PsBuild.IsBareIntegerValue: no leading zero, no sign but `-`, at most 18
        // digits) may stay bare; `08`, `-0`, `1e5` are quoted items. long.TryParse cannot fail on one.
        if (items.Count >= 2 && items.All(PsBuild.IsBareIntegerValue)
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
            if (PsBuild.IsBareIntegerValue(item))
                formatted.Add(item);
            else
                formatted.Add(PsBuild.SingleQuote(item));
        }
        return $"@({string.Join(',', formatted)})";
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

    /// <summary>A part spliced into an unquoted PowerShell word. Literal text PowerShell would read as syntax
    /// (<see cref="PsBuild.BarewordNeedsEscape"/>: `{`/`}` start a scriptblock, `echo a{b}c` split into three; a quote
    /// mark such as U+2019 opens a string, `echo it's` was a parse error; a leading en dash made `echo -n` a
    /// parameter) is one quoted string as a whole word, else backtick-escaped. <paramref name="first"/>: the part
    /// starts the word. Guard PsQuoteMarkGuardTests.</summary>
    private static string EmitBarewordPart(WordPart part, bool wholeWord = false, bool first = false)
        => part is WordPart.Literal { Value: var v } && PsBuild.BarewordNeedsEscape(v, wholeWord || first)
            ? wholeWord ? PsBuild.DoubleQuote(v) : PsBuild.EscapeBareword(v, first)
            : EmitWordPart(part);

    private static string EmitWordPart(WordPart part) => part switch
    {
        WordPart.Literal lit => lit.Value,
        // `\b`, `\e`, `\n`... are plain letters in bash; a backtick before one would be a PowerShell escape (bell, ESC, LF).
        WordPart.EscapedLiteral el => el.Value.Length == 1 && char.IsAsciiLetterOrDigit(el.Value[0]) ? el.Value : $"`{el.Value}",
        WordPart.SingleQuoted sq => PsBuild.SingleQuote(sq.Value),
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
            // Braced: in a string a following `:` (`w=a:~+:~-`) would fold into `$env:PWD:` (a drive-qualified name).
            "+" => "${env:PWD}",      // ~+  -> $PWD (the shell form, docs/specs/paths.md)
            "-" => "${env:OLDPWD}",   // ~-  -> $OLDPWD
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
        // `${x:?}` inside ends only the substitution (bash: a subshell), so it needs the scope that catches it; so does
        // an expansion that can discard the line (`$(echo ${!r})` ends only the substitution).
        return pipeable && CannotChangeShellState(body) && !inner.Contains("BashParamErrorException", StringComparison.Ordinal)
            && !PsBuild.CanDiscardLine(inner)
            // `$(echo $BASHPID)` reads the id the subshell scope sets (PsBuild.BashPidRead), so it needs that scope.
            && !inner.Contains(PsBuild.BashPidRead, StringComparison.Ordinal)
            // ... and so does `$(echo $BASH_SUBSHELL)`, whose depth the scope raises.
            && !inner.Contains(PsBuild.SubshellDepthRead, StringComparison.Ordinal)
            ? inner : PsBuild.ShellStateScope(WithSubshellExitTrap(inner));
    }

    /// <summary>The command a `builtin`/`command` wrapper runs (`builtin command read v` -> `read`), or null when a
    /// word is not literal or the wrapper takes an option (`command -v x` runs nothing).</summary>
    private static string? WrappedCommandName(Command.Simple s)
    {
        int at = 0;
        while (at < s.Words.Length && GetLiteralValue(s.Words[at]) is "builtin" or "command") at++;
        if (at < s.Words.Length && GetLiteralValue(s.Words[at]) == "--") at++;
        return at < s.Words.Length && GetLiteralValue(s.Words[at]) is { } name && !name.StartsWith('-') ? name : null;
    }
    /// <summary>Builtins that change shell state even though they map to a runtime command.</summary>
    private static readonly HashSet<string> StateChangingBuiltins = new(StringComparer.Ordinal)
    {
        "set", "shopt", "trap", "alias", "unalias", "export", "unset", "declare", "typeset", "local",
        "readonly", "cd", "pushd", "popd", "source", ".", "eval", "shift", "getopts", "read", "mapfile",
        "readarray", "let", "exec", "umask", "hash", "builtin", "command", "enable", "exit",
        "return", "wait", "break", "continue", "history", "fc",
    };

    /// <summary>
    /// The <see cref="StateChangingBuiltins"/> a pipe stage runs as a subshell of its own (EmitPipelineStages): all but the
    /// wrappers (a <c>builtin</c>/<c>command</c> stage is guarded by what it wraps, and <c>command grep</c> must keep
    /// streaming) and <c>wait</c> (which reads the shell's own job table). A loop jump is one: the stage keeps its loop
    /// level (<see cref="Command.Subshell.KeepsLoopLevel"/>), so <c>echo q | break</c> ends only the stage.
    /// </summary>
    private static readonly HashSet<string> SubshellStageBuiltins =
        new(StateChangingBuiltins.Where(n => n is not ("builtin" or "command" or "wait")), StringComparer.Ordinal);

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
        string inner = EmitCaptured(body, scoped: true);
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
        try { inner = EmitCaptured(body, scoped: true); }
        finally { if (nested) _dqNestDepth--; }
        // The newline join uses [char]10, NOT a "`n" literal — the SAME reason
        // EmitArithCommandSubValue does. This fragment can land inside an
        // arbitrarily nested "$( … )", where the OUTER string scanner consumes
        // the backtick escape and the inner string ends early ("The string is
        // missing the terminator"). Hit git-completion.bash, where a command sub
        // nested two double-quote levels deep broke the whole file's parse.
        return $"{CommandSubStringPrefix}{PipelineHead(body, inner)} | ConvertTo-BashCapture -Join)))";
    }

    // `<( ... )` is a subshell as `$( ... )` is: the same scope (PipelineHead), so `cat <(f)` with f calling exit, or
    // turning -e on and failing, ends only the substitution. Without it the run-time exit ended the whole script.
    private static string EmitProcessSub(WordPart.ProcessSub ps)
    {
        var body = (Command)ps.Body;
        string inner = EmitCaptured(body, scoped: true);
        return $"(Invoke-ProcessSub {{ {PipelineHead(body, inner)} }})";
    }

    private static string EmitProcessSubPipeline(WordPart.ProcessSub ps)
    {
        var body = (Command)ps.Body;
        string inner = EmitCaptured(body, scoped: true);
        return $"(Invoke-ProcessSubPipeline {{ {PipelineHead(body, inner)} }})";
    }

    private static string EmitArithSub(WordPart.ArithSub arith)
        => $"$({EmitArithmeticInvocation(arith.Expr)})";

    /// <param name="syntax">The expression.</param>
    /// <param name="label">The command evaluating it, which bash names in an error (<c>let</c>, <c>((</c>):
    /// <c>s.sh: line 3: let: 1/0: division by 0</c> (Invoke-BashArith -Label). After the expression, so the
    /// invocation still starts <c>Invoke-BashArith 'EXPR'</c>.</param>
    private static string EmitArithmeticInvocation(ArithmeticSyntax syntax, string? label = null)
    {
        // Left-trimmed only: bash's error names the expression with its trailing blanks and its error token runs to the
        // end (`$(( 1/0 ))`: `1/0 : division by 0 (error token is "0 ")`).
        string expr = syntax.Source.TrimStart();
        var fragments = BuildArithFragments(expr, textual: syntax.Root is ArithmeticExpr.Deferred);
        string argument = fragments is null
            ? PsBuild.SingleQuote(expr)
            : $"('' + {string.Join(" + ", fragments)})";
        var invocation = $"Invoke-BashArith {argument}" + (label is null ? "" : " -Label " + PsBuild.SingleQuote(label));
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
        if (name is not ("RANDOM" or "SRANDOM" or "SECONDS" or "EPOCHSECONDS" or "BASHPID" or "BASH_SUBSHELL" or "PPID" or "UID" or "EUID")) return false;
        int k = end;
        while (k < expr.Length && expr[k] == ' ') k++;
        if (k < expr.Length && expr[k] == '=' && !(k + 1 < expr.Length && expr[k + 1] == '=')) return false; // assignment
        if (k + 1 < expr.Length && expr[k + 1] == '=' && expr[k] is '+' or '-' or '*' or '/' or '%') return false;
        if (PsBuild.TryMapSpecialVar(name, braced: false, inDoubleQuote: false) is not { } mapped) return false;
        length = end - start;
        valueExpr = mapped;
        return true;
    }

    /// <summary>`-Line N` for Get-BashIndirectValue: the bash line of the statement being emitted, which `${!ref}` with
    /// ref=LINENO expands to (a direct `$LINENO` is substituted by the lexer); empty when the line is unknown.</summary>
    private static string IndirectLineArg() =>
        PsBuild.TrapLine > 0 ? " -Line " + PsBuild.TrapLine.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";

    /// <summary>Index of the `}` closing the `${` whose `{` is at <paramref name="open"/> (the lexer's extent,
    /// <see cref="BashLexer.DollarBraceClose"/>); -1 when unclosed.</summary>
    private static int FindBraceEnd(string s, int open) => BashLexer.DollarBraceClose(s, open - 1);

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
        string inner = EmitCaptured(body, scoped: true);
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
        if (PositionalIndex.TryParse(sigil, out int position))
        {
            if (position == 0)
                inner = "$global:BashPositional0";
            else
            {
                int idx = position - 1;
                inner = $"if ({PsBuild.PositionalIsShells}) {{ $global:BashPositional[{idx}] }} else {{ $args[{idx}] }}";
            }
        }
        else
        {
            inner = sigil switch
            {
                "#" => "if (" + PsBuild.PositionalIsShells + ") { $global:BashPositional.Count } else { $args.Count }",
                "@" => "if (" + PsBuild.PositionalIsShells + ") { $global:BashPositional -join ' ' } else { $args -join ' ' }",
                "*" => "if (" + PsBuild.PositionalIsShells + ") { $global:BashPositional -join " + PsBuild.IfsJoinSeparator + " } else { $args -join " + PsBuild.IfsJoinSeparator + " }",
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

        // A nameref whose target is known only at run time (`declare -n r; r=$v`, a `for` over one): the run time reads it.
        if (IsRuntimeNameref(name)) return RuntimeNamerefRead(name, strict);
        name = ResolveNameref(name);
        // `declare -n ref='a[2]'`: the nameref names an array element.
        if (IsNamerefElementTarget(name))
        {
            int lb = name.IndexOf('[');
            string elem = EmitBracedVar(new WordPart.BracedVarSub(name[..lb], name[lb..]), inDoubleQuote);
            // Glued to a literal (`ref=$ref`) a bare `$a['2']` would expand only `$a`.
            return !inDoubleQuote && elem.StartsWith('$') && !elem.StartsWith("$(") ? "$(" + elem + ")" : elem;
        }
        // A function's lexical local (a local array, IFS) is its PowerShell variable; in a background job's body a loop
        // variable is the job's copy. Any other variable, a loop variable included, is the shell variable.
        if (IsJobCaptured(name) || (_lexicalLocals is not null && _lexicalLocals.Contains(name)))
            return $"${name}";

        if (arrayElem0 && _arrayVars is not null && _arrayVars.Contains(name))
        {
            // $Matches (BASH_REMATCH) is keyed by int; every other array reads by the string key.
            string elem0 = "$(if ($null -eq $" + name + ") { $env:" + name + " } else { $" + name
                + (name == "BASH_REMATCH" ? "[0]" : "['0']") + " })";
            // Under nounset an array with no element 0 (`local -a x; echo $x`) is unbound, as a scalar never set is.
            // REGRESSION: an array name read through this branch skipped the check and expanded to nothing.
            return strict && _nounsetMode ? StrictRead(name, elem0) : elem0;
        }

        var special = PsBuild.TryMapSpecialVar(name, braced: false, inDoubleQuote);
        if (special is not null) return strict && _nounsetMode ? StrictPositional(name, special) : special;
        var plain = $"$env:{EnvShadowName(name) ?? name}";
        return strict && _nounsetMode && EnvShadowName(name) is null ? StrictRead(name, plain) : plain;
    }

    /// <summary>`$1`..`$N` past the last positional parameter is unbound under nounset (`$@` `$*` `$#` `$0` never are).</summary>
    private static string StrictPositional(string name, string special)
        => name.Length > 0 && name[0] != '0' && name.All(char.IsAsciiDigit)
            ? $"$(if ($global:__BashNounset -and @(if ({PsBuild.PositionalIsShells}) {{ $global:BashPositional }} else {{ $args }}).Count -lt {name}) {{ {PsBuild.ParamErrorThrowStatements(PsBuild.SingleQuote(name + ": "), "'unbound variable'")} }} else {{ {special} }})"
            : special;

    /// <summary>
    /// <c>$r</c> / <c>${r}</c> / <c>${#r}</c> of a nameref the run time resolves: its target's value, and under nounset an
    /// unset target, or none yet, is r's unbound variable (bash: <c>typeset -n ref; set -u; echo $ref</c> is
    /// "ref: unbound variable", status 1). REGRESSION: it expanded to nothing and the script went on.
    /// </summary>
    private static string RuntimeNamerefRead(string name, bool strict)
        => strict && _nounsetMode ? StrictRead(name, PsBuild.ReadThroughNameref(name)) : PsBuild.ReadThroughNameref(name);

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
    /// (not a bash special parameter), or a positional parameter: <c>$1</c> / <c>${10}</c> holding <c>a b</c> is two
    /// words to bash (as an argument, a for-list item, an array element); it reached commands as one.
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

        return name is not null && (IsOrdinaryVarName(name) || name.Length > 0 && name.All(char.IsAsciiDigit));
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

        // A nameref the run time resolves (PsBuild.ReadThroughNameref) holds its target's value: any string, which splits.
        if (IsRuntimeNameref(name))
            return true;

        // A loop variable bound from quoted items holds an arbitrary string: it splits like any variable
        // (EmitSimpleVar renders it bare, `$cfg`, which the split array takes just as well as `$env:cfg`).
        if (_splitLoopVars is not null && _splitLoopVars.Contains(name))
            return true;
        // Any other loop variable holds one already-split word (a list item, a number, a `read` field).
        if (_storeLoopVars is not null && _storeLoopVars.Contains(name))
            return false;

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
           || IsUnquotedPositionalAffixWord(word) || IsUnquotedVarAffixWord(word) || IsQuotedArrayAffixWord(word) || IsUnquotedQuotedAltWord(word)
           || IsUnquotedPositionalSliceWord(word);

    /// <summary>
    /// Bare <c>${@:2:2}</c> / <c>${*: -1}</c>: the slice is a list of parameters, each split again like bare <c>$@</c>.
    /// Emitted as one argument it was an array beside the other words (<c>echo ${@:2:2} ${*: -1}</c>: "Cannot convert
    /// System.Object[] to System.String").
    /// </summary>
    private static bool IsUnquotedPositionalSliceWord(CompoundWord word)
        => word.Parts is [WordPart.BracedVarSub { Name: "@" or "*", Suffix: { } s }] && IsPositionalSliceSuffix(s);

    /// <summary>
    /// Bare <c>${x+"$x"}</c> / <c>${x:+"--opt=$x"}</c> / <c>${a[@]+"${a[@]}"}</c>: the quoted alternative is its words when it is
    /// chosen and NO word when it is not (x unset: <c>for e in ${x+"$x"}</c> runs zero times). It was one empty argument.
    /// </summary>
    private static bool IsUnquotedQuotedAltWord(CompoundWord word)
        => word.Parts is [WordPart.BracedVarSub { Suffix: { } s, Name: var n, ArgWord: { } aw }]
           && aw is [WordPart.DoubleQuoted] && n is not ("@" or "*") && QuotedAltOp(s) is not null;

    /// <summary>`:+` / `+` for an alternative suffix, read past the `!indirect` mark of <c>${!r+word}</c>; null otherwise.</summary>
    private static string? QuotedAltOp(string suffix)
    {
        string s = suffix.StartsWith("!indirect", StringComparison.Ordinal) ? suffix["!indirect".Length..] : suffix;
        return s.StartsWith(":+", StringComparison.Ordinal) ? ":+" : s.StartsWith('+') ? "+" : null;
    }

    /// <summary>The words of an <see cref="IsUnquotedQuotedAltWord"/> word: the same expansion with the alternative <c>x</c>
    /// tells whether the alternative is chosen (every name form goes through the one operator path), then the quoted word.</summary>
    private static string EmitQuotedAltWordArray(CompoundWord word)
    {
        var bvs = (WordPart.BracedVarSub)word.Parts[0];
        string op = (bvs.Suffix!.StartsWith("!indirect", StringComparison.Ordinal) ? "!indirect" : "") + QuotedAltOp(bvs.Suffix)!;
        string probe = EmitBracedVar(new WordPart.BracedVarSub(bvs.Name, op + "x", ImmutableArray.Create<WordPart>(new WordPart.Literal("x"))));
        var alt = new CompoundWord(bvs.ArgWord!.Value);
        string words = IsUnquotedSplitWord(alt) ? EmitUnquotedSplitWordArray(alt) : EmitArgWord(alt);
        return "@(if ([string](" + probe + ") -ceq 'x') { " + words + " })";
    }

    /// <summary>Bare <c>${@#a}</c> / <c>${*/x/y}</c> / <c>${@%d}</c>: the operator applies to each parameter, so the result is a list of words.</summary>
    private static bool IsUnquotedPositionalOpWord(CompoundWord word)
        => word.Parts is [WordPart.BracedVarSub { Name: "@" or "*", Suffix: { Length: > 0 } s }] && (s[0] is '^' or ',' || s.Length > 1 && s[0] is '/' or '%' or '#'
           || s == "@a"); // bare `${@@a}`: every result is empty, so no word at all (it printed one space per parameter)

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
            // `[$(echo " ")]`, `x$(cmd)y`: an unquoted command substitution glued to text splits the same way (it was
            // one argument, so `$(echo " ")]` kept the space).
            // `${v:-AxBxC}x`, `${f%.c}.o`: an operator result glued to text is split like the bare `${v:-.}` word.
            if ((name is not null && (name is "@" or "*" || ordinaryVar && (IsOrdinaryVarName(name) || name.Length > 0 && name.All(char.IsAsciiDigit))))
                || (ordinaryVar && p is WordPart.CommandSub)
                || (ordinaryVar && p is WordPart.BracedVarSub { Suffix: not null, Name: var opName } && opName is not ("@" or "*")
                    && IsUnquotedDefaultAltWord(new CompoundWord(ImmutableArray.Create(p)))))
            {
                if (found >= 0) return -1;
                found = i;
                continue;
            }
            if (p is not (WordPart.Literal or WordPart.EscapedLiteral or WordPart.SingleQuoted or WordPart.DoubleQuoted)) return -1;
        }
        return found;
    }

    /// <summary>The text before an affixed expansion, mapped like a path word's start: <c>/tmp/a$x</c> names the temp directory
    /// and <c>/c/Users/$x</c> the drive, as <see cref="TransformWordPath"/> does for the unsplit word.</summary>
    private static string AffixPrefixExpr(ImmutableArray<WordPart> prefix)
    {
        if (!_operandsAreText && TryStripTmpPrefix(prefix, out var rest))
            return "(" + PsBuild.TempDirExpr + " + '/'" + (rest.IsDefaultOrEmpty ? "" : " + " + FlattenPartsToDoubleQuotedString(rest)) + ")";
        if (!_operandsAreText && prefix[0] is WordPart.Literal { Value: var first } && TryTranslateMsysDrivePath(first, out var drive))
            return FlattenPartsToDoubleQuotedString(prefix.SetItem(0, new WordPart.Literal(drive)));
        return FlattenPartsToDoubleQuotedString(prefix);
    }

    /// <summary><c>=$*=</c> / <c>a$@</c> unquoted: one field per parameter (each split on IFS), the text glued to the first and last.</summary>
    private static bool IsUnquotedPositionalAffixWord(CompoundWord word) => UnquotedAffixExpansionIndex(word, ordinaryVar: false) >= 0;

    /// <summary><c>a$x</c> / <c>$x.c</c> / <c>"q"$x</c> / <c>f=$x</c> unquoted: the variable's value is split on IFS, the text glued to the
    /// first and last field (x='a b': <c>a$x</c> is <c>aa</c> <c>b</c>). A word with a glob keeps its glob form.</summary>
    private static bool IsUnquotedVarAffixWord(CompoundWord word)
        => UnquotedAffixExpansionIndex(word, ordinaryVar: true) >= 0 && !IsGlobWord(word) && !IsBraceGlobWord(word);

    /// <summary>The fields of an affixed expansion word (see <see cref="UnquotedAffixExpansionIndex"/>), as an array expression.</summary>
    private static string EmitAffixWordArray(CompoundWord word, int idx)
    {
        var parts = word.Parts;
        string pre = idx == 0 ? "''" : AffixPrefixExpr(ImmutableArray.Create(parts, 0, idx));
        string suf = idx == parts.Length - 1 ? "''" : FlattenPartsToDoubleQuotedString(ImmutableArray.Create(parts, idx + 1, parts.Length - idx - 1));
        string values = parts[idx] is WordPart.SimpleVarSub { Name: "@" or "*" } or WordPart.BracedVarSub { Name: "@" or "*" }
            ? "@(" + PsBuild.BuildPositionalExpansion("@") + ")"
            : "@(" + EmitWord(new CompoundWord(ImmutableArray.Create(parts[idx]))) + ")";
        // `""$A` / `$A""`: a quoted affix is a field of its own when the value starts / ends with IFS whitespace, even empty.
        string quoted = (HasQuotedPart(parts, 0, idx) ? " -PrefixQuoted" : "") + (HasQuotedPart(parts, idx + 1, parts.Length) ? " -SuffixQuoted" : "");
        return "@(ConvertTo-BashWords -Prefix " + pre + " -Suffix " + suf + " -Fields " + values + quoted + ")";
    }

    private static bool HasQuotedPart(ImmutableArray<WordPart> parts, int from, int to)
    {
        for (int i = from; i < to; i++)
            if (parts[i] is WordPart.SingleQuoted or WordPart.DoubleQuoted) return true;
        return false;
    }

    /// <summary>
    /// Bare <c>${a[i]}</c> (one subscript, not <c>@</c>/<c>*</c>): the element is word-split like any unquoted expansion, so an
    /// unset or empty element is no argument at all (<c>echo x ${a[7]} y</c> prints <c>x y</c>).
    /// </summary>
    private static bool IsUnquotedElementWord(CompoundWord word)
        => word.Parts is [WordPart.BracedVarSub { Name: var n, Suffix: { Length: > 2 } s }]
           && IsOrdinaryVarName(n) && s[0] == '[' && s[^1] == ']' && BashParser.FindSubscriptClose(s, 0) == s.Length - 1
           && s is not ("[@]" or "[*]") && s[1] != '@' && s[1] != '*';

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
               // A length `${#v}` is split too: with IFS=5, the 5 of a five-character value is no word at all.
               || (s.Length > 0 && s[0] is '#' or '%' or '/' or '^' or ',')
               // `echo ${A@P} - x` of an assoc A with no key 0: the empty result is no argument (it printed ` - x`).
               || (s.Length == 2 && s[0] == '@' && s[1] is 'Q' or 'K' or 'k' or 'A' or 'a' or 'P' or 'E')
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
    /// <summary>
    /// <c>export -p test_var{0..5}</c>, <c>readonly -p</c>, <c>local -p</c>, <c>declare</c>: a brace word of plain
    /// text is expanded here, one operand per item, so the declaration paths see names. Left as a brace word,
    /// <c>export</c>/<c>local</c> fell to the general command path ("export: command not found") and <c>readonly</c>
    /// dropped it. A word with an <c>=</c>, an expansion, a quote or a glob character keeps its old form.
    /// </summary>
    private static Command.Simple ExpandDeclarationBraceWords(Command.Simple cmd)
    {
        if (cmd.Words.Length < 2 || GetLiteralValue(cmd.Words[0]) is not ("export" or "readonly" or "local" or "declare" or "typeset"))
            return cmd;
        ImmutableArray<CompoundWord>.Builder? expanded = null;
        for (int i = 1; i < cmd.Words.Length; i++)
        {
            var items = StaticBraceItems(cmd.Words[i]);
            if (items is null) { expanded?.Add(cmd.Words[i]); continue; }
            if (expanded is null)
            {
                expanded = ImmutableArray.CreateBuilder<CompoundWord>();
                for (int k = 0; k < i; k++) expanded.Add(cmd.Words[k]);
            }
            foreach (var item in items)
                expanded.Add(new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal(item))));
        }
        return expanded is null ? cmd : cmd with { Words = expanded.ToImmutable() };
    }

    /// <summary>
    /// <c>readonly 'b' 'c'</c>, <c>export 'NAME'</c>: a quoted operand that is a plain name (or a flag) is that word
    /// unquoted. The run-time eval of <c>readonly $names</c> / <c>export "$v"</c> quotes each field (<c>${w@Q}</c>), so
    /// its text always has this shape; it ran nothing (readonly) or was "export: command not found".
    /// </summary>
    private static Command.Simple UnquotePlainDeclarationOperands(Command.Simple cmd)
    {
        if (cmd.Words.Length < 2 || GetLiteralValue(cmd.Words[0]) is not ("export" or "readonly" or "local" or "declare" or "typeset"))
            return cmd;
        ImmutableArray<CompoundWord>.Builder? words = null;
        for (int i = 1; i < cmd.Words.Length; i++)
        {
            var w = cmd.Words[i];
            if (GetLiteralValue(w) is null && TryGetStaticArgValue(w) is { } s
                && System.Text.RegularExpressions.Regex.IsMatch(s, "^(?:[A-Za-z_][A-Za-z0-9_]*|[-+][A-Za-z]+)$"))
            {
                if (words is null) { words = ImmutableArray.CreateBuilder<CompoundWord>(); words.AddRange(cmd.Words.Take(i)); }
                words.Add(new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal(s))));
            }
            else words?.Add(w);
        }
        return words is null ? cmd : cmd with { Words = words.ToImmutable() };
    }

    /// <summary><c>export [-p] NAME...</c> with plain names and nothing else, as the parser's declaration node.</summary>
    private static Command.ShAssignment? AsPlainExport(Command.Simple cmd)
    {
        if (GetLiteralValue(cmd.Words[0]) != "export" || !cmd.EnvPairs.IsEmpty || !cmd.Redirects.IsDefaultOrEmpty
            || !cmd.HereDocs.IsDefaultOrEmpty)
            return null;
        var pairs = ImmutableArray.CreateBuilder<Assignment>();
        foreach (var w in cmd.Words.Skip(1))
        {
            if (GetLiteralValue(w) is not { } s) return null;
            if (s == "-p") continue;
            if (!System.Text.RegularExpressions.Regex.IsMatch(s, "^[A-Za-z_][A-Za-z0-9_]*$")) return null;
            pairs.Add(new Assignment(s, AssignOp.Equal, null));
        }
        return pairs.Count == 0 ? null : new Command.ShAssignment(pairs.ToImmutable(), IsExport: true);
    }

    /// <summary>The words a brace word of plain text expands to (cross-multiplied), or null when it is not one.</summary>
    private static List<string>? StaticBraceItems(CompoundWord word)
    {
        if (!HasBraceExpansion(word.Parts)) return null;
        var combos = new List<string> { "" };
        foreach (var part in word.Parts)
        {
            List<string> alts;
            switch (part)
            {
                case WordPart.Literal { Value: var v } when IsPlainNameText(v):
                    alts = [v]; break;
                case WordPart.BracedTuple t when t.Items.All(IsPlainNameText):
                case WordPart.BracedRange:
                    alts = ExpandBrace(part); break;
                default:
                    return null;
            }
            CheckBraceWords((long)combos.Count * alts.Count);
            var next = new List<string>(combos.Count * alts.Count);
            foreach (var c in combos) foreach (var a in alts) next.Add(c + a);
            combos = next;
        }
        return combos;
    }

    /// <summary>Letters, digits, <c>_</c> and <c>-</c>: text that means the same as a literal word anywhere.</summary>
    private static bool IsPlainNameText(string s)
    {
        foreach (char c in s)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) return false;
        return true;
    }

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
            => "@($(if (" + PsBuild.PositionalIsShells + ") { $global:BashPositional } else { $args }))",
        _ => "@(" + EmitBracedVar((WordPart.BracedVarSub)part) + ")",
    };

    /// <summary>
    /// The one quoted all-elements expansion (<c>"${a[@]}"</c>, <c>"$@"</c>) of a word that glues text to it: <c>-"${a[@]}"</c>,
    /// <c>"p${a[@]}q"</c>, <c>k"$@"</c>. <paramref name="outer"/> is its double-quoted part, <paramref name="inner"/> its place
    /// inside. Every other part is literal or quoted text (an unquoted expansion or a glob keeps the old form).
    /// </summary>
    private static bool TryQuotedArrayAffix(CompoundWord word, out int outer, out int inner)
    {
        outer = inner = -1;
        if (word.Parts.IsDefaultOrEmpty || IsQuotedArrayAllWord(word)) return false;
        for (int i = 0; i < word.Parts.Length; i++)
        {
            switch (word.Parts[i])
            {
                case WordPart.Literal or WordPart.EscapedLiteral or WordPart.SingleQuoted:
                    break;
                case WordPart.DoubleQuoted dq:
                    for (int j = 0; j < dq.Parts.Length; j++)
                    {
                        if (!IsQuotedArrayAllWord(new CompoundWord([new WordPart.DoubleQuoted([dq.Parts[j]])]))) continue;
                        if (outer >= 0) return false;
                        outer = i;
                        inner = j;
                    }
                    break;
                default:
                    return false;
            }
        }
        return outer >= 0 && !IsGlobWord(word);
    }

    private static bool IsQuotedArrayAffixWord(CompoundWord word) => TryQuotedArrayAffix(word, out _, out _);

    /// <summary>The fields of a <see cref="TryQuotedArrayAffix"/> word: each element whole, the text glued to the first and last.</summary>
    private static string EmitQuotedArrayAffixArray(CompoundWord word, int outer, int inner)
    {
        var dq = (WordPart.DoubleQuoted)word.Parts[outer];
        var pre = new List<WordPart>();
        for (int i = 0; i < outer; i++) pre.Add(word.Parts[i]);
        if (inner > 0) pre.Add(new WordPart.DoubleQuoted(ImmutableArray.Create(dq.Parts, 0, inner)));
        var suf = new List<WordPart>();
        if (inner < dq.Parts.Length - 1) suf.Add(new WordPart.DoubleQuoted(ImmutableArray.Create(dq.Parts, inner + 1, dq.Parts.Length - inner - 1)));
        for (int i = outer + 1; i < word.Parts.Length; i++) suf.Add(word.Parts[i]);
        return PsBuild.QuotedAffixArray(
            pre.Count == 0 ? "''" : AffixPrefixExpr([.. pre]),
            suf.Count == 0 ? "''" : FlattenPartsToDoubleQuotedString([.. suf]),
            EmitQuotedArrayElements(dq.Parts[inner]));
    }

    /// <summary>
    /// <c>ab+($ABC)</c> / <c>+($v)</c>: an extglob whose one expansion is a bare unquoted variable arm is split like
    /// <c>ab$ABC</c>, because bash expands and IFS-splits the word before it is a pattern (IFS=c ABC='c?d' gives the
    /// fields <c>ab+(</c> and <c>?d)</c>, each then globbed). <paramref name="flat"/> is the word with the extglob's
    /// operators and static arms as literal text around the variable. False for any other shape (quotes, escapes,
    /// a second expansion), which keeps the run-time pattern form.
    /// </summary>
    private static bool TryFlattenSplitExtglob(CompoundWord word, out CompoundWord flat)
    {
        flat = word;
        var parts = ImmutableArray.CreateBuilder<WordPart>();
        var text = new StringBuilder();
        bool found = false, sawExtglob = false;
        foreach (var p in word.Parts)
        {
            switch (p)
            {
                case WordPart.Literal l:
                    text.Append(l.Value);
                    break;
                case WordPart.GlobPart g when !IsDynamicExtglob(g.Pattern):
                    if (g.Pattern.AsSpan().IndexOfAny('\'', '"', '\\') >= 0) return false;
                    text.Append(g.Pattern);
                    break;
                case WordPart.GlobPart g:
                    if (sawExtglob) return false;
                    sawExtglob = true;
                    foreach (var (t, isOperator) in SplitExtglob(g.Pattern))
                    {
                        if (isOperator) { text.Append(t); continue; }
                        var arm = BashParser.DecomposeWord(t);
                        bool bareVar = arm is [WordPart.SimpleVarSub { Name: var n }] && IsOrdinaryVarName(n)
                            || arm is [WordPart.BracedVarSub { Suffix: null, Name: var bn }] && IsOrdinaryVarName(bn);
                        if (bareVar && !found)
                        {
                            found = true;
                            if (text.Length > 0) parts.Add(new WordPart.Literal(text.ToString()));
                            text.Clear();
                            parts.Add(arm[0]);
                        }
                        else if (t.AsSpan().IndexOfAny("$`'\"\\") >= 0) return false;
                        else text.Append(t);
                    }
                    break;
                default:
                    return false;
            }
        }
        if (!found) return false;
        if (text.Length > 0) parts.Add(new WordPart.Literal(text.ToString()));
        flat = new CompoundWord(parts.ToImmutable());
        return true;
    }

    private static string EmitUnquotedSplitWordArray(CompoundWord word)
        => TryFlattenSplitExtglob(word, out var flatExtglob)
            ? EmitAffixWordArray(flatExtglob, UnquotedAffixExpansionIndex(flatExtglob, ordinaryVar: true))
            : IsUnquotedQuotedAltWord(word)
            ? EmitQuotedAltWordArray(word)
            : TryQuotedArrayAffix(word, out var qaOuter, out var qaInner)
            ? EmitQuotedArrayAffixArray(word, qaOuter, qaInner)
            : IsUnquotedPositionalAffixWord(word) || IsUnquotedVarAffixWord(word)
            ? EmitAffixWordArray(word, UnquotedAffixExpansionIndex(word, ordinaryVar: true))
            : IsUnquotedPositionalOpWord(word)
            ? "@(" + EmitBracedVar((WordPart.BracedVarSub)word.Parts[0]) + " | ConvertTo-BashWords)"
            : IsQuotedArrayAllWord(word)
            ? EmitQuotedArrayElements(((WordPart.DoubleQuoted)word.Parts[0]).Parts[0])
            : IsPlainBraceWord(word)
            ? "@(" + EmitBraceExpandedWord(word.Parts) + ")"
            : IsUnquotedArrayAllWord(word)
            // ConvertTo-BashWords takes ONE string: the elements joined by a space split back apart on IFS.
            ? PsBuild.WordSplitArray("(@(" + EmitBracedVar((WordPart.BracedVarSub)word.Parts[0]) + ") -join ' ')")
            : IsUnquotedPositionalSliceWord(word)
            ? "@(ConvertTo-BashWords -Prefix '' -Suffix '' -Fields @(" + EmitBracedVar((WordPart.BracedVarSub)word.Parts[0]) + "))"
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

    /// <summary>Backslash-escapes <c>* ? [ ] \</c> so quoted / escaped text matches itself in a glob pattern. A quoted
    /// <c>]</c> cannot close a bracket: <c>[hello"]"</c> is the literal word <c>[hello]</c> in bash, not the class [hello].</summary>
    /// <summary>
    /// The pattern text of one static extglob arm: quoted and escaped text is literal (backslash-escaped, with the
    /// extglob operators <c>| ( )</c> too), unquoted text and glob parts stay pattern syntax. An arm holding
    /// anything else keeps its source spelling.
    /// </summary>
    private static string StaticExtglobArm(string arm)
    {
        var sb = new StringBuilder(arm.Length + 4);
        void Quoted(string text)
        {
            foreach (char c in text)
            {
                if (c is '*' or '?' or '[' or ']' or '\\' or '|' or '(' or ')') sb.Append('\\');
                sb.Append(c);
            }
        }
        foreach (var part in BashParser.DecomposeWord(arm))
        {
            switch (part)
            {
                case WordPart.Literal l: sb.Append(l.Value); break;
                case WordPart.GlobPart g: sb.Append(g.Pattern); break;
                case WordPart.EscapedLiteral e: Quoted(e.Value); break;
                case WordPart.SingleQuoted q: Quoted(q.Value); break;
                case WordPart.AnsiCQuoted a: Quoted(ExpandAnsiCEscapes(a.Value)); break;
                case WordPart.DoubleQuoted dq when TryGetStaticArgValue(new CompoundWord(ImmutableArray.Create<WordPart>(dq))) is { } t:
                    Quoted(t);
                    break;
                default: return arm;
            }
        }
        return sb.ToString();
    }

    private static string EscapeGlobLiteral(string text)
    {
        if (text.AsSpan().IndexOfAny("*?[]\\") < 0) return text;
        var sb = new StringBuilder(text.Length + 4);
        foreach (char c in text)
        {
            if (c is '*' or '?' or '[' or ']' or '\\') sb.Append('\\');
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
                case WordPart.BracedTuple bt:
                    brace = true;
                    // `{**/*.*,}`: a glob inside an item is pathname-expanded once the braces are gone.
                    foreach (var item in bt.Items)
                        if (item.AsSpan().IndexOfAny('*', '?', '[') >= 0 && item.AsSpan().IndexOfAny("'\"\\$`") < 0) glob = true;
                    break;
                case WordPart.BracedRange:
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
                        Dynamic(PsBuild.RuntimeToShellPath(PsBuild.TempDirExpr));
                        text = text[4..];
                    }
                    else if (mapPaths && i == 0 && TryTranslateMsysDrivePath(text, out var drive))
                        text = BashPath.ToShell(drive, windows: true);
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
                case WordPart.GlobPart gp when IsDynamicExtglob(gp.Pattern):
                    // `@($a|$b)`, `!($str)`: the extglob operators stay pattern syntax, each arm is a word of
                    // its own (expansions spliced, quoted text escaped). Its `$a` matched the text "$a".
                    foreach (var (text, isOperator) in SplitExtglob(gp.Pattern))
                    {
                        if (isOperator) lit.Append(text);
                        else Dynamic(EmitGlobPatternExpr(new CompoundWord(BashParser.DecomposeWord(text)), mapPaths: false));
                    }
                    break;
                case WordPart.GlobPart gp when gp.Pattern.Length > 1 && gp.Pattern[1] == '(' && gp.Pattern.AsSpan().IndexOfAny('\'', '"') >= 0:
                    // `@(x|'*'|'|')`: a quoted arm is literal text with its quotes removed, so an unmatched word
                    // prints `@(x|*||)` (it kept the quote characters).
                    foreach (var (text, isOperator) in SplitExtglob(gp.Pattern))
                        lit.Append(isOperator ? text : StaticExtglobArm(text));
                    break;
                case WordPart.GlobPart gp:
                    // A class's quoted members are literal and its quotes drop: [a"]"b] is {a, ], b}.
                    lit.Append(gp.Pattern.StartsWith('[') ? BashParser.UnquoteClass(gp.Pattern) : gp.Pattern);
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
                    Dynamic(Escape + PsBuild.RuntimeToShellPath(EmitWordPart(ts)) + ")");
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
        // The low 8 bits, as bash keeps them: a literal is folded here (`exit 257` is 1, `exit -2` is 254).
        if (GetLiteralValue(word) is { } exitLiteral
            && long.TryParse(exitLiteral, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var exitNumber))
            return (((exitNumber % 256) + 256) % 256).ToString(System.Globalization.CultureInfo.InvariantCulture);
        // The operand as a value: EmitWord is argument text, so `( exit q )` ran a command `q`. REGRESSION.
        if (!IsPureUnquotedVarWord(word))
            return PsBuild.StatusByte(EmitAssignmentValue(word));

        return PsBuild.StatusByte("$(& { $__bashexit = " + EmitUnquotedVarSplitArray(word) +
               "; if ($__bashexit.Count) { $__bashexit[0] } " +
               "else { $global:LASTEXITCODE } })");
    }

    private static string EmitExitWithUnquotedVar(CompoundWord word) => ShellExit(EmitExitCodeValue(word));

    /// <summary>A literal status operand that is not a number (<c>exit q</c>, <c>return x</c>): bash's "numeric argument required".</summary>
    private static bool IsNonNumericLiteral(CompoundWord word) =>
        GetLiteralValue(word) is { } text && !System.Text.RegularExpressions.Regex.IsMatch(text, @"^\s*[+-]?[0-9]+\s*$");

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
        string trailingTokens = "",
        string headPrelude = "")
    {
        var prelude = new StringBuilder(headPrelude);
        var call = new StringBuilder();
        // The block below is a script block: the stage's pipeline input reaches its `$input`, not the
        // command inside. Forward it (see _pipeStageFeedsInput). Read before the args are emitted, which
        // may recurse (a nested substitution clears the flag for its own body).
        if (_pipeStageFeedsInput) call.Append(PsBuild.ForwardedStageInput);
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
                else if (emitted.Contains("Get-BashIndirectValue") || emitted.Contains(PsBuild.ExpansionErrorFunction)) prelude.Append(PsBuild.AbandonIfExpansionFailed);
                call.Append('@').Append(tempVar.AsSpan(1));
            }
            else
            {
                string plain = emitPlainArg(i);
                // A quoted word with a failing indirect reference (`"${!a[@]-d}"`, a=(x y)) abandons the command as
                // well: evaluate it first, so the command never runs (it ran with the word empty).
                if (plain.Contains("Get-BashIndirectValue", StringComparison.Ordinal) || plain.Contains(PsBuild.ExpansionErrorFunction, StringComparison.Ordinal))
                {
                    string v = $"$__bashsplat{splatCounter++}";
                    prelude.Append(v).Append(" = ").Append(PsBuild.ArgValue(plain)).Append("; ").Append(PsBuild.AbandonIfExpansionFailed);
                    call.Append(v);
                }
                else call.Append(plain);
            }
        }
        call.Append(trailingTokens);

        return $"& {{ {prelude}{call} }}";
    }

    /// <summary>True when the word holds an indirect expansion (`${!ref}`, `"${!a[@]-d}"`): one that fails abandons its
    /// command, so the command goes through <see cref="EmitCommandWithSplatArgs"/>, which evaluates it first.</summary>
    private static bool HasIndirectExpansion(CompoundWord word) => HasIndirectPart(word.Parts);

    private static bool HasIndirectPart(ImmutableArray<WordPart> parts)
    {
        foreach (var part in parts)
        {
            if (part is WordPart.BracedVarSub { Suffix: { } s } && (s.StartsWith("!indirect", StringComparison.Ordinal) || s.StartsWith("#BAD:", StringComparison.Ordinal))) return true;
            if (part is WordPart.DoubleQuoted dq && HasIndirectPart(dq.Parts)) return true;
        }
        return false;
    }

    /// <summary>True when expanding the word assigns a shell variable: `$((n+=2))`, `$((i++))`, `${w=q}`, `${w:=q}`.</summary>
    private static bool MutatesShellState(CompoundWord word) => PartsMutateShellState(word.Parts);

    // A double-quoted "${w=q}" / "$((i++))" assigns as well.
    private static bool PartsMutateShellState(ImmutableArray<WordPart> parts)
    {
        foreach (var part in parts)
        {
            if (part is WordPart.BracedVarSub { Suffix: { } s } && (s.StartsWith("=") || s.StartsWith(":=")))
                return true;
            if (part is WordPart.ArithSub a && ArithAssignRx.IsMatch(a.Expr.Source))
                return true;
            if (part is WordPart.DoubleQuoted dq && PartsMutateShellState(dq.Parts))
                return true;
        }
        return false;
    }

    /// <summary>Arithmetic text that assigns: `=` (not `==` `!=` `&lt;=` `>=`), an op-assign, `++` or `--`.</summary>
    private static readonly System.Text.RegularExpressions.Regex ArithAssignRx =
        new(@"\+\+|--|(?<![=!<>])=(?!=)|[-+*/%&|^]=|<<=|>>=", System.Text.RegularExpressions.RegexOptions.Compiled);


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
        if (!m.Success) return EmitSimpleVar(name, strict: strict);
        string elem = EmitBracedVar(new WordPart.BracedVarSub(m.Groups[1].Value, "[" + m.Groups[2].Value + "]"));
        // `${!a[@]-d}`: the name is the elements joined (`*` by IFS); no elements (an unset array reads as one $null) is an unset name, not ''.
        if (m.Groups[2].Value is "@" or "*")
            return "$(& { $__iel = @(@(" + elem + ") -ne $null); if ($__iel.Count) { $__iel -join "
                + (m.Groups[2].Value == "*" ? PsBuild.IfsJoinSeparator : "' '") + " } })";
        return elem;
    }

    /// <summary>The -RefName Get-BashIndirectValue reports (and looks up when plain): `ref` or `a[1]`; '' otherwise.</summary>
    private static string IndirectRefName(string name) =>
        PsBuild.SingleQuote(System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z_]\w*(\[[^\]']+\])?$") ? name : "");
    private static string EmitBracedVar(WordPart.BracedVarSub bvs, bool inDoubleQuote = false)
    {
        NestingGuard.EnsureStack(); // ${x:-${x:-…}} recurses here without passing Emit(Command)
        // `${!ref}` of a nameref is the NAME it refers to (bash inverts the usual meaning).
        if (bvs.Suffix == "!indirect" && _namerefs is { Count: > 0 } && _namerefs.TryGetValue(bvs.Name, out var namerefTargetName))
            return inDoubleQuote ? "$(" + PsBuild.SingleQuote(namerefTargetName) + ")" : PsBuild.SingleQuote(namerefTargetName);
        if (_namerefs is { Count: > 0 } && _namerefs.ContainsKey(bvs.Name))
        {
            bvs = bvs with { Name = ResolveNameref(bvs.Name) };
            // `typeset -n r='a[@]'; ${r[@]}` / `${r[0]}`: a subscript on a nameref to an ELEMENT reads nothing (bash). It
            // emitted `$a[@]`, which is no PowerShell.
            if (bvs.Name.Contains('[') && bvs.Suffix is ['[', ..])
                return inDoubleQuote ? "$('')" : "''";
            if (bvs.Name.Contains('[') && bvs.Suffix is ['#', 'L', '[', ..]) // ${#r[@]}: no elements
                return inDoubleQuote ? "$('0')" : "'0'";
        }
        // A nameref whose target is known only at run time: `${!r}` is the name it stands for. Every other form reads it
        // through the run time, so no form needs a case of its own here: the scalar forms apply their operator to
        // EmitSimpleVar's PsBuild.ReadThroughNameref (AssignDefault and ParamErrorThrow recognize it), the array forms read
        // `$r`, which follows the reference (BashVariableLookup.ViewNameref), and `${r@a}` names the variable it stands for.
        bool runtimeNameref = IsRuntimeNameref(bvs.Name);
        if (runtimeNameref && bvs.Suffix == "!indirect")
            return PsBuild.IndirectThroughNameref(bvs.Name);
        if (bvs.Suffix is null)
            return EmitSimpleVar(bvs.Name, strict: true);

        // A bad substitution fails the expansion: the command is abandoned with status 1 and a script goes on
        // (`x=${ echo hi;}` is bash 5.3 syntax); under `bash -c` the rest of the command string is discarded.
        if (bvs.Suffix.StartsWith("#BAD:", StringComparison.Ordinal))
            return "$(" + PsBuild.ExpansionError(PsBuild.SingleQuote(bvs.Suffix[5..] + ": "), "'bad substitution'") + ")";

        // Positional-parameter SLICE: ${@:offset[:length]} / ${*:…}. Unhandled, this
        // fell through to the ordinary-variable path and emitted the bare `$env:`
        // (empty name) — "':' was not followed by a valid variable name character",
        // which broke the whole file. `${@: -1}` (the last argument) is the common
        // real-world spelling; it is how zoxide's shell hook reads its target dir.
        if (bvs.Name is "@" or "*" && bvs.Suffix.StartsWith(':')
            && EmitPositionalSlice(bvs.Suffix, inDoubleQuote, bvs.Name) is { } posSlice)
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
            string attrName = runtimeNameref ? PsBuild.NamerefTargetName(bvs.Name) : PsBuild.SingleQuote(bvs.Name);
            string attrs = $"[PsBash.Cmdlets.BashQuoteText]::Attributes({attrName}, (Get-Variable -Name {PsBuild.SingleQuote(bvs.Name)} -ValueOnly -ErrorAction Ignore))";
            // `set -u; ${undef@a}`: an unset name is unbound here too (bash ends the shell or subshell, status 1).
            if (_nounsetMode && bvs.Suffix == "@a")
                return $"$(if ($global:__BashNounset -and $null -eq {EmitSimpleVar(bvs.Name)}) {{ {PsBuild.ParamErrorThrowStatements(PsBuild.SingleQuote(bvs.Name + ": "), "'unbound variable'")} }} else {{ {attrs} }})";
            return "$(" + attrs + ")";
        }
        // Array subscript access: ${arr[0]}, ${arr[@]}, ${arr[*]}, ${arr[key]}, plus
        // the subscript-PLUS-operator forms ${arr[@]:1:2}, ${arr[0]##*/}, ${arr[i]:-x}.
        if (bvs.Suffix.StartsWith('['))
        {
            int close = BashParser.FindSubscriptClose(bvs.Suffix, 0);
            if (close > 0)
            {
                string subscript = bvs.Suffix[1..close];
                string subOp = bvs.Suffix[(close + 1)..]; // trailing operator, "" if none
                // Special bash array vars use their mapped name, not $Name
                string arrayVar = PsBuild.ShellArrayExpr(bvs.Name) ?? $"${bvs.Name}";

                if (subOp.Length == 0)
                {
                    // ${arr[@]} -> $arr (whole array); an associative array (hashtable) yields its VALUES.
                    // "${a[*]}" joins with the first character of IFS ("${a[@]}" with a space).
                    // A SCALAR (`s=abc`) lives in the environment, not in a PowerShell variable: `${s[@]}` / `${s[0]}` are
                    // the scalar itself (bash treats it as a one-element array).
                    string whole = bvs.Name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && !char.IsAsciiDigit(bvs.Name[0])
                        && PsBuild.ShellArrayExpr(bvs.Name) is null
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
                        return PsBuild.NullSafeElement(arrayVar, EmitVarSubscript(arrayVar, EmitSimpleVar(sv.Groups[1].Value)));
                    }
                    // An element of an unset array is empty (indexing $null threw "Cannot index into a null array").
                    if (IsPlainIndex(subscript, out _))
                    {
                        // $Matches (BASH_REMATCH) is keyed by int; every other array reads by the string key.
                        string key = bvs.Name == "BASH_REMATCH" ? subscript : $"'{subscript}'";
                        return PsBuild.NullSafeElement(arrayVar, key);
                    }
                    return PsBuild.NullSafeElement(arrayVar, EmitWordSubscript(arrayVar, subscript));
                }

                // Whole-array operator: only slicing (${arr[@]:off:len}) maps cleanly to
                // a PowerShell range index; other ops degrade to the whole array.
                if (subscript is "@" or "*")
                {
                    if (EmitArraySlice(arrayVar, subOp, inDoubleQuote, label: bvs.Name + "[" + subscript + "]") is { } slice) return slice;
                    // `${a[@]@Q}` / `${a[@]@a}` / `${A[@]@P}` / `${a[@]@E}`: the transform applies to EACH element (the values
                    // of an associative array).
                    if (subOp is "@Q" or "@a" or "@P" or "@E")
                    {
                        string srcv = $"$(if ({arrayVar} -is [System.Collections.IDictionary]) {{ {arrayVar}.Values }} else {{ {arrayVar} }})";
                        string each = subOp switch
                        {
                            "@Q" => "[PsBash.Cmdlets.BashQuoteText]::ForReuse([string]$_)",
                            "@P" => PsBuild.PromptExpand("[string]$_"),
                            "@E" => "[PsBash.Cmdlets.BashPromptText]::Unescape([string]$_)",
                            _ => $"$(if ({arrayVar} -is [System.Collections.IDictionary]) {{ 'A' }} else {{ 'a' }})",
                        };
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
                string elemRef = IsPlainIndex(subscript, out _)
                    ? $"{arrayVar}[{subscript}]"
                    // ${m[$k]:-0}: a variable subscript is the variable's value, as in the operator-less form above.
                    : System.Text.RegularExpressions.Regex.Match(subscript, @"^\$\{?(\w+)\}?$") is { Success: true } subVar
                        ? $"{arrayVar}[{EmitVarSubscript(arrayVar, EmitSimpleVar(subVar.Groups[1].Value))}]"
                        : $"{arrayVar}[{EmitWordSubscript(arrayVar, subscript)}]";
                // A SCALAR (`s=pq`) has no PowerShell variable, and indexing $null throws ("Cannot index into a null
                // array"). bash treats it as a one-element array: ${s[0]-w} is pq, ${s[1]-w} is w. The assigning forms
                // keep the plain element reference AssignDefault parses.
                if (arrayVar == "$" + bvs.Name && subOp.Length > 0 && subOp[0] != '=' && !subOp.StartsWith(":=", StringComparison.Ordinal))
                    elemRef = IsPlainIndex(subscript, out int scalarIdx)
                        ? $"$(if ($null -eq {arrayVar}) {{ {(scalarIdx == 0 ? EmitSimpleVar(bvs.Name) : "$null")} }} else {{ {elemRef} }})"
                        // `${u[i]:-d}` / `${u[$k]-d}` of an unset array: unset, not an indexing error.
                        : $"$(if ($null -eq {arrayVar}) {{ $null }} else {{ {elemRef} }})";
                return EmitScalarSuffix(elemRef, subOp, inDoubleQuote, bvs.ArgWord, bvs.Name + "[" + subscript + "]")
                    ?? (inDoubleQuote ? $"$({elemRef})" : elemRef);
            }
        }

        // Array length: ${#arr[@]} or ${#arr[*]} -> $arr.Count
        if (bvs.Suffix.StartsWith("#L[", StringComparison.Ordinal) && bvs.Suffix.EndsWith(']'))
        {
            string subscript = bvs.Suffix[3..^1]; // strip #L[ and ]
            if (subscript is "@" or "*")
            {
                string lenArr = PsBuild.ShellArrayExpr(bvs.Name) ?? $"${bvs.Name}";
                // Always a subexpression: bare `$a.Count` glued to a literal (`len=${#a[@]}`) expands only `$a`.
                // A scalar (`s=abc`, an env var) is a one-element array: `${#s[@]}` is 1.
                if (lenArr.AsSpan(1).IndexOfAnyExcept(VarNameChars) < 0 && bvs.Name.Length > 0)
                    return $"$(if ({lenArr} -is [System.Collections.IDictionary]) {{ {lenArr}.Count }} elseif ($null -ne {lenArr}) {{ @({lenArr}).Count }} elseif ($null -ne {EmitSimpleVar(bvs.Name)}) {{ 1 }} else {{ 0 }})";
                return $"$({lenArr}.Count)";
            }
            // ${#a[N]}: the length of ONE element (a string), not of the array.
            // (`${s#[0-9]}` is a strip pattern that merely starts with `[`: the subscript must be arithmetic text.)
            if (System.Text.RegularExpressions.Regex.IsMatch(subscript, @"^[\w+\-*/% ]+$") || BashParser.FindSubscriptClose(bvs.Suffix, 2) == bvs.Suffix.Length - 1)
            {
                string elemLen = EmitBracedVar(bvs with { Suffix = "[" + subscript + "]" });
                return $"$([PsBash.Cmdlets.BashLocale]::Length([string]({elemLen})))";
            }
        }

        // `${@@Q}` / `${*@Q}`: quote-for-reuse applies to EACH positional parameter.
        // `${@@a}` / `${*@a}`: a positional parameter has no attributes, so each one becomes the empty string.
        if (bvs.Name is "@" or "*" && bvs.Suffix is "@Q" or "@a")
        {
            string posQ = $"@({PsBuild.BuildPositionalExpansion("@")}) | ForEach-Object {{ "
                + (bvs.Suffix == "@a" ? "''" : "[PsBash.Cmdlets.BashQuoteText]::ForReuse([string]$_)") + " }";
            return bvs.Name == "*" && inDoubleQuote
                ? $"$(@({posQ}) -join {PsBuild.IfsJoinSeparator})"
                : $"$({posQ})";
        }
        string varRef = EmitSimpleVar(bvs.Name, strict: bvs.Suffix.StartsWith("/", StringComparison.Ordinal) || bvs.Suffix == "#L" || IsSliceSuffix(bvs.Suffix)
            || bvs.Suffix is ['@', _]); // `${undef@Q}` / `${undef@P}` under set -u are unbound
        // `$?` `$#` `$$` `$!` and the computed scalars ($SECONDS, $RANDOM, ...) are numbers in PowerShell, and 0 is falsy
        // there: `${?:-q}` after a success gave q (bash: 0), `${SECONDS:+y}` in the first second gave "". A colon operator
        // tests the TEXT, as bash does ("0" is non-empty); the plain `-` `+` `=` `?` test set-ness, which the cast would
        // lose (an unset `$!` is null, `[string]` makes it "": `${!-dash}` printed nothing).
        if (IsNumericShellValue(bvs.Name) && bvs.Suffix is { } colonSfx && colonSfx.StartsWith(":", StringComparison.Ordinal))
            varRef = $"([string]{varRef})";

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
                "Get-BashIndirectValue -Ref (" + IndirectRef(bvs.Name, false) + ") -RefName " + IndirectRefName(bvs.Name)
                + " -Pos @(" + PsBuild.BuildPositionalExpansion("@") + ")" + IndirectLineArg() + (nameOnly ? " -NameOnly" : "");
            // `${!ref@a}`: the attribute letters of the variable NAMED by $ref.
            if (op == "@a")
                return "$(& { $__in = [string]$(" + IndLookup(true) + "); if ($__in) { $__iv = Get-Variable -Name $__in -ValueOnly -ErrorAction Ignore; "
                    + "if ($null -eq $__iv) { $__iv = [PsBash.Cmdlets.BashVariableStore]::Get($__in) }; "
                    + "[PsBash.Cmdlets.BashQuoteText]::Attributes($__in, $__iv) } })";
            if (op.StartsWith(":=", StringComparison.Ordinal) || op.StartsWith('='))
            {
                string assigned = EmitScalarSuffix("$__iv", op, false, bvs.ArgWord) ?? "$__iv";
                // The name first: an unset or empty ref reports its error once and assigns nothing.
                return "$(& { $__ivn = [string]$(" + IndLookup(true) + "); if ($__ivn -eq '') { return }; "
                    + "$__iv0 = $(" + IndLookup(false) + "); $__iv = $__iv0; $__ir = " + assigned + "; "
                    + "if ($__iv -cne $__iv0) { [PsBash.Cmdlets.BashVariableStore]::Set($__ivn, [string]$__iv) }; $__ir })";
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
                + "else { Get-BashIndirectValue -Ref $__indv -RefName " + IndirectRefName(bvs.Name)
                + " -Pos @(" + PsBuild.BuildPositionalExpansion("@") + ")" + IndirectLineArg() + " } }";
            // Under set -u a ref that names an unset variable ends the script like a direct read
            // (`r=nope; set -u; echo ${!r}` is "!r: unbound variable", status 1); a failed lookup already reported.
            if (_nounsetMode && namedRef)
                return "$($__psbash_iv = " + lookup + "; if ($global:__BashNounset -and $null -eq $__psbash_iv -and -not $global:__BashExpansionFailed) { "
                    + PsBuild.ParamErrorThrowStatements(PsBuild.SingleQuote("!" + bvs.Name + ": "), "'unbound variable'") + " } else { $__psbash_iv })";
            return $"$({lookup})";
        }

        if (bvs.Suffix == "!names")
        {
            // Listed and matched under the BASH name: a diverted `path` (stored __psbash_cv_0_PATH, BashNameCase) matches
            // ${!pa*} and prints as path.
            string prefix = BashNameCase.ToBash(bvs.Name);
            string expr = $"@(@(Get-ChildItem env: | ForEach-Object {{ [PsBash.Core.BashNameCase]::ToListed($_.Name) }} | Where-Object {{ $_ -clike '{prefix}*' }}) + @(Get-Variable -Name '{prefix}*', '{BashNameCase.Prefix}*' -ErrorAction SilentlyContinue | Where-Object {{ $_.Options -eq 'None' -and ($_.Value -is [array] -or $_.Value -is [System.Collections.IDictionary]) -and $_.Name -cnotin 'args','input','Error','Matches','PSBoundParameters' }} | ForEach-Object {{ [PsBash.Core.BashNameCase]::ToBash($_.Name) }} | Where-Object {{ $_ -clike '{prefix}*' }}) | Sort-Object -Unique -CaseSensitive)";
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
        return EmitScalarSuffix(varRef, bvs.Suffix, inDoubleQuote, bvs.ArgWord, bvs.Name) ?? varRef;
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
        if (PsBuild.ReadThroughNamerefName(varRef) is { } nameref)
            return PsBuild.AssignDefaultThroughNameref(nameref, value);
        var m = System.Text.RegularExpressions.Regex.Match(varRef, @"^\$([A-Za-z_]\w*)\[(.+)\]$");
        if (!m.Success) return $"({varRef} = {value})";
        string arr = "$" + m.Groups[1].Value;
        return $"$($__psbV = {value}; {arr} = Set-BashElement {arr} {m.Groups[2].Value} $__psbV; $__psbV)";
    }

    private static string ParamErrorThrow(string varRef, string argVal)
    {
        var name = varRef.StartsWith("$env:", StringComparison.Ordinal) ? varRef[5..].Trim('{', '}', '(', ')')
            : PsBuild.ReadThroughNamerefName(varRef) ?? "";
        var prefix = name.Length > 0 ? PsBuild.SingleQuote(name + ": ") : "''";
        return "$(" + PsBuild.ParamErrorThrowStatements(prefix, argVal) + ")";
    }

    private static string? EmitScalarSuffix(string varRef, string suffix, bool inDoubleQuote,
        ImmutableArray<WordPart>? argWord = null, string? label = null)
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
        static string ParenSafeLiteral(string expr) => PsBuild.ParenSafeLiteral(expr);

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
            // Characters, or bytes in the C locale (BashLocale); a subexpression in both contexts.
            return $"$([PsBash.Cmdlets.BashLocale]::Length({varRef}))";
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
        // With no word bash says "parameter not set" (the colon form's default is "parameter null or not set").
        if (suffix.StartsWith("?"))
            return $"{open}{varRef} ?? {ParamErrorThrow(varRef, suffix.Length == 1 ? "'parameter not set'" : ArgVal(suffix[1..]))})";

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
            // BashLocale.Replace: the match is on characters, or on bytes in the C locale (`?` is one byte).
            string tail = first ? ", 1" : ", -1";
            // A quoted glob (`[^'><+-.,[]']`): quotes come off, the quoted characters stay literal.
            if (UnquoteGlobPattern(find) is { } unquotedFind) find = unquotedFind;
            string? findExpr = TryEmitSubstitutionOperand(find, backslashEscapes: true);
            string? repExpr = TryEmitSubstitutionOperand(replace, backslashEscapes: true);
            if (findExpr is null && repExpr is null)
                return $"{open}[PsBash.Cmdlets.BashLocale]::Replace({ReplaceFindRegex(find)}, {varRef}, '{SqEsc(replace)}'{tail}))";
            // An unquoted `$g` is a PATTERN (`g='*'; ${v//$g/-}` replaces the whole value); "$g" is the literal text.
            string regex = findExpr is null ? ReplaceFindRegex(find)
                : System.Text.RegularExpressions.Regex.IsMatch(find, @"^\$(\w+|\{\w+\})$")
                    ? $"$(if ({findExpr}) {{ '(?:(?=[\\s\\S])|\\A)(?:' + [PsBash.Core.Parser.BashPattern]::ToRegex([string]({findExpr}), $false, $false) + ')' }} else {{ '(?!)' }})"
                    : $"$(if ({findExpr}) {{ [regex]::Escape({findExpr}) }} else {{ '(?!)' }})";
            string rep = repExpr is null ? $"'{SqEsc(replace)}'" : $"({repExpr}).Replace('$', '$$')";
            return $"{open}[PsBash.Cmdlets.BashLocale]::Replace({regex}, {varRef}, {rep}{tail}))";
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
            var sliceParts = SplitSlice(sliceBody);
            // Plain decimal bounds (`${s:2}`, `${s: -3:2}`) are a direct, clamped Substring. Any other bound -- `010` (octal
            // 8), `08` (an error), `i+1`, `$i` -- is an arithmetic expression for the one evaluator, below.
            int fastLength = -1;
            if (IsPlainIndex(sliceParts[0].Trim(), out int offset)
                && (sliceParts.Length == 1 || (IsPlainIndex(sliceParts[1].Trim(), out fastLength) && fastLength >= 0)))
            {
                string sv = "([string]" + varRef + ")";   // an unset variable slices as the empty string
                string len = $"{sv}.Length";
                string start = offset >= 0
                    ? $"[Math]::Min({offset}, {len})"
                    : $"[Math]::Max(0, {len} - {-offset})";
                string expr = sliceParts.Length == 1
                    ? $"{sv}.Substring({start})"
                    : $"{sv}.Substring({start}, [Math]::Min({fastLength}, {len} - ({start})))";
                return inDoubleQuote ? $"$({expr})" : expr;
            }

            // Non-literal offset/length (e.g. ${x:$i}, ${x:$i:$n}, ${x:i+1}). Bash
            // treats both as ARITHMETIC expressions, so route each through
            // Invoke-BashArith (which resolves bare and $-prefixed variables) in a
            // scriptblock that evaluates each component ONCE and normalizes/clamps at
            // runtime. Without this, ${x:$i} failed the digit test above and silently
            // fell through to the bare variable — dropping the slice entirely.
            {
                var sb = new StringBuilder("& { $__psbS = [string](");
                sb.Append(varRef);
                sb.Append("); $__psbO = ").Append(EmitArithOperandValue(sliceParts[0], label)).Append("; ");
                sb.Append("if ($__psbO -lt 0) { $__psbO = [Math]::Max(0, $__psbS.Length + $__psbO) } ");
                sb.Append("else { $__psbO = [Math]::Min($__psbO, $__psbS.Length) }; ");
                if (sliceParts.Length == 2)
                {
                    // An empty length is an empty expression, 0 (`${s:1:}` is empty).
                    sb.Append("$__psbL = ").Append(EmitArithOperandValue(sliceParts[1], label)).Append("; ");
                    // bash: negative length ends |length| chars from the string end. A huge one is clamped first.
                    sb.Append("$__psbE = if ($__psbL -ge 0) { $__psbO + [Math]::Min($__psbL, $__psbS.Length) } else { $__psbS.Length + $__psbL }; ");
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
                    return $"{open}{PsBuild.PromptExpand(varRef)})";
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
    private static string? EmitPositionalSlice(string suffix, bool inDoubleQuote, string name)
        => EmitArraySlice(PsBuild.BuildPositionalExpansion("@"), suffix, inDoubleQuote, positional: true, label: name);

    // `${a[$i]}`: an associative array uses the value as the key; an indexed array evaluates it as an arithmetic
    // expression (`code='1+2'; ${a[$code]}` is a[3], k=010 is a[8], i=08 an error). Only a plain decimal skips the
    // evaluator (BashArithmeticParser.PlainDecimal; `-match '^-?[0-9]+$'` read 010 as 10). The evaluator is written as a
    // WORD (`$(Invoke-BashArith`): a subscript error discards the rest of the line in bash, as `$((1/0))` does
    // (InvokeBashArithmeticCommand.IsWordExpansion, PsBuild.CanDiscardLine).
    private static string EmitVarSubscript(string arrayVar, string valueRef)
        => $"$(if ({arrayVar} -is [System.Collections.IDictionary]) {{ {valueRef} }} else {{ {PsBuild.ArithPlainDecimal("[string]" + valueRef)} ?? {PsBuild.ArithValue($"Invoke-BashArith ([string]{valueRef}) -Subscript")} }})";

    /// <summary>
    /// A slice bound that bash evaluates as ARITHMETIC, as a PowerShell number: an empty one is 0, a plain decimal its
    /// literal (BashArithmeticParser.TryPlainDecimal, the one fast path), anything else the one evaluator's value
    /// (`010` is 8; `08` is an error that abandons the command; `i+1`; `$j` with j='1+1'). <paramref name="label"/> names
    /// the expansion in an error: `s: 08: value too great for base (error token is "08")`.
    /// </summary>
    private static string EmitArithOperandValue(string text, string? label)
    {
        string expr = text.TrimStart();
        if (expr.Trim().Length == 0) return "0";
        if (BashArithmeticParser.TryPlainDecimal(expr.TrimEnd(), out long n))
            return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return PsBuild.ArithValue(EmitArithmeticInvocation(BashArithmeticParser.ParseOrDefer(expr), label));
    }

    /// <summary>A literal subscript or slice bound that needs no evaluator: BashArithmeticParser.TryPlainDecimal, within int.</summary>
    private static bool IsPlainIndex(string text, out int value)
    {
        bool plain = BashArithmeticParser.TryPlainDecimal(text, out long n) && n is >= int.MinValue and <= int.MaxValue;
        value = plain ? (int)n : 0;
        return plain;
    }

    // `${a[i]}` / `${a[i+1]}`: on an indexed array the subscript is an arithmetic expression (bare names are
    // variables); on an associative array it is the literal key. The array kind is known only at run time.
    private static string EmitWordSubscript(string arrayVar, string subscript)
    {
        // `${m["k"]}` / `${m['k']}`: a quoted literal subscript is its quote-removed text.
        if (subscript.Length >= 2 && subscript[0] is '"' or '\'' && subscript[^1] == subscript[0]
            && subscript.AsSpan(1, subscript.Length - 2).IndexOfAny("$`\\\"'") < 0)
            subscript = subscript[1..^1];
        // `${a[$(cmd) + 1]}` / `${a[$i+1]}` / `${m["$k b"]}` / `${m[${k}x]}`: expansions and quotes in the subscript are
        // expanded (quotes removed) first; an indexed array then evaluates the result as arithmetic.
        if (subscript.IndexOfAny(['$', '`', '"', '\'', '\\']) >= 0)
        {
            string dyn = FlattenPartsToDoubleQuotedString(BashParser.DecomposeWord(subscript));
            return $"$(if ({arrayVar} -is [System.Collections.IDictionary]) {{ {dyn} }} else {{ [long]$(Invoke-BashArith {dyn} -Subscript) }})";
        }
        string key = PsBuild.SingleQuote(subscript);
        // `${a[b=2]}` / `${a[i<2?0:1]}` / `${a[i++]}`: every arithmetic operator (assignments too, whose side effect
        // persists) is evaluated on an indexed array; an associative array still takes the text as its key.
        if (!System.Text.RegularExpressions.Regex.IsMatch(subscript, @"^[\w+\-*/% ()\[\]=<>!&|^~?:,]+$"))
            return key;
        return $"$(if ({arrayVar} -is [System.Collections.IDictionary]) {{ {key} }} else {{ [long]$(Invoke-BashArith {key} -Subscript) }})";
    }

    /// <summary>
    /// The offset and (when present) length of a <c>${v:offset:length}</c> body. The colon that separates them is the
    /// first one outside parentheses that does not close a <c>?</c> of the offset expression, so
    /// <c>${s: 0 &lt; 1 ? 2 : 0 : 1}</c> is offset <c>0 &lt; 1 ? 2 : 0</c>, length <c>1</c> (bash).
    /// </summary>
    private static string[] SplitSlice(string body)
    {
        int depth = 0, pending = 0;
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (c == '(') depth++;
            else if (c == ')') { if (depth > 0) depth--; }
            else if (depth == 0 && c == '?') pending++;
            else if (depth == 0 && c == ':')
            {
                if (pending > 0) { pending--; continue; }
                return new[] { body[..i], body[(i + 1)..] };
            }
        }
        return new[] { body };
    }

    // Emit a bash array slice ${arr[@]:offset[:length]} (and ${@:offset[:length]}) as a run-time clamped scriptblock.
    // Returns null when `op` is not a slice (`${a[@]:-w}` / `:+` / `:=` / `:?` are the caller's). Each bound is an
    // ARITHMETIC expression of the one evaluator (EmitArithOperandValue): `${a[@]:i}`, `${a[@]:1+1:2}`, `${a[@]:$j}` with
    // j='1+1', `${a[@]:010}` (from element 8), `${@:08}` (an error). Only a plain decimal is written as a literal.
    private static string? EmitArraySlice(string arrayVar, string op, bool inDoubleQuote, bool positional = false,
        string? label = null)
    {
        if (!op.StartsWith(':') || (op.Length > 1 && !IsSliceSuffix(op)))
            return null;
        var parts = SplitSlice(op[1..].Trim());
        // An empty bound is an empty arithmetic expression, 0 (`${a[@]: : }` is the empty slice).
        string offset = EmitArithOperandValue(parts[0], label);
        string? length = parts.Length == 2 ? EmitArithOperandValue(parts[1], label) : null;

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

    /// <param name="sb">Receives the emitted text.</param>
    /// <param name="parts">The parts inside the double quotes.</param>
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
        if (IsRuntimeNameref(name)) return RuntimeNamerefRead(name, strict);
        name = ResolveNameref(name);
        if (IsJobCaptured(name) || (_lexicalLocals is not null && _lexicalLocals.Contains(name)))
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
    /// <para>Every element of a pipeline starts from the shell's <c>$_</c> (bash forks them from it), and after
    /// the pipeline <c>$_</c> is unchanged, unless the last element ran in this shell (lastpipe, or a lone
    /// <c>! cmd</c>): then it is that command's last word.</para>
    /// </summary>
    private static ImmutableArray<Command> ResolveLastArgRefs(ImmutableArray<Command> cmds)
    {
        CompoundWord? prevLast = null;
        ImmutableArray<Command>.Builder? rewritten = null;
        // A `shopt -s/-u lastpipe` in this list decides from where it stands (off before it); without one, the
        // whole-script guess stands.
        bool? lastpipe = null;
        foreach (var c in cmds)
            if (c is Command.Simple sc && LastpipeShoptValue(sc) is not null) { lastpipe = false; break; }
        for (int i = 0; i < cmds.Length; i++)
        {
            var cmd = cmds[i];
            if (cmd is Command.Simple y && y.Words.Length > 0)
            {
                var resolved = SubstituteLastArg(y, prevLast);
                if (!ReferenceEquals(resolved, y)) { rewritten ??= cmds.ToBuilder(); rewritten[i] = resolved; }
                var last = resolved.Words[^1];
                prevLast = PlainLastArgValue(last) is not null ? last : null;
                if (LastpipeShoptValue(y) is bool on) lastpipe = on;
            }
            else if (cmd is Command.Pipeline pipe && pipe.Commands.Length > 0)
            {
                var elems = pipe.Commands;
                ImmutableArray<Command>.Builder? newElems = null;
                for (int k = 0; k < elems.Length; k++)
                {
                    if (elems[k] is not Command.Simple s || s.Words.Length == 0) continue;
                    var r = SubstituteLastArg(s, prevLast);
                    if (ReferenceEquals(r, s)) continue;
                    newElems ??= elems.ToBuilder();
                    newElems[k] = r;
                }
                if (newElems is not null)
                {
                    elems = newElems.ToImmutable();
                    rewritten ??= cmds.ToBuilder();
                    rewritten[i] = pipe with { Commands = elems };
                }
                if ((lastpipe ?? _scriptLastpipe) || elems.Length == 1)
                    prevLast = elems[^1] is Command.Simple ls && ls.Words.Length > 0 && PlainLastArgValue(ls.Words[^1]) is not null
                        ? ls.Words[^1] : null;
            }
            else prevLast = null;
        }
        return rewritten?.ToImmutable() ?? cmds;
    }

    /// <summary>True for a literal <c>shopt -s ... lastpipe</c>, false for <c>shopt -u ... lastpipe</c>, else null.</summary>
    private static bool? LastpipeShoptValue(Command.Simple c)
    {
        if (c.Words.Length < 3 || GetLiteralValue(c.Words[0]) != "shopt") return null;
        bool? set = null, named = false;
        for (int k = 1; k < c.Words.Length; k++)
        {
            switch (GetLiteralValue(c.Words[k]))
            {
                case "-s": set = true; break;
                case "-u": set = false; break;
                case "lastpipe": named = true; break;
            }
        }
        return named == true ? set : null;
    }

    /// <summary><paramref name="y"/> with each whole-word <c>$_</c> argument replaced by <paramref name="prevLast"/>
    /// (see <see cref="ReplaceLastArgRef"/>); <paramref name="y"/> itself when there is nothing to replace.</summary>
    private static Command.Simple SubstituteLastArg(Command.Simple y, CompoundWord? prevLast)
    {
        if (prevLast is null || !y.Words.Skip(1).Any(HasLastArgRef)) return y;
        return y with { Words = y.Words.Select((w, k) => k > 0 && HasLastArgRef(w) ? ReplaceLastArgRef(w, prevLast) : w).ToImmutableArray() };
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
        // The top-level commands of the text: each may need the line scope of a discarding expansion error, which is
        // known only once they are emitted (a function defined on one line and called on another), so they are kept.
        var rootPieces = ReferenceEquals(list, _rootCommand) || ReferenceEquals(list, _execRestRoot)
            ? new List<(string Text, bool SameLine, bool LineStarts)>() : null;
        bool[]? parsedLineStarts = rootPieces is not null ? BashParser.LineStartsOf(list) : null;
        // The root script reports each input line to the shell's reader as it starts (ReaderCommandFor; "" for most scripts).
        var root = list;
        int inputLine = -1;
        list = list with { Commands = ResolveLastArgRefs(list.Commands) };
        var sb = new StringBuilder();
        for (int i = 0; i < list.Commands.Length; i++)
        {
            string readerCommand = parsedLineStarts is not null && (i == 0 || parsedLineStarts[i]) ? ReaderCommandFor(root, ++inputLine) : "";
            if (i > 0)
                sb.Append("; ");
            _readonlyViolation = false;
            // `exec >&2; rest`: the rest of the list runs under the shell-wide redirection.
            if (i + 1 < list.Commands.Length && StdExecRedirects(list.Commands[i]) is { } execRedirects)
            {
                var rest = list.Commands.RemoveRange(0, i + 1);
                if (rootPieces is null || rest.Length == 1)
                {
                    string underExec = readerCommand + EmitStatement(RestUnderExecRedirects(rest, execRedirects));
                    sb.Append(underExec);
                    rootPieces?.Add((underExec, false, false));
                    break;
                }
                // At the top level the commands after the exec are still top-level commands, each its own input line's
                // line scope (inside the redirection): as one scoped block, one discarding expansion error dropped the
                // whole rest of the script (`exec 2>/dev/null` then `echo $((1/0))` ended it).
                var restList = new Command.CommandList(rest);
                BashParser.ShareLineStarts(root, i + 1, restList);   // `list` is a copy (ResolveLastArgRefs): the parser recorded `root`
                var priorExecRest = _execRestRoot;
                _execRestRoot = restList;
                string scopedRest;
                try { scopedRest = readerCommand + EmitStatement(new Command.BraceGroup(restList, execRedirects)); }
                finally { _execRestRoot = priorExecRest; }
                string head = rootPieces.Count == 0 ? ""
                    : (_discardInteractive || rootPieces.Exists(p => PsBuild.CanDiscardLine(p.Text))) ? DiscardLineScopes(rootPieces) : sb.ToString().TrimEnd(' ', ';');
                return head.Length == 0 ? scopedRest : head + "; " + scopedRest;
            }
            if (rootPieces is not null)
            {
                // The parser's record of which commands begin a line; else the lines of their simple commands (none
                // known for a compound command).
                bool onPriorLine, nextOnThisLine;
                if (parsedLineStarts is not null)
                {
                    onPriorLine = i > 0 && !parsedLineStarts[i];
                    nextOnThisLine = i + 1 < list.Commands.Length && !parsedLineStarts[i + 1];
                }
                else
                {
                    int priorLine = i > 0 ? LastSimpleLine(list.Commands[i - 1]) : 0;
                    int thisLine = FirstSimpleLine(list.Commands[i]);
                    onPriorLine = priorLine > 0 && priorLine == thisLine;
                    nextOnThisLine = i + 1 < list.Commands.Length && thisLine > 0
                        && FirstSimpleLine(list.Commands[i + 1]) == LastSimpleLine(list.Commands[i]);
                }
                rootPieces.Add(("", onPriorLine, !onPriorLine && nextOnThisLine));
            }
            // failglob / bad substitution: bash abandons the rest of the input line, so a command that shares its line
            // with the one before runs only if nothing failed since the line began (the mark: $global:__BashFailGlob).
            int lineOfPrior = i > 0 && _lineAbandonMode ? LastSimpleLine(list.Commands[i - 1]) : 0;
            int lineOfThis = _lineAbandonMode ? FirstSimpleLine(list.Commands[i]) : 0;
            bool sameLine = lineOfPrior > 0 && lineOfPrior == lineOfThis;
            bool lineStarts = _lineAbandonMode && !sameLine && i + 1 < list.Commands.Length
                && lineOfThis > 0 && FirstSimpleLine(list.Commands[i + 1]) == LastSimpleLine(list.Commands[i]);
            if (lineStarts) sb.Append(PsBuild.FailGlobReset);
            string statement = readerCommand + EmitStatement(list.Commands[i]);
            // In POSIX mode a refused readonly assignment ends a non-interactive shell (bash 5.2: status 127). Whether
            // the mode is on is known only when it runs. REGRESSION: a `set -o posix` anywhere in the script made the
            // emitter drop every command after the assignment, also when the mode was set later or never reached.
            if (_readonlyViolation && _scriptPosix && _functionDepth == 0)
                statement += "; if (" + PsBuild.PosixOn + ") { " + EndShell("127") + " }";
            sb.Append(sameLine ? PsBuild.UnlessGlobFailed(statement) : statement);
            if (rootPieces is not null) rootPieces[^1] = rootPieces[^1] with { Text = statement };
            if (_readonlyViolation)
            {
                int failedLine = SourceLine(list.Commands[i]);
                while (failedLine > 0 && i + 1 < list.Commands.Length && SourceLine(list.Commands[i + 1]) == failedLine) i++;
            }
        }
        if (rootPieces is not null && (_discardInteractive || rootPieces.Exists(p => PsBuild.CanDiscardLine(p.Text))))
            return DiscardLineScopes(rootPieces);
        return sb.ToString();
    }

    /// <summary>
    /// The top-level commands, each in its line scope (PsBuild.DiscardLineScope): a bad substitution or an invalid
    /// indirect name ends its command and, through the line mark, the commands after it on its input line; the next
    /// line runs (bash: <c>echo ${x!y}; echo same</c> newline <c>echo next</c> prints only next, a <c>-c</c> string too).
    /// </summary>
    private static string DiscardLineScopes(List<(string Text, bool SameLine, bool LineStarts)> pieces)
    {
        var sb = new StringBuilder();
        foreach (var (text, sameLine, lineStarts) in pieces)
        {
            if (sb.Length > 0) sb.Append("; ");
            if (lineStarts) sb.Append(PsBuild.FailGlobReset);
            string scoped = PsBuild.DiscardLineScope(text);
            sb.Append(sameLine ? PsBuild.UnlessGlobFailed(scoped) : scoped);
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
                sb.Append(ChainPipelineOperand(EmitPipeline(unnegated)) + "; " + PsBuild.SetExitFromBool("$global:LASTEXITCODE -ne 0"));
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
                sb.Append(ChainPipelineOperand(EmitPipeline(pipeline)));
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
                // Its block holds the operand: a return in it leaves the function too (RedirectGroup; the passing form is
                // a statement list, so one `$( )` makes it a chain operand). REGRESSION: under set -e or an ERR trap
                // `f() { [[ 1 ]] && { return 6; }; echo no; }` printed no.
                string finalOperand = RedirectGroup(operand, PsBuild.ErrexitFinalOperand);
                sb.Append(finalOperand.StartsWith(PsBuild.GroupReturnVar, StringComparison.Ordinal) ? PsBuild.Subexpr(finalOperand) : finalOperand);
            }

            // failglob / bad substitution abandons the whole list: `echo *.ZZ || echo alt` must not fall back.
            if (i > 0 && _lineAbandonMode)
            {
                int guardStart = operandStart + andOr.Ops[i - 1].Length + 2;
                var guarded = sb.ToString(guardStart, sb.Length - guardStart);
                sb.Length = guardStart;
                sb.Append(PsBuild.ChainOperandUnlessGlobFailed(guarded));
                if (!isFinal) sb.Append("; " + PsBuild.SignalFailIfFailed());
            }
        }

        return sb.ToString();
    }

    private static bool NeedsChainOperandSubexpression(string emitted)
    {
        if (emitted.StartsWith("$(", StringComparison.Ordinal))
            return false;

        // A lone assignment statement (`set -- a b` is `$global:BashPositional = @('a', 'b')`) is no pipeline either:
        // `cd x && set -- a b` was "Unexpected token '='", a parse error for the whole script.
        return emitted.Contains(';') || emitted.StartsWith("if ", StringComparison.Ordinal)
            || ChainOperandAssignment.IsMatch(emitted);
    }

    /// <summary>
    /// A pipeline as an <c>&amp;&amp;</c> / <c>||</c> operand. A pipeline usually emits ONE PowerShell pipeline, which
    /// stays bare so it keeps streaming; under pipefail it is still one element (<see cref="PsBuild.PipefailPipeline"/>).
    /// A statement list (a negated pipeline: the status flip follows it) bare would make only its first statement the
    /// chain's operand, so it is wrapped in <c>$( ... )</c> like a compound operand (current scope). Test:
    /// <see cref="PsBuild.IsStatementList"/> (quote/nesting aware), not a raw <c>;</c>, which every script block has.
    /// </summary>
    private static string ChainPipelineOperand(string pipelineText)
        => PsBuild.IsStatementList(pipelineText) ? PsBuild.Subexpr(pipelineText) : pipelineText;

    private static readonly System.Text.RegularExpressions.Regex ChainOperandAssignment = new(@"^\$[A-Za-z_][\w:]*\s*=(?!=)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

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
        // A `$( ... )` subexpression (`return` outside a function) is an EXPRESSION, which PowerShell allows only as a
        // pipeline's first element: `echo x | return 3` emitted `... | $(...)` and the whole script failed to parse.
        if (emitted.StartsWith("& {", StringComparison.Ordinal)
            || (!emitted.StartsWith("$(", StringComparison.Ordinal) && !PsBuild.HasUnquotedStatementSeparator(emitted)))
            return emitted;

        return $"& {{ {emitted} }}";
    }

    [ThreadStatic] private static int _timeDepth;

    /// <summary>A stage of the pipeline being emitted seeds its status (<see cref="SeedStageStatus"/>).</summary>
    [ThreadStatic] private static bool _stageSeedUsed;

    /// <summary>
    /// THE start of every pipe stage whose body is shell code (a compound command, a subshell, a function or eval stage):
    /// bash runs each stage in a subshell that begins with the status from before the pipeline, so a bare <c>exit</c>
    /// or <c>return</c> there, or <c>$?</c>, sees it (<c>false; { exit; } | cat</c>: PIPESTATUS <c>1 0</c>). The stages of
    /// a PowerShell pipeline start interleaved, and the ones ahead had reset <c>$LASTEXITCODE</c> (a cmdlet's start,
    /// <c>Invoke-BashPipeStage</c>) or set it (<c>true; false | { exit; }</c>) by the time a stage's body ran.
    /// <see cref="EmitPipeline"/> captures the status before the pipeline starts (<see cref="PsBuild.WithPipeEntryStatus"/>).
    /// </summary>
    private static string SeedStageStatus()
    {
        _stageSeedUsed = true;
        return PsBuild.SeedStageStatus;
    }

    /// <summary>
    /// Set by <see cref="EmitStatementBody"/> for a pipeline at a statement position where <c>set -e</c> is armed
    /// (not a condition, an <c>&amp;&amp;</c>/<c>||</c> operand or a negation); <see cref="EmitPipeline"/> takes it.
    /// </summary>
    [ThreadStatic] private static bool _pipelineAtArmedStatement;

    /// <summary>
    /// The compound stages being emitted belong to such a pipeline: bash runs each in a subshell that inherits
    /// <c>-e</c>, so a failure in it ends that stage (<see cref="EmitCompoundStage"/>).
    /// </summary>
    [ThreadStatic] private static bool _armedStages;

    /// <summary>The variable holding the errexit suppression as it was before the armed pipeline being emitted.</summary>
    [ThreadStatic] private static string? _armedBase;
    [ThreadStatic] private static bool _armedBaseUsed;
    [ThreadStatic] private static int _armedBaseDepth;

    /// <summary>A stage's text with its armed pipeline's suppression lifted (<see cref="PsBuild.ErrexitLifted"/>).</summary>
    private static string LiftArmedStage(string stage)
    {
        _armedBaseUsed = true;
        return PsBuild.ErrexitLifted(stage, _armedBase!);
    }

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
        // Each stage of a real pipeline is a subshell in bash: errexit inside one never ends the script. A negated
        // statement decides its suppression at run time (EmitStatementBody); its body keeps the checks.
        bool runtimeNegation = _negationGuardedAtRuntime;
        _negationGuardedAtRuntime = false;
        bool exempt = pipeline.Commands.Length > 1 || (pipeline.Negated && !runtimeNegation);
        bool savedArmedStages = _armedStages;
        _armedStages = _pipelineAtArmedStatement && _errexitSetMode && pipeline.Commands.Length > 1 && !pipeline.Negated;
        _pipelineAtArmedStatement = false;
        if (exempt) _errexitExemptDepth++;
        // Every stage starts with the status from before the pipeline (SeedStageStatus): a stage that seeded it needs that
        // status captured before the pipeline starts, and a pipeline inside a stage captures its own.
        bool savedSeedUsed = _stageSeedUsed;
        _stageSeedUsed = false;
        try
        {
            string text = EmitPipelineStages(pipeline, entryScope);
            return _stageSeedUsed ? PsBuild.WithPipeEntryStatus(text) : text;
        }
        finally
        {
            _stageSeedUsed = savedSeedUsed;
            _inStdinScope = entryScope;
            _armedStages = savedArmedStages;
            if (exempt) _errexitExemptDepth--;
        }
    }

    /// <summary>
    /// <c>yes | { …; }</c> / <c>seq … | { …; }</c>: a literal-argument producer that can be unbounded, piped into a
    /// compound that opens a stdin scope. The scope reads the producer lazily from a background runspace
    /// (<see cref="PsBuild.StdinScopeLazy"/>) so a reader that stops early (<c>head -n1</c>, a lone <c>read</c>)
    /// ends the producer — a script block after a pipe would only start once the producer finished (never).
    /// Other producers keep the eager scope (<see cref="PsBuild.StdinScope"/>). Returns the emitted producer of such a
    /// two-stage pipeline (used by <c>EmitCompoundStage</c>'s stage stdin), or null for any other pipeline.
    /// </summary>
    private static string? LazyProducerText(Command.Pipeline pipeline)
    {
        if (pipeline.Commands.Length != 2 || pipeline.Negated || pipeline.Ops[0] != "|"
            || pipeline.Commands[0] is not Command.Simple producer
            || !producer.EnvPairs.IsEmpty || !producer.HereDocs.IsDefaultOrEmpty
            || !producer.Redirects.IsDefaultOrEmpty || producer.Words.IsDefaultOrEmpty
            || GetLiteralValue(producer.Words[0]) is not ("yes" or "seq"))
            return null;
        foreach (var w in producer.Words)
            if (GetLiteralValue(w) is null) return null;
        return TryEmitMappedCommand(producer, out var producerText) ? producerText : null;
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
                // `a |& b`: a's stderr records reach b as STDOUT records (PsBuild.MergeStderrIntoStdout), never as ErrorRecords,
                // whose message already holds its line's `\n` and so read as one more empty line under the record -> bytes rule.
                if (op == "|&")
                    sb.Append(PsBuild.MergeStderrIntoStdout).Append(" | ");
                else
                    sb.Append(" | ");
            }
            stageStart = sb.Length;
            // `git a |& less`, `{ git a; git b; } |& less`: the stage's stderr goes down the pipe with its stdout, so a
            // program in it runs through Invoke-BashNative (NativeMergeRunner). Transpile resets the depth after a throw.
            bool mergeStage = i < pipeline.Ops.Length && pipeline.Ops[i] == "|&";
            if (mergeStage) _stderrMergeDepth++;

            var cmd = pipeline.Commands[i];
            // `... | echo "[$(cat)]"`: echo ignores the pipe, but the command substitution inherits it as stdin.
            // Run the stage as `{ echo ...; }`, whose stdin scope hands the pipe to the substitution.
            if (i > 0 && cmd is Command.Simple { EnvPairs.IsEmpty: true, Words.Length: >= 1 } substStage
                && GetLiteralValue(substStage.Words[0]) is { } substName && StdinIgnoringCommands.Contains(substName)
                && !HasOwnStdin(substStage.Redirects) && substStage.HereDocs.IsDefaultOrEmpty
                && substStage.Words.Any(w => ContainsCommandSub(w.Parts)))
                cmd = new Command.BraceGroup(substStage, ImmutableArray<Redirect>.Empty);
            // Every pipe stage is a subshell (the last one too, unless lastpipe): a builtin that changes the shell's state
            // runs as `( cmd )`, whose scope saves and restores that state, hands it the pipe and ends at its exit -
            // `... | read v`, `... | set -- a b`, `... | shift`, `... | . f`, `... | eval "$c"`, `cd / | cat`, `exit 55 | (exit 44)`
            // (PIPESTATUS 55 44). REGRESSION: only read/mapfile (last stage) and exit were wrapped; `... | . f` and
            // `... | eval` leaked variables and $#, `... | shift` could not bind the pipe, `... | set -- a b` was not valid
            // PowerShell. A lastpipe stage runs in the shell itself.
            if (pipeline.Commands.Length > 1 && cmd is Command.Simple { Words.Length: >= 1 } stateStage
                && GetLiteralValue(stateStage.Words[0]) is { } stateName
                && (SubshellStageBuiltins.Contains(stateName)
                    || stateName is "builtin" or "command" && WrappedCommandName(stateStage) is { } wrappedState && SubshellStageBuiltins.Contains(wrappedState))
                && !(_scriptLastpipe && i > 0 && i == pipeline.Commands.Length - 1))
                cmd = new Command.Subshell(stateStage, ImmutableArray<Redirect>.Empty, KeepsLoopLevel: true);
            // Every pipe stage is a subshell: a lexical `break` must not reach the enclosing loop through the stage's script
            // block (a lastpipe stage runs in the shell itself). A compound stage starts with no loop (bash warns); a simple
            // stage, at any position, keeps the loop level, so `eval break | cat` and `echo q | break` end only that stage
            // (silently; with no loop around it the break warns). A later simple stage that runs a loop jump is a subshell
            // (SubshellStageBuiltins), which gives it the scope to end. REGRESSION: `echo q | break` in a loop warned.
            bool lastpipeHere = _scriptLastpipe && i > 0 && i == pipeline.Commands.Length - 1;
            string? wholePipelineText = null;
            WithLoopBarrier(pipeline.Commands.Length > 1 && !lastpipeHere,
                pipeline.Commands[i] is Command.Simple && !LoopControlWarnsHere ? LoopBarrierKind.EndsSubshell : LoopBarrierKind.Warn,
                () => { EmitStage(); return 0; });
            if (mergeStage) _stderrMergeDepth--;
            // A read loop that ran its lazily read producer itself is the whole pipeline (set inside EmitStage).
            if (wholePipelineText is not null) return wholePipelineText;
            void EmitStage()
            {
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
                bool ownStdin = HasOwnStdin(simple.Redirects)
                    || !simple.HereDocs.IsDefaultOrEmpty && simple.HereDocs.Any(h => h.Fd < 3);
                // A command run inside the drain block expands its words when the block runs, after the stages ahead have
                // set the status: the block starts with the status from before the pipeline (`false; true | echo $?`: 1).
                if (ownStdin)
                    sb.Append(PsBuild.DrainStageInput(SeedStageStatus() + WithPipeStageFeed(false, () => Emit(simple))));
                else
                {
                    string stage;
                    if (simple.EnvPairs.IsEmpty)
                        stage = EmitSimplePipeStage(simple, isLastStage);
                    else
                    {
                        // `… | VAR=v cmd`: the prefix applies to THAT stage only. Emit the bare stage, then
                        // wrap it in the save/set/restore block (which has to forward the stage's input).
                        var bare = simple with { EnvPairs = ImmutableArray<EnvPair>.Empty };
                        stage = WrapEnvPairsForPipeStage(
                            simple.EnvPairs, WithEnvShadow(simple.EnvPairs, () => EmitSimplePipeStage(bare, isLastStage)));
                    }
                    sb.Append(ignoresStdin ? PsBuild.DrainStageInput(SeedStageStatus() + stage) : stage);
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
                bool lastpipeStage = _scriptLastpipe && i > 0 && i == pipeline.Commands.Length - 1;
                string stage = EmitCompoundStage(cmd, isFirstStage: i == 0, entryScope, lastpipeStage,
                    i == 1 ? pipeline : null, out bool gated, out bool wholePipeline);
                // A read loop that pulls its lines from a lazily read producer runs the producer itself.
                if (wholePipeline) { wholePipelineText = stage; return; }
                // A streamed read loop gets the gate that stops its producer as a pipeline element of its own, in front of
                // the stage (and outside a pipefail stage wrapper, so the stop reaches this pipeline).
                if (gated)
                {
                    sb.Append(PsBuild.PipeGate).Append(" | ");
                    stageStart = sb.Length;
                }
                sb.Append(stage);
            }
            else if (pipeline.Commands.Length > 1)
                sb.Append(WrapPipelineStageIfStatementList(Emit(cmd)));
            else
                sb.Append(Emit(cmd));
            }

            // `echo "${x:?}" | head`: each stage is a subshell, so the parameter error ends only that stage.
            if (pipeline.Commands.Length > 1)
            {
                var rawStage = sb.ToString(stageStart, sb.Length - stageStart);
                // `${cmd=echo} hi | wc -l`: the stage's words are expanded in that subshell, so an assignment they make
                // stays there (bash: cmd is unset afterwards). A lastpipe stage runs in the shell itself.
                bool assigningStage = cmd is Command.Simple { Words.IsDefaultOrEmpty: false } ws && ws.Words.Any(MutatesShellState)
                    && !(_scriptLastpipe && i > 0 && i == pipeline.Commands.Length - 1);
                // `f | cat` with f a function of this script: the call runs in that subshell too, so its assignments stay
                // there and its `exit` ends only the stage (PIPESTATUS 3 0) - the scope is what catches the body's
                // run-time decided exit (PsBuild.DynamicExit). As a bare stage, `exit` in f ended the whole script.
                bool functionStage = cmd is Command.Simple { Words.IsDefaultOrEmpty: false } fs
                    && GetLiteralValue(fs.Words[0]) is { } stageName
                    && (_definedFunctions?.Contains(stageName) == true || _unitFunctions?.Contains(stageName) == true)
                    && !(_scriptLastpipe && i > 0 && i == pipeline.Commands.Length - 1);
                // The FIRST stage also takes the scope when its text can end the stage at run time: eval/source text
                // (`eval 'exit 3' | cat`, `source ./s.sh | cat` - exit, errexit and break there end only the stage), a
                // `break` that ends it, or, in a script that runs eval/source text, a command that may be a function defined
                // there (`f | cat`), which the emitter cannot tell from a program. Without the scope the run-time exit
                // (PsBuild.DynamicExit) ended the whole script.
                bool runTimeExitStage = i == 0 && cmd is Command.Simple { Words.IsDefaultOrEmpty: false } rs
                    && (GetLiteralValue(rs.Words[0]) is "eval" or "fc" or "source" or "."
                        || _runsDynamicCode && GetLiteralValue(rs.Words[0]) is { } rsName && !IsMappedCommandName(rsName) && !StateChangingBuiltins.Contains(rsName)
                        || rawStage.Contains("throw [PsBash.Cmdlets.BashSubshellExitException]", StringComparison.Ordinal)
                           && !rawStage.Contains("catch [PsBash.Cmdlets.BashSubshellExitException]", StringComparison.Ordinal));
                // `echo x | builtin read v`, `builtin cd / | cat`: a state builtin behind the `builtin`/`command` wrapper is
                // still a stage of its own (bash: a subshell), but the wrapper hid it from the forms above, so `v` and the
                // cwd leaked into the shell. A lastpipe stage runs in the shell itself.
                bool wrappedStateStage = cmd is Command.Simple { Words.Length: > 1 } wrapped
                    && GetLiteralValue(wrapped.Words[0]) is "builtin" or "command"
                    && WrappedCommandName(wrapped) is { } wrappedName && StateChangingBuiltins.Contains(wrappedName)
                    && !(_scriptLastpipe && i > 0 && i == pipeline.Commands.Length - 1);
                if (assigningStage || functionStage || runTimeExitStage || wrappedStateStage || rawStage.Contains("BashParamErrorException]::Reported", StringComparison.Ordinal)
                    && !rawStage.Contains("catch [PsBash.Cmdlets.BashParamErrorException]", StringComparison.Ordinal))
                {
                    sb.Length = stageStart;
                    // `set -e; g() { false; echo in-g; }; g | cat`: the stage inherits -e, so g ends at false. The body's
                    // errexit checks fire once the pipeline's suppression is lifted for the stage, and their run-time
                    // exit (PsBuild.DynamicExit) ends only the stage, as in a compound stage (EmitCompoundStage).
                    string Lifted(string stage) => functionStage && _armedStages ? LiftArmedStage(stage) : stage;
                    // Its body starts with the status from before the pipeline (`false; f | cat` with `f() { return; }`).
                    sb.Append(i == 0
                        ? PsBuild.ShellStateScope(SeedStageStatus() + Lifted(rawStage))
                        : "& { " + PsBuild.ShellStateGuard(SeedStageStatus() + Lifted(PsBuild.ForwardStageInput(rawStage))) + " }");
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
                    stageText = $"& {{ {PsBuild.ForwardStageInput(stageText)} }}";
                sb.Length = stageStart;
                sb.Append(PsBuild.PipefailStage(i, stageText));
            }
        }

        var body = sb.ToString();
        if (perStage)
            // The pipeline's status under pipefail: the last stage that failed (0 when none did).
            body = PsBuild.PipefailPipeline(pipeline.Commands.Length, body);

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
            if (simple.Words.Length >= 1 && simple.EnvPairs.IsEmpty && IsConstantStatusBuiltin(simple, out _))
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

            // `... | source f` / `... | . f`: the file's commands read the pipe, as eval text does.
            if (word0 is "eval" or "source" or "." && simple.EnvPairs.IsEmpty)
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
        => "& { " + WrapEnvPairs(pairs, PsBuild.ForwardStageInput(stage)) + " }";

    /// <summary>
    /// A compound command as a stage of a multi-stage pipeline: <c>&amp; { … }</c>. A stage AFTER the
    /// first receives the pipe as its stdin, so it opens a stdin scope (<see cref="PsBuild.StdinScope"/>):
    /// every stdin-reading command inside — <c>sort</c>, <c>cat</c>, <c>read</c>, a native — consumes the
    /// same queue, in order. The FIRST stage keeps whatever stdin its surroundings have. The one other form is
    /// a <c>while read</c> loop whose body reads nothing: it runs per record as the records arrive
    /// (<see cref="PsBuild.StreamingReadLoopStage"/>). See emitter-strategy.md "Stage stdin".
    /// </summary>
    /// <param name="cmd">The compound command.</param>
    /// <param name="isFirstStage">True for the pipeline's first stage, which keeps its surroundings' stdin.</param>
    /// <param name="entryScope">Whether the pipeline itself runs inside a stdin scope.</param>
    /// <param name="lastpipeStage">The last stage under <c>shopt -s lastpipe</c>: it runs in the shell itself.</param>
    /// <param name="pipeline">The two-stage pipeline this is the second stage of, whose producer may be read lazily
    /// (<see cref="LazyProducerText"/>); null otherwise.</param>
    /// <param name="gated">Set when the stage is a streamed read loop, which needs <see cref="PsBuild.PipeGate"/> in front.</param>
    /// <param name="wholePipeline">Set when the returned text runs the producer of <paramref name="pipeline"/> too: it is the whole pipeline.</param>
    private static string EmitCompoundStage(Command cmd, bool isFirstStage, bool entryScope, bool lastpipeStage, Command.Pipeline? pipeline,
        out bool gated, out bool wholePipeline)
    {
        gated = false;
        wholePipeline = false;
        // bash runs every pipe stage in a subshell: `{ x=2; } | cat` must leave x alone. A Subshell stage already
        // carries its own scope; a function definition changes the function table by design here.
        // The guard, not a nested scope: the stage's own `& { }` is the scope, and it must keep its `$input`.
        string Scoped(string inner) => cmd is Command.Subshell or Command.ShFunction || lastpipeStage ? inner : PsBuild.ShellStateGuard(inner);

        // `{ exit 9; } | cat`: `exit` leaves only the stage (a subshell), as in `( exit 9 )`; the stage's `& { }` is
        // what its scoped `return` returns from. A lastpipe stage runs in the shell itself, and a function body is
        // not part of the stage.
        bool ownsExit = !lastpipeStage && cmd is not Command.ShFunction;
        string Body(Func<string>? emitBody = null)
        {
            emitBody ??= () => Emit(cmd);
            // The stage starts with the status from before the pipeline (SeedStageStatus), whatever its form.
            string emit() => SeedStageStatus() + emitBody();
            if (lastpipeStage)
            {
                // It inherits -e like any stage, and being the shell itself its failing command ends the script.
                bool lastArmed = _armedStages;
                int lastExempt = _errexitExemptDepth;
                if (lastArmed) _errexitExemptDepth = 0;
                _armedStages = false;
                _lastpipeStageDepth++;
                try
                {
                    string text = emit();
                    return lastArmed ? LiftArmedStage(text) : text;
                }
                finally { _lastpipeStageDepth--; _armedStages = lastArmed; _errexitExemptDepth = lastExempt; }
            }
            if (!ownsExit) return emit();
            _subshellDepth++; var subRoOuter = EnterSubshellTables();
            // `{ echo one; false; echo two; } | cat` under set -e: the stage is a subshell that inherits -e, so it ends
            // at `false` (its status 1); the pipeline's status is still the last stage's. The stage's statements get
            // their errexit checks, which leave the stage's `& { }` (the scoped `return`), and the pipeline's suppression
            // is lifted for them. As in a subshell, the ERR trap runs inside only under errtrace.
            bool armed = _armedStages;
            string? armedBase = _armedBase;
            int savedExempt = _errexitExemptDepth;
            bool savedErrTrap = PsBuild.ErrTrapNeedsErrtrace;
            if (armed) { _errexitExemptDepth = 0; PsBuild.ErrTrapNeedsErrtrace = true; }
            // A stage that turns -e on itself (`! { set -e; false; echo x; } | cat`) gets its checks even where the
            // pipeline is exempt: the subshell's own -e applies inside it.
            else if (_errexitMode && BodySetsErrexit(cmd)) _errexitExemptDepth = 0;
            _armedStages = false;
            // A subshell starts with no loop (as EmitSubshell): `for i in 1 2; do echo x | { break; }; done` warns and
            // carries on in bash, and a `break 2` in a stage's own loop leaves that loop only. A labelled break here
            // unwound the stage's pipeline into the enclosing loop and ended it. A simple stage run as a subshell keeps the
            // stage's barrier (Command.Subshell.KeepsLoopLevel): `echo q | break` in a loop ends only the stage.
            var outerLabels = _loopLabels; var outerBarrier = _loopBarrier;
            _loopLabels = null;
            if (cmd is not Command.Subshell { KeepsLoopLevel: true }) _loopBarrier = LoopBarrierKind.Warn;
            try
            {
                string text = emit();
                if (!armed) return text;
                _armedBase = armedBase;
                return LiftArmedStage(text);
            }
            finally
            {
                _subshellDepth--;
                LeaveSubshellTables(subRoOuter);
                _armedStages = armed;
                _errexitExemptDepth = savedExempt;
                PsBuild.ErrTrapNeedsErrtrace = savedErrTrap;
                _loopLabels = outerLabels; _loopBarrier = outerBarrier;
            }
        }

        if (isFirstStage)
            return $"& {{ {Scoped(Body())} }}";

        // `producer | while read l; do ...; done` (no input redirect of its own, which would replace the pipe).
        if (cmd is Command.While { IsUntil: false } readStage && !HasInputRedirect(readStage.Redirects)
            && IsWhileRead(readStage.Cond, out var readVars, out var readIfs))
        {
            // A body that reads nothing: the loop handles each record as it arrives (a process block), not the collected
            // input after the producer has finished, behind the gate that stops the producer once the loop is done
            // (EmitPipelineStages puts it in front). Its output redirects (`done > f`, `done 2>&1`) apply to that stage.
            // A body that reads the stage's stdin too (`read b`, `cat`, `head -1`, a nested read loop) must take from the
            // same cursor as the loop, in order: a process block sees one record and cannot wait for the next, so the
            // loop reads the stage's stdin scope line by line -- the pipe's records (or a lazily read endless producer).
            int depth = _loopDepth;
            string gate = "$__psbash_wg" + depth;
            bool canStream = !lastpipeStage && readStage.Redirects.All(r =>
                r.FdVar is null && r.Here is null && r.Fd is 1 or 2 && !r.Op.StartsWith('<'));
            bool stream = false;
            string loop = Body(() =>
            {
                string text = EmitWhileRead(readVars, readIfs,
                    () =>
                    {
                        if (!canStream) return WithStdinScope(true, () => Emit(readStage.Body));
                        var b = EmitReadLoopBody(readStage.Body);
                        stream = !b.Reads;
                        return b.Text;
                    },
                    () => stream ? new ReadLoopFeed(ReadLoopFeedKind.Records, "@($_)", gate + ".Done = $false") : ReadLoopFeed.Shared);
                // Pulled from the scope, the loop's own redirects wrap it inside the stage: the dot-sourced block they put
                // around it does not hide its input, which is the shared stdin, not `$input`.
                return stream ? text : ApplyCompoundRedirects(text, readStage.Redirects, stdinScope: false);
            });
            if (stream)
            {
                gated = true;
                var streamed = new StringBuilder(PsBuild.StreamingReadLoopStage(gate, "$__psbash_ws" + depth, loop));
                AppendRedirectTail(streamed, readStage.Redirects);
                return streamed.ToString();
            }
            return StageStdin(Scoped(loop), out wholePipeline);
        }

        // Every other compound stage -- whatever its form -- reads the pipe through the same stdin scope: a `$( )` in an
        // arithmetic command or `[[ ]]`, a `select` prompt, a function definition's neighbours all inherit it in bash.
        return StageStdin(WithStdinScope(true, () => Scoped(Body())), out wholePipeline);

        // THE way a compound stage after the first gets the pipe as its stdin (emitter-strategy.md "Stage stdin"): the
        // stage's own block turns its pipeline input into the shared stdin cursor BEFORE any wrapper inside it (a
        // redirect's dot-sourced block, the subshell guard, a loop, a function, a `$( )`) could hide `$input`, and every
        // reader inside consumes that cursor. An unbounded literal producer (`yes`, `seq`) is read lazily instead, so a
        // stage that stops reading ends it; that text runs the producer too, so it is the whole pipeline.
        string StageStdin(string body, out bool whole)
        {
            string? lazy = pipeline is null ? null : LazyProducerText(pipeline);
            whole = lazy is not null;
            return lazy is not null
                ? $"& {{ {PsBuild.StdinScopeLazy(lazy, body)} }}"
                : $"& {{ {PsBuild.StdinScope(body)} }}";
        }
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
        // Where fd 2 pointed as each target was opened: that open's error is reported there (bash opens left to right,
        // so `2>/dev/null >nodir/f` is silent and `2>e >nodir/f` writes the report into e).
        var openDiag = new List<FdDest>();
        var fd1 = new FdDest(FdDestKind.Default);
        var fd2 = new FdDest(FdDestKind.Default);
        FdDest Open(string target, bool append, bool force = false)
        {
            if (target == "$null") return new FdDest(FdDestKind.Null);
            opened.Add((target, append, force));
            openDiag.Add(fd2);
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
            errorForce: errorPath is not null && opened[fd2.File].Force,
            openPlan: RedirectOpenPlan(opened, openDiag, fd1, fd2)));
        // `>f >&2`, `2>f >&2`-style lists: the stage (no -Path) passes stdout on to stderr.
        if (fd1.Kind == FdDestKind.StderrDefault) sb.Append(PsBuild.StdoutToStderrStage);
        return true;
    }

    /// <summary>
    /// The <c>-OpenPlan</c> of an <c>Invoke-BashRedirect</c> stage (PsBash.Cmdlets.RedirectOpenPlan reads it): the stage's
    /// targets in the order bash opens them, each with where its open error is reported -- or null when that is what the
    /// stage does without a plan (superseded <c>-Truncate</c> then <c>-Touch</c> targets, <c>-Path</c>, <c>-ErrorPath</c>,
    /// every error on stderr), which keeps the common tails unchanged. A role is <c>p</c> (-Path), <c>e</c> (-ErrorPath),
    /// <c>tN</c> / <c>aN</c> (-Truncate / -Touch element N); a report target after <c>:</c> is <c>null</c>, <c>out</c>
    /// (the original stdout, after <c>2&gt;&amp;1</c>) or the role of a target opened earlier.
    /// </summary>
    private static string? RedirectOpenPlan(List<(string Target, bool Append, bool Force)> opened, List<FdDest> openDiag,
        FdDest fd1, FdDest fd2)
    {
        if (opened.Count == 0) return null;
        // The roles, numbered as the stage's -Truncate / -Touch lists are built (in open order).
        var roles = new string[opened.Count];
        var ranks = new int[opened.Count];
        int truncates = 0, touches = 0;
        for (int i = 0; i < opened.Count; i++)
        {
            if (fd1.Kind == FdDestKind.File && fd1.File == i) { roles[i] = "p"; ranks[i] = 2 * opened.Count; }
            else if (fd2.Kind == FdDestKind.File && fd2.File == i) { roles[i] = "e"; ranks[i] = 2 * opened.Count + 1; }
            else if (opened[i].Append) { roles[i] = "a" + touches; ranks[i] = opened.Count + touches++; }
            else { roles[i] = "t" + truncates; ranks[i] = truncates++; }
        }
        bool needed = false;
        var steps = new string[opened.Count];
        for (int i = 0; i < opened.Count; i++)
        {
            // The stage's own order is ascending rank; any inversion means bash opens them in a different order.
            if (i > 0 && ranks[i] < ranks[i - 1]) needed = true;
            string? report = openDiag[i].Kind switch
            {
                FdDestKind.Null => "null",
                FdDestKind.StdoutDefault => "out",
                FdDestKind.File => roles[openDiag[i].File],
                _ => null,
            };
            if (report is not null) needed = true;
            steps[i] = report is null ? roles[i] : roles[i] + ":" + report;
        }
        return needed ? string.Join(' ', steps) : null;
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
        string body, ImmutableArray<Redirect> redirects, bool stdinScope = true) =>
        redirects.IsDefaultOrEmpty ? body : RedirectGroup(body, b => ApplyCompoundRedirectsCore(b, redirects, stdinScope));

    /// <summary>The construct of <see cref="ApplyCompoundRedirects"/> around <paramref name="body"/>.</summary>
    private static string ApplyCompoundRedirectsCore(string body, ImmutableArray<Redirect> redirects, bool stdinScope)
    {
        if (redirects.IsDefaultOrEmpty)
            return body;

        Redirect? inputRedirect = null;
        var tailRedirects = new List<Redirect>(redirects.Length);
        var highFds = new List<Redirect>();
        var namedFds = new List<string>();
        foreach (var redirect in redirects)
        {
            // `{ ...; } {abc}<<< w` / `{abc}>f`: bash opens a descriptor of its own (10 up), stores its number in abc and
            // leaves it open after the command; the body's stdin/stdout are not touched.
            if (redirect.FdVar is { } fdVar)
            {
                string target = redirect.Here is { } fdHere ? "(" + EmitHereDocLiteral(fdHere) + ")" : EmitArgWord(redirect.Target);
                namedFds.Add($"Invoke-BashExecFd -Variable {PsBuild.SingleQuote(fdVar)} -Operator {PsBuild.SingleQuote(redirect.OpenOperator)} -Target {target}");
                continue;
            }
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
        // Dot-sourced: the body runs in the caller's scope, so an array element it assigns (`{ a[1]=x; } 2>/dev/null`,
        // a PowerShell variable, unlike scalars in $env:) outlives the group.
        var sb = new StringBuilder(". { ");
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
                result = PsBuild.InputRedirectGuard(inTarget, $"Invoke-BashCat {inTarget} -RawStdinBytes | {result}", RedirectFailExit(),
                    InputFailTail(redirects, inputRedirect));
            else if (stdinScope)
                result = $"@() | {result}";
        }

        result = WithScopedHighFds(result, highFds);
        // One dot-sourced block, so the whole thing is still a single pipeline element.
        return namedFds.Count == 0 ? result : ". { " + string.Join("; ", namedFds) + "; " + result + " }";
    }

    private static string EmitCd(ImmutableArray<CompoundWord> args)
    {
        // Leading options: `--` ends them (`cd -- "$dir"` is the safe spelling for a dir that starts with `-`);
        // -L / -P pick logical / physical symlink handling, which this cd does not distinguish, so they are accepted.
        // Any other option is bash's usage error (`cd: --: invalid option` + usage, status 2), not a directory name.
        while (!args.IsEmpty && GetLiteralValue(args[0]) is { Length: > 1 } opt && opt[0] == '-')
        {
            args = args.RemoveAt(0);
            if (opt == "--") break;
            int bad = opt[1] == '-' ? 1 : opt.AsSpan(1).IndexOfAnyExcept("LPe@") is int k and >= 0 ? k + 1 : -1;
            if (bad > 0)
                return PsBuild.BuiltinInvalidOption("cd", BuiltinOptions.Spelling(opt, opt[bad]));
        }
        // bash: `cd a b` is "too many arguments", status 1, and the directory does not change.
        if (args.Length > 1)
            return PsBuild.ShellDiagnosticLine("'cd: too many arguments'") + "; " + PsBuild.SetStatus(1);

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
                targetExpr = "$HOME + " + QuotePsString("/" + literal[2..]);
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
            "$__psbash_cd_resolved = [System.IO.Path]::GetFullPath(" + PsBuild.RuntimeMapPath("[string]$__psbash_cd_target") + ", $__psbash_cd_base); " +
            "if ([System.IO.Directory]::Exists($__psbash_cd_resolved) -and [PsBash.Cmdlets.BashRuntime]::CdPathWalkExists(" + PsBuild.RuntimeMapPath("[string]$__psbash_cd_target") + ", $__psbash_cd_base)) { " +
            // $PWD / $OLDPWD and the echoed directory are the script-visible shell form (C:/x, docs/specs/paths.md);
            // the process cwd and the PowerShell location stay native.
            "$env:OLDPWD = " + PsBuild.RuntimeToShellPath("$__psbash_cd_base") + "; " +
            "$global:__PsBashCwd = $__psbash_cd_resolved; " +
            "[System.Environment]::CurrentDirectory = $__psbash_cd_resolved; " +
            "$env:PWD = " + PsBuild.RuntimeToShellPath("$__psbash_cd_resolved") + "; " +
            "Set-Location -LiteralPath $__psbash_cd_resolved -ErrorAction SilentlyContinue; " +
            (isDash ? "Write-Output $env:PWD; " : useCdPath ? "if ($__psbash_cd_via) { Write-Output $env:PWD }; " : "") +
            "$global:LASTEXITCODE = 0 " +
            "} else { " + PsBuild.ShellDiagnosticLine("'cd: ' + $__psbash_cd_target + ': No such file or directory'") + "; $global:LASTEXITCODE = 1 }";

        // Relative targets resolve against bash's logical $PWD (BashRuntime.CdBase), not getcwd: on macOS that is physical.
        var prefix = "$__psbash_cd_target = " + targetExpr + "; $__psbash_cd_base = [PsBash.Cmdlets.BashRuntime]::CdBase($ExecutionContext.SessionState); " + (useCdPath
            ? "$__psbash_cd_via = [PsBash.Cmdlets.BashRuntime]::CdPathLookup([string]$__psbash_cd_target, $__psbash_cd_base); "
              + "if ($__psbash_cd_via) { $__psbash_cd_target = $__psbash_cd_via }; "
            : "");
        if (isDash)
        {
            // `cd -` with no prior directory: bash prints "cd: OLDPWD not set" and fails
            // rather than trying to resolve an empty path (GetFullPath would throw).
            return prefix +
                "if ([string]::IsNullOrEmpty([string]$__psbash_cd_target)) { " +
                PsBuild.ShellDiagnosticLine("'cd: OLDPWD not set'") + "; $global:LASTEXITCODE = 1 " +
                "} else { " + resolveAndAct + " }";
        }

        // A word known only at run time can be an option (`cd $o`, o=-Q): bash's usage error, as for a literal one above.
        if (!args.IsEmpty && GetLiteralValue(args[0]) is null)
            return prefix + PsBuild.RunTimeBuiltinOptionCheck("cd", "@([string]$__psbash_cd_target)", "LPe@", resolveAndAct);

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
            try { statements.Add(EmitArithCommand(new Command.ArithCommand(BashArithmeticParser.Parse(text)), label: "let")); }
            catch (ParseException) { return null; }
            // `let '1+'`: bash reports the syntax error when the command runs (`let: 1+: syntax error: operand expected`,
            // status 1) and the script goes on. Uncaught here it ended the transpile, so no line of the script ran.
            // The resource limits are not syntax errors: they reach the caller (and its large-stack retry).
            catch (BashArithmeticParseException ex) when (!ex.Message.Contains(NestingGuard.TooDeepMessage, StringComparison.Ordinal)
                                                          && !ex.Message.Contains(NestingGuard.TooLongMessage, StringComparison.Ordinal))
            {
                return null;
            }
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

    /// <summary>
    /// True when <paramref name="cmd"/> is a literal program name (not a ps-bash command, builtin or function of this
    /// script) whose stderr bash merges into its stdout: by its own redirects (<c>2&gt;&amp;1</c>, <c>&amp;&gt;f</c>,
    /// <c>&gt;f 2&gt;&amp;1</c>, <c>|&amp;</c>, which the parser gives as <c>2&gt;&amp;1</c>) or an enclosing compound's
    /// (<see cref="_stderrMergeDepth"/>). Such a command runs through <c>Invoke-BashNative</c>; whether it is merged at
    /// run time (and a program at all) is decided there. See <c>docs/specs/stdio-messaging.md</c> �4.
    /// </summary>
    private static bool NativeMergeRunner(Command.Simple cmd)
    {
        if (GetLiteralValue(cmd.Words[0]) is not { Length: > 0 } name) return false;
        if (_stderrMergeDepth == 0 && !RedirectsMergeStderr(cmd.Redirects)) return false;
        return StdinReaders.Classify(name) == StdinReaders.Kind.Native
            && !IsMappedCommandName(name)
            && _definedFunctions?.Contains(name) != true && _unitFunctions?.Contains(name) != true
            && !PsStatementKeywordCommands.Contains(name) && !PsKeywordCommandNames.Contains(name);
    }

    /// <summary>A compound command's own redirects (none for a simple command, a list, a pipeline...).</summary>
    private static ImmutableArray<Redirect> CompoundRedirects(Command cmd) => cmd switch
    {
        Command.If c => c.Redirects,
        Command.ForIn c => c.Redirects,
        Command.ForArith c => c.Redirects,
        Command.While c => c.Redirects,
        Command.Case c => c.Redirects,
        Command.Subshell c => c.Redirects,
        Command.BraceGroup c => c.Redirects,
        _ => default,
    };

    /// <summary>
    /// True when, after <paramref name="redirects"/> are applied left to right as bash does, fd 2 is the SAME open
    /// description as fd 1 (a dup or <c>&amp;&gt;</c>), not two opens of anything: <c>2&gt;&amp;1</c>, <c>&gt;f 2&gt;&amp;1</c>,
    /// <c>&amp;&gt;f</c>, <c>&gt;&amp;f</c>. Not <c>2&gt;&amp;1 &gt;f</c> (stderr to the old stdout) nor <c>2&gt;f</c>.
    /// </summary>
    internal static bool RedirectsMergeStderr(ImmutableArray<Redirect> redirects)
    {
        if (redirects.IsDefaultOrEmpty) return false;
        // 0 = discarded (/dev/null): output nobody reads has no order to keep.
        int fd1 = 1, fd2 = 2, next = 3;
        foreach (var r in redirects)
        {
            var target = GetLiteralValue(r.Target);
            int file = target == DevicePath.Null ? 0 : next++;
            switch (r.Op)
            {
                case "&>" or "&>>":
                    fd1 = fd2 = file;
                    break;
                case ">" or ">>" or ">|" when r.Fd is 1 or 2:
                    if (r.Fd == 1) fd1 = file; else fd2 = file;
                    break;
                case ">&" when r.Fd == 2 && target == "1":
                    fd2 = fd1;
                    break;
                case ">&" when r.Fd == 1 && target == "2":
                    fd1 = fd2;
                    break;
                case ">&" when r.Fd == 1 && target is { Length: > 0 } && target != "-" && !IsAllDigits(target):
                    fd1 = fd2 = next++;
                    break;
                case ">&" when r.Fd is 1 or 2:
                    if (r.Fd == 1) fd1 = next++; else fd2 = next++;
                    break;
            }
        }
        // Both on the original stderr (`>&2`) is stdout redirected, not a merge PowerShell's 2>&1 carries.
        return fd1 == fd2 && fd1 != 2 && fd1 != 0;
    }

    /// <summary>
    /// A command name PowerShell may resolve to an ALIAS (the module's <see cref="ModuleAliases"/>, or a mapped name): a
    /// script function of that name is called explicitly, since an alias outranks a function in PowerShell and bash runs
    /// the function (<c>true() { ...; }; true</c> ran Invoke-BashTrue). Guard: <c>FunctionShadowsModuleAliasGuardTests</c>.
    /// </summary>
    private static bool IsAliasedCommandName(string name) => ModuleAliases.Names.Contains(name) || IsMappedCommandName(name);

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

        // A function the script defines under a mapped command's name wins over it (bash: functions precede builtins and PATH),
        // `builtin` and `command` included: they are not special builtins.
        if (_definedFunctions is { Count: > 0 } && _definedFunctions.Contains(name))
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
                result = EmitPassthrough("Invoke-BashMapfile", args);
                return true;
            case "readarray":
                // One builtin under two names; its diagnostics name the one typed (`readarray: --: invalid option`).
                result = EmitPassthrough("Invoke-BashMapfile", args, cmdletSwitch: " -BashName readarray");
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
            // Builtins a non-interactive shell here can only refuse or report (PsBash.psm1).
            case "bind":
                result = EmitPassthrough("Invoke-BashBind", args);
                return true;
            case "caller":
                result = EmitPassthrough("Invoke-BashCaller", args);
                return true;
            case "disown":
                result = EmitPassthrough("Invoke-BashDisown", args);
                return true;
            case "enable":
                result = EmitPassthrough("Invoke-BashEnable", args);
                return true;
            case "help":
                result = EmitPassthrough("Invoke-BashHelp", args);
                return true;
            case "logout":
                result = EmitPassthrough("Invoke-BashLogout", args);
                return true;
            case "suspend":
                result = EmitPassthrough("Invoke-BashSuspend", args);
                return true;
            case "history":
                result = EmitPassthrough("Invoke-BashHistory", args);
                return true;
            case "fc":
                result = EmitFc(args);
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
                // `builtin ls`: only a bash builtin can be named (bash: "not a shell builtin", status 1), never a file.
                if (TryGetStaticArgValue(rest[0]) is { } named && !BashBuiltins.Names.Contains(named))
                {
                    // One `. { }` group: the redirect EmitMappedStandalone appends (`2>/dev/null`) covers the report too.
                    result = PsBuild.WithRedirectTail(
                        PsBuild.ShellDiagnosticLine(PsBuild.SingleQuote("builtin: " + named + ": not a shell builtin")) + "; " + PsBuild.SetStatus(1),
                        static _ => { });
                    return true;
                }
                var priorFns = _definedFunctions;
                if (priorFns is not null && GetLiteralValue(rest[0]) is { } bn && priorFns.Contains(bn))
                {
                    _definedFunctions = new HashSet<string>(priorFns, StringComparer.Ordinal);
                    _definedFunctions.Remove(bn);
                }
                // The redirects (`builtin cd /x 2>/dev/null`) are the CALLER's: every caller of this switch appends the
                // command's tail itself (EmitMappedStandalone, a pipe stage), so the inner command keeps only its stdin
                // (`< f`, which it opens itself) and the tail binds to one `. { }` group around it. Emitting them here
                // too gave `. { . } 2>$null 2>$null`, which PowerShell refuses to parse.
                var tailRedirects = cmd.Redirects.IsDefaultOrEmpty ? 0 : cmd.Redirects.Count(r => !(r.Op == "<" && r.Fd == 0));
                var ownRedirects = tailRedirects == 0 ? cmd.Redirects : cmd.Redirects.Where(r => r.Op == "<" && r.Fd == 0).ToImmutableArray();
                try { result = EmitSimple(cmd with { Words = rest, Redirects = ownRedirects }); }
                finally { _definedFunctions = priorFns; }
                // A statement list (`cd`) becomes one group for the tail; a single command (`read`) stays bare: a `. { }`
                // group would not hand it the pipe it reads.
                if (tailRedirects > 0 && !IsSingleRuntimeCommand(result)) result = RedirectGroup(result, static body => PsBuild.WithRedirectTail(body, static _ => { }));
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

    /// <summary>
    /// A quoted or backslash-escaped command name (<c>'local'</c>, <c>"eval"</c>, <c>\export</c>) only escapes alias
    /// expansion in bash: the builtin of that name still runs. Called after <see cref="ExpandAliases"/> (which looks at
    /// bare words only), it makes the name of a bash builtin (<see cref="BashBuiltins.Names"/>) a plain word for
    /// everything after. REGRESSION: every builtin the emitter implements (<c>local</c>, <c>export</c>, <c>set</c>,
    /// <c>eval</c>, <c>read</c>, <c>declare</c>, <c>return</c>, <c>break</c>) was looked up as a program,
    /// <c>command not found</c> (127). Other names keep their quotes (a function or program runs either way). The bare
    /// word is one alias expansion passes over (<see cref="NoAliasWord"/>): EmitSimple re-enters itself (and so
    /// ExpandAliases) for parts of one command, and bash never expands a quoted word (<c>alias eval='echo no';
    /// 'eval' 'echo x'</c> prints x).
    /// <para>A declaration builtin's operands are then ORDINARY words, as behind <c>builtin</c> (bash 5.2: with
    /// x='a b', <c>'export' v=$x</c> exports v=a and b; <c>'declare' -a A=(x y)</c> is a syntax error): it becomes
    /// <c>builtin NAME ...</c>, the wrapper path (<see cref="TryEmitDynamicAssignmentBuiltin"/>). The parser makes the
    /// static spelling (<c>'local' v=3</c>, no expansion) the declaration, as it does behind a wrapper.</para>
    /// </summary>
    private static Command.Simple UnquoteBuiltinName(Command.Simple cmd)
    {
        if (cmd.Words.IsDefaultOrEmpty || GetLiteralValue(cmd.Words[0]) is not null) return cmd;
        if (TryGetStaticArgValue(cmd.Words[0]) is not { } name || !BashBuiltins.Names.Contains(name)) return cmd;
        var words = cmd.Words.SetItem(0, NoAliasWord(name));
        return cmd with { Words = name is "declare" or "typeset" or "local" or "export" or "readonly" ? words.Insert(0, NoAliasWord("builtin")) : words };
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
        var scriptBody = PsBuild.EscapeForSingleQuote(transpiledCmd);
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
                // The body runs once a prefix binding is in force (`v=t eval 'echo $v'` prints t): only eval's own words
                // expand before it, so the body's reads are not the pre-assignment shadow.
                var shadow = _envPrefixShadow;
                _envPrefixShadow = null;
                // The text is emitted in place, so it keeps this site's loops and subshell barrier. At the top of a script
                // (no function, no loop) its `break` warns and its `return` is refused, as they would be here.
                bool scriptTop = _context == TranspileContext.Default && _functionDepth == 0;
                var priorBarrier = _loopBarrier; bool priorTop = _evalAtScriptTop;
                if (scriptTop && _loopLabels is not { Count: > 0 } && _loopBarrier == LoopBarrierKind.None) _loopBarrier = LoopBarrierKind.Warn;
                _evalAtScriptTop = priorTop || scriptTop;
                // The text's lines are the script's from the eval's own line on (bash: `eval $'true\nx'` on line 7
                // reports x on line 8), so its statements record those lines (BASH_LINENO, diagnostics).
                int priorLineBase = _lineBase;
                if (_statementLine > 0) _lineBase = _statementLine - 1;
                string? inner;
                try { inner = Transpile(joined, TranspileContext.Eval); }
                finally { _envPrefixShadow = shadow; _loopBarrier = priorBarrier; _evalAtScriptTop = priorTop; _lineBase = priorLineBase; }
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
            "$__psbash_eval_src = @(" + emittedArgs + ") -join ' '; " +
            // Assembly-qualified type names: BashTranspiler/TranspileContext live
            // in PsBash.Transpiler.dll (assembly name differs from the namespace
            // since REFACTOR-5a's project split, commit 1f23039). This runtime
            // string is evaluated via Invoke-Expression inside the ps-bash-host
            // SDK runspace. PowerShell's [Type] literal resolver only finds types
            // in already-loaded runspace assemblies UNLESS the name is
            // assembly-qualified — given the assembly name it loads the assembly
            // on demand. A bare [PsBash.Core.Transpiler.BashTranspiler] silently
            // failed to resolve there, so eval emitted empty output.
            // A syntax error in the eval text is eval's own failure (status 2), not an exception out of the surrounding script.
            RunTimeBashTextCall("eval");
    }

    /// <summary>`, [string[]]@('a','b')`: the locals of the function this eval runs in (empty at top level).</summary>
    private static string EvalLocalNamesArg() =>
        _functionDepth > 0 && _functionLocalNames is { Count: > 0 } locals
            ? ", [string[]]@(" + string.Join(",", locals.Where(IsValidVarName).Select(PsBuild.SingleQuote)) + ")"
            : "";

    /// <summary>
    /// The ONE emitted call of bash text run at run time (<see cref="PsBuild.RunBashTextCall"/>) with what this call site
    /// hands it: the loop a <c>break</c> in the text leaves, the calling function's locals, and the check that makes a
    /// <c>return</c> in the text (which leaves only the Invoke-Expression) leave the function, or the eval text, it ran
    /// in; at the top of a script, or in a subshell, there is nothing to return from. <c>eval</c> and <c>fc -s</c> run
    /// their text through it (guard <c>RunBashTextCallGuardTests</c>).
    /// </summary>
    private static string RunTimeBashTextCall(string label)
    {
        string returnCheck = _subshellDepth > 0 ? ""
            : _functionDepth > 0 ? LeavingReturn(PsBuild.EvalReturnCheck(inFunction: true))
            : _context == TranspileContext.Eval ? LeavingReturn(PsBuild.EvalReturnCheck(inFunction: false)) : "";
        return PsBuild.RunBashTextCall(label, LoopModeForCallSite(),
            EvalLocalNamesArg() is { Length: > 0 } locals ? locals : ", $null", returnCheck);
    }

    /// <summary>
    /// How many PowerShell <c>return</c>s that leave a function, eval or source text, or a subshell have been emitted
    /// (each goes through <see cref="LeavingReturn"/>, or counts itself). Only ever grows.
    /// </summary>
    [ThreadStatic] private static int _leavingReturns;

    /// <summary><see cref="_leavingReturns"/> when the command being emitted began (<see cref="EmitCommand"/>).</summary>
    [ThreadStatic] private static int _groupReturnMark;

    /// <summary><paramref name="text"/>, which ends with a PowerShell <c>return</c> that leaves a function, eval text or a
    /// subshell: counted for <see cref="RedirectGroup"/>.</summary>
    private static string LeavingReturn(string text)
    {
        _leavingReturns++;
        return text;
    }

    /// <summary>
    /// THE way to put an emitted body in a script block for its redirects (<c>{ ...; } 2&gt;x</c>, <c>while ... done
    /// &lt;f</c>, <c>if ... fi &gt;x</c>, <c>eval '...' 3&gt;x</c>, ...): <paramref name="build"/> makes the whole construct
    /// around the body it is handed. A PowerShell <c>return</c> leaves only that block, so when the body has one (this
    /// command emitted a <see cref="LeavingReturn"/>) the construct is <see cref="PsBuild.GroupPassingReturn"/>: the
    /// return leaves the caller as well (bash: <c>f() { { return 6; } 2&gt;/dev/null; echo no; }</c> returns 6). A body
    /// with no return keeps the plain block. Not for <c>source</c>, whose <c>return</c> ends only the file (it emits
    /// none). Guard: <c>RedirectGroupGuardTests</c>.
    /// </summary>
    private static string RedirectGroup(string body, Func<string, string> build)
    {
        if (_leavingReturns == _groupReturnMark) return build(body);
        _leavingReturns++;   // the check after the block is one more, for an enclosing block
        return PsBuild.GroupPassingReturn(build(PsBuild.GroupBody(body)));
    }

    /// <summary>
    /// <c>fc</c>: Invoke-BashFc lists, refuses an editor, or picks a re-run (<c>fc -s</c>, <c>fc -e -</c>) that it leaves
    /// for <see cref="PsBuild.FcTakeRerun"/>; the re-run then runs here as eval text, through the same call as eval, so a
    /// <c>return</c>, <c>break</c> or <c>local</c> in it acts in the caller.
    /// </summary>
    private static string EmitFc(ImmutableArray<CompoundWord> args) =>
        PsBuild.FcTakeRerun(EmitPassthrough("Invoke-BashFc", args)) + RunTimeBashTextCall("fc");

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
        new HashSet<string>(StringComparer.Ordinal) { "source", "getopts", "mapfile", "seq", "flock", "timeout", "times", "help", "history", "hash", "umask", "ulimit", "compgen", "shopt", "complete", "compopt", "tee", "cp", "mv", "rm", "mkdir", "chmod", "rmdir", "ln", "touch", "xargs", "time", "env", "command", "declare", "type", "bash", "sh", "awk", "head", "tail", "wc", "cat", "tac", "nl", "uniq", "fold", "expand", "unexpand", "paste", "join", "comm", "split", "strings", "base64", "stat", "file", "cut", "sort", "grep", "sed", "rg", "find", "echo", "printf", "test", "ls", "du", "tree", "column", "gzip", "tar", "md5sum", "sha1sum", "sha256sum", "diff", "jq", "od", "readlink", "realpath", "kill", "ping", "uname", "shuf", "traceroute", "id", "hostname", "unset" };

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
    /// <summary>
    /// A static operand (from <see cref="OrderedArgDashLiteral"/>) that a PowerShell callee never receives as written: a
    /// bare <c>--</c> is consumed as end-of-parameters, <c>--%</c> stops parsing, and <c>-x:y</c> is split into <c>-x:</c>
    /// and <c>y</c>. A bash function, and a stand-in for a program (a <c>#!</c> file, a background job's program), is
    /// PowerShell code, so these are single-quoted for every unmapped command; a program gets the same text either way.
    /// A plain <c>-e</c> stays bare (it reaches <c>$args</c> as <c>-e</c>; stand-ins pass it on through PsBuild.ArgvSplat).
    /// </summary>
    internal static bool BinderRewritesOperand(string dashLiteral)
        => dashLiteral == "--" || dashLiteral.StartsWith("--%", StringComparison.Ordinal) || dashLiteral.IndexOf(':') > 0;

    /// <summary>A PowerShell command name, Verb-Noun (<c>Get-ChildItem</c>): its operands keep PowerShell's parameter syntax.</summary>
    internal static bool IsPowerShellCommandName(string name)
    {
        int dash = name.IndexOf('-');
        if (dash < 2 || dash != name.LastIndexOf('-') || dash == name.Length - 1) return false;
        if (!char.IsAsciiLetterUpper(name[0]) || !char.IsAsciiLetterUpper(name[dash + 1])) return false;
        for (int i = 0; i < name.Length; i++)
            if (i != dash && !char.IsAsciiLetterOrDigit(name[i])) return false;
        return true;
    }

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
        // A lone "$@" / "${a[@]}" splats as well: left as one word it was emitted as a double-quoted string, so `cat "$@"`
        // with two files read one file named "a b", and `f() { grep "$@"; }; f -i x` gave grep the single argument "-i x".
        if (HasUnquotedVarSplatArg(args, pipelineProcessSubIndex) || args is [var loneArg] && IsQuotedArrayAllWord(loneArg) || args.Any(HasIndirectExpansion))
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
    private static string SqEsc(string s) => PsBuild.EscapeForSingleQuote(s);

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
