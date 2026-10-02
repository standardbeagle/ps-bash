using System.Globalization;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>What a <c>-printf</c> / <c>-fprintf</c> format needs to know about one visited entry.</summary>
internal sealed class FindPrintItem
{
    public string Path = "";            // the printed path (%p)
    public string Start = "";           // the command-line starting point (%H)
    public int Depth;                   // %d
    public Func<FindStat> StatFn = null!;
    private FindStat? _stat;
    public FindStat Stat => _stat ??= StatFn();
}

/// <summary>
/// GNU find 4.9 <c>-printf</c> format compiler / renderer (oracle: GNU find 4.9). Escapes
/// <c>\a \b \f \n \r \t \v \\ \NNN</c> and <c>\c</c> (stop, flush) are decoded at compile time; an unknown escape keeps its
/// backslash and warns. Directives: <c>%% %a %A@ %b %B@ %c %C@ %d %D %f %F %g %G %h %H %i %k %l %m %M %n %p %P %s %S %t %T@
/// %u %U %y %Y %Z</c> (<c>@</c> = a strftime-style key char for A B C T), with optional <c>-+ #0</c> flags, width and precision.
/// An unknown directive warns and is printed as typed; a trailing <c>%</c> is a compile error.
/// </summary>
internal sealed class FindPrintf
{
    private abstract record Seg;
    private sealed record Lit(string Text) : Seg;
    private sealed record Dir(string Flags, int Width, int Precision, char Conv, char Key, string Raw) : Seg;
    private sealed record Stop : Seg;

    private readonly List<Seg> _segs = new();
    public List<string> Warnings { get; } = new();

    internal static FindPrintf? TryCompile(string fmt, out string? error)
    {
        var f = new FindPrintf();
        error = null;
        var lit = new StringBuilder();
        void Flush() { if (lit.Length > 0) { f._segs.Add(new Lit(lit.ToString())); lit.Clear(); } }
        int i = 0;
        while (i < fmt.Length)
        {
            char c = fmt[i];
            if (c == '\\')
            {
                if (i + 1 >= fmt.Length) { f.Warnings.Add("warning: unrecognized escape `\\'"); lit.Append('\\'); i++; continue; }
                char n = fmt[i + 1];
                switch (n)
                {
                    case 'a': lit.Append('\a'); i += 2; continue;
                    case 'b': lit.Append('\b'); i += 2; continue;
                    case 'f': lit.Append('\f'); i += 2; continue;
                    case 'n': lit.Append('\n'); i += 2; continue;
                    case 'r': lit.Append('\r'); i += 2; continue;
                    case 't': lit.Append('\t'); i += 2; continue;
                    case 'v': lit.Append('\v'); i += 2; continue;
                    case '\\': lit.Append('\\'); i += 2; continue;
                    case 'c': Flush(); f._segs.Add(new Stop()); i = fmt.Length; continue;
                    case >= '0' and <= '7':
                    {
                        int v = 0, k = 0;
                        while (k < 3 && i + 1 + k < fmt.Length && fmt[i + 1 + k] >= '0' && fmt[i + 1 + k] <= '7') { v = v * 8 + (fmt[i + 1 + k] - '0'); k++; }
                        lit.Append((char)(v & 0xFF));
                        i += 1 + k;
                        continue;
                    }
                    default:
                        f.Warnings.Add($"warning: unrecognized escape `\\{n}'");
                        lit.Append('\\').Append(n);
                        i += 2;
                        continue;
                }
            }
            if (c != '%') { lit.Append(c); i++; continue; }

            int start = i;
            i++;
            if (i >= fmt.Length) { error = "error: % at end of format string"; return null; }
            if (fmt[i] == '%') { lit.Append('%'); i++; continue; }
            var flags = new StringBuilder();
            while (i < fmt.Length && "-+ #0".IndexOf(fmt[i]) >= 0) flags.Append(fmt[i++]);
            int width = 0; bool hasW = false;
            while (i < fmt.Length && char.IsAsciiDigit(fmt[i])) { width = width * 10 + (fmt[i++] - '0'); hasW = true; }
            int prec = -1;
            if (i < fmt.Length && fmt[i] == '.')
            {
                i++; prec = 0;
                while (i < fmt.Length && char.IsAsciiDigit(fmt[i])) prec = prec * 10 + (fmt[i++] - '0');
            }
            if (i >= fmt.Length) { error = "error: % at end of format string"; return null; }
            char conv = fmt[i++];
            char key = '\0';
            if (conv is 'A' or 'B' or 'C' or 'T')
            {
                if (i >= fmt.Length) { error = "error: % at end of format string"; return null; }
                key = fmt[i++];
            }
            string raw = fmt.Substring(start, i - start);
            if ("abcdfFgGhHiklmMnpPsStuUyYZABCDT".IndexOf(conv) < 0 && conv != 'D')
            {
                f.Warnings.Add($"warning: unrecognized format directive `%{conv}'");
                Flush();
                f._segs.Add(new Lit(raw));
                continue;
            }
            Flush();
            f._segs.Add(new Dir(flags.ToString(), hasW ? width : 0, prec, conv, key, raw));
        }
        Flush();
        return f;
    }

    /// <summary>Render the format for one entry; <paramref name="stop"/> is set when <c>\c</c> ended the output.</summary>
    internal string Render(FindPrintItem item, out bool stop)
    {
        stop = false;
        var sb = new StringBuilder();
        foreach (var seg in _segs)
        {
            switch (seg)
            {
                case Lit l: sb.Append(l.Text); break;
                case Stop: stop = true; return sb.ToString();
                case Dir d: sb.Append(RenderDir(d, item)); break;
            }
        }
        return sb.ToString();
    }

    // ───────────── directives ─────────────

    private static string RenderDir(Dir d, FindPrintItem it)
    {
        switch (d.Conv)
        {
            // numeric
            case 'd': return Numeric(d, it.Depth, flagsAllowed: "-+ 0");
            case 'm': return Octal(d, it.Stat.Mode);
            case 's': return Numeric(d, it.Stat.Size, "-");
            case 'k': return Numeric(d, it.Stat.BlocksK, "-");
            case 'b': return Numeric(d, it.Stat.Blocks512, "-");
            case 'n': return Numeric(d, it.Stat.Nlink, "-");
            case 'i': return Numeric(d, (long)it.Stat.Inode, "-");
            case 'U': return Numeric(d, it.Stat.Uid, "-");
            case 'G': return Numeric(d, it.Stat.Gid, "-");
            case 'D': return Numeric(d, (long)it.Stat.Device, "-");
            case 'S': return Str(d, Sparseness(it.Stat));
        }
        string s = d.Conv switch
        {
            'p' => it.Path,
            'f' => BaseName(it.Path),
            'h' => DirName(it.Path),
            'H' => it.Start,
            'P' => RelativeToStart(it.Path, it.Start),
            'l' => it.Stat.IsLink ? it.Stat.LinkTarget ?? "" : "",
            'y' => it.Stat.TypeChar.ToString(),
            'Y' => it.Stat.IsLink ? it.Stat.TargetType.ToString() : it.Stat.TypeChar.ToString(),
            'M' => FindStat.SymbolicMode(it.Stat.TypeChar, it.Stat.Mode),
            'u' => it.Stat.User,
            'g' => it.Stat.Group,
            'F' => FsType(it.Path),
            'Z' => "",
            'a' => CTimeFormat(it.Stat.Atime),
            'c' => CTimeFormat(it.Stat.Ctime),
            't' => CTimeFormat(it.Stat.Mtime),
            'A' => FindTimeFormat.Format(it.Stat.Atime, d.Key),
            'C' => FindTimeFormat.Format(it.Stat.Ctime, d.Key),
            'B' => FindTimeFormat.Format(it.Stat.Btime, d.Key),
            'T' => FindTimeFormat.Format(it.Stat.Mtime, d.Key),
            _ => d.Raw,
        };
        return Str(d, s);
    }

    private static string CTimeFormat(DateTime t) => FindTimeFormat.Format(t, 'c', withFraction: true);

    private static string Str(Dir d, string s)
    {
        if (d.Precision >= 0 && s.Length > d.Precision) s = s.Substring(0, d.Precision);
        return Pad(d, s);
    }

    private static string Pad(Dir d, string s)
    {
        if (s.Length >= d.Width) return s;
        return d.Flags.Contains('-') ? s.PadRight(d.Width) : s.PadLeft(d.Width);
    }

    private static string Numeric(Dir d, long v, string flagsAllowed)
    {
        string body = v.ToString(CultureInfo.InvariantCulture);
        if (flagsAllowed.Contains('+') && d.Flags.Contains('+') && v >= 0) body = "+" + body;
        else if (flagsAllowed.Contains(' ') && d.Flags.Contains(' ') && v >= 0) body = " " + body;
        if (d.Width > body.Length)
        {
            if (flagsAllowed.Contains('0') && d.Flags.Contains('0') && !d.Flags.Contains('-'))
            {
                int signLen = body.Length > 0 && (body[0] is '+' or '-' or ' ') ? 1 : 0;
                body = body.Substring(0, signLen) + new string('0', d.Width - body.Length) + body.Substring(signLen);
            }
            else body = d.Flags.Contains('-') ? body.PadRight(d.Width) : body.PadLeft(d.Width);
        }
        return body;
    }

    private static string Octal(Dir d, int mode)
    {
        string body = Convert.ToString(mode & 0xFFF, 8);
        if (d.Flags.Contains('#')) body = "0" + body;
        if (d.Width > body.Length)
        {
            if (d.Flags.Contains('0') && !d.Flags.Contains('-')) body = body.PadLeft(d.Width, '0');
            else body = d.Flags.Contains('-') ? body.PadRight(d.Width) : body.PadLeft(d.Width);
        }
        return body;
    }

    /// <summary>%S: blocks*512 / size as C's <c>%g</c>; 1 for an empty file.</summary>
    private static string Sparseness(FindStat s)
    {
        double v = s.Size == 0 ? 1.0 : s.Blocks512 * 512.0 / s.Size;
        return FormatG(v);
    }

    internal static string FormatG(double v)
    {
        if (v == 0) return "0";
        string r = v.ToString("G6", CultureInfo.InvariantCulture);
        if (r.Contains('E'))
        {
            int e = r.IndexOf('E');
            string mant = r.Substring(0, e);
            int exp = int.Parse(r.Substring(e + 1), CultureInfo.InvariantCulture);
            r = mant + "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("D2", CultureInfo.InvariantCulture);
        }
        return r;
    }

    private static string FsType(string path)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            var root = System.IO.Path.GetPathRoot(full);
            return string.IsNullOrEmpty(root) ? "" : new DriveInfo(root).DriveFormat;
        }
        catch { return ""; }
    }

    // ───────────── path pieces (GNU conventions, oracle-checked) ─────────────

    /// <summary>%f: the last component; a path with trailing slashes keeps one of them (<c>sub/</c>, <c>/</c>).</summary>
    internal static string BaseName(string path)
    {
        if (path.EndsWith('/'))
        {
            var t = path.TrimEnd('/');
            if (t.Length == 0) return "/";
            return t.Substring(t.LastIndexOf('/') + 1) + "/";
        }
        return path.Substring(path.LastIndexOf('/') + 1);
    }

    /// <summary>%h: everything before the last slash ("." when there is none; empty for <c>/x</c> and <c>/</c>).</summary>
    internal static string DirName(string path)
    {
        var t = path.TrimEnd('/');
        if (t.Length == 0) return "";
        int i = t.LastIndexOf('/');
        return i < 0 ? "." : t.Substring(0, i);
    }

    /// <summary>%P: the path with the starting point (and the slashes after it) removed.</summary>
    internal static string RelativeToStart(string path, string start)
    {
        if (path.Length <= start.Length) return "";
        return path.Substring(start.Length).TrimStart('/');
    }

    /// <summary>The <c>-execdir</c> view of an entry: its directory (as %h) and the <c>./name</c> that replaces <c>{}</c>.</summary>
    internal static (string Dir, string Name) ExecDirParts(string path)
    {
        string dir = DirName(path);
        string name = BaseName(path).TrimEnd('/');
        if (name.Length == 0) name = path.Length > 0 ? "/" : "";
        return (dir.Length == 0 && path.StartsWith('/') ? "/" : dir, name);
    }
}

/// <summary>strftime-style fields as GNU find's <c>%T k</c> directives print them (local time, ns fraction as 10 digits).</summary>
internal static class FindTimeFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>"%.10d" fraction: seven .NET tick digits then three zeros (".1234567000").</summary>
    internal static string Frac10(DateTime t) => "." + (t.Ticks % TimeSpan.TicksPerSecond).ToString("D7", Inv) + "000";

    internal static string Format(DateTime t, char k, bool withFraction = false)
    {
        int h12 = t.Hour % 12 == 0 ? 12 : t.Hour % 12;
        switch (k)
        {
            case '@': return new DateTimeOffset(t).ToUnixTimeSeconds().ToString(Inv) + Frac10(t);
            case 's': return new DateTimeOffset(t).ToUnixTimeSeconds().ToString(Inv);
            case 'S': return t.ToString("ss", Inv) + Frac10(t);
            case 'T': case 'X': return t.ToString("HH:mm:ss", Inv) + Frac10(t);
            case '+': return t.ToString("yyyy-MM-dd+HH:mm:ss", Inv) + Frac10(t);
            case 'c': return t.ToString("ddd MMM", Inv) + " " + t.Day.ToString(Inv).PadLeft(2) + " " + t.ToString("HH:mm:ss", Inv)
                             + (withFraction ? Frac10(t) : "") + " " + t.ToString("yyyy", Inv);
            case 'x': case 'D': return t.ToString("MM/dd/yy", Inv);
            case 'r': return t.ToString("hh:mm:ss", Inv) + " " + (t.Hour < 12 ? "AM" : "PM");
            case 'R': return t.ToString("HH:mm", Inv);
            case 'F': return t.ToString("yyyy-MM-dd", Inv);
            case 'H': return t.ToString("HH", Inv);
            case 'I': return h12.ToString("D2", Inv);
            case 'k': return t.Hour.ToString(Inv).PadLeft(2);
            case 'l': return h12.ToString(Inv).PadLeft(2);
            case 'M': return t.ToString("mm", Inv);
            case 'p': return t.Hour < 12 ? "AM" : "PM";
            case 'a': return t.ToString("ddd", Inv);
            case 'A': return t.ToString("dddd", Inv);
            case 'b': case 'h': return t.ToString("MMM", Inv);
            case 'B': return t.ToString("MMMM", Inv);
            case 'C': return (t.Year / 100).ToString("D2", Inv);
            case 'd': return t.ToString("dd", Inv);
            case 'e': return t.Day.ToString(Inv).PadLeft(2);
            case 'g': return (ISOWeek.GetYear(t) % 100).ToString("D2", Inv);
            case 'G': return ISOWeek.GetYear(t).ToString(Inv);
            case 'j': return t.DayOfYear.ToString("D3", Inv);
            case 'm': return t.ToString("MM", Inv);
            case 'n': return "\n";
            case 't': return "\t";
            case 'u': return (t.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)t.DayOfWeek).ToString(Inv);
            case 'U': return ((t.DayOfYear - 1 + 7 - (int)t.DayOfWeek) / 7).ToString("D2", Inv);
            case 'V': return ISOWeek.GetWeekOfYear(t).ToString("D2", Inv);
            case 'w': return ((int)t.DayOfWeek).ToString(Inv);
            case 'W': return ((t.DayOfYear - 1 + 7 - ((int)t.DayOfWeek + 6) % 7) / 7).ToString("D2", Inv);
            case 'y': return t.ToString("yy", Inv);
            case 'Y': return t.ToString("yyyy", Inv);
            case 'z': { var off = TimeZoneInfo.Local.GetUtcOffset(t); return (off < TimeSpan.Zero ? "-" : "+") + Math.Abs(off.Hours).ToString("D2", Inv) + Math.Abs(off.Minutes).ToString("D2", Inv); }
            case 'Z': return ZoneAbbreviation(t);
            case '%': return "%";
            default: return "%" + k;
        }
    }

    internal static string ZoneAbbreviation(DateTime t)
    {
        var tz = TimeZoneInfo.Local;
        if (tz.BaseUtcOffset == TimeSpan.Zero && !tz.SupportsDaylightSavingTime) return "UTC";
        string name = tz.IsDaylightSavingTime(t) ? tz.DaylightName : tz.StandardName;
        if (name.Length <= 5 && !name.Contains(' ')) return name;
        var sb = new StringBuilder();
        foreach (var w in name.Split(' ', StringSplitOptions.RemoveEmptyEntries)) if (char.IsLetter(w[0])) sb.Append(char.ToUpperInvariant(w[0]));
        return sb.ToString();
    }
}
