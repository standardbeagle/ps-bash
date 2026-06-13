using Xunit;
using PsBash.Host.Shell;

namespace PsBash.Shell.Tests;

/// <summary>
/// Pure-parsing tests for the zoxide z/zi prompt-side interception helpers.
/// Frecency ranking itself is covered by <see cref="FrecencyStoreTests"/>.
/// </summary>
public class ZCommandParsingTests
{
    [Theory]
    [InlineData("z", false, new string[0])]
    [InlineData("zi", true, new string[0])]
    [InlineData("z foo", false, new[] { "foo" })]
    [InlineData("zi foo", true, new[] { "foo" })]
    [InlineData("z foo bar", false, new[] { "foo", "bar" })]
    [InlineData("z   foo   bar  ", false, new[] { "foo", "bar" })]
    public void TryParseZCommand_RecognizesJumpCommands(string line, bool expectInteractive, string[] expectKeywords)
    {
        var ok = InteractiveShell.TryParseZCommand(line, out var interactive, out var keywords);

        Assert.True(ok);
        Assert.Equal(expectInteractive, interactive);
        Assert.Equal(expectKeywords, keywords);
    }

    [Theory]
    [InlineData("zoo")]          // not "z"/"zi" and no "z "/"zi " prefix
    [InlineData("zip x")]        // starts with "zi" but not "zi "
    [InlineData("cd foo")]
    [InlineData("zebra")]
    [InlineData("")]
    public void TryParseZCommand_RejectsNonJumpLines(string line)
    {
        Assert.False(InteractiveShell.TryParseZCommand(line, out _, out _));
    }

    [Theory]
    [InlineData("./foo", true)]
    [InlineData("../x", true)]
    [InlineData("/abs/path", true)]
    [InlineData("~/dir", true)]
    [InlineData("a/b", true)]
    [InlineData("C:\\Users", true)]
    [InlineData(".", true)]
    [InlineData("..", true)]
    [InlineData("foo", false)]
    [InlineData("project", false)]
    public void LooksLikePath_DistinguishesPathsFromKeywords(string token, bool expected)
    {
        Assert.Equal(expected, InteractiveShell.LooksLikePath(token));
    }

    [Theory]
    [InlineData("a b", "'a b'")]
    [InlineData("plain", "'plain'")]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("C:\\Program Files\\x", "'C:\\Program Files\\x'")]
    public void SingleQuoteBash_QuotesAndEscapes(string input, string expected)
    {
        Assert.Equal(expected, InteractiveShell.SingleQuoteBash(input));
    }
}
