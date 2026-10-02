using System.Management.Automation;
using System.Text;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet for util-linux <c>column</c> (2.39): in <c>-t</c> (table) mode each input line is
/// split into fields and the columns are padded to the per-column maximum width; without <c>-t</c>
/// the input lines are the ENTRIES of a fill layout (<see cref="RenderFill"/>: the BSD <c>column</c>
/// algorithm util-linux kept — tab-separated columns of tab-stop-rounded width, filled down the
/// columns, or across the rows with <c>-x</c>).
///
/// <para><b>Arguments</b> go through the shared ordered parser (<see cref="ColumnSpec"/>; <c>column</c> is
/// on <c>PsEmitter.OrderedArgCommands</c>). Implemented: <c>-t/--table</c>, <c>-s/--separator CHARS</c>
/// (each character is a delimiter, empty fields are kept), <c>-o/--output-separator STR</c>,
/// <c>-l/--table-columns-limit N</c> (the last column takes the rest of the line),
/// <c>-L/--keep-empty-lines</c>, <c>-c/--output-width N</c> and <c>-x/--fillrows</c> (the fill layout; the
/// width defaults to <c>COLUMNS</c>, else 80 as util-linux does off a terminal; <c>-c</c> is accepted and
/// ignored with <c>-t</c>, <c>-x</c> with <c>-t</c> is util-linux's "mutually exclusive" error);
/// <c>-e</c> and <c>-d</c> are accepted no-ops (no headings exist without
/// <c>-N</c>). The rest of column's table/tree/JSON surface (<c>-n -N -O -C -E -m -H -R -T -W -J
/// -r -i -p</c>) is refused with exit 2. Usage errors exit 1 like util-linux.</para>
///
/// <para>Table rendering follows util-linux: blank lines are ignored unless <c>-L</c>, a row with fewer
/// fields than the table is padded with empty cells (so it carries trailing spaces), the last column is
/// never padded.</para>
///
/// <para>Direct PowerShell calls: <c>-c -o -e -d -V</c> prefix-collide with common parameters and are
/// declared decoys, re-injected before the scan; <c>-o</c> and <c>-c</c> take their value (<see cref="O"/> and
/// <see cref="C"/> are strings).</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashColumn")]
[OutputType(typeof(string))]
public sealed class InvokeBashColumnCommand : PSCmdlet
{
    private const string OptTable = "table", OptSep = "separator", OptOutSep = "output-separator",
        OptLimit = "limit", OptKeepEmpty = "keep-empty-lines", OptNoOp = "noop", OptWidth = "width", OptFillRows = "fillrows";

    /// <summary>util-linux column options ps-bash refuses (exit 2).</summary>
    private static readonly string[] ColumnValidButUnsupported =
    {
        "--columns", "-n", "--table-name", "-N", "--table-columns",
        "-O", "--table-order", "-C", "--table-column", "-E", "--table-noextreme", "-m", "--table-maxout",
        "-H", "--table-hide", "-R", "--table-right", "-T", "--table-truncate", "-W", "--table-wrap",
        "-J", "--json", "-r", "--tree", "-i", "--tree-id", "-p", "--tree-parent",
    };

    /// <summary>column's option surface; ambiguity lists follow util-linux's table order (<c>--t</c>,
    /// <c>--o</c>, <c>--table-h</c> oracle-checked).</summary>
    private static readonly OptSpecSet ColumnSpec = new(
        new[]
        {
            new OptSpec(OptTable, 't', "table"),
            new OptSpec(OptSep, 's', "separator", OptKind.Value),
            new OptSpec(OptOutSep, 'o', "output-separator", OptKind.Value),
            new OptSpec(OptWidth, 'c', "output-width", OptKind.Value),
            new OptSpec(OptFillRows, 'x', "fillrows"),
            new OptSpec(OptLimit, 'l', "table-columns-limit", OptKind.Value),
            new OptSpec(OptKeepEmpty, 'L', "keep-empty-lines"),
            new OptSpec(OptKeepEmpty, '\0', "table-empty-lines"),
            new OptSpec(OptNoOp, 'e', "table-header-repeat"),
            new OptSpec(OptNoOp, 'd', "table-noheadings"),
            new OptSpec(OptSpecSet.HelpId, 'h', "help"),
            new OptSpec(OptSpecSet.VersionId, 'V', "version"),
        },
        ColumnValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true,
        longOptionOrder: new[]
        {
            "columns", "fillrows", "help", "json", "keep-empty-lines", "output-separator", "output-width",
            "separator", "table", "table-columns", "table-column", "table-columns-limit", "table-hide",
            "table-name", "table-maxout", "table-noextreme", "table-noheadings", "table-order", "table-right",
            "table-truncate", "table-wrap", "table-empty-lines", "table-header-repeat", "tree", "tree-id",
            "tree-parent", "version",
        });

    /// <summary>Pure argv scan (unit-test seam).</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, ColumnSpec);

    internal sealed class ColumnArgs
    {
        public ParsedArgs Parsed = null!;
        public bool Table, KeepEmpty, FillRows;

        /// <summary>The fill-layout width from <c>-c</c> (null = <c>COLUMNS</c> / 80).</summary>
        public long? Width;
        public string? Separators;   // null = whitespace
        public string OutputSeparator = "  ";
        public int Limit = int.MaxValue;
        public List<string> Operands = new();
        public string? Error;
    }

    /// <summary>
    /// Scan + validate. Fixes over the old hand scan: <c>-s</c> is a SET of delimiter characters (it was one
    /// literal string), <c>-o</c>/<c>-l</c>/<c>-L</c> are implemented (they were refused), every other
    /// unknown option was a file name (<c>column -z</c> tried to read a file called <c>-z</c>), a dangling
    /// <c>-s</c> was ignored, <c>--</c>/abbreviations/bundles (<c>-ts:</c>) work.
    /// </summary>
    internal static ColumnArgs Plan(string[] args)
    {
        var p = new ColumnArgs { Parsed = ScanArgs(args) };
        p.Operands = p.Parsed.Operands();
        if (p.Parsed.HasError) return p;
        if (p.Parsed.Has(OptSpecSet.HelpId) || p.Parsed.Has(OptSpecSet.VersionId)) return p;

        foreach (var tok in p.Parsed.Tokens)
        {
            if (tok.Kind != ArgTokKind.Option) continue;
            switch (tok.OptId)
            {
                case OptTable: p.Table = true; break;
                case OptKeepEmpty: p.KeepEmpty = true; break;
                case OptFillRows: p.FillRows = true; break;
                case OptWidth:
                    {
                        if (!TryParseWidth(tok.Value!, out long width, out string widthError))
                        {
                            p.Error = $"column: invalid columns argument: '{tok.Value}'{widthError}";
                            return p;
                        }
                        p.Width = width;
                        break;
                    }
                case OptSep: p.Separators = tok.Value!; break;
                case OptOutSep: p.OutputSeparator = tok.Value!; break;
                case OptLimit:
                    {
                        string v = tok.Value!;
                        bool digits = v.Length > 0;
                        foreach (char ch in v) if (ch < '0' || ch > '9') { digits = false; break; }
                        if (!digits) { p.Error = $"column: invalid columns limit argument: '{v}'"; return p; }
                        int n = int.TryParse(v, out int parsed) ? parsed : int.MaxValue;
                        if (n == 0) { p.Error = "column: columns limit must be greater than zero"; return p; }
                        p.Limit = n;
                        break;
                    }
            }
        }
        if (p.Table && p.FillRows) p.Error = "column: mutually exclusive arguments: --table --fillrows";
        return p;
    }

    /// <summary>
    /// util-linux's <c>strtou32_or_err</c>: leading blanks, an optional sign and decimal digits to the end;
    /// anything else is invalid, a value past 32 bits (a negative number wraps to one) is out of range.
    /// <paramref name="error"/> is the text appended to the message (empty, or <c>: Numerical result out of range</c>).
    /// </summary>
    internal static bool TryParseWidth(string s, out long width, out string error)
    {
        width = 0;
        error = string.Empty;
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        bool negative = false;
        if (i < s.Length && (s[i] == '+' || s[i] == '-')) { negative = s[i] == '-'; i++; }
        int digitsFrom = i;
        long v = 0;
        bool overflow = false;
        for (; i < s.Length && s[i] >= '0' && s[i] <= '9'; i++)
        {
            if (v > (long.MaxValue - 9) / 10) overflow = true;
            else v = v * 10 + (s[i] - '0');
        }
        if (i == digitsFrom || i < s.Length) return false;
        if (overflow || (negative && v != 0) || v > uint.MaxValue)
        {
            error = ": Numerical result out of range";
            return false;
        }
        width = v;
        return true;
    }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    /// <summary>The <c>-c WIDTH</c> output width. Bare <c>-c</c> prefix-collides with <c>-Confirm</c>;
    /// declared value-bearing so the value binds with it, then re-injected.</summary>
    [Parameter] public string? C { get; set; }

    /// <summary>The <c>-o STRING</c> output separator. Bare <c>-o</c> prefix-collides with
    /// <c>-OutVariable</c>/<c>-OutBuffer</c>; declared value-bearing so the value binds with it.</summary>
    [Parameter] public string? O { get; set; }

    /// <summary>Decoy for <c>-e</c> (header-repeat no-op): ambiguous between -ErrorAction/-ErrorVariable.</summary>
    [Parameter] public SwitchParameter E { get; set; }

    /// <summary>Decoy for <c>-d</c> (noheadings no-op): would bind <c>-Debug</c>.</summary>
    [Parameter] public SwitchParameter D { get; set; }

    /// <summary>Decoy for <c>-V</c> (version): would bind <c>-Verbose</c>.</summary>
    [Parameter] public SwitchParameter V { get; set; }

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
        var raw = Arguments ?? Array.Empty<string>();
        var pre = new List<string>();
        if (C is not null) { pre.Add("-c"); pre.Add(C); }
        if (E.IsPresent) pre.Add("-e");
        if (D.IsPresent) pre.Add("-d");
        if (V.IsPresent) pre.Add("-V");
        if (O is not null) { pre.Add("-o"); pre.Add(O); }
        var args = pre.Count == 0 ? raw : pre.Concat(raw).ToArray();

        FileSystemHelpers.SetLastExitCode(this, 0);
        var plan = Plan(args);
        if (FileSystemHelpers.TryWriteParseError(this, "column", plan.Parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "column", plan.Parsed)) return;
        if (plan.Error is { } planError)
        {
            FileSystemHelpers.WriteBashError(this, planError);
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }

        var operands = plan.Operands;

        // Collect input lines.
        var lines = new List<string>();
        bool hadError = false;

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
            foreach (var raw2 in operands)
            {
                foreach (var filePath in FileSystemHelpers.ResolveOperandPaths(this, raw2))
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
                        if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                        WriteReadError(filePath, ex);
                        hadError = true;
                    }
                }
            }
        }

        if (!plan.Table)
        {
            long width = plan.Width ?? TextWidth.DefaultTerminalColumns();
            foreach (var row in RenderFill(lines, width, plan.FillRows, plan.KeepEmpty))
            {
                WriteObject(BashRuntime.NewBashObject(row));
            }
        }
        else
        {
            foreach (var row in RenderTable(lines, plan.Separators, plan.OutputSeparator, plan.Limit, plan.KeepEmpty))
            {
                WriteObject(BashRuntime.NewBashObject(row));
            }
        }

        if (hadError) FileSystemHelpers.SetLastExitCode(this, 1);
    }

    /// <summary>
    /// The fill layout (pure) of util-linux / BSD <c>column</c> without <c>-t</c>: every input line is one
    /// ENTRY (blank lines are dropped unless <paramref name="keepEmpty"/>). The cell width is the widest
    /// entry plus one, rounded up to a multiple of 8; <c>termWidth / cell</c> columns (at least 1) are filled
    /// down the columns, or across the rows with <paramref name="fillRows"/>. Columns are separated by TABS:
    /// after an entry the line is padded with tabs until the next tab stop would pass the column's end
    /// (an empty entry therefore still consumes a column), and the last entry of a line is not padded.
    /// Widths are display widths (<see cref="TextWidth"/>).
    /// </summary>
    internal static List<string> RenderFill(IReadOnlyList<string> lines, long termWidth, bool fillRows, bool keepEmpty)
    {
        var entries = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            if (line.Length == 0 && !keepEmpty) continue;
            entries.Add(line);
        }
        var result = new List<string>();
        int n = entries.Count;
        if (n == 0) return result;

        var widths = new int[n];
        int maxLength = 0;
        for (int i = 0; i < n; i++)
        {
            widths[i] = TextWidth.Of(entries[i]);
            if (widths[i] > maxLength) maxLength = widths[i];
        }
        long cell = (maxLength + 8) & ~7L;
        int numCols = (int)Math.Max(1, Math.Min(n, termWidth / cell));
        int numRows = (n + numCols - 1) / numCols;

        for (int row = 0; row < numRows; row++)
        {
            var sb = new StringBuilder();
            long endCol = cell;
            long chCount = 0;
            for (int col = 0; col < numCols; col++)
            {
                int index = fillRows ? row * numCols + col : row + col * numRows;
                if (index >= n) break;
                sb.Append(entries[index]);
                chCount += widths[index];
                int next = fillRows ? index + 1 : index + numRows;
                if (fillRows ? (col == numCols - 1 || next >= n) : next >= n) break;
                long stop;
                while ((stop = (chCount + 8) & ~7L) <= endCol)
                {
                    sb.Append('\t');
                    chCount = stop;
                }
                endCol += cell;
            }
            result.Add(sb.ToString());
        }
        return result;
    }

    /// <summary>
    /// Table layout (pure). <paramref name="separators"/> null = split on runs of blanks; otherwise each
    /// character of the set delimits a field and empty fields are kept. <paramref name="limit"/> caps the
    /// field count (the last field holds the rest of the line). Blank lines are dropped unless
    /// <paramref name="keepEmpty"/>. Every column but the last is padded to its widest cell; a short row is
    /// completed with empty cells.
    /// </summary>
    internal static List<string> RenderTable(
        IReadOnlyList<string> lines, string? separators, string outputSeparator, int limit, bool keepEmpty)
    {
        var rows = new List<string[]>(lines.Count);
        int maxCols = 0;
        foreach (var line in lines)
        {
            string[] fields = SplitFields(line, separators, limit);
            if (fields.Length == 0 && !keepEmpty) continue;
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

        var result = new List<string>(rows.Count);
        foreach (var row in rows)
        {
            var sb = new StringBuilder();
            for (int c = 0; c < maxCols; c++)
            {
                string cell = c < row.Length ? row[c] : string.Empty;
                if (c > 0) sb.Append(outputSeparator);
                if (c < maxCols - 1) sb.Append(cell.PadRight(widths[c]));
                else sb.Append(cell);
            }
            result.Add(sb.ToString());
        }
        return result;
    }

    private static string[] SplitFields(string line, string? separators, int limit)
    {
        if (separators is null)
        {
            // Blanks: leading blanks dropped, runs collapse; past the limit the rest of the line is one field.
            var list = new List<string>();
            int i = 0, n = line.Length;
            while (true)
            {
                while (i < n && (line[i] == ' ' || line[i] == '\t')) i++;
                if (i >= n) break;
                if (list.Count == limit - 1)
                {
                    list.Add(line.Substring(i).TrimEnd(' ', '\t'));
                    break;
                }
                int start = i;
                while (i < n && line[i] != ' ' && line[i] != '\t') i++;
                list.Add(line.Substring(start, i - start));
            }
            return list.ToArray();
        }

        if (line.Length == 0) return Array.Empty<string>();
        if (separators.Length == 0) return new[] { line }; // an empty set cannot delimit: one field
        var parts = new List<string>();
        int from = 0;
        for (int i = 0; i < line.Length; i++)
        {
            if (parts.Count == limit - 1) break;
            if (separators.IndexOf(line[i]) >= 0)
            {
                parts.Add(line.Substring(from, i - from));
                from = i + 1;
            }
        }
        parts.Add(line.Substring(from));
        return parts.ToArray();
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
