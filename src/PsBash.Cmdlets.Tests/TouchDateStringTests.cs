using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <c>touch -d STRING</c> / <c>date -d STRING</c> take GNU <c>parse_datetime</c> strings
/// (relative items, weekdays, epoch, zones), not just what <c>DateTime.Parse</c> knows.
/// The unit-level grammar table is <see cref="GnuDateParserTests"/>; this is the end-to-end
/// proof through both cmdlets. Times are compared with a tolerance because "now" moves.
/// </summary>
public class TouchDateStringTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _tmp;
    private readonly SharedPwshFixture _fixture;

    public TouchDateStringTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmp = Path.Combine(Path.GetTempPath(), $"psb-tds-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    private CmdResult InTmp(string body) => CmdResult.Run(_fixture.AcquireFresh(),
        $"Push-Location '{_tmp}'; try {{ {body} }} finally {{ Pop-Location }}");

    private string P(string rel) => Path.Combine(_tmp, rel);

    private static void Near(DateTime expected, DateTime actual, double seconds = 120) =>
        Assert.True(Math.Abs((expected - actual).TotalSeconds) < seconds,
            $"expected ~{expected:O}, got {actual:O}");

    [Fact]
    public void TouchD_HoursAgo()
    {
        InTmp("Invoke-BashTouch -d '3 hours ago' f").AssertSuccess();
        Near(DateTime.Now.AddHours(-3), File.GetLastWriteTime(P("f")));
    }

    [Fact]
    public void TouchD_Yesterday_KeepsTimeOfDay()
    {
        InTmp("Invoke-BashTouch -d yesterday f").AssertSuccess();
        Near(DateTime.Now.AddDays(-1), File.GetLastWriteTime(P("f")));
    }

    [Fact]
    public void TouchD_NextFriday_IsAFridayAtMidnightInTheFuture()
    {
        InTmp("Invoke-BashTouch -d 'next friday' f").AssertSuccess();
        var t = File.GetLastWriteTime(P("f"));
        Assert.Equal(DayOfWeek.Friday, t.DayOfWeek);
        Assert.Equal(TimeSpan.Zero, t.TimeOfDay);
        Assert.True(t.Date > DateTime.Now.Date);
        Assert.True(t.Date <= DateTime.Now.Date.AddDays(7));
    }

    [Fact]
    public void TouchD_IsoDateTimeWithoutSeconds()
    {
        InTmp("Invoke-BashTouch -d '2024-01-02 10:00' f").AssertSuccess();
        Near(new DateTime(2024, 1, 2, 10, 0, 0), File.GetLastWriteTime(P("f")), 2);
    }

    [Fact]
    public void TouchD_Epoch()
    {
        InTmp("Invoke-BashTouch -d '@1700000000' f").AssertSuccess();
        Near(DateTimeOffset.FromUnixTimeSeconds(1700000000).LocalDateTime, File.GetLastWriteTime(P("f")), 2);
    }

    [Fact]
    public void TouchD_Garbage_IsInvalidDate_Exit1()
    {
        InTmp("Invoke-BashTouch -d 'not a date' f").AssertFailed(1, "invalid date format 'not a date'");
        Assert.False(File.Exists(P("f")));
    }

    [Fact]
    public void DateD_RelativeAndWeekdayForms()
    {
        var r = InTmp("Invoke-BashDate -d '2024-03-15 10:30:00 UTC 2 days ago' -u '+%F %T'").AssertSuccess();
        Assert.Equal("2024-03-13 10:30:00", r.Stdout.Trim());
        r = InTmp("Invoke-BashDate -d '@0' -u '+%F %T'").AssertSuccess();
        Assert.Equal("1970-01-01 00:00:00", r.Stdout.Trim());
        r = InTmp("Invoke-BashDate -d '2024-01-02T10:00:00Z' -u '+%F %T'").AssertSuccess();
        Assert.Equal("2024-01-02 10:00:00", r.Stdout.Trim());
    }
}
