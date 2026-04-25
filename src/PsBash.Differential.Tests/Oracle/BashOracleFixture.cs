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
        BashPath = FindBash();
        PsBashPath = FindPsBash();
    }

    private static string? FindBash()
    {
        // Common locations in priority order
        foreach (var candidate in new[] { "/usr/bin/bash", "/bin/bash", "/usr/local/bin/bash" })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        // On Windows with Git for Windows / WSL tools on PATH
        if (OperatingSystem.IsWindows())
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                var candidate = Path.Combine(dir, "bash.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static string? FindPsBash()
    {
        // Locate the ps-bash binary relative to the test assembly output directory.
        // The Differential.Tests project is in src/PsBash.Differential.Tests/,
        // and the Shell project builds ps-bash into src/PsBash.Shell/bin/Debug/net10.0/.
        var baseDir = AppContext.BaseDirectory;

        // Navigate from bin/Debug/net10.0 up to the repo root (5 levels)
        var repoRoot = baseDir;
        for (int i = 0; i < 5; i++)
        {
            var parent = Path.GetDirectoryName(repoRoot);
            if (parent is null) break;
            repoRoot = parent;
        }

        var candidates = new[]
        {
            Path.Combine(repoRoot, "src", "PsBash.Shell", "bin", "Debug", "net10.0", "ps-bash.exe"),
            Path.Combine(repoRoot, "src", "PsBash.Shell", "bin", "Debug", "net10.0", "ps-bash"),
            Path.Combine(repoRoot, "src", "PsBash.Shell", "bin", "Release", "net10.0", "ps-bash.exe"),
            Path.Combine(repoRoot, "src", "PsBash.Shell", "bin", "Release", "net10.0", "ps-bash"),
        };

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

        var bashTask = RunOneAsync(BashPath!, "-c", script, effective, env);
        var psBashTask = RunOneAsync(PsBashPath!, "-c", script, effective, env,
            extraEnv: new Dictionary<string, string> { ["PSBASH_DEBUG"] = "1" });

        await Task.WhenAll(bashTask, psBashTask);
        return (await bashTask, await psBashTask);
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
