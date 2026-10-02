using System.Globalization;

namespace PsBash.Cmdlets;

/// <summary>One <c>ls -l</c> row before alignment: every column already rendered as text.</summary>
internal readonly record struct LsLongRow(string Perms, string Links, string Owner, string Group, string Size, string Date, string Name);

/// <summary>The <c>--time-style</c> of a long listing (GNU 9.4). Locale is the default <c>Mon dd HH:mm</c> form.</summary>
internal enum LsTimeStyle { Locale, FullIso, LongIso, Iso }

/// <summary>
/// The pure column layout of <c>ls -l</c> (GNU coreutils 9.4, oracle-checked): within ONE listing block the
/// link count and size are right-aligned and owner and group left-aligned to the widest entry of that block, one
/// space between columns; the date is <c>Mon dd HH:mm</c> for a file modified in the last six months and not in
/// the future, else <c>Mon dd  yyyy</c>. Blocks are the unit — each directory section (<c>-R</c>) and the
/// operand-file block align independently, so no width ever leaks between them. <c>-g</c> / <c>-o</c> / <c>-G</c>
/// drop the owner / group column; <c>--time-style</c> picks the date form (<see cref="Date(DateTime, DateTime, LsTimeStyle)"/>).
/// </summary>
internal static class LsLongFormat
{
    /// <summary>GNU's "six months": 182.62 days in seconds (ls.c <c>six_months_ago = now - 31556952 / 2</c>).</summary>
    private const double SixMonthsSeconds = 31556952.0 / 2;

    public static string Date(DateTime date, DateTime now) => Date(date, now, LsTimeStyle.Locale);

    /// <summary>
    /// The time column: <c>locale</c> = <c>Mon dd HH:mm</c> (recent) / <c>Mon dd  yyyy</c>; <c>iso</c> = <c>MM-dd HH:mm</c> /
    /// <c>yyyy-MM-dd </c>; <c>long-iso</c> = <c>yyyy-MM-dd HH:mm</c>; <c>full-iso</c> = <c>yyyy-MM-dd HH:mm:ss.nnnnnnnnn +zzzz</c>.
    /// "Recent" is within six months and not in the future (the same test for the locale and iso styles).
    /// </summary>
    public static string Date(DateTime date, DateTime now, LsTimeStyle style)
    {
        var inv = CultureInfo.InvariantCulture;
        switch (style)
        {
            case LsTimeStyle.FullIso:
            {
                var offset = TimeZoneInfo.Local.GetUtcOffset(date);
                string sign = offset < TimeSpan.Zero ? "-" : "+";
                var abs = offset.Duration();
                // .NET keeps 100 ns ticks: the ninth digit GNU prints is always 0 here.
                return date.ToString("yyyy-MM-dd HH:mm:ss.fffffff", inv) + "00 "
                    + sign + abs.Hours.ToString("00", inv) + abs.Minutes.ToString("00", inv);
            }
            case LsTimeStyle.LongIso:
                return date.ToString("yyyy-MM-dd HH:mm", inv);
        }

        bool recent = !(date < now.AddSeconds(-SixMonthsSeconds) || date > now);
        if (style == LsTimeStyle.Iso)
            return recent ? date.ToString("MM-dd HH:mm", inv) : date.ToString("yyyy-MM-dd", inv) + " ";

        string month = date.ToString("MMM", inv);
        string day = date.Day.ToString(inv).PadLeft(2);
        return recent
            ? $"{month} {day} {date.ToString("HH:mm", inv)}"
            : $"{month} {day}  {date.Year}";
    }

    /// <summary>Align one block; returns the lines (the name last, so a classify indicator can be appended).</summary>
    public static string[] Align(IReadOnlyList<LsLongRow> rows, bool showOwner = true, bool showGroup = true)
    {
        int links = 0, owner = 0, group = 0, size = 0;
        foreach (var r in rows)
        {
            links = Math.Max(links, r.Links.Length);
            owner = Math.Max(owner, r.Owner.Length);
            group = Math.Max(group, r.Group.Length);
            size = Math.Max(size, r.Size.Length);
        }

        var lines = new string[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            string ownerCol = showOwner ? r.Owner.PadRight(owner) + " " : "";
            string groupCol = showGroup ? r.Group.PadRight(group) + " " : "";
            lines[i] = string.Concat(
                r.Perms, " ", r.Links.PadLeft(links), " ", ownerCol, groupCol,
                r.Size.PadLeft(size), " ", r.Date, " ", r.Name);
        }
        return lines;
    }
}
