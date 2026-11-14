using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>A failed arithmetic WORD abandons its command (status 1); a failed <c>(( ))</c> command only fails.</summary>
public class ArithExpansionFailureTests
{
    /// <summary>A variable whose value is not a valid expression is an error, not 0 (x='1 3'); a leading 0 is octal.</summary>
    [Fact]
    public Task InvalidValueOfAVariable_IsAnError_AndLeadingZeroIsOctal() => AssertOracle.EqualAsync(
        "x='1 3'\n{ echo $((x)); } 2>/dev/null\necho st=$?\n{ (( x )); } 2>/dev/null; echo st=$?\n"
        + "s='12 34'\n{ echo $(( s[0] )); } 2>/dev/null\necho st=$?\na=('1 3' 5); b=('1 3' 5)\n{ (( a == b )); } 2>/dev/null; echo st=$?\n"
        + "o=010; echo $((o + 1)); e=08\n{ echo $((e)); } 2>/dev/null\necho st=$?; n=' -7 '; echo $((n * 2)); z=''; w=' '; echo $((z)) $((w + 1))");

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
    // The mark a failed word leaves must die with its own command: a later echo still runs.
    [Fact]
    public Task FailedArithWord_OfAnotherCommand_DoesNotAbandonALaterEcho() => AssertOracle.EqualAsync(
        "{ true $((1/0)); } 2>/dev/null\necho after-true\n{ : $((1/0)); } 2>/dev/null\necho after-colon");

    [Fact]
    public Task FailedArithWord_InARedirectTarget_AbandonsTheCommand() => AssertOracle.EqualAsync(
        "cd \"$(mktemp -d)\"\n{ echo hi > zz$(( 42 / 0 )) in; } 2>/dev/null\necho inside=$?\nls | wc -l");

    /// <summary>
    /// The command has a file redirect, which runs as a second pipeline element that begins first. Before, the redirect
    /// cleared the failure mark the command's word had set, so echo printed <c>q[]</c> with status 0.
    /// </summary>
    [Fact]
    public Task FailedArithWord_OfACommandWithAFileRedirect_AbandonsIt() => AssertOracle.EqualAsync(
        "cd \"$(mktemp -d)\"\n{ echo \"q[$((1/0))]\" 2>>err.txt; } 2>/dev/null\necho st=$?\necho ok 2>>err.txt\necho st=$?");
}
