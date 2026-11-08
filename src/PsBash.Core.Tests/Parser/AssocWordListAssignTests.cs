using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>bash 5.1+: `declare -A a; a=(k v k2 v2)` takes bare words as key/value pairs; an indexed array stays a list.</summary>
public class AssocWordListAssignTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Fact] public void Transpile_BareWordsAssignedToDeclaredAssoc_AreKeyValuePairs()
        => Assert.Contains("BashAssoc]::FromWords(@(", T("declare -A a; a=(k v)"));

    [Fact] public void Transpile_BareWordsAssignedToIndexedArray_StayAList()
        => Assert.DoesNotContain("FromWords", T("a=(k v)"));

    [Fact] public void Transpile_AssocDeclaredInOneScript_DoesNotLeakIntoTheNext()
    {
        T("declare -A a");
        Assert.DoesNotContain("FromWords", T("a=(k v)"));
    }

    [Fact] public void Transpile_AssocLiteral_IsBuiltInBashOrderNotAHashtableLiteral()
    {
        var ps = T("declare -A m=([x]=1 [y]=2)");
        Assert.Contains("BashAssoc]::FromPairs(", ps);
        Assert.DoesNotContain("@{", ps);
    }

    [Fact] public void Transpile_AssocLiteralAppend_MergesIntoTheExistingTable()
        => Assert.Contains("BashAssoc]::Merge(", T("declare -A a; a+=([k]=v)"));
}