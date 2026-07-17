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
    /// @(varRef -split '\s+' | Where-Object { $_ -ne '' }) })</c>. The OUTER
    /// <c>@(...)</c> is required — assigning a bare <c>if (...) { @() }</c> collapses
    /// the empty branch to <c>$null</c>, and splatting <c>$null</c> injects one
    /// spurious empty argument; <c>@(...)</c> forces array context so the empty branch
    /// stays an empty array that splats to nothing.
    /// <para>The <c>Where-Object { $_ -ne '' }</c> is load-bearing: PowerShell's
    /// <c>-split '\s+'</c> yields a LEADING empty field when the value has leading
    /// whitespace (<c>"  a b" -split</c> → <c>['', 'a', 'b']</c>) and a trailing empty
    /// for trailing whitespace, but bash IFS word-splitting discards both (<c>set -- $x</c>
    /// on <c>"  a b"</c> gives 2 params, not 3). Filtering empties matches bash — and
    /// also makes a whitespace-only value split to nothing.</para>
    /// </summary>
    public static string WordSplitArray(string varRef) =>
        "@(if ([string]::IsNullOrEmpty(" + varRef + ")) { @() } "
        + "else { @(" + varRef + " -split '\\s+' | Where-Object { $_ -ne '' }) })";

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
                return (inDoubleQuote || braced) ? "$($MyInvocation.MyCommand.Name)" : "$MyInvocation.MyCommand.Name";
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
