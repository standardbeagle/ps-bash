namespace PsBash.Cmdlets;

/// <summary>
/// The time zone a command formats local time in: the shell's <c>TZ</c> when it names a zone (IANA names such as
/// <c>Asia/Tokyo</c> or <c>US/Eastern</c>, <c>UTC</c>, a leading <c>:</c> allowed), otherwise the machine's zone.
/// glibc reads an unknown name as UTC; so does this.
/// </summary>
public static class BashTimeZone
{
    public static TimeZoneInfo Current() => Resolve(BashVariableStore.Get("TZ"));

    public static TimeZoneInfo Resolve(string? tz)
    {
        if (string.IsNullOrEmpty(tz)) return TimeZoneInfo.Local;
        if (tz[0] == ':') tz = tz[1..];
        if (tz.Length == 0 || tz is "UTC" or "UTC0" or "GMT" or "GMT0" or "Z") return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(tz); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException) { }
        return TimeZoneInfo.Utc;
    }

    /// <summary>The instant <paramref name="epochSeconds"/> (now when negative) as local time in <see cref="Current"/>.</summary>
    public static DateTimeOffset At(long epochSeconds)
    {
        var instant = epochSeconds < 0 ? DateTimeOffset.UtcNow : DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
        return TimeZoneInfo.ConvertTime(instant, Current());
    }
}
