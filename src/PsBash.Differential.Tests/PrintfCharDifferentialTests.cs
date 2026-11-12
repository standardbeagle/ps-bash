using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>printf conversions that disagreed with bash on edge input.</summary>
public class PrintfCharDifferentialTests
{
    /// <summary>
    /// <c>%c</c> writes the first BYTE of its argument. It wrote the first UTF-16 char (both bytes of "μ"), nothing
    /// for an empty or missing argument where bash writes NUL, and ignored the width. The output goes through
    /// <c>od</c> so the bytes are compared, not the terminal's rendering of them.
    /// </summary>
    [Theory]
    [InlineData("printf '[%c]' μ | od -An -tx1")]
    [InlineData("printf '[%2c][%-3c]' μ é | od -An -tx1")]
    [InlineData("printf '%c|' 65 '' x | od -An -c")]
    [InlineData("printf '[%c][%3c][%-3c]' | od -An -c")]
    [InlineData("printf '%s %c\\n' a | od -An -c")]
    // A 4-byte character: still one byte out.
    [InlineData("printf '%c' '😀x' | od -An -tx1")]
    public Task Char_WritesTheFirstByte(string script) => AssertOracle.EqualAsync(script);

    /// <summary>
    /// %u %o %x %X read the argument as unsigned 64-bit (strtoumax) and %d %i as signed: out of range clamps with a
    /// WARNING and status 0. %u stopped at 2^63-1, and an overflow was an error with status 1. bash's stderr lines
    /// carry a <c>bash: line N:</c> prefix ps-bash does not write (a known gap), so the rows drop stderr and the last
    /// compares the message from <c>printf:</c> on.
    /// </summary>
    [Theory]
    [InlineData("for v in 18446744073709551615 18446744073709551616 -18446744073709551615 -18446744073709551616 -1; do printf '%u\\n' $v; echo \"s=$?\"; done 2>/dev/null")]
    [InlineData("for v in 9223372036854775808 18446744073709551616 -9223372036854775809 -9223372036854775808; do printf '%d\\n' $v; echo \"s=$?\"; done 2>/dev/null")]
    [InlineData("{ printf '%x %X %o\\n' 18446744073709551615 -1 -1; echo \"s=$?\"; printf '%x\\n' 18446744073709551616; echo \"s=$?\"; } 2>/dev/null")]
    // Trailing junk is still an error (status 1), out of range or not, and it is the only message.
    [InlineData("{ printf '%d\\n' 12abc; echo \"s=$?\"; printf '%u\\n' 99999999999999999999x; echo \"s=$?\"; } 2>/dev/null")]
    [InlineData("for v in 18446744073709551616 99999999999999999999x; do printf '%u' $v 2>&1 >/dev/null | sed 's/.*printf: //'; done")]
    public Task Integer_OutOfRangeClampsWithAWarning(string script) => AssertOracle.EqualAsync(script);

    /// <summary><c>%(fmt)T</c> ignored its width and precision; the date text is formatted as a %s.</summary>
    [Fact]
    public Task Time_HonoursWidthAndPrecision() =>
        AssertOracle.EqualAsync("printf '[%10.5(%Y-%m-%d)T][%-12(%Y)T][%.2(%Y)T]\\n' 302400 302400 302400");
}
