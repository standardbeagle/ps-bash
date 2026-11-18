using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <see cref="BashRuntime.SplitRecord(string, bool)"/>: the one record → lines splitter. Oracle: the lines bash's
/// reader sees in the record's bytes (end-to-end parity is RecordTrailingEmptyLinesDifferentialTests; these pin the
/// primitive). Regression: every consumer did <c>TrimEnd('\n').Split('\n')</c>, which ate trailing EMPTY lines —
/// <c>printf 'a\n\n' | grep -c ''</c> was 1, bash 2. Absorbs the old <c>InvokeBashHeadCommand.ItemLines</c> cases.
/// </summary>
public class RecordSplitTests
{
    [Theory]
    [InlineData("a", false, new[] { "a" })]
    [InlineData("a\n", false, new[] { "a" })]                  // the record's own terminator
    [InlineData("a\n\n", false, new[] { "a", "" })]            // THE regression: a then an empty line
    [InlineData("a\n\n\n", false, new[] { "a", "", "" })]
    [InlineData("\n\n", false, new[] { "", "" })]
    [InlineData("\n", false, new[] { "" })]
    [InlineData("", false, new[] { "" })]                      // an empty terminated record is one empty line
    [InlineData("a\n\nb\n\n", false, new[] { "a", "", "b", "" })]
    [InlineData("a\nb\n\n\n", false, new[] { "a", "b", "", "" })] // head -n -K count (old ItemLines case)
    [InlineData("a\nb", true, new[] { "a", "b" })]             // unterminated: last line open, nothing dropped
    [InlineData("a\n\nb", true, new[] { "a", "", "b" })]
    [InlineData("", true, new string[0])]                      // `printf ''`: no bytes, no line
    [InlineData("a\r\n\r\n", false, new[] { "a\r", "\r" })]    // '\r' is data here; sites that strip it keep doing so
    public void SplitRecord_DropsOneOwnTerminatorThenSplits(string text, bool unterminated, string[] expected)
        => Assert.Equal(expected, BashRuntime.SplitRecord(text, unterminated));

    [Fact]
    public void SplitRecord_Record_UsesTheRecordsTerminatorFlag()
    {
        // printf 'a\n\n' : one flagged record whose text ends in its own newline -> terminated, 2 lines.
        var printed = BashRuntime.NewBashObject("a\n\n", "PsBash.TextOutput", noTrailingNewline: true);
        Assert.Equal(new[] { "a", "" }, BashRuntime.SplitRecord(printed, out bool open1));
        Assert.False(open1);

        // printf 'a\nb' : the last line is unterminated.
        var partial = BashRuntime.NewBashObject("a\nb", "PsBash.TextOutput", noTrailingNewline: true);
        Assert.Equal(new[] { "a", "b" }, BashRuntime.SplitRecord(partial, out bool open2));
        Assert.True(open2);

        // printf '' : no line at all.
        var empty = BashRuntime.NewBashObject("", "PsBash.TextOutput", noTrailingNewline: true);
        Assert.Empty(BashRuntime.SplitRecord(empty, out _));
    }

    [Fact]
    public void RecordLines_MarksOnlyTheLastLineOfAnUnterminatedRecord()
    {
        var partial = BashRuntime.NewBashObject("a\n\nb", "PsBash.TextOutput", noTrailingNewline: true);
        Assert.Equal(new[] { ("a", false), ("", false), ("b", true) }, BashRuntime.RecordLines(partial).ToArray());
        var terminated = BashRuntime.NewBashObject("a\n\n", "PsBash.TextOutput", noTrailingNewline: true);
        Assert.Equal(new[] { ("a", false), ("", false) }, BashRuntime.RecordLines(terminated).ToArray());
    }

    [Theory]
    [InlineData("x\n", "x", false)]          // one newline: a plain line record
    [InlineData("\n", "", false)]
    [InlineData("a\nb\n", "a\nb", false)]
    [InlineData("x\n\n", "x\n\n", true)]     // a final EMPTY line: exact bytes, never plain "x\n"
    [InlineData("x", "x", true)]             // no newline: exact bytes, unterminated
    public void BytesRecord_NeverLeavesAPlainRecordEndingInNewline(string bytes, string text, bool flagged)
    {
        var rec = PSObject.AsPSObject(BashRuntime.BytesRecord(bytes));
        Assert.Equal(text, BashRuntime.GetBashText(rec));
        Assert.Equal(flagged, rec.Properties["NoTrailingNewline"]?.Value is true);
        // Whatever the shape, a reader recovers exactly the lines of the bytes.
        Assert.Equal(BashRuntime.SplitRecord(bytes, unterminated: !bytes.EndsWith('\n')),
            BashRuntime.SplitRecord(rec, out _));
        Assert.Equal(bytes, BashRuntime.RecordStreamText(new object[] { rec }));
    }
}
