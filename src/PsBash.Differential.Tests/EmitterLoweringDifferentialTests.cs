using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle tests for four emitter/lowering gaps fixed together
/// (R16):
///   (1) <c>while IFS=... read</c> and a bare <c>IFS=... read</c> ignored the
///       <c>IFS=</c> env prefix and always split on whitespace.
///   (2) a brace group used as a pipe target (<c>cmd | { read y; ...; }</c>) was
///       emitted as <c>&amp; { }</c> with no <c>$input</c> forwarding, so the
///       body saw no stdin.
///   (3) <c>[[ $s =~ (a)(b) ]]</c> matched but never populated
///       <c>BASH_REMATCH</c>.
///   (4) <c>trap ... EXIT</c> only fired from the eval path; a plain
///       <c>-c</c>/script run never fired it.
///
/// Each case runs in real bash AND ps-bash and diffs bytes (stdout / stderr /
/// exit code).
/// </summary>
public class EmitterLoweringDifferentialTests
{
    // -----------------------------------------------------------------------
    // (1) IFS in while-read and read
    // -----------------------------------------------------------------------

    /// <summary>
    /// `while IFS=: read -r a b` must split each line on ':'. Before the fix the
    /// fast path hard-coded `-split '\s+'`, so `a` got the whole line and `b` was empty.
    /// </summary>
    [SkippableFact]
    public async Task Differential_WhileRead_IfsColonSplitsFields()
    {
        await AssertOracle.EqualAsync(
            "printf 'root:x\\nbin:y\\n' | while IFS=: read -r a b; do echo \"$a-$b\"; done",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// A bare `IFS=, read -r a b c` from a here-string must split on ','. The read
    /// cmdlet's multi-variable path split on `\s+` regardless of IFS.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Read_IfsCommaSplitsFields()
    {
        await AssertOracle.EqualAsync(
            "IFS=, read -r a b c <<< '1,2,3'; echo \"$a|$b|$c\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // (2) brace group in a pipeline
    // -----------------------------------------------------------------------

    /// <summary>
    /// `echo hi | { read y; echo y=$y; }` — the brace group is the pipe target and
    /// must receive the piped line on stdin.
    /// </summary>
    [SkippableFact]
    public async Task Differential_BraceGroupPipeTarget_ReceivesStdin()
    {
        await AssertOracle.EqualAsync(
            "echo hi | { read y; echo y=$y; }",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // (3) BASH_REMATCH
    // -----------------------------------------------------------------------

    /// <summary>
    /// `[[ $s =~ (a)(b) ]]` must populate `${BASH_REMATCH[1]}` / `[2]`.
    /// </summary>
    [SkippableFact]
    public async Task Differential_RegexMatch_PopulatesBashRematch()
    {
        await AssertOracle.EqualAsync(
            "s=ab; [[ $s =~ (a)(b) ]]; echo \"${BASH_REMATCH[1]}${BASH_REMATCH[2]}\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // (4) EXIT trap
    // -----------------------------------------------------------------------

    /// <summary>
    /// `trap 'echo bye' EXIT` fires on a normal end of script.
    /// </summary>
    [SkippableFact]
    public async Task Differential_TrapExit_FiresOnNormalEnd()
    {
        await AssertOracle.EqualAsync(
            "trap 'echo bye' EXIT; echo main",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// `trap 'echo bye' EXIT` fires on an explicit `exit N`, and the process exits N.
    /// </summary>
    [SkippableFact]
    public async Task Differential_TrapExit_FiresOnExitN()
    {
        await AssertOracle.EqualAsync(
            "trap 'echo bye' EXIT; echo main; exit 3",
            timeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// `trap 'echo bye' EXIT` fires when errexit aborts the script.
    /// </summary>
    [SkippableFact]
    public async Task Differential_TrapExit_FiresUnderErrexit()
    {
        await AssertOracle.EqualAsync(
            "set -e; trap 'echo bye' EXIT; false; echo after",
            timeout: TimeSpan.FromSeconds(15));
    }
}
