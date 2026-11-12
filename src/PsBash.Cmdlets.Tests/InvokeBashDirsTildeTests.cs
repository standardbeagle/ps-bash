using PsBash.Cmdlets;
using Xunit;

namespace PsBash.Cmdlets.Tests;

public class InvokeBashDirsTildeTests
{
    [Theory]
    [InlineData("/home/u", "/home/u", "~")]
    [InlineData("/home/u/x", "/home/u", "~/x")]
    [InlineData("/home/u/", "/home/u/", "~/")]
    [InlineData("C:\\Users\\u\\x", "C:\\Users\\u", "~\\x")]
    [InlineData("/home/uu", "/home/u", "/home/uu")]      // substring of HOME is not under it
    [InlineData("/tmp/oil_tests", "/tmp/oil", "/tmp/oil_tests")]
    [InlineData("/x", "", "/x")]                         // empty HOME
    [InlineData("/x", "/", "/x")]                        // root HOME never abbreviates everything
    [InlineData("C:\\x", "C:\\", "C:\\x")]
    public void TildeAbbreviate_OnlyAtDirectoryBoundary(string path, string home, string expected)
        => Assert.Equal(expected, InvokeBashDirsCommand.TildeAbbreviate(path, home));

    /// <summary>
    /// REGRESSION: on Windows a stack entry under $HOME printed `~\dir` and others `C:\x`, unlike `pwd`'s `C:/x`.
    /// Separators become `/` there (HOME too, so it still matches) and are kept elsewhere, where `\` is a name character.
    /// </summary>
    [Theory]
    [InlineData(true, "C:\\Users\\u", "~/sub|C:/x|~")]
    [InlineData(true, null, "C:/Users/u/sub|C:/x|C:/Users/u")]   // -l: no abbreviation
    [InlineData(true, "C:/Users/u", "~/sub|C:/x|~")]             // HOME already in / form
    [InlineData(false, "C:\\Users\\u", "~\\sub|C:\\x|~")]
    public void Present_WritesSeparatorsLikePwdOnWindowsOnly(bool windows, string? home, string expected)
    {
        var entries = new List<string> { "C:\\Users\\u\\sub", "C:\\x", "C:\\Users\\u" };
        InvokeBashDirsCommand.Present(entries, home, windows);
        Assert.Equal(expected, string.Join('|', entries));
    }
}