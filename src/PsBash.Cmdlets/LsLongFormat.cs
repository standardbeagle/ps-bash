using System.Globalization;

namespace PsBash.Cmdlets;

/// <summary>One <c>ls -l</c> row before alignment: every column already rendered as text.</summary>
internal readonly record struct LsLongRow(string Perms, string Links, string Owner, string Group, string Size, string Date, string Name);

/// <summary>
/// The pure column layout of <c>ls -l</c> (GNU coreutils 9.4, oracle-checked): within ONE listing block the
/// link count and size are right-aligned and owner and group left-aligned to the widest entry of that block, one
/// space between columns; the date is <c>Mon dd HH:mm</c> for a file modified in the last six months and not in
/// the future, else <c>Mon dd  yyyy</c>. Blocks are the unit — each directory section (<c>-R</c>) and the
/// operand-file block align independently, so no width ever leaks between them.
/// </summary>
internal static class LsLongFormat
{
    /// <summary>GNU's "six months": 182.62 days in seconds (ls.c <c>six_months_ago = now - 31556952 / 2</c>).</summary>
    private const double SixMonthsSeconds = 31556952.0 / 2;

    public static string Date(DateTime date, DateTime now)
    {
        string month = date.ToString("MMM", CultureInfo.InvariantCulture);
        string day = date.Day.ToString(CultureInfo.InvariantCulture).PadLeft(2);

        if (date < now.AddSeconds(-SixMonthsSeconds) || date > now)
            return $"{month} {day}  {date.Year}";
        return $"{month} {day} {date.ToString("HH:mm", CultureInfo.InvariantCulture)}";
    }

    /// <summary>Align one block; returns the lines (the name last, so a classify indicator can be appended).</summary>
    public static string[] Align(IReadOnlyList<LsLongRow> rows)
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
            lines[i] = string.Concat(
                r.Perms, " ", r.Links.PadLeft(links), " ", r.Owner.PadRight(owner), " ", r.Group.PadRight(group), " ",
                r.Size.PadLeft(size), " ", r.Date, " ", r.Name);
        }
        return lines;
    }
}
