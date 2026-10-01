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
}
