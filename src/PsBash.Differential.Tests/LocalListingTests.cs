using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary><c>local</c>, <c>local -p</c> and <c>local -p NAME</c> list the function's locals; namerefs list through declare.</summary>
public class LocalListingTests
{
    [Fact]
    public Task Local_Listing_PrintsFunctionLocals() => AssertOracle.EqualAsync(
        "f1() { local a=1 b=2; local -p; echo ---; local -p a; echo ---; local; echo ---; declare -p a; }; f1");

    [Fact]
    public Task Declare_Nameref_IsListed() => AssertOracle.EqualAsync(
        "v1=x; declare -n n1=v1; declare -p n1; declare -pn");
}
