using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A syntax error on a later line is reported only after the complete commands above it ran. Each script runs
/// through a nested <c>bash -c</c> so its (differently worded) syntax-error message can be discarded.
/// </summary>
public class SyntaxErrorAfterPrefixTests
{
    [Fact]
    public Task CommandsAboveASyntaxError_RunFirst() => AssertOracle.EqualAsync(
        "bash -c $'echo one\\nx=5; echo x=$x\\n}\\necho never' 2>/dev/null; echo st=$?");

    [Fact]
    public Task ExitTrap_RunsAfterTheSyntaxError() => AssertOracle.EqualAsync(
        "bash -c $'trap \"echo bye\" EXIT\\necho one\\nfor i in 1 2\\necho never' 2>/dev/null; echo st=$?");

    [Fact]
    public Task TheWholeFailingLine_IsNotRun() => AssertOracle.EqualAsync(
        "bash -c $'echo one\\necho two; }\\necho never' 2>/dev/null; echo st=$?");

    [Fact]
    public Task AnEarlierExit_HidesTheSyntaxError() => AssertOracle.EqualAsync(
        "bash -c $'echo one\\nexit 7\\n}\\n' 2>/dev/null; echo st=$?");
}
