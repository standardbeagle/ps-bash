using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// xUnit collection for every test class that MOVES or DEPENDS ON the process-global
/// <see cref="Environment.CurrentDirectory"/>. Membership serializes those classes
/// against each other; xUnit runs collections in parallel by default, and each class
/// otherwise gets its own runspace, which isolates the PowerShell location but cannot
/// isolate a process-wide value.
///
/// <para><b>Why this became necessary.</b> <c>pushd</c>/<c>popd</c> now write BOTH
/// halves of the working directory (see
/// <c>InvokeBashPushdCommand.SyncProcessWorkingDirectory</c>) — required, because
/// moving only the PowerShell half made the fused lane stream the wrong file at exit 0.
/// The cost is that the dir-stack tests mutate process-global state, so running them
/// beside <see cref="LineStreamCatFileParityTests"/> (which asserts on relative-path
/// resolution) is a race, not a theoretical one. Per <c>.claude/rules/qa-rubric.md</c>
/// Directive 2 the flake bar is zero, so the classes are bound together here rather
/// than left to interleave.</para>
///
/// <para><b>Parallelization is DISABLED, not just serialized within the collection.</b>
/// Binding the classes to each other only stopped them overlapping one another. Every
/// OTHER class still ran alongside them, and each of those calls
/// <see cref="SharedPwshFixture"/>'s reset, which writes the process-global
/// <see cref="Environment.CurrentDirectory"/> back to its baseline. That snapped the
/// process cwd out from under a member mid-test (<c>Cd_Dash_ReturnsToThePreviousDirectory_BothHalves</c>
/// read the fixture baseline instead of its temp dir; <c>Dirs_PFlag_EmitsOnePerLine</c>
/// could not delete a directory another thread was sitting in). With
/// <c>DisableParallelization = true</c> xUnit runs this collection alone, after every
/// parallel collection has finished, so no concurrent writer exists. Pinned by
/// <see cref="ProcessWorkingDirectoryCollectionGuardTests"/>.</para>
///
/// <para>Deliberately NOT an <c>ICollectionFixture</c>: the members keep their own
/// <see cref="SharedPwshFixture"/> (and therefore their own runspace and per-test
/// reset). The only thing being shared is the guarantee that nothing else runs at
/// the same time.</para>
///
/// <para>Add a class here whenever it calls <c>pushd</c>/<c>popd</c>, assigns
/// <see cref="Environment.CurrentDirectory"/>, or asserts on relative-path
/// resolution.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessWorkingDirectoryCollection
{
    public const string Name = "process-working-directory";
}
