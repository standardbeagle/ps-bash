using PsBash.Cmdlets.Args;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>GNU NUM parsing (head/tail -n/-c): sign, digits, multiplier suffix; oracle = coreutils 9.4.</summary>
public class GnuNumberTests
{
    [Theory]
    [InlineData("0", 0, '\0')]
    [InlineData("5", 5, '\0')]
    [InlineData("+5", 5, '+')]
    [InlineData("-5", 5, '-')]
    [InlineData("1b", 512, '\0')]
    [InlineData("1k", 1024, '\0')]
    [InlineData("1K", 1024, '\0')]
    [InlineData("1KiB", 1024, '\0')]
    [InlineData("1kB", 1000, '\0')]
    [InlineData("1KB", 1000, '\0')]
    [InlineData("1m", 1048576, '\0')]           // oracle: head -c 1m = 1048576
    [InlineData("2M", 2 * 1024 * 1024, '\0')]
    [InlineData("2MB", 2_000_000, '\0')]
    [InlineData("1G", 1024 * 1024 * 1024, '\0')]
    [InlineData("-1K", 1024, '-')]
    [InlineData("+1K", 1024, '+')]
    [InlineData("4G", int.MaxValue, '\0')]      // saturates
    [InlineData("1T", int.MaxValue, '\0')]
    [InlineData("1P", int.MaxValue, '\0')]
    [InlineData("0P", 0, '\0')]
    [InlineData("99999999999999999999", int.MaxValue, '\0')]
    public void Valid(string s, int magnitude, char sign)
    {
        Assert.True(GnuNumber.TryParse(s, out int m, out char sg));
        Assert.Equal(magnitude, m);
        Assert.Equal(sign, sg);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData("1x")]
    [InlineData("1Kx")]
    [InlineData("1kb")]   // GNU is case sensitive: kB, not kb
    [InlineData("1t")]    // oracle: only lowercase b, k, m are suffixes
    [InlineData("1g")]
    [InlineData("1bB")]
    [InlineData("1MiBx")]
    [InlineData("K")]
    [InlineData("1 ")]
    [InlineData(" 1")]
    [InlineData("1.5")]
    public void Invalid(string s)
    {
        Assert.False(GnuNumber.TryParse(s, out _, out _));
    }
}
