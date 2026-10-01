using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle for awk <c>getline</c>: each idiom runs in real bash (gawk) AND ps-bash and the
/// bytes are diffed. Programs read RELATIVE names after a <c>cd</c> into a scratch directory (an awk
/// program string is data, so a <c>/tmp</c> path would not be mapped to %TEMP% on Windows). Commands
/// run by <c>cmd | getline</c> / <c>system()</c> are re-entered through ps-bash itself.
/// </summary>
public class AwkGetlineDifferentialTests
{
    private const string Dir = "cd \"$(mktemp -d)\" && printf 'a\\nb\\nc\\n' > f && printf 'x\\ny\\n' > g && ";

    private static Task Eq(string script) =>
        AssertOracle.EqualAsync(Dir + script, timeout: TimeSpan.FromSeconds(30));

    // ── main input ────────────────────────────────────────────────────────────

    [SkippableFact] public Task Getline_Plain_NextRecordIdiom() => Eq("awk 'NR==1{getline; print}' f");
    [SkippableFact] public Task Getline_Plain_AfterPattern() => Eq("awk '/b/{getline; print}' f");
    [SkippableFact] public Task Getline_Var_PairedLines_OddCount() => Eq("awk '{getline nxt; print $0, nxt}' f");
    [SkippableFact] public Task Getline_Plain_UpdatesNrFnrNf() => Eq("awk 'NR==1{getline; print NR, FNR, NF, $0}' f");
    [SkippableFact] public Task Getline_Var_UpdatesNrFnrNotNf() => Eq("awk 'NR==1{getline v; print NR, FNR, NF, $0, v}' f");
    [SkippableFact] public Task Getline_InBegin_FirstRecord() => Eq("awk 'BEGIN{getline; print \"B:\" $0, NR} {print \"M:\" $0}' f");
    [SkippableFact] public Task Getline_InEnd_ReturnsZero() => Eq("awk 'END{r = getline; print r, $0}' f");
    [SkippableFact] public Task Getline_AcrossFiles_FilenameFnr() => Eq("awk '{print FILENAME, FNR; getline; print \"g\", FILENAME, FNR, NR}' f g");
    [SkippableFact] public Task Getline_Stdin_Pairs() => Eq("printf '1\\n2\\n3\\n' | awk '{ getline x; print $0 \"-\" x; print NR }'");
    [SkippableFact] public Task Getline_Stdin_BeginThenMainLoop() => Eq("printf 'a\\nb\\nc\\n' | awk 'BEGIN{ getline; print \"B:\" $0 } { print \"M:\" $0 } END{ print NR }'");

    // ── getline < file ────────────────────────────────────────────────────────

    [SkippableFact] public Task GetlineFile_WhileLoopCount() => Eq("awk '{ while ((getline line < \"f\") > 0) n++ } END{print n}' f");
    [SkippableFact] public Task GetlineFile_SetsRecordNf_NotNr() => Eq("awk 'BEGIN{ getline < \"f\"; print $0, NF, NR }'");
    [SkippableFact] public Task GetlineFile_Var_NextLinePerCall() => Eq("awk 'BEGIN{ getline a < \"f\"; getline b < \"f\"; print a b }'");
    [SkippableFact] public Task GetlineFile_CloseRewinds() => Eq("awk 'BEGIN{ f = \"f\"; getline a < f; getline b < f; close(f); getline c < f; print a b c }'");
    [SkippableFact] public Task GetlineFile_MissingIsMinusOne() => Eq("awk 'BEGIN{ print (getline x < \"nope\") }'");
    [SkippableFact] public Task GetlineFile_UnparenthesizedInCondition() => Eq("awk 'BEGIN{ while (getline line < \"f\" > 0) n++; print n }'");
    [SkippableFact] public Task GetlineFile_ConcatenationBindsAfterFileOperand() => Eq("awk 'BEGIN{ x = getline < \"f\" \"zz\"; print x }'");
    [SkippableFact] public Task GetlineFile_StdinDash() => Eq("echo piped | awk 'BEGIN{ getline x < \"-\"; print \"got\", x }'");

    // ── cmd | getline ─────────────────────────────────────────────────────────

    [SkippableFact] public Task GetlineCmd_Var() => Eq("awk 'BEGIN { \"echo hi\" | getline x; print x }'");
    [SkippableFact] public Task GetlineCmd_Record_SetsNf_NotNr() => Eq("awk 'BEGIN { \"echo x y z\" | getline; print $2, NF, NR }'");
    [SkippableFact] public Task GetlineCmd_WhileLoopSeq() => Eq("awk 'BEGIN{while((\"seq 3\" | getline l) > 0) s+=l; print s}'");
    [SkippableFact] public Task GetlineCmd_UnparenthesizedInCondition() => Eq("awk 'BEGIN{ while (\"echo a; echo b\" | getline > 0) n++; print n }'");
    [SkippableFact] public Task GetlineCmd_ConcatenatedCommand() => Eq("awk 'BEGIN{ \"echo \" \"zz\" | getline q; print q }'");
    [SkippableFact] public Task GetlineCmd_StreamReusedUntilClosed() => Eq("awk 'BEGIN{ c = \"echo 1; echo 2\"; c | getline a; c | getline b; close(c); c | getline d; print a, b, d }'");
    [SkippableFact] public Task GetlineCmd_EofThenReopen() => Eq("awk 'BEGIN{ x = \"echo a\" | getline y; print x, y; x = \"echo a\" | getline y; print x; close(\"echo a\"); x = \"echo a\" | getline y; print x }'");
    [SkippableFact] public Task Close_Command_ExitStatus() => Eq("awk 'BEGIN{ \"exit 3\" | getline; print close(\"exit 3\"); print close(\"exit 3\") }'");
    [SkippableFact] public Task Close_Unopened_MinusOne() => Eq("awk 'BEGIN{ print close(\"never\") }'");

    // ── system / fflush ───────────────────────────────────────────────────────

    [SkippableFact] public Task System_OutputOrderAndStatus() => Eq("awk 'BEGIN{ print \"before\"; system(\"echo sys\"); print \"after\"; print system(\"exit 2\") }'");
    [SkippableFact] public Task Fflush_ReturnsZero() => Eq("awk 'BEGIN{ print fflush() }'");
}
