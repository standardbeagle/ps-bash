using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// End-to-end smoke tests for the <c>Format-Compact</c> cmdlet through a real runspace:
/// PSObject adaptation → <see cref="PsBash.Cmdlets.CompactObjectFormatter"/> → string output.
/// </summary>
public class FormatCompactCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public FormatCompactCommandTests(SharedPwshFixture fixture) => _fixture = fixture;

    private List<string> Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        return result.Select(o => o?.ToString() ?? string.Empty).ToList();
    }

    [Fact]
    public void Objects_RenderAsCompactTable_HeaderOnce()
    {
        var lines = Run("""
            @(
              [pscustomobject]@{ Name='main.rs';    Size=1234 },
              [pscustomobject]@{ Name='cargo.toml'; Size=456  }
            ) | Format-Compact
            """);

        Assert.Equal(3, lines.Count);                                   // header + 2 rows
        Assert.Single(lines, l => l.Contains("Name"));                  // header once, no rule line
        Assert.Contains(lines, l => l.Contains("main.rs") && l.Contains("1234"));
        Assert.DoesNotContain(lines, string.IsNullOrWhiteSpace);        // no blank lines
    }

    [Fact]
    public void NullColumn_IsDropped()
    {
        var lines = Run("""
            @(
              [pscustomobject]@{ Name='a'; Mode=$null },
              [pscustomobject]@{ Name='b'; Mode=''    }
            ) | Format-Compact
            """);

        Assert.DoesNotContain(lines, l => l.Contains("Mode"));
        Assert.Contains(lines, l => l.Contains("Name"));
    }

    [Fact]
    public void SingleObject_RendersInline()
    {
        var lines = Run("[pscustomobject]@{ Name='foo'; Size=1 } | Format-Compact");

        var line = Assert.Single(lines);
        Assert.Equal("Name=foo Size=1", line);
    }

    [Fact]
    public void Scalars_PassThroughAsLines()
    {
        var lines = Run("'alpha','beta' | Format-Compact");

        Assert.Equal(["alpha", "beta"], lines);
    }

    [Fact]
    public void Ultra_UsesTabSeparators()
    {
        var lines = Run("""
            @(
              [pscustomobject]@{ Name='main.rs'; Size=1234 },
              [pscustomobject]@{ Name='x';       Size=5    }
            ) | Format-Compact -Ultra
            """);

        Assert.Contains(lines, l => l.Contains('\t'));
        Assert.Equal("Name\tSize", lines[0]);
    }

    [Fact]
    public void MaxRows_CollapsesExcess()
    {
        var lines = Run("1..5 | ForEach-Object { [pscustomobject]@{ N = $_ } } | Format-Compact -MaxRows 2");

        Assert.Equal("+3 more rows", lines[^1]);
    }

    [Fact]
    public void CompactOutput_MatchesFormatTableShape_AndIsSmaller()
    {
        // Differential-vs-Format-Table SHAPE test (not just aggregate length): the compact
        // output must present the SAME columns, the SAME number of data rows, and the SAME
        // per-row values as Format-Table — proving it compacts without dropping or corrupting
        // data — while still being byte-smaller.
        var pwsh = _fixture.AcquireFresh();
        var script = """
            $rows = 1..5 | ForEach-Object { [pscustomobject]@{ Name = "file$_"; Size = $_ } }
            $compact = ($rows | Format-Compact | Out-String)
            $table   = ($rows | Format-Table   | Out-String)
            [pscustomobject]@{ Compact = $compact; Table = $table }
            """;
        var result = pwsh.AddScript(script).Invoke();
        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));

        var row = result[0];
        // Out-String's result arrives wrapped in a PSObject; unwrap via ToString rather than a hard cast.
        var compact = row.Properties["Compact"].Value?.ToString() ?? string.Empty;
        var table = row.Properties["Table"].Value?.ToString() ?? string.Empty;

        var (tableHeaders, tableRows) = ParseFormatTable(table);
        var (compactHeaders, compactRows) = ParseCompact(compact);

        // Same columns (order-insensitive), same row count, same per-row value set.
        Assert.Equal(tableHeaders.OrderBy(h => h), compactHeaders.OrderBy(h => h));
        Assert.Equal(tableRows.Count, compactRows.Count);
        for (var i = 0; i < tableRows.Count; i++)
            Assert.Equal(tableRows[i].OrderBy(v => v), compactRows[i].OrderBy(v => v));

        // Every input value survives (no silent drop) and nothing is invented.
        var compactCells = compactRows.SelectMany(r => r).ToHashSet();
        for (var i = 1; i <= 5; i++)
        {
            Assert.Contains($"file{i}", compactCells);
            Assert.Contains($"{i}", compactCells);
        }

        // …and the whole point: it is smaller than Format-Table.
        Assert.True(compact.Length < table.Length, $"compact {compact.Length} should be < format-table {table.Length}");
    }

    /// <summary>
    /// Parse a <c>Format-Table</c> block into (headers, rows-of-values). The rule line
    /// (<c>---- ----</c>) delimits the header from the data rows; whitespace runs split cells.
    /// </summary>
    private static (List<string> Headers, List<List<string>> Rows) ParseFormatTable(string table)
    {
        var lines = table.Replace("\r\n", "\n").Split('\n')
                         .Where(l => l.Trim().Length > 0).ToList();
        var ruleIdx = lines.FindIndex(l => l.Trim().Length > 0 && l.Trim().All(c => c is '-' or ' '));
        Assert.True(ruleIdx > 0, "Format-Table output should contain a header rule line");

        var headers = SplitCells(lines[ruleIdx - 1]);
        var rows = lines.Skip(ruleIdx + 1).Select(SplitCells).Where(r => r.Count > 0).ToList();
        return (headers, rows);
    }

    /// <summary>Parse <c>Format-Compact</c> table output: line 0 is the header, the rest are
    /// data rows (excluding any trailing <c>+N more rows</c> marker).</summary>
    private static (List<string> Headers, List<List<string>> Rows) ParseCompact(string compact)
    {
        var lines = compact.Replace("\r\n", "\n").Split('\n')
                          .Where(l => l.Trim().Length > 0)
                          .Where(l => !l.TrimStart().StartsWith('+') || !l.Contains("more rows")).ToList();
        var headers = SplitCells(lines[0]);
        var rows = lines.Skip(1).Select(SplitCells).Where(r => r.Count > 0).ToList();
        return (headers, rows);
    }

    private static List<string> SplitCells(string line)
        => line.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries).ToList();
}
