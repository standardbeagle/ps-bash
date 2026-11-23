using System.Collections;
using System.Runtime.CompilerServices;

namespace PsBash.Host.Tests;

/// <summary>
/// Makes the suite independent of the shell that launches it, including ps-bash itself (the agent's Bash
/// tool, a nested shell, a developer shell with a named session or compact output on).
/// These tests drive a real host in-process, so any inherited <c>PSBASH_*</c> setting changes what they
/// observe: <c>PSBASH_COMPACT_OUTPUT</c> prepends a
/// summary line to every result, <c>PSBASH_SESSION</c> moves the endpoint. Everything is cleared except what
/// the test harness itself passes in; a test that needs a setting sets it in a <c>ProcessEnv</c> scope
/// (<see cref="ProcessEnvironmentCollection"/>).
/// </summary>
internal static class CanonicalTestEnvironment
{
    // Harness knobs (timeouts, oracle mode, tman slots) are inputs to the run, not ambient shell state.
    private static readonly string[] Preserved = ["PSBASH_TEST_", "PSBASH_ORACLE_", "PSBASH_TMAN_"];

    [ModuleInitializer]
    internal static void Initialize()
    {
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            if (e.Key is not string name || !name.StartsWith("PSBASH_", StringComparison.OrdinalIgnoreCase)) continue;
            if (Preserved.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            Environment.SetEnvironmentVariable(name, null);
        }
    }
}
