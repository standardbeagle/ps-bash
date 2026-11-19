using PsBash.Cmdlets;
using Xunit;

namespace PsBash.Cmdlets.Tests;

public class BashPromptTextTests
{
    [Theory]
    [InlineData("\\[foo\\]\\$", "foo$")]
    [InlineData("\\\\\\$", "\\$")]
    [InlineData("\\1004$", "@4$")]
    [InlineData("[\\045]", "[%]")]
    [InlineData("\\555$", "m$")]
    public void Expand_HandlesPromptEscapes(string input, string expected) =>
        Assert.Equal(expected, BashPromptText.Expand(input));

    // bash 5.2 (oracle): the promptvars pass removes a backslash before $ ` " \ after decoding, and \$ decodes to \$.
    [Theory]
    [InlineData("$", "$")]
    [InlineData("\\$", "$")]
    [InlineData("\\\\$", "$")]
    [InlineData("\\\\\\$", "\\$")]
    [InlineData("\\\\\\\\$", "\\$")]
    [InlineData("a\\\"b", "a\"b")]
    [InlineData("a\\qb", "a\\qb")]
    public void Expand_DollarAndBackslash_QuoteRemovalLikeBash(string input, string expected) =>
        Assert.Equal(expected, BashPromptText.Expand(input));

    // bash 5.2 (oracle): x='\'; y=h; PS1='$x$y' prints \h (the value is not decoded or quote-removed);
    // z='a$y'; PS1='<${z}>\\$y' prints <a$y>$y; an unset name is empty. $(...) is left as written: ps-bash
    // never runs code from a variable's value (bash would print the command's output).
    [Theory]
    [InlineData("$x$y", "\\h")]
    [InlineData("<${z}>\\$y", "<a$y>$y")]
    [InlineData("$nope-", "-")]
    [InlineData("${x", "${x")]
    [InlineData("$(echo hi)", "$(echo hi)")]
    [InlineData("$1 $", "$1 $")]
    public void ExpandParameters_ExpandsNamesOnly(string decoded, string expected)
    {
        var values = new Dictionary<string, string> { ["x"] = "\\", ["y"] = "h", ["z"] = "a$y" };
        Assert.Equal(expected, BashPromptText.ExpandParametersAndRemoveQuoting(decoded, n => values.GetValueOrDefault(n)));
    }

    [Fact]
    public void Expand_InsertedWorkingDir_KeepsBackslashes()
    {
        var saved = Environment.GetEnvironmentVariable("PWD");
        try
        {
            Environment.SetEnvironmentVariable("PWD", @"C:\a\$b");
            Assert.Equal(@"[C:\a\$b]", BashPromptText.Expand("[\\w]"));
        }
        finally { Environment.SetEnvironmentVariable("PWD", saved); }
    }

    [Fact]
    public void Expand_EmptyStrftime_IsHourMinuteSecond() =>
        Assert.Matches(@"^foo \d\d:\d\d:\d\d bar$", BashPromptText.Expand("foo \\D{} bar"));

    [Theory]
    [InlineData("a\\tb", "a\tb")]
    [InlineData("\\x41\\101", "AA")]
    [InlineData("\\q", "\\q")]
    public void Unescape_ExpandsDollarQuoteEscapes(string input, string expected) =>
        Assert.Equal(expected, BashPromptText.Unescape(input));

    // \j \! \# are the shell's counters, asked for by letter and only when the prompt holds the escape. They were
    // hardcoded (\j 0, \! and \# 1), so `sleep 5 &` never made \j 1 (oils prompt.spec "\j for number of jobs").
    [Theory]
    [InlineData("foo \\j bar", "foo 3 bar")]
    [InlineData("\\! \\#", "7 12")]
    [InlineData("\\j\\j", "33")]
    public void Decode_CounterEscapes_ComeFromTheShell(string prompt, string expected) =>
        Assert.Equal(expected, BashPromptText.Decode(prompt, c => c switch { 'j' => 3, '!' => 7, '#' => 12, _ => -1 }));

    [Fact]
    public void Decode_PromptWithoutCounters_AsksForNone() =>
        Assert.Equal("a b", BashPromptText.Decode("a \\[b\\]", _ => throw new InvalidOperationException("asked for a counter")));

    // bash 5.2 (oracle, a script file): \# is the number of the top-level command running (bash -c: 0); \! for
    // ${v@P} is 1 with history off, else the entry count, 1 once `history -c` emptied the list.
    [Theory]
    [InlineData(0, false, 1)]
    [InlineData(5, false, 5)]
    [InlineData(5, true, 0)]
    public void CommandNumberFor_CountsLinesRead_ZeroUnderDashC(int read, bool dashC, int expected) =>
        Assert.Equal(expected, ShellReader.CommandNumberFor(read, dashC));

    [Theory]
    [InlineData(false, 9, 1)]
    [InlineData(true, 0, 1)]
    [InlineData(true, 1, 1)]
    [InlineData(true, 3, 3)]
    public void HistoryNumberFor_FollowsBashPromptHistoryNumber(bool historyOn, int entries, int expected) =>
        Assert.Equal(expected, ShellReader.HistoryNumberFor(historyOn, entries));

    // bash 5.2 (oracle, `printf 'echo a\necho b\n' | PS1='[\!] ' bash --norc -i`): [1], [2], [3]: the prompt names the
    // command about to be read, not yet in the list.
    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 3)]
    public void HistoryNumberFor_InteractivePrompt_IsOnePastTheCount(int entries, int expected) =>
        Assert.Equal(expected, ShellReader.HistoryNumberFor(true, entries, interactive: true));
}
