using Xunit;

namespace PsBash.Escalation.Tests;

/// <summary>
/// Scale tests per QA rubric Directive 7 (axis 2 — large input).
///
/// All tests run via subprocess (ps-bash -c "...") with hard timeouts.
/// Mark [Trait("Category","Scale")] so CI can filter them if needed.
///
/// Each test documents the expected completion time and failure mode.
/// </summary>
[Trait("Category", "Escalation")]
[Trait("Category", "Scale")]
public class ScaleTests
{

    // ── 1. Brace expansion — 1000 elements ────────────────────────────────────

    /// <summary>
    /// Directive 7 (large input) / Failure-surface axis 2.
    /// echo {1..1000} must produce ~1000 space-separated tokens containing
    /// "1" and "1000". Timeout: 10s.
    ///
    /// ps-bash-specific assertion: brace expansion is a transpiler feature;
    /// there is no oracle (bash produces the same output, but we assert
    /// ps-bash-specific token count from the emitted array literal).
    /// </summary>
    [SkippableFact]
    public async Task Scale_BraceExpansion_1000Elements()
    {

        var (exitCode, stdout, stderr) = await ProcessRunHelper.RunAsync(
            new[] { "-c", "echo {1..1000}" },
            timeout: TimeSpan.FromSeconds(10));

        Assert.Equal(0, exitCode);

        var normalized = stdout.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        // Output must contain both "1" and "1000".
        Assert.Contains("1", normalized, StringComparison.Ordinal);
        Assert.Contains("1000", normalized, StringComparison.Ordinal);

        // Split on whitespace (spaces or newlines between tokens).
        var tokens = normalized.Split(new[] { ' ', '\n', '\t' },
            StringSplitOptions.RemoveEmptyEntries);
        // EXACTLY 1000 tokens. brace expansion of {1..1000} is deterministic;
        // a ±50 band would pass while silently dropping up to 50 elements. The
        // first and last token must also be exactly "1" and "1000" — a count
        // alone can't catch an off-by-one in the range bounds.
        Assert.Equal(1000, tokens.Length);
        Assert.Equal("1", tokens[0]);
        Assert.Equal("1000", tokens[^1]);
    }

    // ── 2. Large pipe — ~50 KB via seq | wc -c ───────────────────────────────

    /// <summary>
    /// Directive 7 (large input) / Failure-surface axis 2.
    /// Stream ~50 KB of data through a pipeline: seq 1 10000 | wc -c.
    /// The numbers 1–10000 produce 48894 bytes (each decimal + newline).
    /// Output must be within ±20% of expected. Timeout: 30s.
    ///
    /// Note: heredoc+pipe (`cat <<'EOF' | wc -c`) is not supported in ps-bash
    /// because the transpiler cannot represent a heredoc as the stdin of a piped
    /// command. seq is used to generate equivalent large data instead.
    ///
    /// 10000 lines produce ~48 KB — a meaningful large-input probe that completes
    /// in ~3s raw, well within the 30s timeout including dotnet-run startup.
    ///
    /// ps-bash-specific assertion: exact byte count depends on ps-bash's Invoke-BashSeq
    /// newline convention. We allow ±20% tolerance to cover \r\n vs \n differences.
    /// </summary>
    [SkippableFact]
    public async Task Scale_LargeSeqPipe_WcBytes()
    {

        var (exitCode, stdout, stderr) = await ProcessRunHelper.RunAsync(
            new[] { "-c", "seq 1 10000 | wc -c" },
            timeout: TimeSpan.FromSeconds(30));

        Assert.Equal(0, exitCode);

        // Extract the first number from stdout (wc -c output).
        var normalized = stdout.Replace("\r\n", "\n").Trim();
        var parts = normalized.Split(new[] { ' ', '\t', '\n' },
            StringSplitOptions.RemoveEmptyEntries);
        Assert.True(parts.Length >= 1,
            $"Expected numeric output from wc -c, got: '{normalized}'. stderr={stderr}");

        Assert.True(long.TryParse(parts[0], out var byteCount),
            $"Expected numeric first token from wc -c, got: '{parts[0]}'. stdout={normalized}");

        // EXACT byte count. seq 1 10000 emits the decimals 1..10000 each followed
        // by one line terminator. The only legitimate variance is the terminator:
        //   LF   → 48894 bytes  (observed; the internal pipe is \n-terminated BashText)
        //   CRLF → 58894 bytes  (one extra byte per line, if a build ever switches)
        // Anything else means lines were dropped, duplicated, or mis-counted — the
        // old 38000–75000 band (±~50%) would have rubber-stamped all of those.
        Assert.True(byteCount is 48_894 or 58_894,
            $"Expected exactly 48894 (LF) or 58894 (CRLF) bytes from seq 1 10000 | wc -c, " +
            $"got {byteCount}. stderr={stderr}");
    }

    // ── 3. 1M lines through sed (output-heavy, fused lane) ────────────────────

    /// <summary>
    /// Directive 7 (large input) / Failure-surface axis 2.
    /// seq 1 1000000 | sed 's/^/line: /' — output must have exactly 1,000,000 lines.
    /// Timeout: 120s.
    ///
    /// Raised from 10k → 1M once the fused-pipeline lane landed (task
    /// 01KXQQ9QN4EVH1NA21EBE4PDZ8). This is the OUTPUT-HEAVY case: all 1M lines
    /// stream back through the IPC return path AND out the launcher's console, so it
    /// exercises the exact seam the fused lane optimises. This test measures SURVIVAL
    /// under scale, NOT speed — the throughput floor is asserted separately by
    /// FusedStreamingBench.FusedStreaming_ThroughputRegression. The 30s timeout the
    /// 10k version used is not enough for 1M output-heavy lines (the internal fused
    /// stage alone is ~2.1s/100k, and 1M lines add IPC-return + console-drain +
    /// cold-daemon spawn on top), so the spawn timeout is 120s here — generous
    /// survival headroom, not a speed budget.
    ///
    /// ps-bash-specific assertion: line count is the oracle (bash and ps-bash
    /// must produce the same number of lines; content prefix is ps-bash-specific).
    /// </summary>
    [SkippableFact]
    public async Task Scale_1MLines_Sed()
    {

        var (exitCode, stdout, stderr) = await ProcessRunHelper.RunAsync(
            new[] { "-c", "seq 1 1000000 | sed 's/^/line: /'" },
            timeout: TimeSpan.FromSeconds(120));

        Assert.Equal(0, exitCode);

        var normalized = stdout.Replace("\r\n", "\n").TrimEnd('\n');
        var lines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.True(lines.Length == 1_000_000,
            $"Expected 1000000 output lines, got {lines.Length}. stderr={stderr}");

        // Spot-check first and last lines have the prefix.
        Assert.StartsWith("line: ", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("line: ", lines[^1], StringComparison.Ordinal);
    }

    // ── 4. Large multi-stage pipeline — seq | sed | wc -l ────────────────────

    /// <summary>
    /// Directive 7 (large input) / Failure-surface axis 2.
    /// Multi-stage pipeline with 1M lines: seq 1 1000000 | sed 's/$/ /' | wc -l.
    /// Output must be 1,000,000. Timeout: 30s.
    ///
    /// Raised from 10k → 1M once the fused-pipeline lane landed (task
    /// 01KXQQ9QN4EVH1NA21EBE4PDZ8). This is the REDUCE case: the three-stage chain
    /// collapses to a single output line, so 1M lines flow through the internal fused
    /// stages (source → transform → count) with almost no IPC return traffic —
    /// complementing the output-heavy Scale_1MLines_Sed above. The 30s timeout is
    /// deliberately generous — these tests measure SURVIVAL under scale, not speed
    /// (warm floor ~250ms, cold daemon spawn ~3.8s).
    ///
    /// Note: yes | head is not used here because ps-bash buffers all pipeline
    /// data in memory — yes produces infinite data faster than head can terminate,
    /// causing OOM / timeout. seq produces finite bounded data.
    ///
    /// This test deliberately overlaps in spirit with Scale_1MLines_Sed to probe
    /// the three-stage pipeline code path specifically (source → transform → count).
    ///
    /// ps-bash-specific assertion: wc -l numeric output is the oracle.
    /// </summary>
    [SkippableFact]
    public async Task Scale_LargePipe_WcCount()
    {

        var (exitCode, stdout, stderr) = await ProcessRunHelper.RunAsync(
            new[] { "-c", "seq 1 1000000 | sed 's/$/ /' | wc -l" },
            timeout: TimeSpan.FromSeconds(30));

        Assert.Equal(0, exitCode);

        var normalized = stdout.Replace("\r\n", "\n").Trim();
        // wc -l may produce "  1000000" with leading spaces or "1000000 -".
        var parts = normalized.Split(new[] { ' ', '\t', '\n' },
            StringSplitOptions.RemoveEmptyEntries);
        Assert.True(parts.Length >= 1,
            $"Expected numeric output from wc -l, got: '{normalized}'. stderr={stderr}");

        Assert.True(long.TryParse(parts[0], out var lineCount),
            $"Expected numeric first token from wc -l, got: '{parts[0]}'. stdout={normalized}");

        // All 1,000,000 lines must pass through the three-stage pipeline.
        Assert.True(lineCount == 1_000_000,
            $"Expected 1000000 lines, got {lineCount}. stderr={stderr}");
    }
}
