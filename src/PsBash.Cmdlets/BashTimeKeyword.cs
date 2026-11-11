using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// The <c>time [-p] pipeline</c> keyword. The emitter wraps the timed pipeline as
/// <c>$t = [BashTimeKeyword]::Start(); try { pipeline } finally { Write-BashHostStderr ([BashTimeKeyword]::Report($t, posix)) }</c>,
/// so the pipeline keeps its own redirects and status, and the report goes to the error stream (an enclosing
/// <c>2&gt;</c> redirects it). User and system times are this host process's CPU time, which includes the
/// in-process cmdlets but not native children.
/// </summary>
public static class BashTimeKeyword
{
    private const string DefaultFormat = "\nreal\t%3lR\nuser\t%3lU\nsys\t%3lS";
    private const string PosixFormat = "real %2R\nuser %2U\nsys %2S";

    public sealed class Mark
    {
        internal readonly Stopwatch Wall = Stopwatch.StartNew();
        internal readonly TimeSpan User;
        internal readonly TimeSpan Sys;
        internal Mark(TimeSpan user, TimeSpan sys) { User = user; Sys = sys; }
    }

    public static Mark Start()
    {
        var (user, sys) = CpuTimes();
        return new Mark(user, sys);
    }

    /// <summary>The report text without its final newline, or null when <c>TIMEFORMAT</c> is set and empty.</summary>
    public static string? Report(Mark mark, bool posix)
    {
        double real = mark.Wall.Elapsed.TotalSeconds;
        var (user, sys) = CpuTimes();
        string? custom = BashVariableStore.Get("TIMEFORMAT");
        string format = posix ? PosixFormat : custom ?? DefaultFormat;
        if (format.Length == 0) return null;
        return Format(format, real, (user - mark.User).TotalSeconds, (sys - mark.Sys).TotalSeconds);
    }

    /// <summary>bash's TIMEFORMAT: <c>%[p][l]R|U|S</c> (p = 0-3 decimals, default 3; l = <c>MmS.FFFs</c>), <c>%P</c>, <c>%%</c>.</summary>
    public static string Format(string format, double real, double user, double sys)
    {
        var sb = new StringBuilder(format.Length + 16);
        for (int i = 0; i < format.Length; i++)
        {
            char c = format[i];
            if (c != '%' || i + 1 >= format.Length) { sb.Append(c); continue; }
            int j = i + 1;
            if (format[j] == '%') { sb.Append('%'); i = j; continue; }
            if (format[j] == 'P')
            {
                double pct = real > 0 ? (user + sys) / real * 100 : 0;
                sb.Append(pct.ToString("F2", CultureInfo.InvariantCulture));
                i = j;
                continue;
            }
            int precision = 3;
            if (char.IsAsciiDigit(format[j])) { precision = Math.Min(3, format[j] - '0'); j++; }
            bool longForm = j < format.Length && format[j] == 'l';
            if (longForm) j++;
            if (j >= format.Length || format[j] is not ('R' or 'U' or 'S')) { sb.Append(c); continue; }
            double value = format[j] switch { 'R' => real, 'U' => user, _ => sys };
            if (value < 0) value = 0;
            sb.Append(longForm ? LongForm(value, precision) : Fixed(value, precision));
            i = j;
        }
        return sb.ToString();
    }

    private static string Fixed(double seconds, int precision) =>
        Truncate(seconds, precision).ToString("F" + precision, CultureInfo.InvariantCulture);

    private static string LongForm(double seconds, int precision)
    {
        seconds = Truncate(seconds, precision);
        long minutes = (long)(seconds / 60);
        double rest = seconds - minutes * 60;
        return minutes.ToString(CultureInfo.InvariantCulture) + "m" + rest.ToString("F" + precision, CultureInfo.InvariantCulture) + "s";
    }

    // bash truncates the fraction rather than rounding it.
    private static double Truncate(double seconds, int precision)
    {
        double scale = Math.Pow(10, precision);
        return Math.Floor(seconds * scale + 1e-6) / scale; // +1e-6: 0.29 * 100 is 28.999…
    }

    private static (TimeSpan User, TimeSpan Sys) CpuTimes()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            return (p.UserProcessorTime, p.PrivilegedProcessorTime);
        }
        catch (Exception) { return (TimeSpan.Zero, TimeSpan.Zero); }
    }
}
