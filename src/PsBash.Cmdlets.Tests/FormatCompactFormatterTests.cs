using PsBash.Cmdlets;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure unit tests for <see cref="CompactObjectFormatter"/> — the rendering engine behind
/// <c>Format-Compact</c>. No PowerShell runspace needed.
/// </summary>
public class FormatCompactFormatterTests
{
    private static IReadOnlyDictionary<string, string?> Row(params (string, string?)[] cells)
    {
        var d = new Dictionary<string, string?>(System.StringComparer.Ordinal);
        foreach (var (k, v) in cells) d[k] = v;
        return d;
    }

    [Fact]
    public void RenderTable_EmptyRows_ReturnsEmpty()
        => Assert.Empty(CompactObjectFormatter.RenderTable(["Name"], []));

    [Fact]
    public void RenderTable_EmptyColumns_ReturnsEmpty()
        => Assert.Empty(CompactObjectFormatter.RenderTable([], [Row(("Name", "x"))]));

    [Fact]
    public void RenderTable_HeaderOnce_ThenAlignedRows()
    {
        var rows = new[]
        {
            Row(("Name", "main.rs"), ("Size", "1234")),
            Row(("Name", "cargo.toml"), ("Size", "456")),
        };

        var lines = CompactObjectFormatter.RenderTable(["Name", "Size"], rows);

        Assert.Equal(3, lines.Count); // 1 header + 2 rows, no rule line
        Assert.Equal("Name        Size", lines[0]); // padded to widest cell ("cargo.toml")
        Assert.Equal("main.rs     1234", lines[1]);
        Assert.Equal("cargo.toml  456", lines[2]);
        Assert.Single(lines, l => l.Contains("Name")); // header appears exactly once
    }

    [Fact]
    public void RenderTable_DropsAllEmptyColumns()
    {
        var rows = new[]
        {
            Row(("Name", "a"), ("Mode", "")),
            Row(("Name", "b"), ("Mode", null)),
        };

        var lines = CompactObjectFormatter.RenderTable(["Name", "Mode"], rows);

        Assert.DoesNotContain("Mode", lines[0]); // all-empty column dropped
        Assert.Contains("Name", lines[0]);
    }

    [Fact]
    public void RenderTable_AllColumnsEmpty_ReturnsEmpty()
    {
        var rows = new[] { Row(("A", ""), ("B", null)) };
        Assert.Empty(CompactObjectFormatter.RenderTable(["A", "B"], rows));
    }

    [Fact]
    public void RenderTable_BeyondMaxRows_AppendsMoreRows()
    {
        var rows = Enumerable.Range(1, 10).Select(i => Row(("N", i.ToString()))).ToArray();

        var lines = CompactObjectFormatter.RenderTable(["N"], rows, ultra: false, maxRows: 3);

        Assert.Equal("+7 more rows", lines[^1]);
        Assert.Equal(5, lines.Count); // header + 3 rows + marker
        Assert.DoesNotContain(lines, l => l == "10");
    }

    [Fact]
    public void RenderTable_WithinMaxRows_NoMarker()
    {
        var rows = new[] { Row(("N", "1")), Row(("N", "2")) };
        var lines = CompactObjectFormatter.RenderTable(["N"], rows, maxRows: 50);
        Assert.DoesNotContain(lines, l => l.Contains("more rows"));
    }

    [Fact]
    public void RenderTable_Ultra_TabSeparatedNoPadding()
    {
        var rows = new[]
        {
            Row(("Name", "main.rs"), ("Size", "1234")),
            Row(("Name", "x"), ("Size", "5")),
        };

        var lines = CompactObjectFormatter.RenderTable(["Name", "Size"], rows, ultra: true);

        Assert.Equal("Name\tSize", lines[0]);
        Assert.Equal("main.rs\t1234", lines[1]);
        Assert.Equal("x\t5", lines[2]); // no padding in ultra mode
    }

    [Fact]
    public void RenderTable_MissingCell_TreatedAsEmpty()
    {
        var rows = new[]
        {
            Row(("Name", "a"), ("Extra", "present")),
            Row(("Name", "b")), // no "Extra" key
        };

        var lines = CompactObjectFormatter.RenderTable(["Name", "Extra"], rows);

        Assert.Contains("Extra", lines[0]); // kept (one row has it)
        Assert.EndsWith("present", lines[1]);
        Assert.Equal("b", lines[2]); // missing cell -> empty, trailing padding trimmed
    }

    [Fact]
    public void RenderTable_ColumnNonEmptyOnlyBeyondMaxRows_IsDropped()
    {
        // "Mode" is blank in every visible row (1..3) and only carries data in row 4,
        // which is collapsed past maxRows=3. The visible table must not show "Mode".
        var rows = new[]
        {
            Row(("Name", "a"), ("Mode", "")),
            Row(("Name", "b"), ("Mode", "")),
            Row(("Name", "c"), ("Mode", null)),
            Row(("Name", "d"), ("Mode", "x")), // hidden — beyond maxRows
        };

        var lines = CompactObjectFormatter.RenderTable(["Name", "Mode"], rows, ultra: false, maxRows: 3);

        Assert.DoesNotContain("Mode", lines[0]);      // dropped: blank across all visible rows
        Assert.Contains("Name", lines[0]);
        Assert.DoesNotContain(lines, l => l.Contains("Mode")); // and never leaks into a data row
        Assert.Equal("+1 more rows", lines[^1]);      // the data-bearing row is the collapsed one
    }

    [Fact]
    public void RenderTable_MaxRowsZero_CollapsesAll()
    {
        var rows = new[] { Row(("N", "1")), Row(("N", "2")) };
        var lines = CompactObjectFormatter.RenderTable(["N"], rows, maxRows: 0);

        Assert.Equal("N", lines[0]);             // header only
        Assert.Equal("+2 more rows", lines[^1]); // all data collapsed
    }

    [Fact]
    public void RenderInline_DropsEmptyValues()
    {
        var inline = CompactObjectFormatter.RenderInline(
            ["Name", "Size", "Mode"],
            Row(("Name", "main.rs"), ("Size", "1234"), ("Mode", "")));

        Assert.Equal("Name=main.rs Size=1234", inline);
    }

    [Fact]
    public void RenderTable_OutputHasNoBlankLines()
    {
        var rows = new[] { Row(("Name", "a"), ("Size", "1")), Row(("Name", "b"), ("Size", "2")) };
        var lines = CompactObjectFormatter.RenderTable(["Name", "Size"], rows);
        Assert.DoesNotContain(lines, string.IsNullOrEmpty);
    }
}
