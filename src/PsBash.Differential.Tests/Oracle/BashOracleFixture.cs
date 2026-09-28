using System.Diagnostics;
using PsBash.Core.Runtime.Ipc;
using PsBash.Testing;

namespace PsBash.Differential.Tests.Oracle;

/// <summary>
/// Runs a bash script through both <c>bash -c</c> and <c>ps-bash -c</c>,
/// capturing stdout, stderr, exit code, and wall time for each.
///
/// REFACTOR-3: the Process.Start + pipe-drain + timeout + kill-tree loop is no
/// longer hand-rolled here — both the bash side and the ps-bash side delegate
/// to the shared <see cref="ProcessSpawn"/> primitive in PsBash.Testing. What
/// stays Differential-specific:
///   - bash host resolution via <see cref="BashLocator"/> (Native vs WSL args);
///   - the <see cref="SemaphoreSlim"/>(2) WSL-VM throttle (assessed in
///     REFACTOR-7 as VM-overload throttling, not daemon contention — kept);
///   - the ps-bash extra env (PSBASH_DEBUG=1, PSBASH_TIMEOUT=15);
///   - <see cref="OracleResult"/> / <see cref="OracleTimeoutException"/> domain
///     naming. OracleTimeoutException is now a thin subclass of
///     <see cref="SpawnTimeoutException"/>.
///
/// RELIABILITY CONTRACT (process_spawn_contract memory note): every spawn uses
/// a configurable timeout and Kill(entireProcessTree: true) in finally — now
/// enforced once, in PsBash.Testing.ProcessSpawn.
/// </summary>
public sealed class BashOracleFixture
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Test-only seam: when non-null, <see cref="RunOneAsync"/> delegates the
    /// spawn here instead of <see cref="ProcessSpawn.RunAsync"/>. Lets a unit
    /// test assert the effective environment (e.g. the PSBASH_PER_INVOCATION
    /// host-lifetime default) without starting a real host. The signature is
    /// <c>(executable, arguments, timeout, mergedEnv, canonicalizeEnv)</c>.
    ///
    /// <para><b>AsyncLocal, not a plain static.</b> xunit runs test
    /// collections in parallel (<c>parallelizeTestCollections: true</c>), so a
    /// process-wide slot would let one test's fake spawn swallow a concurrent
    /// <see cref="RunOneAsync"/> caller and turn its real spawn into an
    /// exit-0 no-op. <see cref="AsyncLocal{T}"/> flows the override with the
    /// async call chain that set it, so no other collection can observe it.</para>
    /// </summary>
    internal static readonly AsyncLocal<Func<string, IReadOnlyList<string>, TimeSpan,
        IReadOnlyDictionary<string, string>?, bool, Task<SpawnResult>>?> RunOneSpawnOverride = new();

    // Limits concurrent bash (WSL) invocations so the WSL VM stays responsive
    // under parallel test execution. Permit count of 2 allows meaningful
    // parallelism while avoiding WSL overload (which causes timeouts > 5 s).
    private static readonly SemaphoreSlim _bashConcurrency = new(2, 2);

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

    /// <summary>
    /// Per-fixture isolated IPC endpoint. The differential suite reuses ONE warm
    /// host across all its ps-bash spawns instead of forcing
    /// <c>PSBASH_PER_INVOCATION=1</c> (a cold host per spawn). Rationale
    /// (01M3GWY9AX7BY4MDB7YFVWP4BZ): a ps-bash host cold start is ~3 s of runspace
    /// + psm1 import; under host load it exceeds the suite's fixed 15 s per-spawn
    /// timeout, so trivial scripts (`x=hello; echo ${x}`) failed with
    /// OracleTimeoutException. A warm host removes the cold-start cost entirely
    /// rather than chasing a timeout derived from a measured startup. The idle
    /// window is short so the daemon does not outlive the suite and lock the
    /// build's DLLs (the original reason per-invocation was forced).
    /// </summary>
    private readonly string _endpoint = CreateEndpoint();

    private static string CreateEndpoint()
        => OperatingSystem.IsWindows()
            ? "pipe:psbash-oracle-" + Guid.NewGuid().ToString("N")
            : "unix:" + Path.Combine(Path.GetTempPath(), "ps-bash", "oracle-" + Guid.NewGuid().ToString("N") + ".sock");

    /// <summary>Idle window (seconds) for the fixture's warm daemon.</summary>
    private const int WarmHostIdleSecs = 20;

    /// <summary>
    /// Env layered onto the differential ps-bash spawn: point at the fixture's
    /// shared endpoint and let the daemon idle out shortly after the suite.
    /// </summary>
    private Dictionary<string, string> WarmHostEnv() => new()
    {
        [IpcTransportFactory.EndpointEnvVar] = _endpoint,
        ["PSBASH_HOST_IDLE_SECS"] = WarmHostIdleSecs.ToString(),
    };

    public BashOracleFixture(
        OracleRunMode? mode = null,
        Func<BashHost>? bashResolver = null)
    {
        // Replay reads the bash side from disk, so even host discovery is an
        // unwanted WSL touch. Resolve bash only for modes that can spawn it.
        if ((mode ?? OracleCassette.CurrentMode) != OracleRunMode.Replay)
        {
            var host = (bashResolver ?? BashLocator.Find)();
            // BashPath is used by legacy callers; expose the native path when available.
            // For WSL, expose "wsl.exe" so BashPath != null means available.
            BashPath = host.IsAvailable ? host.Path : null;
        }
        // Unified launcher resolution (REFACTOR-3) — replaces the bespoke
        // FindPsBash copy that previously lived here.
        PsBashPath = PsBashLocator.Resolve();
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

        // Throttle concurrent bash invocations to prevent WSL VM overload.
        await _bashConcurrency.WaitAsync().ConfigureAwait(false);
        try
        {
            // Build the bash PSI using BashLocator so WSL gets the correct -e bash -c args.
            var bashPsi = BashLocator.BuildPsi(host, script)!;

            var bashTask = RunOnePsiAsync(bashPsi, effective, extraEnv: env);
            var psBashTask = RunPsBashAsync(script, effective, env);

            await Task.WhenAll(bashTask, psBashTask).ConfigureAwait(false);
            return (await bashTask, await psBashTask);
        }
        finally
        {
            _bashConcurrency.Release();
        }
    }

    /// <summary>
    /// Runs <paramref name="script"/> through ps-bash ONLY (no bash), with the
    /// same extra env the differential comparison uses. This is the sole path in
    /// replay mode — the bash oracle comes from a cassette, so no bash spawns.
    /// Kept identical to the ps-bash side of <see cref="RunBothAsync"/> so a
    /// replayed diff is byte-equivalent to a live one.
    /// </summary>
    public Task<OracleResult> RunPsBashAsync(
        string script,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? env = null,
        bool canonicalizeEnv = false)
    {
        var extra = WarmHostEnv();
        extra["PSBASH_DEBUG"] = "1";
        extra["PSBASH_TIMEOUT"] = "15";
        // Deliberately NOT PSBASH_PER_INVOCATION=1: the fixture keeps one warm
        // host for all its spawns. The endpoint-scoped daemon is shared across the
        // fixture's concurrent spawns, which the host serializes internally via
        // its pooled runspaces (one per connection) — no cross-talk. extraEnv wins
        // over env, so a canonical caller (GoldenAsync) that pins
        // PSBASH_PER_INVOCATION=1 in its whitelist is overridden to 0 here.
        extra["PSBASH_PER_INVOCATION"] = "0";
        return RunOneAsync(PsBashPath!, "-c", script, timeout ?? DefaultTimeout, env,
            extraEnv: extra, canonicalizeEnv: canonicalizeEnv);
    }

    /// <summary>
    /// The endpoint env var value this fixture's warm daemon binds. Exposed so a
    /// test can prove the canonical (golden) fixture uses a DIFFERENT endpoint
    /// than the differential fixture — the daemon freezes its environment at
    /// start, so sharing one endpoint across canonical and non-canonical spawns
    /// would leak the runner's environment into a golden.
    /// </summary>
    internal string EndpointForTest => _endpoint;

    /// <summary>
    /// Runs a process from a pre-built <see cref="ProcessStartInfo"/> and captures output.
    /// Delegates the spawn loop to <see cref="ProcessSpawn"/>; a timeout surfaces
    /// as <see cref="OracleTimeoutException"/>.
    /// </summary>
    /// <param name="canonicalizeEnv">
    /// When true, the inherited environment is cleared before <paramref name="extraEnv"/>
    /// is applied — used by golden tests so frozen output is machine-independent
    /// (QA rubric Directive 6). Callers pass a <see cref="CanonicalEnv"/> whitelist
    /// as <paramref name="extraEnv"/>.
    /// </param>
    public static async Task<OracleResult> RunOnePsiAsync(
        ProcessStartInfo psi,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? extraEnv = null,
        bool canonicalizeEnv = false)
    {
        try
        {
            var result = await ProcessSpawn.RunAsync(
                psi, timeout, stdinContent: null, env: extraEnv, canonicalizeEnv: canonicalizeEnv);
            return new OracleResult(result.Stdout, result.Stderr, result.ExitCode, result.WallMs);
        }
        catch (SpawnTimeoutException ex)
        {
            throw new OracleTimeoutException(
                ex.Executable, ex.Arguments, ex.Timeout, ex.PartialStdout, ex.PartialStderr);
        }
    }

    /// <summary>
    /// Runs a single interpreter with <c>-c script</c> and captures all output.
    /// Delegates the spawn loop to <see cref="ProcessSpawn"/>; a timeout surfaces
    /// as <see cref="OracleTimeoutException"/>.
    ///
    /// <para><b>Host lifetime.</b> When <paramref name="executable"/> resolves to
    /// the ps-bash launcher, <c>PSBASH_PER_INVOCATION=1</c> is injected unless a
    /// caller supplies it explicitly. Without this the launcher starts the shared
    /// 600 s daemon (<see cref="IdleShutdown"/> default), which outlives the test
    /// and locks the <c>src/PsBash.Shell/bin</c> DLLs against the next build
    /// (MSB3027). Passing <c>PSBASH_PER_INVOCATION=0</c> opts back into the daemon
    /// deliberately.</para>
    /// </summary>
    /// <param name="canonicalizeEnv">
    /// When true, the inherited environment is cleared before <paramref name="env"/>
    /// / <paramref name="extraEnv"/> are applied — used by golden tests so frozen
    /// output is machine-independent (QA rubric Directive 6). Callers pass a
    /// <see cref="CanonicalEnv"/> whitelist as <paramref name="env"/>.
    /// </param>
    internal static async Task<OracleResult> RunOneAsync(
        string executable,
        string firstArg,
        string script,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? env = null,
        IReadOnlyDictionary<string, string>? extraEnv = null,
        bool canonicalizeEnv = false)
    {
        // Layer env then extraEnv (extraEnv wins) into a single map, matching
        // the historical two-pass apply order.
        Dictionary<string, string>? merged = null;
        if (env is not null || extraEnv is not null)
        {
            merged = new Dictionary<string, string>();
            if (env is not null)
                foreach (var (k, v) in env) merged[k] = v;
            if (extraEnv is not null)
                foreach (var (k, v) in extraEnv) merged[k] = v;
        }

        var isPsBashLauncher = PsBashLocator.Resolve() is { } launcher
            && string.Equals(Path.GetFullPath(executable), Path.GetFullPath(launcher), StringComparison.OrdinalIgnoreCase);
        if (isPsBashLauncher)
        {
            merged ??= new Dictionary<string, string>();
            if (!merged.ContainsKey("PSBASH_PER_INVOCATION"))
                merged["PSBASH_PER_INVOCATION"] = "1";
        }

        try
        {
            var spawnOverride = RunOneSpawnOverride.Value;
            var result = spawnOverride is not null
                ? await spawnOverride(executable, new[] { firstArg, script }, timeout, merged, canonicalizeEnv)
                : await ProcessSpawn.RunAsync(
                    executable, new[] { firstArg, script }, timeout, stdinContent: null,
                    env: merged, canonicalizeEnv: canonicalizeEnv);
            return new OracleResult(result.Stdout, result.Stderr, result.ExitCode, result.WallMs);
        }
        catch (SpawnTimeoutException ex)
        {
            throw new OracleTimeoutException(
                ex.Executable, script, ex.Timeout, ex.PartialStdout, ex.PartialStderr);
        }
    }
}

/// <summary>
/// Thrown when a spawned interpreter does not exit within the oracle timeout.
/// The message contains "oracle timeout" so test output is unambiguous.
///
/// REFACTOR-3: now a thin subclass of <see cref="SpawnTimeoutException"/> — it
/// keeps the Differential-specific message prefix and the
/// <see cref="Script"/> alias property while inheriting the partial-output
/// capture contract.
/// </summary>
public sealed class OracleTimeoutException : SpawnTimeoutException
{
    /// <summary>The script (or argument string) that timed out.</summary>
    public string Script => Arguments;

    public OracleTimeoutException(
        string executable,
        string script,
        TimeSpan timeout,
        string partialStdout,
        string partialStderr)
        : base(executable, script, timeout, partialStdout, partialStderr)
    {
    }

    public override string Message =>
        $"oracle timeout: {Path.GetFileName(Executable)} did not exit within " +
        $"{Timeout.TotalSeconds:F0}s running script: {Arguments}\n" +
        $"--- partial stdout ---\n{PartialStdout}\n" +
        $"--- partial stderr ---\n{PartialStderr}";
}
