using System.Text.RegularExpressions;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// An arithmetic error inside a WORD (a slice bound, a subscript, <c>$(( ))</c>, a <c>$(( ))</c> nested in another
/// expression) ends the command it belongs to and drops the rest of the line, in bash 5.2 for script and <c>-c</c> alike;
/// a failed arithmetic COMMAND (<c>(( ))</c>, <c>let</c>, <c>[[ -eq ]]</c>) only fails. Invoke-BashArith tells the two
/// apart by how it is written (InvokeBashArithmeticCommand.IsWordExpansion): a word is right after <c>$(</c> or
/// <c>$__psbash_av = </c>, and a <c>-Label</c> names a command, so a labelled call is never a word.
/// REGRESSION: slice bounds passed their parameter's name as <c>-Label</c> (<c>-Label 's'</c>), so every slice error
/// was a command's: <c>x=${s:08}; echo same</c> printed same, and a function went on past it. Run-time behaviour against
/// bash: Differential.Tests ArithExpansionFailureTests.FailedArithWord_InASliceSubscriptOrNestedWord_DropsTheLine.
/// </summary>
public class ArithmeticWordGuardTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    /// <summary>The commands bash names before an arithmetic error: the only values <c>-Label</c> may carry.</summary>
    private static readonly string[] s_commandLabels = ["((", "let", "[["];

    private static readonly Regex s_label = new(@"Invoke-BashArith\b[^\n]*? -Label '((?:[^']|'')*)'", RegexOptions.CultureInvariant);

    public static TheoryData<string> WordsWithABadConstant => new()
    {
        "echo \"${s:08}\"; echo same",
        "x=${s:08}; echo same",
        "echo \"${s:1:08}\"",
        "echo \"${a[@]:08}\"",
        "x=${a[@]:1:08}",
        "echo \"${@:08}\"",
        "echo \"${s:$((08))}\"",
        "echo $(( 1 + $((08)) ))",
        "(( $((08)) )); echo same",
        "case ${s:08} in *) echo c;; esac",
        "for i in ${s:08}; do echo $i; done",
        "if [[ ${s:08} == a ]]; then echo y; fi",
        "echo $((08))",
    };

    // Every evaluation of the bad constant is written as a word (right after `$(` or `$__psbash_av = `), without a label,
    // with and without `set -u`.
    [Theory]
    [MemberData(nameof(WordsWithABadConstant))]
    public void Transpile_ArithmeticInAWord_IsEvaluatedAsAWord(string bash)
    {
        foreach (string src in new[] { bash, "set -u\n" + bash })
        {
            string ps = T(src);
            var calls = Regex.Matches(ps, @"Invoke-BashArith '08'[^;)]*", RegexOptions.CultureInvariant);
            Assert.True(calls.Count > 0, src + "\n" + ps);
            foreach (Match call in calls)
            {
                string before = ps[..call.Index];
                Assert.True(before.EndsWith("$(", StringComparison.Ordinal) || before.EndsWith("$__psbash_av = ", StringComparison.Ordinal),
                    $"not a word: {src}\n{ps}");
                Assert.DoesNotContain(" -Label ", call.Value, StringComparison.Ordinal);
            }
            // The statement can drop its line, so the line runs in a discard scope.
            Assert.Contains("[PsBash.Cmdlets.BashLineDiscard]::Enter()", ps, StringComparison.Ordinal);
        }
    }

    // A -Label names the COMMAND that evaluates the expression; a slice bound names its parameter with -Expansion.
    [Theory]
    [MemberData(nameof(WordsWithABadConstant))]
    [InlineData("let 'x=1/0'; let x++; (( y = 1 )); for (( i = 0; i < 2; i++ )); do :; done")]
    [InlineData("[[ $n -eq 08 ]]; [[ 1 -lt x+1 ]]")]
    [InlineData("echo \"${s:i}\" \"${s:i+1:n}\" \"${a[@]:$i:1}\" \"${@:$#}\" \"${s: -2}\"")]
    public void Transpile_EveryArithmeticLabel_NamesACommand(string bash)
    {
        foreach (string src in new[] { bash, "set -u\n" + bash })
            foreach (Match m in s_label.Matches(T(src)))
                Assert.Contains(m.Groups[1].Value, s_commandLabels);
    }

    [Fact]
    public void Transpile_SliceBound_NamesItsParameterAsTheExpansion()
    {
        Assert.Contains("-Expansion 's'", T("echo \"${s:i+1}\""), StringComparison.Ordinal);
        Assert.Contains("-Expansion 'a[@]'", T("echo \"${a[@]:0:n}\""), StringComparison.Ordinal);
    }

    // `a[08]=x` parses as an expression (its constant is bad, not its syntax): the element write evaluates the subscript
    // when it runs (`08: value too great for base`, the line dropped). A subscript that is no expression is still refused.
    [Fact]
    public void Transpile_BadConstantSubscriptWrite_IsEvaluatedWhenItRuns()
    {
        string ps = T("a[08]=x; echo same");
        Assert.DoesNotContain(PsBuild.BadArithSubscriptMessage, ps, StringComparison.Ordinal);
        Assert.Contains("Set-BashElement $a \"08\"", ps, StringComparison.Ordinal);
        Assert.Contains("[PsBash.Cmdlets.BashLineDiscard]::Enter()", ps, StringComparison.Ordinal);
        Assert.Contains(PsBuild.BadArithSubscriptMessage, T("a['2']=3"), StringComparison.Ordinal);
    }

    // The words a `case` or `for` expands itself are on its own line: an error in them names that line.
    [Theory]
    [InlineData("echo one\ncase ${s:08} in *) echo c;; esac")]
    [InlineData("echo one\ncase x in ${s:08}) echo c;; esac")]
    [InlineData("echo one\nfor i in ${s:08}; do :; done")]
    public void Transpile_CaseOrForWordOnLine2_RecordsLine2(string bash)
        => Assert.Contains(PsBuild.SetCallLine(2), T(bash), StringComparison.Ordinal);

    // `$(( ))` inside an expression is a word of its own, expanded to text first: bash's `x$((1))` is the name x1.
    [Fact]
    public void Transpile_NestedArithmeticExpansion_IsExpandedAsText()
    {
        string ps = T("echo $(( x$((1)) ))");
        Assert.Contains("'x' + $(Invoke-BashArith '1')", ps, StringComparison.Ordinal);
    }
}
