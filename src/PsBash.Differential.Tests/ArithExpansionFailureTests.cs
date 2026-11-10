using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>A failed arithmetic WORD abandons its command (status 1); a failed <c>(( ))</c> command only fails.</summary>
public class ArithExpansionFailureTests
{
    [Fact]
    public Task FailedArithWord_AbandonsEcho() => AssertOracle.EqualAsync(
        "{ echo $((1 + 2.3)); } 2>/dev/null\necho status=$?\necho $((1+2)) ok");

    [Fact]
    public Task FailedArithWord_InsideQuotes_AbandonsEcho() => AssertOracle.EqualAsync(
        "{ echo \"q$((1/0))\"; } 2>/dev/null\necho status=$?\necho next");

    [Fact]
    public Task FailedArithAssignment_KeepsStatus_AndLaterEchoRuns() => AssertOracle.EqualAsync(
        "{ x=$((1/0)); } 2>/dev/null\necho status=$?\necho hi");

    [Fact]
    public Task FailedArithCommand_DoesNotAbandonTheNextCommand() => AssertOracle.EqualAsync(
        "{ (( 1[2] = 3 )); } 2>/dev/null\necho status=$?\na=9\n{ (( (a + 2) = 3 )); } 2>/dev/null\necho $a\n"
        + "{ if (( 1 / 0 )); then echo yes; else echo no; fi; } 2>/dev/null\necho end");
}
