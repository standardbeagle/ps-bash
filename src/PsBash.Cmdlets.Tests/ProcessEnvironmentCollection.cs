using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// xUnit collection for every test class that WRITES the process environment: through
/// <see cref="PsBash.Testing.ProcessEnv"/> in C#, or a script it runs that assigns <c>$env:NAME</c> (a runspace's
/// <c>$env:</c> is the process environment). The environment is one per process, and xUnit runs collections in
/// parallel, so a write in one class is read by whatever runs beside it: GrepOptionsGnuTests set <c>TERM</c> and
/// <c>GREP_COLORS</c> and cleared them after every test, and native <c>tput</c> in
/// <c>InvokeBashTputCommandTests</c> printed another terminal's bytes, and <c>GrepGnuBehaviorTests</c>'
/// <c>--color</c> lost its <c>\e[K</c> (build3 full runs).
///
/// <para>With <c>DisableParallelization = true</c> xUnit runs this collection alone, after every parallel collection
/// has finished, so no class reads the environment while a member has it changed. A member restores each value it
/// changed to what it was BEFORE (<see cref="PsBash.Testing.ProcessEnv"/>), never null: deleting <c>TERM</c> or
/// <c>PATH</c> broke the rest of the run. A class already in another run-alone collection
/// (<see cref="ProcessWorkingDirectoryCollection"/>, <see cref="WallClockBoundCollection"/>) stays there: those run
/// alone too. Replaces the old <c>PsBashSearchEnv</c> collection, which serialized grep/rg classes only against each
/// other.</para>
///
/// <para>Guard: <see cref="ProcessEnvironmentGuardTests"/> (<see cref="PsBash.Testing.ProcessEnvironmentGuard"/>).
/// Keep a big class parallel by moving its few environment-writing tests into a small member class.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "process-environment";
}
