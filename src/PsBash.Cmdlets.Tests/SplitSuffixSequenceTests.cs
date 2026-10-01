using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>Pure naming rule of <see cref="SplitSuffixSequence"/> (GNU split 9.4 oracle, see SplitSuffixGrowthTests).</summary>
public class SplitSuffixSequenceTests
{
    private static List<string> Take(SplitSuffixSequence s, int n)
    {
        var l = new List<string>();
        for (int i = 0; i < n; i++) { var x = s.Next(); if (x is null) break; l.Add(x); }
        return l;
    }

    [Fact]
    public void AutoAlpha_BlockBoundaries()
    {
        var l = Take(new SplitSuffixSequence(false, 2, auto: true), 650 + 16900 + 3);
        Assert.Equal("aa", l[0]);
        Assert.Equal("ab", l[1]);
        Assert.Equal("yz", l[649]);
        Assert.Equal("zaaa", l[650]);
        Assert.Equal("zyzz", l[650 + 16900 - 1]);
        Assert.Equal("zzaaaa", l[650 + 16900]);   // second marker, 4-letter counter
    }

    [Fact]
    public void AutoNumeric_BlockBoundaries()
    {
        var l = Take(new SplitSuffixSequence(true, 2, auto: true), 10000);
        Assert.Equal("00", l[0]);
        Assert.Equal("89", l[89]);
        Assert.Equal("9000", l[90]);
        Assert.Equal("9899", l[989]);
        Assert.Equal("990000", l[990]);
        Assert.Equal("99900000", l[990 + 9000]);   // oracle: the 10000th file is x99900009
    }

    [Fact]
    public void Fixed_RunsOutAfterAllNames()
    {
        var s = new SplitSuffixSequence(false, 1, auto: false);
        Assert.Equal(26, Take(s, 100).Count);
        Assert.Null(s.Next());
        var d = new SplitSuffixSequence(true, 3, auto: false);
        var l = Take(d, 2000);
        Assert.Equal(1000, l.Count);
        Assert.Equal("999", l[^1]);
    }

    [Fact]
    public void NumericFrom_IsRightAligned()
    {
        var s = new SplitSuffixSequence(true, 4, auto: false, from: "5");
        Assert.Equal(new[] { "0005", "0006", "0007" }, Take(s, 3));
        var e = new SplitSuffixSequence(true, 2, auto: false, from: "98");
        Assert.Equal(new[] { "98", "99" }, Take(e, 5));
    }

    [Fact]
    public void AutoHex_BlockBoundaries()
    {
        // oracle (GNU split 9.4, `split -x -l1` on 300 lines): x00 .. xef (240 names), then xf000 ..
        var l = Take(new SplitSuffixSequence(false, 2, auto: true, hex: true), 300);
        Assert.Equal("00", l[0]);
        Assert.Equal("09", l[9]);
        Assert.Equal("0a", l[10]);
        Assert.Equal("ee", l[238]);
        Assert.Equal("ef", l[239]);
        Assert.Equal("f000", l[240]);
        Assert.Equal("f03b", l[299]);
    }

    [Fact]
    public void FixedHex_RunsOutAfterAllNames()
    {
        var s = new SplitSuffixSequence(false, 1, auto: false, hex: true);
        var l = Take(s, 100);
        Assert.Equal(16, l.Count);
        Assert.Equal("f", l[^1]);
        Assert.Null(s.Next());
    }

    [Fact]
    public void HexFrom_IsRightAligned()
    {
        var s = new SplitSuffixSequence(false, 2, auto: false, from: "fe", hex: true);
        Assert.Equal(new[] { "fe", "ff" }, Take(s, 5));
        var t = new SplitSuffixSequence(false, 3, auto: false, from: "a", hex: true);
        Assert.Equal(new[] { "00a", "00b" }, Take(t, 2));
    }
}
