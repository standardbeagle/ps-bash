using System.Globalization;
using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

/// <summary>
/// The date-string grammar of GNU <c>parse_datetime</c> (what <c>date -d</c> and <c>touch -d</c>
/// accept), shared by both cmdlets. Pure: "now" and the local zone are parameters.
///
/// <para>Supported items (any order, whitespace/comma separated, case-insensitive):</para>
/// <list type="bullet">
/// <item><c>@SECONDS</c> (Unix epoch, the only item allowed with it).</item>
/// <item>Dates: <c>YYYY-MM-DD</c>, <c>YYYY/MM/DD</c>, <c>MM/DD[/YY[YY]]</c>, <c>YYYYMMDD</c>,
/// <c>Mon DD[, YYYY]</c>, <c>DD Mon [YYYY]</c> (full or abbreviated month names).</item>
/// <item>Times: <c>HH:MM[:SS[.frac]]</c> with optional am/pm, <c>H am</c> (GNU 9.4 rejects <c>noon</c>/<c>midnight</c>, so do we);
/// an ISO <c>T</c> separator; zones <c>Z</c>/<c>UTC</c>/<c>GMT</c>/<c>UT</c>, <c>+HH[:MM]</c>/<c>+HHMM</c>
/// (after a time) and the US names EST/EDT/CST/CDT/MST/MDT/PST/PDT.</item>
/// <item>Relative: <c>[+-]N unit[s] [ago]</c>, <c>next|last|this|previous unit</c>, and
/// <c>now</c>/<c>today</c>/<c>yesterday</c>/<c>tomorrow</c>; units year, month, fortnight, week, day,
/// hour, minute/min, second/sec. <c>ago</c> negates the unit directly before it (GNU).</item>
/// <item>Weekdays: <c>friday</c>, <c>next friday</c>, <c>last fri</c>, <c>this mon</c>.</item>
/// </list>
///
/// <para>Combination rules copied from gnulib: a string with a date, a weekday or a time but no time of
/// day resets the clock to 00:00:00; a string of only relative items keeps the current time of day
/// (<c>yesterday</c> is yesterday NOW); relative years/months/days are added to the broken-down fields
/// and normalised like <c>mktime</c> (Jan 31 + 1 month = Mar 2/3); a weekday moves to the next
/// occurrence (today counts) and only applies when no explicit date was given. An impossible
/// calendar date (Feb 30) is invalid.</para>
/// </summary>
internal static class GnuDateParser
{
    private const RegexOptions Ro = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private const string Months =
        "january|jan|february|feb|march|mar|april|apr|may|june|jun|july|jul|august|aug|" +
        "september|sept|sep|october|oct|november|nov|december|dec";
    private const string Weekdays =
        "sunday|sun|monday|mon|tuesday|tues|tue|wednesday|wednes|wed|thursday|thurs|thur|thu|" +
        "friday|fri|saturday|sat";
    private const string Units =
        "years?|months?|fortnights?|weeks?|days?|hours?|hrs?|minutes?|mins?|seconds?|secs?";

    private static Regex R(string p) => new(@"\G" + p, Ro);

    private static readonly Regex Epoch = R(@"@(-?\d+)(?:[.,]\d+)?");
    private static readonly Regex IsoDate = R(@"(\d{4})-(\d{1,2})-(\d{1,2})(?!\d)");
    private static readonly Regex SlashYmd = R(@"(\d{4})/(\d{1,2})/(\d{1,2})(?!\d)");
    private static readonly Regex Compact = R(@"(\d{4})(\d{2})(\d{2})(?!\d)");
    private static readonly Regex UsDate = R(@"(\d{1,2})/(\d{1,2})(?:/(\d{2,4}))?(?!\d)");
    private static readonly Regex TimeColon = R(@"(\d{1,2}):(\d{2})(?::(\d{2})(?:[.,](\d+))?)?(?:\s*([ap])\.?m\b\.?)?(?![\d:])");
    private static readonly Regex TimeAmPm = R(@"(\d{1,2})\s*([ap])\.?m\b\.?");
    private static readonly Regex RelNumber = R(@"([+-]?)(\d+)\s*(" + Units + @")\b(?:\s+(ago)\b)?");
    private static readonly Regex RelNext = R(@"(next|last|this|previous)\s+(?:(" + Units + @")|(" + Weekdays + @"))\b");
    private static readonly Regex Keyword = R(@"(now|today|yesterday|tomorrow)\b");
    private static readonly Regex MonthDay = R(@"(" + Months + @")\b\.?\s*(\d{1,2})(?!\d)(?:\s*,?\s*(\d{4})(?![\d:]))?");
    private static readonly Regex DayMonth = R(@"(\d{1,2})[\s-]+(" + Months + @")\b\.?(?:[\s,-]+(\d{4})(?![\d:]))?");
    private static readonly Regex Weekday = R(@"(" + Weekdays + @")\b\.?");
    private static readonly Regex ZoneName = R(@"(z|utc|gmt|ut|est|edt|cst|cdt|mst|mdt|pst|pdt)\b");
    private static readonly Regex ZoneOffset = R(@"([+-])(\d{1,2})(?::?(\d{2}))?(?!\d)");
    private static readonly Regex TSeparator = R(@"t(?=\d)");

    /// <summary>
    /// Parses <paramref name="text"/>. On success <paramref name="result"/> is the instant, expressed in
    /// <paramref name="tz"/>.
    /// </summary>
    public static bool TryParse(string text, DateTimeOffset now, TimeZoneInfo tz, out DateTimeOffset result)
    {
        result = default;
        try { return Parse(text, now, tz, out result); }
        catch (OverflowException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static bool Parse(string text, DateTimeOffset now, TimeZoneInfo tz, out DateTimeOffset result)
    {
        result = default;
        var nowLocal = TimeZoneInfo.ConvertTime(now, tz);

        int year = nowLocal.Year, month = nowLocal.Month, day = nowLocal.Day;
        int hour = nowLocal.Hour, minute = nowLocal.Minute, second = nowLocal.Second;
        long ticks = nowLocal.Ticks % TimeSpan.TicksPerSecond;

        bool datesSeen = false, timesSeen = false, daysSeen = false, relsSeen = false, zoneSeen = false;
        long? epoch = null;
        TimeSpan zone = TimeSpan.Zero;
        long relYear = 0, relMonth = 0, relDay = 0, relHour = 0, relMin = 0, relSec = 0;
        int dayNumber = -1, dayOrdinal = 0;

        int pos = 0;
        while (true)
        {
            while (pos < text.Length && (char.IsWhiteSpace(text[pos]) || text[pos] == ',')) pos++;
            if (pos >= text.Length) break;

            Match m;

            if ((m = Epoch.Match(text, pos)).Success)
            {
                if (epoch is not null) return false;
                epoch = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            else if ((m = IsoDate.Match(text, pos)).Success)
            {
                if (datesSeen) return false;
                datesSeen = true;
                year = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                month = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                day = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                if (!ValidDate(year, month, day)) return false;
            }
            else if ((m = SlashYmd.Match(text, pos)).Success)
            {
                if (datesSeen) return false;
                datesSeen = true;
                year = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                month = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                day = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                if (!ValidDate(year, month, day)) return false;
            }
            else if ((m = Compact.Match(text, pos)).Success)
            {
                if (datesSeen) return false;
                datesSeen = true;
                year = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                month = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                day = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                if (!ValidDate(year, month, day)) return false;
            }
            else if ((m = TimeColon.Match(text, pos)).Success)
            {
                if (timesSeen) return false;
                timesSeen = true;
                hour = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                minute = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                second = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
                ticks = m.Groups[4].Success ? FractionTicks(m.Groups[4].Value) : 0;
                if (m.Groups[5].Success && !ApplyMeridian(ref hour, m.Groups[5].Value)) return false;
                if (!ValidTime(hour, minute, second)) return false;
            }
            else if ((m = RelNumber.Match(text, pos)).Success)
            {
                long n = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                if (m.Groups[1].Value == "-") n = -n;
                if (m.Groups[4].Success) n = -n;
                AddRel(m.Groups[3].Value, n, ref relYear, ref relMonth, ref relDay, ref relHour, ref relMin, ref relSec);
                relsSeen = true;
            }
            else if ((m = TimeAmPm.Match(text, pos)).Success)
            {
                if (timesSeen) return false;
                timesSeen = true;
                hour = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                minute = 0; second = 0; ticks = 0;
                if (!ApplyMeridian(ref hour, m.Groups[2].Value)) return false;
            }
            else if ((m = RelNext.Match(text, pos)).Success)
            {
                int ord = m.Groups[1].Value.ToLowerInvariant() switch { "next" => 1, "this" => 0, _ => -1 };
                if (m.Groups[2].Success)
                {
                    AddRel(m.Groups[2].Value, ord, ref relYear, ref relMonth, ref relDay, ref relHour, ref relMin, ref relSec);
                    relsSeen = true;
                }
                else
                {
                    if (daysSeen) return false;
                    daysSeen = true;
                    dayNumber = WeekdayNumber(m.Groups[3].Value);
                    dayOrdinal = ord;
                }
            }
            else if ((m = Keyword.Match(text, pos)).Success)
            {
                switch (m.Groups[1].Value.ToLowerInvariant())
                {
                    case "yesterday": relDay -= 1; relsSeen = true; break;
                    case "tomorrow": relDay += 1; relsSeen = true; break;
                    case "now": case "today": relsSeen = true; break;
                }
            }
            else if ((m = UsDate.Match(text, pos)).Success)
            {
                if (datesSeen) return false;
                datesSeen = true;
                month = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                day = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                if (m.Groups[3].Success) year = ExpandYear(m.Groups[3].Value);
                if (!ValidDate(year, month, day)) return false;
            }
            else if ((m = MonthDay.Match(text, pos)).Success)
            {
                if (datesSeen) return false;
                datesSeen = true;
                month = MonthNumber(m.Groups[1].Value);
                day = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                if (m.Groups[3].Success) year = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                if (!ValidDate(year, month, day)) return false;
            }
            else if ((m = DayMonth.Match(text, pos)).Success)
            {
                if (datesSeen) return false;
                datesSeen = true;
                day = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                month = MonthNumber(m.Groups[2].Value);
                if (m.Groups[3].Success) year = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                if (!ValidDate(year, month, day)) return false;
            }
            else if ((m = Weekday.Match(text, pos)).Success)
            {
                if (daysSeen) return false;
                daysSeen = true;
                dayNumber = WeekdayNumber(m.Groups[1].Value);
                dayOrdinal = 0;
            }
            else if ((m = ZoneName.Match(text, pos)).Success)
            {
                if (zoneSeen) return false;
                zoneSeen = true;
                zone = m.Groups[1].Value.ToLowerInvariant() switch
                {
                    "est" => TimeSpan.FromHours(-5), "edt" => TimeSpan.FromHours(-4),
                    "cst" => TimeSpan.FromHours(-6), "cdt" => TimeSpan.FromHours(-5),
                    "mst" => TimeSpan.FromHours(-7), "mdt" => TimeSpan.FromHours(-6),
                    "pst" => TimeSpan.FromHours(-8), "pdt" => TimeSpan.FromHours(-7),
                    _ => TimeSpan.Zero,
                };
            }
            else if (timesSeen && !zoneSeen && (m = ZoneOffset.Match(text, pos)).Success)
            {
                zoneSeen = true;
                int hh = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                int mm = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
                if (hh > 14 || mm > 59) return false;
                zone = new TimeSpan(hh, mm, 0);
                if (m.Groups[1].Value == "-") zone = -zone;
            }
            else if ((m = TSeparator.Match(text, pos)).Success)
            {
                // ISO 8601 "2024-01-02T10:00": the T only separates the two items.
            }
            else
            {
                return false;
            }

            pos = m.Index + m.Length;
        }

        if (epoch is not null)
        {
            if (datesSeen || timesSeen || daysSeen || relsSeen || zoneSeen) return false;
            result = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(epoch.Value), tz);
            return true;
        }

        // Time of day: only kept when a time was given or the string is purely relative.
        if (!(timesSeen || (relsSeen && !datesSeen && !daysSeen)))
        {
            hour = 0; minute = 0; second = 0; ticks = 0;
        }

        // Broken-down arithmetic, normalised like mktime.
        long totalMonths = (long)year * 12 + (month - 1) + relYear * 12 + relMonth;
        int y = (int)Math.DivRem(totalMonths, 12, out long mRem);
        if (mRem < 0) { mRem += 12; y--; }
        var dt = new DateTime(y, (int)mRem + 1, 1).AddDays(day - 1 + relDay)
            .AddHours(hour + relHour).AddMinutes(minute + relMin).AddSeconds(second + relSec);

        if (daysSeen && !datesSeen)
        {
            int wday = (int)dt.DayOfWeek;
            int adj = ((dayNumber - wday + 7) % 7) + 7 * (dayOrdinal - ((0 < dayOrdinal && wday != dayNumber) ? 1 : 0));
            dt = dt.AddDays(adj);
        }
        dt = dt.AddTicks(ticks);

        var offset = zoneSeen ? zone : tz.GetUtcOffset(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified));
        result = TimeZoneInfo.ConvertTime(new DateTimeOffset(dt, offset), tz);
        return true;
    }

    private static void AddRel(string unit, long n,
        ref long years, ref long months, ref long days, ref long hours, ref long mins, ref long secs)
    {
        switch (char.ToLowerInvariant(unit[0]))
        {
            case 'y': years += n; break;
            case 'f': days += 14 * n; break;
            case 'w': days += 7 * n; break;
            case 'd': days += n; break;
            case 'h': hours += n; break;
            case 's': secs += n; break;
            default: // 'm': month(s) vs min(s)/minute(s)
                if (unit.StartsWith("mo", StringComparison.OrdinalIgnoreCase)) months += n;
                else mins += n;
                break;
        }
    }

    private static bool ApplyMeridian(ref int hour, string ap)
    {
        if (hour < 1 || hour > 12) return false;
        bool pm = char.ToLowerInvariant(ap[0]) == 'p';
        hour = hour % 12 + (pm ? 12 : 0);
        return true;
    }

    private static bool ValidDate(int year, int month, int day) =>
        year >= 1 && year <= 9999 && month is >= 1 and <= 12 && day >= 1
        && day <= DateTime.DaysInMonth(year, month);

    private static bool ValidTime(int h, int m, int s) => h <= 23 && m <= 59 && s <= 59;

    private static int ExpandYear(string s)
    {
        int v = int.Parse(s, CultureInfo.InvariantCulture);
        if (s.Length >= 3) return v;
        return v >= 69 ? 1900 + v : 2000 + v;
    }

    private static long FractionTicks(string digits)
    {
        if (digits.Length > 7) digits = digits[..7];
        return long.Parse(digits.PadRight(7, '0'), CultureInfo.InvariantCulture);
    }

    private static int MonthNumber(string name) => name.ToLowerInvariant()[..3] switch
    {
        "jan" => 1, "feb" => 2, "mar" => 3, "apr" => 4, "may" => 5, "jun" => 6,
        "jul" => 7, "aug" => 8, "sep" => 9, "oct" => 10, "nov" => 11, _ => 12,
    };

    private static int WeekdayNumber(string name) => name.ToLowerInvariant()[..3] switch
    {
        "sun" => 0, "mon" => 1, "tue" => 2, "wed" => 3, "thu" => 4, "fri" => 5, _ => 6,
    };
}
