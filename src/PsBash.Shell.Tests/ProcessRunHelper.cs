using System.Diagnostics;

namespace PsBash.Shell.Tests;

/// <summary>
/// Helpers for spawning `dotnet run` / `ps-bash` child processes in integration tests.
///
/// CRITICAL RELIABILITY CONTRACT:
/// Every test that spawns an external process MUST use these helpers (or replicate
/// their pattern) so that a hung command NEVER orphans the dotnet-run → ps-bash → pwsh
/// process tree for hours. See Reliability C (Dart task YqMcVdyfYKzt).
///
/// Contract:
///   1. Every wait uses a 30-second default timeout (configurable).
///   2. On timeout → throw <see cref="TimeoutException"/> with partial stdout/stderr,
///      so CI failures surface clearly rather than hanging the runner.
///   3. In a `finally` block → if !HasExited, call Kill(entireProcessTree: true) then Dispose.
/// </summary>
internal static class ProcessRunHelper
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        ProcessStartInfo psi,
        string? stdinContent = null,
        TimeSpan? timeout = null)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.UseShellExecute = false;

        var effectiveTimeout = timeout ?? DefaultTimeout;
        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start process");

        // Start reading stdout/stderr concurrently so large outputs do not deadlock.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            // Always close stdin — either after writing the requested content,
            // or immediately when no content is provided. Leaving stdin open on
            // a child that happens to read it (e.g. interactive mode triggered
            // by arg misparsing) causes an indefinite hang rather than a clean
            // EOF. This EOF guarantee is part of the reliability contract.
            try
            {
                if (stdinContent is not null)
                    await process.StandardInput.WriteAsync(stdinContent);
            }
            finally
            {
                process.StandardInput.Close();
            }

            using var cts = new CancellationTokenSource(effectiveTimeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Timeout. Capture whatever partial output we have and fail loudly.
                string partialStdout = string.Empty;
                string partialStderr = string.Empty;
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch { /* already exited or access denied — ignore */ }

                // After Kill, the read tasks should complete quickly.
                try { partialStdout = await stdoutTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
                try { partialStderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }

                throw new TimeoutException(
                    $"Process did not exit within {effectiveTimeout.TotalSeconds:F0}s; " +
                    $"entire process tree was killed.\n" +
                    $"--- partial stdout ---\n{partialStdout}\n" +
                    $"--- partial stderr ---\n{partialStderr}");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return (process.ExitCode, stdout, stderr);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch { /* already exited — ignore */ }
            process.Dispose();
        }
    }
}
