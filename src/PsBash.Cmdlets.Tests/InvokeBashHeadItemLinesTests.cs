using PsBash.Cmdlets;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Unit tests for <see cref="InvokeBashHeadCommand.ItemLines"/> — the line splitter the
/// <c>head -n -K</c> (all-but-last-K) count + emit share.
///
/// Regression (2026-07 review): the count used <c>GetBashText(item).TrimEnd('\n')</c>,
/// which stripped ALL trailing newlines and so undercounted a multi-line pipeline item
/// ending in genuine BLANK lines — <c>"a\nb\n\n\n"</c> is 4 lines to GNU (a, b, blank,
/// blank) but TrimEnd+Split counted 2, so <c>head -n -2</c> emitted nothing where GNU
/// emits <c>a</c>, <c>b</c>. ItemLines drops only the terminator's trailing empty element.
/// </summary>
public class InvokeBashHeadItemLinesTests
{
    [Theory]
    [InlineData("a", 1)]
    [InlineData("a\n", 1)]          // trailing \n terminates the single line
    [InlineData("a\nb", 2)]
    [InlineData("a\nb\n", 2)]
    [InlineData("a\nb\n\n\n", 4)]   // THE regression: 4 lines (a, b, blank, blank), not 2
    [InlineData("\n", 1)]           // one blank line
    public void ItemLines_KeepsTrailingBlankLines_DropsOnlyTerminator(string text, int expected)
        => Assert.Equal(expected, InvokeBashHeadCommand.ItemLines(text).Length);

    [Fact]
    public void ItemLines_PreservesBlankLineContent()
    {
        Assert.Equal(new[] { "a", "b", "", "" }, InvokeBashHeadCommand.ItemLines("a\nb\n\n\n"));
    }
}
