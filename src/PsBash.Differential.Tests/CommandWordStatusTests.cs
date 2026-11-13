using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>Exit status of odd command words and of `return N` outside 0..255.</summary>
public class CommandWordStatusTests
{
    [Fact] public Task Return_256_Wraps() => AssertOracle.EqualAsync("f() { return 256; }; f; echo s=$?");
    [Fact] public Task Return_257_Wraps() => AssertOracle.EqualAsync("f() { return 257; }; f; echo s=$?");
    [Fact] public Task Return_Negative_Wraps() => AssertOracle.EqualAsync("f() { return -1; }; f; echo s=$?");
    [Fact] public Task EmptyQuotedCommand_Is127() => AssertOracle.EqualAsync("{ ''; } 2>/dev/null; echo s=$?");
    [Fact] public Task EmptyQuotedCommand_InIf_IsFalse() => AssertOracle.EqualAsync("if ''; then echo T; else echo F; fi 2>/dev/null");
    [Fact] public Task CommandSubOnly_True() => AssertOracle.EqualAsync("$(true); echo s=$?");
    [Fact] public Task CommandSubOnly_False() => AssertOracle.EqualAsync("$(false); echo s=$?");
    [Fact] public Task BraceGroup_MissingInput_FailsWithoutRunningBody() => AssertOracle.EqualAsync("{ { echo body; } < /nonexistent/in.txt; } 2>/dev/null; echo s=$?");
    [Fact] public Task Subshell_MissingInput_FailsWithoutRunningBody() => AssertOracle.EqualAsync("{ ( echo body ) < /nonexistent/in.txt; } 2>/dev/null; echo s=$?");
    [Fact] public Task If_MissingInput_FailsWithoutRunningBody() => AssertOracle.EqualAsync("{ if true; then echo body; fi < /nonexistent/in.txt; } 2>/dev/null; echo s=$?");
    [Fact] public Task Time_Builtin_KeepsStatus() => AssertOracle.EqualAsync("{ time false; } 2>/dev/null; echo s=$?");
    [Fact] public Task CommandSubOnly_Exit42() => AssertOracle.EqualAsync("$(exit 42); echo s=$?");

    /// <summary>`return` outside a function is refused with status 2 and the script goes on; it ended the script.</summary>
    [Fact]
    public Task TopLevelReturn_IsRefused_AndTheScriptGoesOn() => AssertOracle.EqualAsync(
        "{ return 7; } 2>/dev/null; echo top=$?\n"
        + "( return 5; echo in=$? ) 2>/dev/null; echo sub=$?\n"
        + "f() { ( return 4 ); echo f=$?; }; f");

    /// <summary>`exit N` keeps the low 8 bits of N: a Windows process status is 32 bits, so 257 stayed 257.</summary>
    [Fact]
    public Task Exit_OutOfRange_KeepsTheLowEightBits() => AssertOracle.EqualAsync(
        "( exit 257 ); echo a=$?; ( exit -2 ); echo b=$?\n"
        + "v=258; ( exit $v ); echo c=$?; x=$(exit 300); echo e=$?\n"
        + "bash -c 'exit 256'; echo d=$?");

    /// <summary>
    /// A command substitution as the command word, with operands: its words split, an empty one leaves the next operand
    /// the command, and with nothing left the status is the last substitution's (`$(exit 42) $(exit 43)` is 43).
    /// PowerShell failed ("The expression after '&amp;' ... was not valid") on an empty one.
    /// </summary>
    [Fact]
    public Task CommandSubCommandWord_WithOperands() => AssertOracle.EqualAsync(
        "if `true` X; then echo T; else echo F; fi 2>/dev/null\n"
        + "$(exit 42) $(exit 43); echo a=$?\n"
        + "false; $nothing $(exit 3); echo b=$?\n"
        + "$(true) echo moved; $(echo printf '%s-') x y; echo");
}
