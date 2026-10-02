using System.Text;

namespace PsBash.Cmdlets;

/// <summary>One printable cell of a multi-column <c>ls</c> listing: the text to write and its display width.</summary>
internal readonly record struct LsCell(string Text, int Width);

/// <summary>
/// The pure column layouts of <c>ls -C</c> (down the columns), <c>-x</c> (across the rows) and <c>-m</c> (comma
/// separated), a port of GNU coreutils 9.4 <c>ls.c</c> (<c>calculate_columns</c>, <c>print_many_per_line</c>,
/// <c>print_horizontal</c>, <c>print_with_separator</c>, <c>indent</c>). Each candidate column count is tried with the
/// per-column maximum widths it would need (every column but the last is padded by two); the largest count whose line
/// stays strictly below the line length wins. Columns are separated with tabs where a tab stop is crossed (tab size
/// <c>-T</c>, default 8; 0 = spaces only) exactly as GNU does, so the bytes match.
/// </summary>
internal static class LsColumns
{
    /// <summary>GNU's MIN_COLUMN_WIDTH: one character plus the two-space separator.</summary>
    private const int MinColumnWidth = 3;

    /// <summary>Per-column widths of the best layout, or one column when nothing wider fits.</summary>
    internal static int[] Plan(IReadOnlyList<LsCell> cells, long lineLength, bool across)
    {
        int n = cells.Count;
        if (lineLength == 0) lineLength = long.MaxValue;     // -w 0 / COLUMNS=0: no limit
        long maxIdx = Math.Max(1, lineLength / MinColumnWidth);
        int maxCols = (int)Math.Min(maxIdx, n);

        var valid = new bool[maxCols];
        var lineLen = new long[maxCols];
        var colArr = new int[maxCols][];
        for (int i = 0; i < maxCols; i++)
        {
            valid[i] = true;
            lineLen[i] = (long)(i + 1) * MinColumnWidth;
            colArr[i] = new int[i + 1];
            Array.Fill(colArr[i], MinColumnWidth);
        }

        for (int f = 0; f < n; f++)
        {
            int nameLength = cells[f].Width;
            for (int j = 0; j < maxCols; j++)
            {
                if (!valid[j]) continue;
                int idx = across ? f % (j + 1) : f / ((n + j) / (j + 1));
                int real = nameLength + (idx == j ? 0 : 2);
                if (colArr[j][idx] < real)
                {
                    lineLen[j] += real - colArr[j][idx];
                    colArr[j][idx] = real;
                    valid[j] = lineLen[j] < lineLength;
                }
            }
        }

        int cols = maxCols;
        for (; cols > 1; cols--) if (valid[cols - 1]) break;
        return cols <= 0 ? new[] { MinColumnWidth } : colArr[cols - 1];
    }

    /// <summary>GNU <c>ls -C</c> (<paramref name="across"/> false) / <c>-x</c> (true): the finished lines.</summary>
    internal static List<string> Render(IReadOnlyList<LsCell> cells, long lineLength, bool across, int tabSize)
    {
        var lines = new List<string>();
        int n = cells.Count;
        if (n == 0) return lines;
        // Oracle: with no line limit (-w 0 / COLUMNS=0) GNU separates with spaces even across a tab stop.
        if (lineLength == 0) tabSize = 0;
        var colArr = Plan(cells, lineLength, across);
        int cols = colArr.Length;
        var sb = new StringBuilder();

        if (!across)
        {
            int rows = n / cols + (n % cols != 0 ? 1 : 0);
            for (int row = 0; row < rows; row++)
            {
                sb.Clear();
                int col = 0, fileNo = row, pos = 0;
                while (true)
                {
                    sb.Append(cells[fileNo].Text);
                    int nameLength = cells[fileNo].Width;
                    int maxName = colArr[col++];
                    fileNo += rows;
                    if (fileNo >= n) break;
                    Indent(sb, pos + nameLength, pos + maxName, tabSize);
                    pos += maxName;
                }
                lines.Add(sb.ToString());
            }
            return lines;
        }

        {
            sb.Append(cells[0].Text);
            int nameLength = cells[0].Width, maxName = colArr[0], pos = 0;
            for (int f = 1; f < n; f++)
            {
                int col = f % cols;
                if (col == 0)
                {
                    lines.Add(sb.ToString());
                    sb.Clear();
                    pos = 0;
                }
                else
                {
                    Indent(sb, pos + nameLength, pos + maxName, tabSize);
                    pos += maxName;
                }
                sb.Append(cells[f].Text);
                nameLength = cells[f].Width;
                maxName = colArr[col];
            }
            lines.Add(sb.ToString());
        }
        return lines;
    }

    /// <summary>GNU <c>ls -m</c>: names joined by ", ", wrapping before a name that would reach the line length.</summary>
    internal static List<string> RenderCommas(IReadOnlyList<LsCell> cells, long lineLength)
    {
        var lines = new List<string>();
        if (cells.Count == 0) return lines;
        var sb = new StringBuilder();
        long pos = 0;
        for (int i = 0; i < cells.Count; i++)
        {
            int len = cells[i].Width;
            if (i != 0)
            {
                sb.Append(',');
                if (lineLength == 0 || pos + len + 2 < lineLength)
                {
                    pos += 2;
                    sb.Append(' ');
                }
                else
                {
                    pos = 0;
                    lines.Add(sb.ToString());
                    sb.Clear();
                }
            }
            sb.Append(cells[i].Text);
            pos += len;
        }
        lines.Add(sb.ToString());
        return lines;
    }

    /// <summary>GNU <c>indent(from, to)</c>: a tab where one reaches a new tab stop, else a space.</summary>
    private static void Indent(StringBuilder sb, int from, int to, int tabSize)
    {
        while (from < to)
        {
            if (tabSize != 0 && to / tabSize > (from + 1) / tabSize)
            {
                sb.Append('\t');
                from += tabSize - from % tabSize;
            }
            else
            {
                sb.Append(' ');
                from++;
            }
        }
    }
}
