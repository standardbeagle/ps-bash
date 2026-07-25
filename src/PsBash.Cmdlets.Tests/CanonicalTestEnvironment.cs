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
/// A test that needs the OPPOSITE (asserting the NO_COLOR path is honored) must set
/// the variable itself inside a try/finally; none do today.
/// </summary>
internal static class CanonicalTestEnvironment
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // Unset, not "0": the cmdlets treat any non-empty value as opt-out.
        Environment.SetEnvironmentVariable("NO_COLOR", null);
    }
}
