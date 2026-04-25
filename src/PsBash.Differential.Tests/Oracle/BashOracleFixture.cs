using System.Diagnostics;

namespace PsBash.Differential.Tests.Oracle;

/// <summary>
/// Runs a bash script through both <c>bash -c</c> and <c>ps-bash -c</c>,
/// capturing stdout, stderr, exit code, and wall time for each.
///
/// RELIABILITY CONTRACT (process_spawn_contract memory note):
///   Every spawn uses a configurable timeout (default 5 s).
///   On timeout → Kill(entireProcessTree: true) in finally, then throw.
///   The Kill is in a finally block so it fires even when the await is cancelled.
/// </summary>
public sealed class BashOracleFixture
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Path to the bash binary. Resolved once at construction.
    /// Null when bash is not available on this platform.
    /// </summary>
    public string? BashPath { get; }

    /// <summary>
    /// Path to the ps-bash binary (Debug build from the Shell project output).
    /// Null when the binary has not been built yet.
    /// </summary>
    public string? PsBashPath { get; }

    public BashOracleFixture()
    {
        var host = BashLocator.Find();
        // BashPath is used by legacy callers; expose the native path when available.
        // For WSL, we expose "wsl.exe" so callers that check BashPath != null see it.
        BashPath = host.IsAvailable ? host.Path : null;
        PsBashPath = FindPsBash();
    }

    private static string? FindPsBash()
    {
        // Locate the ps-bash binary relative to the test assembly output directory.
        // The Differential.Tests project is in src/PsBash.Differential.Tests/,
        // and the Shell project builds ps-bash into src/PsBash.Shell/bin/Debug/net10.0/.
        var baseDir = AppContext.BaseDirectory;

        // Navigate from bin/Debug/net10.0 up to the repo root.
        // TrimEnd the separator first so that a trailing slash does not cause
        // the first GetDirectoryName call to return the same directory (merely
        // stripping the trailing slash without moving up a level).
        var repoRoot = baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int i = 0; i < 5; i++)
        {
            var parent = Path.GetDirectoryName(repoRoot);
            if (parent is null) break;
            repoRoot = parent;
        }

        // On non-Windows (Linux/WSL/macOS) prefer the ELF binary over the PE .exe
        // so that Process.Start can exec it directly.  The .exe variant is returned
        // first only on Windows where it is the native binary.
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

    /// <summary>
    /// Runs <paramref name="script"/> through bash and ps-bash, returning both results.
    /// </summary>
    /// <param name="script">The bash script to execute via <c>-c</c>.</param>
    /// <param name="timeout">Per-process timeout; defaults to <see cref="DefaultTimeout"/>.</param>
    /// <param name="env">Additional environment variables to set for both spawns.</param>
    public async Task<(OracleResult Bash, OracleResult PsBash)> RunBothAsync(
        string script,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var effective = timeout ?? DefaultTimeout;
        var host = BashLocator.Find();
        if (!host.IsAvailable)
            throw new InvalidOperationException("RunBothAsync called but no bash host is available. Check BashLocator.Find() before calling.");

        // Build the bash PSI using BashLocator so WSL gets the correct -e bash -c args.
        var bashPsi = BashLocator.BuildPsi(host, script)!;
        if (env is not null)
            foreach (var (k, v) in env)
                bashPsi.Environment[k] = v;

        var bashTask = RunOnePsiAsync(bashPsi, effective);
        var psBashTask = RunOneAsync(PsBashPath!, "-c", script, effective, env,
            extraEnv: new Dictionary<string, string> { ["PSBASH_DEBUG"] = "1" });

        await Task.WhenAll(bashTask, psBashTask);
        return (await bashTask, await psBashTask);
    }

    /// <summary>
    /// Runs a process from a pre-built <see cref="ProcessStartInfo"/> and captures output.
    /// Enforces timeout with Kill(entireProcessTree: true) in finally.
    /// </summary>
    public static async Task<OracleResult> RunOnePsiAsync(
        ProcessStartInfo psi,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.UseShellExecute = false;

        if (extraEnv is not null)
            foreach (var (k, v) in extraEnv)
                psi.Environment[k] = v;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start: {psi.FileName}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            process.StandardInput.Close();

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                string partial = string.Empty;
                try { partial = await stdoutTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
                string partialErr = string.Empty;
                try { partialErr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }

                throw new OracleTimeoutException(
                    psi.FileName,
                    string.Join(" ", psi.ArgumentList),
                    timeout,
                    partial,
                    partialErr);
            }

            stopwatch.Stop();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new OracleResult(stdout, stderr, process.ExitCode, stopwatch.ElapsedMilliseconds);
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
    /// Runs a single interpreter with <c>-c script</c> and captures all output.
    /// Enforces timeout with Kill(entireProcessTree: true) in finally.
    /// </summary>
    internal static async Task<OracleResult> RunOneAsync(
        string executable,
        string firstArg,
        string script,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? env = null,
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(firstArg);
        psi.ArgumentList.Add(script);

        if (env is not null)
            foreach (var (k, v) in env)
                psi.Environment[k] = v;

        if (extraEnv is not null)
            foreach (var (k, v) in extraEnv)
                psi.Environment[k] = v;

        var stopwatch = Stopwatch.StartNew();
        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start: {executable}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            process.StandardInput.Close();

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Collect partial output before killing
                string partial = string.Empty;
                try { partial = await stdoutTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
                string partialErr = string.Empty;
                try { partialErr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }

                throw new OracleTimeoutException(
                    executable,
                    script,
                    timeout,
                    partial,
                    partialErr);
            }

            stopwatch.Stop();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new OracleResult(stdout, stderr, process.ExitCode, stopwatch.ElapsedMilliseconds);
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

/// <summary>
/// Thrown when a spawned interpreter does not exit within the oracle timeout.
/// The message contains "oracle timeout" so test output is unambiguous.
/// </summary>
public sealed class OracleTimeoutException : Exception
{
    public string Executable { get; }
    public string Script { get; }
    public TimeSpan Timeout { get; }
    public string PartialStdout { get; }
    public string PartialStderr { get; }

    public OracleTimeoutException(
        string executable,
        string script,
        TimeSpan timeout,
        string partialStdout,
        string partialStderr)
        : base(BuildMessage(executable, script, timeout, partialStdout, partialStderr))
    {
        Executable = executable;
        Script = script;
        Timeout = timeout;
        PartialStdout = partialStdout;
        PartialStderr = partialStderr;
    }

    private static string BuildMessage(
        string executable, string script, TimeSpan timeout,
        string partialStdout, string partialStderr)
        => $"oracle timeout: {Path.GetFileName(executable)} did not exit within " +
           $"{timeout.TotalSeconds:F0}s running script: {script}\n" +
           $"--- partial stdout ---\n{partialStdout}\n" +
           $"--- partial stderr ---\n{partialStderr}";
}
