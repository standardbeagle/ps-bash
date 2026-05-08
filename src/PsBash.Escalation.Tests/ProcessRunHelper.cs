using System.Diagnostics;

namespace PsBash.Escalation.Tests;

/// <summary>
/// Helpers for spawning ps-bash child processes in escalation/fault-injection tests.
///
/// RELIABILITY CONTRACT: every spawn uses a timeout + Kill(entireProcessTree: true)
/// in finally so a hung command never orphans the process tree.
/// </summary>
internal static class ProcessRunHelper
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private static readonly string ProjectDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "PsBash.Shell"));

    private static readonly string WorkerScript = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "scripts", "ps-bash-worker.ps1"));

    private static string FindPsBashBinary()
    {
        var preferExe = OperatingSystem.IsWindows();
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var binDir = Path.Combine(dir.FullName, "src", "PsBash.Shell", "bin");
            if (Directory.Exists(binDir))
            {
                string? fallback = null;
                foreach (var exe in Directory.EnumerateFiles(binDir, "ps-bash*", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(exe);
                    var isExe = string.Equals(name, "ps-bash.exe", StringComparison.OrdinalIgnoreCase);
                    var isNoExt = string.Equals(name, "ps-bash", StringComparison.OrdinalIgnoreCase);
                    if (!isExe && !isNoExt)
                        continue;

                    if (preferExe && isExe)
                        return exe;
                    if (!preferExe && isNoExt)
                        return exe;
                    fallback ??= exe;
                }
                if (fallback is not null)
                    return fallback;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("ps-bash binary not found -- build PsBash.Shell first");
    }

    public static ProcessStartInfo BuildPsi(string[] arguments)
    {
        var psi = new ProcessStartInfo { FileName = FindPsBashBinary() };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        psi.Environment["PSBASH_WORKER"] = WorkerScript;
        return psi;
    }

    public static Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string[] arguments,
        TimeSpan? timeout = null)
    {
        var psi = BuildPsi(arguments);
        return RunAsync(psi, stdinContent: null, timeout: timeout);
    }

    public static Task<(int ExitCode, string Stdout, string Stderr)> RunWithStdinAsync(
        string stdinContent,
        string[] arguments,
        TimeSpan? timeout = null)
    {
        var psi = BuildPsi(arguments);
        return RunAsync(psi, stdinContent: stdinContent, timeout: timeout);
    }

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

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
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
                string partialStdout = string.Empty;
                string partialStderr = string.Empty;
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch { /* already exited or access denied */ }

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
                    process.Kill(entireProcessTree: true);
            }
            catch { /* already exited */ }
            process.Dispose();
        }
    }
}
