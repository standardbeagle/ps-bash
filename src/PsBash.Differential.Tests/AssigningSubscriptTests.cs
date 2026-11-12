using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>An element assignment whose subscript assigns (`a[a[0]=1]=X`, `b[i++]=p`) keeps the subscript's write; it
/// was lost (the array was copied before the subscript ran).</summary>
public class AssigningSubscriptTests
{
    [Fact]
    public Task AssigningSubscript_KeepsItsWrite() => AssertOracle.EqualAsync(
        "a[a[0]=1]=X\ndeclare -p a\na[ a[2]=3 ]=Y\ndeclare -p a\na[ a[0]+=1 ]+=X\ndeclare -p a\n"
        + "i=0; b[i++]=p; b[i++]=q && echo i=$i; declare -p b\n"
        + "declare -A m; k=x; m[k=1]=v; declare -p m; echo k=$k\n"
        + "f() { local -a c; c[c[0]=5]=Z; declare -p c; }; f\n"
        + "true && d[d[1]=1]=w || echo no; declare -p d");
}
