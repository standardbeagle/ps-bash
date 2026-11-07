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
}
