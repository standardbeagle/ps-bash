using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace PsBash.Canary.Tests;

/// <summary>
/// Mode identifiers for the QA rubric Directive 4 mode interaction matrix.
/// M4 (interactive TTY) is intentionally excluded — too flaky for CI.
/// </summary>
public enum Mode
{
    M1_CFlag = 1,       // ps-bash -c script
    M2_StdinPipe = 2,   // echo script | ps-bash
    M3_FileArg = 3,     // ps-bash script.sh
    M5_InvokeEval = 5,  // Invoke-BashEval cmdlet (in-process)
    M6_InvokeSource = 6 // Invoke-BashSource cmdlet (in-process, .sh file)
}

/// <summary>
/// Captured result of running a bash script in one execution mode.
/// </summary>
public sealed record ModeResult(
    Mode Mode,
    string Stdout,
    string Stderr,
    int ExitCode,
    long WallMs);

/// <summary>
/// Dispatches a bash script across all active modes (M1, M2, M3, M5, M6).
///
/// PROCESS SPAWN CONTRACT (process_spawn_contract memory note):
///   Every spawn uses a 60 s hard cap timeout.
///   On timeout: Kill(entireProcessTree: true) in finally, then fail.
///   The Kill fires even if the await is cancelled.
/// </summary>
public sealed class ModeRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly string? _psBashPath;

    public ModeRunner()
    {
        _psBashPath = FindPsBash();
    }

    /// <summary>
    /// Path to the ps-bash binary. Null when not built yet — spawn modes skip.
    /// </summary>
    public string? PsBashPath => _psBashPath;

    /// <summary>
    /// Runs <paramref name="script"/> in all available modes.
    /// Modes whose prerequisite is unavailable return a skip sentinel (ExitCode = -999).
    /// </summary>
    public async Task<IReadOnlyList<ModeResult>> RunAllAsync(
        string script,
        TimeSpan? timeout = null)
    {
        var effective = timeout ?? DefaultTimeout;
        var results = new List<ModeResult>();

        var tasks = new List<Task<ModeResult>>();

        if (_psBashPath != null)
        {
            tasks.Add(RunM1Async(script, effective));
            tasks.Add(RunM2Async(script, effective));
            tasks.Add(RunM3Async(script, effective));
        }
        else
        {
            results.Add(new ModeResult(Mode.M1_CFlag, "", "ps-bash binary not found", -999, 0));
            results.Add(new ModeResult(Mode.M2_StdinPipe, "", "ps-bash binary not found", -999, 0));
            results.Add(new ModeResult(Mode.M3_FileArg, "", "ps-bash binary not found", -999, 0));
        }

        tasks.Add(RunM5Async(script, effective));
        tasks.Add(RunM6Async(script, effective));

        var completed = await Task.WhenAll(tasks);
        results.AddRange(completed);

        return results;
    }

    // -------------------------------------------------------------------------
    // M1: ps-bash -c script
    // -------------------------------------------------------------------------
    private async Task<ModeResult> RunM1Async(string script, TimeSpan timeout)
    {
        var psi = BuildPsi(_psBashPath!);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        var result = await SpawnAsync(psi, stdinContent: null, timeout);
        return new ModeResult(Mode.M1_CFlag, result.Stdout, result.Stderr, result.ExitCode, result.WallMs);
    }

    // -------------------------------------------------------------------------
    // M2: echo script | ps-bash
    // -------------------------------------------------------------------------
    private async Task<ModeResult> RunM2Async(string script, TimeSpan timeout)
    {
        var psi = BuildPsi(_psBashPath!);
        // No arguments — reads from stdin
        var result = await SpawnAsync(psi, stdinContent: script, timeout);
        return new ModeResult(Mode.M2_StdinPipe, result.Stdout, result.Stderr, result.ExitCode, result.WallMs);
    }

    // -------------------------------------------------------------------------
    // M3: ps-bash script.sh (file-arg execution)
    // Writes the script to a temp .sh file and passes the path as the first
    // argument to ps-bash. This exercises the real M3 code path in Program.cs.
    // -------------------------------------------------------------------------
    private async Task<ModeResult> RunM3Async(string script, TimeSpan timeout)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "ps-bash", $"canary-m3-{Guid.NewGuid()}.sh");
        Directory.CreateDirectory(Path.GetDirectoryName(tempFile)!);
        try
        {
            await File.WriteAllTextAsync(tempFile, script);
            var psi = BuildPsi(_psBashPath!);
            psi.ArgumentList.Add(tempFile);
            var result = await SpawnAsync(psi, stdinContent: null, timeout);
            return new ModeResult(Mode.M3_FileArg, result.Stdout, result.Stderr, result.ExitCode, result.WallMs);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    // -------------------------------------------------------------------------
    // M5: Invoke-BashEval cmdlet (in-process PowerShell SDK)
    // -------------------------------------------------------------------------
    private async Task<ModeResult> RunM5Async(string script, TimeSpan timeout)
    {
        return await Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var pwsh = CanaryPwshFixture.Create();
                pwsh.Commands.Clear();

                // Check whether Invoke-BashEval is available
                pwsh.AddScript("Get-Command Invoke-BashEval -ErrorAction SilentlyContinue");
                var cmdCheck = pwsh.Invoke();
                pwsh.Commands.Clear();
                if (cmdCheck.Count == 0)
                    return new ModeResult(Mode.M5_InvokeEval, "", "Invoke-BashEval not available", -999, 0);

                // Clear errors that accumulated during fixture setup so we only see script errors
                pwsh.Streams.Error.Clear();

                pwsh.AddCommand("Invoke-BashEval").AddParameter("Source", script);
                var results = pwsh.Invoke();
                sw.Stop();

                var stdout = string.Join("\n", results.Select(r => r.ToString()));
                var stderr = string.Join("\n", pwsh.Streams.Error.Select(e => e.ToString()));
                var exitCode = pwsh.Streams.Error.Count > 0 ? 1 : 0;

                return new ModeResult(Mode.M5_InvokeEval, stdout, stderr, exitCode, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new ModeResult(Mode.M5_InvokeEval, "", ex.Message, 1, sw.ElapsedMilliseconds);
            }
        });
    }

    // -------------------------------------------------------------------------
    // M6: Invoke-BashSource cmdlet (in-process, reads from .sh file)
    // -------------------------------------------------------------------------
    private async Task<ModeResult> RunM6Async(string script, TimeSpan timeout)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "ps-bash", $"canary-m6-{Guid.NewGuid()}.sh");
        Directory.CreateDirectory(Path.GetDirectoryName(tempFile)!);
        try
        {
            await File.WriteAllTextAsync(tempFile, script);
            return await Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    using var pwsh = CanaryPwshFixture.Create();
                    pwsh.Commands.Clear();

                    // Check whether Invoke-BashSource is available
                    pwsh.AddScript("Get-Command Invoke-BashSource -ErrorAction SilentlyContinue");
                    var cmdCheck = pwsh.Invoke();
                    pwsh.Commands.Clear();
                    if (cmdCheck.Count == 0)
                        return new ModeResult(Mode.M6_InvokeSource, "", "Invoke-BashSource not available", -999, 0);

                    pwsh.Streams.Error.Clear();
                    pwsh.AddCommand("Invoke-BashSource").AddParameter("Path", tempFile);
                    var results = pwsh.Invoke();
                    sw.Stop();

                    var stdout = string.Join("\n", results.Select(r => r.ToString()));
                    var stderr = string.Join("\n", pwsh.Streams.Error.Select(e => e.ToString()));
                    var exitCode = pwsh.Streams.Error.Count > 0 ? 1 : 0;

                    return new ModeResult(Mode.M6_InvokeSource, stdout, stderr, exitCode, sw.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    return new ModeResult(Mode.M6_InvokeSource, "", ex.Message, 1, sw.ElapsedMilliseconds);
                }
            });
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private static ProcessStartInfo BuildPsi(string executable)
    {
        return new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
    }

    private static async Task<(string Stdout, string Stderr, int ExitCode, long WallMs)> SpawnAsync(
        ProcessStartInfo psi,
        string? stdinContent,
        TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {psi.FileName}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            if (stdinContent != null)
            {
                await process.StandardInput.WriteAsync(stdinContent);
            }
            process.StandardInput.Close();

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"Process {Path.GetFileName(psi.FileName)} did not exit within {timeout.TotalSeconds:F0}s");
            }

            sw.Stop();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return (stdout, stderr, process.ExitCode, sw.ElapsedMilliseconds);
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

    /// <summary>
    /// Locates the ps-bash binary in the Debug or Release build output.
    /// Mirrors BashOracleFixture.FindPsBash() — do not reference that project directly.
    /// </summary>
    private static string? FindPsBash()
    {
        var baseDir = AppContext.BaseDirectory;

        // Navigate up from bin/Debug/net10.0 to the repo root (5 levels up).
        var repoRoot = baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int i = 0; i < 5; i++)
        {
            var parent = Path.GetDirectoryName(repoRoot);
            if (parent is null) break;
            repoRoot = parent;
        }

        string[] candidates;
        if (OperatingSystem.IsWindows())
        {
            candidates = new[]
            {
                Path.Combine(repoRoot, "src", "PsBash.Shell", "bin", "Debug", "net10.0", "ps-bash.exe"),
                Path.Combine(repoRoot, "src", "PsBash.Shell", "bin", "Debug", "net10.0", "ps-bash"),
                Path.Combine(repoRoot, "src", "PsBash.Shell", "bin", "Release", "net10.0", "ps-bash.exe"),
                Path.Combine(repoRoot, "src", "PsBash.Shell", "bin", "Release", "net10.0", "ps-bash"),
            };
        }
        else
        {
            candidates = new[]
            {
                Path.Combine(repoRoot, "src", "PsBash.Shell", "bin", "Debug", "net10.0", "ps-bash"),
                Path.Combine(repoRoot, "src", "PsBash.Shell", "bin", "Debug", "net10.0", "ps-bash.exe"),
                Path.Combine(repoRoot, "src", "PsBash.Shell", "bin", "Release", "net10.0", "ps-bash"),
                Path.Combine(repoRoot, "src", "PsBash.Shell", "bin", "Release", "net10.0", "ps-bash.exe"),
            };
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
