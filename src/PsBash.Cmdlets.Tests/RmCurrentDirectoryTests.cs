using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// bash can remove its own working directory (`cd "$tmp"; rm -rf "$tmp"` in an EXIT trap). Windows refuses while
/// the process's current directory is inside the tree, so the shared delete steps out first.
/// Hand-written, not oracle: the failure is a Windows file-locking rule with no bash counterpart.
/// </summary>
[Collection(ProcessWorkingDirectoryCollection.Name)]
public class RmCurrentDirectoryTests
{
    [SkippableFact]
    public void RunChildProcess_StillResolvesABareProgram_WhenTheScriptDeletedItsOwnCwd()
    {
        // bash lets a script rm -rf the directory it stands in and keep working; .NET's PATH
        // lookup for a bare name calls getcwd, which then throws FileNotFoundException.
        Skip.If(OperatingSystem.IsWindows(), "Windows refuses to delete a directory that is the process cwd");
        var original = Directory.GetCurrentDirectory();
        var doomed = Path.Combine(Path.GetTempPath(), "psbash-doomed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(doomed);
        try
        {
            Directory.SetCurrentDirectory(doomed);
            Directory.Delete(doomed);

            var result = BashRuntime.RunChildProcess("echo", new[] { "still-works" });

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("still-works", result.Stdout);
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
        }
    }

    [Trait("Platform", "Windows")]
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeleteDirectoryForce_CurrentDirectoryInsideTree_RemovesIt(bool nested)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "a held current directory blocks deletion only on Windows");
        string root = Path.Combine(Path.GetTempPath(), "ps-bash", "rmcwd-" + Guid.NewGuid().ToString("N")[..8]);
        string inner = nested ? Path.Combine(root, "a", "b") : root;
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "f"), "x");
        string saved = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(inner);
            FileSystemHelpers.DeleteDirectoryForce(root);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            Directory.SetCurrentDirectory(saved);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeleteDirectoryForce_CurrentDirectoryElsewhere_LeavesItAlone()
    {
        string root = Path.Combine(Path.GetTempPath(), "ps-bash", "rmcwd-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        string before = Directory.GetCurrentDirectory();
        FileSystemHelpers.DeleteDirectoryForce(root);
        Assert.False(Directory.Exists(root));
        Assert.Equal(before, Directory.GetCurrentDirectory());
    }
}