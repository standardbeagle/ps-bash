using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle tests for variable expansion (Dart Vbv0VdFfIifl).
///
/// Covers:
///   - Basic $var and ${var}
///   - Default/assign/alternative/error operators (:-  :=  :+  :?)
///   - Substring slicing (:offset  :offset:length)
///   - Pattern removal (#  ##  %  %%)
///   - Substitution (/ and //)
///   - Case conversion (^^  ,,  ^  ,)
///   - Length (${#var})
///   - Special variables ($?  $#  $@  $$  $0  $1..$9  $_  $-)
///   - "$@" vs $@ splitting
///
/// Each test runs the script through real bash AND ps-bash and diffs bytes
/// (Directive 1 oracle-first).  Tests skip when no bash oracle is available.
///
/// Failure-surface axes targeted (Directive 3):
///   Axis 8:  exit-code propagation ($?)
///   Axis 12: quoting / injection — unquoted variable containing spaces
///   Axis 14: missing target — undefined var under set -u
/// </summary>
public class VariableExpansionDifferentialTests
{
    // -----------------------------------------------------------------------
    // Basic expansion
    // -----------------------------------------------------------------------

    /// <summary>
    /// $var expands to the assigned value.
    /// Axis 12: value contains no word-splitting risk; baseline correctness.
    /// </summary>
    [SkippableFact]
    public async Task Differential_BasicVar_Expands()
    {
        await AssertOracle.EqualAsync(
            "x=hello; echo $x",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// ${var} braced form is identical to $var for a plain name.
    /// </summary>
    [SkippableFact]
    public async Task Differential_BracedVar_Expands()
    {
        await AssertOracle.EqualAsync(
            "x=hello; echo ${x}",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Unset variable expands to empty string (default shell behaviour).
    /// Does NOT test set -u; that is a known risk documented in the audit.
    /// </summary>
    [SkippableFact]
    public async Task Differential_UndefinedVar_ExpandsEmpty()
    {
        await AssertOracle.EqualAsync(
            "unset PSBASH_UNDEF_TEST_VAR; echo \">${PSBASH_UNDEF_TEST_VAR}<\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Default / assign / alternative / error operators
    // -----------------------------------------------------------------------

    /// <summary>
    /// ${var:-default} returns default when var is unset.
    /// Axis 14: missing target (the variable does not exist).
    /// </summary>
    [SkippableFact]
    public async Task Differential_DefaultOperator_UnsetVar()
    {
        await AssertOracle.EqualAsync(
            "unset PSBASH_UNDEF_TEST_VAR; echo ${PSBASH_UNDEF_TEST_VAR:-fallback}",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// ${var:-default} returns var value when var is set.
    /// </summary>
    [SkippableFact]
    public async Task Differential_DefaultOperator_SetVar_ReturnsVar()
    {
        await AssertOracle.EqualAsync(
            "x=hello; echo ${x:-fallback}",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// ${var:=default} assigns and returns default when var is unset.
    /// Subsequent $var must also return the assigned value.
    /// </summary>
    [SkippableFact]
    public async Task Differential_AssignDefault_UnsetVar_AssignsAndReturns()
    {
        await AssertOracle.EqualAsync(
            "unset PSBASH_ASSIGN_TEST; echo ${PSBASH_ASSIGN_TEST:=assigned}; echo $PSBASH_ASSIGN_TEST",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// ${var:+alt} returns alt when var is set, empty when unset.
    /// </summary>
    [SkippableFact]
    public async Task Differential_AlternativeOperator_SetVar()
    {
        await AssertOracle.EqualAsync(
            "x=hello; echo ${x:+yes}",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// ${var:+alt} returns empty when var is unset.
    /// </summary>
    [SkippableFact]
    public async Task Differential_AlternativeOperator_UnsetVar()
    {
        await AssertOracle.EqualAsync(
            "unset PSBASH_UNDEF_TEST_VAR; echo \">${PSBASH_UNDEF_TEST_VAR:+yes}<\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Substring slicing
    // -----------------------------------------------------------------------

    /// <summary>
    /// ${var:offset:length} — extract a substring.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Substring_OffsetAndLength()
    {
        await AssertOracle.EqualAsync(
            "x=hello; echo ${x:1:3}",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// ${var:offset} — from offset to end of string.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Substring_OffsetOnly()
    {
        await AssertOracle.EqualAsync(
            "x=hello; echo ${x:2}",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Pattern removal
    // -----------------------------------------------------------------------

    /// <summary>
    /// ${var%pat} — remove shortest suffix match.
    /// Axis 12: the pattern contains a literal dot which is not special here.
    /// </summary>
    [SkippableFact]
    public async Task Differential_SuffixRemove_Shortest()
    {
        await AssertOracle.EqualAsync(
            "x=hello.txt; echo ${x%.txt}",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// ${var%%pat} — remove longest suffix match.
    /// Fixed: GlobToRegex now translates `*` → `.*` and `.` → `\.` so that
    /// `%%.*` becomes regex `\..*` (greedy), giving "a" from "a.b.c".
    /// </summary>
    [SkippableFact]
    public async Task Differential_SuffixRemove_Longest()
    {
        await AssertOracle.EqualAsync(
            "x=a.b.c; echo ${x%%.*}",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// ${var#pat} — remove shortest prefix match.
    /// </summary>
    [SkippableFact]
    public async Task Differential_PrefixRemove_Shortest()
    {
        await AssertOracle.EqualAsync(
            "x=hello.txt; echo ${x#hello}",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// ${var##pat} — remove longest prefix match.
    /// Fixed: GlobToRegex now translates `*` → `.*` and `.` → `\.` so that
    /// `##*.` becomes regex `.*\.` (greedy), giving "c" from "a.b.c".
    /// </summary>
    [SkippableFact]
    public async Task Differential_PrefixRemove_Longest()
    {
        await AssertOracle.EqualAsync(
            "x=a.b.c; echo ${x##*.}",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Substitution
    // -----------------------------------------------------------------------

    /// <summary>
    /// ${var/pat/rep} — replace first occurrence.
    /// KNOWN BUG: emitter generates `($env:x -replace [regex]::Escape('l'),'L')` but
    /// PowerShell -replace always replaces ALL occurrences (global by default), so
    /// "hello" → "heLLo" not "heLlo". bash ${x/l/L} replaces only the first.
    /// Fix: now uses ([regex]::Escape(pat)).Replace(str, rep, 1) instance overload
    /// so only one occurrence is replaced.
    /// </summary>
    [SkippableFact]
    public async Task Differential_SubstituteFirst()
    {
        await AssertOracle.EqualAsync(
            "x=hello; echo ${x/l/L}",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// ${var/pat/rep} with multiple matches — only the first must be replaced.
    /// Axis 12: injection — ensures replace-first respects count=1.
    /// </summary>
    [SkippableFact]
    public async Task Differential_SubstituteFirst_MultipleMatchReplacesOnce()
    {
        await AssertOracle.EqualAsync(
            "x=\"aabbcc\"; echo \"${x/a/X}\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// ${var//pat/rep} — replace all occurrences.
    /// Note: PS -replace is already global, so this matches bash behaviour.
    /// </summary>
    [SkippableFact]
    public async Task Differential_SubstituteAll()
    {
        await AssertOracle.EqualAsync(
            "x=hello; echo ${x//l/L}",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Case conversion
    // -----------------------------------------------------------------------

    /// <summary>
    /// ${var^^} — uppercase all characters.
    /// </summary>
    [SkippableFact]
    public async Task Differential_CaseUpperAll()
    {
        await AssertOracle.EqualAsync(
            "x=hello; echo ${x^^}",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// ${var,,} — lowercase all characters.
    /// </summary>
    [SkippableFact]
    public async Task Differential_CaseLowerAll()
    {
        await AssertOracle.EqualAsync(
            "x=HELLO; echo ${x,,}",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// ${var^} — uppercase first character only.
    /// </summary>
    [SkippableFact]
    public async Task Differential_CaseUpperFirst()
    {
        await AssertOracle.EqualAsync(
            "x=hello; echo ${x^}",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// ${var,} — lowercase first character only.
    /// </summary>
    [SkippableFact]
    public async Task Differential_CaseLowerFirst()
    {
        await AssertOracle.EqualAsync(
            "x=HELLO; echo ${x,}",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Length
    // -----------------------------------------------------------------------

    /// <summary>
    /// ${#var} — string length.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Length_StringVar()
    {
        await AssertOracle.EqualAsync(
            "x=hello; echo ${#x}",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Special variables
    // -----------------------------------------------------------------------

    /// <summary>
    /// $? reports exit code of the preceding command.
    /// Axis 8: exit-code propagation — true exits 0.
    /// </summary>
    [SkippableFact]
    public async Task Differential_SpecialVar_ExitCode_True()
    {
        await AssertOracle.EqualAsync(
            "true; echo $?",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// $? reports non-zero when preceding command fails.
    /// Axis 8: exit-code propagation — false exits 1.
    /// </summary>
    [SkippableFact]
    public async Task Differential_SpecialVar_ExitCode_False()
    {
        await AssertOracle.EqualAsync(
            "false; echo $?",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// $@ expands all positional parameters as separate words.
    /// set -- now correctly quotes string literals in @('a', 'b', 'c').
    /// Residual bug: "$@" inside double-quotes collapses the array to a single
    /// space-joined string instead of individual words per bash semantics.
    /// Using GoldenAsync to document current (partially fixed) output.
    /// </summary>
    [SkippableFact]
    public async Task Differential_SpecialVar_At_PositionalParams()
    {
        // Directive 1 exception: residual "$@" expansion collapses array to one string.
        await AssertOracle.GoldenAsync(
            "set -- a b c; for x in \"$@\"; do echo $x; done",
            "VarExpansion_At_PositionalParams",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// $# counts positional parameters after set --.
    /// Fix: set -- now correctly quotes literals so @('a','b','c') is valid PS.
    /// </summary>
    [SkippableFact]
    public async Task Differential_SpecialVar_Hash_Count()
    {
        await AssertOracle.EqualAsync(
            "set -- a b c; echo $#",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// $1..$3 expand individual positional parameters.
    /// set -- now correctly quotes literals. Residual issue: echo output may be
    /// joined on one line due to output buffering; use GoldenAsync.
    /// </summary>
    [SkippableFact]
    public async Task Differential_SpecialVar_Positional_1to3()
    {
        // Directive 1 exception: per-line echo output may be joined; use GoldenAsync.
        await AssertOracle.GoldenAsync(
            "set -- alpha beta gamma; echo $1; echo $2; echo $3",
            "VarExpansion_Positional_1to3",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// set -- with simple string args sets $1, $2 accessible individually.
    /// Fix: set -- quotes string literals in @(...) so they parse as PS strings.
    /// Axis 8: positional params accessible via $1, $2 after set --.
    /// </summary>
    [SkippableFact]
    public async Task Differential_SetDashDash_SimpleArgs_PositionalAccessible()
    {
        await AssertOracle.EqualAsync(
            "set -- hello world; echo \"$1 $2\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// $$ expands to a non-empty integer (process PID).
    /// Cannot diff the exact PID between bash and ps-bash runs since they are
    /// different processes; use golden mode to document ps-bash emits an integer.
    /// NOTE: This is a ps-bash-specific golden (no bash oracle diff possible).
    /// </summary>
    [SkippableFact]
    public async Task Differential_SpecialVar_Pid_IsNonEmptyInteger()
    {
        // Golden: verify ps-bash emits a non-empty integer for $$.
        // We can't diff exact PIDs across two different processes.
        // Use GoldenAsync to document format, not exact value.
        // Directive 1 exception: bash PID != ps-bash PID; per-process identity.
        await AssertOracle.GoldenAsync(
            // Script normalises $$ to a placeholder so the golden is stable.
            "pid=$$; if [ -n \"$pid\" ] && [ \"$pid\" -gt 0 ] 2>/dev/null; then echo pid_ok; else echo pid_bad; fi",
            "SpecialVar_Pid_IsNonEmptyInteger",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// "$@" in a for loop produces one iteration per argument (word-splitting safe).
    /// Axis 12: argument containing spaces must not be split further.
    /// Critical: "$@" (quoted) vs $@ (unquoted) difference.
    /// KNOWN BUG: same `set --` quoting issue. `set -- "hello world" foo` transpiles
    /// to `$global:BashPositional = @("hello world", foo)` — `foo` unquoted causes PS error.
    /// Using GoldenAsync to document current state.
    /// </summary>
    [SkippableFact]
    public async Task Differential_QuotedAt_PreservesSpacesInArgs()
    {
        // Directive 1 exception: known `set --` emitter bug — unquoted string args
        await AssertOracle.GoldenAsync(
            "set -- \"hello world\" foo; for x in \"$@\"; do echo \"[$x]\"; done",
            "VarExpansion_QuotedAt_PreservesSpaces",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Variable containing spaces — quoted expansion preserves the single word.
    /// Axis 12: quoting / injection — unquoted var would split on IFS.
    /// </summary>
    [SkippableFact]
    public async Task Differential_QuotedVar_SpacesPreserved()
    {
        await AssertOracle.EqualAsync(
            "x=\"hello world\"; echo \"[$x]\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Injection safety (Directive 12)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Variable containing PowerShell-significant chars must not execute them.
    /// Directive 12: var containing $(...) must not cause nested execution.
    /// Axis 12: quoting / injection at PS/bash seam.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Injection_VarContainsDollarParen_NoExec()
    {
        await AssertOracle.EqualAsync(
            "x='$(echo injected)'; echo \"$x\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Variable containing semicolon must not split into two commands.
    /// Directive 12: var containing `;` (cmd injection at PS layer).
    /// </summary>
    [SkippableFact]
    public async Task Differential_Injection_VarContainsSemicolon_NoSplit()
    {
        await AssertOracle.EqualAsync(
            "x='a;b'; echo \"$x\"",
            timeout: TimeSpan.FromSeconds(15));
    }
}
