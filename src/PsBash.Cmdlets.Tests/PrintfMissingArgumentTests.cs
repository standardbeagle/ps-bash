using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Guard for the class "printf skips a conversion whose argument is missing": POSIX says a missing argument is the
/// EMPTY argument, so <c>printf FMT</c> must print exactly what <c>printf FMT ''</c> prints — width, precision and
/// flags included. Every conversion letter is run both ways; a new conversion that re-adds a per-case
/// <c>argIdx &lt; Count</c> guard fails here. The ps-bash-specific invariant is asserted directly (bash agrees, see
/// <c>RuntimeCommandParityDifferentialTests.Printf_MissingArguments_AreEmpty</c> for the oracle values).
/// </summary>
public class PrintfMissingArgumentTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    public PrintfMissingArgumentTests(SharedPwshFixture fixture) => _fixture = fixture;

    public static TheoryData<string> Formats()
    {
        var data = new TheoryData<string>();
        foreach (var conv in new[] { "s", "q", "b", "d", "i", "u", "x", "X", "o", "e", "E", "f", "g", "G" })
        {
            data.Add($"[%{conv}]");
            data.Add($"[%6{conv}]");
            data.Add($"[%-6{conv}|]");
        }
        data.Add("[%06d][%+d][%.3d][%8.3f][%#x][%5c]");
        return data;
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Printf_MissingArgument_PrintsAsEmptyArgument(string format)
    {
        string fmt = "'" + format.Replace("'", "''") + "'";
        var missing = CmdResult.Run(_fixture.AcquireFresh(), $"Invoke-BashPrintf {fmt}").AssertSuccess();
        var empty = CmdResult.Run(_fixture.AcquireFresh(), $"Invoke-BashPrintf {fmt} ''").AssertSuccess();
        Assert.Equal(empty.Stdout, missing.Stdout);
    }

    [Fact]
    public void Printf_MissingArgument_WidthStillApplies()
    {
        var r = CmdResult.Run(_fixture.AcquireFresh(), "Invoke-BashPrintf '[%5s][%3d][%.2f][%q]'").AssertSuccess();
        Assert.Equal("[     ][  0][0.00]['']", r.Stdout);
    }
}
