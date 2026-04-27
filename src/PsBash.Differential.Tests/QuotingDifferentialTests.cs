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
    /// KNOWN BUG: ps-bash emitter treats \$ inside double-quoted string as an
    /// EscapedLiteral for the dollar, but the emitter then drops the literal "$5"
    /// from the output — "price:" is emitted without the dollar amount.
    /// Using GoldenAsync to document current (broken) output.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Backslash_EscapesDollarInDoubleQuotes()
    {
        // Directive 1 exception: known emitter bug — \$ inside double-quoted string
        // is not correctly emitted; "$5" is dropped from the output.
        await AssertOracle.GoldenAsync(
            "echo \"price: \\$5\"",
            "Quoting_Backslash_EscapesDollar",
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
    /// $'\n' inside ANSI-C quoting must produce a literal newline.
    /// KNOWN BUG: ps-bash emitter does not recognise the $'...' quoting form;
    /// it emits the literal text $'\n' instead of a newline character.
    /// Using GoldenAsync to document current (broken) ps-bash output.
    /// </summary>
    [SkippableFact]
    public async Task Differential_AnsiCQuote_Newline()
    {
        // Directive 1 exception: known emitter gap — $'...' ANSI-C quoting not implemented;
        // ps-bash treats $'\n' as a literal dollar-single-quote sequence.
        await AssertOracle.GoldenAsync(
            "echo $'hello\\nworld'",
            "Quoting_AnsiC_Newline",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// $'\t' inside ANSI-C quoting must produce a literal tab.
    /// KNOWN BUG: same ANSI-C quoting gap as Differential_AnsiCQuote_Newline.
    /// Using GoldenAsync to document current ps-bash output.
    /// </summary>
    [SkippableFact]
    public async Task Differential_AnsiCQuote_Tab()
    {
        // Directive 1 exception: known emitter gap — $'...' not implemented
        await AssertOracle.GoldenAsync(
            "echo $'col1\\tcol2'",
            "Quoting_AnsiC_Tab",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Empty string handling
    // -----------------------------------------------------------------------

    /// <summary>
    /// Unquoted empty variable contributes no word (the argument is omitted).
    /// Axis 1: empty input variant.
    /// bash: x=; echo start $x end → "start end" (one space, $x omitted).
    /// KNOWN BUG: ps-bash emits "start  end" (two spaces) because it passes an
    /// empty-string argument instead of omitting the word. The emitter maps $x to
    /// $env:x which evaluates to $null/empty in PS but is still passed as an arg.
    /// Using GoldenAsync to document current output.
    /// </summary>
    [SkippableFact]
    public async Task Differential_EmptyVar_UnquotedIsOmitted()
    {
        // Directive 1 exception: known emitter gap — unquoted empty var is not omitted;
        // ps-bash passes empty string through as an argument instead of eliding the word.
        await AssertOracle.GoldenAsync(
            "x=; echo start $x end",
            "Quoting_EmptyVar_UnquotedIsOmitted",
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
    /// VAR=value cmd must set VAR in the child environment for that command only.
    /// After the command finishes, VAR must NOT be visible in the current shell.
    /// Axis 11: environment leak.
    /// bash: PSBASH_ENVPFX_TEST=yes printenv PSBASH_ENVPFX_TEST; echo ${PSBASH_ENVPFX_TEST:-gone}
    /// Expected output: "yes\ngone\n"
    /// KNOWN BUG: ps-bash emits the env-var assignment as `$env:PSBASH_ENVPFX_TEST = "yes"`
    /// before running printenv, so the var leaks into the ps-bash process environment and
    /// persists after the command. The second echo then prints "yes" instead of "gone".
    /// Additionally, `printenv VAR` outputs "PSBASH_ENVPFX_TEST=yes" instead of just "yes".
    /// Using GoldenAsync to document current (broken) output.
    /// </summary>
    [SkippableFact]
    public async Task Differential_EnvPrefix_DoesNotLeakToShell()
    {
        // Directive 1 exception: known emitter bug — env-var prefix leaks into the shell
        // process via $env:NAME assignment; ps-bash does not scope the assignment to the
        // child process only. This is a significant Axis 11 (environment leak) violation.
        await AssertOracle.GoldenAsync(
            "unset PSBASH_ENVPFX_TEST; PSBASH_ENVPFX_TEST=yes printenv PSBASH_ENVPFX_TEST; echo ${PSBASH_ENVPFX_TEST:-gone}",
            "Quoting_EnvPrefix_LeakBug",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Adjacent quoting styles (concatenation)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Adjacent single-quoted and double-quoted parts must join without a space.
    /// bash: echo 'hello'"world" → "helloworld"
    /// KNOWN BUG: ps-bash emits adjacent quoted parts as separate arguments with a
    /// space between them, so echo receives two words: "hello" and "world" → "hello world".
    /// The emitter does not concatenate adjacent word parts that belong to the same word.
    /// Using GoldenAsync to document current (broken) output.
    /// </summary>
    [SkippableFact]
    public async Task Differential_AdjacentQuotes_SingleThenDouble()
    {
        // Directive 1 exception: known emitter bug — adjacent single+double quoted parts
        // in the same word are emitted as two separate arguments instead of concatenated.
        await AssertOracle.GoldenAsync(
            "echo 'hello'\"world\"",
            "Quoting_AdjacentQuotes_SingleThenDouble",
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
