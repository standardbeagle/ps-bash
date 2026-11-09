using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary><c>${!ref@a}</c> gives the attribute letters of the variable NAMED by $ref.</summary>
public class IndirectAttributeTests
{
    [Fact]
    public Task IndirectAttributes_OfArrayAssocReadonlyExportAndPlain() => AssertOracle.EqualAsync(
        "array=(one two); declare -A m=([k]=v); declare -r r=1; declare -x e=1; s=x\nf() { echo \"[${!1@a}]\"; }\nf array; f m; f r; f e; f s; f undef");
}
