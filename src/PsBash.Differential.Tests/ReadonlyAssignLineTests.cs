using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>A failed assignment to a readonly variable drops the rest of its input line.</summary>
public class ReadonlyAssignLineTests
{
    [Fact]
    public Task Readonly_SameLine_RestOfLineDropped() => AssertOracle.EqualAsync(
        "( readonly x=1; x=2; echo hi ) 2>/dev/null\necho next");

    [Fact]
    public Task Readonly_NextLine_Continues() => AssertOracle.EqualAsync(
        "readonly x=1\n{ x=2; } 2>/dev/null\necho hi");
}
