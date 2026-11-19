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
public class InvokeBashDateCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashDateCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
    }

    private string[] RunLines(string script)
    {
        var pwsh = _fixture.AcquireFresh();

        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();

        return result.Select(o => o?.ToString() ?? "").ToArray();
    }

    // Oracle (coreutils 9.4, `date -u -d @1709647629 OPT`). REGRESSION: every one of these options was silently
    // ignored and the default `Tue Mar  5 ...` format printed.
    [Theory]
    [InlineData("'-I'", "2024-03-05")]
    [InlineData("'-Idate'", "2024-03-05")]
    [InlineData("'-Ihours'", "2024-03-05T14+00:00")]
    [InlineData("'-Iminutes'", "2024-03-05T14:07+00:00")]
    [InlineData("'-Iseconds'", "2024-03-05T14:07:09+00:00")]
    [InlineData("'-Is'", "2024-03-05T14:07:09+00:00")]
    [InlineData("'-Ins'", "2024-03-05T14:07:09,000000000+00:00")]
    [InlineData("'--iso-8601'", "2024-03-05")]
    [InlineData("'--iso-8601=seconds'", "2024-03-05T14:07:09+00:00")]
    [InlineData("'--iso=m'", "2024-03-05T14:07+00:00")]
    [InlineData("'-R'", "Tue, 05 Mar 2024 14:07:09 +0000")]
    [InlineData("'--rfc-email'", "Tue, 05 Mar 2024 14:07:09 +0000")]
    [InlineData("'--rfc-2822'", "Tue, 05 Mar 2024 14:07:09 +0000")]
    [InlineData("'--rfc-3339=date'", "2024-03-05")]
    [InlineData("'--rfc-3339=seconds'", "2024-03-05 14:07:09+00:00")]
    [InlineData("'--rfc-3339' ns", "2024-03-05 14:07:09.000000000+00:00")]
    [InlineData("'-uR'", "Tue, 05 Mar 2024 14:07:09 +0000")]
    public void Date_OutputFormatOptions_MatchGnu(string option, string expected)
    {
        var r = CmdResult.Run(_fixture.AcquireFresh(),
            $"(Invoke-BashDate '-u' '-d' '@1709647629' {option}).BashText").AssertSuccess();
        Assert.Equal(new[] { expected }, r.Lines);
    }

    [Theory]
    [InlineData("'-Ifoo'", "date: invalid argument 'foo' for '--iso-8601'", "  - 'ns'")]
    [InlineData("'-IH'", "date: invalid argument 'H' for '--iso-8601'", "  - 'hours'")]
    [InlineData("'--rfc-3339=x'", "date: invalid argument 'x' for '--rfc-3339'", "  - 'seconds'")]
    [InlineData("'--rfc-3339'", "date: option '--rfc-3339' requires an argument", "Try 'date --help'")]
    [InlineData("'--rfc' ", "date: option '--rfc' is ambiguous; possibilities: '--rfc-email' '--rfc-3339'", "Try 'date --help'")]
    [InlineData("'-I' '+%Y'", "date: multiple output formats specified", "")]
    [InlineData("'-R' '-I'", "date: multiple output formats specified", "")]
    [InlineData("'-Q'", "date: invalid option -- 'Q'", "Try 'date --help'")]
    [InlineData("'--bogus'", "date: unrecognized option '--bogus'", "Try 'date --help'")]
    public void Date_BadOutputFormatOption_IsAUsageError(string option, string message, string detail)
        => CmdResult.Run(_fixture.AcquireFresh(), $"Invoke-BashDate '-u' '-d' '@0' {option}").AssertFailed(1, message, detail);

    // -I collides with the -Information* common parameters: a DIRECT bare `-I` binds the decoy (Pester calls cmdlets
    // directly; the transpiler quotes nothing for date).
    [Fact]
    public void Date_DirectBareDashI_IsTheIsoDate()
    {
        var r = CmdResult.Run(_fixture.AcquireFresh(), "(Invoke-BashDate -u -D '@0' -I).BashText").AssertSuccess();
        Assert.Equal(new[] { "1970-01-01" }, r.Lines);
    }

    [Fact]
    public void Date_SetOption_IsRefusedButPrintsTheDateItWouldSet()
    {
        var r = CmdResult.Run(_fixture.AcquireFresh(), "(Invoke-BashDate '-u' '--set=@0' '-I').BashText")
            .AssertFailed(1, "date: cannot set date: Operation not permitted");
        Assert.Equal(new[] { "1970-01-01" }, r.Lines);
    }

    [Theory]
    [InlineData("I", null, "%Y-%m-%d")]
    [InlineData("I", "hours", "%Y-%m-%dT%H%:z")]
    [InlineData("I", "n", "%Y-%m-%dT%H:%M:%S,%N%:z")]
    [InlineData("rfc-3339", "s", "%Y-%m-%d %H:%M:%S%:z")]
    [InlineData("R", null, "%a, %d %b %Y %H:%M:%S %z")]
    public void TryOutputFormat_MapsEachOptionToOneStrftimeFormat(string id, string? spec, string format)
    {
        Assert.True(InvokeBashDateCommand.TryOutputFormat(id, spec, out var f, out _));
        Assert.Equal(format, f);
    }

    [Fact]
    public void ConvertDateFormat_ColonZ_IsTheOffsetWithAColon()
        => Assert.Equal("+05:30 -03:00",
            InvokeBashDateCommand.ConvertDateFormat(new DateTimeOffset(2024, 3, 5, 0, 0, 0, TimeSpan.FromMinutes(330)), "%:z")
            + " " + InvokeBashDateCommand.ConvertDateFormat(new DateTimeOffset(2024, 3, 5, 0, 0, 0, TimeSpan.FromHours(-3)), "%:z"));

    [Fact]
    public void Date_NoArgs_EmitsTypedOutput()
    {
        var pwsh = _fixture.AcquireFresh();
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
        var pwsh = _fixture.AcquireFresh();
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
        var pwsh = _fixture.AcquireFresh();
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
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(
            "Invoke-BashDate -D 'not a date' 2>$null").Invoke();
        Assert.Empty(result);
    }

    [Fact]
    public void Date_ReferenceMissingFile_NoOutputWritesError()
    {
        var pwsh = _fixture.AcquireFresh();
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

    // ---- operands without '+' (oracle: GNU date, LC_ALL=C) ----

    // `date %x` -> "date: invalid date '%x'", status 1, no output. It printed the current date with status 0.
    [Fact]
    public void Date_OperandWithoutPlus_IsAnInvalidDate()
    {
        var r = CmdResult.Run(_fixture.AcquireFresh(), "Invoke-BashDate '%x'").AssertFailed(1, "date: invalid date '%x'");
        Assert.Empty(r.Lines);
    }

    // `date +%Y extra` -> "date: extra operand ‘extra’" + the --help hint, status 1, no output.
    [Fact]
    public void Date_SecondOperand_IsAnExtraOperand()
    {
        var r = CmdResult.Run(_fixture.AcquireFresh(), "Invoke-BashDate '+%Y' extra").AssertFailed(1, "date: extra operand ‘extra’");
        Assert.Empty(r.Lines);
    }

    // `date 01010101` (MMDDhhmm) -> "date: cannot set date: Operation not permitted", status 1, and the time is printed.
    [Fact]
    public void Date_SetOperand_IsRefusedButPrintsTheTime()
    {
        var r = CmdResult.Run(_fixture.AcquireFresh(), "Invoke-BashDate 01010101").AssertFailed(1, "cannot set date: Operation not permitted");
        Assert.NotEmpty(r.Lines);
    }
}
