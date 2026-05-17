using System.Management.Automation;
using System.Text;
using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashXan</c> function
/// (REFACTOR-2 follow-on). Reproduces the psm1 oracle's xan CSV utility
/// surface — five subcommands (<c>headers</c> / <c>count</c> / <c>select</c>
/// / <c>search</c> / <c>table</c>) operating on a CSV input from either a
/// file (last positional after the subcommand+operand) or pipeline.
///
/// Flag surface: <c>-d DELIM</c> sets the CSV field separator (default
/// <c>,</c>). The bare token <c>-d</c> prefix-collides with the
/// <c>-Debug</c> common parameter, so it is declared as an explicit
/// value-bearing parameter literally named <see cref="D"/> (the same
/// pattern <c>cut</c> / <c>base64</c> / <c>date</c> use — exact
/// parameter-name match beats common-parameter prefix matching). The
/// subcommand keyword and its operands stay in <see cref="Arguments"/>.
///
/// Pipeline mode: when no file operand resolves to an existing path, the
/// pipeline input is concatenated via <c>BashRuntime.GetBashText</c> with
/// <c>\n</c> separators, then trimmed (matching the oracle's
/// <c>StringBuilder.Append + Trim</c> slice).
///
/// File mode: the last positional after the subcommand+operand is treated
/// as a CSV file path. Resolved via
/// <c>SessionState.Path.GetUnresolvedProviderPathFromPSPath</c> and probed
/// with <c>File.Exists</c>; a missing target emits a bash-style error via
/// <see cref="FileSystemHelpers.WriteBashError"/> and the cmdlet returns
/// with no output. File reads use <c>File.ReadAllText</c> with CRLF
/// normalization (the rev/strings/cut slice — a trailing newline does not
/// yield a spurious empty final line).
///
/// CSV parsing is intentionally simple, matching the psm1 oracle's
/// <c>ConvertFrom-Csv -Delimiter</c> behavior for the common case (no
/// embedded-quote handling beyond what <see cref="Regex.Escape"/> already
/// provides for the delimiter). A header-only file falls through to the
/// oracle's regex-based first-line split.
///
/// AOT safety: no <see cref="ScriptBlock"/> construction; <c>--help</c>
/// delegates to psm1 <c>Show-BashHelp</c> via parameter-bound
/// <see cref="CommandInvocationIntrinsics.InvokeScript(string, object[])"/>.
///
/// Directive 12 (security): all operand tokens — including the subcommand
/// operand, the file path, and the search pattern — flow only through
/// literal string operations (<see cref="Regex"/> with the input as the
/// pattern, <see cref="string.Split(char[])"/>, file probes). None are
/// concatenated into a script body and re-parsed. The pattern <c>$(throw 'pwn')</c>
/// reaches <see cref="Regex"/> as a literal pattern and either matches no
/// row or raises a regex-parse error caught by the per-row try.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashXan")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashXanCommand : PSCmdlet
{
    // object[] (not string[]) because PowerShell parses bash-style
    // comma-lists like `xan select name,city` into nested arrays. With a
    // string[] declaration the binder errors on the nested array; with
    // object[] we accept it and flatten each element to its ToString()
    // below.
    [Parameter(ValueFromRemainingArguments = true)]
    public object[]? Arguments { get; set; }

    /// <summary>
    /// The bash <c>-d DELIM</c> (CSV delimiter) value flag. The bare token
    /// <c>-d</c> prefix-collides with the <c>-Debug</c> common parameter;
    /// declaring the parameter literally as <c>D</c> beats the
    /// common-parameter prefix match via the binder's exact-name preference.
    /// </summary>
    [Parameter]
    public string? D { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private readonly List<PSObject> _pipeline = new();

    protected override void ProcessRecord()
    {
        if (InputObject != null)
        {
            _pipeline.Add(InputObject);
        }
    }

    protected override void EndProcessing()
    {
        // Flatten the loosely-typed Arguments into a string[] (handling
        // nested arrays from PowerShell's array-literal parsing of
        // comma-separated tokens like 'name,city').
        var flat = new List<string>();
        if (Arguments != null)
        {
            foreach (var item in Arguments)
            {
                if (item is System.Collections.IEnumerable e && item is not string)
                {
                    foreach (var sub in e) flat.Add(sub?.ToString() ?? string.Empty);
                }
                else
                {
                    flat.Add(item?.ToString() ?? string.Empty);
                }
            }
        }
        var args = flat.ToArray();

        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "xan"))
            {
                WriteObject(line);
            }
            return;
        }

        string delimiter = D ?? ",";
        string? subcommand = null;
        var subArgs = new List<string>();

        // Parse global flags then collect subcommand + remaining args
        // (matching the oracle's two-loop structure).
        int i = 0;
        while (i < args.Length)
        {
            string a = args[i];
            // Joined `-dC` form lands here even though bare `-d` binds to D.
            if (a.Length >= 2 && a[0] == '-' && a[1] == 'd' && a.Length > 2)
            {
                delimiter = a.Substring(2);
                i++;
                continue;
            }
            if (subcommand == null && !a.StartsWith("-", StringComparison.Ordinal))
            {
                subcommand = a;
                i++;
                while (i < args.Length)
                {
                    subArgs.Add(args[i]);
                    i++;
                }
                break;
            }
            i++;
        }

        if (subcommand == null)
        {
            FileSystemHelpers.WriteBashError(this,
                "xan: missing subcommand (headers, count, select, search, table)");
            return;
        }

        // Decide which subArgs slot is the file path.
        string? fileArg = null;
        switch (subcommand)
        {
            case "headers":
            case "count":
            case "table":
                if (subArgs.Count > 0)
                {
                    fileArg = subArgs[subArgs.Count - 1];
                }
                break;
            case "select":
            case "search":
                if (subArgs.Count > 1)
                {
                    fileArg = subArgs[subArgs.Count - 1];
                }
                break;
            default:
                FileSystemHelpers.WriteBashError(this,
                    $"xan: unknown subcommand '{subcommand}'");
                return;
        }

        // Resolve CSV text from file or pipeline.
        string? csvText = null;
        if (fileArg != null)
        {
            string resolved;
            try
            {
                resolved = SessionState.Path.GetUnresolvedProviderPathFromPSPath(fileArg);
            }
            catch (Exception ex)
            {
                FileSystemHelpers.WriteBashError(this, $"xan: {fileArg}: {ex.Message}");
                return;
            }
            if (!File.Exists(resolved))
            {
                FileSystemHelpers.WriteBashError(this,
                    $"xan: {fileArg}: No such file or directory");
                return;
            }
            try
            {
                csvText = File.ReadAllText(resolved).Replace("\r\n", "\n");
            }
            catch (Exception ex)
            {
                FileSystemHelpers.WriteBashError(this, $"xan: {fileArg}: {ex.Message}");
                return;
            }
        }
        else
        {
            var sb = new StringBuilder();
            foreach (var item in _pipeline)
            {
                string text = BashRuntime.GetBashText(item);
                sb.Append(text);
                sb.Append('\n');
            }
            csvText = sb.ToString().Trim();
        }

        if (string.IsNullOrEmpty(csvText))
        {
            return;
        }

        // Parse CSV. Split on raw \n (file mode already normalized; pipeline
        // mode is already \n-delimited from the BashText concat above).
        string[] allLines = csvText.Split('\n');
        if (allLines.Length == 0)
        {
            return;
        }

        string headerLine = allLines[0];
        string[] headers = SplitCsvLine(headerLine, delimiter);

        // Build records as parallel string[] arrays. Empty lines after the
        // header are skipped (oracle: ConvertFrom-Csv drops them).
        var records = new List<string[]>();
        for (int li = 1; li < allLines.Length; li++)
        {
            string line = allLines[li];
            if (line.Length == 0) continue;
            records.Add(SplitCsvLine(line, delimiter));
        }

        switch (subcommand)
        {
            case "headers":
                foreach (var h in headers)
                {
                    WriteObject(BashRuntime.NewBashObject(h));
                }
                break;

            case "count":
                WriteObject(BashRuntime.NewBashObject(records.Count.ToString()));
                break;

            case "select":
            {
                if (subArgs.Count < 1) return;
                // First subArg is the column list; with a file, the last
                // subArg is the file path so the column-list slot is index 0.
                string colSpec = subArgs[0];
                string[] cols = colSpec.Split(',');
                // Header line
                WriteObject(BashRuntime.NewBashObject(string.Join(delimiter, cols)));
                // Row lines
                foreach (var rec in records)
                {
                    var picks = new List<string>();
                    foreach (var c in cols)
                    {
                        int idx = Array.IndexOf(headers, c);
                        picks.Add(idx >= 0 && idx < rec.Length ? rec[idx] : string.Empty);
                    }
                    WriteObject(BashRuntime.NewBashObject(string.Join(delimiter, picks)));
                }
                break;
            }

            case "search":
            {
                if (subArgs.Count < 1) return;
                string pattern = subArgs[0];
                // Header line first
                WriteObject(BashRuntime.NewBashObject(string.Join(delimiter, headers)));
                Regex? rx;
                try
                {
                    rx = new Regex(pattern);
                }
                catch (ArgumentException)
                {
                    // Invalid regex from user input — emit no rows.
                    return;
                }
                foreach (var rec in records)
                {
                    string rowText = string.Join(delimiter, rec);
                    if (rx.IsMatch(rowText))
                    {
                        WriteObject(BashRuntime.NewBashObject(rowText));
                    }
                }
                break;
            }

            case "table":
            {
                // Compute per-column max widths (across headers + all rows).
                int[] widths = new int[headers.Length];
                for (int c = 0; c < headers.Length; c++)
                {
                    widths[c] = headers[c].Length;
                }
                foreach (var rec in records)
                {
                    int upto = Math.Min(rec.Length, headers.Length);
                    for (int c = 0; c < upto; c++)
                    {
                        if (rec[c].Length > widths[c]) widths[c] = rec[c].Length;
                    }
                }

                var sb = new StringBuilder();
                var headerParts = new List<string>();
                for (int c = 0; c < headers.Length; c++)
                {
                    headerParts.Add(headers[c].PadRight(widths[c]));
                }
                sb.AppendLine(string.Join("  ", headerParts));
                foreach (var rec in records)
                {
                    var parts = new List<string>();
                    for (int c = 0; c < headers.Length; c++)
                    {
                        string v = c < rec.Length ? rec[c] : string.Empty;
                        parts.Add(v.PadRight(widths[c]));
                    }
                    sb.AppendLine(string.Join("  ", parts));
                }
                string body = sb.ToString().TrimEnd('\r', '\n');
                WriteObject(BashRuntime.NewBashObject(body));
                break;
            }
        }
    }

    /// <summary>
    /// Split a CSV line on the configured delimiter. Mirrors the psm1
    /// oracle's reliance on <c>ConvertFrom-Csv -Delimiter</c> for the
    /// common case (and on the regex-escape branch for the header-only
    /// fallback) — no embedded-quote handling, which the psm1 oracle did
    /// not implement either.
    /// </summary>
    private static string[] SplitCsvLine(string line, string delimiter)
    {
        if (string.IsNullOrEmpty(delimiter))
        {
            return new[] { line };
        }
        // Match the oracle's [regex]::Escape behavior so a multi-char
        // delimiter splits correctly.
        return Regex.Split(line, Regex.Escape(delimiter));
    }
}
