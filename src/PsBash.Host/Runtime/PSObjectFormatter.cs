using System.Management.Automation;

namespace PsBash.Host.Runtime;

/// <summary>
/// Minimal native-PSObject table formatter used by <see cref="SdkWorker"/> when
/// pipeline output contains typed PSObjects with no <c>BashText</c> property —
/// e.g. <c>Get-PnpDevice | Select-Object FriendlyName, Status</c> or a bare
/// <c>[PSCustomObject]@{...}</c> from a user script.
///
/// <para>
/// Why a hand-rolled formatter rather than <c>Out-String -Stream</c>: the
/// in-process SDK runspace can't reliably resolve the
/// <c>Microsoft.PowerShell.Utility</c> module on Windows because the v5
/// manifest its name resolves to references the removed <c>PSSnapIn</c> type
/// (see comment in <see cref="SdkRunspace"/>). Calling <c>Out-String</c> via
/// <c>AddCommand</c> goes through PowerShell's command-discovery path and
/// triggers that broken module load, producing
/// <c>"command was found in the module 'Microsoft.PowerShell.Utility', but
/// the module could not be loaded"</c> errors and silently dropping output
/// — the exact symptom of the Get-PnpDevice rendering bug.
/// </para>
///
/// <para>
/// This formatter mirrors PowerShell's default table view for property bags:
/// it discovers the union of property names from the batch, computes column
/// widths from the longest value seen, emits a header row, an
/// underline-style separator row, and one data row per object. Output style
/// matches native pwsh's table layout closely enough for parity tests that
/// strip ANSI and trailing whitespace per Directive 1 of the QA rubric.
/// </para>
/// </summary>
internal static class PSObjectFormatter
{
    private const int MaxColumnWidth = 60;

    /// <summary>
    /// Format a batch of PSObjects as a table, returning one string per
    /// rendered line (header, separator, each row). Returns empty enumerable
    /// for an empty batch.
    /// </summary>
    public static IEnumerable<string> FormatAsTable(IReadOnlyList<PSObject> items)
    {
        if (items.Count == 0) yield break;

        // Build property order from the first item, then extend with any new
        // properties seen in subsequent items. This matches native pwsh's
        // "first object wins for column order" behavior.
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item is null) continue;
            foreach (var prop in item.Properties)
            {
                if (prop is null) continue;
                if (seen.Add(prop.Name)) columns.Add(prop.Name);
            }
        }

        if (columns.Count == 0)
        {
            // No properties — fall back to ToString() per object so we still
            // emit something rather than swallowing the data.
            foreach (var item in items)
                yield return item?.ToString() ?? "";
            yield break;
        }

        // Materialize rows as string[] so column-width calculations have
        // their final form before we render.
        var rows = new List<string[]>(items.Count);
        foreach (var item in items)
        {
            var row = new string[columns.Count];
            for (int c = 0; c < columns.Count; c++)
            {
                object? val;
                try
                {
                    val = item?.Properties[columns[c]]?.Value;
                }
                catch
                {
                    val = null;
                }
                row[c] = FormatValue(val);
            }
            rows.Add(row);
        }

        // Compute column widths: max(header, all row values), capped to
        // MaxColumnWidth so a single very long value can't blow out layout.
        var widths = new int[columns.Count];
        for (int c = 0; c < columns.Count; c++)
        {
            widths[c] = Math.Min(MaxColumnWidth, columns[c].Length);
            foreach (var row in rows)
                widths[c] = Math.Min(MaxColumnWidth, Math.Max(widths[c], row[c].Length));
        }

        // Header row.
        yield return RenderRow(columns.Select((name, c) => PadOrTruncate(name, widths[c])).ToArray());

        // Separator row: dashes the width of each column.
        yield return RenderRow(widths.Select(w => new string('-', w)).ToArray());

        // Data rows.
        foreach (var row in rows)
        {
            var rendered = new string[columns.Count];
            for (int c = 0; c < columns.Count; c++)
                rendered[c] = PadOrTruncate(row[c], widths[c]);
            yield return RenderRow(rendered);
        }
    }

    /// <summary>
    /// Render a single value as PowerShell's default table view would: null
    /// becomes empty, strings pass through, IEnumerables (other than string)
    /// join with comma-space, everything else uses .ToString().
    /// </summary>
    private static string FormatValue(object? value)
    {
        if (value is null) return string.Empty;
        if (value is PSObject pso) value = pso.BaseObject;
        if (value is null) return string.Empty;
        if (value is string s) return s;
        // Don't iterate into character-by-character for strings; that's why
        // the string check is above.
        if (value is System.Collections.IEnumerable enumerable)
        {
            var parts = new List<string>();
            foreach (var entry in enumerable)
                parts.Add(entry?.ToString() ?? string.Empty);
            return string.Join(", ", parts);
        }
        return value.ToString() ?? string.Empty;
    }

    private static string PadOrTruncate(string s, int width)
    {
        if (s.Length == width) return s;
        if (s.Length > width)
        {
            // Truncate with ellipsis when room allows; otherwise hard truncate.
            return width >= 4 ? s.Substring(0, width - 3) + "..." : s.Substring(0, width);
        }
        return s.PadRight(width);
    }

    private static string RenderRow(string[] cells)
        // Single space gutter between columns; trailing whitespace stripped
        // by string.TrimEnd so canonicalization rules in the QA rubric
        // (Directive 1: strip trailing whitespace per line before diff)
        // pass cleanly without the formatter having to know about ANSI.
        => string.Join(" ", cells).TrimEnd();
}
