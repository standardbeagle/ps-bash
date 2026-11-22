using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle tests for glob and brace expansion features (DART-2wxWlHHXpW1z).
///
/// Layers exercised:
///   - Lexer:   IsBraceExpansion detection, ScanWord for extglob/charclass
///   - Parser:  DecomposeWord → BracedTuple, BracedRange, GlobPart production
///   - Emitter: EmitBraceExpandedWord, FormatBraceArray, ExpandBrace, EmitWordPart(GlobPart)
///
/// Oracle strategy per Directive 1:
///   EqualAsync  — live bash diff; used where ps-bash output matches bash.
///   GoldenAsync — frozen golden file; used for known divergences (documented below).
///
/// Failure-surface axes covered (per QA rubric Directive 3):
///   Axis 12: quoting / injection — quoted brace must not expand inside double-quotes
///   Axis 8:  exit-code propagation — brace expansion is a word transform; exit = 0
///   Axis 3:  unicode / non-ASCII — letter range gap documented (GoldenAsync)
///   Axis 14: missing target — glob with no match passes literal through (no-match)
/// </summary>
public class GlobBraceExpansionDifferentialTests
{
    // -----------------------------------------------------------------------
    // 1. Brace tuple — {a,b,c}
    //
    // Failure-surface: Axis 12 (quoting). The emitter must produce three
    // separate arguments, not a single string with commas.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that a bare brace tuple expands to space-separated words.
    /// Emitter path: BracedTuple → FormatBraceArray → @('a','b','c').
    /// Regression: any loss of the array type collapses words into one argument.
    /// </summary>
    [SkippableFact]
    public async Task Differential_BraceTuple_ExpandsToWords()
    {
        await AssertOracle.EqualAsync(
            "echo {a,b,c}",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // 2. Brace range — {1..5}
    //
    // Failure-surface: Axis 8 (exit code = 0 after word expansion).
    // The emitter must emit @(1..5) which PowerShell evaluates to 1 2 3 4 5.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that an integer brace range emits the correct sequence.
    /// Emitter path: BracedRange(1,5,0,0) → FormatBraceArray → @(1..5).
    /// Regression: if step logic is wrong, the range overshoots or is empty.
    /// </summary>
    [SkippableFact]
    public async Task Differential_BraceRange_IntegerSequence()
    {
        await AssertOracle.EqualAsync(
            "echo {1..5}",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // 3. Zero-padded range — {01..03}
    //
    // Failure-surface: Axis 12 (formatting). The output must be "01 02 03",
    // not "1 2 3". ZeroPad field in BracedRange must be respected.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that a zero-padded range preserves leading zeros in output.
    /// Emitter path: BracedRange(1,3,2,0) → IsPlainInteger returns false for
    /// "01" → string array @('01','02','03').
    /// Regression: IsPlainInteger misclassifying "01" as a plain integer would
    /// emit @(1..3) → "1 2 3" instead of "01 02 03".
    /// </summary>
    [SkippableFact]
    public async Task Differential_BraceRange_ZeroPadded()
    {
        await AssertOracle.EqualAsync(
            "echo {01..03}",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // 4. Step range — {0..10..3}
    //
    // Failure-surface: Axis 8 (step arithmetic). Output must be "0 3 6 9",
    // not "0 1 2 3 4 5 6 7 8 9 10" (missing step) or "0" (step overflow).
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that a brace range with explicit step skips correctly.
    /// Emitter path: BracedRange(0,10,0,3) → ExpandBrace step loop.
    /// The loop must not overshoot (stop at 9, not include 12).
    /// </summary>
    [SkippableFact]
    public async Task Differential_BraceRange_WithStep()
    {
        await AssertOracle.EqualAsync(
            "echo {0..10..3}",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // 5. Prefix brace — file{1,2,3}.txt
    //
    // Failure-surface: Axis 12 (word concatenation). The prefix "file" and
    // suffix ".txt" must be concatenated with each brace item.
    // Emitter path: EmitBraceExpandedWord → @('file1.txt','file2.txt','file3.txt').
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that prefix+brace+suffix concatenation produces correct words.
    /// Regression: if prefix/suffix are dropped, output is just "1 2 3".
    /// If the brace is not detected, the literal string "file{1,2,3}.txt" is
    /// passed as a single argument.
    /// </summary>
    [SkippableFact]
    public async Task Differential_BraceTuple_WithPrefixAndSuffix()
    {
        await AssertOracle.EqualAsync(
            "echo file{1,2,3}.txt",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // 6. Quoted brace — "{a,b}" must NOT expand
    //
    // Failure-surface: Axis 12 (quoting disables expansion). Inside double
    // quotes the braces are literal. Output must be "{a,b}", not "a b".
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that brace expansion is suppressed inside double quotes.
    /// Parser path: DoubleQuoted → inner parts are parsed as literals/var subs,
    /// never as BracedTuple. The raw text {a,b} becomes a Literal node.
    /// Regression: if the word decomposer incorrectly runs brace detection
    /// inside double-quoted content, output becomes "a b" instead of "{a,b}".
    /// </summary>
    [SkippableFact]
    public async Task Differential_BraceTuple_InsideDoubleQuotes_NoExpansion()
    {
        await AssertOracle.EqualAsync(
            "echo \"{a,b}\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // 7. Single-item brace trailing comma — {a,} expands to "a" and ""
    //
    // Failure-surface: Axis 12. bash: "a " (two words: "a" and "").
    // The trailing comma produces an empty second item.
    // -----------------------------------------------------------------------

    /// <summary>
    /// A bare empty item makes an empty unquoted word, which bash removes: <c>{a,}</c> is ONE word, <c>{,}</c> none,
    /// <c>$e{a,}</c> none with <c>e</c> unset. A word with a quoted part stays (<c>{"",a}</c>, <c>''{a,}</c>,
    /// <c>{"$e",b}</c>). REGRESSION: ps-bash passed the empty words on (<c>printf '[%s]' {a,}</c> printed <c>[a][]</c>);
    /// <c>echo {a,}</c> hid it behind a trailing space. Rule: <c>PsEmitter.DropsEmptyBraceWord</c>; the parser records
    /// quoted empty items (<c>BracedTuple.QuotedEmpty</c>).
    /// </summary>
    [SkippableFact]
    public async Task Differential_BraceTuple_TrailingComma_EmptyItem()
    {
        await AssertOracle.EqualAsync(
            "p() { printf '[%s]' \"$@\"; echo \" n=$#\"; }\n"
            + "p {a,}; p {,}; p x{a,}; p ''{a,}; p \"{a,}\"; p {a,,b}; p {,a}{,b}; p {a,}\"\"; p $e{a,}; p {\"\",a}; p {a,$e}\n"
            + "p {\"$e\",b}; p {$e,b}; x=1; p {$x,}; p {a,}{1..2}; p {,}{,}z\n"
            + "for w in {a,} {,} x{,}; do printf '<%s>' \"$w\"; done; echo\n"
            + "arr=({a,} {,b} {,}); echo \"${#arr[@]} [${arr[*]}]\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // 8. Letter range — {a..e}: bash "a b c d e" (ParseBraceExpansion's single-letter range; it was the literal "a..e").
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task Differential_BraceRange_LetterRange()
    {
        await AssertOracle.EqualAsync(
            "echo {a..e}",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // 9. Nested braces — {a,b{1,2},c}: bash "a b1 b2 c" (a depth-aware comma split and ExpandNestedBraces; a flat
    // Split(',') gave "a,c} b{1,c} 2,c}").
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task Differential_BraceTuple_NestedBraces()
    {
        await AssertOracle.EqualAsync(
            "echo {a,b{1,2},c}",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // 10. Glob — * and ? passthrough (no filesystem match needed)
    //
    // Failure-surface: Axis 14 (no-match behavior). When no file matches,
    // bash returns the glob pattern literally (default nullglob=off).
    // ps-bash must also return the literal pattern.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that glob characters in an argument that matches no files
    /// are passed through as literals (default nullglob=off behavior).
    /// Uses a path under /nonexistent/ that can never match on any platform.
    /// Emitter path: GlobPart → gp.Pattern (emitted verbatim); runtime
    /// Resolve-BashGlob returns the literal when no match.
    /// Failure-surface: Axis 14 (missing target).
    /// </summary>
    [SkippableFact]
    public async Task Differential_Glob_NoMatchPassthrough()
    {
        // Both bash and ps-bash should echo the literal glob pattern when no
        // files match and nullglob is off (the default).
        await AssertOracle.EqualAsync(
            "echo /nonexistent_psbash_test_path/*.txt",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // 11. Negated char class — [!abc] passthrough
    //
    // Failure-surface: Axis 12 (char class negation). The lexer must classify
    // [!abc] as a GlobPart (not a BoolExpr or error) when used as a word arg.
    // In a no-match context the pattern is echoed literally.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that a negated character class glob is parsed as a GlobPart
    /// and echoed literally when it matches no files.
    /// Parser path: ContainsBracketGlob detects [!abc] → GlobPart("[!abc]").
    /// </summary>
    [SkippableFact]
    public async Task Differential_Glob_NegatedCharClass_NoMatch_Passthrough()
    {
        await AssertOracle.EqualAsync(
            "echo /nonexistent_psbash_test_path/[!abc]*",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // 12. Reverse integer range — {5..1}
    //
    // Failure-surface: Axis 8 (descending step logic). ExpandBrace must
    // compute step = -1 when Start > End. Output: "5 4 3 2 1".
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that a descending integer range generates the correct sequence.
    /// Emitter path: BracedRange(5,1,0,0) → ExpandBrace, step = -1.
    /// Regression: if step sign is not derived from Start/End order, the loop
    /// never executes and the output is empty.
    /// </summary>
    [SkippableFact]
    public async Task Differential_BraceRange_Descending()
    {
        await AssertOracle.EqualAsync(
            "echo {5..1}",
            timeout: TimeSpan.FromSeconds(15));
    }
}
