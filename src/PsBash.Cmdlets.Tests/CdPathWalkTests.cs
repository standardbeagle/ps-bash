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

    // REGRESSION (macOS: $TMPDIR is /var/folders/…, /var -> /private/var): `cd sub` resolved against getcwd, so `pwd`
    // after it printed /private/var/…/sub where bash prints the path cd went by. Hand-written: the base decision is
    // ps-bash's own (bash keeps $PWD in the shell); the end-to-end form is Differential `Subshell_EveryForm`.
    [SkippableFact] public void CdBase_KeepsTheLogicalPathWhileItNamesTheProcessCwd()
    {
        var link = Path.Combine(_root, "link");
        try { Directory.CreateSymbolicLink(link, Path.Combine(_root, "a")); }
        catch (Exception ex) { Skip.If(true, $"cannot create directory symlink (needs privilege): {ex.Message}"); }

        Assert.Equal(link, BashRuntime.CdBase(link, Path.Combine(_root, "a")));
    }

    [Fact] public void CdBase_UsesTheProcessCwdWhenTheLogicalPathIsStaleOrUnset()
    {
        var a = Path.Combine(_root, "a");
        Assert.Equal(a, BashRuntime.CdBase(null, a));
        Assert.Equal(a, BashRuntime.CdBase("", a));
        Assert.Equal(a, BashRuntime.CdBase("relative/x", a));
        Assert.Equal(a, BashRuntime.CdBase(_root, a));                          // another existing directory
        Assert.Equal(a, BashRuntime.CdBase(Path.Combine(_root, "gone"), a));    // removed since
        Assert.Equal(a + Path.DirectorySeparatorChar, BashRuntime.CdBase(a + Path.DirectorySeparatorChar, a));  // same text
    }
}
