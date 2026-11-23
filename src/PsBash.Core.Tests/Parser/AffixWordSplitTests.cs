using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>An unquoted variable glued to text (<c>a$x</c>, <c>$x.c</c>, <c>"q"$x</c>, <c>f=$x</c>) is field-split like a bare
/// <c>$x</c>, the text joined to the first and last field: with x='a b', <c>printf '[%s]' a$x</c> prints <c>[aa][b]</c>. Only
/// for-lists split it; command arguments passed <c>"aa b"</c> as one. Every such word is one <c>ConvertTo-BashWords -Parts</c>
/// call: kind 1 quoted text, 2 pattern text, 3 a split value, 5 a quoted list (PsBash.Cmdlets.BashWordFields).</summary>
public class AffixWordSplitTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Theory]
    [InlineData("printf '[%s]' a$x", "-Parts @(2, 'a', 3, ($env:x))")]
    [InlineData("f $x.c", "-Parts @(3, ($env:x), 2, '.c')")]
    [InlineData("printf '[%s]' \"q\"$x", ", 3, ($env:x))")]
    [InlineData("echo $y'x'", "-Parts @(3, ($env:y), 1, ")]
    [InlineData("p=printf; $p '[%s]' f=$x", "-Parts @(2, 'f=', 3, ($env:x))")]
    public void Transpile_UnquotedVarGluedToText_SplitsTheValue(string bash, string parts)
        => Assert.Contains(parts, T(bash));

    // `""$A` / `$A""`: a quoted affix anchors a field of its own (bash's quoted null): it is a quoted (kind 1) segment.
    [Theory]
    [InlineData("for i in \"\"$A; do :; done", "-Parts @(1, ")]
    [InlineData("for i in $A\"\"; do :; done", "-Parts @(3, ($env:A), 1, ")]
    public void Transpile_QuotedAffix_IsAQuotedSegment(string bash, string parts)
        => Assert.Contains(parts, T(bash));

    [Fact]
    public void Transpile_TmpPathGluedToVar_MapsTheTempDirectory()
        => Assert.Contains("-Parts @(1, (" + PsBuild.TempDirExpr + "), 2, '/a', 3, ($env:x))", T("cat /tmp/a$x"));

    // bash splits every unquoted expansion of a word, beside a glob or another expansion too (x='a b': `$x*` is a and
    // b*, `a$x$x` is aa, ba, b). Both kept one field before.
    [Theory]
    [InlineData("ls *$x", "-Parts @(2, '*', 3, ($env:x))")]
    [InlineData("echo a$x$y", "-Parts @(2, 'a', 3, ($env:x), 3, ($env:y))")]
    public void Transpile_GlobOrTwoExpansions_SplitsEveryExpansion(string bash, string parts)
        => Assert.Contains(parts, T(bash));

    [Fact]
    public void Transpile_QuotedVarWithText_IsOneWord()
        => Assert.DoesNotContain("ConvertTo-BashWords", T("echo \"$x\"b"));

    // `${b}uiltin declare g=$x` (b=b): a command name glued to text is split and looked up at run time.
    [Theory]
    [InlineData("${b}uiltin declare g=$x")]
    [InlineData("${z}ommand readonly d=$x")]
    public void Transpile_AffixCommandName_ResolvesAtRunTime(string bash)
        => Assert.Contains("Resolve-BashCommand -Words @(ConvertTo-BashWords -Parts", T(bash));

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

    // `-"${a[@]}"`, `"p$@q"`: the text goes on the first and last element, each element whole (a quoted list, kind 5).
    [Theory]
    [InlineData("printf '<%s>' -\"${a[@]}\"", "-Parts @(2, '-', 5, (@(")]
    [InlineData("printf '<%s>' \"p${a[@]}q\"", "-Parts @(1, ")]
    [InlineData("printf '<%s>' k\"$@\"", "-Parts @(2, 'k', 5, (@(")]
    [InlineData("for w in -\"${a[@]}\"; do :; done", "-Parts @(2, '-', 5, (@(")]
    public void Transpile_QuotedArrayWithText_DistributesOverTheElements(string bash, string parts)
        => Assert.Contains(parts, T(bash));

    [Fact]
    public void Transpile_QuotedArrayAlone_IsTheElementArray()
        => Assert.DoesNotContain("ConvertTo-BashWords", T("printf '<%s>' \"${a[@]}\""));

    // `"${a[@]}${b[@]}"`: the last element of a joins the first of b; `x$y"${a[@]}"`: $y is split, the list glued on.
    [Theory]
    [InlineData("printf '<%s>' \"${a[@]}${b[@]}\"", "-Parts @(5, (@(")]
    [InlineData("printf '<%s>' x$y\"${a[@]}\"", "-Parts @(2, 'x', 3, ($env:y), 5, (@(")]
    public void Transpile_QuotedArrayBesideAnotherExpansion_IsOneWordOfSegments(string bash, string parts)
        => Assert.Contains(parts, T(bash));

    [Fact]
    public void Transpile_QuotedVarBesideQuotedArray_IsQuotedText()
        => Assert.Contains("-Parts @(1, (\"$env:x\"), 5, (@(", T("printf '<%s>' \"$x${a[@]}\""));

    [Fact]
    public void Transpile_DeclareFedAQuotedArray_RunsThroughEval()
        => Assert.Contains("$__psbash_eval_src", T("typeset -\"${a[@]}\""));

    // `declare k=1 pwn`: a name holding the letter n was taken for a flag, so the two operands were never split and
    // `pwn` overwrote the declaration (k stayed empty). Reached `builtin declare k=$n` with n='a;echo pwn' via eval.
    [Theory]
    [InlineData("declare k=1 pwn", "$env:k = '1'")]
    [InlineData("declare k=1 pwn", "MarkAttribute('pwn'")]
    [InlineData("typeset nn=2 zz", "$env:nn = '2'")]
    public void Transpile_DeclareNameContainingN_IsANameNotAFlag(string bash, string expected)
        => Assert.Contains(expected, T(bash));

    // `readonly r=1; declare r=2`: refused with the builtin's name, value kept; the rest of the line still runs.
    [Theory]
    [InlineData("readonly r=1; declare r=2; echo after", "declare: r: readonly variable")]
    [InlineData("readonly r=1; typeset +r r=2; echo after", "typeset: r: readonly variable")]
    public void Transpile_DeclareOfReadonlyName_IsRefusedAndTheLineGoesOn(string bash, string error)
    {
        var ps = T(bash);
        Assert.Contains(error, ps);
        Assert.DoesNotContain("$env:r = 'r2'", ps);
        Assert.DoesNotContain("$env:r = '2'", ps);
        Assert.Contains("Invoke-BashEcho after", ps);
    }

    [Fact]
    public void Transpile_DeclarePlusN_StillDropsTheNameref()
        => Assert.Contains(PsBuild.ClearNameref("r"), T("r=x; typeset -n r; typeset +n r"));

    // `local +n lr` in a function ran a command named local (which printed a declare line): it is `typeset +n`.
    [Fact]
    public void Transpile_LocalPlusN_InAFunction_ClearsTheNameref()
        => Assert.Contains(PsBuild.ClearNameref("lr"), T("f() { local -n lr=x; local +n lr; }"));

    [Fact]
    public void Transpile_QuotedDeclarePrintFlag_StillPrints()
        => Assert.Contains("Invoke-BashDeclare", T("declare '-p' b"));

    // bash: in double quotes every `*` form joins its values with IFS's first character (IFS=-: `"${*:2}"` is b-c,
    // `"${a[*]:1}"` b-c, `"${a[*]:-d}"` a-b-c); these joined with a space. Behavior: WordFieldsDifferentialTests.
    [Theory]
    [InlineData("echo \"${*}\"")]
    [InlineData("echo \"${*:2}\"")]
    [InlineData("echo \"${a[*]:1}\"")]
    [InlineData("echo \"${a[*]:-d}\"")]
    public void Transpile_QuotedStarForm_JoinsWithTheFirstIfsCharacter(string bash)
        => Assert.Contains(PsBuild.IfsJoinSeparator, T(bash));
}
