using PsBash.Host.Runtime;
using Xunit;

namespace PsBash.Host.Tests;

/// <summary>
/// Consolidates in-process <see cref="SdkWorker"/> setup/teardown for tests
/// that exercise the PowerShell SDK runspace directly. This is the in-process
/// counterpart to <c>PsBash.Testing.PsBashRunner</c>: that helper drives the
/// external-process surface and lives in the dependency-free
/// <c>PsBash.Testing</c> assembly so multiple test projects can consume it;
/// this fixture references <c>PsBash.Host</c> and therefore stays inside
/// <c>PsBash.Host.Tests</c>.
///
/// Two consumption shapes are supported:
/// 1. <see cref="CreateWorker"/> — fresh worker per test, auto-tracked for
///    disposal at fixture teardown. Use this for tests that mutate worker
///    lifecycle (Dispose, HasExited, ModuleLoadCount probes).
/// 2. <see cref="ExecuteCapturedAsync"/> — one-shot helper that wraps the
///    common pattern (create worker, set OutputCallback to a
///    List&lt;string&gt;, execute, return lines+exit) and
///    <see cref="CaptureAsync"/> for the same pattern against a
///    caller-supplied worker.
///
/// Oracle note (qa-rubric Directive 1): SdkWorker behavior is ps-bash-specific
/// (in-process runspace, no bash oracle); hand-written asserts are justified
/// per the exception list.
/// </summary>
public sealed class HostWorkerFixture : IAsyncLifetime
{
    private readonly List<SdkWorker> _trackedWorkers = new();
    private readonly object _gate = new();

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Create a fresh <see cref="SdkWorker"/> tracked for automatic disposal
    /// when the fixture's <see cref="DisposeAsync"/> runs. The caller may
    /// also dispose explicitly (e.g. to assert HasExited / post-dispose
    /// behavior); duplicate disposes are swallowed.
    /// </summary>
    public SdkWorker CreateWorker()
    {
        var worker = SdkWorker.Create();
        lock (_gate)
        {
            _trackedWorkers.Add(worker);
        }
        return worker;
    }

    /// <summary>
    /// Create a worker, attach a line-capturing OutputCallback, execute the
    /// command, and return the captured lines along with the exit code. The
    /// worker is tracked for disposal at fixture teardown.
    /// </summary>
    public async Task<CapturedRun> ExecuteCapturedAsync(string command, CancellationToken ct = default)
    {
        var worker = CreateWorker();
        var lines = new List<string>();
        worker.OutputCallback = lines.Add;
        var exitCode = await worker.ExecuteAsync(command, ct);
        return new CapturedRun(worker, lines, exitCode);
    }

    /// <summary>
    /// Run a script against a caller-supplied <see cref="SdkWorker"/> with a
    /// fresh line buffer, returning the captured lines and exit code. Useful
    /// when a test needs multiple sequential executes against the same worker
    /// (cross-call state).
    /// </summary>
    public static async Task<CapturedRun> CaptureAsync(SdkWorker worker, string command, CancellationToken ct = default)
    {
        var lines = new List<string>();
        worker.OutputCallback = lines.Add;
        var exitCode = await worker.ExecuteAsync(command, ct);
        return new CapturedRun(worker, lines, exitCode);
    }

    public async Task DisposeAsync()
    {
        SdkWorker[] snapshot;
        lock (_gate)
        {
            snapshot = _trackedWorkers.ToArray();
            _trackedWorkers.Clear();
        }

        foreach (var worker in snapshot)
        {
            try
            {
                await worker.DisposeAsync();
            }
            catch
            {
                // Teardown is best-effort: a test that already disposed the
                // worker (HasExited probes, ObjectDisposedException probes)
                // would otherwise mask the test's own assertion. Tracking is
                // for leak prevention, not lifecycle enforcement.
            }
        }
    }

    public sealed record CapturedRun(SdkWorker Worker, List<string> Lines, int ExitCode);
}

// NOTE: this fixture is intentionally NOT registered as an xunit collection
// fixture. The existing "SdkHost" collection name is used by several test
// classes that share the SDK runspace's global static state (e.g.
// SdkRunspace.ModuleLoadCount) and must run sequentially with each other.
// Registering this fixture under a NEW collection name would let xunit run
// the two collections in parallel and race on that static state.
// Registering it under "SdkHost" via [CollectionDefinition] would force every
// existing test class in that collection to accept the fixture in its ctor —
// a much larger blast radius than this task scopes for.
//
// Consumers therefore instantiate HostWorkerFixture as an instance field via
// xunit's IAsyncLifetime on the test class itself, keeping the fixture's
// lifetime aligned with the per-class lifetime that the existing
// [Collection("SdkHost")] tests already use.
