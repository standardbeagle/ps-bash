using PsBash.Differential.Tests.Oracle;
using Xunit;
using static PsBash.Differential.Tests.Oracle.FsStateOracle;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle for awk output redirection (<c>print &gt; f</c>, <c>&gt;&gt;</c>, <c>| "cmd"</c>,
/// <c>close()</c>): file cases compare the resulting TREE (paths + exact bytes) as well as stdout and
/// exit status (<see cref="FsStateOracle"/>); pipe cases compare stdout bytes, which is where gawk's
/// buffering order (the command's output at close/exit, awk's own later output after it) shows.
/// Awk programs use RELATIVE names — an awk string is data, so a <c>/tmp</c> path would not be mapped
/// to %TEMP% on Windows. Commands run by pipes go through ps-bash itself.
/// </summary>
public class AwkRedirectDifferentialTests
{
    private const string Data = "a 1\nb 2\na 3\n";

    // ───────────── > and >> (resulting files) ─────────────

    [SkippableFact] public Task Redirect_TruncateOnFirstOpenThenAppendWhileOpen() =>
        EqualAsync("", "awk 'BEGIN{ print \"a\" > \"o\"; print \"b\" > \"o\" }'");

    [SkippableFact] public Task Redirect_TruncatesPreExistingFile() =>
        EqualAsync(Tree(("o", "old")), "awk 'BEGIN{ print \"new\" > \"o\" }'");

    [SkippableFact] public Task Append_AddsToExistingFile() =>
        EqualAsync(Tree(("o", "old")), "awk 'BEGIN{ print \"x\" >> \"o\"; print \"y\" >> \"o\" }'");

    [SkippableFact] public Task Redirect_CloseThenReopenTruncatesAgain() =>
        EqualAsync("", "awk 'BEGIN{ f = \"o\"; print \"a\" > f; print \"b\" > f; close(f); print \"c\" > f }'");

    [SkippableFact] public Task Redirect_AppendAfterClose_KeepsEarlierContent() =>
        EqualAsync("", "awk 'BEGIN{ f = \"o\"; print \"a\" > f; close(f); print \"b\" >> f }'");

    [SkippableFact] public Task Redirect_AppendThenTruncateOperator_SameStreamKeepsAppending() =>
        EqualAsync(Tree(("g", "old")), "awk 'BEGIN{ print \"1\" >> \"g\"; print \"2\" > \"g\" }'");

    [SkippableFact] public Task Printf_Redirect_AndAppend() =>
        EqualAsync("", "awk 'BEGIN{ printf \"%s-%d\\n\", \"a\", 3 > \"pf\"; printf \"z\\n\" >> \"pf\" }'");

    [SkippableFact] public Task Printf_ParenthesizedArgs_Redirect() =>
        EqualAsync("", "awk 'BEGIN{ printf(\"%s|%s\\n\", \"a\", \"b\") > \"pf\"; print(\"c\", \"d\") > \"pp\" }'");

    [SkippableFact] public Task Print_NoArgs_RedirectWritesRecord() =>
        EqualAsync(Tree(("data", Data)), "awk '{ print > \"na\" }' data");

    [SkippableFact] public Task Print_SplitIntoFilesByFirstField() =>
        EqualAsync(Tree(("data", Data)), "awk '{ print $2 > ($1 \".txt\") }' data");

    [SkippableFact] public Task Printf_AppendToLogFile() =>
        EqualAsync(Tree(("data", Data), ("log", "start")), "awk '{ printf \"%s\\n\", $2 >> \"log\" }' data");

    [SkippableFact] public Task Print_ManyFilesInLoop() =>
        EqualAsync("", "awk 'BEGIN{ for (i = 0; i < 3; i++) print i > (\"f\" i) }'");

    [SkippableFact] public Task Redirect_TargetIsAConcatenation() =>
        EqualAsync("", "awk 'BEGIN{ x = \"p\"; print \"q\" > x \"3\"; print > \"p2\" 5 }'");

    [SkippableFact] public Task Redirect_ArgsWithCommaThenTarget() =>
        EqualAsync("", "awk 'BEGIN{ print 1, 2 > \"p1\"; print 3, 4 > \"p1\" }'");

    [SkippableFact] public Task Redirect_ParenthesizedComparisonIsNotARedirect() =>
        EqualAsync("", "awk 'BEGIN{ print (3 > 2); print (2 > 3); print (1 > 2) ? \"A\" : \"B\" }'");

    [SkippableFact] public Task Redirect_DevNullAndStdoutAlias() =>
        EqualAsync("", "awk 'BEGIN{ print \"x\" > \"/dev/null\"; print \"1\"; print \"2\" > \"/dev/stdout\"; print \"3\"; print \"d\" > \"-\" }'");

    [SkippableFact] public Task Redirect_CloseReturnsZeroThenMinusOne() =>
        EqualAsync("", "awk 'BEGIN{ print \"x\" > \"f\"; print close(\"f\"); print close(\"f\") }'");

    [SkippableFact] public Task Redirect_ExitStatusKept_FileStillWritten() =>
        EqualAsync("", "awk 'BEGIN{ print \"x\" > \"ex\"; exit 3 }'");

    [SkippableFact] public Task Redirect_UnwritablePathIsFatal_Exit2() =>
        EqualAsync("", "awk 'BEGIN{ print \"x\" > \"no/such/dir/f\"; print \"after\" }'");

    [SkippableFact] public Task Redirect_EmptyNameIsFatal_Exit2() =>
        EqualAsync("", "awk 'BEGIN{ print \"x\" > \"\"; print \"after\" }'");

    [SkippableFact] public Task Redirect_FflushPublishesToGetline() =>
        EqualAsync("", "awk 'BEGIN{ f = \"fl\"; print \"x\" > f; print fflush(f); print fflush(\"zz\"); getline l < f; print \"got[\" l \"]\" }'");

    [SkippableFact] public Task Redirect_SystemSeesUnclosedFile() =>
        EqualAsync("", "awk 'BEGIN{ print \"fromawk\" > \"s.txt\"; r = system(\"cat s.txt\"); print \"rc\", r }'");

    [SkippableFact] public Task Redirect_CloseThenGetlineReadsBack() =>
        EqualAsync(Tree(("data", Data)), "awk '{ print NR > \"nr.out\" } END{ close(\"nr.out\"); while ((getline l < \"nr.out\") > 0) print \"r:\" l }' data");

    // ───────────── | "cmd" (stdout bytes) ─────────────

    private const string Dir = "cd \"$(mktemp -d)\" && ";

    private static Task Eq(string script) =>
        AssertOracle.EqualAsync(Dir + script, timeout: TimeSpan.FromSeconds(60));

    [SkippableFact] public Task Pipe_Sort_OutputAtExit_BeforeHeldStdout() =>
        Eq("printf 'c\\nb\\na\\n' | awk 'BEGIN{ print \"h\" } { print | \"sort\" } END{ print \"f\" }'");

    [SkippableFact] public Task Pipe_Sort_ExplicitCloseBeforeFooter() =>
        Eq("printf 'c\\nb\\na\\n' | awk 'BEGIN{ print \"h\" } { print | \"sort\" } END{ close(\"sort\"); print \"f\" }'");

    [SkippableFact] public Task Pipe_Close_FlushesPendingStdoutFirst() =>
        Eq("awk 'BEGIN{ print \"x\" | \"cat\"; print \"mid\"; close(\"cat\"); print \"end\" }'");

    [SkippableFact] public Task Pipe_NumericSort() =>
        Eq("printf '10\\n9\\n100\\n' | awk '{ print | \"sort -n\" }'");

    [SkippableFact] public Task Pipe_PrintfToSort() =>
        Eq("awk 'BEGIN{ printf \"b\\na\\n\" | \"sort\" }'");

    [SkippableFact] public Task Pipe_CommaArgsJoinedWithOfs() =>
        Eq("awk 'BEGIN{ print \"b\", \"a\" | \"cat\" }'");

    [SkippableFact] public Task Pipe_Pipeline_SortUniqCount() =>
        Eq("printf 'x\\ny\\nx\\nx\\n' | awk '{ print | \"sort | uniq -c | sort -rn\" }'");

    [SkippableFact] public Task Pipe_CloseReturnsExitStatusOfGrep() =>
        Eq("awk 'BEGIN{ print \"x\" | \"grep -q x\"; print close(\"grep -q x\"); print \"y\" | \"grep -q x\"; print close(\"grep -q x\") }'");

    [SkippableFact] public Task Pipe_CloseOfUnopenedIsMinusOne() =>
        Eq("awk 'BEGIN{ print close(\"never\") }'");

    [SkippableFact] public Task Pipe_ReopenAfterCloseRunsAgain() =>
        Eq("awk 'BEGIN{ print \"a\" | \"sort\"; close(\"sort\"); print \"b\" | \"sort\" }'");

    [SkippableFact] public Task Pipe_CommandWritesFile_ThenCat() =>
        Eq("awk 'BEGIN{ print \"zeta\" | \"sort > out.txt\"; print \"alpha\" | \"sort > out.txt\"; close(\"sort > out.txt\") }'; cat out.txt");

    [SkippableFact] public Task Pipe_AndStdoutAlias_ExitOrder() =>
        Eq("awk 'BEGIN{ print \"to-sort\" | \"sort\"; print \"X\" > \"/dev/stdout\" }'");
}
