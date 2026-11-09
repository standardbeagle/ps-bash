using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary><c>${##}</c> is the length of <c>$#</c>; <c>${###}</c> / <c>${##2}</c> / <c>${###2}</c> strip a prefix from <c>$#</c>.</summary>
public class HashParameterOperatorTests
{
    [Fact]
    public Task LengthOfArgCount() => AssertOracle.EqualAsync("set -- $(seq 25); echo ${##}; set -- a; echo ${##}");

    [Fact]
    public Task PrefixStripOfArgCount() => AssertOracle.EqualAsync(
        "set -- $(seq 25); echo ${###} ${####} ${##2} ${###2} ${#}");
}
