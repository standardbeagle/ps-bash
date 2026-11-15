using PsBash.Core.Parser;
using PsBash.Core.Parser.Ast;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// Word shapes the word parser reads the way bash does, and the emitter's line-abandon guards (failglob, bad
/// substitution). Run-time behaviour is checked against bash in Differential.Tests LineAbandonAndWordShapeTests.
/// </summary>
public class WordShapeAndLineAbandonTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    // ~{/src,root}: brace expansion comes first, so the ~ is a literal until each word is tilde-expanded.
    [Fact]
    public void DecomposeWord_TildeBeforeBrace_KeepsTheTildeForAfterTheBraces()
    {
        var parts = BashParser.DecomposeWord("~{/src,root}");
        Assert.Equal(new WordPart.Literal("~"), parts[0]);
        Assert.IsType<WordPart.BracedTuple>(parts[1]);
        Assert.IsType<WordPart.TildeSub>(BashParser.DecomposeWord("~/x")[0]);
    }

    [Fact]
    public void Transpile_TildeBeforeBrace_ExpandsHomeInEachWord()
        => Assert.Contains("$HOME", T("echo ~{/src,/lib}"));

    // {z..A} runs through a backtick: bash fails it as a bad substitution when the command runs (status 1).
    [Fact]
    public void DecomposeWord_CharRangeThroughBacktick_IsABadSubstitution()
    {
        var parts = BashParser.DecomposeWord("-{z..A}-");
        Assert.Contains(parts, p => p is WordPart.BracedVarSub { Suffix: { } s } && s.StartsWith("#BAD:", StringComparison.Ordinal));
        Assert.Contains(BashParser.DecomposeWord("{a..e}"), p => p is WordPart.BracedTuple);
    }

    [Theory]
    [InlineData("[[:punct\\:]]", "[[:punct:]]")]
    [InlineData("[[:al'p'ha:]x]", "[[:alpha:]x]")]
    [InlineData("[[:digit:]]", "[[:digit:]]")]
    public void UnquoteClass_QuotedOrEscapedClassName_IsTheClass(string cls, string expected)
        => Assert.Equal(expected, BashParser.UnquoteClass(cls));

    // In an assignment, the word of ${undef-...} is itself an assignment value: a ~ after its `:` expands.
    [Fact]
    public void Transpile_TildeAfterColonInAssignmentOperatorWord_Expands()
    {
        string ps = T("x=~:${undef-~:~}");
        Assert.True(System.Text.RegularExpressions.Regex.Matches(ps, @"\$\{?HOME").Count >= 3, ps);
    }

    // IFS=5 echo ${#v}: the length is split like any unquoted expansion.
    [Fact]
    public void Transpile_UnquotedLength_IsWordSplit()
        => Assert.Contains("ConvertTo-BashWords", T("echo ${#v}"));

    // readonly "$binding": the eval text single-quotes the value (${w@Q}), so const=~/src keeps its ~.
    [Fact]
    public void Transpile_DynamicDeclaration_QuotesTheFieldWithAtQ()
    {
        string ps = T("readonly \"$binding\"");
        Assert.Contains("__psbash_declw", ps);
        Assert.DoesNotContain("'%q'", ps);
    }

    // ab+($ABC): the variable arm is split before the pattern is formed (IFS=c ABC='c?d' gives `ab+(` and `?d)`).
    [Fact]
    public void Transpile_ExtglobWithBareVariableArm_IsSplitAroundTheVariable()
    {
        Assert.Contains("ConvertTo-BashWords -Prefix", T("echo ab+($ABC)"));
        Assert.DoesNotContain("ConvertTo-BashWords -Prefix", T("echo ab+(\"$ABC\")"));
    }

    [Fact]
    public void Transpile_GlobInsideBraceItem_IsPathnameExpanded()
        => Assert.Contains("ConvertTo-BashGlob", T("echo {**/*.*,}"));

    // failglob: a command sharing its line with an earlier one runs only when no glob failed since the line began.
    [Fact]
    public void Transpile_Failglob_GuardsTheRestOfTheLine()
    {
        string ps = T("shopt -s failglob\necho *.ZZ; echo same\necho next");
        Assert.Contains(PsBuild.FailGlobReset, ps);
        Assert.Contains(PsBuild.UnlessGlobFailed("echo same").Split('{')[0], ps);
        Assert.DoesNotContain("__BashFailGlob) { Invoke-BashEcho next", ps);
        Assert.DoesNotContain("__BashFailGlob", T("echo a; echo b"));
    }

    // A bad substitution abandons its command and the rest of the line (the line scope catches the discard and sets
    // the line mark); the script goes on.
    [Fact]
    public void Transpile_BadSubstitution_GuardsTheRestOfItsLine()
    {
        string ps = T("echo ${a[]}; echo same\necho next");
        Assert.StartsWith(PsBuild.FailGlobReset, ps.TrimStart());
        Assert.Contains(PsBuild.ExpansionErrorFunction, ps);
        Assert.Contains("if (-not $global:__BashFailGlob) { try { [PsBash.Cmdlets.BashLineDiscard]::Enter(); Invoke-BashEcho same", ps);
        Assert.DoesNotContain("__BashFailGlob) { Invoke-BashEcho next", ps);
        Assert.DoesNotContain("__BashFailGlob", T("echo ${a[0]}; echo b"));
    }

    // `echo *.ZZ || echo alt`: a failed glob abandons the whole list, so each later operand is guarded too.
    [Fact]
    public void Transpile_Failglob_GuardsEveryLaterOperandOfAnAndOrList()
    {
        string ps = T("shopt -s failglob\necho *.ZZ || echo alt && echo more");
        Assert.Contains(PsBuild.ChainOperandUnlessGlobFailed("Invoke-BashEcho alt").Split('{')[0], ps);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(ps, @"\|\| \$\(if \(-not|&& \$\(if \(-not").Count);
        Assert.DoesNotContain("$(if (-not $global:__BashFailGlob)", T("false || echo alt"));
    }

    // eval text that can fail a glob ends by clearing the line mark: the line that ran the eval goes on.
    [Fact]
    public void Transpile_EvalTextWithAGlob_ClearsTheLineMarkAtItsEnd()
    {
        string ps = PsEmitter.Transpile("echo *.ZZ", PsBash.Core.Transpiler.TranspileContext.Eval)!;
        Assert.EndsWith("\n$global:__BashFailGlob = $false", ps);
        Assert.DoesNotContain("__BashFailGlob", PsEmitter.Transpile("echo hi", PsBash.Core.Transpiler.TranspileContext.Eval)!);
    }

    // alias c='cat <<EOF\n...': the body is read from the lines after the alias use; the alias's later lines follow it.
    [Fact]
    public void SpliceAliases_HereDocInAlias_TakesTheBodyFromTheInput()
    {
        string src = "shopt -s expand_aliases\nalias c='cat <<EOF\n$(echo hi)\nEOF\n'\nc\nbody\nEOF\necho st";
        string? spliced = PsEmitter.SpliceAliases(src, out bool partial);
        Assert.True(partial);
        Assert.NotNull(spliced);
        Assert.EndsWith("cat <<EOF\nbody\nEOF\n$(echo hi)\nEOF\necho st", spliced);

        string atEnd = PsEmitter.SpliceAliases("shopt -s expand_aliases\nalias c='cat <<EOF\nx\nEOF\n'\nc\n", out _)!;
        Assert.EndsWith("cat <<EOF\nEOF\nx\nEOF\n", atEnd);
    }
}
