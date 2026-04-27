using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle tests for case/esac (Dart swq8znLUb1BN).
///
/// Each test runs the script in real bash AND ps-bash and diffs bytes.
/// Tests skip when no bash oracle is available (e.g. Windows without WSL).
///
/// Failure-surface axes targeted per test (Directive 3):
///   - Axis 8:  exit-code propagation
///   - Axis 12: quoting / injection (case expr with spaces, $var expansion)
/// </summary>
public class CaseEsacDifferentialTests
{
    // -----------------------------------------------------------------------
    // Basic pattern matching
    // -----------------------------------------------------------------------

    /// <summary>
    /// Simple match: case literal string in single pattern arm.
    /// Axis 8: exit code after echo must be 0.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Case_SimpleMatch()
    {
        await AssertOracle.EqualAsync(
            "case hello in hello) echo yes ;; esac",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// No match: all arms fail to match — body must not execute, exit 0.
    /// Confirms the switch does not fall-through or error on no match.
    /// Axis 8: ps-bash and bash must both exit 0 with no output.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Case_NoMatch_NoOutput()
    {
        await AssertOracle.EqualAsync(
            "case x in a) echo a ;; b) echo b ;; esac",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Wildcard default arm `*` catches anything not matched earlier.
    /// Confirms `default { }` emission is correct.
    /// Axis 12: the default arm must fire for an unquoted string value.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Case_WildcardDefault()
    {
        await AssertOracle.EqualAsync(
            "case anything in foo) echo foo ;; *) echo default ;; esac",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Alternate patterns `a|b|c` — any of the alternatives must match.
    /// Confirms each pattern is emitted as a separate switch clause.
    /// Axis 12: the matched value must not be word-split before comparison.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Case_AlternatePatterns()
    {
        await AssertOracle.EqualAsync(
            "case b in a|b|c) echo abc ;; *) echo other ;; esac",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Glob in pattern `*.txt` triggers `-Wildcard` mode on the switch.
    /// Axis 12: the glob must not be treated as a literal string.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Case_GlobPattern()
    {
        await AssertOracle.EqualAsync(
            "case file.txt in *.txt) echo text ;; *) echo other ;; esac",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Variable expansion in the case subject
    // -----------------------------------------------------------------------

    /// <summary>
    /// Variable as case subject: case $x in ... — $x must be dereferenced.
    /// Axis 12: variable must not be emitted as the literal string `$x`.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Case_VariableSubject()
    {
        await AssertOracle.EqualAsync(
            "x=hello; case $x in hello) echo matched ;; *) echo missed ;; esac",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Quoted case subject: case "$x" in — double-quoted var must still match.
    /// Axis 12: quoting must not suppress variable expansion or break matching.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Case_QuotedSubject()
    {
        await AssertOracle.EqualAsync(
            "x=world; case \"$x\" in world) echo yes ;; *) echo no ;; esac",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Leading-paren arm form: (pattern)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Leading `(` before pattern — POSIX allows `(a)` as well as `a)`.
    /// Parser must consume the leading LParen and still produce correct CaseArm.
    /// Axis 12: alternate form must produce identical output to bare `a)`.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Case_LeadingParenForm()
    {
        await AssertOracle.EqualAsync(
            "case hello in (hello) echo yes ;; esac",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Exit-code propagation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Exit code of the case body is visible via $? after esac.
    /// Uses a command that exits non-zero in the matched arm.
    /// Axis 8: $? after `case` must equal the exit code of the last body command.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Case_ExitCodePropagation()
    {
        await AssertOracle.EqualAsync(
            "case ok in ok) false ;; esac; echo $?",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Multiple arms with sequential matching
    // -----------------------------------------------------------------------

    /// <summary>
    /// Only the first matching arm fires; subsequent arms do not run (no fallthrough).
    /// Confirms that when `a` matches the first arm, the second arm body (`b`) does
    /// not execute and the result is a single line of output.
    /// Axis 8: exit code after the matched echo must be 0.
    ///
    /// Note: `;&` (fallthrough) and `;;&` (continue matching) terminators are NOT
    /// supported by the parser — the parser only handles `;;`.  Scripts using `;&`
    /// or `;;&` will hang ps-bash.  Tracked as a known gap in the audit document.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Case_NoFallthrough_OnlyFirstMatchFires()
    {
        // bash: only "first" prints (a matches arm 1; arm 2 not reached without ;&)
        await AssertOracle.EqualAsync(
            "case a in a) echo first ;; b) echo second ;; esac",
            timeout: TimeSpan.FromSeconds(15));
    }
}
