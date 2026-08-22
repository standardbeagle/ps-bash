using System.Globalization;

namespace PsBash.Cmdlets.Media;

/// <summary>
/// Timecode parsing / formatting for the <c>psav</c> media wrapper. Pure and side-effect free so the
/// whole time surface is unit-testable without ffmpeg on PATH.
/// </summary>
/// <remarks>
/// Accepts everything a person naturally types at a shell: plain seconds (<c>7</c>, <c>7.5</c>),
/// clock form (<c>1:02</c>, <c>00:01:02.500</c>), and unit form (<c>90s</c>, <c>1m30s</c>,
/// <c>1h2m3s</c>, <c>250ms</c>). ffmpeg itself only accepts the first two, so every value is
/// re-emitted in the canonical <c>HH:MM:SS.mmm</c> form before it reaches an argv.
/// </remarks>
internal static class AvTime
{
    /// <summary>Parse a timecode to seconds. Returns false (and 0) for anything unparseable.</summary>
    public static bool TryParse(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim();
        var negative = s.StartsWith('-');
        if (negative)
        {
            s = s[1..];
        }

        if (!TryParseMagnitude(s, out seconds))
        {
            return false;
        }

        if (negative)
        {
            seconds = -seconds;
        }

        return true;
    }

    private static bool TryParseMagnitude(string s, out double seconds)
    {
        seconds = 0;
        if (s.Length == 0)
        {
            return false;
        }

        if (s.Contains(':'))
        {
            return TryParseClock(s, out seconds);
        }

        // Unit form: 250ms / 90s / 1m30s / 1h2m3s. Detected by any trailing letter.
        if (char.IsLetter(s[^1]))
        {
            return TryParseUnits(s, out seconds);
        }

        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
    }

    /// <summary>Clock form: <c>SS</c>, <c>MM:SS</c>, or <c>HH:MM:SS</c>, any part fractional.</summary>
    private static bool TryParseClock(string s, out double seconds)
    {
        seconds = 0;
        var parts = s.Split(':');
        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0)
            {
                return false;
            }

            total = (total * 60) + v;
        }

        seconds = total;
        return true;
    }

    /// <summary>Unit form: a run of <c>&lt;number&gt;&lt;unit&gt;</c> pairs, units h/m/s/ms.</summary>
    private static bool TryParseUnits(string s, out double seconds)
    {
        seconds = 0;
        double total = 0;
        var i = 0;
        var matched = false;

        while (i < s.Length)
        {
            var start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.'))
            {
                i++;
            }

            if (i == start)
            {
                return false;   // a unit with no number in front of it
            }

            if (!double.TryParse(s[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            var unitStart = i;
            while (i < s.Length && char.IsLetter(s[i]))
            {
                i++;
            }

            var unit = s[unitStart..i].ToLowerInvariant();
            total += unit switch
            {
                "h" => value * 3600,
                "m" => value * 60,
                "s" => value,
                "ms" => value / 1000,
                _ => double.NaN,
            };

            if (double.IsNaN(total))
            {
                return false;
            }

            matched = true;
        }

        seconds = total;
        return matched;
    }

    /// <summary>Canonical <c>HH:MM:SS.mmm</c> — the only shape handed to ffmpeg.</summary>
    public static string Format(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
        {
            seconds = 0;
        }

        var ts = TimeSpan.FromSeconds(seconds);
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}:{2:00}.{3:000}",
            (int)ts.TotalHours, ts.Minutes, ts.Seconds, ts.Milliseconds);
    }

    /// <summary>Short human form for summary lines: <c>4.2s</c>, <c>1:07</c>, <c>1:02:03</c>.</summary>
    public static string Humanize(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0)
        {
            return "?";
        }

        if (seconds < 60)
        {
            return seconds.ToString("0.#", CultureInfo.InvariantCulture) + "s";
        }

        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)ts.TotalHours, ts.Minutes, ts.Seconds)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", ts.Minutes, ts.Seconds);
    }

    /// <summary>A filename-safe stamp for a timecode (<c>00-01-02.500</c>).</summary>
    public static string FileStamp(double seconds) => Format(seconds).Replace(':', '-');
}
