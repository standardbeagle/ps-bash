using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>A tilde on the right of <c>[k]=v</c>: expanded in an indexed literal (also after a colon), left alone in an associative one (bash 5.2).</summary>
public class ArrayLiteralTildeTests
{
    [Fact]
    public Task IndexedElement_ExpandsTilde() => AssertOracle.EqualAsync(
        "HOME=/h; a=([2]=~ [4]=~:~:~ [5]=x~ [6]=\"~\" [7]=~/y); echo \"${a[2]}|${a[4]}|${a[5]}|${a[6]}|${a[7]}\"");

    [Fact]
    public Task AssocElement_KeepsTilde() => AssertOracle.EqualAsync(
        "HOME=/h; declare -A m=([k]=~/x [j]=~ [i]=~:~); echo \"${m[k]} ${m[j]} ${m[i]}\"");
}
