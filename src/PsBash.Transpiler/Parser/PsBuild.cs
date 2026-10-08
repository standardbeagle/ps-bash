namespace PsBash.Core.Parser;

/// <summary>
/// The PowerShell source builder: the ONE place that knows how to construct the
/// recurring PowerShell-text fragments the emitter needs. Every fragment that has
/// bitten us before — quoting/escaping at the bash↔PowerShell seam, the exit-code
/// test wrapper, output suppression, scriptblock isolation, RC-7 word-split
/// splatting, the null-safe pipeline probe — lives here as a named, tested
/// primitive instead of being hand-concatenated at each call site.
///
/// <para>
/// WHY THIS EXISTS. Hand-built PS strings drifted: the negated-pipeline condition
/// omitted the <c>[void]</c> that the general-command condition had (so
/// <c>if ! echo X</c> captured <c>X</c> into a 2-element array and took the wrong
/// branch), one double-quote literal branch forgot to escape backticks while its
/// twin did, and the exit-code scope flipped between bare <c>$LASTEXITCODE</c> and
/// <c>$global:LASTEXITCODE</c> between sites. Centralizing kills the whole class:
/// fix once here, every call site is correct, and the escaping is unit-tested
/// against the failure axes (embedded quote / backtick / <c>$</c>).
/// </para>
///
/// <para>
/// CONVENTIONS. (1) Exit-code scope is ALWAYS <c>$global:LASTEXITCODE</c> — the
/// automatic <c>$LASTEXITCODE</c> reads the same value, but a scriptblock that
/// ASSIGNS it must target global, so we use the explicit form everywhere for one
/// consistent shape. (2) Any command whose output must not pollute a boolean is
/// wrapped in <see cref="Void"/>. (3) Double-quote escaping is backtick-FIRST
/// (it is the escape char that the <c>$</c>/<c>"</c> replacements introduce).
/// </para>
/// </summary>
public static class PsBuild
{
    // ─────────────────────────────── Portable /tmp ───────────────────────────────

    /// <summary>
    /// Runtime-evaluated temp directory for a bash <c>/tmp</c> operand. The transpiled text
    /// may run on any OS: Windows has no <c>/tmp</c> (map to <c>$env:TEMP</c>), while on
    /// Linux/macOS <c>$env:TEMP</c> is usually unset (keep the literal <c>/tmp</c>).
    /// <c>$env:OS</c> is <c>Windows_NT</c> only on Windows.
    /// </summary>
    public const string TempDirExpr = "$($env:OS -eq 'Windows_NT' ? $env:TEMP : '/tmp')";

    /// <summary>
    /// Word form of <c>/tmp/REST</c>: <c>"TempDirExpr/REST"</c>. Double-quoted because a bare
    /// <c>$(…)/rest</c> is two arguments in PowerShell. <paramref name="rest"/> is emitted word
    /// text and must already be safe inside double quotes (no bare <c>"</c>); a REST containing
    /// one falls back to the Windows-only legacy form.
    /// </summary>
    public static string TempPath(string rest)
        => rest.Contains('"')
            ? "$env:TEMP\\" + rest
            : "\"" + TempDirExpr + "/" + rest + "\"";

    /// <summary>
    /// <paramref name="valueExpr"/> (a PowerShell VALUE expression) through the runtime path policy:
    /// <c>[PsBash.Cmdlets.BashRuntime]::MapPath([string](expr))</c> — <c>/tmp</c> → <c>$env:TEMP</c> and
    /// unix drive paths on Windows, unchanged elsewhere (PsBash.Core.RuntimePath). For paths only
    /// known at run time (<c>cd $d</c>, <c>[ -e "$d/f" ]</c>); the Cmdlets static, not the Transpiler
    /// type, so it resolves in any runspace that has the cmdlets loaded.
    /// </summary>
    public static string RuntimeMapPath(string valueExpr)
        => "[PsBash.Cmdlets.BashRuntime]::MapPath([string](" + valueExpr + "))";

    /// <summary>
    /// A path word whose value is only known at run time (<c>2&gt; $dir/f</c>), mapped through the
    /// runtime path policy (<see cref="RuntimeMapPath"/>) when it is evaluated — the run-time twin of
    /// the emitter's literal <c>/c/…</c> and <c>/tmp/</c> rewrites. <paramref name="argWord"/> is
    /// emitted ARGUMENT-mode text (<c>$env:d/f</c>, <c>"$env:d/f"</c>), so it is passed as an
    /// argument to a script block, never spliced into an expression (where <c>/</c> divides).
    /// </summary>
    public static string RuntimeWindowsPath(string argWord)
        => "$(& { " + RuntimeMapPath("$args[0]") + " } " + argWord + ")";
    // ─────────────────────────────── Quoting / escaping ───────────────────────────────

    /// <summary>The chars that must be escaped inside a PowerShell double-quoted string.</summary>
    private static readonly System.Buffers.SearchValues<char> s_doubleQuoteSpecials =
        System.Buffers.SearchValues.Create("`$\"");

    /// <summary>
    /// Wrap <paramref name="value"/> in a PowerShell single-quoted literal, escaping
    /// embedded single quotes by doubling (<c>'</c> → <c>''</c>) — the only escape a
    /// PS single-quoted string honors. Use for literal paths, positional args, and any
    /// value that must reach PowerShell verbatim with no expansion.
    /// </summary>
    public static string SingleQuote(string value)
        // Fast path (the common case — no embedded quote): one Concat, skip the Replace scan+alloc.
        => value.IndexOf('\'') < 0 ? "'" + value + "'" : "'" + value.Replace("'", "''") + "'";

    /// <summary>
    /// Escape <paramref name="value"/> for placement INSIDE a PowerShell double-quoted
    /// string (returns the inner text, no surrounding quotes). Order is load-bearing:
    /// backtick first (it is PowerShell's escape char, introduced by the next two
    /// replacements), then <c>$</c> (starts variable expansion), then <c>"</c> (ends
    /// the string). Called once per literal string part during emission — the hottest
    /// builder — so the clean case is allocation-free: a single <see cref="System.Buffers.SearchValues{T}"/>
    /// scan returns the original string with no rebuild when nothing needs escaping
    /// (vs. three full Replace passes).
    /// </summary>
    public static string EscapeForDoubleQuote(string value)
    {
        if (value.AsSpan().IndexOfAny(s_doubleQuoteSpecials) < 0)
            return value;
        return value.Replace("`", "``").Replace("$", "`$").Replace("\"", "`\"");
    }

    /// <summary>
    /// Wrap <paramref name="value"/> in a PowerShell double-quoted string with the
    /// inner text escaped via <see cref="EscapeForDoubleQuote"/>.
    /// </summary>
    public static string DoubleQuote(string value) => "\"" + EscapeForDoubleQuote(value) + "\"";

    // ─────────────────────────── Output suppression / wrapping ────────────────────────

    /// <summary>Suppress an expression's pipeline output: <c>[void](expr)</c>.</summary>
    public static string Void(string expr) => "[void](" + expr + ")";

    /// <summary>Isolate a body in a scriptblock invocation: <c>&amp; { body }</c>.</summary>
    public static string Subshell(string body) => "& { " + body + " }";

    /// <summary>
    /// A bash SUBSHELL scope: <c>&amp; { save; try { BODY } finally { restore } }</c>. The child scope
    /// isolates PS-scoped state (arrays, functions, <c>set -e</c>'s <c>$ErrorActionPreference</c>,
    /// <c>set -u</c>'s StrictMode); <c>PsBash.Cmdlets.BashShellState</c> saves and restores what a scope
    /// does not — env-var bash variables, the <c>$global:</c> shell flags/positionals/traps, <c>shopt</c> —
    /// and reports a <c>set -x</c> to undo. Without it <c>( set -e; true ); false; echo ok</c> stopped the
    /// PARENT at <c>false</c>, and <c>( x=1 ); echo $x</c> printed 1. <paramref name="finallyTail"/> runs
    /// after the restore (the working-directory pop). <c>$?</c> is deliberately not restored.
    /// The temp is per child scope, so nested subshells never share it.
    /// </summary>
    public static string ShellStateScope(string body, string? finallyTail = null) =>
        "& { $__psbash_ss = [PsBash.Cmdlets.BashShellState]::Save($ExecutionContext.SessionState); try { "
        + body
        + " } finally { $__psbash_xt = [PsBash.Cmdlets.BashShellState]::Restore($ExecutionContext.SessionState, $__psbash_ss); "
        + "if ($null -ne $__psbash_xt) { if ($__psbash_xt) { Set-PSDebug -Trace 1 } else { Set-PSDebug -Off } }"
        + (finallyTail is null ? "" : "; " + finallyTail)
        + " } }";

    /// <summary>Wrap in a subexpression: <c>$(expr)</c>.</summary>
    public static string Subexpr(string expr) => "$(" + expr + ")";

    /// <summary>
    /// Suppress an emitted statement's output, choosing the form that survives a
    /// statement LIST. <c>(...)</c> (grouping) cannot hold <c>stmt1; stmt2</c>
    /// ("Missing closing ')'"), only the subexpression <c>$(...)</c> can — so a value
    /// containing <c>"; "</c> uses <c>[void]$(...)</c>, a single statement the cheaper
    /// <c>[void](...)</c>. Centralizes the choice the &amp;&amp;/|| chain made inline.
    /// </summary>
    public static string VoidStatement(string text) =>
        text.Contains("; ", System.StringComparison.Ordinal)
            ? "[void]$(" + text + ")"
            : "[void](" + text + ")";

    /// <summary>
    /// True when <paramref name="psText"/> is a PowerShell statement LIST — i.e. it
    /// contains a <c>;</c> at nesting depth 0, outside any quoted span. Such text
    /// CANNOT head a pipeline: <c>$__saved = 1; try { … } finally { … } | Foo</c>
    /// is the parse error "An empty pipe element is not allowed", because the
    /// pipe binds only to the trailing <c>finally</c> block.
    /// <para>
    /// The scan is quote- and nesting-aware on purpose. A naive
    /// <c>Contains(";")</c> misfires on a <c>;</c> inside a string operand
    /// (<c>Invoke-BashEcho "a;b"</c>) or inside an already-wrapped child scope
    /// (<c>&amp; { a; b }</c>) — both of which ARE valid pipeline heads. That
    /// "quote-blind scan" is a recurring bug family in this transpiler, so this
    /// helper is the single place the rule is expressed.
    /// </para>
    /// </summary>
    public static bool IsStatementList(string psText)
    {
        int paren = 0, brace = 0, bracket = 0;
        for (int i = 0; i < psText.Length; i++)
        {
            char c = psText[i];
            switch (c)
            {
                case '\'':
                    // Single-quoted: literal to the next ' ('' is an escaped quote,
                    // which this loop handles naturally as close-then-reopen).
                    i++;
                    while (i < psText.Length && psText[i] != '\'') i++;
                    break;

                case '"':
                    // Double-quoted: backtick escapes, and `$( … )` subexpressions
                    // that may themselves contain quotes and semicolons.
                    i++;
                    for (int depth = 0; i < psText.Length; i++)
                    {
                        if (psText[i] == '`') { i++; continue; }
                        if (psText[i] == '$' && i + 1 < psText.Length && psText[i + 1] == '(')
                        { depth++; i++; continue; }
                        if (psText[i] == ')' && depth > 0) { depth--; continue; }
                        if (psText[i] == '"' && depth == 0) break;
                    }
                    break;

                case '`': i++; break;               // escape: skip the escaped char
                case '(': paren++; break;
                case ')': if (paren > 0) paren--; break;
                case '{': brace++; break;
                case '}': if (brace > 0) brace--; break;
                case '[': bracket++; break;
                case ']': if (bracket > 0) bracket--; break;

                case ';':
                    if (paren == 0 && brace == 0 && bracket == 0)
                        return true;
                    break;
            }
        }
        return false;
    }

    /// <summary>
    /// The bash file-comparison test operators <c>-nt</c> / <c>-ot</c> / <c>-ef</c>
    /// as a PowerShell boolean expression. These were not implemented at all: the
    /// emitter fell through to joining the operands with spaces, producing the
    /// never-valid <c>$env:a -ef $env:b</c> (Go's etetest.sh).
    /// <para>
    /// Missing-operand semantics follow bash exactly (verified against the oracle):
    /// <c>a -nt b</c> is true when a exists and b does not; <c>a -ot b</c> is true
    /// when b exists and a does not; <c>a -ef b</c> is false unless BOTH exist.
    /// </para>
    /// <para>
    /// <c>-ef</c> is "same file". bash compares device+inode; the portable .NET
    /// equivalent is a resolved-path compare, so a symlink and its target match
    /// (<c>ResolvedTarget</c>, null for a non-link, falls back to
    /// <c>FullName</c>). Hard links to the same inode are NOT detected — a
    /// documented approximation, not a silent wrong answer for the common case.
    /// </para>
    /// </summary>
    public static string FileComparisonTest(string lhs, string rhs, string op)
    {
        const string a = "$__psbash_ftA";
        const string b = "$__psbash_ftB";
        string probe =
            a + " = Get-Item -LiteralPath " + lhs + " -Force -ErrorAction SilentlyContinue; " +
            b + " = Get-Item -LiteralPath " + rhs + " -Force -ErrorAction SilentlyContinue; ";

        string body = op switch
        {
            "-nt" => "if ($null -eq " + a + ") { $false } elseif ($null -eq " + b + ") { $true } " +
                     "else { " + a + ".LastWriteTimeUtc -gt " + b + ".LastWriteTimeUtc }",
            "-ot" => "if ($null -eq " + b + ") { $false } elseif ($null -eq " + a + ") { $true } " +
                     "else { " + a + ".LastWriteTimeUtc -lt " + b + ".LastWriteTimeUtc }",
            _     => "if ($null -eq " + a + " -or $null -eq " + b + ") { $false } " +
                     "else { (" + a + ".ResolvedTarget ?? " + a + ".FullName) -eq " +
                     "(" + b + ".ResolvedTarget ?? " + b + ".FullName) }",
        };

        // `$( & { … } )`: the probe is a statement LIST, so it needs a subexpression,
        // and the temp names stay inside the child scope (a top-level assignment
        // would leak into the runspace and shadow a transpiled bash variable).
        return "$(& { " + probe + body + " })";
    }

    // ───────────────────────────────── Exit-code tests ────────────────────────────────

    /// <summary>
    /// The test for a condition that RAN AS A STATEMENT just before it (see
    /// <see cref="HoistedCondition"/>): <c>$global:LASTEXITCODE -eq 0</c> (<c>-ne</c> when negated).
    /// </summary>
    public static string LastStatusTest(bool negate = false) =>
        "$global:LASTEXITCODE " + (negate ? "-ne" : "-eq") + " 0";

    /// <summary>Set bash's <c>$?</c> to a fixed status: <c>$global:LASTEXITCODE = N</c>.</summary>
    public static string SetStatus(int status) => "$global:LASTEXITCODE = " + status;

    /// <summary>
    /// An <c>if</c>/<c>while</c> condition that runs a command, hoisted to statement position:
    /// <c>condStatements; </c> — the caller then tests <see cref="LastStatusTest"/>. Unlike
    /// <see cref="ExitCodeTest"/>, whose expression position forces <c>[void]</c>, the command's
    /// OUTPUT streams like any statement's (bash: <c>if echo in; then …</c> prints <c>in</c>).
    /// </summary>
    public static string HoistedCondition(string condStatements) => condStatements + "; ";

    /// <summary>
    /// A boolean expression that runs <paramref name="emittedCmd"/> and tests its EXIT
    /// CODE (bash semantics), suitable inside <c>if (...)</c>/<c>while (...)</c>:
    /// <c>(&amp; { [void](cmd); $global:LASTEXITCODE -eq 0 })</c>. The <see cref="Void"/>
    /// is NOT optional — without it the scriptblock returns the command's output objects
    /// alongside the boolean, and PowerShell evaluates the resulting multi-element array
    /// as truthy, silently inverting the condition.
    /// </summary>
    /// <param name="emittedCmd">The already-emitted PowerShell command/pipeline text to test.</param>
    /// <param name="negate">
    /// <c>false</c> → test success (<c>-eq 0</c>); <c>true</c> → test failure
    /// (<c>-ne 0</c>), i.e. bash <c>! cmd</c> which succeeds when <paramref name="emittedCmd"/> fails.
    /// </param>
    public static string ExitCodeTest(string emittedCmd, bool negate = false) =>
        // Suppress the command's output via VoidStatement, which picks [void]$(...) over
        // [void](...) when the emitted text is a statement LIST (contains "; "). A grouping
        // paren cannot hold `stmt1; stmt2` ("Missing closing ')'"), so a multi-statement
        // command like `cd DIR` (which emits an if/else block) would produce unparseable
        // PowerShell inside `if cd DIR; then …`. The subexpression form survives it.
        "(& { " + VoidStatement(emittedCmd) + "; $global:LASTEXITCODE " + (negate ? "-ne" : "-eq") + " 0 })";

    // ──────────────────────── Exit-code propagation (&& / || chains) ───────────────────

    /// <summary>
    /// Drive <c>$global:LASTEXITCODE</c> AND <c>$?</c> from a boolean, for use as an
    /// operand of PowerShell's pipeline-chain operators (which check <c>$?</c>, not the
    /// exit code): <c>$(if (boolExpr) { $global:LASTEXITCODE = 0 } else {
    /// $global:LASTEXITCODE = 1; Write-Error '' -ErrorAction SilentlyContinue })</c>.
    /// The empty <c>Write-Error</c> flips <c>$?</c> to false so a following <c>||</c>
    /// fires. <paramref name="boolExpr"/> must already be a parenthesized PS boolean.
    /// </summary>
    public static string SetExitFromBool(string boolExpr) =>
        "$(if (" + boolExpr + ") { $global:LASTEXITCODE = 0 } "
        + "else { $global:LASTEXITCODE = 1; Write-Error '' -ErrorAction SilentlyContinue })";

    /// <summary>
    /// Bridge a plain pipeline's <c>$global:LASTEXITCODE</c> to <c>$?</c> WITHOUT
    /// re-running or suppressing it: emit the pipeline as a statement, then append this
    /// <c>$(if ($global:LASTEXITCODE -ne 0) { Write-Error '' -ErrorAction SilentlyContinue })</c>
    /// as the chain-operator operand. Unlike <see cref="SetExitFromBool"/> this keeps
    /// the pipeline's real stdout flowing (it is the command's output in the chain).
    /// </summary>
    public static string SignalFailIfNonZero() =>
        "$(if ($global:LASTEXITCODE -ne 0) { Write-Error '' -ErrorAction SilentlyContinue })";

    /// <summary>
    /// <see cref="SignalFailIfNonZero"/> for an operand whose failure may show in EITHER signal:
    /// a cmdlet error clears <c>$?</c> (still the operand's at the start of this subexpression),
    /// while a bash function's <c>return 3</c> or a subshell's <c>exit 4</c> sets only the exit
    /// code and leaves <c>$?</c> true. Fails the chain when either says so.
    /// </summary>
    public static string SignalFailIfFailed() =>
        "$(if (-not $? -or $global:LASTEXITCODE -ne 0) { Write-Error '' -ErrorAction SilentlyContinue })";

    /// <summary>
    /// A standalone <c>[ ... ]</c> / <c>[[ ... ]]</c> test as its OWN statement: bash
    /// is silent and sets only the exit code. Emit a form that sets
    /// <c>$global:LASTEXITCODE</c> and produces no stdout:
    /// <c>$(if (boolExpr) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1 })</c>.
    /// (No <c>Write-Error</c> — a bare test outside a chain does not signal <c>$?</c>.)
    /// </summary>
    public static string SilentExitFromBool(string boolExpr) =>
        "$(if (" + boolExpr + ") { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1 })";

    // ─────────────────────────────── errexit (set -e) ───────────────────────────────

    /// <summary>
    /// The runtime counter of enclosing errexit-EXEMPT contexts (an <c>if</c>/<c>while</c>
    /// condition, a non-final <c>&amp;&amp;</c>/<c>||</c> operand, <c>!</c>, a command
    /// substitution). bash ignores <c>-e</c> for everything run inside one — including the
    /// body of a FUNCTION called there, which only a runtime counter can see. Initialised
    /// per command by the host and by <c>set -e</c>.
    /// </summary>
    public const string ErrexitSuppressVar = "$global:__BashErrexitSuppress";

    /// <summary>Whether errexit fires right now: <c>set -e</c> is on and no exempt context is active.</summary>
    private const string ErrexitArmed = "$global:__BashErrexit -and -not " + ErrexitSuppressVar;

    /// <summary>
    /// After a command at statement position under <c>set -e</c>: leave when it failed.
    /// <paramref name="exitStatement"/> is how to leave from here — a real <c>exit</c>, or a
    /// scoped <c>return</c> inside a subshell. <paramref name="status"/> is the status to test.
    /// </summary>
    public static string ErrexitCheck(string exitStatement, string status = "$global:LASTEXITCODE") =>
        "if (" + ErrexitArmed + " -and " + status + " -ne 0) { " + exitStatement + " }";

    /// <summary>
    /// Run statement(s) with errexit suppressed (an exempt context): no command inside —
    /// nor any function it calls — may end the script for failing.
    /// </summary>
    public static string ErrexitSuppressed(string statements) =>
        ErrexitSuppressVar + "++; try { " + statements + " } finally { " + ErrexitSuppressVar + "-- }";

    /// <summary>
    /// <see cref="ErrexitSuppressed"/> for a boolean CONDITION expression (<c>if</c>/<c>while</c>):
    /// a script block, so it stays an expression whose value is the condition's.
    /// </summary>
    public static string ErrexitSuppressedExpr(string condition) =>
        "(& { " + ErrexitSuppressed(condition) + " })";

    /// <summary>
    /// The FINAL operand of an <c>&amp;&amp;</c>/<c>||</c> list at statement position: it is NOT
    /// exempt (bash exits when it runs and fails), so it lifts one level of the list's
    /// suppression, and records its status in <c>$global:__BashErrexitTail</c> — the list's
    /// own status cannot tell "the last command failed" from "an earlier one failed and
    /// short-circuited it". Dot-sourced: current scope, streams, and is a valid chain operand.
    /// </summary>
    public static string ErrexitFinalOperand(string operand) =>
        ". { " + ErrexitSuppressVar + "--; try { " + operand + " } finally { " + ErrexitSuppressVar
        + "++ }; $global:__BashErrexitTail = $global:LASTEXITCODE }";

    /// <summary>
    /// An <c>&amp;&amp;</c>/<c>||</c> list at statement position under <c>set -e</c>: every operand
    /// runs suppressed except the final one (<see cref="ErrexitFinalOperand"/>), then the
    /// script leaves only if that final command ran and failed.
    /// </summary>
    public static string ErrexitAndOrList(string chain, string exitStatement) =>
        "$global:__BashErrexitTail = 0; " + ErrexitSuppressed(chain) + "; "
        + ErrexitCheck(exitStatement, "$global:__BashErrexitTail");

    // ────────────────────────── Subshell working-directory restore ─────────────────────

    /// <summary>
    /// The <c>finally</c> body of a subshell wrapper: pop the PowerShell location AND put
    /// the process working directory back with it.
    ///
    /// <para><b>Why a bare <c>Pop-Location</c> is a correctness bug, not a style nit.</b>
    /// A bash working directory is BOTH halves — the PowerShell location (what
    /// <c>SessionState.Path</c>-based cmdlets resolve against) and
    /// <c>[System.Environment]::CurrentDirectory</c> (what every raw .NET path API
    /// resolves against, including the fused streaming lane's <c>CatFileStage</c>).
    /// <c>EmitCd</c> writes both, and so do <c>pushd</c>/<c>popd</c>; a bare
    /// <c>Pop-Location</c> restored only the first, so after <c>(cd sub)</c> the process
    /// cwd was still pointing INSIDE <c>sub</c>. <c>(cd sub); cat data.txt | …</c> then
    /// streamed <c>sub/data.txt</c> at exit 0 while the unfused pipeline read the outer
    /// one — wrong file, no error anywhere.</para>
    ///
    /// <para><b>Nesting.</b> Subshells nest and so does this wrapper, but the temp is
    /// assigned and consumed within one straight-line statement sequence with no nested
    /// emission in between: an inner subshell's <c>finally</c> has fully run before an
    /// outer one starts, so the levels cannot interleave on the name. It is namespaced
    /// <c>$__psbash*</c> regardless, per the emitter-temp convention.</para>
    ///
    /// <para><c>-PassThru</c> output is CAPTURED into the temp rather than left on the
    /// pipeline — the <c>finally</c> sits inside the subshell's own output scope, so an
    /// uncaptured <c>PathInfo</c> would be emitted as subshell stdout.
    /// <c>-ErrorAction SilentlyContinue</c> keeps an empty stack (only reachable if
    /// <c>Push-Location</c> itself failed) from throwing out of a <c>finally</c> under
    /// <c>set -e</c>, and <c>Directory.Exists</c> guards a non-filesystem provider whose
    /// <c>ProviderPath</c> is not a process working directory.</para>
    /// </summary>
    public const string PopLocationRestoringProcessCwd =
        "$__psbash_subshell_pop = Pop-Location -PassThru -ErrorAction SilentlyContinue; "
        + "if ($__psbash_subshell_pop -and "
        + "[System.IO.Directory]::Exists($__psbash_subshell_pop.ProviderPath)) { "
        + "[System.Environment]::CurrentDirectory = $__psbash_subshell_pop.ProviderPath; "
        + "$global:__PsBashCwd = $__psbash_subshell_pop.ProviderPath; "
        + "$env:PWD = $__psbash_subshell_pop.ProviderPath }";

    // ─────────────────────────── RC-7 unquoted word-split splat ────────────────────────

    /// <summary>
    /// The array an unquoted ordinary <c>$var</c> operand expands into, for <c>@</c>-splatting:
    /// <c>@(ConvertTo-BashWords varRef)</c> — bash's word splitting on <c>$IFS</c> (leading / trailing IFS
    /// whitespace discarded, an empty or blank value gives NO word) followed by pathname expansion of every
    /// word that has a glob character. The OUTER <c>@(...)</c> is required: an empty result must stay an
    /// empty array that splats to nothing (a bare <c>$null</c> would inject one spurious empty argument).
    /// </summary>
    public static string WordSplitArray(string varRef) =>
        "@(ConvertTo-BashWords " + varRef + ")";

    /// <summary>
    /// The array an unquoted GLOB word (<c>*.txt</c>, <c>src/*</c>, <c>$d/[ab]*</c>) pathname-expands into:
    /// <c>@(ConvertTo-BashGlob &lt;pattern&gt; [-ForCmdlet])</c>. <paramref name="patternExpr"/> is a PowerShell
    /// expression for the pattern text in bash's dialect (quoted glob characters already backslash-escaped, see
    /// <c>PsEmitter.EmitGlobPatternExpr</c>). The outer <c>@(...)</c> keeps an empty result (nullglob) an
    /// empty array that splats to nothing.
    /// </summary>
    public static string GlobWordArray(string patternExpr) =>
        "@(ConvertTo-BashGlob " + patternExpr + ")";

    // ───────────────────────── Stdout to stderr (`>&2`) ──────────────────────────

    /// <summary>
    /// The tail stage that sends a command's stdout to bash's stderr (<c>cmd &gt;&amp;2</c>): each
    /// record becomes an error-stream record (<c>Write-BashHostStderr</c>), so a LATER <c>2&gt;</c>
    /// on an enclosing command, <c>2&gt;&amp;1</c>, or <c>2&gt;/dev/null</c> applies to it as bash
    /// expects, while <c>$?</c> and the exit status stay the command's own.
    /// </summary>
    public const string StdoutToStderrStage = " | Write-BashHostStderr";

    /// <summary>
    /// A bare <c>2&gt;&amp;1</c> (stderr to the terminal's stdout): PowerShell's merge plus a stage
    /// that turns each merged error record into a stdout text record. Left as ErrorRecords, an
    /// enclosing <c>2&gt; f</c> stage or a capture would read them as stderr again —
    /// <c>{ echo e &gt;&amp;2; } 2&gt;&amp;1</c> under an outer <c>2&gt; f</c> wrote <c>e</c> to f.
    /// </summary>
    public const string MergeStderrIntoStdout = " 2>&1 | Invoke-BashRedirect -MergeErrors";

    // ───────────────────────── Redirect target without output ──────────────────────────

    /// <summary>
    /// Opens a <c>&gt; file</c> / <c>&gt;&gt; file</c> target for a command that writes NOTHING (<c>:</c>,
    /// <c>true</c>, <c>false</c>, a bare <c>&gt; f</c>): <c>@() | Invoke-BashRedirect -Path f [-Append]</c>.
    /// The cmdlet opens its target in <c>BeginProcessing</c> (truncate, or create for <c>-Append</c>)
    /// whether or not a record arrives, which is exactly bash's "a redirection creates the file".
    /// </summary>
    public static string TouchRedirectTarget(string target, bool append) =>
        "@() | Invoke-BashRedirect -Path " + target + (append ? " -Append" : "");

    /// <summary>
    /// The <c> | Invoke-BashRedirect …</c> stage that applies a command's final stdout/stderr
    /// destinations (see <c>PsEmitter.AppendRedirectTail</c>). Targets are emitted ARGUMENT-mode
    /// words (<c>'f'</c>, <c>$env:d/f</c>, <c>$null</c>); <paramref name="stdoutPath"/> null = stdout
    /// passes through, <c>"$null"</c> = discarded. <paramref name="truncate"/> / <paramref name="touch"/>
    /// are superseded targets, passed through <see cref="ArgWordArray"/> so each stays an argument.
    /// </summary>
    public static string RedirectStage(
        string? stdoutPath, bool append, string? errorPath, bool errorAppend, bool passErrors,
        IReadOnlyList<string> truncate, IReadOnlyList<string> touch)
    {
        var sb = new System.Text.StringBuilder(" | Invoke-BashRedirect");
        if (stdoutPath is not null)
        {
            sb.Append(" -Path ").Append(stdoutPath);
            if (append) sb.Append(" -Append");
        }
        if (errorPath is not null)
        {
            sb.Append(" -ErrorPath ").Append(errorPath);
            if (errorAppend) sb.Append(" -ErrorAppend");
        }
        if (passErrors) sb.Append(" -PassErrors");
        if (truncate.Count > 0) sb.Append(" -Truncate ").Append(ArgWordArray(truncate));
        if (touch.Count > 0) sb.Append(" -Touch ").Append(ArgWordArray(touch));
        return sb.ToString();
    }

    /// <summary>
    /// An array of ARGUMENT-mode words as one argument: <c>@(&amp; { $args } w1 w2)</c>. The words are
    /// arguments to a script block, never spliced into an expression (where <c>/</c> divides and a
    /// bare word is a command).
    /// </summary>
    public static string ArgWordArray(IReadOnlyList<string> argWords) =>
        "@(& { $args } " + string.Join(' ', argWords) + ")";

    // ─────────────────────── Null-safe pipeline text extraction ────────────────────────

    /// <summary>
    /// A <c>ForEach-Object</c> body that extracts a pipeline object's <c>BashText</c>
    /// (else stringifies it), null-safe. The <c>$null -ne $_</c> guard is load-bearing:
    /// <c>$_.PSObject.Properties['BashText']</c> throws "Cannot index into a null array"
    /// on a <c>$null</c> item, and short-circuit order means the guard MUST precede the
    /// property probe. Used to drain <c>while read</c> input.
    /// </summary>
    public const string NullSafeBashText =
        "if ($null -ne $_ -and $_.PSObject.Properties['BashText']) { $_.BashText } else { \"$_\" }";

    // ───────────────────────────── Bash function definitions ─────────────────────────────

    /// <summary>
    /// The text every bash function body starts with (<c>function f {</c> + this + body + <see cref="FunctionEpilogue"/>
    /// <c>}</c>): save/restore of <c>$global:BashPositional</c> so a recursive call sees its own <c>$1 $@ $#</c>. Also
    /// the signature <c>declare -f</c>/<c>-F</c> use to tell a bash-defined function from the runtime's own.
    /// </summary>
    public const string FunctionPrologue = " $__bp = $global:BashPositional; $global:BashPositional = @() + $args; try { ";

    /// <summary>The text every bash function body ends with (see <see cref="FunctionPrologue"/>).</summary>
    public const string FunctionEpilogue = " } finally { $global:BashPositional = $__bp } ";

    // ───────────────────── Compound-command stdin (shared cursor) ──────────────────────

    /// <summary>
    /// A pipeline source that yields what is LEFT of the compound command's shared stdin
    /// (<c>$global:__BashStdIn</c>, a <c>Queue[object]</c> of the original records), one record at a
    /// time. Prepended (<c>feed | cmd</c>) to a stdin-reading command inside a stdin scope. Lazy:
    /// a consumer that stops the pipeline early (<c>head</c>) leaves the rest queued, and
    /// <c>read</c> / a later command advance the same cursor. Empty queue = empty input (EOF).
    /// </summary>
    public const string StdinFeed =
        "& { while ($global:__BashStdIn -and $global:__BashStdIn.Count -gt 0) { $global:__BashStdIn.Dequeue() } }";

    /// <summary>
    /// Wrap <paramref name="body"/> so it runs with the incoming pipeline (<c>$input</c>) as ITS stdin:
    /// the previous queue is saved, a fresh one filled from <c>$input</c> (the records are kept as
    /// objects), and the previous queue is restored in a <c>finally</c> so a nested compound pipe stage
    /// never clobbers its parent's stdin. The result is a statement list for the inside of a
    /// <c>&amp; { … }</c> block — the block is what receives the pipeline.
    /// </summary>
    public static string StdinScope(string body) =>
        "$__psbash_stdin_prev = Get-Variable -Name __BashStdIn -Scope Global -ValueOnly -ErrorAction SilentlyContinue; "
        + "$global:__BashStdIn = [System.Collections.Generic.Queue[object]]::new(); "
        + "foreach ($__psbash_stdin_line in $input) { $global:__BashStdIn.Enqueue($__psbash_stdin_line) }; "
        + "try { " + body + " } finally { $global:__BashStdIn = $__psbash_stdin_prev }";

    /// <summary>
    /// Like <see cref="StdinScope"/> but the stdin is a lazily-pulled background PRODUCER
    /// (<c>New-BashLazyStdin</c>) instead of the incoming pipeline: for an unbounded producer
    /// (<c>yes | { head -n1; }</c>) that a pipe could never hand over (the stage after a pipe starts only
    /// when its upstream has finished). The scope's <c>finally</c> closes the cursor, which stops the producer.
    /// A statement list: callers wrap it in <c>&amp; { … }</c>.
    /// </summary>
    public static string StdinScopeLazy(string producerCommand, string body) =>
        "$__psbash_stdin_prev = Get-Variable -Name __BashStdIn -Scope Global -ValueOnly -ErrorAction SilentlyContinue; "
        + "$global:__BashStdIn = New-BashLazyStdin " + SingleQuote(producerCommand) + "; "
        + "try { " + body + " } finally { try { $global:__BashStdIn.Close() } catch { }; $global:__BashStdIn = $__psbash_stdin_prev }";

    /// <summary>
    /// <c>cmd &lt; /dev/null</c>: run <paramref name="body"/> with an EMPTY stdin (end of input at once) instead
    /// of whatever stdin surrounds it — under the launcher's forwarded stdin "the surrounding stdin" is a live
    /// pipe, so simply dropping the redirect let <c>read</c>, <c>eval</c>'s commands and natives consume it.
    /// Dot-sourced so the command's side effects (<c>read</c>'s variable, <c>eval</c>'s functions) stay in
    /// the caller's scope; <paramref name="depth"/> makes the save variable unique per lexical nesting level
    /// (a dot-sourced block shares its caller's scope, so an inner scope must not overwrite the outer save).
    /// </summary>
    public static string EmptyStdinScope(string body, int depth)
    {
        var prev = "$__psbash_nullin_prev" + depth.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ". { " + prev + " = Get-Variable -Name __BashStdIn -Scope Global -ValueOnly -ErrorAction SilentlyContinue; "
            + "$global:__BashStdIn = [System.Collections.Generic.Queue[object]]::new(); "
            + "try { " + body + " } finally { $global:__BashStdIn = " + prev + " } }";
    }

    /// <summary>
    /// Give a statement-list command (a builtin the emitter expands inline: <c>cd</c>, <c>eval</c>,
    /// <c>source</c>, <c>read</c>, …) its redirect tail: <c>. { statements } 2&gt;$null</c>. Dot-sourced, not
    /// <c>&amp; { }</c>, so the builtin's side effects (variables, functions, cwd) land in the caller's scope;
    /// a bare tail after a statement list would bind only to its LAST statement.
    /// </summary>
    public static string WithRedirectTail(string statements, Action<System.Text.StringBuilder> appendTail)
    {
        var sb = new System.Text.StringBuilder(". { ").Append(statements).Append(" }");
        appendTail(sb);
        return sb.ToString();
    }

    /// <summary>
    /// A native program run with the surrounding shared stdin (<c>$global:__BashStdIn</c>) as its REAL
    /// process stdin. <c>Enter-BashNativeStdin</c> hands the program an OS pipe that a background pump fills
    /// from the shared stdin; <c>Exit-BashNativeStdin</c> (after the program exits) stops the pump and puts
    /// back everything the program did not read. Unlike piping the records into the program
    /// (<c>feed | prog</c>), the program starts at once and the command ends when the PROGRAM ends — a
    /// program that never reads stdin (<c>git --version</c>) is not held hostage by a stdin that never ends.
    /// <paramref name="name"/> is the literal command word: the pipe is only installed when it resolves to an
    /// application (a function or alias of that name runs as before).
    /// </summary>
    public static string NativeStdinScope(string name, string command) =>
        "& { $__psbash_native_stdin = Enter-BashNativeStdin " + SingleQuote(name) + "; try { " + command
        + " } finally { Exit-BashNativeStdin $__psbash_native_stdin } }";

    // ───────────────────────── Positional-parameter expansion ──────────────────────────

    /// <summary>
    /// The PowerShell subexpression for a bash positional-parameter expansion:
    /// <c>$@</c>/<c>$*</c> (the whole list), <c>$#</c> (the count), or a 1-based
    /// positional index (<c>$1</c>..<c>$9</c>, <c>${10}</c>, …). Every site prefers
    /// <c>$global:BashPositional</c> — set inside function bodies (see
    /// <c>EmitFunction</c> save/restore) and by <c>set --</c> — falling back to the
    /// script's own <c>$args</c> at top level. This is the ONE source for that
    /// preference-fallback shape; it used to be hand-copied at every positional call
    /// site and had to change in lockstep.
    /// </summary>
    /// <param name="sigil"><c>"@"</c>, <c>"*"</c>, <c>"#"</c>, or a base-10 positional index string.</param>
    public static string BuildPositionalExpansion(string sigil) =>
        sigil switch
        {
            "@" or "*" => "$(if ($global:BashPositional) { $global:BashPositional } else { $args })",
            "#" => "$(if ($global:BashPositional) { $global:BashPositional.Count } else { $args.Count })",
            _ when int.TryParse(sigil, out int oneBased) =>
                "$(if ($global:BashPositional) { $global:BashPositional[" + (oneBased - 1) + "] } "
                + "else { $args[" + (oneBased - 1) + "] })",
            _ => throw new System.ArgumentException($"Not a positional sigil: '{sigil}'", nameof(sigil)),
        };

    // ───────────────────────────── Special-variable mapping ────────────────────────────

    /// <summary>
    /// The single source of truth for how a bash special variable (<c>$?</c>, <c>$$</c>,
    /// <c>$RANDOM</c>, positional params, …) maps to PowerShell. Returns <c>null</c> when
    /// <paramref name="name"/> is not a recognized special variable OR when the braced
    /// (<c>${name}</c>) form of a recognized special variable has no dedicated mapping —
    /// in both cases the caller falls back to its own plain/braced <c>$env:</c> reference.
    /// <para>
    /// <paramref name="braced"/> selects between the plain-<c>$name</c> emission (used for
    /// a bare <c>$name</c> word) and the brace-quoted <c>${name}</c> emission (used inside
    /// double quotes when the following character would otherwise be misparsed, e.g.
    /// <c>"$x:suffix"</c>). The two forms are NOT symmetric today — several special names
    /// (<c>PWD</c>, <c>RANDOM</c>, <c>SECONDS</c>, <c>PPID</c>, <c>BASH_VERSION</c>,
    /// <c>BASH_VERSINFO</c>, and multi-digit positionals) only have a plain mapping; a
    /// braced reference to one of them falls through to <c>${env:name}</c>. This mirrors
    /// the pre-existing (and pre-existing-buggy) behavior of the two call sites this
    /// method replaces byte-for-byte — it is not a design choice made here.
    /// </para>
    /// <para>
    /// <paramref name="inDoubleQuote"/> only affects <c>$0</c>: inside double quotes (or
    /// always, for the braced form) it must be wrapped as <c>$(...)</c> so PowerShell's
    /// string interpolation invokes the property access rather than treating
    /// <c>$MyInvocation.MyCommand.Name</c> literally.
    /// </para>
    /// </summary>
    public static string? TryMapSpecialVar(string name, bool braced, bool inDoubleQuote)
    {
        switch (name)
        {
            case "null":
            case "true":
            case "false":
            case "HOME":
            case "LASTEXITCODE":
                return braced ? "${" + name + "}" : "$" + name;
            case "PWD":
                return braced ? null : "$" + name;
            case "?":
                return braced ? "${global:LASTEXITCODE}" : "$global:LASTEXITCODE";
            case "RANDOM":
                return braced ? null : "$(Get-Random -Maximum 32768)";
            case "@":
            case "*":
            case "#":
                return BuildPositionalExpansion(name);
            case "0":
                // $0 is the name the launcher was given (`-c CMD NAME`, or the script path as typed;
                // $global:BashPositional0), default `bash`. $MyInvocation.MyCommand.Name was the
                // CALLING function/script's command name: empty at top level, the function's name in
                // a function. Always a $(...) subexpression, so quoted and bare uses share one text.
                return "$(if ($global:BashPositional0) { $global:BashPositional0 } else { 'bash' })";
            case "$":
                return braced ? "${PID}" : "$PID";
            case "!":
                return braced ? "${global:BashBgLastPid}" : "$global:BashBgLastPid";
            case "-":
                return braced ? "${global:BashFlags}" : "$global:BashFlags";
            case "_":
                return braced ? "${global:BashLastArg}" : "$global:BashLastArg";
            case "SECONDS":
                return braced ? null : "$([math]::Floor(([DateTime]::UtcNow - $global:BashStartTime).TotalSeconds))";
            case "PPID":
                return braced ? null : "(Get-Process -Id $PID -ErrorAction SilentlyContinue).Parent.Id";
            case "BASH_VERSION":
                return braced ? null : "$global:BashVersion";
            case "BASH_VERSINFO":
                return braced ? null : "$global:BashVersionInfo";
            default:
                // Single-digit positional ($1..$9): identical in both plain and braced form.
                if (name.Length == 1 && name[0] is >= '1' and <= '9')
                    return BuildPositionalExpansion(name);

                // Multi-digit positional (${10}, ${11}, …) only arises from the braced form
                // — a bare $10 lexes as $1 followed by literal "0" (see BashParser.ParseSimpleVar)
                // — and the plain-form call site is the only one that ever handles it. A bash
                // variable name can never be all-digits, so an all-digit name here is
                // unambiguously a positional index. An index beyond int range (e.g. a
                // 10-billion-digit index) is an unset parameter -> empty string in bash.
                if (!braced && name.Length >= 2 && IsAllDigits(name))
                    return int.TryParse(name, out _) ? BuildPositionalExpansion(name) : "''";

                return null;
        }
    }

    private static bool IsAllDigits(string s)
    {
        foreach (char c in s)
        {
            if (c is < '0' or > '9')
                return false;
        }
        return true;
    }
}
