using PsBash.Host.Shell;
using Xunit;

namespace PsBash.Host.Tests.Shell;

/// <summary>
/// Tests for bash-style history expansion (<see cref="HistoryExpander"/>): the bang commands
/// (<c>!!</c>, <c>!n</c>, <c>!-n</c>, <c>!str</c>, <c>!?str?</c>), word designators
/// (<c>!$</c>, <c>!^</c>, <c>!*</c>, <c>:n</c>), and quick substitution (<c>^old^new</c>).
///
/// Oracle note (qa-rubric Directive 1): history expansion is an interactive REPL surface with no
/// byte-comparable bash oracle in this harness, so hand-written asserts against the documented
/// bash semantics are justified per the exception list. The expander is a pure function over an
/// explicit history list — no shared state, no sleeps.
/// </summary>
public class HistoryExpanderTests
{
    private static readonly string[] Hist =
    {
        "echo first",          // event 1
        "ls -la /tmp",         // event 2
        "git commit -m wip",   // event 3
    };

    private static string ExpandOk(string line, params string[] history)
    {
        var result = HistoryExpander.Expand(line, history, out var error);
        Assert.Null(error);
        Assert.NotNull(result);
        return result!;
    }

    [Fact]
    public void Expand_DoubleBang_ReplacesWithPreviousCommand()
        => Assert.Equal("git commit -m wip", ExpandOk("!!", Hist));

    [Fact]
    public void Expand_BangN_ReplacesWithAbsoluteSessionIndex()
        => Assert.Equal("echo first", ExpandOk("!1", Hist));

    [Fact]
    public void Expand_BangMinusN_CountsBackFromEnd()
        => Assert.Equal("ls -la /tmp", ExpandOk("!-2", Hist));

    [Fact]
    public void Expand_BangPrefix_FindsMostRecentMatch()
        => Assert.Equal("git commit -m wip", ExpandOk("!git", Hist));

    [Fact]
    public void Expand_BangQuestion_FindsMostRecentSubstring()
        => Assert.Equal("ls -la /tmp", ExpandOk("!?-la?", Hist));

    [Fact]
    public void Expand_BangDollar_TakesLastArgumentOfPreviousCommand()
        => Assert.Equal("cat wip", ExpandOk("cat !$", Hist));

    [Fact]
    public void Expand_BangCaret_TakesFirstArgumentOfPreviousCommand()
        => Assert.Equal("echo commit", ExpandOk("echo !^", Hist));

    [Fact]
    public void Expand_BangStar_TakesAllArguments()
        => Assert.Equal("vim commit -m wip", ExpandOk("vim !*", Hist));

    [Fact]
    public void Expand_WordDesignatorSuffix_SelectsWordFromEvent()
        => Assert.Equal("/tmp", ExpandOk("!2:$", Hist));

    [Fact]
    public void Expand_EmbeddedBang_ExpandsInPlace()
        => Assert.Equal("sudo git commit -m wip", ExpandOk("sudo !!", Hist));

    [Fact]
    public void Expand_QuickSubstitution_ReplacesFirstMatchOnPreviousCommand()
        => Assert.Equal("git commit -m done", ExpandOk("^wip^done", Hist));

    [Fact]
    public void Expand_QuickSubstitution_TrailingTextPreserved()
        => Assert.Equal("git commit -m done", ExpandOk("^wip^done^", Hist));

    [Fact]
    public void Expand_NoBang_ReturnsInputUnchanged()
    {
        const string line = "echo hello world";
        Assert.Equal(line, ExpandOk(line, Hist));
    }

    [Fact]
    public void Expand_NegationOperator_NotTreatedAsBang()
    {
        // `! cmd` (pipeline negation) and `a != b` must survive untouched.
        Assert.Equal("! test -f x", ExpandOk("! test -f x", Hist));
        Assert.Equal("[ a != b ]", ExpandOk("[ a != b ]", Hist));
    }

    [Fact]
    public void Expand_InsideSingleQuotes_Suppressed()
        => Assert.Equal("echo '!!'", ExpandOk("echo '!!'", Hist));

    [Fact]
    public void Expand_InsideDoubleQuotes_StillExpands()
    {
        // bash expands history references inside double quotes (only single quotes suppress).
        Assert.Equal("echo \"git commit -m wip\"", ExpandOk("echo \"!!\"", Hist));
    }

    [Fact]
    public void Expand_EscapedBang_Suppressed()
        => Assert.Equal("echo \\!!", ExpandOk("echo \\!!", Hist));

    [Fact]
    public void Expand_UnknownPrefix_ReportsEventNotFound()
    {
        var result = HistoryExpander.Expand("!nope", Hist, out var error);
        Assert.Null(result);
        Assert.Equal("!nope: event not found", error);
    }

    [Fact]
    public void Expand_BangBangEmptyHistory_ReportsEventNotFound()
    {
        var result = HistoryExpander.Expand("!!", System.Array.Empty<string>(), out var error);
        Assert.Null(result);
        Assert.Equal("!!: event not found", error);
    }

    [Fact]
    public void Expand_OutOfRangeIndex_ReportsEventNotFound()
    {
        var result = HistoryExpander.Expand("!9", Hist, out var error);
        Assert.Null(result);
        Assert.Equal("!9: event not found", error);
    }

    [Fact]
    public void Expand_QuickSubstitutionNoMatch_ReportsFailure()
    {
        var result = HistoryExpander.Expand("^missing^x", Hist, out var error);
        Assert.Null(result);
        Assert.Equal("missing: substitution failed", error);
    }

    [Theory]
    [InlineData("!!", true)]              // bang anywhere
    [InlineData("sudo !$", true)]
    [InlineData("^a^b", true)]            // caret at line start
    [InlineData("echo hi", false)]
    [InlineData("a ^ b", false)]          // caret mid-line is not quick-substitution
    [InlineData("plain text", false)]
    public void ContainsExpansion_GatesCorrectly(string line, bool expected)
        => Assert.Equal(expected, HistoryExpander.ContainsExpansion(line));
}
