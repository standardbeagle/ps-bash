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

    // `__ll_`, not a bare `^__`: macOS puts __CF_USER_TEXT_ENCODING in every process's environment, so the
    // listing matched an OS variable bash's sanitized oracle environment does not have. Still a `__` name,
    // which is the case this pins (user names starting `__` are listed, not filtered as ps-bash internals).
    [Fact]
    public Task Set_Listing_ShowsIndexedArraysAndQuotesAssocKeys() => AssertOracle.EqualAsync(
        "declare -a __ll_arr; __ll_arr=(1 2 '3 4'); declare -A __ll_as; __ll_as['k e y']=v; set | grep '^__ll_'");
}
