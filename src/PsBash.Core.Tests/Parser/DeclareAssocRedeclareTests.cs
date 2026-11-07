using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

public class DeclareAssocRedeclareTests
{
    [Fact] public void Transpile_BareDeclareAssoc_KeepsExistingHashtable()
    {
        var ps = PsEmitter.Transpile("declare -A d")!;
        Assert.Contains("-isnot", ps);
        Assert.Contains("@{}", ps);
    }
}
