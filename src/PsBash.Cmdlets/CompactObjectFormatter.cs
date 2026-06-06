using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Pure, token-efficient renderer for tabular object data — the engine behind the
/// <c>Format-Compact</c> cmdlet. Compared to <c>Format-Table</c> it: drops all-empty
/// columns, prints a single header with no rule line, min-width aligns, collapses rows
/// beyond a cap to a <c>+N more rows</c> marker, and (in <c>-Ultra</c>) tab-separates
/// instead of padding. No I/O and no PowerShell dependency, so it is unit-tested directly.
/// </summary>
public static class CompactObjectFormatter
{
    public const int DefaultMaxRows = 50;

    /// <summary>
    /// Render <paramref name="rows"/> (each a column→value map) as compact table lines.
    /// Columns whose every shown value is null/empty are dropped. Returns an empty list
    /// when there is nothing to show.
    /// </summary>
    public static IReadOnlyList<string> RenderTable(
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows,
        bool ultra = false,
        int maxRows = DefaultMaxRows)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0 || columns.Count == 0) return [];

        var kept = new List<string>();
        foreach (var c in columns)
            if (rows.Any(r => !string.IsNullOrEmpty(Cell(r, c)))) kept.Add(c);
        if (kept.Count == 0) return [];

        var limit = Math.Max(0, maxRows);
        var shown = rows.Count > limit ? rows.Take(limit).ToList() : rows;

        var widths = new int[kept.Count];
        if (!ultra)
        {
            for (var i = 0; i < kept.Count; i++)
            {
                var w = kept[i].Length;
                foreach (var r in shown) w = Math.Max(w, (Cell(r, kept[i]) ?? string.Empty).Length);
                widths[i] = w;
            }
        }

        var lines = new List<string>(shown.Count + 2)
        {
            FormatRow(kept, widths, ultra, i => kept[i]),
        };
        foreach (var r in shown)
            lines.Add(FormatRow(kept, widths, ultra, i => Cell(r, kept[i]) ?? string.Empty));

        if (rows.Count > limit)
            lines.Add($"+{rows.Count - limit} more rows");

        return lines;
    }

    /// <summary>Render a single object inline as space-separated <c>key=value</c> (empty values dropped).</summary>
    public static string RenderInline(IReadOnlyList<string> columns, IReadOnlyDictionary<string, string?> row)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(row);
        return string.Join(' ',
            columns.Where(c => !string.IsNullOrEmpty(Cell(row, c)))
                   .Select(c => $"{c}={Cell(row, c)}"));
    }

    private static string FormatRow(IReadOnlyList<string> kept, int[] widths, bool ultra, Func<int, string> cell)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < kept.Count; i++)
        {
            if (i > 0) sb.Append(ultra ? "\t" : "  ");
            var v = cell(i);
            sb.Append(ultra ? v : v.PadRight(widths[i]));
        }
        return ultra ? sb.ToString() : sb.ToString().TrimEnd();
    }

    private static string? Cell(IReadOnlyDictionary<string, string?> row, string column)
        => row.TryGetValue(column, out var v) ? v : null;
}
