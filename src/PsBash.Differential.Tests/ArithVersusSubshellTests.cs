using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>`((` is an arithmetic command only when an adjacent `))` closes it; otherwise it is nested subshells.</summary>
public class ArithVersusSubshellTests
{
    [Fact]
    public Task DoubleParen_ClosedBySpacedParens_IsSubshell() => AssertOracle.EqualAsync(
        "(( echo 1\necho 2\n(( x ))\n: $(( x ))\necho 3\n) )");

    [Fact]
    public Task TripleParen_Pipeline_IsNestedSubshells() => AssertOracle.EqualAsync(
        "(((echo a; echo b) ; (echo c)) | cat)");

    [Fact]
    public Task DoubleParen_Arithmetic_StillWorks() => AssertOracle.EqualAsync(
        "(( 1+2 )); echo $?\n(( 0 )); echo $?\nx=$(( 2*3 )); echo $x");
}
