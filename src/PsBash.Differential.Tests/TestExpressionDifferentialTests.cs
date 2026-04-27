using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle tests for test expressions: [ ], [[ ]], and the bare `test` command.
///
/// Dart audit task: ckLNYBBiqRC0
///
/// Coverage axes targeted per Directive 3:
///   - Axis 1:  empty/undefined var (empty string in condition)
///   - Axis 8:  exit-code propagation (-f on nonexistent file, ! negation)
///   - Axis 12: quoting critical (empty-var without quotes causes arg-count mismatch)
///   - Axis 14: missing target (-f on /nonexistent path)
/// </summary>
public class TestExpressionDifferentialTests
{
    // -----------------------------------------------------------------------
    // File tests — [ ] form
    // -----------------------------------------------------------------------

    /// <summary>
    /// [ -f /nonexistent ] must exit 1 (false) with no stdout.
    /// Axis 14: missing target path, exit code propagation.
    /// Emitter maps -f to Test-Path -PathType Leaf; must return false for missing file.
    /// </summary>
    [SkippableFact]
    public async Task Differential_FileTest_NonexistentFile_IsFalse()
    {
        await AssertOracle.EqualAsync(
            "if [ -f /nonexistent/path/file.txt ]; then echo yes; else echo no; fi",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// [ -d /tmp ] must exit 0 (true) — /tmp is always a directory.
    /// Axis 8: positive case, exit code 0 drives then branch.
    /// </summary>
    [SkippableFact]
    public async Task Differential_DirTest_TmpIsDirectory()
    {
        await AssertOracle.EqualAsync(
            "if [ -d /tmp ]; then echo yes; else echo no; fi",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // String tests — [ ] form
    // -----------------------------------------------------------------------

    /// <summary>
    /// String equality: [ "hello" = "hello" ] must be true.
    /// Axis 8: exit code 0 from positive match.
    /// Emitter maps = to -eq.
    /// </summary>
    [SkippableFact]
    public async Task Differential_StringTest_EqualityTrue()
    {
        await AssertOracle.EqualAsync(
            "if [ \"hello\" = \"hello\" ]; then echo yes; else echo no; fi",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// String inequality: [ "a" != "b" ] must be true.
    /// Axis 8: exit code 0 from positive != test.
    /// Emitter maps != to -ne.
    /// </summary>
    [SkippableFact]
    public async Task Differential_StringTest_InequalityTrue()
    {
        await AssertOracle.EqualAsync(
            "if [ \"a\" != \"b\" ]; then echo yes; else echo no; fi",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// -z "" must be true; -z "x" must be false.
    /// Axis 1: empty string is the canonical empty-input case for -z.
    /// Axis 12: unquoted empty var would cause [ -z ] to have wrong arg count.
    /// </summary>
    [SkippableFact]
    public async Task Differential_StringTest_ZeroLength_EmptyIsTrue()
    {
        await AssertOracle.EqualAsync(
            "if [ -z \"\" ]; then echo empty; else echo nonempty; fi",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// -n "x" must be true (non-empty string).
    /// Paired with the -z test to cover both sides of the length axis.
    /// Axis 1: ensures non-empty path also correct.
    /// </summary>
    [SkippableFact]
    public async Task Differential_StringTest_NonEmpty_StringIsTrue()
    {
        await AssertOracle.EqualAsync(
            "if [ -n \"x\" ]; then echo yes; else echo no; fi",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Integer comparison tests — [ ] form
    // -----------------------------------------------------------------------

    /// <summary>
    /// Integer comparison: [ 5 -gt 3 ] must be true.
    /// Axis 8: numeric -gt emitted as PS -gt; exit code 0 drives then branch.
    /// </summary>
    [SkippableFact]
    public async Task Differential_IntTest_GtTrue()
    {
        await AssertOracle.EqualAsync(
            "if [ 5 -gt 3 ]; then echo yes; else echo no; fi",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Integer equality -eq: [ 7 -eq 7 ] must be true.
    /// Axis 8: -eq exit code 0.
    /// </summary>
    [SkippableFact]
    public async Task Differential_IntTest_EqTrue()
    {
        await AssertOracle.EqualAsync(
            "if [ 7 -eq 7 ]; then echo yes; else echo no; fi",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Integer less-than -lt: [ 2 -lt 10 ] must be true.
    /// Axis 8: -lt emitted as PS -lt.
    /// </summary>
    [SkippableFact]
    public async Task Differential_IntTest_LtTrue()
    {
        await AssertOracle.EqualAsync(
            "if [ 2 -lt 10 ]; then echo yes; else echo no; fi",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Negation — ! in [ ] form (via Pipeline.Negated)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Negation of a file test: [ ! -f /nonexistent ] must be true.
    /// Known bug: emitter emits invalid PS for ! inside [ ].
    /// Axis 8: exit code after ! negation.
    /// Axis 14: missing file is the test subject.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Negation_BracketNot_FileTest()
    {
        await AssertOracle.GoldenAsync(
            "if [ ! -f /nonexistent/file ]; then echo yes; else echo no; fi",
            "Differential_Negation_BracketNot_FileTest",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// ! before a simple command (pipeline negation): ! false must succeed.
    /// Known bug: negation suffix emits '; $LASTEXITCODE = ...' which breaks PS &&/||.
    /// Axis 8: pipeline Negated=true flips exit code.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Negation_PipelineNegated_False()
    {
        await AssertOracle.GoldenAsync(
            "if ! false; then echo yes; else echo no; fi",
            "Differential_Negation_PipelineNegated_False",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Double-bracket [[ ]] — regex and logical operators inside
    // -----------------------------------------------------------------------

    /// <summary>
    /// [[ "hello world" =~ hello ]] — regex match must be true.
    /// Emitter maps =~ to PS -match with single-quoted pattern.
    /// Axis 8: true result drives then-branch.
    /// </summary>
    [SkippableFact]
    public async Task Differential_DoubleBracket_Regex_Matches()
    {
        await AssertOracle.EqualAsync(
            "if [[ \"hello world\" =~ hello ]]; then echo yes; else echo no; fi",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// [[ -f /nonexistent && -d /tmp ]] — compound: file-missing AND dir-exists.
    /// Known bug: && inside [[ ]] not properly handled by emitter's SplitLogical path.
    /// Axis 8: exit code from compound && inside [[ ]].
    /// </summary>
    [SkippableFact]
    public async Task Differential_DoubleBracket_LogicalAnd_FalseAndTrue_IsFalse()
    {
        await AssertOracle.GoldenAsync(
            "if [[ -f /nonexistent && -d /tmp ]]; then echo yes; else echo no; fi",
            "Differential_DoubleBracket_LogicalAnd_FalseAndTrue_IsFalse",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// [[ "abc" == a* ]] — glob pattern inside [[ ]] uses -like in PS.
    /// Axis 12: glob characters (* ? [) trigger -like emission not -eq.
    /// </summary>
    [SkippableFact]
    public async Task Differential_DoubleBracket_GlobMatch_Matches()
    {
        await AssertOracle.EqualAsync(
            "if [[ \"abc\" == a* ]]; then echo yes; else echo no; fi",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Quoting critical (Axis 12) — undefined/empty var in test
    // -----------------------------------------------------------------------

    /// <summary>
    /// [ -z "$UNDEFINED_VAR_PSBASH_TEST" ] — var is undefined; with quotes the
    /// shell sees -z "" (one arg) which is true; without quotes it would see
    /// -z with zero args, causing an error.
    /// Axis 1+12: empty/undefined var combined with quoting discipline.
    /// </summary>
    [SkippableFact]
    public async Task Differential_QuotedUndefinedVar_ZeroLengthTest_IsTrue()
    {
        await AssertOracle.EqualAsync(
            "unset UNDEFINED_VAR_PSBASH_TEST; if [ -z \"$UNDEFINED_VAR_PSBASH_TEST\" ]; then echo empty; else echo defined; fi",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Standalone test expressions (exit-code propagation, void wrapping)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Standalone [ -f /tmp ] in an && chain — left side is true so right runs.
    /// Tests void-wrapping path: emitter wraps BoolExpr in [void](...) in && context.
    /// Axis 8: exit code from standalone BoolExpr drives && continuation.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Standalone_FileTest_AndChain_TmpExists()
    {
        await AssertOracle.EqualAsync(
            "[ -d /tmp ] && echo yes",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Standalone [ -f /nonexistent ] drives || fallback.
    /// Axis 8: false BoolExpr exit code triggers || right arm.
    /// Bug 7 fixed: BoolExpr in &&/|| chains now propagates exit code via
    /// $global:LASTEXITCODE instead of being silently discarded by [void].
    /// </summary>
    [SkippableFact]
    public async Task Differential_Standalone_FileTest_OrChain_NonexistentFallsBack()
    {
        await AssertOracle.EqualAsync(
            "[ -f /nonexistent/file ] || echo no",
            timeout: TimeSpan.FromSeconds(15));
    }
}
