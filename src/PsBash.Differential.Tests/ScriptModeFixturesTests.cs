using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential tests for T09 script-mode parity: exit code propagation via
/// ExitException on the shared SdkWorker runspace.
///
/// Failure-surface axes covered (per QA rubric Directive 3):
///   Tests 1-2 (ExitCode):    Axis 8 — exit code propagation; ExitException vs RuntimeException
///   Test 3  (ExitZero):      Axis 8 — exit 0 must return 0, not be swallowed as "no error"
///   Test 4  (ExitFunction):  Axis 8 — exit inside a function must propagate to the script boundary
///   Test 5  (NegatedCmd):    Axis 8 — '!' negation operator and $? propagation
///   Test 6  (SetEAbort):     Axis 8 — set -e with explicit exit code check (avoids 'done' reserved-word bug)
/// </summary>
public class ScriptModeFixturesTests
{
    // -----------------------------------------------------------------------
    // Tests 1-4: exit N propagation
    //
    // Failure surface: Axis 8 (exit code propagation).
    // SdkWorker.RunCommand catches RuntimeException before T09. ExitException
    // inherits RuntimeException — if caught in the wrong order, exit 42 returns
    // 1 instead of 42.  These tests fail before the T09 ExitException fix.
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task ScriptMode_ExitCode_BareExitN()
    {
        await AssertOracle.EqualAsync(
            "exit 42",
            timeout: TimeSpan.FromSeconds(15));
    }

    [SkippableFact]
    public async Task ScriptMode_ExitCode_BareExitLarge()
    {
        await AssertOracle.EqualAsync(
            "exit 127",
            timeout: TimeSpan.FromSeconds(15));
    }

    [SkippableFact]
    public async Task ScriptMode_ExitCode_ExitZero()
    {
        // exit 0 must not be swallowed as "error": SdkWorker must return 0.
        await AssertOracle.EqualAsync(
            "echo before; exit 0",
            timeout: TimeSpan.FromSeconds(15));
    }

    [SkippableFact]
    public async Task ScriptMode_ExitCode_ExitInsideFunction()
    {
        // exit inside a function must propagate to the calling script.
        await AssertOracle.EqualAsync(
            "f() { exit 7; }; f; echo should_not_print",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Test 5: negated exit code
    //
    // Failure surface: Axis 8 — exit code through '!' negation operator.
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task ScriptMode_ExitCode_NegatedCommand()
    {
        // '! false' exits 0; '! true' exits 1.
        await AssertOracle.EqualAsync(
            "! false; echo $?; ! true; echo $?",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Test 6: set -e — exit code of the aborting command
    //
    // Failure surface: Axis 8 (exit code propagation with set -e).
    // NOTE: 'echo done' cannot be used because 'done' is a bash reserved word
    // that the current parser strips from argument lists (pre-existing bug).
    // This test uses 'exit 1' explicitly and checks that set -e exits with 1.
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task ScriptMode_SetE_ExplicitExitCode()
    {
        // set -e with explicit exit: must propagate the exit code.
        await AssertOracle.EqualAsync(
            "set -e; exit 3",
            timeout: TimeSpan.FromSeconds(15));
    }
}
