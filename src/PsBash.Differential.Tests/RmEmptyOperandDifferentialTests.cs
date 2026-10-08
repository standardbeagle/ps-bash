using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// DATA LOSS regression (released 0.11.2): PowerShell resolves an EMPTY path to the current
/// directory, so `rm -rf "$unset"` deleted everything in the cwd. bash removes nothing. Each
/// scenario runs in a fresh mktemp dir (FsStateOracle) and compares stdout, exit status and the
/// resulting tree against real bash.
/// </summary>
public class RmEmptyOperandDifferentialTests
{
    private static readonly string Fixture =
        FsStateOracle.Tree(("file.txt", "keep"), ("sub/inner.txt", "keep"), ("gone.txt", "x"));

    [SkippableFact]
    public Task Rm_RecursiveForce_UnsetVariable_DeletesNothing()
        => FsStateOracle.EqualAsync(Fixture, "rm -rf \"$unset_var\"; echo rc=$?", compareStderr: true);

    [SkippableFact]
    public Task Rm_Recursive_EmptyLiteral_ErrorsAndDeletesNothing()
        => FsStateOracle.EqualAsync(Fixture, "rm -r ''; echo rc=$?", compareStderr: true);

    [SkippableFact]
    public Task Rm_EmptyBesideRealOperand_RemovesOnlyTheRealOne()
        => FsStateOracle.EqualAsync(Fixture, "rm -rf \"\" gone.txt; echo rc=$?", compareStderr: true);
}
