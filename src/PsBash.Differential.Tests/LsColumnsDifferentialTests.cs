using Xunit;
using static PsBash.Differential.Tests.Oracle.FsStateOracle;

namespace PsBash.Differential.Tests;

/// <summary>
/// <c>ls -C</c> / <c>-x</c> / <c>-m</c> and their width controls against GNU coreutils 9.4: column counts, the tab
/// stops between columns (<c>cat -A</c> shows the tabs), <c>-w</c> / <c>COLUMNS</c> / <c>-T</c> / <c>TABSIZE</c>,
/// indicators and block counts inside the cell width, and the usage errors. Names are lowercase (the oracle sorts in
/// the C locale, ps-bash case-insensitively).
/// </summary>
public class LsColumnsDifferentialTests
{
    private const string Mixed =
        "for f in alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu; do printf '' > $f; done";
    private const string Six = "for f in aaaa bbbb cccc dddd eeee ffff; do printf '' > $f; done";

    [SkippableFact] public Task Vertical_Width40() => EqualAsync(Mixed, "ls -C -w 40 | cat -A");
    [SkippableFact] public Task Vertical_Width20() => EqualAsync(Mixed, "ls -C -w 20 | cat -A");
    [SkippableFact] public Task Vertical_Width100() => EqualAsync(Mixed, "ls -C -w 100 | cat -A");
    [SkippableFact] public Task Vertical_DefaultWidth80() => EqualAsync(Mixed, "ls -C | cat -A");
    [SkippableFact] public Task Vertical_WidthOne() => EqualAsync(Mixed, "ls -C -w 1 | cat -A");
    [SkippableFact] public Task Vertical_WidthZero_Unlimited_NoTabs() => EqualAsync(Six, "ls -C -w 0 | cat -A");
    [SkippableFact] public Task Vertical_ExactFit_IsNotEnough() => EqualAsync(Six, "ls -C -w 16 | cat -A; ls -C -w 17 | cat -A");
    [SkippableFact] public Task Vertical_TabCrossing() => EqualAsync(Six, "ls -C -w 1000 | cat -A");
    [SkippableFact] public Task Across_Width40() => EqualAsync(Mixed, "ls -x -w 40 | cat -A");
    [SkippableFact] public Task Across_Width20() => EqualAsync(Mixed, "ls -x -w 20 | cat -A");
    [SkippableFact] public Task Across_TabCrossing() => EqualAsync(Six, "ls -x -w 30 | cat -A");
    [SkippableFact] public Task Commas_Width30() => EqualAsync(Mixed, "ls -m -w 30 | cat -A");
    [SkippableFact] public Task Commas_Width12() => EqualAsync(Mixed, "ls -m -w 12 | cat -A");
    [SkippableFact] public Task Commas_Unlimited() => EqualAsync(Mixed, "ls -m -w 0 | cat -A");
    [SkippableFact] public Task Commas_DefaultWidth() => EqualAsync(Mixed, "ls -m | cat -A");
    [SkippableFact] public Task TabSize4() => EqualAsync(Six, "ls -C -T 4 -w 40 | cat -A");
    [SkippableFact] public Task TabSize3() => EqualAsync(Six, "ls -C -T 3 -w 40 | cat -A");
    [SkippableFact] public Task TabSize0_SpacesOnly() => EqualAsync(Six, "ls -x -T 0 -w 30 | cat -A");
    [SkippableFact] public Task LongOptions() => EqualAsync(Mixed, "ls --width=40 --tabsize=4 --format=across | cat -A");
    [SkippableFact] public Task FormatWords() =>
        EqualAsync(Mixed, "ls --format=vertical -w 40 | cat -A; ls --format=commas -w 40 | cat -A; ls --format=horizontal -w 40 | cat -A; ls --format=single-column | head -3");
    [SkippableFact] public Task FormatAbbreviations() => EqualAsync(Mixed, "ls --format=h -w 40 | cat -A; ls --format=ac -w 40 | cat -A; ls --format=c -w 40 | cat -A");
    [SkippableFact] public Task ColumnsEnvironment() => EqualAsync(Mixed, "COLUMNS=30 ls -C | cat -A");
    [SkippableFact] public Task WidthBeatsColumnsEnvironment() => EqualAsync(Mixed, "COLUMNS=100 ls -C -w 30 | cat -A");
    [SkippableFact] public Task TabsizeEnvironment() => EqualAsync(Six, "TABSIZE=0 ls -C -w 1000 | cat -A");
    [SkippableFact] public Task FormatIsLastWins() =>
        EqualAsync(Mixed, "ls -Cx -w 40 | cat -A; ls -xC -w 40 | cat -A; ls -mC -w 40 | cat -A; ls -x1 | head -3; ls -1x -w 40 | cat -A");
    [SkippableFact] public Task LongFormatIsOverriddenByLaterColumns_ButNotByDashOne() =>
        EqualAsync(Six, "ls -lC -w 30 | cat -A; ls -l1 | wc -l; ls -1l | wc -l");
    [SkippableFact] public Task IndicatorsCountTowardsTheWidth() =>
        EqualAsync(Six + "; mkdir gggg", "ls -FC -w 30 | cat -A; ls -pC -w 18 | cat -A");
    [SkippableFact] public Task BlockSizes() => EqualAsync(Six + "; printf 'xxx' > bbbb", "ls -sC -w 40 | cat -A; ls -sm -w 40 | cat -A");
    [SkippableFact] public Task Recursive_EachDirectoryIsItsOwnBlock() =>
        EqualAsync(Six + "; mkdir sub; printf '' > sub/one; printf '' > sub/two", "ls -RC -w 30 | cat -A");
    [SkippableFact] public Task SeveralOperands() => EqualAsync(Six + "; mkdir sub; printf '' > sub/one", "ls -C -w 30 aaaa bbbb sub | cat -A");
    [SkippableFact] public Task DirectoryOperandsWithD() => EqualAsync(Six, "ls -Cd -w 30 aaaa bbbb cccc | cat -A");
    [SkippableFact] public Task Reverse() => EqualAsync(Mixed, "ls -Cr -w 40 | cat -A");
    [SkippableFact] public Task AllIncludesDots() => EqualAsync(Mixed, "ls -Ca -w 40 | cat -A");
    [SkippableFact] public Task Utf8Names() => EqualAsync("for f in héllo abc ñandú zzz; do printf '' > $f; done", "ls -C -w 30 | cat -A");

    [SkippableFact] public Task Error_WidthNotANumber() => EqualAsync(Six, "ls -w x");
    [SkippableFact] public Task Error_WidthNegative() => EqualAsync(Six, "ls -w -3");
    [SkippableFact] public Task Error_WidthTrailingJunk() => EqualAsync(Six, "ls --width=1x");
    [SkippableFact] public Task Error_TabSize() => EqualAsync(Six, "ls -T x");
    [SkippableFact] public Task Error_FormatWord() => EqualAsync(Six, "ls --format=bogus");
}
