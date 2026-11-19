using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <see cref="BashRuntime.SplitBytes"/> / <see cref="BashRuntime.SplitRecord(object?, out bool)"/>: the one record → lines
/// splitter, over the one record → bytes rule <see cref="BashRuntime.RecordBytes"/>. Oracle: the lines bash's reader sees
/// in the record's bytes (end-to-end parity is RecordTrailingEmptyLinesDifferentialTests and
/// RecordBytesDifferentialTests; these pin the primitives). Regressions: every consumer did
/// <c>TrimEnd('\n').Split('\n')</c>, which ate trailing EMPTY lines (<c>printf 'a\n\n' | grep -c ''</c> was 1, bash 2);
/// and readers gave a PLAIN record whose text ends in a newline no boundary newline while the host renderer did, so
/// <c>jq -r '"a\n"'</c> printed two lines on the screen and one into <c>| cat</c>.
/// </summary>
public class RecordSplitTests
{
    [Theory]
    [InlineData("a", true, new[] { "a" })]                     // unterminated: last line open, nothing dropped
    [InlineData("a\n", false, new[] { "a" })]                  // the bytes' final newline ends the last line
    [InlineData("a\n\n", false, new[] { "a", "" })]            // a then an empty line
    [InlineData("a\n\n\n", false, new[] { "a", "", "" })]
    [InlineData("\n\n", false, new[] { "", "" })]
    [InlineData("\n", false, new[] { "" })]
    [InlineData("a\n\nb\n\n", false, new[] { "a", "", "b", "" })]
    [InlineData("a\nb\n\n\n", false, new[] { "a", "b", "", "" })] // head -n -K count (old ItemLines case)
    [InlineData("a\n\nb", true, new[] { "a", "", "b" })]
    [InlineData("", true, new string[0])]                      // no bytes, no line
    [InlineData("a\r\n\r\n", false, new[] { "a\r", "\r" })]    // '\r' is data here; sites that strip it keep doing so
    public void SplitBytes_DropsTheFinalNewlineThenSplits(string bytes, bool unterminated, string[] expected)
    {
        Assert.Equal(expected, BashRuntime.SplitBytes(bytes, out bool open));
        Assert.Equal(unterminated, open);
    }

    [Theory]
    [InlineData("a", new[] { "a" })]
    [InlineData("", new[] { "" })]                             // an empty plain record is one empty line
    [InlineData("a\n", new[] { "a", "" })]                     // THE regression: text "a\n" + the boundary = 2 lines
    [InlineData("a\n\n", new[] { "a", "", "" })]
    [InlineData("a\nb", new[] { "a", "b" })]
    public void SplitRecord_PlainRecord_IsItsTextPlusTheBoundaryNewline(string text, string[] expected)
    {
        Assert.Equal(expected, BashRuntime.SplitRecord(text, out bool open));
        Assert.False(open);
        Assert.Equal(text + "\n", BashRuntime.RecordBytes(text));
    }

    [Fact]
    public void SplitRecord_ExactBytesRecord_IsItsBytes()
    {
        // printf 'a\n\n' : one exact-bytes record whose text ends in its own newline -> terminated, 2 lines.
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
    [InlineData("x\n\n", "x\n\n", true)]     // a final EMPTY line: exact bytes (no plain record ends in its own \n)
    [InlineData("\n\n", "\n\n", true)]
    [InlineData("x", "x", true)]             // no newline: exact bytes, unterminated
    public void BytesRecord_RoundTripsTheBytes(string bytes, string text, bool exact)
    {
        var rec = PSObject.AsPSObject(BashRuntime.BytesRecord(bytes));
        Assert.Equal(text, BashRuntime.GetBashText(rec));
        Assert.Equal(exact, BashRuntime.IsExactBytes(rec));
        // Whatever the shape, the record IS the bytes, and a reader recovers exactly their lines.
        Assert.Equal(bytes, BashRuntime.RecordBytes(rec));
        Assert.Equal(BashRuntime.SplitBytes(bytes, out _), BashRuntime.SplitRecord(rec, out _));
        Assert.Equal(bytes, BashRuntime.RecordStreamText(new object[] { rec }));
    }

    [Theory]
    [InlineData("a\n", false, "\n", "a\n\n")]  // the renderer's rule: a plain record always gets its boundary
    [InlineData("a", false, "\r\n", "a\r\n")]
    [InlineData("a\n", true, "\n", "a\n")]     // exact bytes get nothing
    public void StdoutRecordBytes_IsTheOneRule(string text, bool exact, string eol, string expected)
        => Assert.Equal(expected, PsBash.Core.StdoutRecord.Bytes(text, exact, eol));
}
