using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary><c>${!a}</c> whose target is not a variable name abandons its command with status 1.</summary>
public class BadIndirectRefTests
{
    [Fact]
    public Task BadIndirect_NameWithSpaces_AbandonsCommand() => AssertOracle.EqualAsync(
        "a='bad var name'\n{ echo ref ${!a}; } 2>/dev/null\necho status=$?");

    [Fact]
    public Task BadIndirect_Slash_AbandonsCommand() => AssertOracle.EqualAsync(
        "b='/'\n{ echo ref ${!b}; } 2>/dev/null\necho status=$?");

    [Fact]
    public Task GoodIndirect_StillExpands_AfterBadOne() => AssertOracle.EqualAsync(
        "a='/'; x=ok; r=x\n{ echo ref ${!a}; } 2>/dev/null\necho ${!r} status=$?");
}
