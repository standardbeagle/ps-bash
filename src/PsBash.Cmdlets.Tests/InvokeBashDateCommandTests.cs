using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 follow-on migration of
/// <c>Invoke-BashDate</c> from PsBash.psm1 to a binary cmdlet
/// (<see cref="PsBash.Cmdlets.InvokeBashDateCommand"/>).
///
/// Oracle: the psm1 function. Tests cover default invocation, <c>-d</c>
/// parsed date, <c>-u</c> UTC, <c>+%Y-%m-%d</c> / <c>+%s</c> format strings,
/// <c>-r</c> reference file, missing-reference error, alias resolution,
/// <c>--help</c>, and the Directive-12 injection probe.
/// </summary>
public class InvokeBashDateCommandTests
{
    private static string[] RunLines(string script)
    {
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$error.Clear()").Invoke();
        pwsh.Commands.Clear();

        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();

        return result.Select(o => o?.ToString() ?? "").ToArray();
    }

    [Fact]
    public void Date_NoArgs_EmitsTypedOutput()
    {
        using var pwsh = PwshTestFixture.Create();
        var result = pwsh.AddScript("(Invoke-BashDate).PSTypeNames -join ','").Invoke();
        Assert.Single(result);
        var types = result[0]?.ToString() ?? "";
        Assert.Contains("PsBash.DateOutput", types);
    }

    [Fact]
    public void Date_NoArgs_BashTextNotEmpty()
    {
        // Default output: "Thu Jan  2 15:04:05 MST 2006"-style. We can't fix
        // the wall clock, but BashText must be a non-empty string of that
        // shape (six space-separated fields).
        var lines = RunLines("(Invoke-BashDate).BashText");
        Assert.Single(lines);
        var parts = lines[0].Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        Assert.True(parts.Length >= 6, $"expected >=6 space-sep fields, got: '{lines[0]}'");
    }

    [Fact]
    public void Date_DateFlag_ParsesAndFormatsKnownDate()
    {
        // -d "2025-01-15" → 2025-01-15 at 00:00:00 local. Verify the Y/M/D
        // properties on the typed object — wall-clock independent.
        using var pwsh = PwshTestFixture.Create();
        var result = pwsh.AddScript(
            "$d = Invoke-BashDate -D '2025-01-15'; \"$($d.Year)-$($d.Month)-$($d.Day)\"").Invoke();
        Assert.Single(result);
        Assert.Equal("2025-1-15", result[0]?.ToString());
    }

    [Fact]
    public void Date_DateFlagWithFormat_YearMonthDay()
    {
        // +%Y-%m-%d on a parsed date must produce the exact known string.
        var lines = RunLines(
            "(Invoke-BashDate -D '2025-01-15' '+%Y-%m-%d').BashText");
        Assert.Single(lines);
        Assert.Equal("2025-01-15", lines[0]);
    }

    [Fact]
    public void Date_DateFlagWithEpochFormat_EmitsUnixSeconds()
    {
        // +%s emits ToUnixTimeSeconds. 1970-01-01 UTC = 0.
        var lines = RunLines(
            "(Invoke-BashDate -u -D '1970-01-01T00:00:00Z' '+%s').BashText");
        Assert.Single(lines);
        Assert.Equal("0", lines[0]);
    }

    [Fact]
    public void Date_UtcFlag_TimeZoneIsUTC()
    {
        using var pwsh = PwshTestFixture.Create();
        var result = pwsh.AddScript("(Invoke-BashDate -u).TimeZone").Invoke();
        Assert.Single(result);
        Assert.Equal("UTC", result[0]?.ToString());
    }

    [Fact]
    public void Date_FormatPercentPercent_EmitsLiteralPercent()
    {
        var lines = RunLines(
            "(Invoke-BashDate -D '2025-01-15' '+100%%').BashText");
        Assert.Single(lines);
        Assert.Equal("100%", lines[0]);
    }

    [Fact]
    public void Date_FormatUnknownSpec_PreservesPercentAndChar()
    {
        // Oracle: unknown %X passes through as literal "%X".
        var lines = RunLines(
            "(Invoke-BashDate -D '2025-01-15' '+%Q').BashText");
        Assert.Single(lines);
        Assert.Equal("%Q", lines[0]);
    }

    [Fact]
    public void Date_DateFlagInvalid_NoOutputWritesError()
    {
        // Oracle returns after Write-BashError on parse failure: no output.
        using var pwsh = PwshTestFixture.Create();
        var result = pwsh.AddScript(
            "Invoke-BashDate -D 'not a date' 2>$null").Invoke();
        Assert.Empty(result);
    }

    [Fact]
    public void Date_ReferenceMissingFile_NoOutputWritesError()
    {
        using var pwsh = PwshTestFixture.Create();
        var result = pwsh.AddScript(
            "Invoke-BashDate -r /no/such/path/xyzzy.txt 2>$null").Invoke();
        Assert.Empty(result);
    }

    [Fact]
    public void Date_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashDate --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("date", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Date_ViaAlias_Works()
    {
        var lines = RunLines("(date -D '2025-01-15' '+%Y').BashText");
        Assert.Single(lines);
        Assert.Equal("2025", lines[0]);
    }

    // ---- injection probe (Directive 12) ----

    [Fact]
    public void Date_FormatContainingScriptblockSubst_TreatedAsLiteral()
    {
        // A format string containing $(...) must reach the per-char engine
        // verbatim and emit the embedded $(...) literal — never evaluated.
        // %Y still expands to the year for the date supplied via -D.
        var lines = RunLines(
            "(Invoke-BashDate -D '2025-01-15' '+$(throw ''pwn'')%Y').BashText");
        Assert.Single(lines);
        Assert.Equal("$(throw 'pwn')2025", lines[0]);
    }
}
