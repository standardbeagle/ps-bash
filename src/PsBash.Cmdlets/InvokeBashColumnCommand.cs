using System.Management.Automation;
using System.Text;
using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashColumn</c> function
/// (REFACTOR-2 follow-on). Formats input as a table; in default (passthrough)
/// mode each line is emitted unchanged, in <c>-t</c> mode each line is split
/// on whitespace (or <c>-s SEP</c>) and the columns are padded to the per-column
/// max width before joining with the oracle-fixed two-space gap.
///
/// Behavioral parity oracle: the original psm1 function. Flag set:
/// <list type="bullet">
/// <item><c>-t</c> — enable table mode (column alignment).</item>
/// <item><c>-s SEP</c> / <c>-sSEP</c> — input separator (regex-escaped before
/// use; falls back to <c>\s+</c> when unset). The joined form
/// (<c>-sSEP</c>) requires exactly one delimiter character, matching the
/// oracle's <c>^-s(.)$</c> pattern.</item>
/// <item><c>--</c> — end-of-flags.</item>
/// <item><c>--help</c> — delegates to psm1 <c>Show-BashHelp</c>.</item>
/// </list>
/// The output separator is hard-coded to two spaces (<c>"  "</c>), matching
/// the oracle byte-for-byte; an <c>-o</c> output-separator flag is intentionally
/// not supported (the oracle never accepted one).
///
/// No PowerShell common-parameter prefix collision: <c>-t</c> and <c>-s</c>
/// share no prefix with any common parameter (no <c>-T*</c> / <c>-S*</c>
/// common parameter exists). Both stay in <see cref="Arguments"/> and are
/// parsed by a manual scan.
///
/// File mode glob expansion routes through
/// <see cref="FileSystemHelpers.ResolveOperandPaths"/>; missing files emit a
/// bash-style error via <see cref="FileSystemHelpers.WriteBashError"/>
/// (parameter-bound <c>InvokeScript</c>, AOT-safe) and the cmdlet continues
/// with the rest of the operands — matching the oracle's
/// <c>Read-BashFileLines</c> null-swallow contract.
///
/// Output: each emitted record goes through
/// <see cref="BashRuntime.NewBashObject(string)"/> — the same default
/// <c>PsBash.TextOutput</c> shape the psm1 oracle produced via
/// <c>New-BashObject -BashText</c>.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashColumn")]
[OutputType(typeof(string))]
public sealed class InvokeBashColumnCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

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
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "column", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "column"))
            {
                WriteObject(line);
            }
            return;
        }

        bool tableMode = false;
        string? separator = null;
        var operands = new List<string>();
        bool pastDoubleDash = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (pastDoubleDash)
            {
                operands.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                pastDoubleDash = true;
                continue;
            }

            // -t (table mode) — case-sensitive match parity with the oracle's `-ceq`.
            if (arg == "-t")
            {
                tableMode = true;
                continue;
            }

            // -s SEP (separated form) — case-sensitive match parity with oracle.
            if (arg == "-s")
            {
                if (i + 1 < args.Length)
                {
                    separator = args[i + 1];
                    i++;
                }
                continue;
            }

            // -sX joined form — oracle requires exactly one char (^-s(.)$).
            if (arg.Length == 3 && arg[0] == '-' && arg[1] == 's')
            {
                separator = arg[2].ToString();
                continue;
            }

            operands.Add(arg);
        }

        // Collect input lines.
        var lines = new List<string>();

        if (operands.Count == 0 && _pipeline.Count > 0)
        {
            foreach (var item in _pipeline)
            {
                string text = BashRuntime.GetBashText(item);
                string trimmed = text.TrimEnd('\n');
                if (trimmed.Contains('\n'))
                {
                    foreach (var subLine in trimmed.Split('\n'))
                    {
                        lines.Add(subLine);
                    }
                }
                else
                {
                    lines.Add(trimmed);
                }
            }
        }
        else
        {
            foreach (var raw in operands)
            {
                foreach (var filePath in FileSystemHelpers.ResolveOperandPaths(this, raw))
                {
                    try
                    {
                        foreach (var line in BashFileSystem.ReadLines(filePath))
                        {
                            lines.Add(line);
                        }
                    }
                    catch (Exception ex)
                    {
                        WriteReadError(filePath, ex);
                    }
                }
            }
        }

        if (!tableMode)
        {
            foreach (var line in lines)
            {
                WriteObject(BashRuntime.NewBashObject(line));
            }
            return;
        }

        // Table mode: split each line into fields, compute per-column widths,
        // emit padded rows. The oracle pre-trims each non-empty line before
        // splitting (its `$line.Trim()`); empty lines become a single empty
        // field. The output column separator is hard-coded to two spaces.
        string splitPattern = separator != null ? Regex.Escape(separator) : @"\s+";
        var rows = new List<string[]>();
        int maxCols = 0;
        foreach (var line in lines)
        {
            string[] fields;
            if (line == string.Empty)
            {
                fields = new[] { string.Empty };
            }
            else
            {
                fields = Regex.Split(line.Trim(), splitPattern);
            }
            rows.Add(fields);
            if (fields.Length > maxCols) maxCols = fields.Length;
        }

        var widths = new int[maxCols];
        foreach (var row in rows)
        {
            for (int c = 0; c < row.Length; c++)
            {
                if (row[c].Length > widths[c]) widths[c] = row[c].Length;
            }
        }

        foreach (var row in rows)
        {
            var sb = new StringBuilder();
            for (int c = 0; c < row.Length; c++)
            {
                if (c > 0) sb.Append("  ");
                if (c < row.Length - 1)
                {
                    sb.Append(row[c].PadRight(widths[c]));
                }
                else
                {
                    sb.Append(row[c]);
                }
            }
            WriteObject(BashRuntime.NewBashObject(sb.ToString()));
        }
    }

    private void WriteReadError(string path, Exception ex)
    {
        bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
            || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
        string msg = notFound ? "No such file or directory" : ex.Message;
        string normalized = path.Replace('\\', '/');
        FileSystemHelpers.WriteBashError(this, $"column: {normalized}: {msg}");
    }
}
