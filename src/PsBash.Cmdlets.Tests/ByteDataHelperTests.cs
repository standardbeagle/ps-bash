using PsBash.Core;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The pure seams behind byte-level data handling (the end-to-end behavior is oracle-tested in
/// Differential.Tests ByteDataDifferentialTests): sed's \xHH byte-escape rewrite and the /dev/zero operand test.
/// </summary>
public class ByteDataHelperTests
{
    [Fact]
    public void SedByteEscape_HighByte_BecomesItsByteChar()
    {
        string p = @"\xce";
        Assert.True(InvokeBashSedCommand.TryRewriteByteEscapes(ref p));
        Assert.Equal(RawBytes.ToMarker(0xCE).ToString(), p);
    }

    [Fact]
    public void SedByteEscape_OtherTextInThePattern_IsTakenAsBytesToo()
    {
        string p = @"μ\xce";
        Assert.True(InvokeBashSedCommand.TryRewriteByteEscapes(ref p));
        Assert.Equal(new string([RawBytes.ToMarker(0xCE), RawBytes.ToMarker(0xBC), RawBytes.ToMarker(0xCE)]), p);
    }

    [Theory]
    [InlineData(@"\x41")]        // ASCII: the regex engine's own \x41 matches it
    [InlineData(@"\\xce")]       // an escaped backslash, then the text "xce"
    [InlineData(@"\xc")]         // too short to be a byte escape
    [InlineData("abc")]
    public void SedByteEscape_NoHighByteEscape_LeavesThePatternAlone(string pattern)
    {
        string p = pattern;
        Assert.False(InvokeBashSedCommand.TryRewriteByteEscapes(ref p));
        Assert.Equal(pattern, p);
    }
}
