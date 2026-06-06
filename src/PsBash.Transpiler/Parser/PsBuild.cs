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
    // ─────────────────────────────── Quoting / escaping ───────────────────────────────

    /// <summary>
    /// Wrap <paramref name="value"/> in a PowerShell single-quoted literal, escaping
    /// embedded single quotes by doubling (<c>'</c> → <c>''</c>) — the only escape a
    /// PS single-quoted string honors. Use for literal paths, positional args, and any
    /// value that must reach PowerShell verbatim with no expansion.
    /// </summary>
    public static string SingleQuote(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>
    /// Escape <paramref name="value"/> for placement INSIDE a PowerShell double-quoted
    /// string (returns the inner text, no surrounding quotes). Order is load-bearing:
    /// backtick first (it is PowerShell's escape char, introduced by the next two
    /// replacements), then <c>$</c> (starts variable expansion), then <c>"</c> (ends
    /// the string).
    /// </summary>
    public static string EscapeForDoubleQuote(string value) =>
        value.Replace("`", "``").Replace("$", "`$").Replace("\"", "`\"");

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

    // ───────────────────────────────── Exit-code tests ────────────────────────────────

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
        "(& { " + Void(emittedCmd) + "; $global:LASTEXITCODE " + (negate ? "-ne" : "-eq") + " 0 })";

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
    /// A standalone <c>[ ... ]</c> / <c>[[ ... ]]</c> test as its OWN statement: bash
    /// is silent and sets only the exit code. Emit a form that sets
    /// <c>$global:LASTEXITCODE</c> and produces no stdout:
    /// <c>$(if (boolExpr) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1 })</c>.
    /// (No <c>Write-Error</c> — a bare test outside a chain does not signal <c>$?</c>.)
    /// </summary>
    public static string SilentExitFromBool(string boolExpr) =>
        "$(if (" + boolExpr + ") { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1 })";

    // ─────────────────────────── RC-7 unquoted word-split splat ────────────────────────

    /// <summary>
    /// The array an unquoted ordinary <c>$var</c> operand word-splits into, for
    /// <c>@</c>-splatting: <c>@(if ([string]::IsNullOrEmpty(varRef)) { @() } else {
    /// @(varRef -split '\s+') })</c>. The OUTER <c>@(...)</c> is required — assigning a
    /// bare <c>if (...) { @() }</c> collapses the empty branch to <c>$null</c>, and
    /// splatting <c>$null</c> injects one spurious empty argument; <c>@(...)</c> forces
    /// array context so the empty branch stays an empty array that splats to nothing.
    /// </summary>
    public static string WordSplitArray(string varRef) =>
        "@(if ([string]::IsNullOrEmpty(" + varRef + ")) { @() } "
        + "else { @(" + varRef + " -split '\\s+') })";

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
}
