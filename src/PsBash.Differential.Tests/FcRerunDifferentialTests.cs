using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// <c>fc</c> and <c>history</c> over the session history list (<c>InvokeBashHistoryCommand.Entries</c>), against bash 5.2.
/// <c>fc -s</c> / <c>fc -e -</c> re-run the chosen entry through the one run-time call of bash text
/// (<c>PsBuild.RunBashTextCall</c>, as eval), in the caller's scope. Opening an editor is an intentional difference, not
/// tested here.
/// </summary>
public class FcRerunDifferentialTests
{
    private static Task Same(string script) => AssertOracle.EqualAsync(script, TimeSpan.FromSeconds(30));

    [SkippableFact]
    public Task FcList_RangesMatchBash() => Same(
        "for c in a b c d e; do history -s \"echo $c\"; done\n"
        + "fc -l 5 1; echo \"--\"; fc -ln 5; echo \"--\"; fc -ln 6 2; echo \"--\"; fc -lnr 2 3; echo \"--\"; fc -ln -2\n");

    [SkippableFact]
    public Task FcRerun_SubstitutesRunsAndReplacesTheEntry() => Same(
        "history -s 'echo x1'; history -s 'echo x2'\n"
        + "fc -s 9; echo \"s9=$?\"\n"
        + "fc -s x=Q 1; echo \"sxq=$?\"\n"
        + "fc -e - Q=R; echo \"e=$?\"\n"
        + "history\n");

    [SkippableFact]
    public Task FcRerun_RunsInTheCallerAndPipes() => Same(
        "history -s 'v=7'; f() { fc -s 2>/dev/null; echo \"v=$v\"; }; f\n"
        + "history -s 'echo pipe'; fc -s 2>/dev/null | tr a-z A-Z\n"
        + "history -s 'false'; fc -s 2>/dev/null; echo \"false=$?\"\n"
        + "history -c; fc -s; echo \"empty=$?\"\n"
        + "c=fc; history -s 'echo dyn'; $c -s 2>/dev/null; echo \"dyn=$?\"\n");

    // REGRESSION (review of the port): the re-run had its own copy of eval's call site, without the return check, the loop
    // mode or the function's locals.
    [SkippableFact]
    public Task FcRerun_ReturnBreakAndLocalsActInTheCaller() => Same(
        "history -s 'return 7'; g() { fc -s 2>/dev/null; echo notreached; }; g; echo \"st=$?\"\n"
        + "history -s break; for i in 1 2 3; do echo \"$i\"; fc -s 2>/dev/null; done\n"
        + "history -s continue; for i in 1 2; do fc -s 2>/dev/null; echo \"no$i\"; done; echo \"c=$i\"\n"
        + "history -s 'v=2'; v=0; h() { local v=1; fc -s 2>/dev/null; echo \"in=$v\"; }; h; echo \"out=$v\"\n"
        + "history -s 'return 5'; k() { fc -s; echo notreached; }; k 2>/dev/null; echo \"bare=$?\"\n"
        + "k2() { eval 'return 4' 2>/dev/null; echo notreached; }; k2; echo \"ev=$?\"\n"
        + "c='return 3'; k3() { eval \"$c\" >/dev/null; echo notreached; }; k3; echo \"evr=$?\"\n");

    // A pipe stage is a subshell: its change to the history list is lost (bash 5.2).
    [SkippableFact]
    public Task FcRerun_InAPipeStage_LeavesTheListAlone() => Same(
        "history -c; history -s 'echo seven'; fc -s 2>/dev/null | cat; history\n"
        + "( history -s sub ); history -s piped | cat; history\n");

    // set -o history: a listing's -0 is this fc line itself (bash's real_last); 0 is the entry before it.
    [SkippableFact]
    public Task FcList_MinusZero_IsTheCurrentLine() => Same(
        "set -o history\necho one\necho two\nfc -l -0\nfc -l 0\nfc -l -2 -0\n");

    [SkippableFact]
    public Task HistoryPrint_ChainedGlobalAndRepeatedModifiers() => Same(
        "history -c; history -s 'echo three tee'\n"
        + "history -p '!e:s/e/E/:s/c/C/' '!!:gs/e/E/' '!!:s/e/[&]/' '!!:g&' '!!:s' '!!:s/e/\\&x/' '!!:$:s/e/E/'; echo \"p=$?\"\n"
        + "history -p '!!:gs/t/T/:&'; echo \"rep=$?\"\n");

    [SkippableFact]
    public Task History_DeleteRangesAndFileOptions() => Same(
        "for c in a b c d e; do history -s \"$c\"; done\n"
        + "history -d 2-3; echo \"r=$?\"; history\n"
        + "history -d 3-1; echo \"bad=$?\"\n"
        + "history -a -r; echo \"ar=$?\"\n"
        + "history -c; history -s 'echo e'; history -p '!!' '!e:s/e/Q/' '!nope'; echo \"p=$?\"\n");
}
