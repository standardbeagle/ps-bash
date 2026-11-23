namespace PsBash.Spec.Tests;

/// <summary>
/// xUnit collection for every test class that WRITES the process environment (through
/// <see cref="PsBash.Testing.ProcessEnv"/>, which restores the value from before the test). The environment is one per
/// process, so a write in one class is read by whatever runs beside it. With <c>DisableParallelization = true</c> this
/// collection runs alone whatever xunit.runner.json says (the guard reads attributes, not the runner config). Guard:
/// <see cref="ProcessEnvironmentGuardTests"/>.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "process-environment";
}
