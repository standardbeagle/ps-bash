using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>Bare `${a[i]}` is word-split: an unset or empty element is no argument. Oracle (bash 5.2): `echo x ${a[7]} y` prints `x y`.</summary>
public class UnquotedElementWordTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Fact] public void Transpile_UnquotedElement_IsWordSplit() => Assert.Contains("ConvertTo-BashWords", T("echo x ${a[7]} y"));
    [Fact] public void Transpile_UnquotedNegativeElement_IsWordSplit() => Assert.Contains("ConvertTo-BashWords", T("echo ${a[-1]}"));
    [Fact] public void Transpile_QuotedElement_IsNotSplit() => Assert.DoesNotContain("ConvertTo-BashWords", T("echo \"${a[7]}\""));
    [Fact] public void Transpile_AllElements_KeepsItsOwnPath() => Assert.DoesNotContain("ConvertTo-BashWords ${a[0]}", T("echo ${a[@]}"));
}
