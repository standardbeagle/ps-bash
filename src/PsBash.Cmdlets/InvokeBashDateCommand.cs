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
/// the date via <c>DateTimeOffset.Parse</c> (invariant culture).</item>
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
public sealed class InvokeBashDateCommand : PSCmdlet
{
    [Parameter] public string? D { get; set; }

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

        string? dateString = D;
        string? format = null;
        bool utc = false;
        string? refFile = null;

        int i = 0;
        while (i < args.Length)
        {
            var arg = args[i];

            if (arg == "-u" || arg == "--utc" || arg == "--universal")
            {
                utc = true;
                i++;
                continue;
            }

            if (arg == "-d" || arg == "--date")
            {
                i++;
                if (i < args.Length) { dateString = args[i]; }
                i++;
                continue;
            }

            if (arg.StartsWith("--date=", StringComparison.Ordinal))
            {
                dateString = arg.Substring("--date=".Length);
                i++;
                continue;
            }

            if (arg == "-r" || arg == "--reference")
            {
                i++;
                if (i < args.Length) { refFile = args[i]; }
                i++;
                continue;
            }

            if (arg.StartsWith("--reference=", StringComparison.Ordinal))
            {
                refFile = arg.Substring("--reference=".Length);
                i++;
                continue;
            }

            if (arg.Length > 0 && arg[0] == '+')
            {
                format = arg.Substring(1);
                i++;
                continue;
            }

            i++;
        }

        // Determine the source datetime (psm1 oracle semantics).
        DateTimeOffset dto;
        if (refFile != null)
        {
            string resolved =
                SessionState.Path.GetUnresolvedProviderPathFromPSPath(refFile);
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
        else if (dateString != null)
        {
            try
            {
                dto = DateTimeOffset.Parse(dateString,
                    CultureInfo.InvariantCulture);
            }
            catch
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
    private static string ConvertDateFormat(DateTimeOffset dto, string format)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        int i = 0;
        while (i < format.Length)
        {
            if (format[i] == '%' && (i + 1) < format.Length)
            {
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
