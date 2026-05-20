using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle tests for simple commands and quoting (Dart DOa4DSJxrFne).
///
/// Covers:
///   - Single-quote literals (no expansion, space preservation)
///   - Double-quote with $var expansion
///   - Backslash escapes inside and outside double quotes
///   - $'...' ANSI-C quoting
///   - Word splitting on unquoted variables containing spaces/IFS
///   - Empty string handling (quoted empty, unquoted empty)
///   - Env-var prefix (FOO=bar cmd) — does not leak to environment
///   - Multiple words concatenated with adjacent quoting styles
///
/// Each test runs the script through real bash AND ps-bash and diffs bytes
/// (Directive 1 oracle-first). Tests skip when no bash oracle is available.
///
/// Failure-surface axes targeted (Directive 3):
///   Axis 11: environment leak — VAR=x cmd must not persist VAR
///   Axis 12: quoting / injection — unquoted var with spaces, IFS, $(...) in single-quotes
/// </summary>
public class QuotingDifferentialTests
{
    // -----------------------------------------------------------------------
    // Single-quote literals
    // -----------------------------------------------------------------------

    /// <summary>
    /// A single-quoted string containing a space is a single word; echo receives
    /// one argument and must not split it.
    /// Axis 12: quoting / injection.
    /// </summary>
    [SkippableFact]
    public async Task Differential_SingleQuote_SpacePreserved()
    {
        await AssertOracle.EqualAsync(
            "echo 'hello world'",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Dollar signs inside single quotes must NOT expand.
    /// Axis 12: injection — if the emitter loses quotes, $HOME would expand.
    /// </summary>
    [SkippableFact]
    public async Task Differential_SingleQuote_NoDollarExpansion()
    {
        await AssertOracle.EqualAsync(
            "echo '$HOME is not expanded'",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Backticks inside single quotes must NOT be executed.
    /// Axis 12: injection — if the emitter loses quotes, `date` would run.
    /// </summary>
    [SkippableFact]
    public async Task Differential_SingleQuote_NoBacktickExpansion()
    {
        await AssertOracle.EqualAsync(
            "echo 'no `date` here'",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Empty single-quoted string '' must produce an empty argument, not no argument.
    /// Axis 1 (empty input variant): empty quoted string handling.
    /// </summary>
    [SkippableFact]
    public async Task Differential_SingleQuote_EmptyString()
    {
        await AssertOracle.EqualAsync(
            "x=''; echo \">${x}<\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Double-quote with $var expansion
    // -----------------------------------------------------------------------

    /// <summary>
    /// $var inside double quotes must expand and the result must not be word-split.
    /// Axis 12: a value with spaces inside double quotes stays one word.
    /// </summary>
    [SkippableFact]
    public async Task Differential_DoubleQuote_VarExpansionNoSplit()
    {
        await AssertOracle.EqualAsync(
            "x=\"hello world\"; echo \"[$x]\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Multiple $var references inside one double-quoted string must all expand.
    /// </summary>
    [SkippableFact]
    public async Task Differential_DoubleQuote_MultipleVarRefs()
    {
        await AssertOracle.EqualAsync(
            "a=foo; b=bar; echo \"$a and $b\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Unquoted variable containing spaces must word-split into separate arguments.
    /// Axis 12: word splitting on unquoted var.
    /// bash: echo receives two args — "hello" and "world" — and prints them space-separated.
    /// ps-bash must do the same.
    /// </summary>
    [SkippableFact]
    public async Task Differential_UnquotedVar_WordSplitsOnSpaces()
    {
        await AssertOracle.EqualAsync(
            "x=\"hello world\"; echo $x",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Backslash escapes
    // -----------------------------------------------------------------------

    /// <summary>
    /// Backslash before $ inside double quotes suppresses expansion.
    /// bash: echo "price: \$5" outputs literal 'price: $5'.
    /// Fix: Literal("$") parts inside double-quoted strings are now emitted as `$
    /// (backtick-dollar) so PowerShell treats the dollar as a literal character.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Backslash_EscapesDollarInDoubleQuotes()
    {
        await AssertOracle.EqualAsync(
            "echo \"price: \\$5\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Backslash before a non-special char inside double quotes is literal
    /// (both backslash and the character appear in output).
    /// Per bash spec: backslash is only special before $ ` " \ and newline.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Backslash_NonSpecialCharIsLiteral()
    {
        await AssertOracle.EqualAsync(
            "echo \"hello\\nworld\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Backslash-n outside quotes is a literal two-character sequence, not a newline.
    /// (echo without -e does not interpret escape sequences.)
    /// </summary>
    [SkippableFact]
    public async Task Differential_Backslash_OutsideQuotes_Literal()
    {
        await AssertOracle.EqualAsync(
            "echo hello\\\\world",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // $'...' ANSI-C quoting
    // -----------------------------------------------------------------------

    /// <summary>
    /// $'\n' ANSI-C quoting produces a literal newline. Implemented via the
    /// lexer $'...' scan, parser WordPart.AnsiCQuoted, and emitter
    /// ExpandAnsiCEscapes (escapes folded to literal chars at transpile time).
    /// </summary>
    [SkippableFact]
    public async Task Differential_AnsiCQuote_Newline()
    {
        await AssertOracle.EqualAsync(
            "echo $'hello\\nworld'",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>$'\t' produces a literal tab.</summary>
    [SkippableFact]
    public async Task Differential_AnsiCQuote_Tab()
    {
        await AssertOracle.EqualAsync(
            "echo $'col1\\tcol2'",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>Hex escapes: $'\x41\x42' → AB.</summary>
    [SkippableFact]
    public async Task Differential_AnsiCQuote_Hex()
    {
        await AssertOracle.EqualAsync(
            "echo $'\\x41\\x42'",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>Octal escapes: $'\101\102' → AB.</summary>
    [SkippableFact]
    public async Task Differential_AnsiCQuote_Octal()
    {
        await AssertOracle.EqualAsync(
            "echo $'\\101\\102'",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Unicode escape (Axis 3): $'café' → café. Quarantined: the \uHHHH escape
    /// expands correctly (transpile yields `Invoke-BashEcho 'café'`, see
    /// PsEmitterTests.Transpile_AnsiCUnicodeEscape_*), but the ps-bash host emits
    /// non-ASCII stdout in the system code page on a non-UTF-8 console (CI
    /// runners) so the roundtrip mojibakes (café -> cafΘ). Pre-existing host
    /// encoding bug, Dart z0GXccJmhX2H. Re-enable once that lands.
    /// </summary>
    [SkippableFact]
    public async Task Differential_AnsiCQuote_Unicode()
    {
        Skip.If(true, "quarantine: non-ASCII stdout encoding on non-UTF-8 host (Dart z0GXccJmhX2H); " +
                      "\\u expansion itself is covered at transpile level in PsEmitterTests.");
        await AssertOracle.EqualAsync(
            "echo $'caf\\u00e9'",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>Escaped single quote inside $'...': $'it\'s' → it's.</summary>
    [SkippableFact]
    public async Task Differential_AnsiCQuote_EscapedQuote()
    {
        await AssertOracle.EqualAsync(
            "echo $'it\\'s here'",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>Unrecognized escape keeps backslash + char (bash behavior): $'a\zb' → a\zb.</summary>
    [SkippableFact]
    public async Task Differential_AnsiCQuote_UnknownEscapeKept()
    {
        await AssertOracle.EqualAsync(
            "echo $'a\\zb'",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>ANSI-C quote adjacent to a literal suffix joins into one word: pre$'\t'post.</summary>
    [SkippableFact]
    public async Task Differential_AnsiCQuote_AdjacentToLiteral()
    {
        await AssertOracle.EqualAsync(
            "echo pre$'\\t'post",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>ANSI-C quote leading (self-delimiting) with a literal suffix: $'\t'post → one word.</summary>
    [SkippableFact]
    public async Task Differential_AnsiCQuote_LeadingThenLiteral()
    {
        await AssertOracle.EqualAsync(
            "echo $'X\\tY'post",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Injection (Directive 12): a command-substitution-looking payload inside
    /// $'...' is data, not code. bash: echo $'$(echo PWN)' → $(echo PWN) literal.
    /// </summary>
    [SkippableFact]
    public async Task Differential_AnsiCQuote_InjectionStaysData()
    {
        await AssertOracle.EqualAsync(
            "echo $'$(echo PWN)'",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Empty string handling
    // -----------------------------------------------------------------------

    /// <summary>
    /// Unquoted empty variable contributes no word (the argument is omitted).
    /// Axis 1: empty input variant.
    /// bash: x=; echo start $x end → "start end" (one space, $x omitted).
    /// RC-7 fix: the emitter now routes a pure unquoted ordinary variable
    /// argument through a word-splitting splat that yields @() when the
    /// variable is empty, so PowerShell contributes no argument for $x.
    /// Oracle-first (Directive 1): diffs ps-bash bytes against real bash.
    /// </summary>
    [SkippableFact]
    public async Task Differential_EmptyVar_UnquotedIsOmitted()
    {
        await AssertOracle.EqualAsync(
            "x=; echo start $x end",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Quoted empty variable "$x" contributes an empty-string argument.
    /// bash: x=; echo \"$x\"  →  echo receives one empty-string arg →  blank line.
    /// The output must be identical to the unquoted case for echo, but the
    /// argument count differs; we verify echo output is the same blank line.
    /// </summary>
    [SkippableFact]
    public async Task Differential_EmptyVar_QuotedIsEmptyArg()
    {
        await AssertOracle.EqualAsync(
            "x=; echo \"[$x]\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Env-var prefix (FOO=bar cmd)
    // -----------------------------------------------------------------------

    /// <summary>
    /// VAR=value cmd sets VAR only for that command; it must NOT leak into the
    /// shell afterward (Axis 11). bash: FOO=bar echo hi; echo "after:[$FOO]"
    /// → "hi\nafter:[]\n". The emitter wraps the env-pair assignment in a
    /// try/finally that saves and restores $env:FOO, so the value does not
    /// persist past the command.
    /// </summary>
    [SkippableFact]
    public async Task Differential_EnvPrefix_DoesNotLeakToShell()
    {
        await AssertOracle.EqualAsync(
            "FOO=bar echo hi; echo \"after:[$FOO]\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Env-prefix with a pre-existing value restores the ORIGINAL value, not
    /// unset. bash: FOO=orig; FOO=temp echo hi; echo "[$FOO]" → "hi\n[orig]\n".
    /// </summary>
    [SkippableFact]
    public async Task Differential_EnvPrefix_RestoresPriorValue()
    {
        await AssertOracle.EqualAsync(
            "FOO=orig; FOO=temp echo hi; echo \"[$FOO]\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Documents the residual `printenv VAR` shape difference (separate from the
    /// env-leak above, which is fixed): the env cmdlet's one-name form emits
    /// "NAME=value" where bash `printenv VAR` emits just "value". The second
    /// line ("gone") confirms there is NO leak. Frozen via GoldenAsync until the
    /// printenv one-name form is fixed.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Printenv_OneNameShape_KnownGap()
    {
        Skip.If(true, "quarantine: documents the printenv NAME=value gap (Dart wpCPSd25qMuI); " +
                      "golden is host-env-sensitive and unreliable on CI runners. " +
                      "env-leak itself is covered by Differential_EnvPrefix_DoesNotLeakToShell.");
        // Directive 1 exception: known cmdlet shape gap — printenv VAR prints
        // "NAME=value" instead of "value". Tracked separately; not an env leak.
        await AssertOracle.GoldenAsync(
            "unset PSBASH_ENVPFX_TEST; PSBASH_ENVPFX_TEST=yes printenv PSBASH_ENVPFX_TEST; echo ${PSBASH_ENVPFX_TEST:-gone}",
            "Quoting_EnvPrefix_LeakBug",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Adjacent quoting styles (concatenation)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Adjacent single-quoted then double-quoted parts join into one word.
    /// bash: echo 'hello'"world" → "helloworld".
    /// Fixed by the EmitWord adjacency-flatten path (NeedsAdjacencyFlatten):
    /// a word whose first part is a self-delimiting token is emitted as one PS
    /// double-quoted string instead of split arguments.
    /// </summary>
    [SkippableFact]
    public async Task Differential_AdjacentQuotes_SingleThenDouble()
    {
        await AssertOracle.EqualAsync(
            "echo 'hello'\"world\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>Double-quoted then single-quoted parts join: echo "hello"'world' → helloworld.</summary>
    [SkippableFact]
    public async Task Differential_AdjacentQuotes_DoubleThenSingle()
    {
        await AssertOracle.EqualAsync(
            "echo \"hello\"'world'",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>Two adjacent single-quoted parts join: echo 'a''b' → ab (NOT a'b).</summary>
    [SkippableFact]
    public async Task Differential_AdjacentQuotes_SingleThenSingle()
    {
        await AssertOracle.EqualAsync(
            "echo 'a''b'",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>Two adjacent double-quoted parts join: echo "a""b" → ab.</summary>
    [SkippableFact]
    public async Task Differential_AdjacentQuotes_DoubleThenDouble()
    {
        await AssertOracle.EqualAsync(
            "echo \"a\"\"b\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Quoted-first word with an expanding part still joins: x set, echo "$x"'lit' → value+lit.
    /// </summary>
    [SkippableFact]
    public async Task Differential_AdjacentQuotes_DoubleVarThenSingle()
    {
        await AssertOracle.EqualAsync(
            "x=mid; echo \"$x\"'-tail'",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Injection (Directive 12): a leading single-quoted part adjacent to a double-quoted
    /// expansion of a var carrying `;`/`$(...)` must concatenate as data, not execute.
    /// bash: x='$(echo PWN);rm'; echo 'lit'"$x" → lit$(echo PWN);rm (literal).
    /// </summary>
    [SkippableFact]
    public async Task Differential_AdjacentQuotes_InjectionStaysData()
    {
        await AssertOracle.EqualAsync(
            "x='$(echo PWN);rm'; echo 'lit'\"$x\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Unquoted text adjacent to a double-quoted section forms a single word.
    /// bash: echo pre\"mid\"suf → "premidsuf"
    /// </summary>
    [SkippableFact]
    public async Task Differential_AdjacentQuotes_UnquotedAndDoubleQuoted()
    {
        await AssertOracle.EqualAsync(
            "echo pre\"mid\"suf",
            timeout: TimeSpan.FromSeconds(15));
    }
}
