using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>An unquoted variable glued to text (<c>a$x</c>, <c>$x.c</c>, <c>"q"$x</c>, <c>f=$x</c>) is field-split like a bare
/// <c>$x</c>, the text joined to the first and last field: with x='a b', <c>printf '[%s]' a$x</c> prints <c>[aa][b]</c>. Only
/// for-lists split it; command arguments passed <c>"aa b"</c> as one.</summary>
public class AffixWordSplitTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Theory]
    [InlineData("printf '[%s]' a$x", "-Prefix \"a\" -Suffix '' -Fields @($env:x)")]
    [InlineData("f $x.c", "-Prefix '' -Suffix \".c\" -Fields @($env:x)")]
    [InlineData("printf '[%s]' \"q\"$x", "-Prefix \"q\" -Suffix '' -Fields @($env:x)")]
    [InlineData("echo $y'x'", "-Prefix '' -Suffix \"x\" -Fields @($env:y)")]
    [InlineData("p=printf; $p '[%s]' f=$x", "-Prefix \"f=\" -Suffix '' -Fields @($env:x)")]
    public void Transpile_UnquotedVarGluedToText_SplitsTheValue(string bash, string fields)
        => Assert.Contains("ConvertTo-BashWords " + fields, T(bash));

    [Fact]
    public void Transpile_TmpPathGluedToVar_MapsTheTempDirectory()
    {
        var ps = T("cat /tmp/a$x");
        Assert.Contains("-Prefix (" + PsBuild.TempDirExpr + " + '/' + \"a\")", ps);
        Assert.Contains("-Fields @($env:x)", ps);
    }

    [Theory]
    [InlineData("ls *$x")]
    [InlineData("echo \"$x\"b")]
    [InlineData("echo a$x$y")]
    public void Transpile_GlobOrQuotedOrTwoExpansions_KeepsItsForm(string bash)
        => Assert.DoesNotContain("-Prefix", T(bash));

    // `${b}uiltin declare g=$x` (b=b): a command name glued to text is split and looked up at run time.
    [Theory]
    [InlineData("${b}uiltin declare g=$x")]
    [InlineData("${z}ommand readonly d=$x")]
    public void Transpile_AffixCommandName_ResolvesAtRunTime(string bash)
        => Assert.Contains("Resolve-BashCommand -Words @(ConvertTo-BashWords -Prefix", T(bash));

    [Fact]
    public void Transpile_PathCommandWordWithVar_KeepsItsPathForm()
        => Assert.DoesNotContain("Resolve-BashCommand", T("$dir/bin/tool x"));

    // `declare 'b'`: quote removal first, so the name is declared; it went to the print cmdlet (`declare -- b="1"`).
    [Theory]
    [InlineData("declare 'b'")]
    [InlineData("declare \"b\"")]
    [InlineData("declare 'f=a' 'b'")]
    public void Transpile_QuotedDeclareName_DeclaresWithoutPrinting(string bash)
        => Assert.DoesNotContain("Invoke-BashDeclare", T(bash));

    // `-"${a[@]}"`, `"p$@q"`: the text goes on the first and last element, each element whole.
    [Theory]
    [InlineData("printf '<%s>' -\"${a[@]}\"", "Affix(\"-\", ''")]
    [InlineData("printf '<%s>' \"p${a[@]}q\"", "Affix(\"p\", \"q\"")]
    [InlineData("printf '<%s>' k\"$@\"", "Affix(\"k\", ''")]
    [InlineData("for w in -\"${a[@]}\"; do :; done", "Affix(\"-\", ''")]
    public void Transpile_QuotedArrayWithText_DistributesOverTheElements(string bash, string affix)
        => Assert.Contains("[PsBash.Cmdlets.BashQuotedWords]::" + affix, T(bash));

    [Theory]
    [InlineData("printf '<%s>' \"${a[@]}\"")]
    [InlineData("printf '<%s>' \"${a[@]}${b[@]}\"")]
    [InlineData("printf '<%s>' x$y\"${a[@]}\"")]
    public void Transpile_QuotedArrayAloneOrBesideAnotherList_KeepsItsForm(string bash)
        => Assert.DoesNotContain("BashQuotedWords", T(bash));

    [Fact]
    public void Transpile_QuotedVarBesideQuotedArray_IsAffixText()
        => Assert.Contains("Affix(\"$env:x\", ''", T("printf '<%s>' \"$x${a[@]}\""));

    [Fact]
    public void Transpile_DeclareFedAQuotedArray_RunsThroughEval()
        => Assert.Contains("$__psbash_eval_src", T("typeset -\"${a[@]}\""));

    [Fact]
    public void Transpile_QuotedDeclarePrintFlag_StillPrints()
        => Assert.Contains("Invoke-BashDeclare", T("declare '-p' b"));
}
