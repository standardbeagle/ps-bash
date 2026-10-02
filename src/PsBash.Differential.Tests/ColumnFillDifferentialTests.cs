using Xunit;
using static PsBash.Differential.Tests.Oracle.FsStateOracle;

namespace PsBash.Differential.Tests;

/// <summary>
/// <c>column</c> without <c>-t</c> against util-linux 2.39: the BSD fill layout (tab-separated columns of
/// tab-stop-rounded cells, down the columns or, with -x, across the rows). Output goes through <c>cat -A</c> so
/// the tabs are compared byte for byte (the canonicalizer strips trailing blanks, not <c>^I</c>).
/// </summary>
public class ColumnFillDifferentialTests
{
    private static Task Same(string command) => EqualAsync("printf '' > none", command);

    [SkippableFact] public Task Numbers_Width40() => Same("seq 1 10 | column -c 40 | cat -A");
    [SkippableFact] public Task Numbers_Width40_FillRows() => Same("seq 1 10 | column -c 40 -x | cat -A");
    [SkippableFact] public Task Numbers_DefaultWidth80() => Same("seq 1 40 | column | cat -A");
    [SkippableFact] public Task Numbers_HundredFillRows() => Same("seq 1 100 | column -x -c 80 | cat -A");
    // (a file operand: an env prefix on a PIPE-TARGET column is not seen by the cmdlet in ps-bash — separate gap)
    [SkippableFact] public Task Numbers_ColumnsEnvironment() => EqualAsync("seq 1 40 > n40", "COLUMNS=60 column n40 | cat -A");
    [SkippableFact] public Task Numbers_OptionBeatsColumnsEnvironment() => EqualAsync("seq 1 40 > n40", "COLUMNS=60 column -c 30 n40 | cat -A");
    [SkippableFact] public Task Words_WideCell_OnePerLine() =>
        Same("printf '%s\\n' apple banana cherry date elderberry fig grape honeydew kiwi lemon | column -c 30 | cat -A");
    [SkippableFact] public Task EntryWiderThanWidth() =>
        Same("printf '%s\\n' aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa b c | column -c 40 | cat -A");
    [SkippableFact] public Task ExactFit() => Same("printf '%s\\n' aaaa bbbb cccc dddd | column -c 19 | cat -A");
    [SkippableFact] public Task BlankLinesDropped() => Same("printf 'a\\n\\nb\\n\\n\\nc\\n' | column -c 40 | cat -A");
    [SkippableFact] public Task BlankLinesKept() => Same("printf 'a\\n\\nb\\n\\nc\\n' | column -c 40 -L | cat -A");
    [SkippableFact] public Task MixedWidths_ColumnMajor() =>
        Same("printf '%s\\n' a bbbbbbbbbbbbbb cc d eeeeeeeeee ff g hhhh | column -c 60 | cat -A");
    [SkippableFact] public Task MixedWidths_RowMajor() =>
        Same("printf '%s\\n' a bbbbbbbbbbbbbb cc d eeeeeeeeee ff g hhhh | column -c 60 -x | cat -A");
    [SkippableFact] public Task Utf8Entries() => Same("printf '%s\\n' héllo wörld ñandú ünï cøde | column -c 30 | cat -A");
    [SkippableFact] public Task WideCjkEntries() => Same("printf '%s\\n' 日本語 中文 한국어 abc def ghi | column -c 30 | cat -A");
    [SkippableFact] public Task SeparatorAndOutputSeparatorIgnored() => Same("printf 'a:b\\nc:d\\ne\\n' | column -s: -o '|' -c 40 | cat -A");
    [SkippableFact] public Task WidthZeroAndOne() => Same("seq 1 3 | column -c 0 | cat -A; seq 1 3 | column -c 1 | cat -A");
    [SkippableFact] public Task BundledXc() => Same("seq 1 12 | column -xc 20 | cat -A");
    [SkippableFact] public Task LongOptions() => Same("seq 1 12 | column --fillrows --output-width=20 | cat -A");
    [SkippableFact] public Task FileOperands() => EqualAsync("seq 1 12 > nums", "column -c 20 nums nums | cat -A");
    [SkippableFact] public Task EmptyInput() => Same("printf '' | column -c 20 | cat -A");
    [SkippableFact] public Task TableIgnoresWidth() => Same("printf 'a b\\nc d\\n' | column -c 40 -t | cat -A");

    [SkippableFact] public Task Error_TableAndFillRows() => Same("printf 'a b\\n' | column -t -x");
    [SkippableFact] public Task Error_WidthNotANumber() => Same("echo a | column -c x");
    [SkippableFact] public Task Error_WidthNegative() => Same("echo a | column -c -5");
    [SkippableFact] public Task Error_WidthTooLarge() => Same("echo a | column -c 4294967296");
}
