using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>A lone operator word in [[ ]] is a one-word string test; a failed failglob array literal leaves status 1.</summary>
public class DoubleBracketLoneWordTests
{
    [Fact]
    public Task LoneOperatorWord_IsTrue() => AssertOracle.EqualAsync("[[ = ]]\necho status=$?\n[[ == ]]\necho status=$?\n[[ ! = ]]\necho status=$?");

    [Fact]
    public Task Failglob_ArrayLiteral_StatusIsOne() => AssertOracle.EqualAsync("shopt -s failglob\n{ myarr=(*.ZZ); } 2>/dev/null\necho status=$?\na=(x y)\necho $?");
}
