using System.Runtime.CompilerServices;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Normalizes the ambient environment for this in-process test assembly before any
/// test runs (qa-rubric Directive 6 — determinism harness: a test must not depend on
/// the developer's or CI runner's inherited environment).
///
/// The cmdlets under test read process environment variables directly, so an
/// inherited value silently changes what they emit. <c>NO_COLOR</c> is the live
/// example: <c>FormatStyledCommand</c> / <c>StyledInteractiveSession</c> honor it by
/// switching Spectre to <c>ColorSystemSupport.NoColors</c>. Any shell that exports
/// <c>NO_COLOR=1</c> (Claude Code's Bash tool does) therefore turned the 12
/// ANSI-asserting <c>Format-Styled</c> tests red on a developer box while CI stayed
/// green — an environment artifact, not a product regression.
///
/// A module initializer (rather than a fixture or a per-test save/clear/restore) is
/// deliberate: it runs once, before the first test, so there is no window where a
/// parallel test class observes a half-applied environment. Environment variables are
/// process-global — mutating them mid-run is inherently racy, which is exactly what
/// this avoids.
///
/// A test that needs the OPPOSITE (asserting the NO_COLOR path is honored) sets the
/// variable through <c>PsBash.Testing.ProcessEnv</c> in <see cref="ProcessEnvironmentCollection"/>.
/// </summary>
internal static class CanonicalTestEnvironment
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // Nested ps-bash children (awk/sed spawning commands) inherit these: a launching shell's compact
        // output, named session or pinned endpoint changes every result. Harness knobs are kept.
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            if (e.Key is not string name || !name.StartsWith("PSBASH_", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.StartsWith("PSBASH_TEST_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("PSBASH_ORACLE_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("PSBASH_TMAN_", StringComparison.OrdinalIgnoreCase)) continue;
            Environment.SetEnvironmentVariable(name, null);
        }

        // Unset, not "0": the cmdlets treat any non-empty value as opt-out.
        Environment.SetEnvironmentVariable("NO_COLOR", null);

        // grep's colour tests assert GNU's default SGR sequences; an inherited palette would change every one.
        Environment.SetEnvironmentVariable("GREP_COLORS", null);
        Environment.SetEnvironmentVariable("GREP_COLOR", null);

        // Tests must never read the real console: run from a terminal, an `rm -i` whose answer a test
        // did not supply would block on the keyboard (under tman stdin is not a console, so it hid).
        // A test that exercises a prompt feeds its answer through the pipeline / brace-group queue.
        Environment.SetEnvironmentVariable(StdinLineSource.NoTtyEnvVar, "1");
    }
}
