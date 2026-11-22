using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// <c>fc</c> and <c>history</c> over the session history list (<c>InvokeBashHistoryCommand.Entries</c>), against bash 5.2.
/// <c>fc -s</c> / <c>fc -e -</c> re-run the chosen entry through <c>PsBuild.FcCall</c> (the one runner of bash text,
/// <c>PsBuild.RunBashTextAtRunTime</c>), in the caller's scope. Opening an editor is an intentional difference, not tested
/// here.
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

    [SkippableFact]
    public Task History_DeleteRangesAndFileOptions() => Same(
        "for c in a b c d e; do history -s \"$c\"; done\n"
        + "history -d 2-3; echo \"r=$?\"; history\n"
        + "history -d 3-1; echo \"bad=$?\"\n"
        + "history -a -r; echo \"ar=$?\"\n"
        + "history -c; history -s 'echo e'; history -p '!!' '!e:s/e/Q/' '!nope'; echo \"p=$?\"\n");
}
