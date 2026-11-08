using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

public class IndexedArrayLiteralEmitterTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Fact] public void Transpile_NumericSubscriptLiteral_BuildsFromEntriesNotLiteralWords()
    {
        var ps = T("a=([2]=p [5]=q)");
        Assert.Contains("BashSparseArray]::FromEntries('2', (", ps);
        Assert.DoesNotContain("[2]=p", ps);
    }

    [Fact] public void Transpile_PlainWordList_IsUnchanged() => Assert.DoesNotContain("FromEntries", T("a=(x y z)"));

    [Fact] public void Transpile_AssocLiteralWithNumericKey_StaysAssoc()
    {
        var ps = T("declare -A m=([1]=one [x]=ex)");
        Assert.Contains("BashAssoc", ps);
        Assert.DoesNotContain("FromEntries", ps);
    }
}
