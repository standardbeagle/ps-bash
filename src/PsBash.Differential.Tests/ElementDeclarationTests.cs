using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary><c>declare b[2]=y</c> / <c>local c[1]=z</c>: an element initializer declares the array and assigns the
/// element. The word was globbed and handed to declare, which reported "b[2]=y: not found".</summary>
public class ElementDeclarationTests
{
    [Fact]
    public Task DeclareElementInitializers() => AssertOracle.EqualAsync(
        "declare b[2]=y; declare -p b\ndeclare a[a[0]=1]=X; declare -p a\n"
        + "declare a[ a[2]=3 ]=Y 2>/dev/null; echo \"st:$?\"; declare -p a\n"
        + "declare 1x e[1]=2 2>/dev/null; echo \"st:$?\"; declare -p e\n"
        + "declare -a n[1]+=p r; declare -p n r\ntypeset t[1]=*; declare -p t\n"
        + "v='w x'; declare g[0]=\"$v\"; declare -p g\n"
        + "declare -A m; declare m[k]=v m[j]+=w; declare -p m\n"
        + "declare -r ro[0]=1 2>/dev/null; echo \"st:$?\"\n"
        + "f() { local c[1]=z; declare d[0]=q; declare -p c d; }; f; declare -p c d 2>&1 | wc -l");
}
