using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>A pipe into a shell function carries the exact bytes: no newline is added to an unterminated last line.</summary>
public class PipeIntoFunctionBytesTests
{
    [Theory]
    [InlineData("show() { od -A n -t x1; }; printf 'ab' | show")]
    [InlineData("show() { od -A n -t x1; }; echo -n xy | show")]
    [InlineData("show() { od -A n -t x1; }; printf 'a\\nb' | show")]
    [InlineData("show() { od -A n -t x1; }; printf 'a\\nb\\n' | show")]
    [InlineData("f() { cat; }; printf 'ab' | f | od -A n -t x1")]
    [InlineData("f() { wc -c; }; printf 'ab' | f")]
    [InlineData("f() { tr a-z A-Z; }; printf 'ab' | f | od -A n -c")]
    public Task PipeIntoFunction_KeepsBytes(string script) => AssertOracle.EqualAsync(script);

    /// <summary>`egrep` / `fgrep` in a function read the pipe it is called with; on Windows they read nothing.</summary>
    [Fact]
    public Task EgrepAndFgrepInAFunction_ReadThePipe() => AssertOracle.EqualAsync(
        "only() { egrep -o 'b+|can only'; return 0; }; printf 'abbc\\nreturn: can only x\\n' | only; echo s=$?\n"
        + "count() { fgrep -c 'a.'; }; printf 'a.\\nab\\na.\\n' | count\n"
        + "only < /dev/null; echo n=$?");

    // $'\cX' is X masked to a control character (bash TOCTRL), and \c? is DEL.
    [Fact]
    public Task AnsiCQuote_ControlEscapes() => AssertOracle.EqualAsync(
        "echo -n $'\\c0\\c9-\\ca\\cz\\cA\\cZ\\cm\\ck\\cb\\c?' | od -A n -t x1");
}
