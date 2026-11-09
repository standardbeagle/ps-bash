using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary><c>${!prefix@}</c> lists variable names and includes declared arrays (even empty ones).</summary>
public class NamePrefixExpansionTests
{
    [Fact]
    public Task ScalarsAndEmptyArray_AreListed() => AssertOracle.EqualAsync(
        "hello1=1 hello2=2; echo ${!hello@}; hello=(); echo ${!hello@}");

    [Fact]
    public Task IndexedAndAssocArrays_AreListed() => AssertOracle.EqualAsync(
        "zq_a=(1 2); declare -A zq_m=([k]=v); zq_s=x; echo ${!zq_@}");
}
