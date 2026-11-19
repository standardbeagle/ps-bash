using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <see cref="OperandChain"/>: the directory chain GNU mkdir -p / install -d / rmdir -p walk, as operand TEXT.
/// Expected chains are the verbose lines of GNU coreutils 9.4 (WSL Ubuntu-24.04): <c>mkdir -pv a/b/c</c>,
/// <c>rmdir -pv a/x//y/</c>, <c>rmdir -pv ./z/w</c>. The guard half: a removal chain never leaves the operand's
/// own components (rmdir -p used to climb the absolute parents and delete empty directories above the operand).
/// </summary>
public class OperandChainTests
{
    [Theory]
    [InlineData("a/b/c", new[] { "a", "a/b", "a/b/c" })]
    [InlineData("a", new[] { "a" })]
    [InlineData("./a/x//y/", new[] { ".", "./a", "./a/x", "./a/x//y/" })]
    [InlineData("a//b//", new[] { "a", "a//b//" })]
    [InlineData("/tmp/q/r", new[] { "/tmp", "/tmp/q", "/tmp/q/r" })]
    [InlineData("/", new[] { "/" })]
    public void CreationChain_IsEveryPrefixAsTyped(string operand, string[] expected)
        => Assert.Equal(expected, OperandChain.CreationChain(operand));

    [Theory]
    [InlineData("a/x//y/", new[] { "a/x", "a" })]
    [InlineData("./z/w", new[] { "./z", "." })]
    [InlineData("a", new string[0])]
    [InlineData("a/", new string[0])]
    [InlineData("/tmp/abs/x", new[] { "/tmp/abs", "/tmp" })]
    [InlineData("//x/y", new[] { "//x" })]
    public void RemovalParents_AreTheOperandsOwnAncestors(string operand, string[] expected)
        => Assert.Equal(expected, OperandChain.RemovalParents(operand));

    [SkippableFact]
    public void RemovalParents_StopBeforeADriveRoot()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "drive designators are Windows-only");
        Assert.Equal(new[] { "C:/a" }, OperandChain.RemovalParents("C:/a/b"));
        Assert.Equal(new[] { "C:/a", "C:/a/b" }, OperandChain.CreationChain("C:/a/b"));
    }

    // REGRESSION (Windows): `\` was not a separator, so `rmdir -p C:\t\x\y` had no parents and `mkdir -pv a\b`
    // named one directory.
    [SkippableFact]
    [Trait("Platform", "Windows")]
    public void Backslash_IsASeparatorOnWindows()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "\\ is an ordinary character in a Unix file name");
        Assert.Equal(new[] { @"C:\t\x", @"C:\t" }, OperandChain.RemovalParents(@"C:\t\x\y"));
        Assert.Equal(new[] { "a", @"a\b" }, OperandChain.CreationChain(@"a\b"));
    }

    [Theory]
    [InlineData("a/b/c")]
    [InlineData("./x//y/z/")]
    [InlineData("/r/s/t")]
    public void RemovalParents_ArePrefixesOfTheOperand(string operand)
    {
        foreach (var parent in OperandChain.RemovalParents(operand))
            Assert.StartsWith(parent, operand, StringComparison.Ordinal);
    }
}
