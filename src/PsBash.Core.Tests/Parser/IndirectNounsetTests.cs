using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>bash: <c>r=nope; set -u; echo ${!r}; echo end</c> stops at the expansion ("!r: unbound variable", status 1)
/// like a direct read of an unset name. ps-bash expanded it to nothing and went on.</summary>
public class IndirectNounsetTests
{
    [Theory]
    [InlineData("r=nope; set -u; echo ${!r}")]
    [InlineData("r=nope; set -u; echo \"${!r}\"")]
    public void Transpile_IndirectReadOfUnsetUnderNounset_EndsTheScript(string bash)
    {
        var ps = PsEmitter.Transpile(bash)!;
        Assert.Contains("'!r: '", ps);
        Assert.Contains("'unbound variable'", ps);
    }

    [Theory]
    [InlineData("r=nope; echo ${!r}")]
    [InlineData("r=nope; set -u; echo ${!r-d}")]
    public void Transpile_IndirectReadWithoutNounsetOrWithDefault_HasNoUnboundCheck(string bash)
        => Assert.DoesNotContain("'!r: '", PsEmitter.Transpile(bash)!);
}
