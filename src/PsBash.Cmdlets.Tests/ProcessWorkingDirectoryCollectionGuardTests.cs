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
}
