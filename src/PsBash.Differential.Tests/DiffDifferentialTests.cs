using PsBash.Differential.Tests.Oracle;
using Xunit;
using static PsBash.Differential.Tests.Oracle.FsStateOracle;

namespace PsBash.Differential.Tests;

/// <summary>
/// GNU diffutils 3.10 <c>diff</c> against ps-bash: stdout, exit status and the filesystem (diff writes nothing, so the tree is
/// a canary). Every case goes through <see cref="Run"/>, which keeps the exit status and strips the tab-separated file
/// timestamps of the <c>---</c>/<c>+++</c>/<c>***</c> headers (they differ by machine and time zone; their format is unit-tested).
/// Fixture data has no tabs. Hunk headers, ranges, grouping and the <c>\ No newline at end of file</c> notice are compared byte for byte.
/// </summary>
public class DiffDifferentialTests
{
    /// <summary>Runs <c>diff ARGS</c>, prints its status, then its output with timestamps removed.</summary>
    private static string Run(string args) =>
        $"diff {args} > __out 2>/dev/null; echo \"rc=$?\"; sed 's/\\t.*//' __out; rm -f __out";

    private static string Files() =>
        "printf 'a\\nb\\nc\\nd\\ne\\nf\\ng\\nh\\ni\\nj\\n' > a; printf 'a\\nB\\nc\\nd\\ne\\nf\\ng\\nh\\nI\\nj\\nk\\n' > b";

    // ───────────── the three formats ─────────────

    [SkippableFact] public Task Normal() => EqualAsync(Files(), Run("a b"));
    [SkippableFact] public Task Unified() => EqualAsync(Files(), Run("-u a b"));
    [SkippableFact] public Task Context() => EqualAsync(Files(), Run("-c a b"));
    [SkippableFact] public Task Unified_Context1_SplitsHunks() => EqualAsync(Files(), Run("-U1 a b"));
    [SkippableFact] public Task Context_Context1_SplitsHunks() => EqualAsync(Files(), Run("-C1 a b"));
    [SkippableFact] public Task Unified_Zero() => EqualAsync(Files(), Run("-U0 a b"));
    [SkippableFact] public Task Context_Zero() => EqualAsync(Files(), Run("-C0 a b"));
    [SkippableFact] public Task Unified_LongForms() => EqualAsync(Files(), Run("--unified=2 a b") + "; " + Run("--unified a b") + "; " + Run("--context=1 a b"));
    [SkippableFact] public Task Context_BareLong() => EqualAsync(Files(), Run("--context a b"));
    [SkippableFact] public Task Context_LargestNumberWins() => EqualAsync(Files(), Run("-U2 -U1 a b"));
    [SkippableFact] public Task Context_CWithZero_StillThree() => EqualAsync(Files(), Run("-c -C0 a b"));
    [SkippableFact] public Task Context_SeparateValue() => EqualAsync(Files(), Run("-U 1 a b"));
    [SkippableFact] public Task Context_AttachedValue() => EqualAsync(Files(), Run("-U1 a b"));
    [SkippableFact] public Task Style_UnifiedThenContext_Conflict() => EqualAsync(Files(), Run("-u -c a b"));
    [SkippableFact] public Task Style_ContextThenUnified_Conflict() => EqualAsync(Files(), Run("-c -U1 a b"));
    [SkippableFact] public Task Style_SameStyleTwiceIsFine() => EqualAsync(Files(), Run("-c -C1 a b"));
    [SkippableFact] public Task Style_BadLength() => EqualAsync(Files(), Run("-U x a b"));
    [SkippableFact] public Task Style_NegativeLength() => EqualAsync(Files(), Run("-C -1 a b"));

    // ───────────── hunk grouping ─────────────

    private static string Seq(string a, string b) =>
        $"seq 1 20 > s1; sed '{a}' s1 > s2; {b}";

    [SkippableFact] public Task Hunks_GapOfSixMerges() => EqualAsync("", Seq("s/^4$/X/;s/^11$/Y/", Run("-u s1 s2")));
    [SkippableFact] public Task Hunks_GapOfSevenSplits() => EqualAsync("", Seq("s/^4$/X/;s/^12$/Y/", Run("-u s1 s2")));
    [SkippableFact] public Task Hunks_ContextFormatGapOfSixMerges() => EqualAsync("", Seq("s/^4$/X/;s/^11$/Y/", Run("-c s1 s2")));
    [SkippableFact] public Task Hunks_ContextFormatGapOfSevenSplits() => EqualAsync("", Seq("s/^4$/X/;s/^12$/Y/", Run("-c s1 s2")));
    [SkippableFact] public Task Hunks_ChangeAtTheFirstLine() => EqualAsync("", Seq("s/^1$/X/", Run("-u s1 s2")));
    [SkippableFact] public Task Hunks_ChangeAtTheLastLine() => EqualAsync("", Seq("s/^20$/X/", Run("-u s1 s2")));
    [SkippableFact] public Task Hunks_ManyChanges() => EqualAsync("", Seq("s/^3$/X/;s/^9$/Y/;s/^15$/Z/;s/^19$/W/", Run("-u s1 s2")));
    [SkippableFact] public Task Hunks_DeleteBlock() => EqualAsync("", Seq("5,8d", Run("s1 s2") + "; " + Run("-u s1 s2") + "; " + Run("-c s1 s2")));
    [SkippableFact] public Task Hunks_InsertBlock() => EqualAsync("", Seq("5a\\\nnew1\\\nnew2", Run("s1 s2") + "; " + Run("-u s1 s2") + "; " + Run("-c s1 s2")));
    [SkippableFact] public Task Hunks_ReplaceBlockWithShorterBlock() => EqualAsync("", Seq("5,8c\\\nonly", Run("s1 s2") + "; " + Run("-u s1 s2") + "; " + Run("-c s1 s2")));
    [SkippableFact] public Task Hunks_ContextAddOnly() => EqualAsync("", "seq 1 5 > t1; seq 1 6 > t2; " + Run("-c t1 t2") + "; " + Run("-c t2 t1"));
    [SkippableFact] public Task Hunks_SingleLineFiles() => EqualAsync("", "printf 'a\\n' > p1; printf 'b\\n' > p2; " + Run("-u p1 p2") + "; " + Run("-c p1 p2") + "; " + Run("p1 p2"));
    [SkippableFact] public Task Hunks_RepeatedLinesShiftBoundaries() =>
        EqualAsync("", "printf 'a\\nb\\nb\\nb\\nc\\n' > r1; printf 'a\\nb\\nb\\nc\\n' > r2; " + Run("r1 r2") + "; " + Run("-u r1 r2") + "; " + Run("-c r2 r1"));
    [SkippableFact] public Task Hunks_Reordered() =>
        EqualAsync("", "printf '1\\n2\\n3\\n4\\n5\\n6\\n' > o1; printf '4\\n5\\n6\\n1\\n2\\n3\\n' > o2; " + Run("o1 o2") + "; " + Run("-u o1 o2"));
    [SkippableFact] public Task Hunks_CommonLinesWithAmbiguousAlignment() =>
        EqualAsync("", "printf 'x\\ny\\nx\\ny\\nx\\n' > m1; printf 'y\\nx\\ny\\nx\\ny\\n' > m2; " + Run("m1 m2") + "; " + Run("-u m1 m2"));
    [SkippableFact] public Task Hunks_AllLinesDiffer() =>
        EqualAsync("", "seq 1 5 > q1; seq 11 15 > q2; " + Run("q1 q2") + "; " + Run("-u q1 q2") + "; " + Run("-c q1 q2"));

    // ───────────── empty files, missing newline, odd content ─────────────

    [SkippableFact] public Task EmptyVersusContent() => EqualAsync(Files(), "printf '' > e1; " + Run("e1 a") + "; " + Run("a e1") + "; " + Run("-u e1 a") + "; " + Run("-u a e1") + "; " + Run("-c e1 a") + "; " + Run("-c a e1"));
    [SkippableFact] public Task EmptyBoth() => EqualAsync("", "printf '' > e1; printf '' > e2; " + Run("e1 e2") + "; " + Run("-s e1 e2"));
    [SkippableFact] public Task NoTrailingNewline() => EqualAsync("", "printf 'x\\ny' > n1; printf 'x\\ny\\n' > n2; " + Run("n1 n2") + "; " + Run("-u n1 n2") + "; " + Run("-c n1 n2") + "; " + Run("-u n2 n1"));
    [SkippableFact] public Task NoTrailingNewline_BothAndEqual() => EqualAsync("", "printf 'x\\ny' > n1; printf 'x\\ny' > n2; " + Run("n1 n2"));
    [SkippableFact] public Task NoTrailingNewline_BothButDifferent() => EqualAsync("", "printf 'x\\ny' > n1; printf 'x\\nz' > n2; " + Run("n1 n2") + "; " + Run("-u n1 n2"));
    [SkippableFact] public Task SameFile() => EqualAsync(Files(), Run("a a") + "; " + Run("-s a a"));
    [SkippableFact] public Task ReportIdentical_WithDifferences_PrintsTheDiff() => EqualAsync(Files(), Run("-s a b"));
    [SkippableFact] public Task Brief() => EqualAsync(Files(), Run("-q a b") + "; " + Run("-q a a") + "; " + Run("--brief a b"));
    [SkippableFact] public Task Brief_WithReportIdentical() => EqualAsync(Files(), Run("-qs a a"));
    [SkippableFact] public Task Binary_Differs() => EqualAsync("", "printf 'a\\0b\\n' > bin1; printf 'a\\0c\\n' > bin2; " + Run("bin1 bin2") + "; " + Run("-u bin1 bin2") + "; " + Run("-q bin1 bin2"));
    [SkippableFact] public Task Binary_Same() => EqualAsync("", "printf 'a\\0b\\n' > bin1; cp bin1 bin2; " + Run("bin1 bin2") + "; " + Run("-s bin1 bin2"));
    [SkippableFact] public Task Binary_AgainstText() => EqualAsync("", "printf 'a\\0b\\n' > bin1; printf 'a\\n' > t; " + Run("bin1 t") + "; " + Run("t bin1"));
    [SkippableFact] public Task Binary_TreatedAsText() => EqualAsync("", "printf 'a\\0b\\n' > bin1; printf 'a\\0c\\n' > bin2; " + Run("-a bin1 bin2") + "; " + Run("--text bin1 bin2"));

    // ───────────── whitespace / case flags ─────────────

    [SkippableFact] public Task IgnoreAllSpace() => EqualAsync("", "printf 'a b\\n' > w1; printf 'ab\\n' > w2; " + Run("-w w1 w2") + "; " + Run("w1 w2"));
    [SkippableFact] public Task IgnoreSpaceChange() => EqualAsync("", "printf 'a  b \\n' > w1; printf 'a b\\n' > w2; " + Run("-b w1 w2") + "; " + Run("w1 w2"));
    [SkippableFact] public Task IgnoreSpaceChange_LeadingSpaceStillCounts() => EqualAsync("", "printf ' a\\n' > w1; printf 'a\\n' > w2; " + Run("-b w1 w2") + "; " + Run("-w w1 w2"));
    [SkippableFact] public Task IgnoreSpaceChange_NoSpaceVersusSpace() => EqualAsync("", "printf 'ab\\n' > w1; printf 'a b\\n' > w2; " + Run("-b w1 w2"));
    [SkippableFact] public Task IgnoreSpaceChange_TabVersusSpaces() => EqualAsync("", "printf 'a\\t\\tb\\n' > w1; printf 'a b\\n' > w2; " + Run("-b w1 w2") + "; " + Run("-w w1 w2"));
    [SkippableFact] public Task IgnoreCase() => EqualAsync("", "printf 'AbC\\n' > i1; printf 'aBc\\n' > i2; " + Run("-i i1 i2") + "; " + Run("--ignore-case i1 i2") + "; " + Run("i1 i2"));
    [SkippableFact] public Task IgnoreCase_ShowsTheOriginalLines() => EqualAsync("", "printf 'AbC\\nx\\n' > i1; printf 'aBc\\ny\\n' > i2; " + Run("-i i1 i2") + "; " + Run("-iu i1 i2"));
    [SkippableFact] public Task StripTrailingCr() => EqualAsync("", "printf 'a\\r\\nb\\r\\n' > c1; printf 'a\\nb\\n' > c2; " + Run("--strip-trailing-cr c1 c2") + "; " + Run("-s --strip-trailing-cr c1 c2"));
    [SkippableFact] public Task CarriageReturnsAreContent() => EqualAsync("", "printf 'a\\r\\nb\\r\\n' > c1; printf 'a\\nb\\n' > c2; " + "diff c1 c2 | tr '\\r' '@'");
    [SkippableFact] public Task CarriageReturns_IgnoredByAllSpace() => EqualAsync("", "printf 'a\\r\\nb\\r\\n' > c1; printf 'a\\nb\\n' > c2; " + Run("-w c1 c2") + "; " + Run("-b c1 c2"));

    // ───────────── -B ─────────────

    [SkippableFact] public Task IgnoreBlankLines_OnlyBlankChange() => EqualAsync("", "printf 'a\\n\\nb\\n' > b1; printf 'a\\nb\\n' > b2; " + Run("-B b1 b2") + "; " + Run("b1 b2"));
    [SkippableFact] public Task IgnoreBlankLines_MixedChanges_Normal() => EqualAsync("", "printf 'a\\n\\nb\\nx\\n' > b3; printf 'a\\nb\\ny\\n' > b4; " + Run("-B b3 b4"));
    [SkippableFact] public Task IgnoreBlankLines_MixedChanges_Unified() => EqualAsync("", "printf 'a\\n\\nb\\nx\\n' > b3; printf 'a\\nb\\ny\\n' > b4; " + Run("-B -u b3 b4") + "; " + Run("-B -c b3 b4"));
    [SkippableFact] public Task IgnoreBlankLines_WhitespaceOnlyLineIsNotBlank() => EqualAsync("", "printf 'a\\n  \\nb\\n' > b5; printf 'a\\nb\\n' > b2; " + Run("-B b5 b2"));
    [SkippableFact] public Task IgnoreBlankLines_WithAllSpace() => EqualAsync("", "printf 'a\\n  \\nb\\n' > b5; printf 'a\\nb\\n' > b2; " + Run("-B -w b5 b2"));
    [SkippableFact] public Task IgnoreBlankLines_DistantRealChange() =>
        EqualAsync("", "printf '1\\n2\\n\\n3\\n4\\n5\\n6\\n7\\n8\\n9\\n10\\n11\\n' > d1; printf '1\\n2\\n3\\n4\\n5\\n6\\n7\\n8\\n9\\n10\\nX\\n' > d2; " + Run("-B -u d1 d2"));

    // ───────────── operands ─────────────

    [SkippableFact] public Task Stdin_AsSecondOperand() => EqualAsync(Files(), "cat b | diff a - ; echo rc=$?");
    [SkippableFact] public Task Stdin_AsFirstOperand() => EqualAsync(Files(), "cat a | diff - b ; echo rc=$?");
    [SkippableFact] public Task Stdin_UnifiedBody() => EqualAsync(Files(), "cat b | diff -u a - | sed 1,2d; echo done");
    [SkippableFact] public Task Stdin_Twice_IsIdentical() => EqualAsync(Files(), "diff - - < a; echo rc=$?");
    [SkippableFact] public Task MissingFile() => EqualAsync(Files(), Run("a nosuch"));
    [SkippableFact] public Task MissingBoth() => EqualAsync(Files(), Run("nosuch1 nosuch2"));
    [SkippableFact] public Task OneOperand() => EqualAsync(Files(), Run("a"));
    [SkippableFact] public Task NoOperand() => EqualAsync(Files(), Run(""));
    [SkippableFact] public Task ThreeOperands() => EqualAsync(Files(), Run("a b a"));
    [SkippableFact] public Task UnknownOption() => EqualAsync(Files(), Run("--bogus a b"));
    [SkippableFact] public Task OptionsAfterOperands() => EqualAsync(Files(), Run("a b -u"));
    [SkippableFact] public Task DoubleDashEndsOptions() => EqualAsync(Files(), "cp a ./-x; " + Run("-- -x b"));
    [SkippableFact] public Task DirectoryAndFile_ComparesTheNamedFileInside() => EqualAsync(Tree(("d/a", "hi"), ("a", "ho")), Run("a d") + "; " + Run("d a"));
    [SkippableFact] public Task DirectoryAndFile_MissingInside() => EqualAsync(Tree(("d/zz", "hi"), ("a", "ho")), Run("a d"));
    [SkippableFact] public Task NewFile_MissingOperandIsEmpty() => EqualAsync(Files(), Run("-N a nosuch") + "; " + Run("-N nosuch a") + "; " + Run("-uN a nosuch") + "; " + Run("-cN nosuch a"));
    [SkippableFact] public Task NewFile_BothMissingIsAnError() => EqualAsync(Files(), Run("-N nosuch1 nosuch2"));
    [SkippableFact] public Task Labels() => EqualAsync(Files(), Run("-u --label A --label B a b") + "; " + Run("-c --label A --label B a b") + "; " + Run("-u -L X -L Y a b"));
    [SkippableFact] public Task Labels_OnlyOne() => EqualAsync(Files(), Run("-u --label A a b"));
    [SkippableFact] public Task Labels_TooMany() => EqualAsync(Files(), Run("--label x --label y --label z a b"));

    // ───────────── directories ─────────────

    private static string Dirs() =>
        "mkdir -p d1/sub d1/only1 d2/sub d2/only2 d1/both d2/both; echo same > d1/same; echo same > d2/same; echo one > d1/diff; echo two > d2/diff; " +
        "echo x > d1/onlyfile1; echo y > d2/onlyfile2; echo s1 > d1/sub/f; echo s2 > d2/sub/f; echo c > d1/Zed; echo c > d2/zzed; mkdir d1/dirvsfile; echo f > d2/dirvsfile";

    [SkippableFact] public Task Dir_Plain() => EqualAsync(Dirs(), Run("d1 d2"));
    [SkippableFact] public Task Dir_Recursive() => EqualAsync(Dirs(), Run("-r d1 d2"));
    [SkippableFact] public Task Dir_RecursiveUnified_HeaderLineShowsTheSwitches() => EqualAsync(Dirs(), Run("-ru d1 d2"));
    [SkippableFact] public Task Dir_SeparateSwitchesInOrder() => EqualAsync(Dirs(), Run("-u -r d1 d2") + "; " + Run("--recursive --unified d1 d2"));
    [SkippableFact] public Task Dir_SwitchesAfterOperands() => EqualAsync(Dirs(), Run("d1 d2 -r -c"));
    [SkippableFact] public Task Dir_RecursiveContext() => EqualAsync(Dirs(), Run("-rc d1 d2"));
    [SkippableFact] public Task Dir_Brief() => EqualAsync(Dirs(), Run("-q d1 d2") + "; " + Run("-rq d1 d2"));
    [SkippableFact] public Task Dir_ReportIdentical() => EqualAsync(Dirs(), Run("-rs d1 d2"));
    [SkippableFact] public Task Dir_NewFile() => EqualAsync(Dirs(), Run("-rN d1 d2"));
    [SkippableFact] public Task Dir_NewFileUnified() => EqualAsync(Dirs(), Run("-ruN d1 d2"));
    [SkippableFact] public Task Dir_NewFileNotRecursive() => EqualAsync(Dirs(), Run("-N d1 d2"));
    [SkippableFact] public Task Dir_TrailingSlashes() => EqualAsync(Dirs(), Run("-r d1/ d2/"));
    [SkippableFact] public Task Dir_OrderIsByteOrder() => EqualAsync(Dirs(), "diff -q d1 d2 | grep -i zed");
    [SkippableFact] public Task Dir_Equal() => EqualAsync("mkdir e1 e2", Run("e1 e2") + "; " + Run("-r e1 e2"));
    [SkippableFact] public Task Dir_MissingSide() => EqualAsync(Dirs(), Run("-r d1 nosuch"));
    [SkippableFact] public Task Dir_Nested() => EqualAsync(
        Tree(("a/x/y/f", "1"), ("a/x/g", "same"), ("b/x/y/f", "2"), ("b/x/g", "same"), ("b/x/h", "new")), Run("-r a b") + "; " + Run("a b"));
    [SkippableFact] public Task Dir_BinaryInside() => EqualAsync(Dirs(), "printf 'a\\0b\\n' > d1/bin; printf 'a\\0c\\n' > d2/bin; " + Run("-r d1 d2"));
    [SkippableFact] public Task Dir_ExitStatusWhenOnlyCommonSubdirectories() => EqualAsync("mkdir -p e1/s e2/s", Run("e1 e2"));
}
