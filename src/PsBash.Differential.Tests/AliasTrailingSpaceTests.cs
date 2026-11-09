using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>An alias value ending in a blank makes the next word an alias candidate, even when its value is an operator-like word such as <c>!!!</c>.</summary>
public class AliasTrailingSpaceTests
{
    [Fact]
    public Task TrailingBlank_ExpandsNextWord_OperatorLikeValue() => AssertOracle.EqualAsync(
        "shopt -s expand_aliases\nalias hi='echo hello world '\nalias punct='!!!'\nhi punct\nalias hi='echo hello world'\nhi punct");
}
