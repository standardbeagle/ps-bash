using PsBash.Core.Runtime.Compaction;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Compaction;

/// <summary>
/// End-to-end behavior of the embedded built-in filters: load the real JSON resources
/// and run representative captured command output through <see cref="FilterEngine"/>.
/// These are the per-command oracle cases (Directive 1/3) for P2.
/// </summary>
public class BuiltinFiltersTests
{
    private static OutputFrame Out(string text) => new(StreamTag.Stdout, text);

    // Embedded built-ins only (no user/project dirs).
    private static readonly IReadOnlyList<FilterSpec> Builtins = FilterLibrary.Load(null, null);

    [Fact]
    public void EmbeddedBuiltins_CoverExpectedCommands()
    {
        string[] expected =
        [
            "git/status", "git/log", "git/diff", "git/push", "git/commit", "git/add",
            "dotnet/build", "dotnet/test", "npm/test", "npm/run",
            "cargo/test", "cargo/build", "pytest", "docker/ps", "kubectl/get-pods",
        ];
        foreach (var name in expected)
            Assert.Contains(Builtins, f => f.Name == name);

        Assert.True(Builtins.Count >= 15, $"expected >=15 built-ins, got {Builtins.Count}");
    }

    [Fact]
    public void GitStatus_Clean_ShortCircuitsToCleanMarker()
    {
        var r = FilterEngine.Apply("git status", 0, false,
            [Out("On branch main\n"), Out("nothing to commit, working tree clean\n")], Builtins);

        Assert.Contains("clean ✓", r);
        Assert.DoesNotContain("On branch main", r);
    }

    [Fact]
    public void GitPush_Success_EmitsTerseConfirmation()
    {
        var r = FilterEngine.Apply("git push", 0, false,
        [
            Out("Enumerating objects: 5, done.\n"),
            Out("Counting objects: 100% (5/5), done.\n"),
            Out("Writing objects: 100% (3/3), 312 bytes, done.\n"),
            Out("To github.com:user/repo.git\n"),
        ], Builtins);

        Assert.Contains("ok ✓ pushed", r);
        Assert.DoesNotContain("Enumerating objects", r);
    }

    [Fact]
    public void GitAdd_Success_EmitsOk()
    {
        var r = FilterEngine.Apply("git add", 0, false, [Out("")], Builtins);
        Assert.Contains("ok ✓", r);
    }

    [Fact]
    public void GitCommit_DropsFileStats_KeepsHeadline()
    {
        var r = FilterEngine.Apply("git commit", 0, false,
        [
            Out("[main a1b2c3d] my commit message\n"),
            Out(" 3 files changed, 10 insertions(+), 2 deletions(-)\n"),
            Out(" create mode 100644 src/new.cs\n"),
        ], Builtins);

        Assert.Contains("my commit message", r);
        Assert.DoesNotContain("files changed", r);
        Assert.DoesNotContain("create mode", r);
    }

    [Fact]
    public void DotnetBuild_Success_ShortCircuits()
    {
        var r = FilterEngine.Apply("dotnet build", 0, false,
        [
            Out("Determining projects to restore...\n"),
            Out("Build succeeded.\n"),
            Out("    0 Warning(s)\n"),
        ], Builtins);

        Assert.Contains("Build succeeded ✓", r);
        Assert.DoesNotContain("Determining projects", r);
    }

    [Fact]
    public void DotnetBuild_Failure_KeepsErrorsDropsRestoreNoise()
    {
        var frames = new List<OutputFrame>();
        for (var i = 0; i < 20; i++) frames.Add(Out("Determining projects to restore...\n"));
        frames.Add(Out("src/App.cs(42,5): error CS1002: ; expected\n"));
        frames.Add(Out("Build FAILED.\n"));

        var r = FilterEngine.Apply("dotnet build", 1, false, frames, Builtins);

        Assert.Contains("error CS1002", r);
        Assert.Contains("Build FAILED", r);
        Assert.DoesNotContain("Determining projects", r);
    }

    [Fact]
    public void DotnetTest_KeepsFailuresAndSummary()
    {
        var r = FilterEngine.Apply("dotnet test", 1, false,
        [
            Out("  Passed Foo.Bar.Test1 [2 ms]\n"),
            Out("  Failed Foo.Bar.Test2 [5 ms]\n"),
            Out("  Assert.Equal() Failure\n"),
            Out("Failed!  - Failed: 1, Passed: 1, Total: 2\n"),
        ], Builtins);

        Assert.Contains("Failed Foo.Bar.Test2", r);
        Assert.Contains("Total: 2", r);
        Assert.DoesNotContain("Passed Foo.Bar.Test1", r);
    }

    [Fact]
    public void CargoTest_KeepsFailuresDropsCompiling()
    {
        var r = FilterEngine.Apply("cargo test", 1, false,
        [
            Out("   Compiling proc-macro2 v1.0.92\n"),
            Out("   Compiling serde v1.0.217\n"),
            Out("thread 'tests::it' panicked at src/lib.rs:18\n"),
            Out("test result: FAILED. 14 passed; 1 failed\n"),
        ], Builtins);

        Assert.Contains("panicked", r);
        Assert.Contains("test result: FAILED", r);
        Assert.DoesNotContain("Compiling", r);
    }

    [Fact]
    public void Pytest_KeepsFailuresDropsHeader()
    {
        var r = FilterEngine.Apply("pytest", 1, false,
        [
            Out("platform linux -- Python 3.11\n"),
            Out("collected 5 items\n"),
            Out("test_math.py::test_add FAILED\n"),
            Out("E   assert 1 == 2\n"),
            Out("1 failed, 4 passed in 0.12s\n"),
        ], Builtins);

        Assert.Contains("FAILED", r);
        Assert.Contains("1 failed, 4 passed", r);
        Assert.DoesNotContain("platform linux", r);
    }

    [Fact]
    public void NpmTest_KeepsFailingDropsPassing()
    {
        var r = FilterEngine.Apply("npm test", 1, false,
        [
            Out("✓ adds numbers\n"),
            Out("✕ subtracts numbers\n"),
            Out("Tests: 1 failed, 1 passed\n"),
        ], Builtins);

        Assert.Contains("✕ subtracts numbers", r);
        Assert.Contains("Tests: 1 failed", r);
        Assert.DoesNotContain("adds numbers", r);
    }

    [Fact]
    public void DockerPs_DropsHeaderRow()
    {
        var r = FilterEngine.Apply("docker ps", 0, false,
        [
            Out("CONTAINER ID   IMAGE     STATUS\n"),
            Out("abc123         nginx     Up 2 hours\n"),
        ], Builtins);

        Assert.DoesNotContain("CONTAINER ID", r);
        Assert.Contains("nginx", r);
    }

    [Fact]
    public void UnmatchedCommand_FallsBackToGenericDigest()
    {
        // A command with no built-in filter still gets the plain digest (no exception).
        var frames = new[] { Out("hello\n") };
        var viaEngine = FilterEngine.Apply("some-unknown-tool arg", 0, false, frames, Builtins);
        var viaCompactor = OutputCompactor.CompactCommandOutput("some-unknown-tool arg", 0, false, frames);

        Assert.Equal(viaCompactor, viaEngine);
    }
}
