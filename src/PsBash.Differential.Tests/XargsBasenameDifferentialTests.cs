using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle for xargs and basename (R23b). Each script runs in real
/// bash AND ps-bash and the canonicalized stdout/stderr/exit are diffed.
///
/// xargs coverage: the default command is <c>echo</c>; <c>-I</c>/<c>-i</c>
/// replacement mode reads whole lines (internal blanks preserved); the default
/// tokenizer honors quotes and backslash escapes; and the GNU exit-status
/// aggregate (123 = child exited 1..125, 124 = child exited 255) is reproduced.
/// Exit-status cases silence stderr with <c>2&gt;/dev/null</c> so the diff is on
/// stdout + status only: the human-readable message text is not part of GNU's
/// guaranteed interface, and ps-bash's cmdlet error stream is not suppressible
/// through a nested xargs invocation (a pre-existing, out-of-scope host gap).
///
/// basename coverage: the two-operand <c>NAME [SUFFIX]</c> form, <c>-a</c>, and
/// <c>-s</c> (which implies <c>-a</c>).
///
/// The exit-status cases use <c>awk 'BEGIN{exit N}'</c> as the child because
/// ps-bash has no <c>sh</c>; awk is available on both sides, both honor the
/// <c>exit N</c> status, and a BEGIN-only program emits no stderr in ps-bash.
///
/// Exit statuses 125/126/127 are covered by the direct cmdlet tests
/// (<c>InvokeBashXargsCommandTests</c>): 127 (command not found) and 126
/// (cannot-run) are xargs's own diagnostics, which ps-bash emits on an error
/// stream <c>2&gt;/dev/null</c> cannot suppress inside xargs — not
/// differential-matchable without a host-level fix outside this task's scope.
/// </summary>
public class XargsBasenameDifferentialTests
{
    // 30 s, not the 15 s used elsewhere: every ps-bash spawn here starts a
    // fresh per-invocation host (PSBASH_PER_INVOCATION=1), ~8 s cold on a
    // loaded Windows box. At 15 s a 2-way-parallel run intermittently times
    // out on startup alone, which reads as a false regression.
    private static Task Eq(string script) =>
        AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(30));

    // ---- xargs: default command ----

    [SkippableFact] public Task Xargs_NoCommand_DefaultsToEcho() =>
        Eq("printf 'a b c\\n' | xargs");

    [SkippableFact] public Task Xargs_NoCommand_MultipleLines_DefaultsToEcho() =>
        Eq("printf 'a\\nb c\\n' | xargs");

    // ---- xargs: -I / -i read whole lines ----

    [SkippableFact] public Task Xargs_DashI_WholeLinePreservesBlanks() =>
        Eq("printf 'a b\\nc d\\n' | xargs -I{} echo '[{}]'");

    [SkippableFact] public Task Xargs_DashLowerI_DefaultBraces() =>
        Eq("printf 'a b\\nc\\n' | xargs -i echo '[{}]'");

    [SkippableFact] public Task Xargs_DashLowerIAttached() =>
        Eq("printf 'a b\\n' | xargs -iX echo '[X]'");

    [SkippableFact] public Task Xargs_DashI_BatchingStillWholeLine() =>
        Eq("printf 'x y\\n' | xargs -I{} -n 1 echo '[{}]'");

    // ---- xargs: default tokenizer quote handling ----

    [SkippableFact] public Task Xargs_Default_DoubleQuotedTokenStaysOneItem() =>
        Eq("printf 'a \"b c\" d\\n' | xargs -n 1 echo");

    [SkippableFact] public Task Xargs_Default_BackslashEscapedSpace() =>
        Eq("printf 'a\\\\ b\\n' | xargs -n 1 echo");

    // ---- xargs: exit-status aggregate ----

    [SkippableFact] public Task Xargs_ExitCode_ChildFailure_Is123() =>
        Eq("printf 'x\\n' | xargs -n 1 awk 'BEGIN{exit 3}' 2>/dev/null; echo $?");

    [SkippableFact] public Task Xargs_ExitCode_ChildExit255_Is124() =>
        Eq("printf 'x\\n' | xargs -n 1 awk 'BEGIN{exit 255}' 2>/dev/null; echo $?");

    // ---- basename: NAME [SUFFIX], -a, -s ----

    [SkippableFact] public Task Basename_TwoOperands_Suffix() =>
        Eq("basename /a/b.txt .txt");

    [SkippableFact] public Task Basename_TwoOperands_SuffixNotPresent() =>
        Eq("basename /a/b.txt .log");

    [SkippableFact] public Task Basename_TwoOperands_SuffixEqualsName() =>
        Eq("basename /a/b.txt b.txt");

    [SkippableFact] public Task Basename_DashA() =>
        Eq("basename -a /a/b.txt /x/y.txt");

    [SkippableFact] public Task Basename_DashS_ImpliesAll() =>
        Eq("basename -s .txt /a/b.txt /x/y.txt");
}
