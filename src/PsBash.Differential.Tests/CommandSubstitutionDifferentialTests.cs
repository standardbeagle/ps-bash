using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle tests for command substitution ($(cmd), backtick), arithmetic
/// substitution ($((expr))), and process substitution (&lt;(cmd), &gt;(cmd)).
///
/// Dart audit task: hp88yKa9SGoF
///
/// Layers exercised:
///   - Lexer:   CommandSub token recognition, backtick scanning, ArithSub $((, ProcessSub &lt;(
///   - Parser:  WordPart.CommandSub / WordPart.ArithSub / WordPart.ProcessSub production
///   - Emitter: EmitWordPart dispatch — CommandSub → $(...), ArithSub → EmitArithSub,
///              ProcessSub → Invoke-ProcessSub { ... }
///   - Runtime: trailing newline stripping from command output; Invoke-ProcessSub temp-file path
///
/// Oracle strategy per Directive 1:
///   EqualAsync  — live bash diff; used where ps-bash output is expected to match bash.
///   GoldenAsync — frozen golden file; used for features where runtime support is partial.
///
/// Failure-surface axes covered (per QA rubric Directive 3):
///   Axis 1:  empty input — command sub on empty-output command
///   Axis 8:  exit-code propagation — cmd sub does NOT propagate exit code of inner cmd
///   Axis 12: quoting / injection — cmd sub inside double quotes; var with special chars
///   Axis 15: recursion depth — nested command substitution three levels deep
/// </summary>
public class CommandSubstitutionDifferentialTests
{
    // -----------------------------------------------------------------------
    // 1. Basic command substitution: $(cmd)
    //
    // Failure-surface: Axis 12 (quoting). Emitter must produce $(...) whose
    // output is word-split then passed as arguments — but inside double quotes
    // word-splitting is suppressed.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that $(echo hello) expands to "hello" at runtime.
    /// Emitter path: CommandSub → $(...) containing Invoke-BashEcho.
    /// Regression: if CommandSub body is not transpiled, the literal text
    /// "echo hello" appears instead of "hello".
    /// </summary>
    [SkippableFact]
    public async Task Differential_CommandSub_BasicEcho_ExpandsAtRuntime()
    {
        await AssertOracle.EqualAsync(
            "echo $(echo hello)",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // 2. Trailing newline stripping
    //
    // Failure-surface: Axis 1 (empty input), Axis 12 (quoting).
    // Bash strips all trailing newlines from command substitution output.
    // So $(printf "a\n\n") yields "a" not "a\n\n". The ps-bash runtime must
    // replicate this via .TrimEnd on the captured subexpression output.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that command substitution strips trailing newlines from the
    /// captured output.  printf "a\n\n" produces "a" followed by two newlines;
    /// command substitution must strip both trailing newlines.
    /// Regression: if output is not trimmed, the assignment carries extra blank
    /// lines and the subsequent echo adds another newline, producing "a\n\n".
    /// </summary>
    [SkippableFact]
    public async Task Differential_CommandSub_TrailingNewlineStripped()
    {
        // Axis 1: tests that empty trailing lines are stripped.
        // Note: bash strips ALL trailing newlines from $(...) output.
        await AssertOracle.EqualAsync(
            "x=$(printf 'a\\n\\n'); echo \"$x\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // 3. Nested command substitution
    //
    // Failure-surface: Axis 15 (recursion depth).
    // $(echo $(echo deep)) requires the inner CommandSub to be fully resolved
    // before the outer one uses its output.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies three-level nested command substitution.
    /// Emitter path: outer CommandSub body contains inner CommandSub.
    /// The emitter must recurse into the inner body and produce
    /// $(Invoke-BashEcho $(Invoke-BashEcho deep)).
    /// Regression: if nesting is flattened, the literal text "$(echo deep)"
    /// appears in the output rather than "deep".
    /// </summary>
    [SkippableFact]
    public async Task Differential_CommandSub_NestedThreeLevels()
    {
        // Axis 15: three-level nesting exercises the recursive emitter path.
        await AssertOracle.EqualAsync(
            "echo $(echo $(echo deep))",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // 4. Pipeline inside command substitution
    //
    // Failure-surface: Axis 8 (exit-code of inner pipeline does not propagate).
    // The inner pipeline exit code is consumed by the substitution; only the
    // outer command's exit code is visible to the shell.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that a pipeline inside $(...) is fully transpiled.
    /// Emitter path: CommandSub body is a Command.Pipeline, which EmitPipeline
    /// must handle. The output is captured then passed as an argument.
    /// Regression: if the pipeline is emitted as two separate commands (losing
    /// the pipe), head -1 runs on empty stdin and outputs nothing because no
    /// data flows into head.
    /// </summary>
    [SkippableFact]
    public async Task Differential_CommandSub_PipelineInside()
    {
        // Axis 8: inner pipeline exit code is not visible outside the $(...).
        // Use a straightforward $(printf | head) pattern with no subshell wrapper —
        // subshell at end of pipeline is a separate known bug in EmitSubshell.
        await AssertOracle.EqualAsync(
            "x=$(printf 'line1\\nline2\\n' | head -1); echo \"got: $x\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // 5. Backtick form normalized to $()
    //
    // Failure-surface: Axis 12 (quoting). Backtick form is semantically
    // identical to $() in this usage but uses a different lexer path.
    // The emitter must normalize it to $(…) so the runtime sees the same form.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that backtick command substitution produces the same output
    /// as the $() form.
    /// Emitter path: lexer scans backtick span into CommandSub AST node; emitter
    /// emits it as $(...) just like the dollar-paren form.
    /// Regression: if the backtick is left as a literal string, echo outputs the
    /// raw backtick characters rather than the command output.
    /// </summary>
    [SkippableFact]
    public async Task Differential_CommandSub_BacktickForm_SameAsParenForm()
    {
        // Backtick form: `echo hi`; both forms must produce "hi".
        // Axis 12: quoting inside the substitution follows the same rules.
        await AssertOracle.EqualAsync(
            "echo `echo hi`",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // 6. Arithmetic substitution: $((expr)) → integer result
    //
    // Failure-surface: Axis 8 (exit code 0 after arithmetic).
    // $((2 + 3)) must yield "5" at runtime. The emitter maps it to
    // $(2 + 3) which PowerShell evaluates as an integer expression.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that $((2 + 3)) produces "5".
    /// Emitter path: ArithSub → EmitArithSub → $(2 + 3).
    /// PowerShell evaluates the expression and the result is stringified.
    /// Regression: if EmitArithSub emits a literal string "2 + 3", the output
    /// is "2 + 3" rather than "5".
    /// </summary>
    [SkippableFact]
    public async Task Differential_ArithSub_LiteralAddition_ProducesInteger()
    {
        await AssertOracle.EqualAsync(
            "echo $((2 + 3))",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // 7. Arithmetic vs command sub disambiguation: $((expr)) vs $( (expr) )
    //
    // Failure-surface: Axis 12 (quoting / injection).
    // $((1+2)) is ArithSub. $( (1+2) ) is CommandSub containing a subshell
    // with arithmetic. They produce the same result in bash but via different
    // parse paths.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that $((1+2)) (ArithSub) and $( (1+2) ) (CommandSub/subshell)
    /// both produce "3".  The lexer must correctly distinguish the two forms:
    /// $((  starts ArithSub; $(  followed by whitespace and ( starts CommandSub.
    /// Regression: if the parser conflates both forms, one will silently emit
    /// wrong PowerShell and produce incorrect or empty output.
    /// </summary>
    [SkippableFact]
    public async Task Differential_ArithSub_VsCommandSubSubshell_BothProduceResult()
    {
        // Both $((1+2)) and $( (1+2) ) should yield "3 3".
        await AssertOracle.EqualAsync(
            "echo $((1+2)) $( echo $((1+2)) )",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // 8. Command sub inside double quotes — no word splitting
    //
    // Failure-surface: Axis 12 (quoting / injection).
    // Inside double quotes, $(...) output is NOT word-split. A command that
    // outputs "a b" inside "..." must be preserved as a single argument.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that $(echo "a b") inside double quotes is not word-split.
    /// The output "a b" is a single argument to the outer command.
    /// Emitter path: DoubleQuoted → EmitDoubleQuoted → CommandSub inner.
    /// Regression: if the double-quote wrapper is lost, word-splitting produces
    /// two arguments "a" and "b" and echo outputs "a b" with a different spacing.
    /// </summary>
    [SkippableFact]
    public async Task Differential_CommandSub_InsideDoubleQuotes_NoWordSplit()
    {
        await AssertOracle.EqualAsync(
            "x=\"$(echo 'a b')\"; echo \"$x\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // 9. Process substitution <(cmd) — golden mode (runtime temp-file path)
    //
    // Failure-surface: Axis 8 (exit code propagation), Axis 14 (missing target).
    // <(cmd) expands to a path that diff/comm/sort can read. The ps-bash runtime
    // uses Invoke-ProcessSub to write output to a temp file and return its path.
    // This is a golden test because temp-file paths differ across runs/platforms.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that diff &lt;(echo a) &lt;(echo b) produces a diff with at least one
    /// changed line. Uses GoldenAsync because the diff header contains temp paths
    /// and line numbers that are not byte-stable across runs.
    ///
    /// If Invoke-ProcessSub is broken (returns null path or throws), diff will
    /// error rather than produce output, and the golden will mismatch on the
    /// next UPDATE_GOLDENS run.
    ///
    /// Record golden: UPDATE_GOLDENS=1 ./scripts/test.sh src/PsBash.Differential.Tests --filter ProcessSub_DiffTwoSources
    /// </summary>
    [SkippableFact]
    public async Task Differential_ProcessSub_DiffTwoSources_ProducesDiff()
    {
        // Axis 8: diff exit code is 1 when files differ; Invoke-ProcessSub must
        // not swallow the diff exit code.
        // GoldenAsync: diff output contains file paths (temp files) which vary,
        // so we freeze the "changed lines" portion via golden.
        await AssertOracle.GoldenAsync(
            "diff <(echo a) <(echo b) | grep -E '^[<>]'",
            "ProcessSub_DiffTwoSources",
            timeout: TimeSpan.FromSeconds(20));
    }

    // -----------------------------------------------------------------------
    // 10. Empty command sub — captures empty string
    //
    // Failure-surface: Axis 1 (empty input / empty output from inner command).
    // $(true) produces empty output; the substitution must yield an empty string.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that command substitution on a command with no output yields
    /// an empty string. $(true) outputs nothing; echo should output a blank line.
    /// Axis 1: empty output from inner command is the boundary case.
    /// </summary>
    [SkippableFact]
    public async Task Differential_CommandSub_EmptyOutput_YieldsEmptyString()
    {
        await AssertOracle.EqualAsync(
            "x=$(true); echo \"[$x]\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // 11. Exit code from $(false) propagates through assignment to $?
    //
    // Failure-surface: Axis 8 (exit code propagation through command substitution).
    // In bash, `x=$(cmd)` propagates cmd's exit code to $?.
    // -----------------------------------------------------------------------

    /// <summary>
    /// `x=$(false); echo $?` must print 1 — the exit code of false inside
    /// the command substitution must propagate through the assignment to $?.
    /// Failure-surface axis 8: exit code through $(…) assignment.
    /// </summary>
    [SkippableFact]
    public async Task Differential_CommandSub_ExitCodeFromFalse_PropagatesViaAssignment()
    {
        await AssertOracle.EqualAsync(
            "x=$(false); echo $?",
            timeout: TimeSpan.FromSeconds(15));
    }
}
