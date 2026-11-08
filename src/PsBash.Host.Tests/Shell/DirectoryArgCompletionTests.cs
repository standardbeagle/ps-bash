using PsBash.Host.Shell;
using Xunit;

namespace PsBash.Host.Tests.Shell;

/// <summary>`cd <Tab>` offers the current folder's sub-directories only (bash `compgen -d`); other commands still get files.
/// Completion has no bash oracle, so hand-written asserts (qa-rubric exception).</summary>
public class DirectoryArgCompletionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "psb-dircomp-" + Guid.NewGuid().ToString("N"));

    public DirectoryArgCompletionTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "file.txt"), "x");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private IReadOnlyList<string> Tab(string line)
        => TabCompleter.Complete(line, line.Length, new Dictionary<string, string>(), _dir).Select(c => c.InsertText).ToList();

    [Theory]
    [InlineData("cd ")]
    [InlineData("pushd ")]
    [InlineData("rmdir ")]
    public void Complete_DirectoryCommandEmptyToken_ListsOnlySubdirectories(string line)
    {
        var r = Tab(line);
        Assert.Contains("sub/", r);
        Assert.DoesNotContain("file.txt", r);
    }

    [Fact] public void Complete_CdPartialName_StillSkipsFiles() => Assert.DoesNotContain("file.txt", Tab("cd fil"));

    [Fact] public void Complete_CatEmptyToken_ListsFilesAndDirectories()
    {
        var r = Tab("cat ");
        Assert.Contains("sub/", r);
        Assert.Contains("file.txt", r);
    }
}
