using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Table-driven grammar tests for <see cref="GnuDateParser"/> with a FIXED "now"
/// (Friday 2024-03-15 10:30:00 UTC, zone UTC). Expected values were produced by GNU
/// <c>date -u -d</c> 9.4 under wsl: relative rows by prefixing the same instant
/// (<c>date -u -d '2024-03-15 10:30:00 UTC 3 hours ago'</c>), absolute rows verbatim, and the
/// now-dependent weekday / year-less rows by checking the same semantics against real "now"
/// (today counts for a bare weekday, <c>next</c> skips today, <c>last</c> goes back, a weekday
/// is ignored once a date is given, a date with no time resets the clock).
/// </summary>
public class GnuDateParserTests
{
    private static readonly DateTimeOffset Now = new(2024, 3, 15, 10, 30, 0, TimeSpan.Zero);

    private static string? Parse(string text)
    {
        return GnuDateParser.TryParse(text, Now, TimeZoneInfo.Utc, out var r)
            ? r.ToString("yyyy-MM-dd HH:mm:ss")
            : null;
    }

    [Theory]
    // relative to the fixed instant (time of day kept)
    [InlineData("3 hours ago", "2024-03-15 07:30:00")]
    [InlineData("yesterday", "2024-03-14 10:30:00")]
    [InlineData("tomorrow", "2024-03-16 10:30:00")]
    [InlineData("next month", "2024-04-15 10:30:00")]
    [InlineData("last month", "2024-02-15 10:30:00")]
    [InlineData("2 weeks ago", "2024-03-01 10:30:00")]
    [InlineData("1 fortnight ago", "2024-03-01 10:30:00")]
    [InlineData("5 minutes ago", "2024-03-15 10:25:00")]
    [InlineData("10 mins ago", "2024-03-15 10:20:00")]
    [InlineData("2 sec ago", "2024-03-15 10:29:58")]
    [InlineData("+90 seconds", "2024-03-15 10:31:30")]
    [InlineData("-2 days", "2024-03-13 10:30:00")]
    [InlineData("3 days", "2024-03-18 10:30:00")]
    [InlineData("1 hour", "2024-03-15 11:30:00")]
    [InlineData("1 year ago", "2023-03-15 10:30:00")]
    [InlineData("last year", "2023-03-15 10:30:00")]
    [InlineData("next year", "2025-03-15 10:30:00")]
    [InlineData("2 months", "2024-05-15 10:30:00")]
    [InlineData("last day", "2024-03-14 10:30:00")]
    [InlineData("next day", "2024-03-16 10:30:00")]
    [InlineData("next week", "2024-03-22 10:30:00")]
    [InlineData("this month", "2024-03-15 10:30:00")]
    [InlineData("now", "2024-03-15 10:30:00")]
    [InlineData("today", "2024-03-15 10:30:00")]
    // `ago` negates only the unit it follows
    [InlineData("2 days 3 hours ago", "2024-03-17 07:30:00")]
    // weekdays: a date-less weekday lands on 00:00; today counts unless `next`
    [InlineData("friday", "2024-03-15 00:00:00")]
    [InlineData("next friday", "2024-03-22 00:00:00")]
    [InlineData("last friday", "2024-03-08 00:00:00")]
    [InlineData("this friday", "2024-03-15 00:00:00")]
    [InlineData("wednesday", "2024-03-20 00:00:00")]
    [InlineData("next wednesday", "2024-03-20 00:00:00")]
    [InlineData("last wednesday", "2024-03-13 00:00:00")]
    [InlineData("sat", "2024-03-16 00:00:00")]
    [InlineData("tuesday 2 weeks ago", "2024-03-05 00:00:00")]
    [InlineData("monday next week", "2024-03-25 00:00:00")]
    // a date with no time resets the clock; year-less dates use the current year
    [InlineData("jan 5", "2024-01-05 00:00:00")]
    [InlineData("5 jan", "2024-01-05 00:00:00")]
    [InlineData("10am", "2024-03-15 10:00:00")]
    [InlineData("5pm tomorrow", "2024-03-16 17:00:00")]
    [InlineData("", "2024-03-15 00:00:00")]
    // absolute forms (verbatim GNU results)
    [InlineData("2024-01-02", "2024-01-02 00:00:00")]
    [InlineData("2024-1-2", "2024-01-02 00:00:00")]
    [InlineData("2024/01/02", "2024-01-02 00:00:00")]
    [InlineData("20240102", "2024-01-02 00:00:00")]
    [InlineData("1/2/2024", "2024-01-02 00:00:00")]
    [InlineData("1/2/24", "2024-01-02 00:00:00")]
    [InlineData("1/2/70", "1970-01-02 00:00:00")]
    [InlineData("Jan 2 2024", "2024-01-02 00:00:00")]
    [InlineData("Jan 2, 2024", "2024-01-02 00:00:00")]
    [InlineData("2 Jan 2024", "2024-01-02 00:00:00")]
    [InlineData("jan 2 2024 3:04pm", "2024-01-02 15:04:00")]
    [InlineData("Mon, 15 Jan 2024 10:00:00 GMT", "2024-01-15 10:00:00")]
    [InlineData("2024-01-02 10:00", "2024-01-02 10:00:00")]
    [InlineData("2024-01-02 10:00:30", "2024-01-02 10:00:30")]
    [InlineData("2024-01-02 10:00:00.5", "2024-01-02 10:00:00")]
    [InlineData("10:00 2024-01-02", "2024-01-02 10:00:00")]
    [InlineData("2024-01-02 12:00 am", "2024-01-02 00:00:00")]
    [InlineData("2024-01-02 12 am", "2024-01-02 00:00:00")]
    [InlineData("2024-01-02 12:30 pm", "2024-01-02 12:30:00")]
    [InlineData("2024-01-02 12 pm", "2024-01-02 12:00:00")]
    [InlineData("2024-01-02 3pm", "2024-01-02 15:00:00")]
    [InlineData("  2024-01-02  ", "2024-01-02 00:00:00")]
    [InlineData("2024-02-29", "2024-02-29 00:00:00")]
    // zones convert to the target zone (UTC here)
    [InlineData("2024-01-02T10:00:00Z", "2024-01-02 10:00:00")]
    [InlineData("2024-01-02T10:00:00+02:00", "2024-01-02 08:00:00")]
    [InlineData("2024-01-02 10:00 -0500", "2024-01-02 15:00:00")]
    [InlineData("2024-01-02 10:00 EST", "2024-01-02 15:00:00")]
    [InlineData("2024-01-02 10:00 PST", "2024-01-02 18:00:00")]
    [InlineData("2024-01-02 10:00 UTC", "2024-01-02 10:00:00")]
    [InlineData("Jan 2 2024 10:00:00 +0100", "2024-01-02 09:00:00")]
    // epoch
    [InlineData("@0", "1970-01-01 00:00:00")]
    [InlineData("@1700000000", "2023-11-14 22:13:20")]
    [InlineData("@-1", "1969-12-31 23:59:59")]
    // mktime-style normalisation of relative months
    [InlineData("2024-01-31 +1 month", "2024-03-02 00:00:00")]
    [InlineData("2024-03-31 -1 month", "2024-03-02 00:00:00")]
    // a weekday is ignored once a date is given
    [InlineData("2024-03-15 next friday", "2024-03-15 00:00:00")]
    [InlineData("2024-03-15 10:30 friday", "2024-03-15 10:30:00")]
    public void Parses(string text, string expected) => Assert.Equal(expected, Parse(text));

    [Theory]
    [InlineData("garbage")]
    [InlineData("not a date")]
    [InlineData("2024-02-30")]
    [InlineData("2023-02-29")]
    [InlineData("2024-13-01")]
    [InlineData("25:00")]
    [InlineData("noon")]
    [InlineData("midnight")]
    [InlineData("2024-01-02 10:00 11:00")]
    [InlineData("2024-01-02 13:00 pm")]
    [InlineData("2024-01-02 0:30 am")]
    [InlineData("@1700000000 +1 day")]
    [InlineData("January 2nd 2024")]
    [InlineData("20240102T101530")]
    public void Rejects(string text) => Assert.Null(Parse(text));

    [Fact]
    public void LocalZone_IsUsedWhenTheTextNamesNone()
    {
        var tz = TimeZoneInfo.CreateCustomTimeZone("t+3", TimeSpan.FromHours(3), "t+3", "t+3");
        Assert.True(GnuDateParser.TryParse("2024-01-02 10:00", Now, tz, out var r));
        Assert.Equal(TimeSpan.FromHours(3), r.Offset);
        Assert.Equal(new DateTimeOffset(2024, 1, 2, 7, 0, 0, TimeSpan.Zero), r.ToUniversalTime());
    }
}
