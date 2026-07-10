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
    public void CompactOutput_IsSmallerThanFormatTable()
    {
        var pwsh = _fixture.AcquireFresh();
        var script = """
            $rows = 1..5 | ForEach-Object { [pscustomobject]@{ Name = "file$_"; Size = $_ } }
            $compact = ($rows | Format-Compact | Out-String)
            $table   = ($rows | Format-Table   | Out-String)
            [pscustomobject]@{ Compact = $compact.Length; Table = $table.Length }
            """;
        var result = pwsh.AddScript(script).Invoke();
        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));

        var row = result[0];
        var compactLen = (int)row.Properties["Compact"].Value;
        var tableLen = (int)row.Properties["Table"].Value;
        Assert.True(compactLen < tableLen, $"compact {compactLen} should be < format-table {tableLen}");
    }
}
