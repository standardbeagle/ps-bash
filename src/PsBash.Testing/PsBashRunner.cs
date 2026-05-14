using System.Diagnostics;

namespace PsBash.Testing;

/// <summary>
/// Builder-API runner for spawning the ps-bash launcher in tests (REFACTOR-3).
///
/// This is the single entry point the external-process test suites use —
/// Escalation, Canary (M1–M3), and Differential's ps-bash side. Per-suite
/// differences (timeout length, build config, environment canonicalisation,
/// execution mode) are expressed as builder calls, NOT as duplicated
/// Process.Start + pipe-wiring code. The spawn loop itself lives once in
/// <see cref="ProcessSpawn"/>.
///
/// Usage:
/// <code>
///   var result = await PsBashRunner.Create()
///       .WithConfig(BuildConfig.Release)
///       .WithTimeout(TimeSpan.FromSeconds(30))
///       .WithEnv(canonicalize: true)
///       .WithMode(PsBashMode.M1_CFlag)
///       .RunScriptAsync("echo hello");
/// </code>
///
/// The runner is immutable: every <c>With*</c> call returns a new instance, so
/// a configured base runner can be safely shared across tests and specialised
/// per-call.
/// </summary>
public sealed class PsBashRunner
{
    private readonly BuildConfig _config;
    private readonly TimeSpan _timeout;
    private readonly PsBashMode _mode;
    private readonly bool _canonicalizeEnv;
    private readonly IReadOnlyDictionary<string, string>? _env;

    /// <summary>Default per-spawn timeout when none is configured.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private PsBashRunner(
        BuildConfig config,
        TimeSpan timeout,
        PsBashMode mode,
        bool canonicalizeEnv,
        IReadOnlyDictionary<string, string>? env)
    {
        _config = config;
        _timeout = timeout;
        _mode = mode;
        _canonicalizeEnv = canonicalizeEnv;
        _env = env;
    }

    /// <summary>
    /// Creates a runner with defaults: auto-detected build config, 30 s
    /// timeout, M1 (<c>-c</c>) mode, inherited environment.
    /// </summary>
    public static PsBashRunner Create()
        => new(BuildConfig.Auto, DefaultTimeout, PsBashMode.M1_CFlag, canonicalizeEnv: false, env: null);

    /// <summary>Selects the ps-bash launcher build configuration to spawn.</summary>
    public PsBashRunner WithConfig(BuildConfig config)
        => new(config, _timeout, _mode, _canonicalizeEnv, _env);

    /// <summary>Sets the hard per-spawn timeout.</summary>
    public PsBashRunner WithTimeout(TimeSpan timeout)
        => new(_config, timeout, _mode, _canonicalizeEnv, _env);

    /// <summary>Sets the hard per-spawn timeout, in whole seconds.</summary>
    public PsBashRunner WithTimeout(int seconds)
        => WithTimeout(TimeSpan.FromSeconds(seconds));

    /// <summary>
    /// Sets extra environment variables for the spawned process and, when
    /// <paramref name="canonicalize"/> is true, clears the inherited
    /// environment block first so the child runs with a known, reproducible
    /// environment (no leakage from the test host's shell).
    /// </summary>
    public PsBashRunner WithEnv(
        IReadOnlyDictionary<string, string>? env = null,
        bool canonicalize = false)
        => new(_config, _timeout, _mode, canonicalize, env);

    /// <summary>Selects the execution mode (M1/M2/M3) the script is run under.</summary>
    public PsBashRunner WithMode(PsBashMode mode)
        => new(_config, _timeout, mode, _canonicalizeEnv, _env);

    /// <summary>
    /// Resolves the ps-bash launcher path for the configured build, or
    /// <c>null</c> when it has not been built. Suites that skip on a missing
    /// binary check this; suites that hard-fail use <see cref="ResolveBinary"/>.
    /// </summary>
    public string? TryResolveBinary() => PsBashLocator.Resolve(_config);

    /// <summary>Resolves the launcher path, throwing if not built.</summary>
    public string ResolveBinary() => PsBashLocator.ResolveRequired(_config);

    /// <summary>
    /// Runs <paramref name="script"/> through the ps-bash launcher in the
    /// configured mode and returns the captured result.
    ///
    /// Mode dispatch:
    ///   M1 — <c>ps-bash -c script</c>.
    ///   M2 — <c>ps-bash</c> with <paramref name="script"/> piped to stdin.
    ///   M3 — script written to a temp <c>.sh</c> file, path passed as arg0.
    ///   M5/M6 — not supported here (in-process cmdlet modes; use the Canary
    ///           PowerShell fixture). Throws <see cref="NotSupportedException"/>.
    /// </summary>
    public async Task<SpawnResult> RunScriptAsync(string script)
    {
        var binary = ResolveBinary();

        switch (_mode)
        {
            case PsBashMode.M1_CFlag:
                return await ProcessSpawn.RunAsync(
                    binary, new[] { "-c", script }, _timeout,
                    stdinContent: null, env: _env, canonicalizeEnv: _canonicalizeEnv);

            case PsBashMode.M2_StdinPipe:
                return await ProcessSpawn.RunAsync(
                    binary, Array.Empty<string>(), _timeout,
                    stdinContent: script, env: _env, canonicalizeEnv: _canonicalizeEnv);

            case PsBashMode.M3_FileArg:
                return await RunFileArgAsync(binary, script);

            case PsBashMode.M5_InvokeEval:
            case PsBashMode.M6_InvokeSource:
                throw new NotSupportedException(
                    $"{_mode} is an in-process cmdlet mode; PsBashRunner only drives " +
                    "external-process modes (M1/M2/M3). Use the in-process PowerShell " +
                    "fixture for M5/M6.");

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(_mode), _mode, "Unknown ps-bash execution mode.");
        }
    }

    /// <summary>
    /// Runs the launcher with an explicit argument list (no script-mode
    /// wrapping). For suites that pass flags directly, e.g. fault-injection
    /// probes. Honors the configured timeout and environment.
    /// </summary>
    public Task<SpawnResult> RunArgsAsync(string[] arguments, string? stdinContent = null)
        => ProcessSpawn.RunAsync(
            ResolveBinary(), arguments, _timeout,
            stdinContent: stdinContent, env: _env, canonicalizeEnv: _canonicalizeEnv);

    private async Task<SpawnResult> RunFileArgAsync(string binary, string script)
    {
        var tempFile = Path.Combine(
            Path.GetTempPath(), "ps-bash", $"psbash-m3-{Guid.NewGuid()}.sh");
        Directory.CreateDirectory(Path.GetDirectoryName(tempFile)!);
        try
        {
            await File.WriteAllTextAsync(tempFile, script);
            return await ProcessSpawn.RunAsync(
                binary, new[] { tempFile }, _timeout,
                stdinContent: null, env: _env, canonicalizeEnv: _canonicalizeEnv);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* best-effort cleanup */ }
        }
    }
}
