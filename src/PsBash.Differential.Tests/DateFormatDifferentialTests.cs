using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle for date's sub-second format and input. Fixed epochs + <c>-u</c> keep the
/// output deterministic.
///
/// Covered regressions:
///   - <c>%N</c> / <c>%3N</c> printed literally (<c>date +%s%N</c>, the common ms-timestamp idiom)
///   - a fractional <c>@SECS.FRAC</c> input dropped its fraction
/// </summary>
public class DateFormatDifferentialTests
{
    private static Task Eq(string script) =>
        AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(30));

    [SkippableFact]
    public Task Date_Nanoseconds_FullAndWidthForms()
        // 7 fraction digits: .NET time is 100 ns ticks (intentional-differences.md).
        => Eq("date -d @0.1234567 -u +%N; date -d @1.25 -u +%T.%3N; date -d @0 -u +%N; date -d @1,5 -u +%6N");

    [SkippableFact]
    public Task Date_NegativeFractionalEpoch_FloorsLikeGnu()
        => Eq("date -d @-1.5 -u +%s.%N");

    [SkippableFact]
    public Task Date_EpochNanos_HasNineteenDigits()
        => Eq("date +%s%N | wc -c");
}
