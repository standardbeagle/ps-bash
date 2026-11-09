using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>`trap N SIG...` with an unsigned integer first argument resets every listed signal; an option-like first argument is refused.</summary>
public class TrapResetTests
{
    [Fact]
    public Task Trap_UnsignedIntegerFirst_ResetsAllListed() => AssertOracle.EqualAsync(
        "trap 'echo EXIT' EXIT\ntrap 0 2\ntrap\necho ---\ntrap 'echo INT' INT\ntrap 'echo EXIT' EXIT\ntrap 2 EXIT\ntrap\necho ---\ntrap 'echo noprint' EXIT\ntrap ' 42 ' EXIT\ntrap 'echo np2' EXIT\ntrap 0 EXIT\necho ok0");

    [Fact]
    public Task Trap_OptionLikeFirstArg_IsStatus2() => AssertOracle.EqualAsync(
        "trap 'echo kept' EXIT\n{ trap -1 EXIT; } 2>/dev/null\necho st=$?");
}
