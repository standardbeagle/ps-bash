using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>An unquoted variable glued to text (<c>a$x</c>, <c>$x.c</c>, <c>"q"$x</c>, <c>f=$x</c>) is field-split like a bare
/// <c>$x</c>, the text joined to the first and last field: with x='a b', <c>printf '[%s]' a$x</c> prints <c>[aa][b]</c>. Only
/// for-lists split it; command arguments passed <c>"aa b"</c> as one. Every such word is one list of segments
/// (<c>$__psbash_wp</c>, PsBuild.WordFieldsArray): kind 1 quoted text, 2 pattern text, 3 a split value, 5 a quoted list,
/// 7 quoted text with no anchor (PsBash.Cmdlets.BashWordFields).</summary>
public class AffixWordSplitTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Theory]
    [InlineData("printf '[%s]' a$x", "$__psbash_wp = @(2, 'a', 3, ($env:x))")]
    [InlineData("f $x.c", "$__psbash_wp = @(3, ($env:x), 2, '.c')")]
    [InlineData("printf '[%s]' \"q\"$x", ", 3, ($env:x))")]
    [InlineData("echo $y'x'", "$__psbash_wp = @(3, ($env:y), 1, ")]
    [InlineData("p=printf; $p '[%s]' f=$x", "$__psbash_wp = @(2, 'f=', 3, ($env:x))")]
    public void Transpile_UnquotedVarGluedToText_SplitsTheValue(string bash, string parts)
        => Assert.Contains(parts, T(bash));

    // `""$A` / `$A""`: a quoted affix anchors a field of its own (bash's quoted null): it is a quoted (kind 1) segment.
    [Theory]
    [InlineData("for i in \"\"$A; do :; done", "$__psbash_wp = @(1, ")]
    [InlineData("for i in $A\"\"; do :; done", "$__psbash_wp = @(3, ($env:A), 1, ")]
    public void Transpile_QuotedAffix_IsAQuotedSegment(string bash, string parts)
        => Assert.Contains(parts, T(bash));

    [Fact]
    public void Transpile_TmpPathGluedToVar_MapsTheTempDirectory()
        => Assert.Contains("$__psbash_wp = @(1, (" + PsBuild.TempDirExpr + "), 2, '/a', 3, ($env:x))", T("cat /tmp/a$x"));

    // bash splits every unquoted expansion of a word, beside a glob or another expansion too (x='a b': `$x*` is a and
    // b*, `a$x$x` is aa, ba, b). Both kept one field before.
    [Theory]
    [InlineData("ls *$x", "$__psbash_wp = @(2, '*', 3, ($env:x))")]
    [InlineData("echo a$x$y", "$__psbash_wp = @(2, 'a', 3, ($env:x), 3, ($env:y))")]
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
        => Assert.Contains("Resolve-BashCommand -Words @(if ($null -eq ($__psbash_wf = [PsBash.Cmdlets.BashWordFields]::Words(", T(bash));

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
    [InlineData("printf '<%s>' -\"${a[@]}\"", "$__psbash_wp = @(2, '-', 5, (@(")]
    [InlineData("printf '<%s>' \"p${a[@]}q\"", "$__psbash_wp = @(1, ")]
    [InlineData("printf '<%s>' k\"$@\"", "$__psbash_wp = @(2, 'k', 5, (@(")]
    [InlineData("for w in -\"${a[@]}\"; do :; done", "$__psbash_wp = @(2, '-', 5, (@(")]
    public void Transpile_QuotedArrayWithText_DistributesOverTheElements(string bash, string parts)
        => Assert.Contains(parts, T(bash));

    [Fact]
    public void Transpile_QuotedArrayAlone_IsTheElementArray()
        => Assert.DoesNotContain("ConvertTo-BashWords", T("printf '<%s>' \"${a[@]}\""));

    // `"${a[@]}${b[@]}"`: the last element of a joins the first of b; `x$y"${a[@]}"`: $y is split, the list glued on.
    [Theory]
    [InlineData("printf '<%s>' \"${a[@]}${b[@]}\"", "$__psbash_wp = @(5, (@(")]
    [InlineData("printf '<%s>' x$y\"${a[@]}\"", "$__psbash_wp = @(2, 'x', 3, ($env:y), 5, (@(")]
    public void Transpile_QuotedArrayBesideAnotherExpansion_IsOneWordOfSegments(string bash, string parts)
        => Assert.Contains(parts, T(bash));

    // `"$x${a[@]}"`: the quoted $x is text with no anchor of its own (kind 7): with x and a empty there is no word (bash
    // 5.2: `set --; e=; set -- "$e$@"` gives $# 0), while literal text (`"p$@"`) makes the field exist (kind 1).
    [Fact]
    public void Transpile_QuotedVarBesideQuotedArray_IsQuotedTextWithoutAnchor()
        => Assert.Contains("$__psbash_wp = @(7, (\"$env:x\"), 5, (@(", T("printf '<%s>' \"$x${a[@]}\""));

    [Theory]
    [InlineData("set -- \"$@$e\"", "$__psbash_wp = @(5, (@(")]
    [InlineData("set -- \"$@$e\"", "))), 7, (\"")]
    [InlineData("set -- \"x$@\"", "$__psbash_wp = @(1, ")]
    public void Transpile_QuotedListWithExpansionsOnly_HasNoAnchor(string bash, string parts)
        => Assert.Contains(parts, T(bash));

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

    // `"${!a[*]}"` / `"${!p*}"` too (IFS=-: `0-1-2`, `pa-pb`); the parser kept them as the `@` forms, which join with a
    // space. The `@` forms stay one word per key or name.
    [Theory]
    [InlineData("echo \"${!a[*]}\"")]
    [InlineData("echo \"${!qzp*}\"")]
    public void Transpile_QuotedStarKeysOrNames_JoinWithTheFirstIfsCharacter(string bash)
        => Assert.Contains(PsBuild.IfsJoinSeparator, T(bash));

    [Theory]
    [InlineData("echo \"${!a[@]}\"")]
    [InlineData("echo \"${!qzp@}\"")]
    public void Transpile_QuotedAtKeysOrNames_DoNotJoin(string bash)
        => Assert.DoesNotContain(PsBuild.IfsJoinSeparator, T(bash));

    // `p1=1 p2=2; set -- "${!p@}"`: one word per name ($# 2), like "${!a[@]}"; it was one joined word.
    [Theory]
    [InlineData("set -- \"${!p@}\"", "Get-ChildItem env:")]
    [InlineData("set -- x\"${!p@}\"", "$__psbash_wp = @(2, 'x', 5, (@(")]
    public void Transpile_QuotedPrefixNamesAt_IsAList(string bash, string expected)
    {
        var ps = T(bash);
        Assert.Contains(expected, ps);
        Assert.DoesNotContain("$(@(@(Get-ChildItem env:", ps);
    }

    // `${v:-${w:-'a b'}}`: the nested default's quoted space is not a split point (bash: one field), so the word is not
    // the lone-value shortcut (its value split as a whole) but the general segments with the nested word's own quoting.
    [Fact]
    public void Transpile_NestedDefaultWithQuotedWord_UsesTheSegments()
    {
        var ps = T("set -- ${v:-${w:-'a b'}}");
        // Two groups: the outer default, and inside its word the nested one, whose word is a quoted (kind 1) segment.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(ps, System.Text.RegularExpressions.Regex.Escape("-ceq 'x') { ,@(")).Count);
        Assert.Contains("} else { ,@(1, ", ps);
    }

    [Fact]
    public void Transpile_NestedDefaultWithPlainWord_KeepsTheShortcut()
        => Assert.DoesNotContain("-ceq 'x') { ,@(", T("set -- ${v:-${w:-a b}}"));

    // `IFS=: printf '<%s>' $x` (x='a:b c'): bash expands the words before the binding applies, so $x splits on the old
    // IFS (`<a:b><c>`). The word reads the IFS parked from before the binding, through the store (an empty IFS stays set).
    [Fact]
    public void Transpile_TempIfsBinding_SplitsTheWordsOnTheIfsFromBefore()
    {
        var ps = T("IFS=: printf '<%s>' $x");
        Assert.Contains(PsBuild.ParkShellVariable("__psbash_ea_IFS", "IFS"), ps);
        Assert.Contains(PsBuild.WordSplitArray("$env:x", "__psbash_ea_IFS"), ps);
        Assert.True(ps.IndexOf(PsBuild.ParkShellVariable("__psbash_ea_IFS", "IFS"), System.StringComparison.Ordinal)
            < ps.IndexOf("$env:IFS = ", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Transpile_SplitWordWithoutTempIfs_ReadsIfs()
        => Assert.DoesNotContain("__psbash_ea_IFS", T("v=1 printf '<%s>' $x"));

    // `c=$(shopt -s failglob; echo zz*)`: the failglob error ends the substitution (status 1, reported to its stderr), not
    // the command around it: in a `try` a terminating error passed every enclosing block.
    [Theory]
    [InlineData("echo \"c=$(echo zz*)\"")]
    [InlineData("( echo zz* ) 2>/dev/null")]
    public void Transpile_SubshellScope_CatchesTheFailglobError(string bash)
        => Assert.Contains("catch [PsBash.Cmdlets.BashNoMatchException] { Write-Error -Message $_.Exception.Message -ErrorAction Continue; $global:LASTEXITCODE = 1 }", T(bash));

    // bash: `unset z; f "${z[@]}"` passes no argument. The scalar fallback (`${s[@]}` of a scalar s is s) yielded one
    // $null word for an unset name: `printf '%q,' "${z[@]}"` failed with a null reference, `$#` was 1.
    [Theory]
    [InlineData("f \"${z[@]}\"")]
    [InlineData("f \"a${z[@]}b\"")]
    [InlineData("f \"${BASH_REMATCH[@]}\"")]
    public void Transpile_UnsetArrayInQuotes_IsNoWord(string bash)
        => Assert.Contains(") -ne $null }", T(bash));
}
