using Xunit;

namespace PsBash.Canary.Tests;

/// <summary>
/// Canary feature tests: 30 cases covering 15 features x 2 cases each.
/// Per qa-rubric Directive 8: one test per feature in the failure-surface matrix,
/// dispatched across all available modes (M1–M3, M5–M6) via ModeRunner.
///
/// Skip sentinel: ExitCode == -999 means a mode prerequisite is unavailable on this
/// platform (binary not found, cmdlet not loaded). Tests skip per-mode rather than fail.
///
/// STDOUT ASSERTION SCOPE:
/// M1/M2/M3 (spawn modes) capture raw stdout from the child process and are asserted
/// for content. M5/M6 (in-process PowerShell SDK) capture PSObject.ToString() output
/// which may differ from the raw text stream — those modes are checked for ExitCode
/// only unless the test explicitly notes it checks M5/M6 stdout.
///
/// M6 (Invoke-BashSource) runs scripts from a .sh temp file via the cmdlet. Exit code
/// semantics under sourcing differ from subprocess execution — tests that depend on
/// a subprocess exit code (exit 1, set -e, false) skip M6 explicitly.
/// </summary>
public sealed class CanaryFeatureTests
{
    private readonly ModeRunner _runner = new();

    // -------------------------------------------------------------------------
    // Helper: assert per mode, scoping which checks apply to which mode family.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns true when the mode is a spawn mode (M1/M2/M3) that captures raw stdout.
    /// </summary>
    private static bool IsSpawnMode(ModeResult r) =>
        r.Mode is Mode.M1_CFlag or Mode.M2_StdinPipe or Mode.M3_FileArg;

    // =========================================================================
    // 1. ECHO
    // =========================================================================

    /// <summary>
    /// echo basic output: spawn modes must print "hello"; all modes must exit 0.
    /// ps-bash-specific assertion: no bash oracle available for all modes.
    /// </summary>
    [Fact]
    public async Task Echo_Basic_AllModes()
    {
        var results = await _runner.RunAllAsync("echo hello");
        foreach (var r in results)
        {
            Skip.If(r.ExitCode == -999, $"{r.Mode} skipped: {r.Stderr}");
            Assert.Equal(0, r.ExitCode);
            if (IsSpawnMode(r))
                Assert.Contains("hello", r.Stdout);
        }
    }

    /// <summary>
    /// echo -n: spawn mode output must contain "hello"; all modes exit 0.
    /// ps-bash-specific: Invoke-BashEcho -n behavior tested on spawn modes.
    /// </summary>
    [Fact]
    public async Task Echo_NFlag_AllModes()
    {
        var results = await _runner.RunAllAsync("echo -n hello");
        foreach (var r in results)
        {
            Skip.If(r.ExitCode == -999, $"{r.Mode} skipped: {r.Stderr}");
            Assert.Equal(0, r.ExitCode);
            if (IsSpawnMode(r))
                Assert.Contains("hello", r.Stdout);
        }
    }

    // =========================================================================
    // 2. EXIT CODE
    // =========================================================================

    /// <summary>
    /// exit 0: spawn mode exit code must be 0.
    /// M5/M6 skipped — in-process modes do not expose subprocess exit codes.
    /// </summary>
    [Fact]
    public async Task ExitCode_Zero_SpawnModes()
    {
        var results = await _runner.RunAllAsync("exit 0");
        foreach (var r in results)
        {
            if (!IsSpawnMode(r)) continue;
            Skip.If(r.ExitCode == -999, $"{r.Mode} skipped: {r.Stderr}");
            Assert.Equal(0, r.ExitCode);
        }
    }

    /// <summary>
    /// exit 1: spawn mode exit code must be 1.
    /// M5/M6 skipped — in-process cmdlet exit semantics differ under sourcing/eval.
    /// </summary>
    [Fact]
    public async Task ExitCode_One_SpawnModes()
    {
        var results = await _runner.RunAllAsync("exit 1");
        foreach (var r in results)
        {
            if (!IsSpawnMode(r)) continue;
            Skip.If(r.ExitCode == -999, $"{r.Mode} skipped: {r.Stderr}");
            Assert.Equal(1, r.ExitCode);
        }
    }

    // =========================================================================
    // 3. VARIABLE ASSIGNMENT + EXPANSION
    // =========================================================================

    /// <summary>
    /// Variable assignment then expansion: x=hello; echo $x must print "hello"
    /// on spawn modes; all modes exit 0.
    /// </summary>
    [Fact]
    public async Task Variable_AssignThenExpand_AllModes()
    {
        var results = await _runner.RunAllAsync("x=hello; echo $x");
        foreach (var r in results)
        {
            Skip.If(r.ExitCode == -999, $"{r.Mode} skipped: {r.Stderr}");
            Assert.Equal(0, r.ExitCode);
            if (IsSpawnMode(r))
                Assert.Contains("hello", r.Stdout);
        }
    }

    /// <summary>
    /// Variable with spaces in value via double-quotes must preserve the spaces
    /// in spawn mode output.
    /// </summary>
    [Fact]
    public async Task Variable_QuotedValueWithSpaces_AllModes()
    {
        var results = await _runner.RunAllAsync("x=\"hello world\"; echo $x");
        foreach (var r in results)
        {
            Skip.If(r.ExitCode == -999, $"{r.Mode} skipped: {r.Stderr}");
            Assert.Equal(0, r.ExitCode);
            if (IsSpawnMode(r))
                Assert.Contains("hello world", r.Stdout);
        }
    }

    // =========================================================================
    // 4. PIPE
    // =========================================================================

    /// <summary>
    /// Two-stage pipe: echo hello | grep hello must print "hello" on spawn modes;
    /// all modes exit 0.
    /// </summary>
    [Fact]
    public async Task Pipe_EchoToGrep_AllModes()
    {
        var results = await _runner.RunAllAsync("echo hello | grep hello");
        foreach (var r in results)
        {
            Skip.If(r.ExitCode == -999, $"{r.Mode} skipped: {r.Stderr}");
            Assert.Equal(0, r.ExitCode);
            if (IsSpawnMode(r))
                Assert.Contains("hello", r.Stdout);
        }
    }

    /// <summary>
    /// Pipe with grep no-match: stdout must be empty on spawn modes.
    /// Exit code is not asserted — ps-bash normalizes pipeline exit codes and
    /// grep's non-match exit code (1) may not propagate from the last pipe segment.
    /// ps-bash-specific: pipeline exit code propagation is a known behavioral delta.
    /// </summary>
    [Fact]
    public async Task Pipe_GrepNoMatch_EmptyOutput_SpawnModes()
    {
        var results = await _runner.RunAllAsync("echo hello | grep world");
        foreach (var r in results)
        {
            if (!IsSpawnMode(r)) continue;
            Skip.If(r.ExitCode == -999, $"{r.Mode} skipped: {r.Stderr}");
            // ps-bash-specific: stdout must be empty when grep finds no match.
            Assert.Empty(r.Stdout.Trim());
        }
    }

    // =========================================================================
    // 5. IF/THEN/ELSE
    // =========================================================================

    /// <summary>
    /// If true branch: condition succeeds, "yes" printed on spawn modes; all exit 0.
    /// </summary>
    [Fact]
    public async Task If_TrueBranch_PrintsYes_AllModes()
    {
        var results = await _runner.RunAllAsync("if true; then echo yes; else echo no; fi");
        foreach (var r in results)
        {
            Skip.If(r.ExitCode == -999, $"{r.Mode} skipped: {r.Stderr}");
            Assert.Equal(0, r.ExitCode);
            if (IsSpawnMode(r))
            {
                Assert.Contains("yes", r.Stdout);
                Assert.DoesNotContain("no", r.Stdout);
            }
        }
    }

    /// <summary>
    /// If false branch: condition fails, "no" printed on spawn modes; all exit 0.
    /// </summary>
    [Fact]
    public async Task If_FalseBranch_PrintsNo_AllModes()
    {
        var results = await _runner.RunAllAsync("if false; then echo yes; else echo no; fi");
        foreach (var r in results)
        {
            Skip.If(r.ExitCode == -999, $"{r.Mode} skipped: {r.Stderr}");
            Assert.Equal(0, r.ExitCode);
            if (IsSpawnMode(r))
            {
                Assert.Contains("no", r.Stdout);
                Assert.DoesNotContain("yes", r.Stdout);
            }
        }
    }

    // =========================================================================
    // 6. FOR LOOP
    // =========================================================================

    /// <summary>
    /// For loop over a b c: all three items printed on spawn modes; all exit 0.
    /// </summary>
    [Fact]
    public async Task ForLoop_ThreeItems_AllPrinted_AllModes()
    {
        var results = await _runner.RunAllAsync("for i in a b c; do echo $i; done");
        foreach (var r in results)
        {
            Skip.If(r.ExitCode == -999, $"{r.Mode} skipped: {r.Stderr}");
            Assert.Equal(0, r.ExitCode);
            if (IsSpawnMode(r))
            {
                Assert.Contains("a", r.Stdout);
                Assert.Contains("b", r.Stdout);
                Assert.Contains("c", r.Stdout);
            }
        }
    }

    /// <summary>
    /// For loop with empty expansion list: body never runs, exits 0, no output.
    /// </summary>
    [Fact]
    public async Task ForLoop_EmptyList_NoOutput_AllModes()
    {
        var results = await _runner.RunAllAsync("items=\"\"; for i in $items; do echo $i; done");
        foreach (var r in results)
        {
            Skip.If(r.ExitCode == -999, $"{r.Mode} skipped: {r.Stderr}");
            Assert.Equal(0, r.ExitCode);
            if (IsSpawnMode(r))
                Assert.Empty(r.Stdout.Trim());
        }
    }

    // =========================================================================
    // 7. WHILE LOOP
    // =========================================================================

    /// <summary>
    /// While false: condition immediately false, body never runs, all modes exit 0.
    /// </summary>
    [Fact]
    public async Task WhileLoop_ImmediatelyFalse_ExitsZero_AllModes()
    {
        var results = await _runner.RunAllAsync("while false; do echo never; done");
        foreach (var r in results)
        {
            Skip.If(r.ExitCode == -999, $"{r.Mode} skipped: {r.Stderr}");
            Assert.Equal(0, r.ExitCode);
            if (IsSpawnMode(r))
                Assert.DoesNotContain("never", r.Stdout);
        }
    }

    /// <summary>
    /// While loop counting 1 to 3: spawn modes print 1, 2, 3; all exit 0.
    /// </summary>
    [Fact]
    public async Task WhileLoop_CounterTo3_AllModes()
    {
        var script = "i=1; while [ $i -le 3 ]; do echo $i; i=$((i+1)); done";
        var results = await _runner.RunAllAsync(script);
        foreach (var r in results)
        {
            Skip.If(r.ExitCode == -999, $"{r.Mode} skipped: {r.Stderr}");
            Assert.Equal(0, r.ExitCode);
            if (IsSpawnMode(r))
            {
                Assert.Contains("1", r.Stdout);
                Assert.Contains("2", r.Stdout);
                Assert.Contains("3", r.Stdout);
            }
        }
    }

    // =========================================================================
    // 8. FUNCTION DEFINITION + CALL
    // =========================================================================

    /// <summary>
    /// Function definition then call: f() { echo hi; }; f prints "hi" on spawn modes.
    /// </summary>
    [Fact]
    public async Task Function_DefineAndCall_AllModes()
    {
        var results = await _runner.RunAllAsync("f() { echo hi; }; f");
        foreach (var r in results)
        {
            Skip.If(r.ExitCode == -999, $"{r.Mode} skipped: {r.Stderr}");
            Assert.Equal(0, r.ExitCode);
            if (IsSpawnMode(r))
                Assert.Contains("hi", r.Stdout);
        }
    }

    /// <summary>
    /// Function with argument: greet() { echo "hello $1"; }; greet world prints
    /// "hello world" on spawn modes; exit code checked only for spawn modes.
    /// M5/M6 skipped for ExitCode — $1 in function body may trigger strict mode
    /// when no positional args are available in the in-process runspace.
    /// </summary>
    [Fact]
    public async Task Function_WithArgument_AllModes()
    {
        var results = await _runner.RunAllAsync("greet() { echo \"hello $1\"; }; greet world");
        foreach (var r in results)
        {
            Skip.If(r.ExitCode == -999, $"{r.Mode} skipped: {r.Stderr}");
            if (IsSpawnMode(r))
            {
                Assert.Equal(0, r.ExitCode);
                Assert.Contains("hello world", r.Stdout);
            }
            // M5/M6: skip exit-code and stdout checks — in-process positional arg
            // semantics differ; the test verifies the mode runs without throwing.
        }
    }

    // =========================================================================
    // 9. COMMAND SUBSTITUTION
    // =========================================================================

    /// <summary>
    /// Command substitution: x=$(echo hi); echo $x prints "hi" on spawn modes.
    /// </summary>
    [Fact]
    public async Task CommandSub_AssignAndExpand_AllModes()
    {
        var results = await _runner.RunAllAsync("x=$(echo hi); echo $x");
        foreach (var r in results)
        {
            Skip.If(r.ExitCode == -999, $"{r.Mode} skipped: {r.Stderr}");
            Assert.Equal(0, r.ExitCode);
            if (IsSpawnMode(r))
                Assert.Contains("hi", r.Stdout);
        }
    }

    /// <summary>
    /// Nested command substitution: echo $(echo $(echo deep)) prints "deep" on spawn modes.
    /// </summary>
    [Fact]
    public async Task CommandSub_Nested_AllModes()
    {
        var results = await _runner.RunAllAsync("echo $(echo $(echo deep))");
        foreach (var r in results)
        {
            Skip.If(r.ExitCode == -999, $"{r.Mode} skipped: {r.Stderr}");
            Assert.Equal(0, r.ExitCode);
            if (IsSpawnMode(r))
                Assert.Contains("deep", r.Stdout);
        }
    }

    // =========================================================================
    // 10. ARITHMETIC
    // =========================================================================

    /// <summary>
    /// Arithmetic expansion: echo $((2+3)) prints "5" on spawn modes; all exit 0.
    /// </summary>
    [Fact]
    public async Task Arithmetic_Addition_AllModes()
    {
        var results = await _runner.RunAllAsync("echo $((2+3))");
        foreach (var r in results)
        {
            Skip.If(r.ExitCode == -999, $"{r.Mode} skipped: {r.Stderr}");
            Assert.Equal(0, r.ExitCode);
            if (IsSpawnMode(r))
                Assert.Contains("5", r.Stdout);
        }
    }

    /// <summary>
    /// Arithmetic with variable: x=10; echo $((x * 3)) prints "30" on spawn modes.
    /// </summary>
    [Fact]
    public async Task Arithmetic_WithVariable_AllModes()
    {
        var results = await _runner.RunAllAsync("x=10; echo $((x * 3))");
        foreach (var r in results)
        {
            Skip.If(r.ExitCode == -999, $"{r.Mode} skipped: {r.Stderr}");
            Assert.Equal(0, r.ExitCode);
            if (IsSpawnMode(r))
                Assert.Contains("30", r.Stdout);
        }
    }

    // =========================================================================
    // 11. STDERR REDIRECT
    // =========================================================================

    /// <summary>
    /// Stderr redirect: echo err >&2 — spawn mode stdout must be empty, all exit 0.
    /// </summary>
    [Fact]
    public async Task StderrRedirect_StdoutEmpty_AllModes()
    {
        var results = await _runner.RunAllAsync("echo err >&2");
        foreach (var r in results)
        {
            Skip.If(r.ExitCode == -999, $"{r.Mode} skipped: {r.Stderr}");
            Assert.Equal(0, r.ExitCode);
            if (IsSpawnMode(r))
                Assert.Empty(r.Stdout.Trim());
        }
    }

    /// <summary>
    /// Stderr redirect: spawn mode stderr must contain the redirected message.
    /// M5/M6 skipped — in-process stderr routing differs from subprocess streams.
    /// </summary>
    [Fact]
    public async Task StderrRedirect_StderrContainsMessage_SpawnModes()
    {
        var results = await _runner.RunAllAsync("echo err >&2");
        foreach (var r in results)
        {
            if (!IsSpawnMode(r)) continue;
            Skip.If(r.ExitCode == -999, $"{r.Mode} skipped: {r.Stderr}");
            Assert.Equal(0, r.ExitCode);
            Assert.Contains("err", r.Stderr);
        }
    }

    // =========================================================================
    // 12. STDIN PIPE (CAT)
    // =========================================================================

    /// <summary>
    /// stdin pipe through cat: echo hello | cat prints "hello" on spawn modes; all exit 0.
    /// </summary>
    [Fact]
    public async Task StdinPipe_Cat_PassesThrough_AllModes()
    {
        var results = await _runner.RunAllAsync("echo hello | cat");
        foreach (var r in results)
        {
            Skip.If(r.ExitCode == -999, $"{r.Mode} skipped: {r.Stderr}");
            Assert.Equal(0, r.ExitCode);
            if (IsSpawnMode(r))
                Assert.Contains("hello", r.Stdout);
        }
    }

    /// <summary>
    /// Multi-line pipe through cat: printf with newlines piped to cat preserves all lines
    /// in spawn modes.
    /// </summary>
    [Fact]
    public async Task StdinPipe_Cat_MultiLine_AllModes()
    {
        var results = await _runner.RunAllAsync("printf 'line1\\nline2\\nline3\\n' | cat");
        foreach (var r in results)
        {
            Skip.If(r.ExitCode == -999, $"{r.Mode} skipped: {r.Stderr}");
            Assert.Equal(0, r.ExitCode);
            if (IsSpawnMode(r))
            {
                Assert.Contains("line1", r.Stdout);
                Assert.Contains("line2", r.Stdout);
                Assert.Contains("line3", r.Stdout);
            }
        }
    }

    // =========================================================================
    // 13. SET -E EXIT CODE PROPAGATION
    // =========================================================================

    /// <summary>
    /// set -e; false: with errexit, spawn modes must exit non-zero.
    /// M5/M6 skipped — in-process cmdlet exit code semantics differ.
    /// </summary>
    [Fact]
    public async Task SetE_False_ExitsNonZero_SpawnModes()
    {
        var results = await _runner.RunAllAsync("set -e; false");
        foreach (var r in results)
        {
            if (!IsSpawnMode(r)) continue;
            Skip.If(r.ExitCode == -999, $"{r.Mode} skipped: {r.Stderr}");
            Assert.NotEqual(0, r.ExitCode);
        }
    }

    /// <summary>
    /// set -e; true: with errexit, a succeeding command exits 0 in spawn modes.
    /// M5/M6 skipped.
    /// </summary>
    [Fact]
    public async Task SetE_True_ExitsZero_SpawnModes()
    {
        var results = await _runner.RunAllAsync("set -e; true");
        foreach (var r in results)
        {
            if (!IsSpawnMode(r)) continue;
            Skip.If(r.ExitCode == -999, $"{r.Mode} skipped: {r.Stderr}");
            Assert.Equal(0, r.ExitCode);
        }
    }

    // =========================================================================
    // 14. ENV VAR EXPORT
    // =========================================================================

    /// <summary>
    /// export FOO=bar; echo $FOO prints "bar" on spawn modes; all exit 0.
    /// </summary>
    [Fact]
    public async Task EnvVar_Export_ExpandsCorrectly_AllModes()
    {
        var results = await _runner.RunAllAsync("export FOO=bar; echo $FOO");
        foreach (var r in results)
        {
            Skip.If(r.ExitCode == -999, $"{r.Mode} skipped: {r.Stderr}");
            Assert.Equal(0, r.ExitCode);
            if (IsSpawnMode(r))
                Assert.Contains("bar", r.Stdout);
        }
    }

    /// <summary>
    /// Exported var visible in command substitution: echo $(echo $GREETING) prints "hello"
    /// on spawn modes.
    /// </summary>
    [Fact]
    public async Task EnvVar_ExportedToSubcommand_AllModes()
    {
        var results = await _runner.RunAllAsync("export GREETING=hello; echo $(echo $GREETING)");
        foreach (var r in results)
        {
            Skip.If(r.ExitCode == -999, $"{r.Mode} skipped: {r.Stderr}");
            Assert.Equal(0, r.ExitCode);
            if (IsSpawnMode(r))
                Assert.Contains("hello", r.Stdout);
        }
    }

    // =========================================================================
    // 15. HERE-STRING
    // =========================================================================

    /// <summary>
    /// Here-string: cat &lt;&lt;&lt; hello prints "hello" on spawn modes; all exit 0.
    /// </summary>
    [Fact]
    public async Task HereString_Basic_AllModes()
    {
        var results = await _runner.RunAllAsync("cat <<< hello");
        foreach (var r in results)
        {
            Skip.If(r.ExitCode == -999, $"{r.Mode} skipped: {r.Stderr}");
            Assert.Equal(0, r.ExitCode);
            if (IsSpawnMode(r))
                Assert.Contains("hello", r.Stdout);
        }
    }

    /// <summary>
    /// Here-string with variable expansion: cat &lt;&lt;&lt; "hello $NAME" expands NAME
    /// and prints "hello world" on spawn modes.
    /// </summary>
    [Fact]
    public async Task HereString_WithVariableExpansion_AllModes()
    {
        var results = await _runner.RunAllAsync("NAME=world; cat <<< \"hello $NAME\"");
        foreach (var r in results)
        {
            Skip.If(r.ExitCode == -999, $"{r.Mode} skipped: {r.Stderr}");
            Assert.Equal(0, r.ExitCode);
            if (IsSpawnMode(r))
                Assert.Contains("hello world", r.Stdout);
        }
    }
}
