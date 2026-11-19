using System.Globalization;
using System.Management.Automation;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashDate</c> function
/// (REFACTOR-2 follow-on). Emits the current (or specified) date / time,
/// matching GNU coreutils <c>date</c>.
///
/// <para>
/// Behavioral parity oracle: the original psm1 function. The cmdlet
/// reproduces every branch byte-for-byte:
/// </para>
/// <list type="bullet">
/// <item>Default: <c>Thu Jan  2 15:04:05 MST 2006</c>-style local datetime.</item>
/// <item><c>-d STRING</c> / <c>--date STRING</c> / <c>--date=STRING</c> parses
/// the date with <see cref="GnuDateParser"/> (the GNU <c>parse_datetime</c> grammar).</item>
/// <item><c>-u</c> / <c>--utc</c> / <c>--universal</c> emits in UTC.</item>
/// <item><c>-r FILE</c> / <c>--reference FILE</c> / <c>--reference=FILE</c>
/// uses the file's <c>LastWriteTime</c>.</item>
/// <item><c>+FORMAT</c> applies a GNU strftime-style format via
/// <see cref="ConvertDateFormat"/> with the same per-character switch as the
/// psm1 oracle's <c>Convert-DateFormat</c> helper.</item>
/// </list>
///
/// <para>
/// <b>One colliding flag</b> declared as an explicit value-bearing parameter
/// with a single-letter name: <c>-d</c> prefix-collides with
/// <c>-Debug</c>. Declaring the parameter literally as <c>D</c> binds the
/// bare <c>-d</c> token by exact-name match (the same pattern <c>cut</c> /
/// <c>base64</c> used). The long forms <c>--date</c> / <c>--date=</c> stay
/// in <see cref="Arguments"/> and are recovered by the manual scan.
/// <c>-u</c> / <c>-r</c> have no PowerShell common-parameter prefix collision
/// and stay in <see cref="Arguments"/>.
/// </para>
///
/// <para>
/// Output: a typed <c>PsBash.DateOutput</c> PSObject (with
/// <c>Year</c> / <c>Month</c> / <c>Day</c> / <c>Hour</c> / <c>Minute</c> /
/// <c>Second</c> / <c>Epoch</c> / <c>DayOfWeek</c> / <c>TimeZone</c> /
/// <c>DateTime</c> / <c>BashText</c>), matching the oracle's
/// <c>[PSCustomObject]</c> shape.
/// </para>
///
/// <para>
/// <c>--help</c> delegates to the psm1 <c>Show-BashHelp</c> via
/// parameter-bound <c>InvokeCommand.InvokeScript</c> (no
/// <see cref="ScriptBlock"/> construction, AOT-safe).
/// </para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashDate")]
[OutputType("PsBash.DateOutput")]
public sealed class InvokeBashDateCommand : BashCmdlet
{
    [Parameter] public string? D { get; set; }

    /// <summary>Decoy for a DIRECT bare <c>-I</c> (ISO 8601 date), an ambiguous prefix of the -Information* common parameters.</summary>
    [Parameter] public SwitchParameter I { get; set; }

    /// <summary>
    /// GNU coreutils 9.4 <c>date</c> options, long names in its <c>long_options</c> order (abbreviations and the
    /// ambiguity list follow it: <c>--rfc</c> = <c>'--rfc-email' '--rfc-3339'</c>). <c>-f</c>/<c>--file</c> (a date
    /// per input line) and <c>--debug</c> are refused, never silently ignored.
    /// </summary>
    private static readonly Args.OptSpecSet DateOptions = new(
        new Args.OptSpec[]
        {
            new("d", 'd', "date", Args.OptKind.Value),
            new("I", 'I', "iso-8601", Args.OptKind.OptionalValue),
            new("r", 'r', "reference", Args.OptKind.Value),
            new("R", 'R', "rfc-email"),
            new("R", '\0', "rfc-822"),
            new("R", '\0', "rfc-2822"),
            new("rfc-3339", '\0', "rfc-3339", Args.OptKind.Value),
            new("s", 's', "set", Args.OptKind.Value),
            new("u", 'u', "uct"),
            new("u", '\0', "utc"),
            new("u", '\0', "universal"),
        },
        validButUnsupported: new[] { "--debug", "-f", "--file" },
        allowAbbrev: true,
        gnuInfoOptions: true,
        longOptionOrder: new[]
        {
            "date", "debug", "file", "iso-8601", "reference", "rfc-email", "rfc-822", "rfc-2822", "rfc-3339", "set",
            "uct", "utc", "universal", "help", "version",
        });

    private static readonly (string Name, string Value)[] IsoTimespecs =
    {
        ("hours", "hours"), ("minutes", "minutes"), ("date", "date"), ("seconds", "seconds"), ("ns", "ns"),
    };

    private static readonly (string Name, string Value)[] Rfc3339Timespecs =
    {
        ("date", "date"), ("seconds", "seconds"), ("ns", "ns"),
    };

    /// <summary>
    /// The one place an output-format OPTION becomes a strftime format for <see cref="ConvertDateFormat"/> (GNU date.c
    /// <c>iso_8601_format</c> / <c>rfc_3339_format</c> / <c>rfc_email_format</c>): <c>-I[TIMESPEC]</c> /
    /// <c>--iso-8601[=TIMESPEC]</c> (default <c>date</c>), <c>--rfc-3339=TIMESPEC</c>, <c>-R</c>/<c>--rfc-email</c>.
    /// TIMESPEC is matched like GNU argmatch (a unique prefix: <c>-Is</c>); a bad one is the argmatch usage error. Pure.
    /// </summary>
    internal static bool TryOutputFormat(string optId, string? timespec, out string format, out string? error)
    {
        format = "";
        error = null;
        if (optId == "R")
        {
            format = "%a, %d %b %Y %H:%M:%S %z";
            return true;
        }
        bool iso = optId == "I";
        var table = iso ? IsoTimespecs : Rfc3339Timespecs;
        string block = iso
            ? "  - 'hours'\n  - 'minutes'\n  - 'date'\n  - 'seconds'\n  - 'ns'"
            : "  - 'date'\n  - 'seconds'\n  - 'ns'";
        string spec = "date";
        if (timespec != null
            && !GnuArgMatch.TryMatch("date", iso ? "iso-8601" : "rfc-3339", timespec, table, block, out spec, out error))
            return false;
        format = (iso, spec) switch
        {
            (_, "date") => "%Y-%m-%d",
            (true, "hours") => "%Y-%m-%dT%H%:z",
            (true, "minutes") => "%Y-%m-%dT%H:%M%:z",
            (true, "seconds") => "%Y-%m-%dT%H:%M:%S%:z",
            (true, _) => "%Y-%m-%dT%H:%M:%S,%N%:z",
            (false, "seconds") => "%Y-%m-%d %H:%M:%S%:z",
            _ => "%Y-%m-%d %H:%M:%S.%N%:z",
        };
        return true;
    }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "date", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "date"))
            {
                WriteObject(line);
            }
            return;
        }

        // Decoy-bound flags go back into argv first (`-d` binds D; a bare `-I` binds the I switch, as it would
        // otherwise be an ambiguous prefix of -InformationAction/-InformationVariable).
        var argv = new List<string>(args.Length + 3);
        if (D != null) { argv.Add("-d"); argv.Add(D); }
        if (I.IsPresent) argv.Add("-I");
        argv.AddRange(args);
        var parsed = Args.ArgParser.Parse(argv.ToArray(), DateOptions);
        if (FileSystemHelpers.TryWriteParseError(this, "date", parsed)) return;

        string? dateString = null;
        string? format = null;
        bool utc = false;
        string? refFile = null;
        string? setOperand = null;
        DateTime? setInstant = null;
        string? setString = null;
        bool sawFormatOperand = false;

        foreach (var tok in parsed.Tokens)
        {
            if (tok.Kind == Args.ArgTokKind.Option)
            {
                switch (tok.OptId)
                {
                    case "u": utc = true; break;
                    case "d": dateString = tok.Value; break;
                    case "r": refFile = tok.Value; break;
                    case "s": setString = tok.Value; break;
                    case "I" or "R" or "rfc-3339":
                        if (!TryOutputFormat(tok.OptId, tok.Value, out var fmt, out var err))
                        {
                            WriteBashError(err!);
                            return;
                        }
                        if (format != null)
                        {
                            WriteBashError("date: multiple output formats specified");
                            return;
                        }
                        format = fmt;
                        break;
                }
                continue;
            }
            if (tok.Kind != Args.ArgTokKind.Operand) continue;
            string arg = tok.Raw;

            // GNU date: the first operand starting with '+' is the format (after -I/-R/--rfc-3339 it is a second
            // one). Any other word is an operand. A second one is an extra operand; a lone word without the '+'
            // is a date to SET (MMDDhhmm[[CC]YY][.ss]), which an unprivileged run cannot do, and anything else an
            // invalid date. `date %x` printed the current date with status 0 (oils errexit "Command sub exit code").
            if (arg.Length > 0 && arg[0] == '+' && setOperand == null && !sawFormatOperand)
            {
                if (format != null)
                {
                    WriteBashError("date: multiple output formats specified");
                    return;
                }
                format = arg.Substring(1);
                sawFormatOperand = true;
                continue;
            }
            if (sawFormatOperand || setOperand != null)
            {
                FileSystemHelpers.WriteExtraOperand(this, "date", arg);
                return;
            }
            // The date it names is what an unprivileged GNU date prints after "cannot set date". REGRESSION: it
            // printed the current time.
            if (!TouchStamp.TryParseDateOperand(arg, DateTime.Now, out var named))
            {
                WriteBashError($"date: invalid date '{arg}'");
                return;
            }
            setOperand = arg;
            setInstant = named;
        }
        if (setString != null)
        {
            // `-s STRING`: GNU parses it, cannot set the clock unprivileged, and prints the date it would have set.
            dateString = setString;
            setOperand ??= setString;
        }
        if (setOperand != null)
        {
            // Never sets the clock: report as an unprivileged GNU date does, then print the time it names.
            WriteBashError("date: cannot set date: Operation not permitted");
        }

        // Determine the source datetime (psm1 oracle semantics).
        DateTimeOffset dto;
        if (refFile != null)
        {
            string resolved =
                FileSystemHelpers.ProviderPath(this, refFile);
            if (!System.IO.File.Exists(resolved)
                && !System.IO.Directory.Exists(resolved))
            {
                WriteBashError($"date: '{refFile}': No such file or directory");
                return;
            }
            DateTime mtime;
            if (System.IO.File.Exists(resolved))
            {
                mtime = System.IO.File.GetLastWriteTime(resolved);
            }
            else
            {
                mtime = System.IO.Directory.GetLastWriteTime(resolved);
            }
            dto = new DateTimeOffset(mtime);
        }
        else if (setInstant is { } instant)
        {
            // Under -u the operand names a UTC time (GNU reads it in the TZ it prints in).
            dto = utc
                ? new DateTimeOffset(DateTime.SpecifyKind(instant, DateTimeKind.Unspecified), TimeSpan.Zero)
                : new DateTimeOffset(instant);
        }
        else if (dateString != null)
        {
            // GNU parse_datetime grammar (relative items, weekdays, @epoch, zones ...).
            // The result is shown in the LOCAL zone, like GNU, whatever zone the text named.
            if (!GnuDateParser.TryParse(dateString, DateTimeOffset.Now, TimeZoneInfo.Local, out dto))
            {
                WriteBashError($"date: invalid date '{dateString}'");
                return;
            }
        }
        else
        {
            dto = DateTimeOffset.Now;
        }

        if (utc)
        {
            dto = dto.ToUniversalTime();
        }

        string text;
        if (format != null)
        {
            text = ConvertDateFormat(dto, format);
        }
        else
        {
            // Default: "Thu Jan  2 15:04:05 MST 2006"-style.
            var ci = CultureInfo.InvariantCulture;
            string dow = dto.ToString("ddd", ci);
            string mon = dto.ToString("MMM", ci);
            string day = dto.Day.ToString(ci).PadLeft(2);
            string time = dto.ToString("HH:mm:ss", ci);
            string tz = utc ? "UTC" : TimeZoneInfo.Local.Id;
            int yr = dto.Year;
            text = $"{dow} {mon} {day} {time} {tz} {yr}";
        }

        long epoch = dto.ToUnixTimeSeconds();
        var ci2 = CultureInfo.InvariantCulture;

        var obj = new PSObject();
        obj.TypeNames.Insert(0, "PsBash.DateOutput");
        obj.Properties.Add(new PSNoteProperty("Year", dto.Year));
        obj.Properties.Add(new PSNoteProperty("Month", dto.Month));
        obj.Properties.Add(new PSNoteProperty("Day", dto.Day));
        obj.Properties.Add(new PSNoteProperty("Hour", dto.Hour));
        obj.Properties.Add(new PSNoteProperty("Minute", dto.Minute));
        obj.Properties.Add(new PSNoteProperty("Second", dto.Second));
        obj.Properties.Add(new PSNoteProperty("Epoch", epoch));
        obj.Properties.Add(new PSNoteProperty("DayOfWeek",
            dto.ToString("dddd", ci2)));
        obj.Properties.Add(new PSNoteProperty("TimeZone",
            utc ? "UTC" : TimeZoneInfo.Local.Id));
        obj.Properties.Add(new PSNoteProperty("DateTime", dto));
        obj.Properties.Add(new PSNoteProperty("BashText", text));
        WriteObject(obj);
    }

    /// <summary>
    /// Reproduces the psm1 oracle's <c>Convert-DateFormat</c> per-char switch
    /// byte-for-byte. Unknown specs preserve <c>%X</c> literally.
    /// </summary>
    internal static string ConvertDateFormat(DateTimeOffset dto, string format)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        int i = 0;
        while (i < format.Length)
        {
            if (format[i] == '%' && (i + 1) < format.Length)
            {
                // %N nanoseconds (9 digits); %1N..%9N GNU width form, truncated (not rounded).
                // .NET ticks are 100 ns, so the last two digits are always 0.
                int nWidth = 9, nAt = i + 1;
                if (format[nAt] is >= '1' and <= '9' && nAt + 1 < format.Length && format[nAt + 1] == 'N')
                    nWidth = format[nAt++] - '0';
                if (format[nAt] == 'N')
                {
                    long nanos = dto.UtcTicks % TimeSpan.TicksPerSecond * 100;
                    sb.Append(nanos.ToString("000000000", ci), 0, nWidth);
                    i = nAt + 1;
                    continue;
                }
                // %:z: numeric zone with a colon (+05:30), what -I/--rfc-3339 print.
                if (format[i + 1] == ':' && i + 2 < format.Length && format[i + 2] == 'z')
                {
                    var off = dto.Offset;
                    sb.Append(off < TimeSpan.Zero ? '-' : '+')
                      .Append(Math.Abs(off.Hours).ToString("00", ci)).Append(':')
                      .Append(Math.Abs(off.Minutes).ToString("00", ci));
                    i += 3;
                    continue;
                }
                char spec = format[i + 1];
                switch (spec)
                {
                    case 'Y': sb.Append(dto.ToString("yyyy", ci)); break;
                    case 'y': sb.Append(dto.ToString("yy", ci)); break;
                    case 'm': sb.Append(dto.ToString("MM", ci)); break;
                    case 'd': sb.Append(dto.ToString("dd", ci)); break;
                    case 'H': sb.Append(dto.ToString("HH", ci)); break;
                    case 'M': sb.Append(dto.ToString("mm", ci)); break;
                    case 'S': sb.Append(dto.ToString("ss", ci)); break;
                    case 's': sb.Append(dto.ToUnixTimeSeconds()
                        .ToString(ci)); break;
                    case 'F': sb.Append(dto.ToString("yyyy-MM-dd", ci)); break;
                    case 'T': sb.Append(dto.ToString("HH:mm:ss", ci)); break;
                    case 'w': sb.Append(((int)dto.DayOfWeek)
                        .ToString(ci)); break;
                    case 'A': sb.Append(dto.ToString("dddd", ci)); break;
                    case 'B': sb.Append(dto.ToString("MMMM", ci)); break;
                    case 'Z':
                        if (dto.Offset == TimeSpan.Zero)
                        {
                            sb.Append("UTC");
                        }
                        else
                        {
                            sb.Append(TimeZoneInfo.Local.Id);
                        }
                        break;
                    case 'a': sb.Append(dto.ToString("ddd", ci)); break;
                    case 'b': sb.Append(dto.ToString("MMM", ci)); break;
                    case 'h': sb.Append(dto.ToString("MMM", ci)); break; // = %b
                    case 'I': sb.Append(dto.ToString("hh", ci)); break;  // 12-hour, zero-padded
                    case 'l': sb.Append((((dto.Hour + 11) % 12) + 1).ToString(ci).PadLeft(2)); break; // 12h space-padded
                    case 'k': sb.Append(dto.Hour.ToString(ci).PadLeft(2)); break; // 24h space-padded
                    case 'R': sb.Append(dto.ToString("HH:mm", ci)); break;        // = %H:%M
                    case 'r': sb.Append(dto.ToString("hh:mm:ss tt", ci)); break;  // 12h time
                    case 'D': sb.Append(dto.ToString("MM/dd/yy", ci)); break;     // = %m/%d/%y
                    case 'u': sb.Append((((int)dto.DayOfWeek + 6) % 7 + 1).ToString(ci)); break; // ISO weekday 1=Mon..7=Sun
                    case 'P': sb.Append(dto.ToString("tt", ci).ToLowerInvariant()); break;       // lowercase am/pm
                    case 'z':
                    {
                        var off = dto.Offset;
                        sb.Append(off < TimeSpan.Zero ? '-' : '+')
                          .Append(Math.Abs(off.Hours).ToString("00", ci))
                          .Append(Math.Abs(off.Minutes).ToString("00", ci));
                        break;
                    }
                    case 'e': sb.Append(dto.Day.ToString(ci).PadLeft(2)); break;
                    case 'j': sb.Append(dto.DayOfYear.ToString("000", ci)); break;
                    case 'p': sb.Append(dto.ToString("tt", ci)); break;
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case '%': sb.Append('%'); break;
                    default: sb.Append('%').Append(spec); break;
                }
                i += 2;
            }
            else
            {
                sb.Append(format[i]);
                i++;
            }
        }
        return sb.ToString();
    }

    private void WriteBashError(string message)
    {
        FileSystemHelpers.WriteBashError(this, message);
    }
}
