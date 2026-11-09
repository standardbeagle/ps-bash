using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>A bare or array assignment resets <c>$_</c>; <c>declare name=(…)</c> sets it to the name.</summary>
public class LastArgAssignmentTests
{
    [Fact]
    public Task Underscore_AfterAssignments() => AssertOracle.EqualAsync(
        ": foo\necho \"colon [$_]\"\ns=bar\necho \"bare assign [$_]\"\ndeclare s=bar\necho \"declare [$_]\"\na=(1 2)\necho \"array [$_]\"\ndeclare a=(1 2)\necho \"declare array [$_]\"\ndeclare -g d=(1 2)\necho \"declare flag [$_]\"");
}
