using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The <c>time</c> keyword's report text. Expected strings are bash 5.2's for the same times (oracle: TIMEFORMAT
/// with fixed inputs is deterministic; `{ time -p sleep 0.01; } 2>&amp;1` gives the -p layout).
/// </summary>
public class BashTimeKeywordTests
{
    [Theory]
    [InlineData("\nreal\t%3lR\nuser\t%3lU\nsys\t%3lS", 0.0159, 0.0019, 0.007, "\nreal\t0m0.015s\nuser\t0m0.001s\nsys\t0m0.007s")]
    [InlineData("real %2R\nuser %2U\nsys %2S", 0.0209, 0.0, 0.0, "real 0.02\nuser 0.00\nsys 0.00")]
    [InlineData("R=%R r=%3R l=%lR u=%2U s=%S %% x", 0.0168, 0.0, 0.0014, "R=0.016 r=0.016 l=0m0.016s u=0.00 s=0.001 % x")]
    [InlineData("%0R|%1R|%lR", 61.25, 0, 0, "61|61.2|1m1.250s")]
    [InlineData("%P", 2.0, 0.5, 0.5, "50.00")]
    [InlineData("%2R", 0.29, 0, 0, "0.29")]
    public void Format_MatchesBashTimeformat(string format, double real, double user, double sys, string expected)
        => Assert.Equal(expected, BashTimeKeyword.Format(format, real, user, sys));
}
