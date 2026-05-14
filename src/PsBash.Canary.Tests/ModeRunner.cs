using System.Diagnostics;
using PsBash.Testing;

namespace PsBash.Canary.Tests;

/// <summary>
/// Mode identifiers for the QA rubric Directive 4 mode interaction matrix.
/// M4 (interactive TTY) is intentionally excluded — too flaky for CI.
///
/// Aliased onto the shared <see cref="PsBashMode"/> so the canary suite and
/// the unified runner agree on mode numbering.
/// </summary>
public enum Mode
{
    M1_CFlag = (int)PsBashMode.M1_CFlag,        // ps-bash -c script
    M2_StdinPipe = (int)PsBashMode.M2_StdinPipe, // echo script | ps-bash
    M3_FileArg = (int)PsBashMode.M3_FileArg,     // ps-bash script.sh
    M5_InvokeEval = (int)PsBashMode.M5_InvokeEval,  // Invoke-BashEval cmdlet (in-process)
    M6_InvokeSource = (int)PsBashMode.M6_InvokeSource // Invoke-BashSource cmdlet (in-process, .sh file)
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
/// REFACTOR-3: the external-process modes (M1/M2/M3) now delegate to the
/// shared <see cref="PsBashRunner"/> / <see cref="ProcessSpawn"/> helpers, so
/// the Process.Start + pipe-drain + timeout + kill-tree loop is no longer
/// duplicated here. The 60 s canary timeout is expressed as a builder call.
/// The in-process cmdlet modes (M5/M6) stay local — they are a different
/// shape (no Process.Start) and run through <see cref="CanaryPwshFixture"/>.
///
/// PROCESS SPAWN CONTRACT (process_spawn_contract memory note):
///   Every external spawn uses a 60 s hard cap timeout.
///   On timeout: Kill(entireProcessTree: true) in finally, then fail.
///   The Kill fires even if the await is cancelled.
///   (Contract is now enforced once, in PsBash.Testing.ProcessSpawn.)
/// </summary>
public sealed class ModeRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly PsBashRunner _baseRunner;
    private readonly string? _psBashPath;

    public ModeRunner()
    {
        _baseRunner = PsBashRunner.Create().WithTimeout(DefaultTimeout);
        _psBashPath = _baseRunner.TryResolveBinary();
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
        var runner = timeout is { } t ? _baseRunner.WithTimeout(t) : _baseRunner;
        var results = new List<ModeResult>();

        var tasks = new List<Task<ModeResult>>();

        if (_psBashPath != null)
        {
            tasks.Add(RunSpawnModeAsync(runner, Mode.M1_CFlag, PsBashMode.M1_CFlag, script));
            tasks.Add(RunSpawnModeAsync(runner, Mode.M2_StdinPipe, PsBashMode.M2_StdinPipe, script));
            tasks.Add(RunSpawnModeAsync(runner, Mode.M3_FileArg, PsBashMode.M3_FileArg, script));
        }
        else
        {
            results.Add(new ModeResult(Mode.M1_CFlag, "", "ps-bash binary not found", -999, 0));
            results.Add(new ModeResult(Mode.M2_StdinPipe, "", "ps-bash binary not found", -999, 0));
            results.Add(new ModeResult(Mode.M3_FileArg, "", "ps-bash binary not found", -999, 0));
        }

        tasks.Add(RunM5Async(script));
        tasks.Add(RunM6Async(script));

        var completed = await Task.WhenAll(tasks);
        results.AddRange(completed);

        return results;
    }

    // -------------------------------------------------------------------------
    // M1/M2/M3: external-process modes — delegate to the shared PsBashRunner.
    // -------------------------------------------------------------------------
    private static async Task<ModeResult> RunSpawnModeAsync(
        PsBashRunner runner, Mode mode, PsBashMode psBashMode, string script)
    {
        var result = await runner.WithMode(psBashMode).RunScriptAsync(script);
        return new ModeResult(mode, result.Stdout, result.Stderr, result.ExitCode, result.WallMs);
    }

    // -------------------------------------------------------------------------
    // M5: Invoke-BashEval cmdlet (in-process PowerShell SDK)
    // -------------------------------------------------------------------------
    private static async Task<ModeResult> RunM5Async(string script)
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
    private static async Task<ModeResult> RunM6Async(string script)
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
}
