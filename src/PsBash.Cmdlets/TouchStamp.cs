namespace PsBash.Cmdlets;

/// <summary>Which timestamp <c>touch --time=WORD</c> names.</summary>
internal enum TouchTimeWord
{
    /// <summary><c>atime</c> / <c>access</c> / <c>use</c> — same as <c>-a</c>.</summary>
    Access,

    /// <summary><c>mtime</c> / <c>modify</c> — same as <c>-m</c>.</summary>
    Modify,
}

/// <summary>
/// The pure parts of <c>touch -t</c> and <c>touch --time</c>.
/// <para>
/// <c>-t [[CC]YY]MMDDhhmm[.ss]</c>: an 8, 10 or 12 digit stamp with an optional <c>.ss</c>. Eight
/// digits take the current year; ten take a two-digit year (69-99 = 19xx, 00-68 = 20xx, POSIX);
/// twelve a four-digit one. The calendar is validated (no 2023-02-29, no hour 24), seconds may be
/// 00-60 and 60 rolls to the next minute (a leap second, as <c>mktime</c> does). The result is LOCAL
/// time. Everything else — wrong length, a stray character, a one- or three-digit seconds field — is
/// GNU's <c>invalid date format</c>. All of it oracle-checked against coreutils 9.4.
/// </para>
/// </summary>
internal static class TouchStamp
{
    public static bool TryParse(string stamp, DateTime now, out DateTime result)
    {
        result = default;

        string main = stamp;
        int seconds = 0;
        int dot = stamp.IndexOf('.');
        if (dot >= 0)
        {
            // Exactly one dot, followed by exactly two digits.
            if (stamp.Length - dot != 3 || stamp.IndexOf('.', dot + 1) >= 0) return false;
            if (!IsDigit(stamp[dot + 1]) || !IsDigit(stamp[dot + 2])) return false;
            seconds = (stamp[dot + 1] - '0') * 10 + (stamp[dot + 2] - '0');
            main = stamp.Substring(0, dot);
        }

        if (main.Length != 8 && main.Length != 10 && main.Length != 12) return false;
        foreach (var c in main)
        {
            if (!IsDigit(c)) return false;
        }
        if (seconds > 60) return false;

        int year;
        int pos = 0;
        switch (main.Length)
        {
            case 12:
                year = Two(main, 0) * 100 + Two(main, 2);
                pos = 4;
                break;
            case 10:
            {
                int yy = Two(main, 0);
                year = yy >= 69 ? 1900 + yy : 2000 + yy;
                pos = 2;
                break;
            }
            default:
                year = now.Year;
                break;
        }

        int month = Two(main, pos), day = Two(main, pos + 2), hour = Two(main, pos + 4), minute = Two(main, pos + 6);
        if (year < 1 || year > 9999) return false;
        if (month < 1 || month > 12) return false;
        if (day < 1 || day > DateTime.DaysInMonth(year, month)) return false;
        if (hour > 23 || minute > 59) return false;

        // Second 60 is a leap second: build the :59 instant and step forward, as mktime normalizes.
        var t = new DateTime(year, month, day, hour, minute, Math.Min(seconds, 59), DateTimeKind.Local);
        result = seconds == 60 ? t.AddSeconds(1) : t;
        return true;
    }

    private static readonly (string Name, TouchTimeWord Value)[] TimeWords =
    {
        ("atime", TouchTimeWord.Access), ("access", TouchTimeWord.Access), ("use", TouchTimeWord.Access),
        ("mtime", TouchTimeWord.Modify), ("modify", TouchTimeWord.Modify),
    };

    /// <summary>GNU <c>--time=WORD</c> (<c>XARGMATCH</c>): the five names and their unambiguous prefixes.</summary>
    public static bool TryParseTimeWord(string word, out TouchTimeWord value, out string? error) =>
        GnuArgMatch.TryMatch("touch", "time", word, TimeWords,
            "  - 'atime', 'access', 'use'\n  - 'mtime', 'modify'", out value, out error);

    private static bool IsDigit(char c) => c >= '0' && c <= '9';

    private static int Two(string s, int at) => (s[at] - '0') * 10 + (s[at + 1] - '0');
}
