using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <c>touch -t [[CC]YY]MMDDhhmm[.ss]</c>: the pure parser. Expected instants and rejections are
/// GNU coreutils 9.4 behaviour (<c>wsl bash</c>): a two-digit year 69-99 is 19xx and 00-68 is 20xx,
/// an 8-digit stamp takes the current year, seconds may be 00-60 (60 is a leap second that rolls to
/// the next minute), the calendar is validated (no 2023-02-29), and anything else is
/// <c>invalid date format</c>.
/// </summary>
public class TouchStampTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Local);

    private static DateTime Parse(string stamp)
    {
        Assert.True(TouchStamp.TryParse(stamp, Now, out var t), stamp);
        return t;
    }

    [Theory]
    [InlineData("202401021530", 2024, 1, 2, 15, 30, 0)]
    [InlineData("202401021530.45", 2024, 1, 2, 15, 30, 45)]
    [InlineData("202401021530.00", 2024, 1, 2, 15, 30, 0)]
    [InlineData("202401021530.59", 2024, 1, 2, 15, 30, 59)]
    [InlineData("2401021530", 2024, 1, 2, 15, 30, 0)]
    [InlineData("2401021530.07", 2024, 1, 2, 15, 30, 7)]
    [InlineData("199912312359", 1999, 12, 31, 23, 59, 0)]
    [InlineData("6912312359", 1969, 12, 31, 23, 59, 0)]   // 69 -> 1969
    [InlineData("9912312359", 1999, 12, 31, 23, 59, 0)]
    [InlineData("7001010000", 1970, 1, 1, 0, 0, 0)]
    [InlineData("0001010000", 2000, 1, 1, 0, 0, 0)]
    [InlineData("6801010000", 2068, 1, 1, 0, 0, 0)]       // 68 -> 2068
    [InlineData("01021530", 2026, 1, 2, 15, 30, 0)]       // no year: the current one
    [InlineData("01021530.30", 2026, 1, 2, 15, 30, 30)]
    [InlineData("202402291200", 2024, 2, 29, 12, 0, 0)]   // a real leap day
    [InlineData("202401021530.60", 2024, 1, 2, 15, 31, 0)] // leap second rolls forward
    [InlineData("202412312359.60", 2025, 1, 1, 0, 0, 0)]
    public void TryParse_MatchesGnuTouch(string stamp, int y, int mo, int d, int h, int mi, int s)
    {
        Assert.Equal(new DateTime(y, mo, d, h, mi, s), Parse(stamp));
    }

    [Theory]
    [InlineData("0102153")]            // 7 digits
    [InlineData("20240102153")]        // 11 digits
    [InlineData("2024010215301")]      // 13 digits
    [InlineData("202413021530")]       // month 13
    [InlineData("202400021530")]       // month 0
    [InlineData("202401321530")]       // day 32
    [InlineData("202401001530")]       // day 0
    [InlineData("202401022530")]       // hour 25
    [InlineData("202401022430")]       // hour 24
    [InlineData("202401021560")]       // minute 60
    [InlineData("202401021530.61")]    // second 61
    [InlineData("202401021530.6")]     // one-digit seconds
    [InlineData("202401021530.")]      // dot, no seconds
    [InlineData("202401021530.123")]   // three-digit seconds
    [InlineData("202401021530.45.6")]  // two dots
    [InlineData(".45")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("2024010215a0")]
    [InlineData("202302291200")]       // 2023 is not a leap year
    [InlineData("-202401021530")]
    [InlineData("202401021530 ")]
    public void TryParse_RejectsWhatGnuCallsAnInvalidDateFormat(string stamp)
    {
        Assert.False(TouchStamp.TryParse(stamp, Now, out _));
    }

    [Fact]
    public void TryParse_EightDigitStampUsesTheYearOfTheSuppliedNow()
    {
        Assert.True(TouchStamp.TryParse("02291200", new DateTime(2024, 6, 1), out var t));
        Assert.Equal(new DateTime(2024, 2, 29, 12, 0, 0), t);
        Assert.False(TouchStamp.TryParse("02291200", new DateTime(2023, 6, 1), out _));
    }

    [Theory]
    [InlineData("atime", true, false)]
    [InlineData("access", true, false)]
    [InlineData("use", true, false)]
    [InlineData("mtime", false, true)]
    [InlineData("modify", false, true)]
    [InlineData("a", true, false)]     // prefix of atime/access: one meaning
    [InlineData("at", true, false)]
    [InlineData("u", true, false)]
    [InlineData("m", false, true)]
    [InlineData("mod", false, true)]
    public void TryParseTimeWord_MatchesGnuXargmatch(string word, bool access, bool modify)
    {
        Assert.True(TouchStamp.TryParseTimeWord(word, out var w, out var error), error);
        Assert.Equal(access, w == TouchTimeWord.Access);
        Assert.Equal(modify, w == TouchTimeWord.Modify);
    }

    [Theory]
    [InlineData("bogus", "invalid argument 'bogus' for '--time'")]
    [InlineData("", "ambiguous argument '' for '--time'")]
    [InlineData("atimex", "invalid argument 'atimex' for '--time'")]
    public void TryParseTimeWord_RejectsWithTheGnuUsageMessage(string word, string fragment)
    {
        Assert.False(TouchStamp.TryParseTimeWord(word, out _, out var error));
        Assert.Contains(fragment, error);
        Assert.Contains("'atime', 'access', 'use'", error);
        Assert.Contains("'mtime', 'modify'", error);
        Assert.EndsWith("Try 'touch --help' for more information.", error);
    }
}
