using System.Globalization;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>The figures GNU <c>time</c> reports for one run.</summary>
/// <param name="Elapsed">Wall-clock time.</param>
/// <param name="User">User CPU time.</param>
/// <param name="System">System CPU time.</param>
/// <param name="MaxRssKb">Peak resident set, KiB.</param>
/// <param name="ExitStatus">The command's exit status.</param>
/// <param name="CommandLine">The command and its arguments, space-joined (<c>%C</c>).</param>
public readonly record struct GnuTimeStats(TimeSpan Elapsed, TimeSpan User, TimeSpan System, long MaxRssKb, int ExitStatus, string CommandLine);

/// <summary>
/// The external GNU <c>time</c> (1.9) options and report: <c>time [-p] [-q] [-f FORMAT] [--] COMMAND [ARG...]</c>,
/// as <c>command time</c> and <c>env time</c> reach it (the <c>time</c> keyword is the parser's). Options end at the
/// first operand. <c>-o</c>/<c>-a</c>/<c>-v</c> are refused (exit 2, not implemented). With no <c>-f</c> or
/// <c>-p</c> the report is ps-bash's own <c>real N.NNNs</c> line.
/// </summary>
public static class GnuTimeFormat
{
    /// <summary><c>-p</c>: the POSIX report.</summary>
    public const string Portable = "real %e\nuser %U\nsys %S";

    /// <summary>The parsed options.</summary>
    /// <param name="Format">The report format, or null for the default report.</param>
    /// <param name="Quiet"><c>-q</c>: no "Command exited with non-zero status" line.</param>
    /// <param name="CommandIndex">Index of the command word in the arguments.</param>
    /// <param name="Unsupported">A recognized option ps-bash does not implement (<c>-o</c>, <c>-a</c>, <c>-v</c>).</param>
    /// <param name="Error">A usage error (an option without its argument).</param>
    public readonly record struct Options(string? Format, bool Quiet, int CommandIndex, string? Unsupported, string? Error);

    public static Options Parse(IReadOnlyList<string> args)
    {
        string? format = null;
        bool quiet = false;
        int i = 0;
        for (; i < args.Count; i++)
        {
            string a = args[i];
            if (a == "--") { i++; break; }
            if (a is "-p" or "--portability") { format = Portable; continue; }
            if (a is "-q" or "--quiet") { quiet = true; continue; }
            if (a is "-f" or "--format")
            {
                if (i + 1 >= args.Count) return new(format, quiet, i, null, $"time: option requires an argument -- '{(a == "-f" ? "f" : "format")}'");
                format = args[++i];
                continue;
            }
            if (a.StartsWith("--format=", StringComparison.Ordinal)) { format = a["--format=".Length..]; continue; }
            if (a.Length > 2 && a.StartsWith("-f", StringComparison.Ordinal) && a[1] != '-') { format = a[2..]; continue; }
            if (a is "-o" or "-a" or "-v" or "--verbose" or "--append" || a.StartsWith("--output", StringComparison.Ordinal)
                || a.Length > 2 && a.StartsWith("-o", StringComparison.Ordinal))
                return new(format, quiet, i, a.StartsWith("--output", StringComparison.Ordinal) ? "--output" : a.Length > 2 && a[1] == 'o' ? "-o" : a, null);
            break; // the first operand (or anything else) is the command
        }
        return new(format, quiet, i, null, null);
    }

    /// <summary>
    /// The report for <paramref name="s"/>: GNU's "Command exited with non-zero status N" line first (unless
    /// <paramref name="quiet"/>), then <paramref name="format"/> expanded, each ending in a newline.
    /// </summary>
    public static string Render(string format, GnuTimeStats s, bool quiet)
    {
        var sb = new StringBuilder();
        // -p (the last of -p/-f wins) has no exit line: `time -p false` is just real/user/sys. A -f spelling of the
        // same report keeps it, and differs from Portable (its \n are escapes, not newlines).
        if (s.ExitStatus != 0 && !quiet && format != Portable)
            sb.Append("Command exited with non-zero status ").Append(s.ExitStatus.ToString(CultureInfo.InvariantCulture)).Append('\n');
        var inv = CultureInfo.InvariantCulture;
        for (int i = 0; i < format.Length; i++)
        {
            char c = format[i];
            if (c == '\\' && i + 1 < format.Length)
            {
                char e = format[++i];
                sb.Append(e switch { 'n' => "\n", 't' => "\t", '\\' => "\\", _ => "?\\" + e });
                continue;
            }
            if (c != '%' || i + 1 >= format.Length) { sb.Append(c); continue; }
            char f = format[++i];
            switch (f)
            {
                case '%': sb.Append('%'); break;
                case 'e': sb.Append(Seconds(s.Elapsed)); break;
                case 'E': sb.Append(Clock(s.Elapsed)); break;
                case 'U': sb.Append(Seconds(s.User)); break;
                case 'S': sb.Append(Seconds(s.System)); break;
                case 'P':
                    double el = s.Elapsed.TotalSeconds;
                    sb.Append(el > 0 ? ((long)((s.User + s.System).TotalSeconds * 100 / el)).ToString(inv) + "%" : "?%");
                    break;
                case 'M': sb.Append(s.MaxRssKb.ToString(inv)); break;
                case 'x': sb.Append(s.ExitStatus.ToString(inv)); break;
                case 'C': sb.Append(s.CommandLine); break;
                case 'Z': sb.Append("4096"); break;
                case 'c' or 'D' or 'F' or 'I' or 'k' or 'K' or 'O' or 'p' or 'r' or 'R' or 's' or 't' or 'w' or 'W' or 'X':
                    sb.Append('0'); break; // not measured on this platform
                default: sb.Append('?').Append(f); break;
            }
        }
        return sb.Append('\n').ToString();
    }

    private static string Seconds(TimeSpan t)
        => (Math.Floor(t.TotalSeconds * 100) / 100).ToString("0.00", CultureInfo.InvariantCulture);

    // %E: [hours:]minutes:seconds; under an hour minutes:seconds.hundredths (GNU time).
    private static string Clock(TimeSpan t)
    {
        long cs = (long)(t.TotalMilliseconds / 10);
        long hours = cs / 360000, minutes = cs / 6000 % 60, secs = cs / 100 % 60, hundredths = cs % 100;
        var inv = CultureInfo.InvariantCulture;
        return hours > 0
            ? $"{hours.ToString(inv)}:{minutes.ToString("00", inv)}:{secs.ToString("00", inv)}"
            : $"{(cs / 6000).ToString(inv)}:{secs.ToString("00", inv)}.{hundredths.ToString("00", inv)}";
    }
}
