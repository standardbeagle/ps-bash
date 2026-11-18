using System.Reflection;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pins the process-working-directory collection to run with parallelization
/// disabled. Serializing its classes only against EACH OTHER was not enough:
/// every other class's <see cref="SharedPwshFixture"/> reset writes the
/// process-global <see cref="Environment.CurrentDirectory"/> back to its baseline,
/// and running beside them flaked <c>Cd_Dash_ReturnsToThePreviousDirectory_BothHalves</c>
/// and <c>Dirs_PFlag_EmitsOnePerLine</c> in full-suite gate runs.
/// </summary>
public class ProcessWorkingDirectoryCollectionGuardTests
{
    [Fact]
    public void Collection_DisablesParallelization()
    {
        var definition = typeof(ProcessWorkingDirectoryCollection)
            .GetCustomAttribute<CollectionDefinitionAttribute>();

        Assert.NotNull(definition);
        Assert.True(definition!.DisableParallelization,
            "ProcessWorkingDirectoryCollection must set DisableParallelization = true: its classes read and "
            + "write the process-global Environment.CurrentDirectory, which every other class's fixture reset "
            + "also writes.");
    }

    // A write of the process-global cwd, from C# or from the PowerShell a test runs.
    private static readonly System.Text.RegularExpressions.Regex ProcessCwdWrite = new(
        @"(?:\bEnvironment\.CurrentDirectory|\[(?:System\.)?Environment\]::CurrentDirectory)\s*=(?!=)|\bDirectory\.SetCurrentDirectory\(",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// REGRESSION (Windows fleet s22k): <c>ConvertToBashWordsTests</c> set the process cwd to its temp dir outside this
    /// collection, so a parallel class captured or restored that cwd and the test's own cleanup could not delete the dir
    /// ("being used by another process"). Six classes wrote the process cwd unserialized; membership was a doc comment.
    /// </summary>
    [Fact]
    public void EveryTestFileThatWritesTheProcessCwd_IsInTheCollection()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PsBash.Cmdlets.Tests.csproj"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir!.FullName, "*.cs", SearchOption.AllDirectories))
        {
            var sep = Path.DirectorySeparatorChar;
            if (file.Contains($"{sep}bin{sep}") || file.Contains($"{sep}obj{sep}")) continue;
            // The fixture reset is the baseline writer the collection is serialized against.
            if (Path.GetFileName(file) is "PwshTestFixture.cs" or nameof(ProcessWorkingDirectoryCollectionGuardTests) + ".cs") continue;
            var text = File.ReadAllText(file);
            if (ProcessCwdWrite.IsMatch(text) && !text.Contains("[Collection(ProcessWorkingDirectoryCollection.Name)]", StringComparison.Ordinal))
                offenders.Add(Path.GetFileName(file));
        }
        Assert.True(offenders.Count == 0,
            "A test that writes Environment.CurrentDirectory runs in [Collection(ProcessWorkingDirectoryCollection.Name)]:\n"
            + string.Join("\n", offenders));
    }
}
