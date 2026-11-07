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

    // The rewrite quotes the expanded path. `cd '~'` looks for a directory named "~".
    [Fact]
    public void CdRewrite_Tilde_QuotesExpandedHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "psbash-ztilde-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var rewrite = InteractiveShell.CdRewriteForExistingDirectory("~", cwd: home, home: home);

            var expected = "cd " + InteractiveShell.SingleQuoteBash(Path.GetFullPath(home));
            Assert.Equal(expected, rewrite);
            Assert.DoesNotContain("'~'", rewrite!);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void CdRewrite_TildeSubdir_QuotesExpandedPath()
    {
        var home = Path.Combine(Path.GetTempPath(), "psbash-ztilde-" + Guid.NewGuid().ToString("N"));
        var child = Path.Combine(home, "x");
        Directory.CreateDirectory(child);
        try
        {
            var rewrite = InteractiveShell.CdRewriteForExistingDirectory("~/x", cwd: home, home: home);

            Assert.Equal("cd " + InteractiveShell.SingleQuoteBash(Path.GetFullPath(child)), rewrite);
            Assert.DoesNotContain("'~", rewrite!);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void CdRewrite_MissingTildeSubdir_DoesNotRewrite()
    {
        var home = Path.Combine(Path.GetTempPath(), "psbash-ztilde-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            // Missing path falls through to frecency instead of `cd '~/nope'`.
            Assert.Null(InteractiveShell.CdRewriteForExistingDirectory("~/nope", cwd: home, home: home));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    // A bare name is not "path-shaped" (LooksLikePath is false) but still cds when the directory exists.
    [Fact]
    public void CdRewrite_BareSubdir_QuotesItEvenWhenNeverVisited()
    {
        var parent = Path.Combine(Path.GetTempPath(), "psbash-zsub-" + Guid.NewGuid().ToString("N"));
        var child = Path.Combine(parent, "somedir");
        Directory.CreateDirectory(child);
        try
        {
            Assert.False(InteractiveShell.LooksLikePath("somedir"));

            var rewrite = InteractiveShell.CdRewriteForExistingDirectory("somedir", cwd: parent, home: parent);

            Assert.Equal("cd " + InteractiveShell.SingleQuoteBash(Path.GetFullPath(child)), rewrite);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void CdRewrite_MissingBareName_DoesNotRewrite()
    {
        var parent = Path.Combine(Path.GetTempPath(), "psbash-zsub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            Assert.Null(InteractiveShell.CdRewriteForExistingDirectory("somedir", cwd: parent, home: parent));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void CdRewrite_BareNameThatIsAFile_DoesNotRewrite()
    {
        var parent = Path.Combine(Path.GetTempPath(), "psbash-zsub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        File.WriteAllText(Path.Combine(parent, "somedir"), "not a directory");
        try
        {
            Assert.Null(InteractiveShell.CdRewriteForExistingDirectory("somedir", cwd: parent, home: parent));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    // A name with a quote and command substitution must stay one single-quoted word.
    [Fact]
    public void CdRewrite_NameWithQuoteAndSubstitution_IsSingleQuoted()
    {
        var parent = Path.Combine(Path.GetTempPath(), "psbash-zsub-" + Guid.NewGuid().ToString("N"));
        var name = "a'$(uname)";
        var child = Path.Combine(parent, name);
        Directory.CreateDirectory(child);
        try
        {
            var rewrite = InteractiveShell.CdRewriteForExistingDirectory(name, cwd: parent, home: parent);

            Assert.Equal("cd " + InteractiveShell.SingleQuoteBash(Path.GetFullPath(child)), rewrite);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }
}
