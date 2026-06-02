// Serialize the canary suite. Every canary test spawns one or more real ps-bash processes (across
// modes M1-M6). xUnit's default is to run test classes in parallel collections, which fires many
// concurrent process spawns at once — on resource-constrained CI runners (notably macOS and Windows
// GitHub hosts) that contention shows up as flaky "test host process crashed" / 60s spawn timeouts
// even though each test passes in isolation. Running the spawn-heavy tests one at a time removes the
// contention nondeterminism (QA-rubric Directive 6: deterministic harness, no flake) at a modest
// wall-clock cost that stays well under the workflow's 10-minute budget.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
