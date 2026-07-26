namespace PsBash.Testing;

/// <summary>
/// Thrown when a spawned process EXITED normally but its stdout/stderr pipes never
/// reached EOF within <see cref="PsBash.Testing.ProcessSpawn.DrainGrace"/>.
///
/// This is not a slow child — the child is already gone and its writes are flushed.
/// It means some OTHER process inherited the write end of the pipe and outlived it.
/// In ps-bash the near-certain culprit is a persisted <c>ps-bash-host</c> daemon that
/// inherited the launcher's stdout: the launcher exits, but the parent never sees EOF.
/// (See <c>IpcWorker.SpawnAndWaitAsync</c>, which severs stdio inheritance precisely to
/// prevent this; a fresh occurrence means a spawn path is bypassing that.)
///
/// Before this existed the drain simply blocked forever, hanging the whole test run
/// with no message — the failure mode recorded in
/// <c>docs/bugs/corpus-sweep-2026-07-25.md</c> as "running PsBash.Host.Tests
/// immediately after another suite can hang the test harness".
/// </summary>
public class SpawnDrainTimeoutException : TimeoutException
{
    /// <summary>The executable that was spawned.</summary>
    public string Executable { get; }

    /// <summary>The arguments passed to the executable, space-joined.</summary>
    public string Arguments { get; }

    /// <summary>The exit code the process reported before the drain stalled.</summary>
    public int ExitCode { get; }

    /// <summary>The post-exit drain grace that elapsed.</summary>
    public TimeSpan DrainGrace { get; }

    public SpawnDrainTimeoutException(string executable, string arguments, int exitCode, TimeSpan drainGrace)
        : base(BuildMessage(executable, arguments, exitCode, drainGrace))
    {
        Executable = executable;
        Arguments = arguments;
        ExitCode = exitCode;
        DrainGrace = drainGrace;
    }

    private static string BuildMessage(string executable, string arguments, int exitCode, TimeSpan drainGrace) =>
        $"""
        Process exited (code {exitCode}) but its stdout/stderr never reached EOF within {drainGrace.TotalSeconds:0.#}s.

          executable : {executable}
          arguments  : {arguments}

        A surviving grandchild is holding the write end of the pipe — most likely a
        persisted ps-bash-host that inherited the launcher's stdio. Kill stray hosts and
        check the spawn path severs inheritance (IpcWorker.SpawnAndWaitAsync).
        Raise the grace with PSBASH_TEST_DRAIN_GRACE_SEC if the machine is merely slow.
        """;
}
