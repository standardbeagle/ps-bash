using System.Security.Cryptography;
using System.Text;

namespace PsBash.Differential.Tests.Oracle;

/// <summary>
/// How the oracle assertion sources the bash side of a differential.
/// </summary>
public enum OracleRunMode
{
    /// <summary>
    /// Default. The bash oracle is read from a checked-in cassette — ZERO bash
    /// processes are spawned. Only ps-bash runs live and is diffed against the
    /// frozen oracle. A missing cassette is a hard failure (record it), never a
    /// silent skip.
    /// </summary>
    Replay,

    /// <summary>
    /// Spawns real bash AND ps-bash live and diffs the outputs (the historical
    /// behavior). Behind <c>PSBASH_ORACLE_LIVE=1</c>. Skips when no bash host.
    /// </summary>
    Live,

    /// <summary>
    /// Spawns real bash AND ps-bash live, verifies they agree, then (re)writes
    /// the cassette from the bash side. Behind <c>PSBASH_ORACLE_RECORD=1</c>.
    /// Requires a bash host.
    /// </summary>
    Record,
}

/// <summary>The result of looking up and parsing a cassette from disk.</summary>
public enum CassetteLoadStatus
{
    Loaded,
    Missing,
    Corrupt,
}

/// <summary>
/// Record/replay store for the bash oracle used by the differential suite.
///
/// A differential test spawns real bash as the oracle for every case, every run
/// (WSL on Windows), which is slow and needs bash present. This store freezes
/// each bash oracle result into a deterministic, human-diffable, checked-in
/// cassette so the default run replays the oracle with no bash processes.
///
/// Cassette design:
///   - One file per unique script, named by the SHA-256 of the script
///     (<c>{hex}.cassette</c>) so lookup is O(1) and rename-stable.
///   - The file EMBEDS the raw script and the recording bash version so a
///     reviewer can read/grep/diff it, and a bash-version bump produces a
///     visible git diff (the <c>bash-version:</c> line changes on re-record).
///   - stdout/stderr are stored CANONICALIZED (ANSI-stripped, LF, trimmed) —
///     exactly what the diff compares — so the frozen oracle is byte-identical
///     to what a live diff would have used.
///   - Section bodies are sliced by explicit length headers (not marker
///     scanning), so content that itself contains a marker line round-trips.
///   - Written with LF; the parser normalizes CRLF-on-checkout back to LF
///     before applying the length headers (a <c>.gitattributes</c> pins LF too).
/// </summary>
public static class OracleCassette
{
    private const string Header = "PSBASH-ORACLE-CASSETTE v1";
    private const string ScriptMarker = "==== SCRIPT ====";
    private const string StdoutMarker = "==== STDOUT ====";
    private const string StderrMarker = "==== STDERR ====";

    /// <summary>A frozen bash oracle result plus its provenance.</summary>
    public sealed record CassetteEntry(
        string Script,
        string BashVersion,
        string Stdout,
        string Stderr,
        int ExitCode)
    {
        /// <summary>
        /// Presents the frozen oracle as an <see cref="OracleResult"/> so the
        /// same diff path serves live and replay. Wall time is not recorded
        /// (meaningless for a frozen result).
        /// </summary>
        public OracleResult ToOracleResult() => new(Stdout, Stderr, ExitCode, WallMs: 0);
    }

    // ── Mode detection ────────────────────────────────────────────────────

    /// <summary>The effective run mode for this process, from the environment.</summary>
    public static OracleRunMode CurrentMode => ModeFromEnv(
        Environment.GetEnvironmentVariable("PSBASH_ORACLE_RECORD"),
        Environment.GetEnvironmentVariable("PSBASH_ORACLE_LIVE"));

    /// <summary>
    /// Resolves the run mode from the two flag values. Record wins over Live;
    /// both absent/false is Replay. Pure — the unit-test seam.
    /// </summary>
    public static OracleRunMode ModeFromEnv(string? record, string? live)
    {
        if (IsTruthy(record)) return OracleRunMode.Record;
        if (IsTruthy(live)) return OracleRunMode.Live;
        return OracleRunMode.Replay;
    }

    // Mirrors PsBash.Core.Runtime.EnvFlags.IsTruthy but operates on a passed
    // value so ModeFromEnv stays pure/testable without touching the environment.
    private static bool IsTruthy(string? value)
    {
        value = value?.Trim();
        return value is not null
            && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    // ── Keying + paths ────────────────────────────────────────────────────

    /// <summary>Lower-hex SHA-256 of the script — the cassette's stable key.</summary>
    public static string KeyFor(string script)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(script));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // Directory where cassettes live, resolved the same way as Goldens: walk up
    // from the test assembly base dir to the repo's source tree so recording
    // writes to the checked-in copy (not the bin/ shadow), then fall back.
    private static readonly Lazy<string> CassettesDirLazy = new(FindCassettesDir);

    /// <summary>Absolute path to the checked-in cassettes directory.</summary>
    public static string CassettesDir => CassettesDirLazy.Value;

    private static string FindCassettesDir()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "src", "PsBash.Differential.Tests", "Cassettes");
            if (Directory.Exists(candidate))
                return candidate;
            var parent = Path.GetDirectoryName(dir);
            if (parent is null) break;
            dir = parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "Cassettes");
    }

    private static string PathFor(string script) =>
        Path.Combine(CassettesDir, $"{KeyFor(script)}.cassette");

    // ── Load / save ───────────────────────────────────────────────────────

    /// <summary>
    /// Loads the cassette for <paramref name="script"/>, distinguishing an
    /// absent file from a present file that cannot be read or parsed.
    /// </summary>
    public static CassetteLoadStatus Load(string script, out CassetteEntry? entry)
    {
        entry = null;
        var path = PathFor(script);
        if (!File.Exists(path)) return CassetteLoadStatus.Missing;
        try
        {
            if (!TryParse(File.ReadAllText(path), out entry) ||
                !string.Equals(entry!.Script, script, StringComparison.Ordinal))
            {
                entry = null;
                return CassetteLoadStatus.Corrupt;
            }

            return CassetteLoadStatus.Loaded;
        }
        catch
        {
            return CassetteLoadStatus.Corrupt;
        }
    }

    /// <summary>Compatibility helper for callers that only need success/failure.</summary>
    public static bool TryLoad(string script, out CassetteEntry? entry) =>
        Load(script, out entry) == CassetteLoadStatus.Loaded;

    /// <summary>
    /// Writes (or overwrites) the cassette for <paramref name="entry"/>. The
    /// content is written with LF endings.
    /// </summary>
    public static void Save(CassetteEntry entry)
    {
        Directory.CreateDirectory(CassettesDir);
        var path = PathFor(entry.Script);
        File.WriteAllText(path, Serialize(entry), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    // ── Serialization ─────────────────────────────────────────────────────

    /// <summary>Serializes a cassette to its on-disk text form (LF endings).</summary>
    public static string Serialize(CassetteEntry e)
    {
        var script = Lf(e.Script);
        var stdout = Lf(e.Stdout);
        var stderr = Lf(e.Stderr);

        var sb = new StringBuilder();
        sb.Append(Header).Append('\n');
        sb.Append("bash-version: ").Append(Lf(e.BashVersion).Replace("\n", " ")).Append('\n');
        sb.Append("exit-code: ").Append(e.ExitCode).Append('\n');
        sb.Append("script-length: ").Append(script.Length).Append('\n');
        sb.Append("stdout-length: ").Append(stdout.Length).Append('\n');
        sb.Append("stderr-length: ").Append(stderr.Length).Append('\n');
        sb.Append(ScriptMarker).Append('\n').Append(script).Append('\n');
        sb.Append(StdoutMarker).Append('\n').Append(stdout).Append('\n');
        sb.Append(StderrMarker).Append('\n').Append(stderr).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Parses on-disk cassette text. Normalizes CRLF to LF first, then slices
    /// each section by its length header so marker-like bodies round-trip.
    /// </summary>
    public static bool TryParse(string raw, out CassetteEntry? entry)
    {
        entry = null;
        if (string.IsNullOrEmpty(raw)) return false;

        var text = raw.Replace("\r\n", "\n").Replace("\r", "\n");
        if (!text.StartsWith(Header, StringComparison.Ordinal)) return false;

        string? bashVersion = null;
        int exitCode = 0, scriptLen = -1, stdoutLen = -1, stderrLen = -1;

        // Read header lines up to (but not including) the SCRIPT marker line.
        int cursor = 0;
        while (true)
        {
            int nl = text.IndexOf('\n', cursor);
            if (nl < 0) return false;
            var line = text.Substring(cursor, nl - cursor);
            cursor = nl + 1;
            if (line == ScriptMarker) break;
            if (line == Header) continue;

            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line.Substring(0, colon).Trim();
            var val = line.Substring(colon + 1).Trim();
            switch (key)
            {
                case "bash-version": bashVersion = val; break;
                case "exit-code": if (!int.TryParse(val, out exitCode)) return false; break;
                case "script-length": if (!int.TryParse(val, out scriptLen)) return false; break;
                case "stdout-length": if (!int.TryParse(val, out stdoutLen)) return false; break;
                case "stderr-length": if (!int.TryParse(val, out stderrLen)) return false; break;
            }
        }

        if (bashVersion is null || scriptLen < 0 || stdoutLen < 0 || stderrLen < 0)
            return false;

        // cursor is now positioned at the first char of the script body.
        if (!TrySlice(text, ref cursor, scriptLen, out var script)) return false;
        if (!TryAdvanceToMarker(text, ref cursor, StdoutMarker)) return false;
        if (!TrySlice(text, ref cursor, stdoutLen, out var stdout)) return false;
        if (!TryAdvanceToMarker(text, ref cursor, StderrMarker)) return false;
        if (!TrySlice(text, ref cursor, stderrLen, out var stderr)) return false;

        entry = new CassetteEntry(script, bashVersion, stdout, stderr, exitCode);
        return true;
    }

    // Takes exactly <length> chars from <cursor>, advancing it past them.
    private static bool TrySlice(string text, ref int cursor, int length, out string slice)
    {
        slice = string.Empty;
        if (cursor + length > text.Length) return false;
        slice = text.Substring(cursor, length);
        cursor += length;
        return true;
    }

    // Skips a single separator newline plus the given marker line, leaving the
    // cursor at the first char of the following section body.
    private static bool TryAdvanceToMarker(string text, ref int cursor, string marker)
    {
        // A single '\n' separates a section body from the next marker.
        if (cursor < text.Length && text[cursor] == '\n') cursor++;
        var expected = marker + "\n";
        if (string.CompareOrdinal(text, cursor, expected, 0, expected.Length) != 0)
            return false;
        cursor += expected.Length;
        return true;
    }

    private static string Lf(string s) =>
        string.IsNullOrEmpty(s) ? (s ?? string.Empty) : s.Replace("\r\n", "\n").Replace("\r", "\n");

    // ── Diagnostics ───────────────────────────────────────────────────────

    /// <summary>
    /// The hard-failure message shown in replay mode when a cassette is absent.
    /// Directs the developer to record rather than silently skipping.
    /// </summary>
    public static string MissingMessage(string script)
    {
        var oneLine = script.Replace("\r", " ").Replace("\n", "\\n");
        return
            "oracle cassette missing — the default differential run replays a frozen bash oracle " +
            "and spawns no bash.\n" +
            $"  script : {oneLine}\n" +
            $"  key    : {KeyFor(script)}.cassette\n" +
            $"  dir    : {CassettesDir}\n" +
            "Record it (needs a bash host / WSL), then commit the cassette:\n" +
            "  PSBASH_ORACLE_RECORD=1 dotnet test src/PsBash.Differential.Tests -f net10.0";
    }

    /// <summary>The hard-failure message shown when a cassette exists but is invalid.</summary>
    public static string CorruptMessage(string script)
    {
        var oneLine = script.Replace("\r", " ").Replace("\n", "\\n");
        return
            "oracle cassette corrupt — the cassette file exists but could not be parsed or read.\n" +
            $"  script : {oneLine}\n" +
            $"  file   : {PathFor(script)}\n" +
            "Restore the file from git or inspect it for truncation; this is not a missing cassette.";
    }
}
