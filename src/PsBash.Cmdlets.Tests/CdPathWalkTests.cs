using PsBash.Cmdlets;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>bash's logical cd checks every directory before a `..`: `cd nonexistent/..` fails (status 1).</summary>
public class CdPathWalkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "psbash-cdwalk-" + Guid.NewGuid().ToString("N")[..8]);

    public CdPathWalkTests() => Directory.CreateDirectory(Path.Combine(_root, "a"));
    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData("nonexistent/..", false)]
    [InlineData("a/..", true)]
    [InlineData("./a/../a/..", true)]
    [InlineData("a/nonexistent/../..", false)]
    [InlineData("..", true)]
    [InlineData("plain", true)]            // no `..`: GetFullPath + Directory.Exists decide
    public void CdPathWalkExists_Relative(string rel, bool expected)
        => Assert.Equal(expected, BashRuntime.CdPathWalkExists(rel, _root));

    [Fact] public void CdPathWalkExists_Absolute_ChecksEveryComponent()
    {
        Assert.True(BashRuntime.CdPathWalkExists(Path.Combine(_root, "a", "..", "a"), _root));
        Assert.False(BashRuntime.CdPathWalkExists(Path.Combine(_root, "missing", "..", "a"), _root));
    }
}
