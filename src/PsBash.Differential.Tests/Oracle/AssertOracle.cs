using PsBash.Core.Transpiler;
using PsBash.Testing;
using Xunit;
using Xunit.Sdk;

namespace PsBash.Differential.Tests.Oracle;

/// <summary>
/// Determines how the oracle assertion operates.
/// </summary>
public enum OracleMode
{
    /// <summary>
    /// Runs both bash and ps-bash live and diffs the outputs.
    /// Skips the test when no bash is available.
    /// </summary>
    Differential,

    /// <summary>
    /// Compares ps-bash output against a frozen golden file.
    /// When recording is enabled (UPDATE_GOLDENS=1 at process start, or
    /// <see cref="AssertOracle.BeginUpdateGoldens"/>), records the golden
    /// instead of comparing. Never skips due to bash unavailability.
    /// </summary>
    Golden,
}

/// <summary>
/// Single-method assertion API for differential oracle tests.
///
/// Usage:
///   await AssertOracle.EqualAsync("echo hello");
///   await AssertOracle.GoldenAsync("echo hello", "EchoHello");
///
/// On mismatch, throws <see cref="XunitException"/> whose message contains a
/// structured diff bundle with: input script, both stdouts (canonicalized),
/// both stderrs, both exit codes, both wall times, transpiled PowerShell text
/// (from PSBASH_DEBUG=1 stderr capture), and a filtered env snapshot.
///
/// On bash or ps-bash unavailability, the test is skipped via Skip.If.
/// </summary>
public static class AssertOracle
{
    private static readonly BashOracleFixture Fixture = new();

    /// <summary>
    /// Separate warm host for golden spawns. The daemon freezes its environment
    /// when it starts (one host serves every spawn on its endpoint), so golden
    /// scripts — which must observe the CANONICAL whitelist so <c>$USER</c>/locale
    /// are byte-stable — cannot share the differential fixture's endpoint: a
    /// non-canonical <see cref="EqualAsync"/> spawn could warm that daemon first
    /// and freeze the runner's environment into what the golden observes. Two
    /// fixtures, two endpoints, no cross-talk.
    /// </summary>
    private static readonly BashOracleFixture CanonicalFixture = new();

    /// <summary>Test seam: the differential fixture's warm-host endpoint.</summary>
    internal static string DifferentialEndpointForTest => Fixture.EndpointForTest;

    /// <summary>
    /// One stable canonical HOME/TEMP for the whole process, matching the
    /// canonical fixture's single warm daemon. Created on first use and
    /// deliberately never deleted here: the daemon owns it (Windows module
    /// extraction lives under <c>{TEMP}/ps-bash</c>) and outlives individual
    /// golden calls. OS temp cleanup reaps it after the test process exits.
    /// </summary>
    private static readonly Lazy<string> _goldenCanonicalHome = new(() =>
    {
        var dir = Path.Combine(
            Path.GetTempPath(), $"psb-g{Guid.NewGuid():N}".Substring(0, 13));
        Directory.CreateDirectory(dir);
        return dir;
    });

    private static string GoldenCanonicalHome => _goldenCanonicalHome.Value;

    // Environment variable names that are safe to include in the bundle
    private static readonly HashSet<string> AllowedEnvKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "HOME", "USERPROFILE", "TEMP", "TMP", "TMPDIR",
        "OS", "PROCESSOR_ARCHITECTURE", "COMPUTERNAME", "USERNAME",
        "TERM", "LANG", "LC_ALL", "SHELL",
        "PSBASH_DEBUG", "PSBASH_TIMEOUT", "PSBASH_UNIX_PATHS", "DOTNET_ROOT",
    };

    // Directory where golden files are stored, relative to repo root.
    private static readonly string GoldensDir = FindGoldensDir();

    // ── Recording flag ────────────────────────────────────────────────────────
    //
    // Recording is deliberately NOT process-global. There are two entry points:
    //   1. UPDATE_GOLDENS=1 set before the process starts (the CLI recording
    //      run). It is read ONCE here, so a test that mutates the environment
    //      mid-run cannot leak recording mode into another test's comparison.
    //   2. BeginUpdateGoldens(), an AsyncLocal scope only the current async flow
    //      observes. A concurrently running test captured its own
    //      ExecutionContext and stays in compare mode.
    private static readonly bool EnvUpdateGoldens = string.Equals(
        Environment.GetEnvironmentVariable("UPDATE_GOLDENS"),
        "1",
        StringComparison.Ordinal);

    private static readonly AsyncLocal<bool> ScopedUpdateGoldens = new();

    /// <summary>
    /// Enters golden-recording mode for the current async flow only; dispose to
    /// leave. Use for the self-tests and any in-process recording workflow.
    /// Other tests running concurrently in their own flows are unaffected.
    /// </summary>
    public static IDisposable BeginUpdateGoldens()
    {
        var prior = ScopedUpdateGoldens.Value;
        ScopedUpdateGoldens.Value = true;
        return new RecordingScope(prior);
    }

    private sealed class RecordingScope : IDisposable
    {
        private readonly bool _prior;
        private bool _disposed;

        public RecordingScope(bool prior) => _prior = prior;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ScopedUpdateGoldens.Value = _prior;
        }
    }

    /// <summary>
    /// The absolute path of the golden file for <paramref name="testName"/>.
    /// Shared with the self-tests so each can delete what it writes.
    /// </summary>
    internal static string GoldenFilePath(string testName) =>
        Path.Combine(GoldensDir, $"{testName}.golden.txt");

    /// <summary>
    /// Returns why a recording-mode spawn must NOT be written as a golden, or
    /// null when the spawn is healthy. A spawn that failed at startup (non-zero
    /// exit, or an unhandled exception / oracle timeout on stderr) prints empty
    /// stdout; recording that would overwrite a tracked golden with an empty
    /// file and hide the crash.
    /// </summary>
    internal static string? RecordableSpawnFailure(OracleResult result)
    {
        if (result.ExitCode != 0)
            return $"ps-bash exited with code {result.ExitCode}";
        if (result.Stderr.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase))
            return "ps-bash reported an unhandled exception on stderr";
        if (result.Stderr.Contains("OracleTimeoutException", StringComparison.OrdinalIgnoreCase))
            return "ps-bash reported an oracle timeout on stderr";
        return null;
    }

    private static string FindGoldensDir()
    {
        // Navigate up from the test assembly directory to find the Goldens directory.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "src", "PsBash.Differential.Tests", "Goldens");
            if (Directory.Exists(candidate))
                return candidate;
            var parent = Path.GetDirectoryName(dir);
            if (parent is null) break;
            dir = parent;
        }
        // Fallback: compute from assembly path
        return Path.Combine(AppContext.BaseDirectory, "Goldens");
    }

    /// <summary>
    /// Asserts ps-bash matches the bash oracle for <paramref name="script"/>
    /// (canonicalized stdout, stderr, exit code).
    ///
    /// The bash oracle is sourced by run mode (<see cref="OracleCassette.CurrentMode"/>):
    ///   - <b>Replay</b> (default): the oracle is read from a checked-in cassette;
    ///     ZERO bash processes spawn, only ps-bash runs live. A missing cassette
    ///     is a hard failure (record it) — never a silent skip.
    ///   - <b>Live</b> (<c>PSBASH_ORACLE_LIVE=1</c>): spawns real bash + ps-bash
    ///     and diffs. Skips when no bash host.
    ///   - <b>Record</b> (<c>PSBASH_ORACLE_RECORD=1</c>): spawns both, verifies
    ///     they agree, then rewrites the cassette from the bash side.
    /// </summary>
    /// <param name="script">The bash script to compare.</param>
    /// <param name="timeout">Per-process timeout; defaults to 20 s.</param>
    /// <param name="liveOnly">
    /// Marks a case whose oracle output is not reproducible from a frozen cassette
    /// (env / timing / PID / machine-specific filesystem). Such cases are never
    /// cassetted: they run live under Live/Record and skip-with-reason under
    /// Replay rather than diffing against a stale frozen value.
    /// </param>
    /// <exception cref="XunitException">When outputs differ, or (Replay) the cassette is missing.</exception>
    public static async Task EqualAsync(
        string script,
        TimeSpan? timeout = null,
        bool liveOnly = false)
    {
        Skip.If(Fixture.PsBashPath is null, "ps-bash binary not found -- build PsBash.Shell first");

        var mode = OracleCassette.CurrentMode;

        if (mode == OracleRunMode.Replay)
        {
            Skip.If(liveOnly,
                "oracle: live-only case (env/timing/PID/filesystem-dependent) — " +
                "not reproducible from a cassette. Set PSBASH_ORACLE_LIVE=1 to run it.");

            var loadStatus = OracleCassette.Load(script, out var cassette);
            if (loadStatus == CassetteLoadStatus.Missing)
                throw new XunitException(OracleCassette.MissingMessage(script));
            if (loadStatus == CassetteLoadStatus.Corrupt)
                throw new XunitException(OracleCassette.CorruptMessage(script));

            var psReplay = await Fixture.RunPsBashAsync(script, timeout);
            AssertMatches(script, cassette!.ToOracleResult(), psReplay);
            return;
        }

        // Live or Record: both need a bash host.
        var host = BashLocator.Find();
        Skip.If(!host.IsAvailable, "oracle: no bash available");

        var (bashResult, psBashResult) = await Fixture.RunBothAsync(script, timeout);

        // Record mode: freeze the bash oracle BEFORE asserting, so the cassette
        // captures the pure oracle even if ps-bash currently has a bug (the
        // assertion below still reports that mismatch). liveOnly cases are
        // deliberately not cassetted — they must always run live.
        if (mode == OracleRunMode.Record && !liveOnly)
        {
            OracleCassette.Save(new OracleCassette.CassetteEntry(
                Script: script,
                BashVersion: host.Version,
                Stdout: Canonicalizer.Canonicalize(bashResult.Stdout),
                Stderr: Canonicalizer.Canonicalize(bashResult.Stderr),
                ExitCode: bashResult.ExitCode));
        }

        AssertMatches(script, bashResult, psBashResult);
    }

    /// <summary>
    /// Canonicalizes both sides, compares stdout/stderr/exit, and throws a diff
    /// bundle on mismatch. The <paramref name="bashResult"/> may be a live bash
    /// result or a replayed cassette result — canonicalization is idempotent, so
    /// both paths are byte equivalent.
    /// </summary>
    private static void AssertMatches(
        string script,
        OracleResult bashResult,
        OracleResult psBashResult)
    {
        var bashStdout = Canonicalizer.Canonicalize(bashResult.Stdout);
        var psBashStdout = Canonicalizer.Canonicalize(psBashResult.Stdout);
        var bashStderr = Canonicalizer.Canonicalize(bashResult.Stderr);

        // Extract transpiled PS text from ps-bash debug stderr lines
        var transpiledPs = ExtractTranspiled(psBashResult.Stderr);
        var psBashStderr = Canonicalizer.Canonicalize(
            StripDebugLines(psBashResult.Stderr));

        bool stdoutMatch = bashStdout == psBashStdout;
        bool stderrMatch = bashStderr == psBashStderr;
        bool exitMatch = bashResult.ExitCode == psBashResult.ExitCode;

        if (stdoutMatch && stderrMatch && exitMatch)
            return;

        var bundle = BuildDiffBundle(
            script,
            bashResult, psBashResult,
            bashStdout, psBashStdout,
            bashStderr, psBashStderr,
            transpiledPs);

        throw new XunitException(bundle);
    }

    /// <summary>
    /// Compares ps-bash output against a golden file stored in
    /// <c>src/PsBash.Differential.Tests/Goldens/{testName}.golden.txt</c>.
    ///
    /// Recording is enabled per call by <paramref name="updateGoldens"/> when it
    /// is not null, otherwise by the current recording mode: the
    /// <c>UPDATE_GOLDENS=1</c> environment variable read at process start, or an
    /// active <see cref="BeginUpdateGoldens"/> scope. Recording refuses a failed
    /// spawn (non-zero exit or crash stderr) rather than overwrite a tracked
    /// golden with empty output.
    ///
    /// Never skips due to bash unavailability — golden mode is designed for
    /// platforms without live bash.
    /// </summary>
    /// <param name="script">The bash script to run through ps-bash.</param>
    /// <param name="testName">
    /// Identifier used to name the golden file. Use a short, stable name
    /// (e.g. <c>"EchoHello"</c>). Will become
    /// <c>src/PsBash.Differential.Tests/Goldens/EchoHello.golden.txt</c>.
    /// </param>
    /// <param name="timeout">Per-process timeout; defaults to 5 s.</param>
    /// <param name="updateGoldens">
    /// Overrides recording mode for this call: <c>true</c> records, <c>false</c>
    /// compares, <c>null</c> (default) uses the ambient recording mode.
    /// </param>
    /// <exception cref="XunitException">When output does not match the golden.</exception>
    /// <exception cref="SkipException">
    /// When no golden file exists and recording is not active.
    /// </exception>
    public static async Task GoldenAsync(
        string script,
        string testName,
        TimeSpan? timeout = null,
        bool? updateGoldens = null)
    {
        Skip.If(Fixture.PsBashPath is null, "ps-bash binary not found -- build PsBash.Shell first");

        var goldenPath = GoldenFilePath(testName);
        var recording = updateGoldens ?? (ScopedUpdateGoldens.Value || EnvUpdateGoldens);

        // QA rubric Directive 6: golden output must be machine-independent.
        // Spawn ps-bash under a canonical environment — the inherited block is
        // cleared and only the CanonicalEnv whitelist is applied — so env-derived
        // values ($USER, $HOME, locale) are byte-stable across dev boxes and CI
        // runners.
        //
        // HOME/TEMP are one STABLE per-process directory, not a fresh one per
        // call. The canonical fixture keeps ONE warm daemon, and a daemon freezes
        // its environment (including TEMP, which on Windows is the root of the
        // `{TEMP}/ps-bash/module-{version}-{hash}` extraction dir) when it first
        // starts. A per-call HOME would be deleted by this method's cleanup while
        // the daemon still references it, so the next golden spawn races the
        // daemon over a vanished module directory ("PsBash.Cmdlets.dll ... being
        // used by another process"). One stable root matches the daemon's
        // lifetime and is left for OS temp cleanup.
        //
        // The directory name is kept SHORT: TEMP/TMPDIR point here, and the
        // ps-bash host derives its Unix domain socket path under the per-user
        // runtime dir (PsBashRuntimeDirectory) as
        // "{runtimeDir}/host-pi-{pid}-{guid}.sock", where runtimeDir is
        // "{TEMP}\ps-bash" on Windows and, on POSIX, "$XDG_RUNTIME_DIR/ps-bash"
        // when set, else "{TMPDIR}/ps-bash-{uid}". sun_path is a 108-byte
        // buffer INCLUDING the terminating NUL on Linux/Windows (104 on macOS),
        // so the whole path must stay within 107 chars (103 on macOS) or the
        // host cannot bind. IpcTransportFactory now shortens the random suffix
        // to fit and falls back to a named pipe when even that will not fit,
        // but a short root keeps these golden runs on the fast unix path.
        var canonicalHome = GoldenCanonicalHome;
        var canonicalEnv = CanonicalEnv.ForPsBash(canonicalHome);
        canonicalEnv["PSBASH_TIMEOUT"] = "15";

        // Route through the canonical fixture's ONE warm host rather than a
        // cold host per golden. A cold ps-bash start is ~3 s idle and exceeds
        // this call's 15 s timeout under host load (01M3GWY9AX7BY4MDB7YFVWP4BZ).
        // The fixture layers its warm-host env over canonicalEnv and pins
        // PSBASH_PER_INVOCATION=0; canonicalizeEnv keeps the inherited block
        // cleared so only the whitelist reaches the daemon.
        var psBashResult = await CanonicalFixture.RunPsBashAsync(
            script,
            timeout ?? BashOracleFixture.DefaultTimeout,
            canonicalEnv,
            canonicalizeEnv: true);

        var canonicalized = Canonicalizer.Canonicalize(
            StripDebugLines(psBashResult.Stdout));

        if (recording)
        {
            // A crashed spawn prints empty stdout; writing it over a tracked
            // golden is silent corruption. Refuse and report the spawn instead.
            var failure = RecordableSpawnFailure(psBashResult);
            if (failure is not null)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("=== Golden Recording Refused ===");
                sb.AppendLine();
                sb.AppendLine($"Golden file: {goldenPath}");
                sb.AppendLine($"Reason: {failure}");
                sb.AppendLine();
                sb.AppendLine("--- Input Script ---");
                sb.AppendLine(script);
                sb.AppendLine();
                sb.AppendLine("--- ps-bash stdout (canonicalized, NOT written) ---");
                sb.AppendLine(canonicalized);
                sb.AppendLine();
                sb.AppendLine($"--- ps-bash exit code: {psBashResult.ExitCode} ---");
                sb.AppendLine("--- ps-bash stderr ---");
                sb.AppendLine(psBashResult.Stderr);
                throw new XunitException(sb.ToString());
            }

            Directory.CreateDirectory(GoldensDir);
            await File.WriteAllTextAsync(goldenPath, canonicalized);
            return; // Recording mode — always pass
        }

        if (!File.Exists(goldenPath))
        {
            Skip.If(true,
                $"golden file missing: {goldenPath}. Run with UPDATE_GOLDENS=1 to record.");
        }

        var expected = await File.ReadAllTextAsync(goldenPath);

        if (expected != canonicalized)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== Golden Diff Bundle ===");
            sb.AppendLine();
            sb.AppendLine($"Golden file: {goldenPath}");
            sb.AppendLine();
            sb.AppendLine("--- Input Script ---");
            sb.AppendLine(script);
            sb.AppendLine();
            sb.AppendLine("--- Expected (golden) ---");
            sb.AppendLine(expected);
            sb.AppendLine("--- Actual (ps-bash) ---");
            sb.AppendLine(canonicalized);
            sb.AppendLine();
            sb.AppendLine($"--- ps-bash exit code: {psBashResult.ExitCode} ---");
            sb.AppendLine("--- ps-bash stderr ---");
            sb.AppendLine(psBashResult.Stderr);
            sb.AppendLine();
            sb.AppendLine("--- Diff ---");
            sb.AppendLine(ComputeLineDiff(expected, canonicalized));

            throw new XunitException(sb.ToString());
        }
    }

    /// <summary>
    /// Extracts the "[ps-bash] transpiled: ..." line from PSBASH_DEBUG=1 stderr output.
    /// Returns empty string when the debug line is absent.
    /// </summary>
    private static string ExtractTranspiled(string debugStderr)
    {
        const string marker = "[ps-bash] transpiled: ";
        foreach (var line in debugStderr.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith(marker, StringComparison.Ordinal))
                return trimmed.Substring(marker.Length);
        }

        return string.Empty;
    }

    /// <summary>
    /// Removes [ps-bash] debug lines from stderr so they do not pollute the stderr diff.
    /// </summary>
    private static string StripDebugLines(string stderr)
    {
        var lines = stderr.Split('\n');
        return string.Join('\n', lines.Where(l =>
            !l.TrimEnd('\r').StartsWith("[ps-bash] ", StringComparison.Ordinal)));
    }

    private static string BuildDiffBundle(
        string script,
        OracleResult bashResult,
        OracleResult psBashResult,
        string bashStdout,
        string psBashStdout,
        string bashStderr,
        string psBashStderr,
        string transpiledPs)
    {
        // Compute transpiled PS if not captured from debug output
        string ps = transpiledPs;
        if (string.IsNullOrEmpty(ps))
        {
            try { ps = BashTranspiler.Transpile(script); }
            catch (Exception ex) { ps = $"<transpile error: {ex.Message}>"; }
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== Oracle Diff Bundle ===");
        sb.AppendLine();
        sb.AppendLine("--- Input Script ---");
        sb.AppendLine(script);
        sb.AppendLine();
        sb.AppendLine("--- Transpiled PowerShell ---");
        sb.AppendLine(ps);
        sb.AppendLine();
        sb.AppendLine($"--- Exit Codes --- bash={bashResult.ExitCode} ps-bash={psBashResult.ExitCode}");
        sb.AppendLine($"--- Wall Times  --- bash={bashResult.WallMs}ms ps-bash={psBashResult.WallMs}ms");
        sb.AppendLine();
        sb.AppendLine("--- bash stdout (canonicalized) ---");
        sb.AppendLine(bashStdout);
        sb.AppendLine("--- ps-bash stdout (canonicalized) ---");
        sb.AppendLine(psBashStdout);
        sb.AppendLine();
        sb.AppendLine("--- bash stderr (canonicalized) ---");
        sb.AppendLine(bashStderr);
        sb.AppendLine("--- ps-bash stderr (canonicalized) ---");
        sb.AppendLine(psBashStderr);
        sb.AppendLine();
        sb.AppendLine("--- Diff (stdout) ---");
        sb.AppendLine(ComputeLineDiff(bashStdout, psBashStdout));
        sb.AppendLine();
        sb.AppendLine("--- Diff (stderr) ---");
        sb.AppendLine(ComputeLineDiff(bashStderr, psBashStderr));
        sb.AppendLine();
        sb.AppendLine("--- Environment Snapshot ---");
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key?.ToString() ?? "";
            if (AllowedEnvKeys.Contains(key))
                sb.AppendLine($"  {key}={entry.Value}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Produces a simple line diff between two canonicalized strings.
    /// Lines only in <paramref name="expected"/> are prefixed with '-',
    /// lines only in <paramref name="actual"/> with '+', common lines with ' '.
    /// </summary>
    private static string ComputeLineDiff(string expected, string actual)
    {
        if (expected == actual)
            return "(identical)";

        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');
        var sb = new System.Text.StringBuilder();

        int i = 0, j = 0;
        while (i < expectedLines.Length || j < actualLines.Length)
        {
            if (i < expectedLines.Length && j < actualLines.Length &&
                expectedLines[i] == actualLines[j])
            {
                sb.AppendLine($"  {expectedLines[i]}");
                i++; j++;
            }
            else
            {
                if (i < expectedLines.Length)
                    sb.AppendLine($"- {expectedLines[i++]}");
                if (j < actualLines.Length)
                    sb.AppendLine($"+ {actualLines[j++]}");
            }
        }

        return sb.ToString();
    }
}
