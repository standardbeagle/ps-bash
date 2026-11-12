using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// A subscript is a word: it may hold quotes, nested `${…}` and nested `[…]` (`m["${a[@]}"]=v`, `${a[a[1]]}`), and its
/// expansions are expanded before the lookup (`${m["$k b"]}`, `${a[$i+1]}`). Oracle: bash 5.2.
/// </summary>
public class ArraySubscriptWordTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Theory]
    [InlineData("[1]", 2)]
    [InlineData("[a[1]]x", 5)]
    [InlineData("[\"${a[@]}\"]", 10)]
    [InlineData("[\"]\"]", 4)]
    [InlineData("['$k']=", 5)]
    [InlineData("[$(echo ])]", 10)]
    [InlineData("[open", -1)]
    public void FindSubscriptClose_SkipsQuotesAndNesting(string s, int expected)
        => Assert.Equal(expected, BashParser.FindSubscriptClose(s, 0));

    [Theory]
    [InlineData("m[\"${a[@]}\"]=foo")]
    [InlineData("m['x]y']=v")]
    [InlineData("a[a[0]]=1")]
    public void Tokenize_QuotedOrNestedSubscript_IsAssignmentWord(string word)
        => Assert.Equal(BashTokenKind.AssignmentWord, BashLexer.Tokenize(word)[0].Kind);

    [Fact] public void Transpile_QuotedSubscriptWithNestedArray_IsOneSubscript()
    {
        var ps = T("echo \"${m[\"${a[@]}\"]}\"");
        Assert.DoesNotContain("]}", ps.Replace("$a['0']", ""), StringComparison.Ordinal);
        Assert.Contains("$a.Values", ps);
    }

    [Theory]
    [InlineData("echo ${m[\"$k b\"]}", "$env:k b")]
    [InlineData("echo ${a[$i+1]}", "Invoke-BashArith \"$env:i+1\"")]
    [InlineData("echo ${m[${k}x]}", "${env:k}x")]
    public void Transpile_SubscriptExpansions_AreExpanded(string bash, string expected)
        => Assert.Contains(expected, T(bash));

    // `a=(x y); a=z`: a scalar write to an array writes element 0.
    [Fact] public void Transpile_ScalarWriteToArray_WritesElementZero()
        => Assert.Contains("Set-BashElement $a '0'", T("a=(x y); a=z"));

    [Fact] public void Transpile_ScalarWriteToUnknownName_StaysScalar()
        => Assert.DoesNotContain("Set-BashElement", T("a=z"));

    // `c=s; c[1]=t`: the scalar becomes element 0 before the element write.
    [Fact] public void Transpile_ElementWrite_SeedsFromTheScalar()
        => Assert.Contains(PsBuild.SeedArrayFromScalar("$c", "$env:c"), T("c=s; c[1]=t"));

    // `declare -A m; m[0]=x; echo $m`: a bare `$m` reads the element keyed 0.
    [Fact] public void Transpile_BareNameOfDeclaredAssoc_ReadsElementZero()
        => Assert.Contains("$m['0']", T("declare -A m; echo $m"));
}
