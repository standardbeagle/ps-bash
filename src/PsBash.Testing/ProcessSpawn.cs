using System.Diagnostics;

namespace PsBash.Testing;

/// <summary>
/// The single, unified process-spawn primitive for every test suite
/// (REFACTOR-3). Replaces the four near-duplicate spawn loops that previously
/// lived in ProcessRunHelper, ModeRunner, and BashOracleFixture.
///
/// RELIABILITY CONTRACT (the reason this is consolidated — QA rubric
/// Directive 13, "process spawn without timeout + kill-tree" is a known-bad):
///
///   1. stdout/stderr are drained with ReadToEndAsync started BEFORE
///      WaitForExitAsync — never block on WaitForExit while a full pipe
///      buffer wedges the child.
///   2. stdin is written (if any) then ALWAYS closed, even if the write
///      throws — a child blocked on stdin EOF must be released.
///   3. The whole wait is bounded by a CancellationTokenSource timeout.
///   4. On timeout: partial stdout/stderr are collected (bounded 5 s each),
///      then SpawnTimeoutException is thrown carrying that partial output.
///   5. Kill(entireProcessTree: true) runs in a finally block so a hung
///      child never orphans its process tree — fires even when the await is
///      cancelled or an exception unwinds.
///
/// Every behaviour knob a suite needs (timeout, env, stdin) is a parameter
/// here; suites do not re-implement the loop.
/// </summary>
public static class ProcessSpawn
{
    /// <summary>
    /// Builds a <see cref="ProcessStartInfo"/> for <paramref name="executable"/>
    /// with the standard redirection flags already set, then appends
    /// <paramref name="arguments"/> via <see cref="ProcessStartInfo.ArgumentList"/>
    /// (no shell quoting pitfalls).
    /// </summary>
    public static ProcessStartInfo BuildPsi(string executable, params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);
        return psi;
    }

    /// <summary>
    /// Runs <paramref name="executable"/> with <paramref name="arguments"/> and
    /// captures all output. Convenience overload that builds the PSI for you.
    /// </summary>
    public static Task<SpawnResult> RunAsync(
        string executable,
        string[] arguments,
        TimeSpan timeout,
        string? stdinContent = null,
        IReadOnlyDictionary<string, string>? env = null,
        bool canonicalizeEnv = false)
        => RunAsync(BuildPsi(executable, arguments), timeout, stdinContent, env, canonicalizeEnv);

    /// <summary>
    /// Runs a process from a pre-built <see cref="ProcessStartInfo"/> and
    /// captures stdout, stderr, exit code, and wall time.
    /// </summary>
    /// <param name="psi">
    /// The start info. Redirection flags are forced on; the caller's
    /// FileName/ArgumentList/Environment are respected.
    /// </param>
    /// <param name="timeout">Hard cap on the run. On expiry the process tree is killed.</param>
    /// <param name="stdinContent">Optional stdin payload. stdin is always closed after.</param>
    /// <param name="env">
    /// Extra environment variables applied on top of the inherited (or, with
    /// <paramref name="canonicalizeEnv"/>, cleared) environment.
    /// </param>
    /// <param name="canonicalizeEnv">
    /// When true, the inherited environment block is cleared before
    /// <paramref name="env"/> is applied, so the child runs with a known,
    /// reproducible environment (no leakage from the test host's shell).
    /// When false (default), <paramref name="env"/> is layered on top of the
    /// inherited environment — matching the historical per-suite behaviour.
    /// </param>
    /// <exception cref="SpawnTimeoutException">
    /// Thrown when the process does not exit within <paramref name="timeout"/>.
    /// </exception>
    public static async Task<SpawnResult> RunAsync(
        ProcessStartInfo psi,
        TimeSpan timeout,
        string? stdinContent = null,
        IReadOnlyDictionary<string, string>? env = null,
        bool canonicalizeEnv = false)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.UseShellExecute = false;

        if (canonicalizeEnv)
            psi.Environment.Clear();

        if (env is not null)
            foreach (var (k, v) in env)
                psi.Environment[k] = v;

        var stopwatch = Stopwatch.StartNew();
        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {psi.FileName}");

        // Drain both pipes concurrently, started BEFORE WaitForExit so a full
        // pipe buffer can never wedge the child.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            // Write stdin then ALWAYS close it — a child blocked on stdin EOF
            // must be released even if the write itself throws.
            try
            {
                if (stdinContent is not null)
                    await process.StandardInput.WriteAsync(stdinContent);
            }
            finally
            {
                process.StandardInput.Close();
            }

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Kill first so the drain tasks can complete, then collect
                // whatever partial output made it through (bounded so a wedged
                // pipe does not hang the timeout path too).
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch { /* already exited or access denied */ }

                var partialStdout = string.Empty;
                var partialStderr = string.Empty;
                try { partialStdout = await stdoutTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
                try { partialStderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }

                throw new SpawnTimeoutException(
                    psi.FileName,
                    string.Join(" ", psi.ArgumentList),
                    timeout,
                    partialStdout,
                    partialStderr);
            }

            stopwatch.Stop();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new SpawnResult(process.ExitCode, stdout, stderr, stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { /* already exited */ }
            process.Dispose();
        }
    }
}
